using System.Collections.Generic;

namespace Warden
{
    public static class Loc
    {
        public static string CurrentLang { get; set; } = "TR";

        private static readonly Dictionary<string, Dictionary<string, string>> Strings = new()
        {
            {
                "EN", new Dictionary<string, string>
                {
                    // General / Shell
                    { "Title",                  "Warden" },
                    { "Online",                 "ONLINE" },
                    { "Settings",               "Settings" },
                    { "SettingsHeader",         "Settings" },
                    { "SettingsDesc",           "Configure application behavior and preferences." },
                    { "GeneralSection",         "GENERAL SETTINGS" },
                    { "StartWithWin",           "Start with Windows" },
                    { "StartWithWinDesc",       "Automatically launches minimized in system tray on system boot." },
                    { "StartupInsecureTitle",   "Start with Windows disabled" },
                    { "PawnIoMissing",          "PawnIO driver is not installed, so CPU temperature, clock and power sensors may stay empty. Install it with \"winget install namazso.PawnIO\" or from" },
                    { "StartupInsecureBody",    "Warden starts at logon with administrator rights, but its folder can be modified by standard users:\n\n{0}\n\nAny program running as you could replace Warden.exe or one of its DLLs and gain administrator rights at the next logon. \"Start with Windows\" has been turned off.\n\nTo enable it, publish Warden into a protected folder (e.g. C:\\Program Files\\Warden) from an elevated terminal:\ndotnet publish -c Release -o \"C:\\Program Files\\Warden\"\n\nDetail: {1}" },
                    { "Language",               "Language / Dil" },
                    { "ScanInterval",           "Scan Interval (ms)" },
                    { "ScanIntervalDesc",       "How frequently to check the active game/window." },
                    { "BtnSave",                "Save" },
                    { "BtnSaveSettings",        "Save Settings" },
                    { "SaveSuccess",            "Settings saved successfully!" },
                    { "SavedSuccess",           "Saved ✔" },
                    { "Connecting",             "Connecting..." },
                    { "Connected",              "Connected" },
                    { "ConnFailed",             "Connection Failed" },
                    { "Active",                 "Active" },
                    { "Desktop",                "Desktop" },
                    { "Info",                   "Info" },
                    { "Warning",                "Warning" },
                    { "Error",                  "Error" },

                    // Window Chrome Tooltips
                    { "ToolTipMinimize",        "Minimize" },
                    { "ToolTipMaximize",        "Maximize" },
                    { "ToolTipClose",           "Close (Runs in background tray)" },

                    // Navigation Tooltips
                    { "NavHome",                "Sonar Profiles" },
                    { "NavDeviceManager",       "Audio Device Manager" },
                    { "NavGpuMonitor",          "GPU Monitor" },
                    { "NavTelemetry",           "Hardware Monitor" },
                    { "NavSettings",            "Settings" },

                    // Page 1: Sonar Profiles & Rules
                    { "RulesHeader",            "Auto Switch Profiles" },
                    { "RulesDesc",              "Automatically switches SteelSeries Sonar profiles based on active games and apps." },
                    { "DefaultEQ",              "Default Profile:" },
                    { "DefaultEQDesc",          "Used when no custom rule matches or while on the desktop." },
                    { "PerAppConfig",           "APPLICATION / GAME" },
                    { "TargetSonarConfig",      "TARGET SONAR PROFILE" },
                    { "BtnScan",                "Scan Installed Games" },
                    { "BtnClear",               "Reset" },
                    { "ClearScannedGames",      "Clear Scanned Games" },
                    { "ClearScannedGamesDesc",  "Resets the list of scanned background games." },
                    { "ClearedSuccess",         "Scanned games list has been reset." },
                    { "ClearedSuccessTitle",    "Reset Complete" },
                    { "ManualAdd",              "Add Manually:" },
                    { "ManualAddDesc",          "If a game was not found in scan, enter its executable name manually." },
                    { "ManualExe",              "Executable Name" },
                    { "ManualName",             "Display Name (Optional)" },
                    { "ManualBrowse",           "Browse" },
                    { "BtnAddManual",           "Add" },
                    { "BtnAddRule",             "Add Game" },
                    { "BtnDeleteRule",          "Remove" },
                    { "ManualSuccess",          "Game added successfully!" },
                    { "ScanDone",               "{0} new games found" },
                    { "ScanNone",               "No new games found" },
                    { "WarningSelectProcess",   "Please select or enter an executable name." },
                    { "WarningSelectPreset",    "Please select a target EQ preset." },

                    // Page 2: Audio Device Manager
                    { "DeviceManagerHeader",    "Audio Device Manager" },
                    { "DeviceManagerDesc",      "Checked audio devices will be automatically hidden and disabled in the background." },
                    { "DeviceDelayTitle",       "Initial Delay (Seconds):" },
                    { "DeviceDelayDesc",        "Wait time after system startup before enforcing disabled devices." },
                    { "BtnSaveAndApply",        "Apply and Save" },

                    // Page 3: GPU Monitor & Auto Profile
                    { "GpuMonitorHeader",       "GPU Monitor & Auto Profile" },
                    { "GpuMonitorDesc",         "Monitors NVIDIA GPU clock frequency and triggers Afterburner profile when limit is exceeded." },
                    { "GpuCoreClock",           "CORE CLOCK" },
                    { "GpuTemperature",         "TEMPERATURE" },
                    { "GpuUsage",               "USAGE" },
                    { "GpuSettingsHeader",      "MSI AFTERBURNER TRIGGER" },
                    { "GpuLimitTitle",          "Maximum Clock Limit (MHz)" },
                    { "GpuLimitDesc",           "If core clock exceeds this threshold, the target profile is automatically applied." },
                    { "GpuProfileTitle",        "Target Profile Number" },
                    { "GpuProfileDesc",         "MSI Afterburner profile slot to apply (1-5)" },
                    { "GpuCooldownTitle",       "Cooldown (Seconds)" },
                    { "GpuCooldownDesc",        "Minimum cooldown period before re-applying the profile." },

                    // Page 4: Telemetry
                    { "TelemetryHeader",        "Hardware Telemetry" },
                    { "TelemetryDesc",          "Real-time temperatures, clock speeds, wattage and utilization metrics." },
                    { "FavoritesTitle",         "FAVORITES / PINNED METRICS" },
                    { "LiveGraphTitle",         "LIVE SENSOR GRAPH (LAST 60 SECONDS)" },
                    { "NoGraphSensorsHint",     "Click 📈 next to any sensor to plot it on the live graph." },
                    { "GraphNow",               "Now" },
                    { "CategoryCpu",            "Processor (CPU)" },
                    { "CategoryGpu",            "Graphics Card (GPU)" },
                    { "CategoryFans",           "Fans & Cooling" },
                    { "CategoryMotherboard",    "Motherboard & System" },
                    { "CategoryMemory",         "Memory (RAM)" },
                    { "PinToFavorites",         "Pin to Favorites" },
                    { "UnpinFromFavorites",     "Unpin from Favorites" },
                    { "PlotOnGraph",            "Plot on Graph" },
                    { "RemoveFromGraph",        "Remove from Graph" },

                    // Tray Context Menu
                    { "TrayOpenSettings",       "Open Dashboard" },
                    { "TrayReloadConfig",       "Reload Configuration" },
                    { "TrayDiscoverPresets",    "Discover Presets" },
                    { "TrayExit",               "Exit" },

                    // Notifications & telemetry sensor names
                    { "Scanning", "Scanning..." },
                    { "ProfileN", "Profile {0}" },
                    { "SecondsN", "{0} Seconds" },
                    { "PresetSwitched", "Preset switched: {0} → {1}" },
                    { "GpuProfileApplied", "Core clock reached {0} MHz. Profile {1} applied." },
                    { "ConnLost", "SteelSeries GG connection lost. Trying to reconnect..." },
                    { "ConnRestored", "SteelSeries GG connection restored." },
                    { "ConfigCorrupt", "config.json could not be read. A backup was saved and default settings were loaded." },
                    { "SensorGpuTemp", "GPU Temperature" },
                    { "SensorGpuHotSpot", "GPU Hot Spot Temperature" },
                    { "SensorGpuPower", "GPU Power (Watt)" },
                    { "SensorGpuCoreClock", "GPU Core Clock (MHz)" },
                    { "SensorGpuMemClock", "GPU Memory Clock (MHz)" },
                    { "SensorGpuLoad", "GPU Usage (%)" },
                    { "SensorCpuTemp", "CPU Package Temperature" },
                    { "SensorCpuPower", "CPU Power (Watt)" },
                    { "SensorCpuClock", "CPU Clock (MHz)" },
                    { "SensorCpuLoad", "CPU Total Usage (%)" },
                    { "SensorSystemTemp", "System Temperature" },
                    { "SensorChipset", "Chipset" },
                    { "SensorVrm", "VRM MOS Temperature" },
                    { "SensorMemLoad", "Memory Usage (%)" },
                    { "SensorMemUsed", "Used Memory" }
                }
            },
            {
                "TR", new Dictionary<string, string>
                {
                    // General / Shell
                    { "Title",                  "Warden" },
                    { "Online",                 "ÇEVRİMİÇİ" },
                    { "Settings",               "Ayarlar" },
                    { "SettingsHeader",         "Ayarlar" },
                    { "SettingsDesc",           "Uygulama davranışını ve tercihlerini yapılandırın." },
                    { "GeneralSection",         "GENEL AYARLAR" },
                    { "StartWithWin",           "Windows ile Başlat" },
                    { "StartWithWinDesc",       "Bilgisayar açıldığında arka planda sistem tepsisinde sessizce başlar." },
                    { "StartupInsecureTitle",   "Windows ile başlatma kapatıldı" },
                    { "PawnIoMissing",          "PawnIO sürücüsü kurulu değil; CPU sıcaklık, saat ve güç sensörleri boş kalabilir. \"winget install namazso.PawnIO\" ile ya da şuradan kurabilirsin:" },
                    { "StartupInsecureBody",    "Warden oturum açılışında yönetici yetkisiyle başlıyor, ancak klasörü standart kullanıcılar tarafından değiştirilebiliyor:\n\n{0}\n\nSenin yetkinle çalışan herhangi bir program Warden.exe'yi veya bir DLL'ini değiştirip sonraki açılışta yönetici yetkisi kazanabilir. Bu yüzden \"Windows ile başlat\" kapatıldı.\n\nAçmak için Warden'ı korumalı bir klasöre (ör. C:\\Program Files\\Warden) yönetici terminalinden yayınla:\ndotnet publish -c Release -o \"C:\\Program Files\\Warden\"\n\nAyrıntı: {1}" },
                    { "Language",               "Dil / Language" },
                    { "ScanInterval",           "Tarama Aralığı (ms)" },
                    { "ScanIntervalDesc",       "Aktif oyun veya pencerenin ne sıklıkla kontrol edileceği." },
                    { "BtnSave",                "Kaydet" },
                    { "BtnSaveSettings",        "Ayarları Kaydet" },
                    { "SaveSuccess",            "Ayarlar başarıyla kaydedildi!" },
                    { "SavedSuccess",           "Kaydedildi ✔" },
                    { "Connecting",             "Bağlanıyor..." },
                    { "Connected",              "Bağlandı" },
                    { "ConnFailed",             "Bağlantı Başarısız" },
                    { "Active",                 "Aktif" },
                    { "Desktop",                "Masaüstü" },
                    { "Info",                   "Bilgi" },
                    { "Warning",                "Uyarı" },
                    { "Error",                  "Hata" },

                    // Window Chrome Tooltips
                    { "ToolTipMinimize",        "Simge Durumuna Küçült" },
                    { "ToolTipMaximize",        "Ekranı Kapla" },
                    { "ToolTipClose",           "Kapat (Arka planda sistem tepsisinde çalışır)" },

                    // Navigation Tooltips
                    { "NavHome",                "Sonar Profilleri" },
                    { "NavDeviceManager",       "Ses Aygıtı Yöneticisi" },
                    { "NavGpuMonitor",          "GPU Performans & Monitör" },
                    { "NavTelemetry",           "Donanım Sensörleri & Telemetri" },
                    { "NavSettings",            "Ayarlar" },

                    // Page 1: Sonar Profiles & Rules
                    { "RulesHeader",            "Otomatik Geçiş Profilleri" },
                    { "RulesDesc",              "Aktif oyun ve uygulamalara göre SteelSeries Sonar profillerini otomatik değiştirir." },
                    { "DefaultEQ",              "Varsayılan Profil:" },
                    { "DefaultEQDesc",          "Hiçbir kural eşleşmediğinde veya masaüstündeyken kullanılır." },
                    { "PerAppConfig",           "UYGULAMA / OYUN" },
                    { "TargetSonarConfig",      "HEDEF SONAR PROFİLİ" },
                    { "BtnScan",                "Yüklü Oyunları Tara" },
                    { "BtnClear",               "Sıfırla" },
                    { "ClearScannedGames",      "Taranmış Oyunları Temizle" },
                    { "ClearScannedGamesDesc",  "Arka planda keşfedilen tüm exe isimlerini sıfırlar." },
                    { "ClearedSuccess",         "Taranmış oyun listesi başarıyla sıfırlandı." },
                    { "ClearedSuccessTitle",    "Sıfırlandı" },
                    { "ManualAdd",              "Manuel Ekle:" },
                    { "ManualAddDesc",          "Eğer taramada bulunamadıysa oyunun EXE adını elle ekleyin." },
                    { "ManualExe",              "EXE Adı" },
                    { "ManualName",             "Görünür Ad (İsteğe Bağlı)" },
                    { "ManualBrowse",           "Gözat" },
                    { "BtnAddManual",           "Ekle" },
                    { "BtnAddRule",             "Oyun Ekle" },
                    { "BtnDeleteRule",          "Kaldır" },
                    { "ManualSuccess",          "Oyun başarıyla eklendi!" },
                    { "ScanDone",               "{0} yeni oyun bulundu" },
                    { "ScanNone",               "Yeni oyun bulunamadı" },
                    { "WarningSelectProcess",   "Lütfen bir işlem adı seçin veya yazın." },
                    { "WarningSelectPreset",    "Lütfen bir hedef EQ profili seçin." },

                    // Page 2: Audio Device Manager
                    { "DeviceManagerHeader",    "Ses Cihazı Yöneticisi" },
                    { "DeviceManagerDesc",      "İşaretlenen ses cihazları arka planda otomatik olarak gizlenir ve devre dışı bırakılır." },
                    { "DeviceDelayTitle",       "İlk Gecikme (Saniye):" },
                    { "DeviceDelayDesc",        "Sistem açıldıktan sonra cihazların devre dışı bırakılması için bekleme süresi." },
                    { "BtnSaveAndApply",        "Uygula ve Kaydet" },

                    // Page 3: GPU Monitor & Auto Profile
                    { "GpuMonitorHeader",       "GPU Monitör & Otomatik Profil" },
                    { "GpuMonitorDesc",         "NVIDIA GPU frekansını izler ve belirlenen limit aşıldığında Afterburner profilini tetikler." },
                    { "GpuCoreClock",           "ÇEKİRDEK HIZI" },
                    { "GpuTemperature",         "SICAKLIK" },
                    { "GpuUsage",               "KULLANIM" },
                    { "GpuSettingsHeader",      "MSI AFTERBURNER TETİKLEYİCİ" },
                    { "GpuLimitTitle",          "Maksimum Saat Hızı Sınırı (MHz)" },
                    { "GpuLimitDesc",           "Çekirdek hızı bu sınırı aşarsa hedef profil otomatik olarak uygulanır." },
                    { "GpuProfileTitle",        "Hedef Profil Numarası" },
                    { "GpuProfileDesc",         "Uygulanacak MSI Afterburner profil slotu (1-5)" },
                    { "GpuCooldownTitle",       "Bekleme Süresi (Saniye)" },
                    { "GpuCooldownDesc",        "Profil tekrar uygulanmadan önceki bekleme süresi." },

                    // Page 4: Telemetry
                    { "TelemetryHeader",        "Sistem Telemetrisi" },
                    { "TelemetryDesc",          "Canlı sıcaklıklar, saat hızları, güç tüketimleri ve kullanım oranları." },
                    { "FavoritesTitle",         "FAVORİLER / HIZLI BAKIŞ" },
                    { "LiveGraphTitle",         "CANLI SENSÖR GRAFİĞİ (SON 60 SANİYE)" },
                    { "NoGraphSensorsHint",     "Grafikte çizilmesini istediğiniz değerlerin yanındaki 📈 butonuna tıklayın." },
                    { "GraphNow",               "Şimdi" },
                    { "CategoryCpu",            "İşlemci (CPU)" },
                    { "CategoryGpu",            "Ekran Kartı (GPU)" },
                    { "CategoryFans",           "Fanlar & Soğutma" },
                    { "CategoryMotherboard",    "Anakart & Sistem" },
                    { "CategoryMemory",         "Bellek (RAM)" },
                    { "PinToFavorites",         "Favorilere Ekle" },
                    { "UnpinFromFavorites",     "Favorilerden Kaldır" },
                    { "PlotOnGraph",            "Grafiğe Ekle" },
                    { "RemoveFromGraph",        "Grafikten Kaldır" },

                    // Tray Context Menu
                    { "TrayOpenSettings",       "Kontrol Panelini Aç" },
                    { "TrayReloadConfig",       "Yapılandırmayı Yenile" },
                    { "TrayDiscoverPresets",    "Profilleri Keşfet" },
                    { "TrayExit",               "Çıkış" },

                    // Notifications & telemetry sensor names
                    { "Scanning", "Taranıyor..." },
                    { "ProfileN", "Profil {0}" },
                    { "SecondsN", "{0} Saniye" },
                    { "PresetSwitched", "Profil değişti: {0} → {1}" },
                    { "GpuProfileApplied", "Çekirdek hızı {0} MHz'e ulaştı. Profil {1} uygulandı." },
                    { "ConnLost", "SteelSeries GG bağlantısı kesildi. Yeniden bağlanmaya çalışıyor..." },
                    { "ConnRestored", "SteelSeries GG bağlantısı yeniden kuruldu." },
                    { "ConfigCorrupt", "config.json okunamadı. Yedeği alındı ve varsayılan ayarlar yüklendi." },
                    { "SensorGpuTemp", "GPU Sıcaklığı" },
                    { "SensorGpuHotSpot", "GPU Hot Spot Sıcaklığı" },
                    { "SensorGpuPower", "GPU Güç Tüketimi (Watt)" },
                    { "SensorGpuCoreClock", "GPU Çekirdek Hızı (MHz)" },
                    { "SensorGpuMemClock", "GPU Bellek Hızı (MHz)" },
                    { "SensorGpuLoad", "GPU Kullanımı (%)" },
                    { "SensorCpuTemp", "CPU Paket Sıcaklığı" },
                    { "SensorCpuPower", "CPU Güç Tüketimi (Watt)" },
                    { "SensorCpuClock", "CPU Saat Hızı (MHz)" },
                    { "SensorCpuLoad", "CPU Toplam Kullanım (%)" },
                    { "SensorSystemTemp", "Sistem Sıcaklığı" },
                    { "SensorChipset", "Yonga Seti (Chipset)" },
                    { "SensorVrm", "VRM MOS Sıcaklığı" },
                    { "SensorMemLoad", "Bellek Kullanımı (%)" },
                    { "SensorMemUsed", "Kullanılan Bellek" }
                }
            }
        };

        /// <summary>Yerelleştirilmiş metni string.Format ile doldurur.</summary>
        public static string Format(string key, params object[] args)
            => string.Format(Get(key), args);

        public static string Get(string key)
        {
            if (Strings.TryGetValue(CurrentLang, out var langDict) &&
                langDict.TryGetValue(key, out var val))
                return val;

            if (Strings["EN"].TryGetValue(key, out var fallback))
                return fallback;

            return key;
        }
    }
}
