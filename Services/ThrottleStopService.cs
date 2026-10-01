using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace Warden
{
    public class ThrottleStopService : IDisposable
    {
        private const uint WM_COMMAND = 0x0111;
        private const uint BM_CLICK = 0x00F5;
        private const int BASE_PROFILE_CONTROL_ID = 1088; // 1088: Profile 1, 1089: Profile 2, 1090: Profile 3, 1091: Profile 4

        private delegate bool EnumThreadWndProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(int dwThreadId, EnumThreadWndProc lpfn, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        private IntPtr _cachedHwnd = IntPtr.Zero;

        private string _exePath = @"D:\ThrottleStop_9.7\ThrottleStop.exe";
        private string _iniPath = @"D:\ThrottleStop_9.7\ThrottleStop.ini";
        private FileSystemWatcher? _watcher;
        private System.Threading.Timer? _iniDebounceTimer;
        private readonly object _iniLock = new();
        private volatile int _cachedActiveProfile = 0;
        private readonly string[] _profileNames = { "Performance", "Game", "Internet", "Battery" };
        private readonly Action<string>? _log;
        private bool _disposed;

        public event EventHandler<int>? ActiveProfileChanged;
        public event EventHandler? ProfileNamesChanged;

        public ThrottleStopService(string? customPath = null, Action<string>? log = null)
        {
            _log = log;
            InitPaths(customPath);
            LoadProfileNames();
            _cachedActiveProfile = ReadActiveProfileFromIni() ?? 0;
            SetupWatcher();
        }

        private void InitPaths(string? customPath)
        {
            if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
            {
                _exePath = customPath;
                _iniPath = Path.Combine(Path.GetDirectoryName(customPath)!, "ThrottleStop.ini");
            }
            else
            {
                // Fallback detection
                string[] candidates =
                {
                    @"D:\ThrottleStop_9.7\ThrottleStop.exe",
                    @"C:\ThrottleStop\ThrottleStop.exe",
                    @"D:\ThrottleStop\ThrottleStop.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ThrottleStop", "ThrottleStop.exe")
                };

                foreach (var c in candidates)
                {
                    if (File.Exists(c))
                    {
                        _exePath = c;
                        _iniPath = Path.Combine(Path.GetDirectoryName(c)!, "ThrottleStop.ini");
                        break;
                    }
                }
            }
        }

        public string ExePath => _exePath;
        public string IniPath => _iniPath;
        public int ActiveProfile => _cachedActiveProfile;
        public string[] ProfileNames { get { lock (_profileNames) return (string[])_profileNames.Clone(); } }

        public bool IsRunning()
        {
            if (_cachedHwnd != IntPtr.Zero && IsWindow(_cachedHwnd)) return true;
            var procs = Process.GetProcessesByName("ThrottleStop");
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();   // Process handle sızıntısını önle
            return running;
        }

        public bool StartThrottleStop()
        {
            try
            {
                if (File.Exists(_exePath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = _exePath,
                        WorkingDirectory = Path.GetDirectoryName(_exePath)!,
                        UseShellExecute = true
                    };
                    using var _ = Process.Start(psi);
                    return true;
                }
                _log?.Invoke($"[THROTTLESTOP WARN] Executable not found: {_exePath}");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[THROTTLESTOP WARN] Failed to start: {ex.Message}");
            }
            return false;
        }

        public bool SetProfile(int profileIndex)
        {
            if (profileIndex < 0 || profileIndex > 3) return false;

            _cachedActiveProfile = profileIndex;
            ActiveProfileChanged?.Invoke(this, profileIndex);

            if (!IsRunning())
            {
                if (!StartThrottleStop()) return false;

                // ThrottleStop'un açılış süresi sisteme göre değişir (uyarı penceresi vb.);
                // sabit 1 sn yerine pencere bulunana kadar ~10 sn boyunca tekrar dene.
                _ = Task.Run(async () =>
                {
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        await Task.Delay(500);
                        if (FindAndCacheWindow() != IntPtr.Zero)
                        {
                            SendProfileMessage(profileIndex);
                            return;
                        }
                    }
                    _log?.Invoke($"[THROTTLESTOP WARN] Window did not appear after start; profile {profileIndex + 1} not applied");
                });
                return true;
            }

            return SendProfileMessage(profileIndex);
        }

        private IntPtr FindAndCacheWindow()
        {
            IntPtr hDialog = _cachedHwnd;
            if (hDialog == IntPtr.Zero || !IsWindow(hDialog))
            {
                hDialog = FindThrottleStopWindow();
                _cachedHwnd = hDialog;
            }
            return hDialog;
        }

        private bool SendProfileMessage(int profileIndex)
        {
            IntPtr hDialog = FindAndCacheWindow();
            int controlId = BASE_PROFILE_CONTROL_ID + profileIndex;

            if (hDialog != IntPtr.Zero)
            {
                IntPtr hRadio = GetDlgItem(hDialog, controlId);
                if (hRadio != IntPtr.Zero)
                {
                    // Click radio button
                    SendMessage(hRadio, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                }

                // Send WM_COMMAND to dialog
                PostMessage(hDialog, WM_COMMAND, (IntPtr)controlId, hRadio);
                _log?.Invoke($"[THROTTLESTOP] Switched to profile {profileIndex + 1} ({ProfileNames[profileIndex]}) via WM_COMMAND {controlId}");
                return true;
            }

            _log?.Invoke($"[THROTTLESTOP WARN] Window not found when trying to switch to profile {profileIndex + 1}");
            return false;
        }

        private IntPtr FindThrottleStopWindow()
        {
            var processes = Process.GetProcessesByName("ThrottleStop");
            if (processes.Length == 0) return IntPtr.Zero;

            IntPtr foundHwnd = IntPtr.Zero;

            try
            {
                foreach (var proc in processes)
                {
                    try
                    {
                        foreach (ProcessThread thread in proc.Threads)
                        {
                            EnumThreadWindows(thread.Id, (hWnd, lParam) =>
                            {
                                var cls = new StringBuilder(256);
                                GetClassName(hWnd, cls, 256);

                                if (cls.ToString() == "#32770")
                                {
                                    var title = new StringBuilder(256);
                                    GetWindowText(hWnd, title, 256);
                                    if (title.ToString().Contains("ThrottleStop"))
                                    {
                                        foundHwnd = hWnd;
                                        return false; // Stop enumeration
                                    }
                                }
                                return true;
                            }, IntPtr.Zero);

                            if (foundHwnd != IntPtr.Zero) break;
                        }
                    }
                    catch { }

                    if (foundHwnd != IntPtr.Zero) break;
                }
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }

            return foundHwnd;
        }

        /// <summary>
        /// INI'deki aktif profili okur. Dosya yoksa, kilitliyse veya değer bulunamazsa null döner —
        /// eskiden 0 dönüyordu ve ThrottleStop dosyayı yazarken okunursa widget yanlışlıkla Profil 1'e atlıyordu.
        /// </summary>
        private int? ReadActiveProfileFromIni()
        {
            try
            {
                if (File.Exists(_iniPath))
                {
                    foreach (var line in ReadIniLines())
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("Profile=", StringComparison.OrdinalIgnoreCase))
                        {
                            string val = trimmed.Substring("Profile=".Length).Trim();
                            if (int.TryParse(val, out int p) && p >= 0 && p <= 3)
                            {
                                return p;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>ThrottleStop dosyayı yazarken de okuyabilmek için paylaşımlı modda açar.</summary>
        private string[] ReadIniLines()
        {
            using var fs = new FileStream(_iniPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Split('\n');
        }

        /// <summary>Profil adlarını INI'den okur; değiştiyse true döner.</summary>
        private bool LoadProfileNames()
        {
            try
            {
                if (!File.Exists(_iniPath)) return false;

                bool changed = false;
                foreach (var line in ReadIniLines())
                {
                    var trimmed = line.Trim();
                    for (int i = 0; i < 4; i++)
                    {
                        string key = $"ProfileName{i + 1}=";
                        if (trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                        {
                            string name = trimmed.Substring(key.Length).Trim();
                            if (string.IsNullOrEmpty(name)) break; // Boş ad → varsayılanı koru
                            lock (_profileNames)
                            {
                                if (_profileNames[i] != name)
                                {
                                    _profileNames[i] = name;
                                    changed = true;
                                }
                            }
                            break;
                        }
                    }
                }
                return changed;
            }
            catch { return false; }
        }

        private void SetupWatcher()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_iniPath);
                string file = Path.GetFileName(_iniPath);

                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    _iniDebounceTimer = new System.Threading.Timer(_ => ProcessIniChange(), null, Timeout.Infinite, Timeout.Infinite);

                    _watcher = new FileSystemWatcher(dir, file)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        EnableRaisingEvents = true
                    };

                    _watcher.Changed += OnIniChanged;
                    _watcher.Created += OnIniChanged;
                    _watcher.Renamed += OnIniChanged;
                }
            }
            catch { }
        }

        private void OnIniChanged(object sender, FileSystemEventArgs e)
        {
            // Trailing-edge debounce: art arda gelen yazma olaylarında son yazmadan 250 ms sonra bir kez oku.
            // (Eski leading-edge yaklaşım, ilk olayda yarım yazılmış dosyayı okuyup son değişikliği kaçırabiliyordu.)
            lock (_iniLock)
            {
                if (_disposed) return;
                _iniDebounceTimer?.Change(250, Timeout.Infinite);
            }
        }

        private void ProcessIniChange()
        {
            if (LoadProfileNames())
                ProfileNamesChanged?.Invoke(this, EventArgs.Empty);

            int? newProfile = ReadActiveProfileFromIni();
            if (newProfile.HasValue && newProfile.Value != _cachedActiveProfile)
            {
                _cachedActiveProfile = newProfile.Value;
                ActiveProfileChanged?.Invoke(this, newProfile.Value);
            }
        }

        public void Dispose()
        {
            lock (_iniLock)
            {
                _disposed = true;
                _watcher?.Dispose();
                _watcher = null;
                _iniDebounceTimer?.Dispose();
                _iniDebounceTimer = null;
            }
        }
    }
}
