using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Warden
{
    public class SonarWatcher
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private readonly SteelSeriesClient _client;
        private readonly int _intervalMs;

        // Config — hot-update için lock ile korunur
        private string _defaultPresetId;
        private Dictionary<string, string> _rules;
        private readonly object _configLock = new();

        // Callbacks
        private readonly Action<string, string, string> _onPresetChanged; // ruleName, presetId, presetName
        private readonly Action<string> _onActiveWindowChanged;
        private readonly Action<string> _onLog;
        private readonly Action? _onConnectionLost;
        private readonly Action? _onConnectionRestored;
        private readonly Action<string?>? _onGameSessionChanged;   // oyun kuralı başladı (ad) / bitti (null)
        private string? _reportedGame;

        // Timer
        private System.Threading.Timer? _timer;

        // State
        private string? _currentActiveProcess;
        private string? _currentPresetId;
        private string? _activeRuleProcess;

        // Ön plan pencere → process adı cache'i: aynı pencere için her tick'te
        // Process nesnesi + MainModule (modül listesi) sorgusu yapılmaz.
        private IntPtr _lastHwnd = IntPtr.Zero;
        private uint _lastPid = 0;
        private string _lastProcessName = "";
        private string _lastExeName = "";

        // Kural oyunu arka planda hâlâ çalışıyor mu? (Process.GetProcessesByName pahalı → periyodik kontrol)
        private bool _activeRuleStillRunning;
        private DateTime _lastRunningCheckUtc = DateTime.MinValue;
        private static readonly TimeSpan RunningCheckInterval = TimeSpan.FromSeconds(5);

        // Başarısız preset geçişini tekrar deneme (eskiden pencere değişmeden hiç tekrar denenmiyordu)
        // GG kapalıyken log'u ve CPU'yu meşgul etmemek için bekleme üstel artar: 5 sn, 10 sn, 20 sn ... en fazla 60 sn
        private string? _lastFailedPresetId;
        private int _failStreak = 0;
        private DateTime _retryAfterUtc = DateTime.MinValue;
        private static readonly TimeSpan RetryDelayOnError = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RetryDelayOnRejected = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

        // Atomic işlem bayrağı: bool yerine int — Interlocked ile thread-safe
        private int _isProcessing = 0;

        // Bağlantı sağlığı takibi
        private int _consecutiveErrors = 0;
        private bool _isConnectionLost = false;
        private const int MaxConsecutiveErrors = 5;

        // Preset adı cache: her değişimde GetConfigsAsync() çağırmak yerine cache kullan
        // GG reconnect sonrası temizlenir (presetler değişmiş olabilir)
        private readonly Dictionary<string, string> _presetNameCache = new(StringComparer.OrdinalIgnoreCase);

        public SonarWatcher(
            SteelSeriesClient client,
            int intervalMs,
            string defaultPresetId,
            Dictionary<string, string> rules,
            Action<string, string, string> onPresetChanged,
            Action<string> onActiveWindowChanged,
            Action<string> onLog,
            Action? onConnectionLost = null,
            Action? onConnectionRestored = null,
            Action<string?>? onGameSessionChanged = null)
        {
            _client = client;
            _intervalMs = intervalMs;
            _defaultPresetId = defaultPresetId;
            _rules = new Dictionary<string, string>(rules, StringComparer.OrdinalIgnoreCase);
            _onPresetChanged = onPresetChanged;
            _onActiveWindowChanged = onActiveWindowChanged;
            _onLog = onLog;
            _onConnectionLost = onConnectionLost;
            _onConnectionRestored = onConnectionRestored;
            _onGameSessionChanged = onGameSessionChanged;
        }

        public void Start()
        {
            _client.SonarRestarted -= OnSonarRestarted;
            _client.SonarRestarted += OnSonarRestarted;
            _timer?.Dispose();
            _timer = new System.Threading.Timer(async _ => await TickAsync(), null, 0,
                Math.Max(AppConfig.MinCheckIntervalMs, _intervalMs));
            _onLog("Watcher started.");
        }

        public void Stop()
        {
            // Watcher interval değişince yeniden oluşturulur; abonelik kalırsa eski watcher bellekte tutulur
            _client.SonarRestarted -= OnSonarRestarted;
            _timer?.Dispose();
            _timer = null;
            _onLog("Watcher stopped.");
        }

        /// <summary>
        /// Kuralları ve varsayılan preset'i watcher'ı durdurmadan günceller.
        /// _currentPresetId sıfırlanmaz — gereksiz API çağrısı olmaz.
        /// </summary>
        public void UpdateConfig(string defaultPresetId, Dictionary<string, string> rules)
        {
            lock (_configLock)
            {
                _defaultPresetId = defaultPresetId;
                _rules = new Dictionary<string, string>(rules, StringComparer.OrdinalIgnoreCase);
                _lastFailedPresetId = null; // Kural düzeltilmiş olabilir → beklemeden tekrar dene
            }
            _onLog("Config hot-reloaded (watcher devam ediyor).");
        }

        // Sonar yeniden başlayınca aktif preset sıfırlanabilir; "zaten uygulandı" durumu geçersizdir
        private int _forceReapply = 0;

        private void OnSonarRestarted() => Interlocked.Exchange(ref _forceReapply, 1);

        // ── Tick ─────────────────────────────────────────────────────────

        private async Task TickAsync()
        {
            // Atomik guard: önceki tick hâlâ çalışıyorsa bu tick'i atla
            if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0) return;

            try
            {
                if (Interlocked.Exchange(ref _forceReapply, 0) == 1 && _currentPresetId != null)
                {
                    _onLog("Sonar restart detected → active preset will be re-applied.");
                    _currentPresetId = null;
                    _presetNameCache.Clear();
                }

                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return;

                if (hwnd != _lastHwnd || pid != _lastPid)
                {
                    if (!TryResolveProcess(pid, out string resolvedProcess, out string resolvedExe))
                        return; // Process kapandı veya erişilemiyor

                    _lastHwnd = hwnd;
                    _lastPid = pid;
                    _lastProcessName = resolvedProcess;
                    _lastExeName = resolvedExe;
                }

                string processName = _lastProcessName;
                string exeName     = _lastExeName;

                bool windowChanged = !exeName.Equals(_currentActiveProcess, StringComparison.OrdinalIgnoreCase);
                if (windowChanged)
                {
                    _currentActiveProcess = exeName;
                    _onLog($"Active window changed to: {exeName}");
                    _onActiveWindowChanged?.Invoke(exeName);
                }

                // Thread-safe config snapshot al (kopyalamadan — UpdateConfig referansı tamamen değiştirir)
                string defaultPresetId;
                Dictionary<string, string> rules;
                lock (_configLock)
                {
                    defaultPresetId = _defaultPresetId;
                    rules = _rules;
                }

                string targetPresetId = defaultPresetId;
                string targetRuleName = "Desktop";
                bool foundRuleForForeground = false;

                // 1. Ön plandaki pencere kuralla eşleşiyor mu?
                if (rules.TryGetValue(exeName, out string? fgPreset) ||
                    rules.TryGetValue(processName, out fgPreset) ||
                    rules.TryGetValue(processName + ".exe", out fgPreset))
                {
                    targetPresetId = fgPreset;
                    targetRuleName = rules.Keys.First(k =>
                        k.Equals(exeName, StringComparison.OrdinalIgnoreCase) ||
                        k.Equals(processName, StringComparison.OrdinalIgnoreCase) ||
                        k.Equals(processName + ".exe", StringComparison.OrdinalIgnoreCase));
                    _activeRuleProcess = targetRuleName;
                    _activeRuleStillRunning = true;
                    _lastRunningCheckUtc = DateTime.UtcNow;
                    foundRuleForForeground = true;
                }

                // 2. Eşleşme yoksa: önceki aktif oyun hâlâ çalışıyor mu?
                //    Pencere değiştiğinde hemen, aynı pencerede kalındığında periyodik olarak kontrol edilir;
                //    böylece masaüstündeyken oyun kapanırsa da varsayılan profile dönülür.
                if (!foundRuleForForeground && !string.IsNullOrEmpty(_activeRuleProcess))
                {
                    if (windowChanged || DateTime.UtcNow - _lastRunningCheckUtc >= RunningCheckInterval)
                    {
                        _activeRuleStillRunning = IsProcessRunning(_activeRuleProcess);
                        _lastRunningCheckUtc = DateTime.UtcNow;
                    }

                    if (_activeRuleStillRunning && rules.TryGetValue(_activeRuleProcess, out string? rulePreset))
                    {
                        // Oyun hâlâ çalışıyor → kuralı korumaya devam et
                        targetPresetId = rulePreset;
                        targetRuleName = _activeRuleProcess;
                    }
                    else
                    {
                        // Oyun kapandı (veya kural silindi) → varsayılana dön
                        _activeRuleProcess = null;
                        targetPresetId = defaultPresetId;
                        targetRuleName = "Desktop";
                    }
                }

                // Oyun oturumu takibi (GG bağlantısından bağımsız): CSV kaydı gibi özellikler kullanır
                string? activeGame = targetRuleName == "Desktop" ? null : targetRuleName;
                if (!string.Equals(activeGame, _reportedGame, StringComparison.OrdinalIgnoreCase))
                {
                    _reportedGame = activeGame;
                    try { _onGameSessionChanged?.Invoke(activeGame); } catch { }
                }

                // 3. Gerekiyorsa preset'i değiştir
                if (string.IsNullOrWhiteSpace(targetPresetId)) return;   // Hedef tanımlı değil (ör. varsayılan seçilmemiş)
                if (_currentPresetId == targetPresetId) return;

                // Başarısız olan aynı hedefi her tick'te tekrar deneme; kısa bir bekleme uygula
                if (targetPresetId == _lastFailedPresetId && DateTime.UtcNow < _retryAfterUtc) return;

                _onLog($"Switching preset → rule: {targetRuleName} (ID: {targetPresetId})");
                await TrySwitchPresetAsync(targetPresetId, targetRuleName);
            }
            catch (Exception ex)
            {
                _onLog($"Watcher tick error: {ex.Message}");
            }
            finally
            {
                // Atomik olarak bayrağı sıfırla
                Interlocked.Exchange(ref _isProcessing, 0);
            }
        }

        private static bool TryResolveProcess(uint pid, out string processName, out string exeName)
        {
            processName = "";
            exeName = "";
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                processName = proc.ProcessName;
                exeName     = processName + ".exe";

                try
                {
                    string? mainModule = proc.MainModule?.ModuleName;
                    if (!string.IsNullOrEmpty(mainModule))
                        exeName = mainModule;
                }
                catch { /* Erişim reddedildi — processName.exe fallback kullan */ }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsProcessRunning(string ruleProcess)
        {
            try
            {
                string checkName = ruleProcess.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? ruleProcess.Substring(0, ruleProcess.Length - 4)
                    : ruleProcess;

                var procs = Process.GetProcessesByName(checkName);
                bool running = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                return running;
            }
            catch
            {
                return false;
            }
        }

        // ── Preset Switching ─────────────────────────────────────────────

        private async Task TrySwitchPresetAsync(string targetPresetId, string targetRuleName)
        {
            try
            {
                bool success = await _client.SetPresetAsync(targetPresetId);

                if (success)
                {
                    _currentPresetId = targetPresetId;
                    _lastFailedPresetId = null;
                    _failStreak = 0;
                    RegisterSuccess();

                    // Cache'de varsa direkt kullan — yoksa API'den al ve cache'e ekle
                    if (!_presetNameCache.TryGetValue(targetPresetId, out string? actualPresetName))
                    {
                        try
                        {
                            var configs = await _client.GetConfigsAsync();
                            foreach (var c in configs)
                                _presetNameCache[c.id] = c.name;

                            _presetNameCache.TryGetValue(targetPresetId, out actualPresetName);
                        }
                        catch { }
                    }

                    _onPresetChanged(targetRuleName, targetPresetId, actualPresetName ?? "Unknown EQ");
                }
                else
                {
                    // GG yanıt verdi ama preset'i reddetti (ör. silinmiş/geçersiz ID) — bağlantı hatası değil
                    _onLog($"API preset değiştirme başarısız: {targetRuleName} (ID: {targetPresetId})");
                    MarkFailed(targetPresetId, RetryDelayOnRejected);
                }
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                _onLog($"HTTP hatası (preset switch): {ex.Message}");
                MarkFailed(targetPresetId, RetryDelayOnError);
                RegisterError();
            }
            catch (TaskCanceledException ex)
            {
                _onLog($"Zaman aşımı (preset switch): {ex.Message}");
                MarkFailed(targetPresetId, RetryDelayOnError);
                RegisterError();
            }
            catch (Exception ex)
            {
                _onLog($"Beklenmedik hata (preset switch): {ex.Message}");
                MarkFailed(targetPresetId, RetryDelayOnError);
                RegisterError();
            }
        }

        private void MarkFailed(string presetId, TimeSpan baseDelay)
        {
            _failStreak = presetId == _lastFailedPresetId ? Math.Min(_failStreak + 1, 10) : 1;
            _lastFailedPresetId = presetId;

            double seconds = baseDelay.TotalSeconds * Math.Pow(2, _failStreak - 1);
            _retryAfterUtc = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
        }

        // ── Connection Health ─────────────────────────────────────────────

        private void RegisterSuccess()
        {
            Interlocked.Exchange(ref _consecutiveErrors, 0);

            if (_isConnectionLost)
            {
                _isConnectionLost = false;
                _presetNameCache.Clear(); // GG reconnect sonrası presetler değişmiş olabilir
                _onLog("SteelSeries GG bağlantısı yeniden kuruldu.");
                _onConnectionRestored?.Invoke();
            }
        }

        private void RegisterError()
        {
            int errors = Interlocked.Increment(ref _consecutiveErrors);

            if (errors >= MaxConsecutiveErrors && !_isConnectionLost)
            {
                _isConnectionLost = true;
                _onLog($"Bağlantı koptu ({errors} ardışık hata). Watcher yeniden bağlanmayı deniyor...");
                _onConnectionLost?.Invoke();
            }
        }
    }
}
