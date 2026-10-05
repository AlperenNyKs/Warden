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
        private readonly ToolStripMenuItem _menuOpen, _menuReload, _menuDiscover, _menuExit, _menuUpdate;
        private readonly SteelSeriesClient _client;
        private readonly Application _app;
        private SonarWatcher? _watcher;
        private MainWindow? _mainWindow;
        private readonly string _appDataFolder;
        private readonly string _configPath;
        private readonly string _logPath;
        private readonly object _logLock = new();
        private readonly object _configFileLock = new();
        
        // Donanım telemetrisi: tek örnek, tepsi sahiplenir (pencere kapalıyken de alarm / GPU profili / kayıt çalışır)
        public HardwareMonitorService Telemetry { get; } = new();
        public SessionRecorder Recorder { get; }
        private readonly TempAlarmMonitor _tempAlarm = new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10));
        private readonly object _telemetryHandlerLock = new();
        private readonly HashSet<string> _uiTelemetryDemand = new(StringComparer.Ordinal);
        private bool _telemetryInitialized;
        public TelemetrySnapshot? LastSnapshot { get; private set; }

        // GPU Monitor
        private readonly AfterburnerService _afterburner = new();
        private DateTime _lastGpuProfileApplied = DateTime.MinValue;
        private bool _gpuProfileApplied;          // Profil uygulandı (ve hedef/profil ayarı değişmedi)
        private bool _gpuDroppedBelowLimit;       // Uygulamadan sonra saat sınırın altına indi mi
        private bool _gpuIneffectiveLogged;       // "Profil işe yaramadı" logu bir kez yazılır
        private bool _afterburnerMissingLogged;   // "Afterburner yok" logu bir kez yazılır
        private (double TargetMhz, int TargetProfile) _gpuAppliedFor;

        // Güncellemeler
        private readonly UpdateService _updateService = new();
        private System.Threading.Timer? _updateTimer;
        private UpdateInfo? _pendingUpdate;
        private Version? _lastNotifiedUpdate;
        private bool _lastBalloonIsUpdate;
        private int _updateBusy;                       // kontrol/kurulum aynı anda iki kez çalışmasın
        private static readonly TimeSpan UpdateFirstCheckDelay = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

        /// <summary>Son kontrol sonucu (ayarlar sayfası açılınca gösterilir).</summary>
        public UpdateCheckResult? LastUpdateResult { get; private set; }
        public UpdateInfo? PendingUpdate => _pendingUpdate;

        /// <summary>Bulunan MSIAfterburner.exe yolu (yoksa null).</summary>
        public string? AfterburnerPath => _afterburner.FindExecutable();

        // Akıllı ReloadConfig: interval değişmediğinde watcher'ı yeniden başlatmamak için
        private int _lastIntervalMs = 0;

        public AppConfig Config { get; private set; } = new();

        private static string CreateAppDataFolder()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "Warden");
            Directory.CreateDirectory(Path.Combine(folder, "logs")); // Var olan klasörde no-op
            return folder;
        }

        public TrayApplicationContext(Application app)
        {
            _app = app;

            // Config her zaman %AppData%\Warden altında tutulur. Eskiden önce çalışma dizinindeki
            // "config.json" deneniyordu; Görev Zamanlayıcı ile açılışta (System32) ve elle açılışta (exe klasörü)
            // farklı config dosyaları kullanılabiliyordu.
            _appDataFolder = CreateAppDataFolder();
            _configPath = Path.Combine(_appDataFolder, "config.json");
            _logPath = Path.Combine(_appDataFolder, "logs", "service.log");

            _client = new SteelSeriesClient();
            Recorder = new SessionRecorder(Path.Combine(_appDataFolder, "sessions"));
            Recorder.StateChanged += () =>
            {
                UpdateTelemetryDemand();
                _app.Dispatcher.BeginInvoke(new Action(() => _mainWindow?.UpdateRecordingState()));
            };
            Telemetry.TelemetryUpdated += OnTelemetryUpdated;

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
            _trayIcon.BalloonTipClicked += (s, e) =>
            {
                if (_lastBalloonIsUpdate && _pendingUpdate != null)
                    _ = ConfirmAndInstallUpdateAsync();
            };

            // 2. Context Menu (metinler her açılışta seçili dile göre yenilenir)
            // Menü bir kez oluşturulur; dil değişince yalnızca metinler güncellenir (UpdateTrayMenu)
            var contextMenu = new ContextMenuStrip();
            _menuOpen     = new ToolStripMenuItem("", null, (s, e) => ShowMainWindow());
            _menuReload   = new ToolStripMenuItem("", null, (s, e) => ReloadConfigFromDisk());
            _menuDiscover = new ToolStripMenuItem("", null, async (s, e) => await DiscoverFromTray());
            _menuExit     = new ToolStripMenuItem("", null, (s, e) => Exit());
            _menuUpdate   = new ToolStripMenuItem("", null, (s, e) => _ = ConfirmAndInstallUpdateAsync()) { Visible = false };
            contextMenu.Items.Add(_menuUpdate);
            contextMenu.Items.Add(_menuOpen);
            contextMenu.Items.Add(_menuReload);
            contextMenu.Items.Add(_menuDiscover);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(_menuExit);
            contextMenu.Opening += (s, e) => UpdateTrayMenu();
            _trayIcon.ContextMenuStrip = contextMenu;

            // 3. Load config and start watcher (menü metinleri de burada seçili dile göre ayarlanır)
            LoadConfigAndStart();

            // 4. Güncelleme kontrolü (önceki güncellemeden kalan setup dosyalarını da temizler)
            UpdateService.CleanupOldDownloads();
            ApplyUpdateSchedule();

            // 5. Pre-create MainWindow on background idle dispatcher so opening from tray is instantaneous (0ms)
            _app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    if (_mainWindow == null)
                    {
                        _mainWindow = new MainWindow(this, _client);
                        _mainWindow.Closed += (s, e) => _mainWindow = null;
                    }

                    // İlk açılış: neyin çalışıp neyin eksik olduğunu gösteren durum sayfasıyla aç
                    if (!Config.FirstRunCompleted)
                    {
                        Config.FirstRunCompleted = true;
                        SaveConfig();
                        _mainWindow.ShowStatusPage();
                        ShowMainWindow();
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

        /// <summary>Tray menüsü metinlerini seçili dile göre günceller (menü her açılışta da çağırır).</summary>
        public void UpdateTrayMenu()
        {
            _menuOpen.Text     = Loc.Get("TrayOpenSettings");
            _menuReload.Text   = Loc.Get("TrayReloadConfig");
            _menuDiscover.Text = Loc.Get("TrayDiscoverPresets");
            _menuExit.Text     = Loc.Get("TrayExit");
            _menuUpdate.Visible = _pendingUpdate != null;
            if (_pendingUpdate != null)
                _menuUpdate.Text = Loc.Format("TrayInstallUpdate", _pendingUpdate.Tag);
        }

        /// <summary>
        /// Bellekteki Config değişikliklerini (UI'dan) çalışan servislere uygular. Diskten okumaz.
        /// </summary>
        public void ReloadConfig()
        {
            Config.CheckIntervalMilliseconds = Math.Clamp(Config.CheckIntervalMilliseconds,
                AppConfig.MinCheckIntervalMs, AppConfig.MaxCheckIntervalMs);
            Loc.CurrentLang = Config.Language;
            ApplyStartupIfChanged();
            ApplyUpdateSchedule();
            UpdateTelemetryDemand();

            // Interval değiştiyse veya watcher hiç oluşturulmadıysa → watcher'ı yeniden oluştur
            if (_watcher == null || Config.CheckIntervalMilliseconds != _lastIntervalMs)
            {
                StartWatcher();
                return;
            }

            // Sadece kural/preset değiştiyse → watcher'ı durdurmadan hot-update.
            // (Eskiden her ayar değişiminde schtasks.exe 3-4 kez senkron çalıştırılıyor ve
            //  cihaz denetim zamanlayıcısı sıfırlanıyordu; artık gerek yok.)
            _watcher.UpdateConfig(Config.DefaultPresetId, Config.Rules);
        }

        /// <summary>Tray menüsündeki "Reload Config": config.json'ı diskten yeniden okur.</summary>
        private void ReloadConfigFromDisk()
        {
            LoadConfigAndStart();
            _mainWindow?.ReloadFromConfig();
        }

        private void StartWatcher()
        {
            _watcher?.Stop();
            _watcher = new SonarWatcher(
                _client,
                Config.CheckIntervalMilliseconds,
                Config.DefaultPresetId,
                Config.Rules,
                OnPresetChanged,
                OnActiveWindowChanged,
                Log,
                OnConnectionLost,
                OnConnectionRestored,
                OnGameSessionChanged
            );
            _watcher.Start();
            _lastIntervalMs = Config.CheckIntervalMilliseconds;
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
                Log("Loading configuration...");

                MigrateLegacyConfig();
                Config = ReadConfigFromDisk();

                // Apply Localization
                Loc.CurrentLang = Config.Language;
                UpdateTrayMenu();

                // Apply Startup Registry / Task (arka planda, yalnızca gerekirse)
                ApplyStartupIfChanged();

                StartWatcher();

                // Donanım telemetrisi (GPU profili / sıcaklık alarmı açıksa arka planda çalışır)
                UpdateTelemetryDemand(reinitialize: true);

                // Cihazları otomatik devre dışı bırakma ve sürekli denetimi başlat
                StartDeviceEnforcement();
            }
            catch (Exception ex)
            {
                Log($"Error loading config: {ex}");
                MessageBox.Show($"Error loading config: {ex.Message}", "Warden Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Eski sürümlerin config dosyalarını (ilk açılışta) AppData'ya taşır.</summary>
        private void MigrateLegacyConfig()
        {
            if (File.Exists(_configPath)) return;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidates = new (string Path, string Label)[]
            {
                (Path.Combine(appData, "PCWarden", "config.json"), "PCWarden"),
                (Path.Combine(appData, "SonarEQChanger", "config.json"), "SonarEQChanger"),
                (Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"), "application folder"),
                (Path.Combine(Environment.CurrentDirectory, "config.json"), "working directory")
            };

            foreach (var (path, label) in candidates)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    File.Copy(path, _configPath);
                    Log($"Migrated config.json from {label}: {path}");
                    return;
                }
                catch (Exception ex)
                {
                    Log($"Config migration from {label} failed: {ex.Message}");
                }
            }
        }

        private AppConfig ReadConfigFromDisk()
        {
            if (!File.Exists(_configPath))
            {
                // Yer tutucu ("CHANGE_ME") preset/kural yazılmaz: bunlar GG'ye geçersiz istek gönderip
                // gereksiz hata/bağlantı uyarısı üretiyordu. Kullanıcı arayüzden seçer.
                var fresh = new AppConfig();
                fresh.Normalize();
                Config = fresh;
                SaveConfig();
                Log($"Default config.json created at: {_configPath}");
                return fresh;
            }

            try
            {
                string configJson;
                lock (_configFileLock) configJson = File.ReadAllText(_configPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(configJson) ?? new AppConfig();
                cfg.Normalize();
                return cfg;
            }
            catch (JsonException ex)
            {
                // Bozuk JSON: uygulamayı çalışmaz bırakmak yerine yedekle ve varsayılanla devam et
                string backup = _configPath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                try { File.Copy(_configPath, backup, overwrite: true); } catch { }
                Log($"config.json is corrupt ({ex.Message}). Backup: {backup}. Loading defaults.");
                ShowBalloon(4000, Loc.Get("ConfigCorrupt"), ToolTipIcon.Warning);

                var fresh = new AppConfig();
                fresh.Normalize();
                return fresh;
            }
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
                    // UI thread listeleri değiştirebilir → arka planda gezmeden önce kopyala
                    var cfg = Config;
                    var ids = cfg.DisabledDevices.ToList();
                    var names = cfg.DisabledDeviceNames.ToList();
                    if (ids.Count > 0 || names.Count > 0)
                    {
                        AudioDeviceEnforcer.EnforceDisabledDevices(ids, names);
                    }
                }
                catch { }
            }, null, initialDelayMs, 30000); // SteelSeries GG güncellense bile 30 sn içinde tekrar gizler
        }

        private bool? _lastStartupState = null;
        private bool _legacyStartupCleaned = false;
        private readonly object _startupLock = new();     // yalnızca _lastStartupState için (kısa süreli)
        private readonly object _startupRunLock = new();  // schtasks çalıştırmalarını sıraya koyar (arka planda)

        public void SaveConfig()
        {
            try
            {
                string json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });

                // Atomik yazma: önce geçici dosyaya yaz, sonra değiştir. Yazma sırasında çökme/elektrik kesintisi
                // olursa config.json yarım kalıp bozulmaz.
                lock (_configFileLock)
                {
                    string tmp = _configPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, _configPath, overwrite: true);
                }

                // Reapply settings that might have changed
                Loc.CurrentLang = Config.Language;
                ApplyStartupIfChanged();
            }
            catch (Exception ex)
            {
                Log($"Failed to save config: {ex.Message}");
            }
        }

        /// <summary>
        /// Başlangıç görevi yalnızca ayar değiştiğinde (veya ilk açılışta) güncellenir ve schtasks.exe
        /// arka planda çalıştırılır; UI thread'i bloklanmaz.
        /// </summary>
        private void ApplyStartupIfChanged()
        {
            bool enable = Config.StartWithWindows;
            lock (_startupLock)
            {
                if (_lastStartupState == enable) return;
                _lastStartupState = enable;
            }

            Task.Run(() =>
            {
                lock (_startupRunLock)
                {
                    // Arada ayar tekrar değiştiyse eski isteği uygulama
                    bool? latest;
                    lock (_startupLock) latest = _lastStartupState;
                    if (latest != enable) return;
                    SetStartup(enable);
                }
            });
        }

        private static void RunSchtasks(string args)
        {
            using var proc = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow  = true
            });
            if (proc != null && !proc.WaitForExit(10000))
            {
                try { proc.Kill(); } catch { }
            }
        }

        private static void DeleteRunKeyValue(string name)
        {
            try
            {
                using var regKey = Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (regKey?.GetValue(name) != null)
                    regKey.DeleteValue(name);
            }
            catch { }
        }

        private void SetStartup(bool enable)
        {
            // Registry Run key, requireAdministrator yetkisindeki uygulamaları başlatamaz.
            // Task Scheduler /rl highest ile UAC olmadan admin yetkisiyle başlatma sağlanır.
            try
            {
                const string taskName = "Warden";

                // Eski task ve registry anahtarlarını temizle (oturum başına bir kez yeterli)
                if (!_legacyStartupCleaned)
                {
                    _legacyStartupCleaned = true;
                    foreach (var oldTask in new[] { "PCWarden", "SonarEQChanger" })
                    {
                        try { RunSchtasks($"/delete /tn \"{oldTask}\" /f"); } catch { }
                        DeleteRunKeyValue(oldTask);
                    }
                }

                if (enable)
                {
                    string exePath = Environment.ProcessPath ?? "";
                    if (string.IsNullOrEmpty(exePath)) return;

                    // Eski Registry kaydını temizle
                    DeleteRunKeyValue(taskName);

                    // Exe klasörü standart kullanıcılarca yazılabiliyorsa yönetici yetkili görev bir yetki yükseltme
                    // kapısıdır (exe/DLL değiştirilip sonraki oturumda admin olarak çalıştırılabilir) → kaydetme, varsa kaldır.
                    if (InstallLocationGuard.IsWritableByNonAdmins(exePath, out string reason))
                    {
                        RunSchtasks($"/delete /tn \"{taskName}\" /f");
                        Log($"Startup task NOT registered (insecure install location): {reason}");
                        _app.Dispatcher.BeginInvoke(new Action(() => OnInsecureStartupLocation(exePath, reason)));
                        return;
                    }

                    // /tr değeri içinde exe yolu her zaman tırnaklanır (iç tırnaklar \" ile kaçırılır)
                    // /it  = yalnızca oturum açık kullanıcı için çalış
                    // /rl highest = en yüksek yetkiyle başlat (UAC bypass)
                    // /f   = varsa üstüne yaz
                    string args = $"/create /tn \"{taskName}\" /tr \"\\\"{exePath}\\\"\" /sc onlogon /rl highest /it /f";
                    RunSchtasks(args);
                    Log($"Startup task registered: {exePath}");
                }
                else
                {
                    RunSchtasks($"/delete /tn \"{taskName}\" /f");
                    DeleteRunKeyValue(taskName);
                }
            }
            catch (Exception ex)
            {
                Log($"SetStartup error: {ex.Message}");
            }
        }

        /// <summary>
        /// Güvensiz kurulum konumunda "Windows ile başlat" kapatılır ve kullanıcıya nedeni anlatılır (UI thread).
        /// </summary>
        private void OnInsecureStartupLocation(string exePath, string reason)
        {
            Config.StartWithWindows = false;
            SaveConfig();
            _mainWindow?.SetStartupChecked(false);

            string folder = Path.GetDirectoryName(exePath) ?? exePath;
            MessageBox.Show(Loc.Format("StartupInsecureBody", folder, reason),
                            Loc.Get("StartupInsecureTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ── Güncellemeler ───────────────────────────────────────────────

        /// <summary>Otomatik kontrol ayarına göre zamanlayıcıyı başlatır/durdurur.</summary>
        private void ApplyUpdateSchedule()
        {
            if (Config.AutoCheckUpdates)
            {
                _updateTimer ??= new System.Threading.Timer(
                    async _ => await CheckForUpdatesAsync(manual: false),
                    null, UpdateFirstCheckDelay, UpdateCheckInterval);
            }
            else
            {
                _updateTimer?.Dispose();
                _updateTimer = null;
            }
        }

        /// <summary>GitHub'da yeni sürüm var mı bakar; sonucu ayarlar sayfasına ve (otomatikse) tepsiye bildirir.</summary>
        public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool manual)
        {
            if (Interlocked.CompareExchange(ref _updateBusy, 1, 0) != 0)
                return LastUpdateResult ?? new UpdateCheckResult(UpdateCheckStatus.Error, null, "busy");

            try
            {
                if (manual) PostUpdateStatus(null);   // "kontrol ediliyor"
                var result = await _updateService.CheckAsync().ConfigureAwait(false);

                // Aynı "erişilemedi" durumunu her 6 saatte bir loglamaya gerek yok
                if (result.Status != LastUpdateResult?.Status || result.Status == UpdateCheckStatus.UpdateAvailable)
                    Log($"[Update] {result.Status}: {result.Message} (current {UpdateService.CurrentVersion.ToString(3)})");

                LastUpdateResult = result;
                if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Update != null)
                {
                    _pendingUpdate = result.Update;
                    if (!manual && _lastNotifiedUpdate != result.Update.Version)
                    {
                        _lastNotifiedUpdate = result.Update.Version;
                        ShowBalloon(10000, Loc.Format("UpdateBalloon", result.Update.Tag), ToolTipIcon.Info, isUpdate: true);
                    }
                }
                else if (result.Status == UpdateCheckStatus.UpToDate)
                {
                    _pendingUpdate = null;
                }

                PostUpdateStatus(result);
                return result;
            }
            finally
            {
                Interlocked.Exchange(ref _updateBusy, 0);
            }
        }

        /// <summary>Kullanıcıya sorar, onaylarsa indirip kurar (UI thread'inden çağrılır).</summary>
        public async Task ConfirmAndInstallUpdateAsync()
        {
            var update = _pendingUpdate;
            if (update == null) return;

            var answer = MessageBox.Show(Loc.Format("UpdateConfirm", update.Tag), Loc.Get("UpdateConfirmTitle"),
                                         MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            if (Interlocked.CompareExchange(ref _updateBusy, 1, 0) != 0) return;
            try
            {
                var progress = new Progress<double>(p =>
                    _mainWindow?.ShowUpdateMessage(Loc.Format("UpdateDownloading", (int)Math.Round(p * 100)), busy: true));

                int exitCode = await _updateService.DownloadAndInstallAsync(update, progress, Log, onInstallerStarted: () =>
                {
                    // Setup başladı: birazdan Warden'ı kapatıp yeni sürümü açacak. Kapanınca tepside
                    // "hayalet" ikon kalmasın diye ikon şimdiden gizlenir.
                    _app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _mainWindow?.ShowUpdateMessage(Loc.Get("UpdateInstalling"), busy: true);
                        _mainWindow?.Hide();
                        _trayIcon.Visible = false;
                    }));
                });

                // Buraya gelindiyse setup Warden'ı kapatmadan bitti → iptal edildi veya başarısız oldu
                await _app.Dispatcher.InvokeAsync(() =>
                {
                    _trayIcon.Visible = true;
                    _mainWindow?.ShowUpdateMessage(Loc.Get("UpdateCancelled"), busy: false);
                });
                ShowBalloon(5000, Loc.Get("UpdateCancelled"), ToolTipIcon.Warning);
                Log($"[Update] Installer exit code {exitCode}");
            }
            catch (Exception ex)
            {
                Log($"[Update] Failed: {ex}");
                string reason = ex is InvalidDataException ? Loc.Get("UpdateHashMismatch") : ex.Message;
                await _app.Dispatcher.InvokeAsync(() =>
                {
                    _trayIcon.Visible = true;
                    _mainWindow?.ShowUpdateMessage(Loc.Format("UpdateFailed", reason), busy: false);
                });
                ShowBalloon(5000, Loc.Format("UpdateFailed", reason), ToolTipIcon.Error);
            }
            finally
            {
                Interlocked.Exchange(ref _updateBusy, 0);
            }
        }

        private void PostUpdateStatus(UpdateCheckResult? result)
        {
            _app.Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateTrayMenu();
                _mainWindow?.ShowUpdateStatus(result);
            }));
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

                string outputPath = Path.Combine(_appDataFolder, "presets_list.txt");
                await File.WriteAllTextAsync(outputPath, sb.ToString());

                using var _ = Process.Start(new ProcessStartInfo
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

        /// <summary>
        /// NotifyIcon bir WinForms nesnesidir ve thread-safe değildir; watcher/timer thread'lerinden
        /// gelen bildirimler UI thread'ine yönlendirilir.
        /// </summary>
        private void ShowBalloon(int timeoutMs, string text, ToolTipIcon icon, bool isUpdate = false)
        {
            _app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _lastBalloonIsUpdate = isUpdate;
                    if (_trayIcon.Visible)
                        _trayIcon.ShowBalloonTip(timeoutMs, "Warden", text, icon);
                }
                catch { }
            }));
        }

        private void OnPresetChanged(string appName, string presetId, string presetName)
        {
            Log($"Active EQ Preset switched to match process: {appName} (Preset: {presetName})");

            // Only show notification if we are switching to a game (not Desktop)
            if (appName != "Desktop")
            {
                ShowBalloon(2000, Loc.Format("PresetSwitched", appName, presetName), ToolTipIcon.Info);
            }

            // Dispatch to UI thread
            _app.Dispatcher.BeginInvoke(new Action(() =>
            {
                _mainWindow?.UpdateActivePreset(appName, presetName);
            }));
        }

        // ── Donanım telemetrisi ─────────────────────────────────────────

        /// <summary>
        /// Telemetrinin çalışıp çalışmayacağına ve hızına karar verir:
        ///  - telemetri sayfası açık → 1 sn, tam tarama
        ///  - kayıt sürüyor → 2 sn, tam tarama
        ///  - GPU profili / sıcaklık alarmı / GPU veya durum sayfası → 2 sn, yalnızca CPU+GPU
        ///  - hiçbiri → durur (LibreHardwareMonitor hiç başlatılmaz)
        /// </summary>
        public void UpdateTelemetryDemand(bool reinitialize = false)
        {
            bool uiTelemetry, anyUi;
            lock (_uiTelemetryDemand)
            {
                uiTelemetry = _uiTelemetryDemand.Contains("ui-telemetry");
                anyUi = _uiTelemetryDemand.Count > 0;
            }
            bool recording = Recorder.IsRecording;
            bool background = Config.TargetMhz > 0 || Config.TempAlarmEnabled;

            if (!(anyUi || recording || background))
            {
                Telemetry.Stop();
                return;
            }

            if (!_telemetryInitialized || reinitialize)
            {
                _telemetryInitialized = true;
                Telemetry.Initialize(Config.TelemetryFavorites, Config.TelemetryGraphSensors);
            }

            Telemetry.FullScan = uiTelemetry || recording;
            Telemetry.Start(uiTelemetry ? 1000 : 2000);
        }

        /// <summary>Pencere sayfaları telemetri ihtiyacını bildirir (ör. "ui-telemetry", "ui-gpu", "ui-status").</summary>
        public void SetUiTelemetryDemand(string key, bool active)
        {
            lock (_uiTelemetryDemand)
            {
                if (active) _uiTelemetryDemand.Add(key);
                else _uiTelemetryDemand.Remove(key);
            }
            UpdateTelemetryDemand();
        }

        private void OnTelemetryUpdated(object? sender, TelemetrySnapshot snapshot)
        {
            // Hem zamanlayıcı thread'inden hem (★/📈 tıklamalarında) UI thread'inden gelebilir → sırala
            lock (_telemetryHandlerLock)
            {
                LastSnapshot = snapshot;
                try
                {
                    if (snapshot.PrimaryGpu != null) HandleGpuProfile(snapshot.PrimaryGpu);
                    if (Config.TempAlarmEnabled) HandleTempAlarm(snapshot);
                    if (Recorder.IsRecording) Recorder.Write(snapshot);
                }
                catch (Exception ex)
                {
                    Log($"[Telemetry] handler error: {ex.Message}");
                }
            }
            _mainWindow?.UpdateGpuData(snapshot);
        }

        private void HandleTempAlarm(TelemetrySnapshot snapshot)
        {
            float? gpuTemp = snapshot.PrimaryGpu is { TemperatureCelsius: > 0 } g ? g.TemperatureCelsius : null;
            var alerts = _tempAlarm.Evaluate(DateTime.UtcNow,
                ("CPU", snapshot.CpuTemperature, (float)Config.CpuTempLimit),
                ("GPU", gpuTemp, (float)Config.GpuTempLimit));

            foreach (var a in alerts)
            {
                string msg = Loc.Format("TempAlarm", a.Source, Math.Round(a.Temperature), a.Limit);
                Log($"[TempAlarm] {msg}");
                ShowBalloon(8000, msg, ToolTipIcon.Warning);
            }
        }

        // ── Oturum kaydı ────────────────────────────────────────────────

        private void OnGameSessionChanged(string? game)
        {
            if (!Config.AutoRecordGameSessions) return;
            try
            {
                if (game != null)
                {
                    // Elle başlatılmış kayda dokunma; otomatik kayıt aynı oyunsa devam etsin
                    if (Recorder.IsRecording && (!Recorder.IsAutomatic ||
                        string.Equals(Recorder.Label, game, StringComparison.OrdinalIgnoreCase)))
                        return;
                    string file = Recorder.Start(game, automatic: true);
                    Log($"[Recorder] Auto recording started for {game}: {file}");
                }
                else if (Recorder.IsRecording && Recorder.IsAutomatic)
                {
                    Recorder.Stop();
                    Log("[Recorder] Auto recording stopped (game closed).");
                }
            }
            catch (Exception ex)
            {
                Log($"[Recorder] {ex.Message}");
            }
        }

        public void StartManualRecording()
        {
            try
            {
                string file = Recorder.Start(Loc.Get("RecordManualLabel"), automatic: false);
                Log($"[Recorder] Manual recording started: {file}");
            }
            catch (Exception ex)
            {
                Log($"[Recorder] {ex.Message}");
                MessageBox.Show(ex.Message, "Warden", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void OpenSessionsFolder()
        {
            try
            {
                Directory.CreateDirectory(Recorder.Folder);
                // Yönetici yetkisi Gezgin'e geçmesin diye explorer.exe ile açılır
                using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Recorder.Folder}\"") { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                Log($"[Recorder] open folder failed: {ex.Message}");
            }
        }

        private void HandleGpuProfile(GpuData gpu)
        {
            // Hedef MHz 0 (veya altı) ise otomatik profil devre dışı kabul edilir
            if (Config.TargetMhz <= 0) return;

            // Kullanıcı hedefi veya profili değiştirdiyse önceki uygulama durumu geçersizdir
            var settings = (Config.TargetMhz, Config.TargetProfile);
            if (settings != _gpuAppliedFor)
            {
                _gpuAppliedFor = settings;
                _gpuProfileApplied = false;
                _gpuIneffectiveLogged = false;
            }

            if (gpu.CoreClockMhz <= Config.TargetMhz)
            {
                // Profil uygulandıktan sonra saat sınırın altına indi → profil işe yarıyor
                if (_gpuProfileApplied) _gpuDroppedBelowLimit = true;
                return;
            }

            // Saat sınırın üstünde.
            // Profil uygulandı ama saat hiç sınırın altına inmediyse aynı profili tekrar uygulamak işe yaramaz;
            // eskiden her bekleme süresinde Afterburner yeniden başlatılıp bildirim gösteriliyordu.
            if (_gpuProfileApplied && !_gpuDroppedBelowLimit)
            {
                if (!_gpuIneffectiveLogged)
                {
                    _gpuIneffectiveLogged = true;
                    Log($"[GPU Monitor] Profile {Config.TargetProfile} applied but clock is still {Math.Round(gpu.CoreClockMhz)} MHz > {Config.TargetMhz} MHz. Not re-applying.");
                }
                return;
            }

            // Buraya gelindiyse ya hiç uygulanmadı ya da saat düştükten sonra tekrar yükseldi (profil geri alınmış)
            if ((DateTime.UtcNow - _lastGpuProfileApplied).TotalSeconds <= Config.CooldownSeconds) return;

            // Bekleme süresi, başarısız denemede de başlatılır (Afterburner yoksa her 2 sn'de deneme yapılmaz)
            _lastGpuProfileApplied = DateTime.UtcNow;

            if (_afterburner.ApplyProfile(Config.TargetProfile))
            {
                _gpuProfileApplied = true;
                _gpuDroppedBelowLimit = false;
                _gpuIneffectiveLogged = false;
                _afterburnerMissingLogged = false;

                string msg = Loc.Format("GpuProfileApplied", Math.Round(gpu.CoreClockMhz), Config.TargetProfile);
                Log($"[GPU Monitor] {msg}");
                ShowBalloon(2000, msg, ToolTipIcon.Warning);
            }
            else if (!_afterburnerMissingLogged)
            {
                _afterburnerMissingLogged = true;
                Log($"[GPU Monitor] Clock {Math.Round(gpu.CoreClockMhz)} MHz > limit, but MSI Afterburner is not installed or could not be started.");
            }
        }

        private void OnActiveWindowChanged(string windowName)
        {
            // Reserved for future active window HUD / UI display
        }

        private void OnConnectionLost()
        {
            Log("SteelSeries GG bağlantısı kesildi.");
            ShowBalloon(3000, Loc.Get("ConnLost"), ToolTipIcon.Warning);
        }

        private void OnConnectionRestored()
        {
            Log("SteelSeries GG bağlantısı yeniden kuruldu.");
            ShowBalloon(2000, Loc.Get("ConnRestored"), ToolTipIcon.Info);

            // Bağlantı geri geldiğinde preset listesi boş kalmasın
            _app.Dispatcher.BeginInvoke(new Action(() => _mainWindow?.RefreshPresetsIfNeeded()));
        }

        private void Log(string message)
        {
            string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            Debug.WriteLine(logLine);

            try
            {
                // Log birden fazla thread'den (watcher, timer, UI) çağrılır; eşzamanlı yazma IOException
                // ile satır kaybına yol açıyordu → kilitle
                lock (_logLock)
                {
                    RotateLogIfNeeded(_logPath, maxBytes: 1024 * 1024); // 1 MB
                    File.AppendAllText(_logPath, logLine + Environment.NewLine);
                }
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
            // Her adım ayrı korunur: birindeki hata (ör. pencere kapatma) uygulamanın kapanmasını engellemesin
            void Safe(Action a) { try { a(); } catch (Exception ex) { Log($"[Exit] {ex.Message}"); } }

            Safe(() => _deviceEnforceTimer?.Dispose());
            Safe(() => _updateTimer?.Dispose());
            Safe(() => _watcher?.Stop());
            Safe(() => Recorder.Dispose());
            Safe(() => Telemetry.Dispose());
            Safe(() => _app.Dispatcher.Invoke(() =>
            {
                if (_mainWindow != null)
                {
                    _mainWindow.IsExitExplicit = true;
                    _mainWindow.Close();
                }
            }));
            Safe(() => _client.Dispose()); // FileSystemWatcher ve HttpClient'ı temizle
            Safe(() =>
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            });
            _app.Shutdown();
        }
    }
}
