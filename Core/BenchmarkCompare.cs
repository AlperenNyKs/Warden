using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Warden
{
    /// <summary>
    /// Karşılaştırma tablosundaki bir ölçü. Direction: +1 yüksek iyi, -1 düşük iyi, 0 yalnızca bilgi.
    /// Absolute: fark yüzde yerine birim olarak verilir (sıcaklıkta "+6 °C", yüzdeden anlamlı).
    /// </summary>
    public sealed record CompareMetric(string Key, Func<BenchmarkSummary, EfficiencyBasis, double?> Value, int Direction,
                                       string Format, bool Absolute = false);

    /// <summary>Grafikte çizilebilen saniyelik ölçü.</summary>
    public sealed record ChartMetric(string Key, Func<BenchmarkSample, double?> Value, string Unit);

    /// <summary>Bir grubun temele göre farkı ve bu farkın ne kadar güvenilir olduğu.</summary>
    public enum DiffVerdict
    {
        Better,
        Worse,
        Same,           // fark gürültü eşiğinin altında
        NotSignificant, // iki grupta da 2+ kayıt var ve fark kayıtlar arası dalgalanmadan küçük
        Info            // MHz, yük gibi yalnızca bilgi amaçlı ölçüler
    }

    /// <param name="Diff">Absolute ölçülerde birim farkı, diğerlerinde yüzde.</param>
    /// <param name="SignificanceKnown">İki grupta da en az 2 kayıt var (dalgalanma hesaplanabildi).</param>
    public sealed record Comparison(double? Diff, DiffVerdict Verdict, bool SignificanceKnown);

    /// <summary>Aynı etiketli kayıtlar; değerleri kayıtların ortalamasıdır.</summary>
    public sealed class CompareGroup
    {
        public string Label { get; }
        public IReadOnlyList<BenchmarkRun> Runs { get; }
        public IReadOnlyList<BenchmarkSummary> Summaries { get; }

        public CompareGroup(string label, IReadOnlyList<BenchmarkRun> runs, IReadOnlyList<BenchmarkSummary> summaries)
        {
            Label = label;
            Runs = runs;
            Summaries = summaries;
        }

        public IEnumerable<double> Values(CompareMetric metric, EfficiencyBasis basis)
            => Summaries.Select(s => metric.Value(s, basis)).Where(v => v != null).Select(v => v!.Value);

        public double? Value(CompareMetric metric, EfficiencyBasis basis)
        {
            var values = Values(metric, basis).ToList();
            return values.Count > 0 ? values.Average() : null;
        }

        /// <summary>Kayıtlar arası örneklem standart sapması; 2'den az kayıtta null.</summary>
        public double? Spread(CompareMetric metric, EfficiencyBasis basis)
        {
            var v = Values(metric, basis).ToList();
            if (v.Count < 2) return null;
            double mean = v.Average();
            return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1));
        }

        public double MeasuredSeconds => Summaries.Count > 0 ? Summaries.Average(s => s.MeasuredSeconds) : 0;
    }

    public static class BenchmarkCompare
    {
        /// <summary>Grafik okunaklı kalsın diye en fazla bu kadar grup karşılaştırılır.</summary>
        public const int MaxGroups = 4;

        // Farkın "değişmedi" sayıldığı eşikler: ölçüm gürültüsünü iyi/kötü diye boyamasın
        public const double NeutralPercent = 0.5;
        public const double NeutralAbsolute = 0.5;   // °C

        // Kayıt süreleri bu orandan fazla farklıysa (ör. 2 dk ↔ 5 dk) sıcaklık karşılaştırması adil değildir
        public const double DurationMismatchRatio = 1.25;

        public static readonly CompareMetric[] Metrics =
        {
            new("AvgFps",        (s, _) => s.AvgFps, +1, "0.0"),
            new("Low1Fps",       (s, _) => s.Low1Fps, +1, "0.0"),
            new("FpsPerWatt",    (s, b) => s.FpsPerWatt(b), +1, "0.00"),
            new("GpuTempSteady", (s, _) => s.SteadyGpuTemp, -1, "0.0", Absolute: true),
            new("GpuTemp",       (s, _) => s.AvgGpuTemp, -1, "0.0", Absolute: true),
            new("GpuTempP95",    (s, _) => s.P95GpuTemp, -1, "0.0", Absolute: true),
            new("CpuTempSteady", (s, _) => s.SteadyCpuTemp, -1, "0.0", Absolute: true),
            new("CpuTemp",       (s, _) => s.AvgCpuTemp, -1, "0.0", Absolute: true),
            new("CpuTempP95",    (s, _) => s.P95CpuTemp, -1, "0.0", Absolute: true),
            new("GpuPower",      (s, _) => s.AvgGpuPower, -1, "0.0"),
            new("CpuPower",      (s, _) => s.AvgCpuPower, -1, "0.0"),
            new("TotalPower",    (s, _) => s.AvgTotalPower, -1, "0.0"),
            new("GpuClock",      (s, _) => s.AvgGpuClock, 0, "0"),
            new("GpuClockMin",   (s, _) => s.MinGpuClock, 0, "0"),
            new("GpuClockMax",   (s, _) => s.MaxGpuClock, 0, "0"),
            new("CpuClock",      (s, _) => s.AvgCpuClock, 0, "0"),
            new("CpuClockMin",   (s, _) => s.MinCpuClock, 0, "0"),
            new("CpuClockMax",   (s, _) => s.MaxCpuClock, 0, "0"),
            new("GpuLoad",       (s, _) => s.AvgGpuLoad, 0, "0"),
            new("CpuLoad",       (s, _) => s.AvgCpuLoad, 0, "0"),
        };

        public static CompareMetric Metric(string key) => Metrics.Single(m => m.Key == key);

        public static readonly ChartMetric[] ChartMetrics =
        {
            new("Fps",        s => s.Fps, "FPS"),
            new("GpuTemp",    s => s.GpuTemp, "°C"),
            new("CpuTemp",    s => s.CpuTemp, "°C"),
            new("GpuPower",   s => s.GpuPower, "W"),
            new("CpuPower",   s => s.CpuPower, "W"),
            new("TotalPower", s => s.CpuPower is float c && s.GpuPower is float g ? c + g : null, "W"),
            new("GpuClock",   s => s.GpuClock, "MHz"),
            new("CpuClock",   s => s.CpuClock, "MHz"),
        };

        /// <summary>Etikete göre gruplar (büyük/küçük harf duyarsız); en eski kaydı olan grup önce.</summary>
        public static List<CompareGroup> Group(IEnumerable<BenchmarkRun> runs, double warmupSeconds)
        {
            return runs
                .GroupBy(r => r.Label.Trim(), StringComparer.CurrentCultureIgnoreCase)
                .Select(g =>
                {
                    var ordered = g.OrderBy(r => r.StartUtc).ToList();
                    return new CompareGroup(ordered[0].Label.Trim(), ordered,
                                            ordered.Select(r => BenchmarkStats.Compute(r, warmupSeconds)).ToList());
                })
                .OrderBy(g => g.Runs[0].StartUtc)
                .ToList();
        }

        /// <summary>
        /// Kullanıcının sürükleyerek verdiği sıra (etiketler); sırada olmayan gruplar en sona, eskiden yeniye eklenir.
        /// Temel: seçilen etiket varsa o, yoksa ilk sütun.
        /// </summary>
        public static (List<CompareGroup> Ordered, CompareGroup Baseline) Arrange(
            IReadOnlyList<CompareGroup> groups, IReadOnlyList<string> order, string? baselineLabel)
        {
            var cmp = StringComparer.CurrentCultureIgnoreCase;
            var ordered = order
                .Select(label => groups.FirstOrDefault(g => cmp.Equals(g.Label, label)))
                .Where(g => g != null).Select(g => g!)
                .Concat(groups.Where(g => !order.Contains(g.Label, cmp)))
                .ToList();
            var baseline = ordered.FirstOrDefault(g => baselineLabel != null && cmp.Equals(g.Label, baselineLabel)) ?? ordered[0];
            return (ordered, baseline);
        }

        /// <summary>
        /// Temele göre fark ve karar. İki grupta da 2+ kayıt varsa fark, kayıtlar arası dalgalanmanın yaklaşık iki
        /// standart hatasından (2·√(s₁²/n₁ + s₂²/n₂)) küçükse anlamlı sayılmaz.
        /// </summary>
        public static Comparison Compare(CompareMetric metric, CompareGroup baseline, CompareGroup group, EfficiencyBasis basis)
        {
            double? b = baseline.Value(metric, basis), v = group.Value(metric, basis);
            double? diff = metric.Absolute
                ? (b is double bb && v is double vv ? vv - bb : null)
                : PercentDiff(b, v);

            double? sb = baseline.Spread(metric, basis), sv = group.Spread(metric, basis);
            bool known = sb != null && sv != null;

            if (metric.Direction == 0) return new Comparison(diff, DiffVerdict.Info, known);
            if (diff is not double d) return new Comparison(null, DiffVerdict.Same, known);
            if (Math.Abs(d) < (metric.Absolute ? NeutralAbsolute : NeutralPercent)) return new Comparison(d, DiffVerdict.Same, known);

            if (known && b is double bv && v is double gv)
            {
                double se = Math.Sqrt(sb!.Value * sb.Value / baseline.Summaries.Count + sv!.Value * sv.Value / group.Summaries.Count);
                if (Math.Abs(gv - bv) <= 2 * se) return new Comparison(d, DiffVerdict.NotSignificant, true);
            }
            return new Comparison(d, Math.Sign(d) * metric.Direction > 0 ? DiffVerdict.Better : DiffVerdict.Worse, known);
        }

        /// <summary>Temele göre yüzde fark; temel 0 veya değer yoksa null.</summary>
        public static double? PercentDiff(double? baseline, double? value)
            => baseline is double b && value is double v && Math.Abs(b) > 1e-9 ? (v - b) / Math.Abs(b) * 100 : null;

        /// <summary>Kayıt süreleri (ısınma sonrası) birbirinden çok farklı mı.</summary>
        public static bool DurationMismatch(IReadOnlyList<CompareGroup> groups)
        {
            var d = groups.Select(g => g.MeasuredSeconds).Where(x => x > 0).ToList();
            return d.Count >= 2 && d.Max() / d.Min() > DurationMismatchRatio;
        }

        /// <summary>
        /// Grubun saniyelik serisi: ısınma sonrası, zaman ısınma bitişinden başlar; birden fazla kayıt varsa aynı saniyenin
        /// değerleri ortalanır.
        /// </summary>
        public static List<(double T, double Value)> Series(CompareGroup group, ChartMetric metric, double warmupSeconds)
        {
            return group.Runs
                .SelectMany(r => r.Samples.Where(s => s.T > warmupSeconds)
                                          .Select(s => (T: Math.Round(s.T - warmupSeconds), V: metric.Value(s))))
                .Where(x => x.V != null)
                .GroupBy(x => x.T)
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Average(x => x.V!.Value)))
                .ToList();
        }

        /// <summary>
        /// Karşılaştırma tablosu (sütunlar ekrandaki sırayla): ölçü, her grubun değeri ve temel dışındakilerin farkı
        /// (sıcaklıkta °C, diğerlerinde %). Bölge ayarına göre ayırıcı.
        /// </summary>
        public static string SummaryCsv(IReadOnlyList<CompareGroup> groups, CompareGroup baseline, EfficiencyBasis basis,
                                        Func<string, string> metricName, CultureInfo culture)
        {
            string sep = SessionRecorder.Separator(culture);
            var sb = new StringBuilder();
            var header = new List<string> { metricName("Metric") };
            foreach (var g in groups)
            {
                header.Add($"{g.Label} ({g.Runs.Count})");
                if (g != baseline) header.Add($"{g.Label} Δ");
            }
            sb.AppendLine(string.Join(sep, header.Select(h => SessionRecorder.Escape(h, sep))));

            foreach (var m in Metrics)
            {
                var row = new List<string> { metricName(m.Key) };
                foreach (var g in groups)
                {
                    row.Add(g.Value(m, basis)?.ToString(m.Format, culture) ?? "");
                    if (g != baseline)
                    {
                        var c = Compare(m, baseline, g, basis);
                        row.Add(c.Diff is double d ? d.ToString("0.0", culture) + (m.Absolute ? "" : "%") : "");
                    }
                }
                sb.AppendLine(string.Join(sep, row.Select(r => SessionRecorder.Escape(r, sep))));
            }
            return sb.ToString();
        }

        /// <summary>Seçilen kayıtların tüm saniyelik örnekleri (grafikteki ham veri).</summary>
        public static string SamplesCsv(IReadOnlyList<CompareGroup> groups, CultureInfo culture)
        {
            string sep = SessionRecorder.Separator(culture);
            var sb = new StringBuilder();
            string[] header = { "Label", "Run", "Game", "T", "FPS", "FrameTimeAvgMs", "FrameTimeMaxMs", "CpuTemp", "GpuTemp",
                                "CpuPower", "GpuPower", "CpuClock", "GpuClock", "CpuLoad", "GpuLoad" };
            sb.AppendLine(string.Join(sep, header));

            string F(double? v) => v?.ToString("0.###", culture) ?? "";
            foreach (var g in groups)
            foreach (var r in g.Runs)
            foreach (var s in r.Samples)
            {
                var row = new[]
                {
                    g.Label, r.Id, r.Game, F(s.T), F(s.Fps), F(s.FrameTimeAvgMs), F(s.FrameTimeMaxMs), F(s.CpuTemp), F(s.GpuTemp),
                    F(s.CpuPower), F(s.GpuPower), F(s.CpuClock), F(s.GpuClock), F(s.CpuLoad), F(s.GpuLoad)
                };
                sb.AppendLine(string.Join(sep, row.Select(x => SessionRecorder.Escape(x, sep))));
            }
            return sb.ToString();
        }
    }
}
