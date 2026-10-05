using System;
using System.Collections.Generic;
using System.Linq;

namespace Warden
{
    public class AppConfig
    {
        public const int MinCheckIntervalMs = 250;
        public const int MaxCheckIntervalMs = 60000;

        // Modüller: kapalı modül yalnızca gizlenmez, arka planda da hiç çalışmaz.
        // Eski config'lerde alan yok → varsayılan açık (güncelleme davranışı değiştirmez).
        public bool ModuleSonar { get; set; } = true;        // GG/Sonar profil geçişi
        public bool ModuleAudioDevices { get; set; } = true; // ses cihazı denetleyicisi
        public bool ModuleHardware { get; set; } = true;     // sensörler, sıcaklık alarmı, CSV kaydı
        public bool ModuleGpuProfile { get; set; } = true;   // Afterburner profil geçişi (ModuleHardware gerekir)

        /// <summary>GPU profili sensörlerden saat hızını okur; donanım izleme kapalıysa çalışamaz.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool GpuProfileActive =>ModuleGpuProfile && ModuleHardware;

        public int CheckIntervalMilliseconds { get; set; } = 1000;
        public string DefaultPresetId { get; set; } = "";
        public Dictionary<string, string> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string Language { get; set; } = "TR";
        public bool StartWithWindows { get; set; } = false;
        public bool AutoCheckUpdates { get; set; } = true;
        public List<string> DiscoveredGames { get; set; } = new();
        public Dictionary<string, string> DiscoveredGameNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Oyun tarayıcısının bulduğu exe'ler (manuel eklenenler burada olmaz). null = alan eklenmeden önceki config.
        public List<string>? ScannedGames { get; set; }

        // Açılışta yüklü oyunları tara; bulunan oyunlara GG'deki oyun profilini otomatik ata
        public bool AutoScanGames { get; set; } = true;
        public bool AutoAssignGgPresets { get; set; } = true;

        // GG profili otomatik atanmış exe'ler: kullanıcı bu kuralı silerse bir daha atanmaz
        public List<string> GgAutoAssigned { get; set; } = new();

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

        // Sıcaklık alarmı (°C)
        public const int MinTempLimit = 50;
        public const int MaxTempLimit = 110;
        public bool TempAlarmEnabled { get; set; } = true;
        public int CpuTempLimit { get; set; } = 90;
        public int GpuTempLimit { get; set; } = 85;

        // Oyun kuralı aktifken telemetriyi CSV'ye otomatik kaydet
        public bool AutoRecordGameSessions { get; set; } = false;

        // İlk açılışta "Sistem Durumu" sayfası gösterildi mi
        public bool FirstRunCompleted { get; set; } = false;

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

            ScannedGames = ScannedGames?.Where(g => !string.IsNullOrWhiteSpace(g))
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .ToList();
            GgAutoAssigned = (GgAutoAssigned ?? new()).Where(g => !string.IsNullOrWhiteSpace(g))
                                                      .Distinct(StringComparer.OrdinalIgnoreCase)
                                                      .ToList();

            DisabledDevices ??= new();
            DisabledDeviceNames ??= new();
            TelemetryFavorites ??= new();
            TelemetryGraphSensors ??= new();

            DefaultPresetId ??= "";
            Language = string.Equals(Language, "EN", StringComparison.OrdinalIgnoreCase) ? "EN" : "TR";

            CheckIntervalMilliseconds = Math.Clamp(CheckIntervalMilliseconds, MinCheckIntervalMs, MaxCheckIntervalMs);
            TargetProfile = Math.Clamp(TargetProfile, 1, 5);
            CooldownSeconds = Math.Max(1, CooldownSeconds);
            DeviceDisableDelaySeconds = Math.Clamp(DeviceDisableDelaySeconds, 0, 300);
            if (double.IsNaN(TargetMhz) || double.IsInfinity(TargetMhz) || TargetMhz < 0) TargetMhz = 0;
            CpuTempLimit = Math.Clamp(CpuTempLimit, MinTempLimit, MaxTempLimit);
            GpuTempLimit = Math.Clamp(GpuTempLimit, MinTempLimit, MaxTempLimit);
        }

        /// <summary>
        /// Daha önce taramada bulunup bu taramada bulunamayan (kaldırılmış) oyunları listeden ve kurallardan siler,
        /// ardından taranan oyun listesini günceller. Manuel eklenen oyunlara dokunulmaz. Silinen exe'leri döndürür.
        /// </summary>
        public List<string> PruneUninstalledGames(IEnumerable<string> foundExes)
        {
            var found = new HashSet<string>(foundExes, StringComparer.OrdinalIgnoreCase);

            // Tarama hiçbir şey bulamadıysa (erişim hatası vb.) her şeyi silmek yerine hiçbir şeyi silme
            if (found.Count == 0) return new List<string>();

            // Eski config'ler kaynağı tutmuyordu: adı dolu kayıtları tarayıcı yazar, manuel/kural satırından
            // eklenenlerin adı boştur.
            var previous = ScannedGames ?? DiscoveredGames
                .Where(e => DiscoveredGameNames.TryGetValue(e, out var n) && !string.IsNullOrEmpty(n))
                .ToList();

            var removed = previous.Where(e => !found.Contains(e)).ToList();
            foreach (var exe in removed)
            {
                DiscoveredGames.RemoveAll(g => g.Equals(exe, StringComparison.OrdinalIgnoreCase));
                DiscoveredGameNames.Remove(exe);
                Rules.Remove(exe);
                // Yeniden kurulursa profil tekrar atanabilsin
                GgAutoAssigned.RemoveAll(g => g.Equals(exe, StringComparison.OrdinalIgnoreCase));
            }

            ScannedGames = found.ToList();
            return removed;
        }
    }
}
