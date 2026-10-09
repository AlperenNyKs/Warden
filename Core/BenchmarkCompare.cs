using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Warden
{
    /// <summary>Karşılaştırma tablosundaki bir ölçü. Direction: +1 yüksek iyi, -1 düşük iyi, 0 yalnızca bilgi.</summary>
    public sealed record CompareMetric(string Key, Func<BenchmarkSummary, EfficiencyBasis, double?> Value, int Direction, string Format);

    /// <summary>Grafikte çizilebilen saniyelik ölçü.</summary>
    public sealed record ChartMetric(string Key, Func<BenchmarkSample, double?> Value, string Unit);

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

        public double? Value(CompareMetric metric, EfficiencyBasis basis)
        {
            var values = Summaries.Select(s => metric.Value(s, basis)).Where(v => v != null).Select(v => v!.Value).ToList();
            return values.Count > 0 ? values.Average() : null;
        }
    }

    public static class BenchmarkCompare
    {
        /// <summary>Grafik okunaklı kalsın diye en fazla bu kadar grup karşılaştırılır.</summary>
        public const int MaxGroups = 4;

        // Farkın "değişmedi" sayıldığı eşik (%): ölçüm gürültüsünü iyi/kötü diye boyamasın
        public const double NeutralBand = 0.5;

        public static readonly CompareMetric[] Metrics =
        {
            new("AvgFps",      (s, _) => s.AvgFps, +1, "0.0"),
            new("Low1Fps",     (s, _) => s.Low1Fps, +1, "0.0"),
            new("FpsPerWatt",  (s, b) => s.FpsPerWatt(b), +1, "0.00"),
            new("GpuClock",    (s, _) => s.AvgGpuClock, 0, "0"),
            new("CpuClock",    (s, _) => s.AvgCpuClock, 0, "0"),
            new("GpuTemp",     (s, _) => s.AvgGpuTemp, -1, "0.0"),
            new("GpuTempP95",  (s, _) => s.P95GpuTemp, -1, "0.0"),
            new("CpuTemp",     (s, _) => s.AvgCpuTemp, -1, "0.0"),
            new("CpuTempP95",  (s, _) => s.P95CpuTemp, -1, "0.0"),
            new("GpuPower",    (s, _) => s.AvgGpuPower, -1, "0.0"),
            new("CpuPower",    (s, _) => s.AvgCpuPower, -1, "0.0"),
            new("TotalPower",  (s, _) => s.AvgTotalPower, -1, "0.0"),
            new("GpuLoad",     (s, _) => s.AvgGpuLoad, 0, "0"),
            new("CpuLoad",     (s, _) => s.AvgCpuLoad, 0, "0"),
        };

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

        /// <summary>Etikete göre gruplar (büyük/küçük harf duyarsız); en eski kaydı olan grup önce (temel).</summary>
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

        /// <summary>Temele göre yüzde fark; temel 0 veya değer yoksa null.</summary>
        public static double? PercentDiff(double? baseline, double? value)
            => baseline is double b && value is double v && Math.Abs(b) > 1e-9 ? (v - b) / Math.Abs(b) * 100 : null;

        /// <summary>+1 iyileşme, -1 kötüleşme, 0 bilgi amaçlı ölçü veya fark gürültü bandında.</summary>
        public static int Verdict(CompareMetric metric, double? percent)
        {
            if (metric.Direction == 0 || percent is not double p || Math.Abs(p) < NeutralBand) return 0;
            return Math.Sign(p) * metric.Direction;
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

        /// <summary>Karşılaştırma tablosu: ölçü, her grubun değeri ve temele göre farkı (bölge ayarına göre ayırıcı).</summary>
        public static string SummaryCsv(IReadOnlyList<CompareGroup> groups, EfficiencyBasis basis, Func<string, string> metricName,
                                        CultureInfo culture)
        {
            string sep = SessionRecorder.Separator(culture);
            var sb = new StringBuilder();
            var header = new List<string> { metricName("Metric") };
            for (int i = 0; i < groups.Count; i++)
            {
                header.Add($"{groups[i].Label} ({groups[i].Runs.Count})");
                if (i > 0) header.Add($"{groups[i].Label} %");
            }
            sb.AppendLine(string.Join(sep, header.Select(h => SessionRecorder.Escape(h, sep))));

            foreach (var m in Metrics)
            {
                var row = new List<string> { metricName(m.Key) };
                double? baseline = groups[0].Value(m, basis);
                for (int i = 0; i < groups.Count; i++)
                {
                    double? v = groups[i].Value(m, basis);
                    row.Add(v?.ToString(m.Format, culture) ?? "");
                    if (i > 0) row.Add(PercentDiff(baseline, v)?.ToString("0.0", culture) ?? "");
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
