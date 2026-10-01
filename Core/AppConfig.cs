using System.Collections.Generic;

namespace Warden
{
    public class AppConfig
    {
        public int CheckIntervalMilliseconds { get; set; } = 1000;
        public string DefaultPresetId { get; set; } = "";
        public Dictionary<string, string> Rules { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public string Language { get; set; } = "TR";
        public bool StartWithWindows { get; set; } = false;
        public List<string> DiscoveredGames { get; set; } = new();
        public Dictionary<string, string> DiscoveredGameNames { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

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
        public string ThrottleStopPath { get; set; } = @"D:\ThrottleStop_9.7\ThrottleStop.exe";

        // UI Settings
        public double WindowWidth { get; set; } = 780;
        public double WindowHeight { get; set; } = 560;
    }
}
