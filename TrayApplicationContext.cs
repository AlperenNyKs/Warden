using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Microsoft.Win32;

namespace Warden
{
    public class TrayApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private readonly SteelSeriesClient _client;
        private readonly Application _app;
        private SonarWatcher? _watcher;
        private MainWindow? _mainWindow;
        private string _configPath = "config.json";
        
        // GPU Monitor
        private GpuMonitor? _gpuMonitor;
        private readonly AfterburnerService _afterburner = new();
        private DateTime _lastGpuProfileApplied = DateTime.MinValue;

        // ThrottleStop & Desktop Widget
        private ThrottleStopService? _throttleStopService;
        private DesktopWidgetWindow? _desktopWidget;

        // Akıllı ReloadConfig: interval değişmediğinde watcher'ı yeniden başlatmamak için
        private int _lastIntervalMs = 0;

        public AppConfig Config { get; private set; } = new();

        private string GetAppDataFolder()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "Warden");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            string logsFolder = Path.Combine(folder, "logs");
            if (!Directory.Exists(logsFolder))
            {
                Directory.CreateDirectory(logsFolder);
            }
            return folder;
        }

        public TrayApplicationContext(Application app)
        {
            _app = app;
            _client = new SteelSeriesClient();

            _trayIcon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "Warden",
                Visible = true
            };
            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    ShowMainWindow();
            };

            // 2. Context Menu
            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Open Settings", null, (s, e) => ShowMainWindow());
            var widgetMenuItem = new ToolStripMenuItem("Masaüstü Widget'ı", null, (s, e) => ToggleDesktopWidget());
            contextMenu.Items.Add(widgetMenuItem);
            contextMenu.Items.Add("Reload Config", null, (s, e) => LoadConfigAndStart());
            contextMenu.Items.Add("Discover Presets", null, async (s, e) => await DiscoverFromTray());
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("Exit", null, (s, e) => Exit());
            contextMenu.Opening += (s, e) =>
            {
                widgetMenuItem.Checked = Config.DesktopWidgetEnabled;
            };
            _trayIcon.ContextMenuStrip = contextMenu;

            // 3. Load config and start watcher
            LoadConfigAndStart();

            // 4. Pre-create MainWindow on background idle dispatcher so opening from tray is instantaneous (0ms)
            _app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    if (_mainWindow == null)
                    {
                        _mainWindow = new MainWindow(this, _client);
                        _mainWindow.Closed += (s, e) => _mainWindow = null;
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Preload] MainWindow creation notice: {ex.Message}");
                }
            }));
        }

        /// <summary>İkonu EmbeddedResource'tan yükler; hata halinde sistem ikonu kullanılır.</summary>
        private static System.Drawing.Icon LoadIcon()
        {
            try
            {
                var asm    = System.Reflection.Assembly.GetExecutingAssembly();
                // Embedded resource adı: <AssemblyName>.<filename> şeklinde oluşur
                string name = asm.GetManifestResourceNames()
                                 .FirstOrDefault(n => n.EndsWith("icon.ico",
                                     System.StringComparison.OrdinalIgnoreCase)) ?? "";
                if (!string.IsNullOrEmpty(name))
                {
                    using var stream = asm.GetManifestResourceStream(name)!;
                    return new System.Drawing.Icon(stream);
                }
            }
            catch { }
            // Fallback: uygulama varsayılan ikonu
            return System.Drawing.SystemIcons.Application;
        }

        public void ReloadConfig()
        {
            // Interval değiştiyse veya watcher hiç oluşturulmadıysa → tam restart
            if (_watcher == null || Config.CheckIntervalMilliseconds != _lastIntervalMs)
            {
                LoadConfigAndStart();
                return;
            }

            // Sadece kural/preset/dil değiştiyse → watcher'ı durdurmadan hot-update
            Loc.CurrentLang = Config.Language;
            SetStartup(Config.StartWithWindows);
            _watcher.UpdateConfig(Config.DefaultPresetId, Config.Rules);
            StartDeviceEnforcement();
        }

        private readonly object _windowLock = new();
        private bool _isOpeningWindow = false;
        private DateTime _lastTrayClickTime = DateTime.MinValue;

        public void ShowMainWindow()
        {
            var now = DateTime.UtcNow;
            lock (_windowLock)
            {
                if ((now - _lastTrayClickTime).TotalMilliseconds < 350)
                {
                    return; // Ignore rapid duplicate clicks within 350ms
                }
                _lastTrayClickTime = now;
            }

            // Run on WPF UI thread
            _app.Dispatcher.Invoke(() =>
            {
                if (_isOpeningWindow) return;
                _isOpeningWindow = true;

                try
                {
                    if (_mainWindow == null)
                    {
                        _mainWindow = new MainWindow(this, _client);
                        _mainWindow.Closed += (s, e) => _mainWindow = null;
                    }

                    // Enforce single-window guarantee across Application.Current.Windows
                    foreach (Window w in _app.Windows)
                    {
                        if (w is MainWindow mw && mw != _mainWindow)
                        {
                            try { mw.Close(); } catch { }
                        }
                    }

                    if (_mainWindow.WindowState == WindowState.Minimized)
                    {
                        _mainWindow.WindowState = WindowState.Normal;
                    }

                    if (!_mainWindow.IsVisible)
                    {
                        _mainWindow.Show();
                    }

                    _mainWindow.Activate();
                    _mainWindow.Topmost = true;
                    _mainWindow.Topmost = false;
                    _mainWindow.Focus();
                }
                catch (Exception ex)
                {
                    Log($"[ShowMainWindow Error] {ex.Message}");
                }
                finally
                {
                    _isOpeningWindow = false;
                }
            });
        }

        private void LoadConfigAndStart()
        {
            try
            {
                _watcher?.Stop();
                Log("Loading configuration...");

                if (!File.Exists(_configPath))
                {
                    _configPath = Path.Combine(GetAppDataFolder(), "config.json");
                    string oldPCWardenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCWarden", "config.json");
                    string oldSonarPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonarEQChanger", "config.json");
                    string oldConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                    if (!File.Exists(_configPath))
                    {
                        if (File.Exists(oldPCWardenPath))
                        {
                            try
                            {
                                File.Copy(oldPCWardenPath, _configPath);
                                Log("Migrated config.json from PCWarden.");
                            }
                            catch { }
                        }
                        else if (File.Exists(oldSonarPath))
                        {
                            try
                            {
                                File.Copy(oldSonarPath, _configPath);
                                Log("Migrated config.json from SonarEQChanger.");
                            }
                            catch { }
                        }
                        else if (File.Exists(oldConfigPath))
                        {
                            try
                            {
                                File.Copy(oldConfigPath, _configPath);
                                Log("Migrated old config.json to AppData folder.");
                            }
                            catch { }
                        }
                    }
                }

                if (!File.Exists(_configPath))
                {
                    Config = new AppConfig
                    {
                        DefaultPresetId = "CHANGE_ME_TO_DEFAULT_DESKTOP_PRESET_UUID",
                        Rules = { { "VALORANT-Win64-Shipping.exe", "CHANGE_ME" } }
                    };
                    SaveConfig();
                    Log($"Default config.json created at: {_configPath}");
                }
                else
                {
                    string configJson = File.ReadAllText(_configPath);
                    Config = JsonSerializer.Deserialize<AppConfig>(configJson) ?? new AppConfig();
                }

                // Apply Localization
                Loc.CurrentLang = Config.Language;

                // Apply Startup Registry
                SetStartup(Config.StartWithWindows);

                _watcher = new SonarWatcher(
                    _client,
                    Config.CheckIntervalMilliseconds,
                    Config.DefaultPresetId,
                    Config.Rules,
                    OnPresetChanged,
                    OnActiveWindowChanged,
                    Log,
                    OnConnectionLost,
                    OnConnectionRestored
                );
                _watcher.Start();
                _lastIntervalMs = Config.CheckIntervalMilliseconds;

                // Start GPU Monitor
                if (_gpuMonitor == null)
                {
                    _gpuMonitor = new GpuMonitor();
                    _gpuMonitor.GpuDataUpdated += OnGpuDataUpdated;
                    _gpuMonitor.Start();
                }

                // Cihazları otomatik devre dışı bırakma ve sürekli denetimi başlat
                StartDeviceEnforcement();

                // ThrottleStop Masaüstü Widget'ı
                _app.Dispatcher.BeginInvoke(() =>
                {
                    if (Config.DesktopWidgetEnabled)
                        ShowDesktopWidget();
                    else
                        HideDesktopWidget();
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading config: {ex.Message}", "Warden Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public ThrottleStopService GetThrottleStopService()
        {
            if (_throttleStopService == null)
            {
                _throttleStopService = new ThrottleStopService(Config.ThrottleStopPath, Log);
            }
            return _throttleStopService;
        }

        public void ShowDesktopWidget()
        {
            _app.Dispatcher.Invoke(() =>
            {
                try
                {
                    var ts = GetThrottleStopService();
                    if (_desktopWidget == null)
                    {
                        _desktopWidget = new DesktopWidgetWindow(
                            ts,
                            Config,
                            () => SaveConfig(),
                            () => ShowMainWindow()
                        );
                    }
                    else
                    {
                        _desktopWidget.ApplyVisibleProfiles();
                    }
                    _desktopWidget.Show();
                    Log($"[WIDGET] Desktop widget shown at ({_desktopWidget.Left}, {_desktopWidget.Top})");
                }
                catch (Exception ex)
                {
                    Log($"[WIDGET ERROR] Failed to show widget: {ex.Message}");
                }
            });
        }

        public void UpdateDesktopWidgetProfiles()
        {
            _app.Dispatcher.Invoke(() =>
            {
                _desktopWidget?.ApplyVisibleProfiles();
            });
        }

        public void HideDesktopWidget()
        {
            _app.Dispatcher.Invoke(() =>
            {
                _desktopWidget?.Hide();
            });
        }

        public void ToggleDesktopWidget()
        {
            Config.DesktopWidgetEnabled = !Config.DesktopWidgetEnabled;
            SaveConfig();
            if (Config.DesktopWidgetEnabled)
                ShowDesktopWidget();
            else
                HideDesktopWidget();
        }

        private System.Threading.Timer? _deviceEnforceTimer;

        private void StartDeviceEnforcement()
        {
            _deviceEnforceTimer?.Dispose();

            // İlk çalıştırma gecikmeli (DeviceDisableDelaySeconds), ardından her 30 saniyede bir otomatik denetim
            int initialDelayMs = Math.Max(0, Config.DeviceDisableDelaySeconds) * 1000;
            _deviceEnforceTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (Config.DisabledDevices.Count > 0 || Config.DisabledDeviceNames.Count > 0)
                    {
                        AudioDeviceEnforcer.EnforceDisabledDevices(Config.DisabledDevices, Config.DisabledDeviceNames);
                    }
                }
                catch { }
            }, null, initialDelayMs, 30000); // SteelSeries GG güncellense bile 30 sn içinde tekrar gizler
        }

        private bool? _lastStartupState = null;

        public void SaveConfig()
        {
            try
            {
                string json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, json);
                
                // Reapply settings that might have changed
                Loc.CurrentLang = Config.Language;
                if (_lastStartupState == null || _lastStartupState != Config.StartWithWindows)
                {
                    _lastStartupState = Config.StartWithWindows;
                    SetStartup(Config.StartWithWindows);
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to save config: {ex.Message}");
            }
        }

        private void SetStartup(bool enable)
        {
            // Registry Run key, requireAdministrator yetkisindeki uygulamaları başlatamaz.
            // Task Scheduler /rl highest ile UAC olmadan admin yetkisiyle başlatma sağlanır.
            try
            {
                const string taskName = "Warden";
                string[] oldTasks = { "PCWarden", "SonarEQChanger" };

                // Eski task ve registry anahtarlarını temizle
                foreach (var oldTask in oldTasks)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("schtasks.exe", $"/delete /tn \"{oldTask}\" /f")
                        {
                            UseShellExecute = false,
                            CreateNoWindow  = true
                        })?.WaitForExit();

                        using var regKeyOld = Microsoft.Win32.Registry.CurrentUser
                            .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                        if (regKeyOld?.GetValue(oldTask) != null)
                            regKeyOld.DeleteValue(oldTask);
                    }
                    catch { }
                }

                if (enable)
                {
                    string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (string.IsNullOrEmpty(exePath)) return;

                    // Eski Registry kaydını temizle
                    try
                    {
                        using var regKey = Microsoft.Win32.Registry.CurrentUser
                            .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                        if (regKey?.GetValue(taskName) != null)
                            regKey.DeleteValue(taskName);
                    }
                    catch { }

                    // Yol boşluk içeriyorsa tırnak ekle
                    string trValue = exePath.Contains(' ') ? $"\"{exePath}\"" : exePath;

                    // /it  = yalnızca oturum açık kullanıcı için çalış
                    // /rl highest = en yüksek yetkiyle başlat (UAC bypass)
                    // /f   = varsa üstüne yaz
                    string args = $"/create /tn \"{taskName}\" /tr \"{trValue}\" /sc onlogon /rl highest /it /f";
                    Process.Start(new ProcessStartInfo("schtasks.exe", args)
                    {
                        UseShellExecute = false,
                        CreateNoWindow  = true
                    })?.WaitForExit();
                }
                else
                {
                    Process.Start(new ProcessStartInfo("schtasks.exe", $"/delete /tn \"{taskName}\" /f")
                    {
                        UseShellExecute = false,
                        CreateNoWindow  = true
                    })?.WaitForExit();

                    try
                    {
                        using var regKey = Microsoft.Win32.Registry.CurrentUser
                            .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                        if (regKey?.GetValue(taskName) != null)
                            regKey.DeleteValue(taskName);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"SetStartup error: {ex.Message}");
            }
        }

        private async Task DiscoverFromTray()
        {
            try
            {
                string address = await _client.GetSonarAddressAsync();
                var presets = await _client.GetConfigsAsync();
                
                var sb = new StringBuilder();
                sb.AppendLine("==================================================");
                sb.AppendLine(" Warden - Sonar EQ Preset Discovery Report");
                sb.AppendLine("==================================================");
                sb.AppendLine($"Report Date: {DateTime.Now}");
                sb.AppendLine($"Sonar Web Server: {address}");
                sb.AppendLine("--------------------------------------------------");
                sb.AppendLine();

                foreach (var item in presets)
                {
                    if (item.virtualAudioDevice == "game")
                    {
                        sb.AppendLine($"Name: {item.name,-30} | UUID: {item.id}");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("--------------------------------------------------");
                sb.AppendLine("Copy the UUID of the preset you want and paste it");
                sb.AppendLine("into config.json or configure it inside the GUI.");
                sb.AppendLine("==================================================");

                string outputPath = Path.Combine(GetAppDataFolder(), "presets_list.txt");
                await File.WriteAllTextAsync(outputPath, sb.ToString());

                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{outputPath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Discovery failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnPresetChanged(string appName, string presetId, string presetName)
        {
            Log($"Active EQ Preset switched to match process: {appName} (Preset: {presetName})");
            
            // Only show notification if we are switching to a game (not Desktop)
            if (appName != "Desktop")
            {
                _trayIcon.ShowBalloonTip(2000, "Warden", $"Preset switched: {appName} -> {presetName}", ToolTipIcon.Info);
            }
            
            // Dispatch to UI thread
            _app.Dispatcher.BeginInvoke(new Action(() =>
            {
                _mainWindow?.UpdateActivePreset(appName, presetName);
            }));
        }

        private void OnGpuDataUpdated(object? sender, GpuData data)
        {
            // Update UI if window is open
            _app.Dispatcher.BeginInvoke(new Action(() =>
            {
                _mainWindow?.UpdateGpuData(data);
            }));

            // Check thresholds in background
            if (data.CoreClockMhz > Config.TargetMhz)
            {
                if ((DateTime.Now - _lastGpuProfileApplied).TotalSeconds > Config.CooldownSeconds)
                {
                    _afterburner.ApplyProfile(Config.TargetProfile);
                    _lastGpuProfileApplied = DateTime.Now;
                    
                    string msg = $"Core clock reached {Math.Round(data.CoreClockMhz)} MHz. Profile {Config.TargetProfile} applied.";
                    Log($"[GPU Monitor] {msg}");
                    _trayIcon.ShowBalloonTip(2000, "Warden", msg, ToolTipIcon.Warning);
                }
            }
        }

        private void OnActiveWindowChanged(string windowName)
        {
            // Reserved for future active window HUD / UI display
        }

        private void OnConnectionLost()
        {
            Log("SteelSeries GG bağlantısı kesildi.");
            _trayIcon.ShowBalloonTip(
                3000,
                "Warden",
                "SteelSeries GG bağlantısı kesildi. Yeniden bağlanmaya çalışıyor...",
                ToolTipIcon.Warning);
        }

        private void OnConnectionRestored()
        {
            Log("SteelSeries GG bağlantısı yeniden kuruldu.");
            _trayIcon.ShowBalloonTip(
                2000,
                "Warden",
                "SteelSeries GG bağlantısı yeniden kuruldu.",
                ToolTipIcon.Info);
        }

        private void Log(string message)
        {
            string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            Debug.WriteLine(logLine);

            try
            {
                string logPath = Path.Combine(GetAppDataFolder(), "logs", "service.log");
                RotateLogIfNeeded(logPath, maxBytes: 1024 * 1024); // 1 MB
                File.AppendAllText(logPath, logLine + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>
        /// Log dosyası maxBytes'ı aşarsa .bak'a taşır ve yeni bir tane başlatır.
        /// </summary>
        private static void RotateLogIfNeeded(string logPath, long maxBytes)
        {
            try
            {
                if (!File.Exists(logPath)) return;
                if (new FileInfo(logPath).Length <= maxBytes) return;

                string backupPath = logPath + ".bak";
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(logPath, backupPath);
            }
            catch { }
        }

        private void Exit()
        {
            _deviceEnforceTimer?.Dispose();
            _watcher?.Stop();
            _gpuMonitor?.Stop();
            _client.Dispose(); // FileSystemWatcher ve HttpClient'ı temizle
            _app.Dispatcher.Invoke(() =>
            {
                if (_desktopWidget != null)
                {
                    _desktopWidget.Close();
                    _desktopWidget = null;
                }
                if (_mainWindow != null)
                {
                    _mainWindow.IsExitExplicit = true;
                    _mainWindow.Close();
                }
            });
            _throttleStopService?.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _app.Shutdown();
        }
    }
}
