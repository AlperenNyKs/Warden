using System;

namespace Warden
{
    /// <summary>Biten bir oyun oturumunun özeti.</summary>
    public sealed record SessionSummary(string Game, TimeSpan Duration, float? MaxCpuTemp, float? MaxGpuTemp);

    /// <summary>
    /// Oyun oturumu boyunca en yüksek CPU/GPU sıcaklığını tutar; oturum bitince özet döndürür.
    /// Watcher ve telemetri farklı thread'lerden çağırır → kilitli.
    /// </summary>
    public sealed class SessionSummaryTracker
    {
        // Bundan kısa oturumlar (ör. oyunu açıp hemen kapatmak) bildirim olarak gösterilmez
        public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(1);

        private readonly object _lock = new();
        private string? _game;
        private DateTime _startUtc;
        private float? _maxCpu, _maxGpu;

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
                        finished = new SessionSummary(_game, duration, _maxCpu, _maxGpu);
                }

                _game = game;
                _startUtc = nowUtc;
                _maxCpu = _maxGpu = null;
                return finished;
            }
        }

        /// <summary>Telemetri ölçümü; oturum yoksa yok sayılır. 0 veya null "okunamadı" demektir.</summary>
        public void Observe(float? cpuTemp, float? gpuTemp)
        {
            lock (_lock)
            {
                if (_game == null) return;
                if (cpuTemp is > 0 && (_maxCpu == null || cpuTemp > _maxCpu)) _maxCpu = cpuTemp;
                if (gpuTemp is > 0 && (_maxGpu == null || gpuTemp > _maxGpu)) _maxGpu = gpuTemp;
            }
        }

        public bool IsActive { get { lock (_lock) return _game != null; } }
    }
}
