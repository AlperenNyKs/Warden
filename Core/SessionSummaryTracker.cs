using System;

namespace Warden
{
    /// <summary>Biten bir oyun oturumunun özeti.</summary>
    public sealed record SessionSummary(string Game, DateTime StartUtc, TimeSpan Duration,
                                        float? MaxCpuTemp, float? MaxGpuTemp,
                                        float? AvgCpuTemp, float? AvgGpuTemp);

    /// <summary>
    /// Oyun oturumu boyunca CPU/GPU sıcaklığının en yüksek ve ortalama değerini tutar; oturum bitince özet döndürür.
    /// Watcher ve telemetri farklı thread'lerden çağırır → kilitli.
    /// </summary>
    public sealed class SessionSummaryTracker
    {
        // Bundan kısa oturumlar (ör. oyunu açıp hemen kapatmak) bildirilmez ve geçmişe yazılmaz
        public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(1);

        // Oyunun ilk dakikası (yükleme ekranı, menü, shader derleme) sıcaklık istatistiğine sayılmaz:
        // düşük yük ortalamayı aşağı çeker, derleme sıçraması en yüksek değeri şişirir. Süre yine baştan sayılır.
        public static readonly TimeSpan TempWarmUp = TimeSpan.FromMinutes(1);

        private readonly object _lock = new();
        private string? _game;
        private DateTime _startUtc;
        private readonly TempStats _cpu = new(), _gpu = new();

        /// <summary>
        /// Oyun oturumu değişti (başladı, bitti ya da başka oyuna geçildi). Önceki oturum yeterince uzunsa özetini döndürür.
        /// </summary>
        public SessionSummary? OnSessionChanged(string? game, DateTime nowUtc)
        {
            lock (_lock)
            {
                SessionSummary? finished = null;
                if (_game != null)
                {
                    var duration = nowUtc - _startUtc;
                    if (duration >= MinDuration)
                        finished = new SessionSummary(_game, _startUtc, duration, _cpu.Max, _gpu.Max, _cpu.Average, _gpu.Average);
                }

                _game = game;
                _startUtc = nowUtc;
                _cpu.Reset();
                _gpu.Reset();
                return finished;
            }
        }

        /// <summary>
        /// Telemetri ölçümü; oturum yoksa veya oturumun ilk dakikasındaysa yok sayılır. 0 veya null "okunamadı" demektir.
        /// </summary>
        public void Observe(float? cpuTemp, float? gpuTemp, DateTime nowUtc)
        {
            lock (_lock)
            {
                if (_game == null || nowUtc - _startUtc < TempWarmUp) return;
                _cpu.Add(cpuTemp);
                _gpu.Add(gpuTemp);
            }
        }

        public bool IsActive { get { lock (_lock) return _game != null; } }

        private sealed class TempStats
        {
            private double _sum;
            private int _count;
            public float? Max { get; private set; }
            public float? Average => _count > 0 ? (float)(_sum / _count) : null;

            public void Add(float? value)
            {
                if (value is not > 0) return;
                float v = value.Value;
                _sum += v;
                _count++;
                if (Max == null || v > Max) Max = v;
            }

            public void Reset()
            {
                _sum = 0;
                _count = 0;
                Max = null;
            }
        }
    }
}
