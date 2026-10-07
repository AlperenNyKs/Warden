using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Warden
{
    public enum LastPlayedKind { Known, Never, Unknown }

    /// <summary>Kurulu bir oyunun disk alanı ve en son oynanma bilgisi.</summary>
    public sealed record InstalledGameUsage(string ExeName, string Name, string Drive, long? SizeBytes,
                                            LastPlayedKind LastPlayedKind, DateTime? LastPlayedUtc, bool Unused);

    /// <summary>Oyun kurulu bir sürücünün boş alanı.</summary>
    public sealed record DriveSpace(string Drive, long FreeBytes, long TotalBytes)
    {
        public double FreeRatio => TotalBytes > 0 ? (double)FreeBytes / TotalBytes : 1;
        public bool IsLow => FreeRatio < DiskUsage.LowSpaceRatio;
    }

    public static class DiskUsage
    {
        // Bu kadar süredir oynanmayan (veya Steam'e göre hiç açılmamış) oyun "yer kaplıyor" diye işaretlenir
        public static readonly TimeSpan UnusedAfter = TimeSpan.FromDays(60);

        // Oyun kurulu bir sürücüde boş alan bu oranın altına düşerse uyarılır
        public const double LowSpaceRatio = 0.10;

        /// <param name="folderSizes">Mağazanın boyut bilgisi olmayan oyunlar için klasörden hesaplanmış boyutlar (kurulum yolu → bayt).</param>
        public static List<InstalledGameUsage> Build(IEnumerable<DiscoveredGame> games, IEnumerable<GameSession> history,
                                                     IReadOnlyDictionary<string, long> folderSizes, DateTime nowUtc)
        {
            // Warden geçmişindeki en son oturumun bitişi
            var lastSession = history
                .GroupBy(s => s.Game, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(s => s.StartUtc + s.Duration), StringComparer.OrdinalIgnoreCase);

            return games
                .Where(g => !string.IsNullOrEmpty(g.InstallPath))
                .Select(g =>
                {
                    DateTime? fromHistory = lastSession.TryGetValue(g.ExeName, out var h) ? h : null;
                    DateTime? last = Latest(g.LastPlayedUtc, fromHistory);

                    // Steam "hiç oynanmadı" diyebilir; diğer mağazalarda kayıt yoksa bu yalnızca "bilinmiyor" demektir
                    var kind = last != null ? LastPlayedKind.Known
                             : g.Source == "Steam" ? LastPlayedKind.Never
                             : LastPlayedKind.Unknown;
                    bool unused = kind == LastPlayedKind.Never ||
                                  (kind == LastPlayedKind.Known && nowUtc - last!.Value >= UnusedAfter);

                    long? size = g.SizeBytes ?? (folderSizes.TryGetValue(g.InstallPath, out long s) ? s : null);
                    return new InstalledGameUsage(g.ExeName, g.GameName, DriveOf(g.InstallPath), size, kind, last, unused);
                })
                .OrderByDescending(u => u.SizeBytes ?? -1)
                .ToList();
        }

        public static string DriveOf(string path)
        {
            try { return Path.GetPathRoot(path)?.ToUpperInvariant() ?? ""; }
            catch { return ""; }
        }

        /// <summary>Oyunların kurulu olduğu sürücülerin boş alanı.</summary>
        public static List<DriveSpace> Drives(IEnumerable<string> installPaths)
        {
            var result = new List<DriveSpace>();
            foreach (var root in installPaths.Select(DriveOf).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var d = new DriveInfo(root);
                    if (d.IsReady) result.Add(new DriveSpace(root, d.AvailableFreeSpace, d.TotalSize));
                }
                catch { }
            }
            return result.OrderBy(d => d.Drive).ToList();
        }

        /// <summary>Klasördeki dosyaların toplam boyutu (erişilemeyenler atlanır). Büyük oyunlarda birkaç saniye sürebilir.</summary>
        public static long FolderSize(string path)
        {
            try
            {
                // Gizli/sistem dosyaları da yer kaplar → sayılır; bağlantılar (junction/symlink) döngüye sokmasın diye atlanır
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                return new DirectoryInfo(path).EnumerateFiles("*", options).Sum(f => f.Length);
            }
            catch { return 0; }
        }

        private static DateTime? Latest(DateTime? a, DateTime? b)
            => a == null ? b : b == null ? a : (a > b ? a : b);
    }
}
