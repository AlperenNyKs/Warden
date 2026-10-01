using System;
using System.Collections.Generic;
using System.Linq;

namespace Warden
{
    public class AppConfig
    {
        public const int MinCheckIntervalMs = 250;
        public const int MaxCheckIntervalMs = 60000;

        public int CheckIntervalMilliseconds { get; set; } = 1000;
        public string DefaultPresetId { get; set; } = "";
        public Dictionary<string, string> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string Language { get; set; } = "TR";
        public bool StartWithWindows { get; set; } = false;
        public List<string> DiscoveredGames { get; set; } = new();
        public Dictionary<string, string> DiscoveredGameNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // GPU Monitor
        public double TargetMhz { get; set; } = 1550;
        public int TargetProfile { get; set; } = 1;
        public int CooldownSeconds { get; set; } = 15;

        // Device Enforcer
        public List<string> DisabledDevices { get; set; } = new();
        public List<string> DisabledDeviceNames { get; set; } = new();
        public int DeviceDisableDelaySeconds { get; set; } = 30;

        // Hardware Telemetry
        public List<string> TelemetryFavorites { get; set; } = new();
        public List<string> TelemetryGraphSensors { get; set; } = new();

        // Desktop Widget & ThrottleStop
        public bool DesktopWidgetEnabled { get; set; } = true;
        public double DesktopWidgetLeft { get; set; } = 100;
        public double DesktopWidgetTop { get; set; } = 100;
        public bool DesktopWidgetLocked { get; set; } = false;
        public List<int> DesktopWidgetVisibleProfiles { get; set; } = new() { 0, 1, 2, 3 };
        // Boş = otomatik algıla (ThrottleStopService bilinen konumları tarar). Ayarlar sayfasından değiştirilebilir.
        public string ThrottleStopPath { get; set; } = "";

        // UI Settings (MainWindow.xaml varsayılanlarıyla aynı)
        public double WindowWidth { get; set; } = 940;
        public double WindowHeight { get; set; } = 640;

        /// <summary>
        /// JSON'dan okunan değerleri güvenli hale getirir:
        /// - null koleksiyonları boş koleksiyonla değiştirir (elle düzenlenmiş/eski config'lerde NullReference önler)
        /// - System.Text.Json'ın düşürdüğü büyük/küçük harf duyarsız karşılaştırıcıyı geri ekler
        /// - Aralık dışı sayısal değerleri sınırlar (ör. 0 veya negatif tarama aralığı Timer'ı bozar)
        /// </summary>
        public void Normalize()
        {
            Rules = new Dictionary<string, string>(
                (Rules ?? new()).Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
                                .GroupBy(kv => kv.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(g => g.Key, g => g.Last().Value ?? ""),
                StringComparer.OrdinalIgnoreCase);

            DiscoveredGameNames = new Dictionary<string, string>(
                (DiscoveredGameNames ?? new()).GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                              .ToDictionary(g => g.Key, g => g.Last().Value ?? ""),
                StringComparer.OrdinalIgnoreCase);

            DiscoveredGames = (DiscoveredGames ?? new())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            DisabledDevices ??= new();
            DisabledDeviceNames ??= new();
            TelemetryFavorites ??= new();
            TelemetryGraphSensors ??= new();
            DesktopWidgetVisibleProfiles = (DesktopWidgetVisibleProfiles ?? new())
                .Where(i => i >= 0 && i <= 3).Distinct().OrderBy(i => i).ToList();
            if (DesktopWidgetVisibleProfiles.Count == 0)
                DesktopWidgetVisibleProfiles = new() { 0, 1, 2, 3 };

            DefaultPresetId ??= "";
            ThrottleStopPath = (ThrottleStopPath ?? "").Trim().Trim('"');
            Language = string.Equals(Language, "EN", StringComparison.OrdinalIgnoreCase) ? "EN" : "TR";

            CheckIntervalMilliseconds = Math.Clamp(CheckIntervalMilliseconds, MinCheckIntervalMs, MaxCheckIntervalMs);
            TargetProfile = Math.Clamp(TargetProfile, 1, 5);
            CooldownSeconds = Math.Max(1, CooldownSeconds);
            DeviceDisableDelaySeconds = Math.Clamp(DeviceDisableDelaySeconds, 0, 300);
            if (double.IsNaN(TargetMhz) || double.IsInfinity(TargetMhz) || TargetMhz < 0) TargetMhz = 0;
        }
    }
}
