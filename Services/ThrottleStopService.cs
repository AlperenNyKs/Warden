using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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
        private DateTime _lastFileRead = DateTime.MinValue;
        private int _cachedActiveProfile = 0;
        private string[] _profileNames = new[] { "Performance", "Game", "Internet", "Battery" };
        private readonly Action<string>? _log;

        public event EventHandler<int>? ActiveProfileChanged;

        public ThrottleStopService(string? customPath = null, Action<string>? log = null)
        {
            _log = log;
            InitPaths(customPath);
            LoadProfileNames();
            _cachedActiveProfile = ReadActiveProfileFromIni();
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
                    @"D:\ThrottleStop\ThrottleStop.exe"
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
        public string[] ProfileNames => _profileNames;

        public bool IsRunning()
        {
            if (_cachedHwnd != IntPtr.Zero && IsWindow(_cachedHwnd)) return true;
            return Process.GetProcessesByName("ThrottleStop").Length > 0;
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
                    Process.Start(psi);
                    return true;
                }
            }
            catch { }
            return false;
        }

        public bool SetProfile(int profileIndex)
        {
            if (profileIndex < 0 || profileIndex > 3) return false;

            _cachedActiveProfile = profileIndex;
            ActiveProfileChanged?.Invoke(this, profileIndex);

            if (!IsRunning())
            {
                StartThrottleStop();
                // Wait briefly for startup
                Task.Delay(1000).ContinueWith(_ => SendProfileMessage(profileIndex));
                return true;
            }

            return SendProfileMessage(profileIndex);
        }

        private bool SendProfileMessage(int profileIndex)
        {
            IntPtr hDialog = _cachedHwnd;
            if (hDialog == IntPtr.Zero || !IsWindow(hDialog))
            {
                hDialog = FindThrottleStopWindow();
                _cachedHwnd = hDialog;
            }
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
                _log?.Invoke($"[THROTTLESTOP] Switched to profile {profileIndex + 1} ({_profileNames[profileIndex]}) via WM_COMMAND {controlId}");
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

            return foundHwnd;
        }

        private int ReadActiveProfileFromIni()
        {
            try
            {
                if (File.Exists(_iniPath))
                {
                    var lines = File.ReadAllLines(_iniPath);
                    foreach (var line in lines)
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
            return 0;
        }

        private void LoadProfileNames()
        {
            try
            {
                if (File.Exists(_iniPath))
                {
                    var lines = File.ReadAllLines(_iniPath);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("ProfileName1=", StringComparison.OrdinalIgnoreCase))
                            _profileNames[0] = trimmed.Substring("ProfileName1=".Length).Trim();
                        else if (trimmed.StartsWith("ProfileName2=", StringComparison.OrdinalIgnoreCase))
                            _profileNames[1] = trimmed.Substring("ProfileName2=".Length).Trim();
                        else if (trimmed.StartsWith("ProfileName3=", StringComparison.OrdinalIgnoreCase))
                            _profileNames[2] = trimmed.Substring("ProfileName3=".Length).Trim();
                        else if (trimmed.StartsWith("ProfileName4=", StringComparison.OrdinalIgnoreCase))
                            _profileNames[3] = trimmed.Substring("ProfileName4=".Length).Trim();
                    }
                }
            }
            catch { }
        }

        private void SetupWatcher()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_iniPath);
                string file = Path.GetFileName(_iniPath);

                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    _watcher = new FileSystemWatcher(dir, file)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };

                    _watcher.Changed += OnIniChanged;
                }
            }
            catch { }
        }

        private void OnIniChanged(object sender, FileSystemEventArgs e)
        {
            if ((DateTime.Now - _lastFileRead).TotalMilliseconds < 200) return;
            _lastFileRead = DateTime.Now;

            // Small delay to allow file write release
            Task.Delay(100).ContinueWith(_ =>
            {
                int newProfile = ReadActiveProfileFromIni();
                if (newProfile != _cachedActiveProfile)
                {
                    _cachedActiveProfile = newProfile;
                    ActiveProfileChanged?.Invoke(this, newProfile);
                }
            });
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}
