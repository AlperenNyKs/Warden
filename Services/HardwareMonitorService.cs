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
        public string Category { get; set; } = ""; // CPU, GPU, Fans, Motherboard, Memory
        public string HardwareName { get; set; } = "";
        public SensorType SensorType { get; set; }
        public float Value { get; set; }
        public string FormattedValue { get; set; } = "";
        public string Unit { get; set; } = "";
        public bool IsFavorite { get; set; }
        public bool IsOnGraph { get; set; }
        public string GraphColor { get; set; } = "#00C9B1";
        public List<float> History { get; } = new(60);

        public void AddHistory(float val)
        {
            History.Add(val);
            if (History.Count > 60)
            {
                History.RemoveAt(0);
            }
        }
    }

    public class TelemetrySnapshot
    {
        public List<TelemetrySensorItem> Favorites { get; set; } = new();
        public List<TelemetrySensorItem> GraphSensors { get; set; } = new();
        public Dictionary<string, List<TelemetrySensorItem>> Categories { get; set; } = new();
        public List<TelemetrySensorItem> AllSensors { get; set; } = new();
    }

    public class HardwareMonitorService : IDisposable
    {
        private Computer? _computer;
        private System.Threading.Timer? _timer;
        private readonly object _lock = new();
        private int _isUpdating = 0;
        private bool _isInitialized = false;

        private readonly Dictionary<string, TelemetrySensorItem> _sensors = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _favoriteIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _graphIds = new(StringComparer.OrdinalIgnoreCase);

        public static readonly string[] GraphColors =
        {
            "#00C9B1", // Turquoise
            "#FF3B5C", // Neon Red
            "#38BDF8", // Sky Blue
            "#FACC15", // Amber Yellow
            "#4ADE80", // Lime Green
            "#C084FC", // Purple
            "#FB923C", // Orange
            "#F43F5E"  // Rose
        };

        public event EventHandler<TelemetrySnapshot>? TelemetryUpdated;

        private static void LogTelemetry(string message)
        {
            try
            {
                string logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Warden", "logs");
                System.IO.Directory.CreateDirectory(logDir);
                string logPath = System.IO.Path.Combine(logDir, "telemetry.log");
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        private bool _loggedFirstRun = false;

        private bool _isInitializingComputer = false;

        public void Initialize(List<string> favorites, List<string> graphSensors)
        {
            lock (_lock)
            {
                _favoriteIds.Clear();
                foreach (var f in favorites) _favoriteIds.Add(f);

                _graphIds.Clear();
                foreach (var g in graphSensors) _graphIds.Add(g);

                if (_computer == null && !_isInitializingComputer)
                {
                    _isInitializingComputer = true;
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
                            lock (_lock)
                            {
                                _computer = comp;
                                _isInitialized = true;
                            }
                            LogTelemetry("LibreHardwareMonitor Computer opened successfully in background.");

                            UpdateSensorsInternal();
                            LogTelemetry($"Initial scan completed. Total sensors captured: {_sensors.Count}");
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

        public void Start(int intervalMs = 1000)
        {
            lock (_lock)
            {
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

        public bool ToggleFavorite(string sensorId)
        {
            lock (_lock)
            {
                bool isFav;
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

                NotifySnapshot();
                return isFav;
            }
        }

        public bool ToggleGraph(string sensorId)
        {
            lock (_lock)
            {
                bool isOnGraph;
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
                NotifySnapshot();
                return isOnGraph;
            }
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
            if (_computer == null || !_isInitialized) return;

            lock (_lock)
            {
                try
                {
                    if (!_loggedFirstRun)
                    {
                        _loggedFirstRun = true;
                        LogTelemetry("=== Initial Hardware & Sensors Discovery ===");
                        foreach (var hw in _computer.Hardware)
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

                    foreach (var hardware in _computer.Hardware)
                    {
                        hardware.Update();
                        ProcessHardwareSensors(hardware, hardware.Name);

                        foreach (var sub in hardware.SubHardware)
                        {
                            sub.Update();
                            ProcessHardwareSensors(sub, $"{hardware.Name} - {sub.Name}");
                        }
                    }

                    // Eğer kullanıcı hiç grafik sensörü seçmediyse ilk seferde CPU ve GPU sıcaklıklarını varsayılan yap
                    if (_graphIds.Count == 0 && _sensors.Count > 0)
                    {
                        var defaultCpu = _sensors.Values.FirstOrDefault(s => s.Category == "CPU" && s.SensorType == SensorType.Temperature);
                        var defaultGpu = _sensors.Values.FirstOrDefault(s => s.Category == "GPU" && s.SensorType == SensorType.Temperature);

                        if (defaultCpu != null) _graphIds.Add(defaultCpu.Id);
                        if (defaultGpu != null) _graphIds.Add(defaultGpu.Id);
                    }

                    AssignGraphColors();
                }
                catch (Exception ex)
                {
                    LogTelemetry($"UpdateSensorsInternal error: {ex.Message}");
                }
            }
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

        private bool ShouldIncludeSensor(ISensor sensor, string category, out string displayName, out string targetCategory)
        {
            displayName = sensor.Name;
            targetCategory = category;

            if (category == "GPU")
            {
                if (sensor.SensorType == SensorType.Temperature)
                {
                    if (sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("GPU", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("GPU Temperature", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = "GPU Sıcaklığı";
                        return true;
                    }
                    if (sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = "GPU Hot Spot Sıcaklığı";
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
                        if (_sensors.Values.Any(s => s.Category == "GPU" && s.SensorType == SensorType.Power && s.Id != sensor.Identifier.ToString()))
                            return false;

                        displayName = "GPU Güç Tüketimi (Watt)";
                        return true;
                    }
                }
                else if (sensor.SensorType == SensorType.Clock)
                {
                    if (sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = "GPU Çekirdek Hızı (MHz)";
                        return true;
                    }
                    if (sensor.Name.Equals("GPU Memory", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = "GPU Bellek Hızı (MHz)";
                        return true;
                    }
                }
                else if (sensor.SensorType == SensorType.Load)
                {
                    if (sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("GPU", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = "GPU Kullanımı (%)";
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
                    if (sensor.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("Package", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = "CPU Paket Sıcaklığı";
                        return true;
                    }
                    if (sensor.Name.Equals("Core (Tctl/Tdie)", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = "CPU Paket Sıcaklığı";
                        return true;
                    }
                    if (sensor.Name.Equals("CPU Core", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("Core Average", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_sensors.Values.Any(s => s.Category == "CPU" && s.SensorType == SensorType.Temperature))
                        {
                            displayName = "CPU Paket Sıcaklığı";
                            return true;
                        }
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
                        var existingPower = _sensors.Values.FirstOrDefault(s => s.Category == "CPU" && s.SensorType == SensorType.Power && s.Id != sensor.Identifier.ToString());
                        if (existingPower != null)
                        {
                            if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) && !existingPower.HardwareName.Contains("Package", StringComparison.OrdinalIgnoreCase))
                            {
                                _sensors.Remove(existingPower.Id);
                            }
                            else
                            {
                                return false;
                            }
                        }

                        displayName = "CPU Güç Tüketimi (Watt)";
                        return true;
                    }
                }
                else if (sensor.SensorType == SensorType.Clock)
                {
                    if (sensor.Name.Equals("Core Max", StringComparison.OrdinalIgnoreCase))
                    {
                        var existingClock = _sensors.Values.FirstOrDefault(s => s.Category == "CPU" && s.SensorType == SensorType.Clock && s.Id != sensor.Identifier.ToString());
                        if (existingClock != null)
                        {
                            _sensors.Remove(existingClock.Id);
                        }

                        displayName = "CPU Saat Hızı (MHz)";
                        return true;
                    }

                    if (sensor.Name.Equals("P-Core #1", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("CPU Core #1", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("Core #1", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("Core Average", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_sensors.Values.Any(s => s.Category == "CPU" && s.SensorType == SensorType.Clock && s.Id != sensor.Identifier.ToString()))
                            return false;

                        displayName = "CPU Saat Hızı (MHz)";
                        return true;
                    }
                }
                else if (sensor.SensorType == SensorType.Load)
                {
                    if (sensor.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("Total", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = "CPU Toplam Kullanım (%)";
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
                        // Check if CPU hardware already provided a temperature
                        if (!_sensors.Values.Any(s => s.Category == "CPU" && s.SensorType == SensorType.Temperature))
                        {
                            targetCategory = "CPU";
                            displayName = "CPU Paket Sıcaklığı";
                            return true;
                        }
                    }

                    displayName = sensor.Name switch
                    {
                        "System" => "Sistem Sıcaklığı",
                        "Chipset" => "Yonga Seti (Chipset)",
                        "VRM MOS" => "VRM MOS Sıcaklığı",
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
                    displayName = "Bellek Kullanımı (%)";
                    return true;
                }
                if (sensor.SensorType == SensorType.Data && sensor.Name.Equals("Memory Used", StringComparison.OrdinalIgnoreCase))
                {
                    displayName = "Kullanılan Bellek";
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
            lock (_lock)
            {
                var all = _sensors.Values.ToList();
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

                var snapshot = new TelemetrySnapshot
                {
                    Favorites = favs,
                    GraphSensors = graphs,
                    Categories = cats,
                    AllSensors = all
                };

                TelemetryUpdated?.Invoke(this, snapshot);
            }
        }

        public void Dispose()
        {
            Stop();
            try
            {
                _computer?.Close();
                _computer = null;
            }
            catch { }
        }
    }
}
