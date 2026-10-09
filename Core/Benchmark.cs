using System;
using System.Collections.Generic;
using System.Linq;

namespace Warden
{
    /// <summary>Benchmark kaydının saniyelik satırı. Okunamayan değerler null.</summary>
    public sealed class BenchmarkSample
    {
        public double T { get; set; }                 // kayıt başından saniye
        public double? Fps { get; set; }
        public double? FrameTimeAvgMs { get; set; }
        public double? FrameTimeMaxMs { get; set; }
        public float? CpuTemp { get; set; }
        public float? GpuTemp { get; set; }
        public float? CpuPower { get; set; }
        public float? GpuPower { get; set; }
        public float? CpuClock { get; set; }
        public float? GpuClock { get; set; }
        public float? CpuLoad { get; set; }
        public float? GpuLoad { get; set; }
    }

    /// <summary>Bir benchmark kaydı: etiket, oyun, saniyelik örnekler ve kare süreleri.</summary>
    public sealed class BenchmarkRun
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Game { get; set; } = "";
        public DateTime StartUtc { get; set; }
        public double DurationSeconds { get; set; }

        /// <summary>true: kare süreleri RTSS tamponundan kare kare okundu; false: yalnızca saniyelik FPS (yaklaşık %1 low).</summary>
        public bool FrameTimesExact { get; set; }

        public List<BenchmarkSample> Samples { get; set; } = new();

        /// <summary>Kare süreleri (ms), sırayla. JSON'a yazılmaz; ayrı ikili dosyada saklanır.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float[] FrameTimesMs { get; set; } = Array.Empty<float>();
    }

    public enum EfficiencyBasis { Gpu, Cpu, Total }

    /// <summary>Isınma süresi atıldıktan sonraki özet.</summary>
    public sealed record BenchmarkSummary(
        double MeasuredSeconds,
        double? AvgFps, double? Low1Fps, bool Low1Approximate,
        double? AvgCpuTemp, double? P95CpuTemp, double? AvgGpuTemp, double? P95GpuTemp,
        double? AvgCpuPower, double? AvgGpuPower,
        double? AvgCpuClock, double? AvgGpuClock, double? AvgCpuLoad, double? AvgGpuLoad)
    {
        public double? AvgTotalPower => AvgCpuPower is double c && AvgGpuPower is double g ? c + g : null;

        /// <summary>Ortalama FPS ÷ seçilen gücün ortalaması.</summary>
        public double? FpsPerWatt(EfficiencyBasis basis)
        {
            double? watts = basis switch
            {
                EfficiencyBasis.Gpu => AvgGpuPower,
                EfficiencyBasis.Cpu => AvgCpuPower,
                _ => AvgTotalPower
            };
            return AvgFps is double fps && watts is > 0 ? fps / watts.Value : null;
        }
    }

    public static class BenchmarkStats
    {
        /// <param name="warmupSeconds">Baştan atılacak süre (yükleme, shader derleme, boost'a çıkış).</param>
        public static BenchmarkSummary Compute(BenchmarkRun run, double warmupSeconds)
        {
            var samples = run.Samples.Where(s => s.T > warmupSeconds).ToList();
            double measured = Math.Max(0, run.DurationSeconds - warmupSeconds);

            double? avgFps, low1;
            bool approximate;
            var frames = FramesAfter(run.FrameTimesMs, warmupSeconds);
            if (frames.Length > 0)
            {
                // Ortalama FPS = kare sayısı ÷ süre (saniyelik FPS'lerin ortalaması takılmaları gizler)
                avgFps = frames.Length / (frames.Sum(f => (double)f) / 1000.0);
                // %1 low = kare sürelerinin 99. yüzdeliği, FPS'ye çevrilmiş
                low1 = 1000.0 / Percentile(frames.Select(f => (double)f), 0.99);
                approximate = false;
            }
            else
            {
                // Yedek: yalnızca saniyelik FPS var → en kötü %1'lik saniye
                var fps = samples.Where(s => s.Fps is > 0).Select(s => s.Fps!.Value).ToList();
                avgFps = fps.Count > 0 ? fps.Average() : null;
                low1 = fps.Count > 0 ? Percentile(fps, 0.01) : null;
                approximate = true;
            }

            return new BenchmarkSummary(
                measured, avgFps, low1, approximate,
                Avg(samples, s => s.CpuTemp), P95(samples, s => s.CpuTemp),
                Avg(samples, s => s.GpuTemp), P95(samples, s => s.GpuTemp),
                Avg(samples, s => s.CpuPower), Avg(samples, s => s.GpuPower),
                Avg(samples, s => s.CpuClock), Avg(samples, s => s.GpuClock),
                Avg(samples, s => s.CpuLoad), Avg(samples, s => s.GpuLoad));
        }

        // Kare süreleri sırayla toplanarak geçen süre bulunur; ısınma süresi içindeki kareler atılır
        internal static float[] FramesAfter(float[] frameTimesMs, double warmupSeconds)
        {
            double warmupMs = warmupSeconds * 1000, elapsed = 0;
            int start = 0;
            while (start < frameTimesMs.Length && elapsed < warmupMs)
                elapsed += frameTimesMs[start++];
            return frameTimesMs[start..];
        }

        /// <summary>En yakın sıra yöntemiyle yüzdelik (p: 0..1).</summary>
        internal static double Percentile(IEnumerable<double> values, double p)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return double.NaN;
            int rank = (int)Math.Ceiling(p * sorted.Length);
            return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
        }

        private static double? Avg(List<BenchmarkSample> s, Func<BenchmarkSample, float?> f)
        {
            var v = s.Select(f).Where(x => x != null).Select(x => (double)x!.Value).ToList();
            return v.Count > 0 ? v.Average() : null;
        }

        private static double? P95(List<BenchmarkSample> s, Func<BenchmarkSample, float?> f)
        {
            var v = s.Select(f).Where(x => x != null).Select(x => (double)x!.Value).ToList();
            return v.Count > 0 ? Percentile(v, 0.95) : null;
        }
    }

    /// <summary>
    /// RTSS'in 1024 karelik halka tamponundan yeni kareleri sırayla çıkarır. Yazma konumu ister 0..1023 arası bir
    /// sıra numarası ister sürekli artan bir sayaç olsun, iki okuma arasındaki fark kadar kare alınır. Okumalar
    /// 1024 kareden sık yapılmalıdır (250 ms → saniyede 4000 FPS'ye kadar).
    /// </summary>
    public sealed class FrameTimeCollector
    {
        private uint? _lastPos;

        public void Reset() => _lastPos = null;

        /// <returns>Son okumadan bu yana gelen kare süreleri (ms); ilk okumada boş.</returns>
        public List<float> Collect(uint pos, uint[] buffer)
        {
            var result = new List<float>();
            int len = buffer.Length;
            if (len == 0) return result;

            if (_lastPos is uint last && pos != last)
            {
                // Sayaç ise fark doğrudan; sıra numarası ise halka içinde ileri mesafe
                long count = pos > last ? pos - last : (pos + (long)len - last) % len;
                count = Math.Min(count, len);
                // pos bir sonraki yazılacak yer: yeni kareler (pos - count) .. (pos - 1), eskiden yeniye
                for (long i = count; i > 0; i--)
                {
                    long idx = ((long)pos - i) % len;
                    if (idx < 0) idx += len;
                    uint us = buffer[idx];   // RTSS kare süreleri mikrosaniye
                    if (us > 0) result.Add(us / 1000f);
                }
            }
            _lastPos = pos;
            return result;
        }
    }
}
