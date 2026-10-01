using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        // Timer
        private System.Threading.Timer? _timer;

        // State
        private string? _currentActiveProcess;
        private string? _currentPresetId;
        private string? _activeRuleProcess;

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
            Action? onConnectionRestored = null)
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
        }

        public void Start()
        {
            _timer = new System.Threading.Timer(async _ => await TickAsync(), null, 0, _intervalMs);
            _onLog("Watcher started.");
        }

        public void Stop()
        {
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
            }
            _onLog("Config hot-reloaded (watcher devam ediyor).");
        }

        // ── Tick ─────────────────────────────────────────────────────────

        private async Task TickAsync()
        {
            // Atomik guard: önceki tick hâlâ çalışıyorsa bu tick'i atla
            if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0) return;

            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return;

                string processName = "";
                string exeName     = "";

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
                }
                catch
                {
                    return; // Process kapandı veya erişilemiyor
                }

                // Aynı process zaten aktifse yapacak bir şey yok
                if (exeName.Equals(_currentActiveProcess, StringComparison.OrdinalIgnoreCase))
                    return;

                _currentActiveProcess = exeName;
                _onLog($"Active window changed to: {exeName}");
                _onActiveWindowChanged?.Invoke(exeName);

                // Thread-safe config snapshot al
                string defaultPresetId;
                Dictionary<string, string> rules;
                lock (_configLock)
                {
                    defaultPresetId = _defaultPresetId;
                    rules = new Dictionary<string, string>(_rules, StringComparer.OrdinalIgnoreCase);
                }

                string targetPresetId = defaultPresetId;
                string targetRuleName = "Desktop";
                bool foundRuleForForeground = false;

                // 1. Ön plandaki pencere kuralla eşleşiyor mu?
                foreach (var rule in rules)
                {
                    if (exeName.Equals(rule.Key, StringComparison.OrdinalIgnoreCase) ||
                        processName.Equals(rule.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        targetPresetId = rule.Value;
                        targetRuleName = rule.Key;
                        _activeRuleProcess = rule.Key;
                        foundRuleForForeground = true;
                        break;
                    }
                }

                // 2. Eşleşme yoksa: önceki aktif oyun hâlâ çalışıyor mu?
                if (!foundRuleForForeground && !string.IsNullOrEmpty(_activeRuleProcess))
                {
                    bool isStillRunning = false;
                    try
                    {
                        string checkName = _activeRuleProcess.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            ? _activeRuleProcess.Substring(0, _activeRuleProcess.Length - 4)
                            : _activeRuleProcess;

                        var procs = Process.GetProcessesByName(checkName);
                        isStillRunning = procs.Length > 0;
                        for (int i = 0; i < procs.Length; i++)
                        {
                            procs[i].Dispose();
                        }
                    }
                    catch { }

                    if (isStillRunning && rules.TryGetValue(_activeRuleProcess, out string? rulePreset))
                    {
                        // Oyun hâlâ çalışıyor → kuralı korumaya devam et
                        targetPresetId = rulePreset;
                        targetRuleName = _activeRuleProcess;
                    }
                    else
                    {
                        // Oyun kapandı → varsayılana dön
                        _activeRuleProcess = null;
                        targetPresetId = defaultPresetId;
                        targetRuleName = "Desktop";
                    }
                }

                // 3. Gerekiyorsa preset'i değiştir
                if (_currentPresetId != targetPresetId)
                {
                    _onLog($"Switching preset → rule: {targetRuleName} (ID: {targetPresetId})");
                    await TrySwitchPresetAsync(targetPresetId, targetRuleName);
                }
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

        // ── Preset Switching ─────────────────────────────────────────────

        private async Task TrySwitchPresetAsync(string targetPresetId, string targetRuleName)
        {
            try
            {
                bool success = await _client.SetPresetAsync(targetPresetId);

                if (success)
                {
                    _currentPresetId = targetPresetId;
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
                    _onLog($"API preset değiştirme başarısız: {targetRuleName}");
                }
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                _onLog($"HTTP hatası (preset switch): {ex.Message}");
                RegisterError();
            }
            catch (TaskCanceledException ex)
            {
                _onLog($"Zaman aşımı (preset switch): {ex.Message}");
                RegisterError();
            }
            catch (Exception ex)
            {
                _onLog($"Beklenmedik hata (preset switch): {ex.Message}");
                RegisterError();
            }
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
