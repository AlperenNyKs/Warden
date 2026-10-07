using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Warden
{
    /// <summary>Geçmişe yazılan tek bir oyun oturumu.</summary>
    public sealed class GameSession
    {
        public string Game { get; set; } = "";   // kural anahtarı (exe adı)
        public string Name { get; set; } = "";   // o anki oyun adı; oyun sonradan kaldırılsa da geçmişte okunur kalsın
        public DateTime StartUtc { get; set; }
        public double DurationSeconds { get; set; }
        public float? AvgCpuTemp { get; set; }
        public float? MaxCpuTemp { get; set; }
        public float? AvgGpuTemp { get; set; }
        public float? MaxGpuTemp { get; set; }

        public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);
        public string DisplayName => string.IsNullOrEmpty(Name) ? Path.GetFileNameWithoutExtension(Game) : Name;
    }

    /// <summary>Bir oyunun tüm oturumlarının özeti.</summary>
    public sealed record GameStats(string Game, string Name, TimeSpan TotalTime, int Sessions, DateTime LastPlayedUtc,
                                   float? AvgCpuTemp, float? MaxCpuTemp, float? AvgGpuTemp, float? MaxGpuTemp);

    /// <summary>
    /// Oyun oturumlarını %AppData%\Warden\history.json dosyasında tutar. Watcher thread'inden yazılır,
    /// UI thread'inden okunur → kilitli.
    /// </summary>
    public sealed class GameHistory
    {
        public const int MaxSessions = 1000;   // dosya sınırsız büyümesin; en eski oturumlar düşer

        private readonly string _path;
        private readonly object _lock = new();
        private readonly List<GameSession> _sessions;

        public GameHistory(string path)
        {
            _path = path;
            _sessions = Load(path);
        }

        public IReadOnlyList<GameSession> Sessions
        {
            get { lock (_lock) return _sessions.ToList(); }
        }

        public void Add(GameSession session)
        {
            lock (_lock)
            {
                _sessions.Add(session);
                if (_sessions.Count > MaxSessions)
                    _sessions.RemoveRange(0, _sessions.Count - MaxSessions);
                Save();
            }
        }

        /// <summary>Oyun başına toplam süre, oturum sayısı ve süreye göre ağırlıklı ortalama sıcaklık; en çok oynanan önce.</summary>
        public static List<GameStats> Aggregate(IEnumerable<GameSession> sessions)
        {
            return sessions
                .GroupBy(s => s.Game, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var latest = g.OrderBy(s => s.StartUtc).Last();
                    return new GameStats(
                        g.Key,
                        latest.DisplayName,
                        TimeSpan.FromSeconds(g.Sum(s => s.DurationSeconds)),
                        g.Count(),
                        latest.StartUtc,
                        WeightedAverage(g, s => s.AvgCpuTemp),
                        g.Max(s => s.MaxCpuTemp),
                        WeightedAverage(g, s => s.AvgGpuTemp),
                        g.Max(s => s.MaxGpuTemp));
                })
                .OrderByDescending(s => s.TotalTime)
                .ToList();
        }

        // Uzun oturumun ortalaması kısa olandan daha çok sayılır; sıcaklığı okunamayan oturumlar atlanır
        private static float? WeightedAverage(IEnumerable<GameSession> sessions, Func<GameSession, float?> value)
        {
            double sum = 0, weight = 0;
            foreach (var s in sessions)
            {
                if (value(s) is not float v) continue;
                sum += v * s.DurationSeconds;
                weight += s.DurationSeconds;
            }
            return weight > 0 ? (float)(sum / weight) : null;
        }

        private static List<GameSession> Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new List<GameSession>();
                return JsonSerializer.Deserialize<List<GameSession>>(File.ReadAllText(path)) ?? new List<GameSession>();
            }
            catch
            {
                // Bozuk dosya uygulamayı durdurmasın; yenisi ilk oturumda yazılır (eskisi .bak olarak saklanır)
                try { File.Copy(path, path + ".bak", overwrite: true); } catch { }
                return new List<GameSession>();
            }
        }

        // Atomik yazma: yarıda kalan yazma dosyayı bozmasın
        private void Save()
        {
            try
            {
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_sessions));
                File.Move(tmp, _path, overwrite: true);
            }
            catch { }
        }
    }
}
