using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace Warden
{
    public class TelemetrySensorItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string RawName { get; set; } = "";       // LibreHardwareMonitor'daki orijinal sensör adı
        public string Category { get; set; } = ""; // CPU, GPU, Fans, Motherboard, Memory
        public string HardwareName { get; set; } = "";
        public SensorType SensorType { get; set; }
        public float Value { get; set; }
        public string FormattedValue { get; set; } = "";
        public string Unit { get; set; } = "";
        public bool IsFavorite { get; set; }
        public bool IsOnGraph { get; set; }
        public string GraphColor { get; set; } = "#FF8A3D";
        public List<float> History { get; } = new(60);

        public void AddHistory(float val)
        {
            History.Add(val);
            if (History.Count > 60)
            {
                History.RemoveAt(0);
            }
        }

        /// <summary>
        /// UI'ya gönderilecek bağımsız kopya. Arka plan thread'i History'yi değiştirirken UI thread'i
        /// aynı listeyi grafikte gezerse "Collection was modified" hatası oluşuyordu.
        /// </summary>
        public TelemetrySensorItem Clone()
        {
            var copy = new TelemetrySensorItem
            {
                Id = Id,
                Name = Name,
                RawName = RawName,
                Category = Category,
                HardwareName = HardwareName,
                SensorType = SensorType,
                Value = Value,
                FormattedValue = FormattedValue,
                Unit = Unit,
                IsFavorite = IsFavorite,
                IsOnGraph = IsOnGraph,
                GraphColor = GraphColor
            };
            copy.History.AddRange(History);
            return copy;
        }
    }

    /// <summary>Birincil ekran kartının özet değerleri (NVIDIA, AMD veya Intel).</summary>
    public class GpuData
    {
        public string Name { get; set; } = "";
        public string Vendor { get; set; } = "";
        public double CoreClockMhz { get; set; }
        public int TemperatureCelsius { get; set; }
        public int UsagePercentage { get; set; }
        public float? PowerWatts { get; set; }
    }

    public class TelemetrySnapshot
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        /// <summary>Birincil GPU; hiç GPU sensörü yoksa null.</summary>
        public GpuData? PrimaryGpu { get; set; }
        /// <summary>CPU paket/Tctl sıcaklığı (°C); okunamıyorsa null (ör. PawnIO kurulu değil).</summary>
        public float? CpuTemperature { get; set; }
        /// <summary>CPU paket gücü (W), en yüksek çekirdek saati (MHz) ve toplam yük (%); okunamıyorsa null.</summary>
        public float? CpuPowerWatts { get; set; }
        public float? CpuClockMhz { get; set; }
        public float? CpuLoadPercent { get; set; }
        /// <summary>LibreHardwareMonitor açıldı ve en az bir tarama yapıldı.</summary>
        public bool HardwareReady { get; set; }
        public List<TelemetrySensorItem> Favorites { get; set; } = new();
        public List<TelemetrySensorItem> GraphSensors { get; set; } = new();
        public Dictionary<string, List<TelemetrySensorItem>> Categories { get; set; } = new();
        public List<TelemetrySensorItem> AllSensors { get; set; } = new();
    }

    public class HardwareMonitorService : IDisposable
    {
        private Computer? _computer;
        private System.Threading.Timer? _timer;
        // _lock: sensör sözlüğü ve favori/grafik kümeleri (UI thread'i de alır → kısa tutulur)
        // _hwLock: LibreHardwareMonitor donanım erişimi (Update/Close; yüzlerce ms sürebilir)
        // Kilit sırası her zaman _hwLock → _lock
        private readonly object _lock = new();
        private readonly object _hwLock = new();
        private int _isUpdating = 0;
        private bool _isInitialized = false;
        private int _openGeneration;   // Release() artırır: arka planda süren açılış sonradan bırakılır

        private readonly Dictionary<string, TelemetrySensorItem> _sensors = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _favoriteIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _graphIds = new(StringComparer.OrdinalIgnoreCase);

        public static readonly string[] GraphColors =
        {
            "#FF8A3D", // Orange (accent)
            "#38BDF8", // Sky Blue
            "#4ADE80", // Lime Green
            "#C084FC", // Purple
            "#FACC15", // Amber Yellow
            "#2DD4BF", // Teal
            "#FF3B5C", // Neon Red
            "#EDEDF2"  // White
        };

        public event EventHandler<TelemetrySnapshot>? TelemetryUpdated;

        /// <summary>
        /// true: tüm donanım taranır (telemetri sayfası açık veya kayıt sürüyor).
        /// false: arka plan modu, yalnızca CPU ve GPU taranır (alarm / GPU profili için yeterli, daha hafif).
        /// </summary>
        public volatile bool FullScan = true;

        private GpuData? _primaryGpu;
        private int _intervalMs;

        /// <summary>
        /// LibreHardwareMonitor 0.9.6+ eski WinRing0 yerine PawnIO sürücüsünü kullanır (WinRing0'ı Defender
        /// "vulnerable driver" olarak engelliyor). PawnIO kurulu değilse CPU sıcaklık/saat/güç gibi MSR
        /// tabanlı sensörler boş gelir; arayüz bunu kullanıcıya göstermek için bu bilgiyi kullanır.
        /// </summary>
        public static bool IsPawnIoInstalled
        {
            get
            {
                try { return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
                catch { return false; }
            }
        }

        private static readonly object LogLock = new();

        private static void LogTelemetry(string message)
        {
            try
            {
                string logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Warden", "logs");
                System.IO.Directory.CreateDirectory(logDir);
                string logPath = System.IO.Path.Combine(logDir, "telemetry.log");
                lock (LogLock)
                {
                    // Her açılışta sensör dökümü eklendiği için dosya sınırsız büyüyordu → 1 MB'ta döndür
                    var fi = new System.IO.FileInfo(logPath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string bak = logPath + ".bak";
                        if (System.IO.File.Exists(bak)) System.IO.File.Delete(bak);
                        System.IO.File.Move(logPath, bak);
                    }
                    System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
                }
            }
            catch { }
        }

        private bool _loggedFirstRun = false;

        private bool _isInitializingComputer = false;
        private bool _disposed = false;

        // Kullanıcının kayıtlı grafik sensörü yoksa CPU/GPU sıcaklığı yalnızca bir kez varsayılan olarak eklenir.
        // (Eskiden her tick'te kontrol ediliyordu; kullanıcı grafikteki tüm sensörleri kaldıramıyordu.)
        private bool _applyDefaultGraph = false;

        public void Initialize(List<string> favorites, List<string> graphSensors)
        {
            lock (_lock)
            {
                _favoriteIds.Clear();
                foreach (var f in favorites) _favoriteIds.Add(f);

                _graphIds.Clear();
                foreach (var g in graphSensors) _graphIds.Add(g);
                _applyDefaultGraph = _graphIds.Count == 0;

                if (_computer == null && !_isInitializingComputer)
                {
                    _isInitializingComputer = true;
                    int generation = _openGeneration;
                    Task.Run(() =>
                    {
                        try
                        {
                            LogTelemetry("Initializing LibreHardwareMonitor Computer in background...");
                            var comp = new Computer
                            {
                                IsCpuEnabled = true,
                                IsGpuEnabled = true,
                                IsMotherboardEnabled = true,
                                IsMemoryEnabled = true,
                                IsControllerEnabled = true,
                                IsStorageEnabled = false
                            };
                            comp.Open();
                            lock (_hwLock)
                            lock (_lock)
                            {
                                if (_disposed || generation != _openGeneration)
                                {
                                    comp.Close();
                                    return;
                                }
                                _computer = comp;
                                _isInitialized = true;
                            }
                            LogTelemetry("LibreHardwareMonitor Computer opened successfully in background.");

                            UpdateSensorsInternal();
                            LogTelemetry($"Initial scan completed. Total sensors captured: {SensorCount}");
                        }
                        catch (Exception ex)
                        {
                            LogTelemetry($"Computer Open failed: {ex.Message}\n{ex.StackTrace}");
                        }
                        finally
                        {
                            _isInitializingComputer = false;
                        }
                    });
                }
            }
        }

        private int SensorCount { get { lock (_lock) return _sensors.Count; } }

        public bool IsRunning { get { lock (_lock) return _timer != null; } }

        public void Start(int intervalMs = 1000)
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (_timer != null && _intervalMs == intervalMs) return;   // zaten bu aralıkla çalışıyor
                _intervalMs = intervalMs;
                _timer?.Dispose();
                _timer = new System.Threading.Timer(async _ => await TickAsync(), null, 0, intervalMs);
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        /// <summary>
        /// Durdurur ve LibreHardwareMonitor'u (çekirdek sürücüsüyle birlikte) kapatır. Donanım izleme modülü
        /// kapatılınca çağrılır; sonraki Initialize yeniden açar.
        /// </summary>
        public void Release()
        {
            Stop();
            lock (_hwLock)
            {
                Computer? computer;
                lock (_lock)
                {
                    computer = _computer;
                    _computer = null;
                    _isInitialized = false;
                    _openGeneration++;
                }
                try { computer?.Close(); }
                catch { }
            }
            LogTelemetry("LibreHardwareMonitor released (hardware module off).");
        }

        public bool ToggleFavorite(string sensorId)
        {
            bool isFav;
            lock (_lock)
            {
                if (_favoriteIds.Contains(sensorId))
                {
                    _favoriteIds.Remove(sensorId);
                    isFav = false;
                }
                else
                {
                    _favoriteIds.Add(sensorId);
                    isFav = true;
                }

                if (_sensors.TryGetValue(sensorId, out var item))
                {
                    item.IsFavorite = isFav;
                }
            }

            NotifySnapshot();
            return isFav;
        }

        public bool ToggleGraph(string sensorId)
        {
            bool isOnGraph;
            lock (_lock)
            {
                _applyDefaultGraph = false;
                if (_graphIds.Contains(sensorId))
                {
                    _graphIds.Remove(sensorId);
                    isOnGraph = false;
                }
                else
                {
                    _graphIds.Add(sensorId);
                    isOnGraph = true;
                }

                if (_sensors.TryGetValue(sensorId, out var item))
                {
                    item.IsOnGraph = isOnGraph;
                }

                AssignGraphColors();
            }

            NotifySnapshot();
            return isOnGraph;
        }

        public List<string> GetFavoriteIds()
        {
            lock (_lock) return _favoriteIds.ToList();
        }

        public List<string> GetGraphSensorIds()
        {
            lock (_lock) return _graphIds.ToList();
        }

        private async Task TickAsync()
        {
            if (Interlocked.CompareExchange(ref _isUpdating, 1, 0) != 0) return;

            try
            {
                await Task.Run(() =>
                {
                    UpdateSensorsInternal();
                });

                NotifySnapshot();
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _isUpdating, 0);
            }
        }

        private void UpdateSensorsInternal()
        {
            // Donanım okuması yalnızca _hwLock altında yapılır; UI'nin beklediği _lock bu sırada serbesttir.
            // Eskiden tüm hw.Update() çağrıları _lock altındaydı ve ★/📈 tıklamaları UI'yi dondurabiliyordu.
            lock (_hwLock)
            {
                Computer? computer;
                lock (_lock)
                {
                    if (_computer == null || !_isInitialized || _disposed) return;
                    computer = _computer;
                }

                try
                {
                    if (!_loggedFirstRun)
                    {
                        _loggedFirstRun = true;
                        LogTelemetry("=== Initial Hardware & Sensors Discovery ===");
                        foreach (var hw in computer.Hardware)
                        {
                            LogTelemetry($"Hardware: '{hw.Name}' [{hw.HardwareType}]");
                            hw.Update();
                            foreach (var s in hw.Sensors)
                            {
                                LogTelemetry($"  Sensor: '{s.Name}' [{s.SensorType}] = {s.Value}");
                            }
                            foreach (var sub in hw.SubHardware)
                            {
                                LogTelemetry($"  SubHardware: '{sub.Name}' [{sub.HardwareType}]");
                                sub.Update();
                                foreach (var s in sub.Sensors)
                                {
                                    LogTelemetry($"    Sensor: '{s.Name}' [{s.SensorType}] = {s.Value}");
                                }
                            }
                        }
                        LogTelemetry("=============================================");
                    }

                    // 1. Yavaş kısım: donanımdan oku (yalnızca _hwLock). Arka plan modunda yalnızca CPU/GPU.
                    bool full = FullScan;
                    var scanned = computer.Hardware.Where(h => full || IsCpuOrGpu(h.HardwareType)).ToList();
                    foreach (var hardware in scanned)
                    {
                        hardware.Update();
                        foreach (var sub in hardware.SubHardware)
                            sub.Update();
                    }
                    GpuData? primaryGpu = ReadPrimaryGpu(scanned);

                    // 2. Hızlı kısım: okunan değerleri sözlüğe işle (kısa süreli _lock)
                    lock (_lock)
                    {
                        if (_disposed) return;

                        _primaryGpu = primaryGpu;

                        foreach (var hardware in scanned)
                        {
                            ProcessHardwareSensors(hardware, hardware.Name);
                            foreach (var sub in hardware.SubHardware)
                                ProcessHardwareSensors(sub, $"{hardware.Name} - {sub.Name}");
                        }

                        // Eğer kullanıcının kayıtlı grafik sensörü yoksa, ilk seferde CPU ve GPU sıcaklıklarını varsayılan yap
                        if (_applyDefaultGraph && _graphIds.Count == 0 && _sensors.Count > 0)
                        {
                            var defaultCpu = _sensors.Values.FirstOrDefault(s => s.Category == "CPU" && s.SensorType == SensorType.Temperature);
                            var defaultGpu = _sensors.Values.FirstOrDefault(s => s.Category == "GPU" && s.SensorType == SensorType.Temperature);

                            if (defaultCpu != null) { _graphIds.Add(defaultCpu.Id); defaultCpu.IsOnGraph = true; }
                            if (defaultGpu != null) { _graphIds.Add(defaultGpu.Id); defaultGpu.IsOnGraph = true; }
                            _applyDefaultGraph = false;
                        }

                        AssignGraphColors();
                    }
                }
                catch (Exception ex)
                {
                    LogTelemetry($"UpdateSensorsInternal error: {ex.Message}");
                }
            }
        }

        private static bool IsCpuOrGpu(HardwareType type)
            => type is HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

        /// <summary>
        /// Birincil GPU: harici kartlar (NVIDIA, AMD) Intel'in önüne geçer. Tüm üreticilerde LibreHardwareMonitor
        /// çekirdek sensörlerini "GPU Core" (sıcaklık / saat / yük) olarak adlandırır.
        /// </summary>
        private static GpuData? ReadPrimaryGpu(IEnumerable<IHardware> hardware)
        {
            var gpu = hardware
                .Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                .OrderBy(h => h.HardwareType switch
                {
                    HardwareType.GpuNvidia => 0,
                    HardwareType.GpuAmd => 1,
                    _ => 2
                })
                .FirstOrDefault();
            if (gpu == null) return null;

            float? Read(SensorType type) => gpu.Sensors
                .FirstOrDefault(s => s.SensorType == type && s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))?.Value;

            return new GpuData
            {
                Name = gpu.Name,
                Vendor = gpu.HardwareType switch
                {
                    HardwareType.GpuNvidia => "NVIDIA",
                    HardwareType.GpuAmd => "AMD",
                    _ => "Intel"
                },
                CoreClockMhz = Read(SensorType.Clock) ?? 0,
                TemperatureCelsius = (int)Math.Round(Read(SensorType.Temperature) ?? 0),
                UsagePercentage = (int)Math.Round(Read(SensorType.Load) ?? 0),
                // Kart gücü: NVIDIA/AMD "GPU Package", Intel "GPU Power"; yoksa ilk güç sensörü
                PowerWatts = ReadPower(gpu)
            };
        }

        private static float? ReadPower(IHardware gpu)
        {
            var power = gpu.Sensors.Where(s => s.SensorType == SensorType.Power).ToList();
            return (power.FirstOrDefault(s => s.Name.Equals("GPU Package", StringComparison.OrdinalIgnoreCase)) ??
                    power.FirstOrDefault(s => s.Name.Equals("GPU Power", StringComparison.OrdinalIgnoreCase)) ??
                    power.FirstOrDefault())?.Value;
        }

        private void ProcessHardwareSensors(IHardware hardware, string hardwareName)
        {
            string category = DetermineCategory(hardware);

            foreach (var sensor in hardware.Sensors)
            {
                if (!sensor.Value.HasValue) continue;

                if (!ShouldIncludeSensor(sensor, category, out string displayName, out string targetCategory))
                {
                    continue;
                }

                string id = sensor.Identifier.ToString();
                float val = sensor.Value.Value;

                if (!_sensors.TryGetValue(id, out var item))
                {
                    item = new TelemetrySensorItem
                    {
                        Id = id,
                        Name = displayName,
                        RawName = sensor.Name,
                        Category = targetCategory,
                        HardwareName = hardwareName,
                        SensorType = sensor.SensorType,
                        Unit = GetSensorUnit(sensor.SensorType)
                    };
                    _sensors[id] = item;
                }
                else
                {
                    item.Name = displayName;
                    item.Category = targetCategory;
                }

                item.Value = val;
                item.FormattedValue = FormatValue(val, item.SensorType);
                item.AddHistory(val);
                item.IsFavorite = _favoriteIds.Contains(id);
                item.IsOnGraph = _graphIds.Contains(id);
            }
        }

        /// <summary>Kendisi dışında, verilen kategori ve tipte zaten listelenen bir sensör var mı?</summary>
        private bool HasOtherSensor(string category, SensorType type, string ownId)
            => _sensors.Values.Any(s => s.Category == category && s.SensorType == type && s.Id != ownId);

        private bool ShouldIncludeSensor(ISensor sensor, string category, out string displayName, out string targetCategory)
        {
            displayName = sensor.Name;
            targetCategory = category;
            string ownId = sensor.Identifier.ToString();

            if (category == "GPU")
            {
                if (sensor.SensorType == SensorType.Temperature)
                {
                    if (sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("GPU", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("GPU Temperature", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = Loc.Get("SensorGpuTemp");
                        return true;
                    }
                    if (sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = Loc.Get("SensorGpuHotSpot");
                        return true;
                    }
                }
                else if (sensor.SensorType == SensorType.Power)
                {
                    if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("Board", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("GPU Power", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("GPU", StringComparison.OrdinalIgnoreCase))
                    {
                        if (HasOtherSensor("GPU", SensorType.Power, ownId))
                            return false;

                        displayName = Loc.Get("SensorGpuPower");
                        return true;
                    }
                }
                else if (sensor.SensorType == SensorType.Clock)
                {
                    if (sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = Loc.Get("SensorGpuCoreClock");
                        return true;
                    }
                    if (sensor.Name.Equals("GPU Memory", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = Loc.Get("SensorGpuMemClock");
                        return true;
                    }
                }
                else if (sensor.SensorType == SensorType.Load)
                {
                    if (sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("GPU", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = Loc.Get("SensorGpuLoad");
                        return true;
                    }
                }
                else if (sensor.SensorType == SensorType.Fan)
                {
                    targetCategory = "Fans";
                    displayName = sensor.Name.Contains("Fan", StringComparison.OrdinalIgnoreCase) ? sensor.Name : $"{sensor.Name} (RPM)";
                    return true;
                }
                else if (sensor.SensorType == SensorType.Control && sensor.Name.Contains("Fan", StringComparison.OrdinalIgnoreCase))
                {
                    targetCategory = "Fans";
                    displayName = $"{sensor.Name} (%)";
                    return true;
                }

                return false;
            }

            if (category == "CPU")
            {
                if (sensor.SensorType == SensorType.Temperature)
                {
                    bool isPrimary =
                        sensor.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("Package", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase);
                    bool isFallback =
                        sensor.Name.Equals("CPU Core", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("Core Average", StringComparison.OrdinalIgnoreCase);

                    if (isPrimary || isFallback)
                    {
                        var other = _sensors.Values.FirstOrDefault(s =>
                            s.Category == "CPU" && s.SensorType == SensorType.Temperature && s.Id != ownId);

                        if (other != null)
                        {
                            // Birincil (Package/Tctl) sensör, yedek (Core Average) olanın yerini alır;
                            // aksi halde aynı adla iki "CPU Paket Sıcaklığı" satırı oluşmaz.
                            bool otherIsPrimary =
                                other.RawName.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) ||
                                other.RawName.Equals("Package", StringComparison.OrdinalIgnoreCase) ||
                                other.RawName.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase);
                            if (isPrimary && !otherIsPrimary)
                                _sensors.Remove(other.Id);
                            else
                                return false;
                        }

                        displayName = Loc.Get("SensorCpuTemp");
                        return true;
                    }
                }
                else if (sensor.SensorType == SensorType.Power)
                {
                    if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("PPT", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("CPU", StringComparison.OrdinalIgnoreCase))
                    {
                        var existingPower = _sensors.Values.FirstOrDefault(s => s.Category == "CPU" && s.SensorType == SensorType.Power && s.Id != ownId);
                        if (existingPower != null)
                        {
                            // Not: eskiden HardwareName (işlemci adı) kontrol ediliyordu; doğru olan mevcut sensörün adıdır
                            if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) && !existingPower.RawName.Contains("Package", StringComparison.OrdinalIgnoreCase))
                            {
                                _sensors.Remove(existingPower.Id);
                            }
                            else
                            {
                                return false;
                            }
                        }

                        displayName = Loc.Get("SensorCpuPower");
                        return true;
                    }
                }
                else if (sensor.SensorType == SensorType.Clock)
                {
                    if (sensor.Name.Equals("Core Max", StringComparison.OrdinalIgnoreCase))
                    {
                        var existingClock = _sensors.Values.FirstOrDefault(s => s.Category == "CPU" && s.SensorType == SensorType.Clock && s.Id != ownId);
                        if (existingClock != null)
                        {
                            _sensors.Remove(existingClock.Id);
                        }

                        displayName = Loc.Get("SensorCpuClock");
                        return true;
                    }

                    if (sensor.Name.Equals("P-Core #1", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("CPU Core #1", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("Core #1", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("Core Average", StringComparison.OrdinalIgnoreCase))
                    {
                        if (HasOtherSensor("CPU", SensorType.Clock, ownId))
                            return false;

                        displayName = Loc.Get("SensorCpuClock");
                        return true;
                    }
                }
                else if (sensor.SensorType == SensorType.Load)
                {
                    if (sensor.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("Total", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = Loc.Get("SensorCpuLoad");
                        return true;
                    }
                }

                return false;
            }

            if (category == "Fans")
            {
                if (sensor.SensorType == SensorType.Fan || sensor.SensorType == SensorType.Control)
                {
                    displayName = sensor.Name;
                    return true;
                }
                return false;
            }

            if (category == "Motherboard")
            {
                if (sensor.SensorType == SensorType.Temperature)
                {
                    if (sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                    {
                        // Check if CPU hardware already provided a temperature.
                        // Kendi kaydı hariç tutulur; aksi halde sonraki tick'te kendini "başka sensör" sanıp
                        // CPU ↔ Anakart kategorileri arasında her saniye gidip geliyordu.
                        if (!HasOtherSensor("CPU", SensorType.Temperature, ownId))
                        {
                            targetCategory = "CPU";
                            displayName = Loc.Get("SensorCpuTemp");
                            return true;
                        }
                    }

                    displayName = sensor.Name switch
                    {
                        "System" => Loc.Get("SensorSystemTemp"),
                        "Chipset" => Loc.Get("SensorChipset"),
                        "VRM MOS" => Loc.Get("SensorVrm"),
                        _ => sensor.Name
                    };
                    return true;
                }
                if (sensor.SensorType == SensorType.Fan || sensor.SensorType == SensorType.Control)
                {
                    targetCategory = "Fans";
                    displayName = sensor.Name;
                    return true;
                }
                return false;
            }

            if (category == "Memory")
            {
                if (sensor.SensorType == SensorType.Load && sensor.Name.Equals("Memory", StringComparison.OrdinalIgnoreCase))
                {
                    displayName = Loc.Get("SensorMemLoad");
                    return true;
                }
                if (sensor.SensorType == SensorType.Data && sensor.Name.Equals("Memory Used", StringComparison.OrdinalIgnoreCase))
                {
                    displayName = Loc.Get("SensorMemUsed");
                    return true;
                }
                return false;
            }

            return false;
        }

        private static string DetermineCategory(IHardware hardware)
        {
            return hardware.HardwareType switch
            {
                HardwareType.Cpu => "CPU",
                HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "GPU",
                HardwareType.Motherboard or HardwareType.SuperIO => "Motherboard",
                HardwareType.Memory => "Memory",
                _ => "Other"
            };
        }

        private static string GetSensorUnit(SensorType type)
        {
            return type switch
            {
                SensorType.Temperature => "°C",
                SensorType.Power => "W",
                SensorType.Clock => "MHz",
                SensorType.Fan => "RPM",
                SensorType.Control => "%",
                SensorType.Load => "%",
                SensorType.Data => "GB",
                SensorType.SmallData => "MB",
                SensorType.Voltage => "V",
                _ => ""
            };
        }

        private static string FormatValue(float val, SensorType type)
        {
            return type switch
            {
                SensorType.Temperature => $"{val:F1} °C",
                SensorType.Power => $"{val:F1} W",
                SensorType.Clock => $"{val:F0} MHz",
                SensorType.Fan => $"{val:F0} RPM",
                SensorType.Control => $"%{val:F0}",
                SensorType.Load => $"%{val:F1}",
                SensorType.Data => $"{val:F1} GB",
                SensorType.SmallData => $"{val:F0} MB",
                SensorType.Voltage => $"{val:F2} V",
                _ => $"{val:F1}"
            };
        }

        private void AssignGraphColors()
        {
            int colorIdx = 0;
            foreach (var item in _sensors.Values.Where(s => s.IsOnGraph))
            {
                item.GraphColor = GraphColors[colorIdx % GraphColors.Length];
                colorIdx++;
            }
        }

        private void NotifySnapshot()
        {
            TelemetrySnapshot snapshot;
            lock (_lock)
            {
                var all = _sensors.Values.Select(s => s.Clone()).ToList();
                var favs = all.Where(s => s.IsFavorite).ToList();
                var graphs = all.Where(s => s.IsOnGraph).ToList();

                var cats = new Dictionary<string, List<TelemetrySensorItem>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "CPU", all.Where(s => s.Category == "CPU").ToList() },
                    { "GPU", all.Where(s => s.Category == "GPU").ToList() },
                    { "Fans", all.Where(s => s.Category == "Fans").ToList() },
                    { "Motherboard", all.Where(s => s.Category == "Motherboard").ToList() },
                    { "Memory", all.Where(s => s.Category == "Memory").ToList() }
                };

                var cpuTemp = _sensors.Values.FirstOrDefault(x => x.Category == "CPU" && x.SensorType == SensorType.Temperature);
                float? Cpu(SensorType type) => _sensors.Values.FirstOrDefault(x => x.Category == "CPU" && x.SensorType == type)?.Value;

                snapshot = new TelemetrySnapshot
                {
                    Timestamp = DateTime.Now,
                    PrimaryGpu = _primaryGpu,
                    CpuTemperature = cpuTemp?.Value,
                    CpuPowerWatts = Cpu(SensorType.Power),
                    CpuClockMhz = Cpu(SensorType.Clock),
                    CpuLoadPercent = Cpu(SensorType.Load),
                    HardwareReady = _isInitialized,
                    Favorites = favs,
                    GraphSensors = graphs,
                    Categories = cats,
                    AllSensors = all
                };
            }

            // Olay kilit dışında tetiklenir: abone senkron Dispatcher.Invoke yaparsa kilitlenme (deadlock) olmaz
            TelemetryUpdated?.Invoke(this, snapshot);
        }

        public void Dispose()
        {
            Stop();
            // Kilit altında kapatılır: devam eden bir tick'in kapatılmış Computer'a erişmesi önlenir
            lock (_hwLock)
            {
                Computer? computer;
                lock (_lock)
                {
                    _disposed = true;
                    computer = _computer;
                    _computer = null;
                    _isInitialized = false;
                }
                try
                {
                    computer?.Close();
                }
                catch { }
            }
        }
    }
}
