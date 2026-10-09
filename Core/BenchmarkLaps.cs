using System;
using System.Collections.Generic;
using System.Linq;

namespace Warden
{
    /// <summary>Bir kaydın içindeki tur: kayıt başından saniye olarak [Start, End].</summary>
    public sealed class BenchmarkSegment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Label { get; set; } = "";

        /// <summary>Kullanıcı etiketi değiştirdi; otomatik öneri bunun üzerine yazmaz.</summary>
        public bool LabelEdited { get; set; }

        public double Duration => End - Start;
    }

    public enum LapProfileKind
    {
        Locked,       // frekans sabit (ör. Afterburner eğrisinde kilitli)
        PowerLimited, // frekans oynuyor, güç sabit ve yüksek (fabrika / güç sınırında)
        Variable,     // ikisi de değil
        Unknown       // yeterli veri yok
    }

    /// <summary>Tur için gözlenen GPU davranışı; etiket metni arayüzde dile göre oluşturulur.</summary>
    public sealed record LapProfile(LapProfileKind Kind, double? MedianMhz, double? MinMhz, double? MaxMhz, double? AvgWatts);

    public static class BenchmarkLaps
    {
        // GPU yükü bu değerin altındaki saniyeler "boşluk" adayıdır (menü, yükleme ekranı)
        public const double LowLoadPercent = 50;
        // En az bu kadar ardışık düşük saniye turları ayırır; daha kısa düşüşler tur içinde kalır
        public const int MinGapSeconds = 3;
        // Bundan kısa parçalar tur sayılmaz (ör. menüde gezinirken anlık yük)
        public const double MinLapSeconds = 20;

        // Frekansın p5–p95 aralığı bundan darsa "sabit"
        public const double LockedSpreadMhz = 15;
        // Güç kayıtlar boyunca bu kadar az oynuyorsa (değişim katsayısı) "güç sınırında"
        public const double FlatPowerCv = 0.05;

        /// <summary>
        /// GPU yükünün en az MinGapSeconds boyunca LowLoadPercent altına düştüğü yerlerden turlara ayırır. Yükü
        /// okunamayan saniyede FPS de yoksa düşük sayılır. Kısa parçalar atılır. Turlar kayıt sırasıyla döner.
        /// </summary>
        public static List<BenchmarkSegment> AutoSplit(BenchmarkRun run)
        {
            var samples = run.Samples.OrderBy(s => s.T).ToList();
            var laps = new List<BenchmarkSegment>();
            if (samples.Count == 0) return laps;

            bool IsLow(BenchmarkSample s) => s.GpuLoad is float load ? load < LowLoadPercent : s.Fps is not > 0;

            // Kısa düşüşleri yüksek say: yalnızca MinGapSeconds'tan uzun düşük diziler boşluktur
            var low = samples.Select(IsLow).ToArray();
            var gap = new bool[samples.Count];
            for (int i = 0; i < samples.Count;)
            {
                if (!low[i]) { i++; continue; }
                int j = i;
                while (j < samples.Count && low[j]) j++;
                if (j - i >= MinGapSeconds)
                    for (int k = i; k < j; k++) gap[k] = true;
                i = j;
            }

            for (int i = 0; i < samples.Count;)
            {
                if (gap[i]) { i++; continue; }
                int j = i;
                while (j < samples.Count && !gap[j]) j++;
                // Örnek T, (T-1, T] aralığını temsil eder
                var seg = new BenchmarkSegment { Start = Math.Max(0, samples[i].T - 1), End = samples[j - 1].T };
                if (seg.Duration >= MinLapSeconds) laps.Add(seg);
                i = j;
            }
            return laps;
        }

        /// <summary>
        /// Turu, karşılaştırmada ayrı bir kayıt gibi kullanılabilecek bir kayda çevirir: örnekler ve kareler tur
        /// aralığından alınır, zaman tur başından başlar. Kareler zaman damgası taşımadığı için konumları kare sürelerinin
        /// birikimli toplamından bulunur.
        /// </summary>
        public static BenchmarkRun Slice(BenchmarkRun run, BenchmarkSegment seg, int index)
        {
            // Birikimli süre tam sayı mikrosaniye: kayan nokta toplamı sınırdaki kareyi yanlış tura düşürmesin
            var frames = new List<float>();
            long elapsedUs = 0, startUs = (long)Math.Round(seg.Start * 1e6), endUs = (long)Math.Round(seg.End * 1e6);
            foreach (var f in run.FrameTimesMs)
            {
                elapsedUs += (long)Math.Round(f * 1000.0);
                if (elapsedUs > startUs && elapsedUs <= endUs) frames.Add(f);
                else if (elapsedUs > endUs) break;
            }

            return new BenchmarkRun
            {
                Id = $"{run.Id}#{index}",
                Label = seg.Label,
                Game = run.Game,
                StartUtc = run.StartUtc.AddSeconds(seg.Start),
                DurationSeconds = seg.Duration,
                FrameTimesExact = run.FrameTimesExact,
                FrameTimesMs = frames.ToArray(),
                Samples = run.Samples
                    .Where(s => s.T > seg.Start && s.T <= seg.End)
                    .Select(s => Rebase(s, seg.Start))
                    .ToList()
            };
        }

        /// <summary>Karşılaştırmaya giren birimler: turu yoksa kaydın kendisi, varsa her tur.</summary>
        public static List<BenchmarkRun> Expand(BenchmarkRun run)
            => run.Segments is { Count: > 0 } segs ? segs.Select((s, i) => Slice(run, s, i + 1)).ToList() : new List<BenchmarkRun> { run };

        /// <summary>Isınma sonrası GPU frekansı ve gücüne bakarak turun nasıl çalıştığını çıkarır.</summary>
        public static LapProfile Profile(BenchmarkRun lap, double warmupSeconds)
        {
            var s = lap.Samples.Where(x => x.T > warmupSeconds).ToList();
            var clocks = s.Where(x => x.GpuClock is > 0).Select(x => (double)x.GpuClock!.Value).OrderBy(x => x).ToList();
            var watts = s.Where(x => x.GpuPower is > 0).Select(x => (double)x.GpuPower!.Value).ToList();
            if (clocks.Count < 5) return new LapProfile(LapProfileKind.Unknown, null, null, null, watts.Count > 0 ? watts.Average() : null);

            double p5 = BenchmarkStats.Percentile(clocks, 0.05), p95 = BenchmarkStats.Percentile(clocks, 0.95);
            double median = BenchmarkStats.Percentile(clocks, 0.5);
            double? avgW = watts.Count > 0 ? watts.Average() : null;

            LapProfileKind kind;
            if (p95 - p5 <= LockedSpreadMhz) kind = LapProfileKind.Locked;
            else if (watts.Count >= 5 && avgW > 0 && StdDev(watts) / avgW.Value <= FlatPowerCv) kind = LapProfileKind.PowerLimited;
            else kind = LapProfileKind.Variable;

            return new LapProfile(kind, median, p5, p95, avgW);
        }

        private static double StdDev(List<double> v)
        {
            double mean = v.Average();
            return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Count);
        }

        private static BenchmarkSample Rebase(BenchmarkSample s, double offset) => new()
        {
            T = s.T - offset, Fps = s.Fps, FrameTimeAvgMs = s.FrameTimeAvgMs, FrameTimeMaxMs = s.FrameTimeMaxMs,
            CpuTemp = s.CpuTemp, GpuTemp = s.GpuTemp, CpuPower = s.CpuPower, GpuPower = s.GpuPower,
            CpuClock = s.CpuClock, GpuClock = s.GpuClock, CpuLoad = s.CpuLoad, GpuLoad = s.GpuLoad
        };
    }
}

namespace Warden
{
    /// <summary>Tur profilinden okunur etiket ("1575 MHz sabit", "Güç sınırında · 143 W").</summary>
    public static class LapLabels
    {
        public static string Text(LapProfile p) => p.Kind switch
        {
            LapProfileKind.Locked => Loc.Format("LapLocked", Math.Round(p.MedianMhz ?? 0)),
            LapProfileKind.PowerLimited => Loc.Format("LapPowerLimited", Math.Round(p.AvgWatts ?? 0)),
            LapProfileKind.Variable => Loc.Format("LapVariable", Math.Round(p.MinMhz ?? 0), Math.Round(p.MaxMhz ?? 0), Math.Round(p.AvgWatts ?? 0)),
            _ => Loc.Get("LapUnknown")
        };

        /// <summary>Turun önerilen etiketi (ısınma sonrası GPU frekansı ve gücüne göre).</summary>
        public static string Suggest(BenchmarkRun run, BenchmarkSegment seg, double warmupSeconds)
            => Text(BenchmarkLaps.Profile(BenchmarkLaps.Slice(run, seg, 0), warmupSeconds));
    }
}
