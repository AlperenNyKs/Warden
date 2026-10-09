using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Warden
{
    /// <summary>
    /// Benchmark kaydı: 250 ms'de bir RTSS'ten yeni kareleri toplar, saniyede bir FPS/kare süresi ve sensör değerleriyle
    /// bir örnek yazar. Ayarları değiştirmez, yalnızca ölçer.
    /// </summary>
    public sealed class BenchmarkRecorder : IDisposable
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        private const int PollMs = 250;

        private readonly Func<TelemetrySnapshot?> _snapshot;
        private readonly object _lock = new();
        private readonly FrameTimeCollector _collector = new();
        private System.Threading.Timer? _timer;

        private BenchmarkRun? _run;
        private DateTime _startUtc;
        private int _targetPid;
        private string _targetName = "";
        private readonly List<float> _frames = new();
        private readonly List<float> _secondFrames = new();
        private double _nextSampleAt;
        private RtssApp? _lastApp;

        public BenchmarkRecorder(Func<TelemetrySnapshot?> snapshot)
        {
            _snapshot = snapshot;
        }

        public bool IsRecording { get { lock (_lock) return _run != null; } }

        /// <summary>Ölçülen uygulamanın exe adı ve son saniyenin FPS'i (sayfadaki canlı durum için).</summary>
        public (string App, double? Fps, TimeSpan Elapsed)? Live
        {
            get
            {
                lock (_lock)
                {
                    if (_run == null) return null;
                    return (Path.GetFileName(_targetName), _run.Samples.LastOrDefault()?.Fps, DateTime.UtcNow - _startUtc);
                }
            }
        }

        public void Start(string label, string game)
        {
            lock (_lock)
            {
                if (_run != null) return;
                _startUtc = DateTime.UtcNow;
                _run = new BenchmarkRun
                {
                    Id = DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                    Label = label,
                    Game = game,
                    StartUtc = _startUtc
                };
                _frames.Clear();
                _secondFrames.Clear();
                _collector.Reset();
                _targetPid = 0;
                _targetName = "";
                _lastApp = null;
                _nextSampleAt = 1;
                _timer = new System.Threading.Timer(_ => Tick(), null, 0, PollMs);
            }
        }

        /// <summary>Son kaydın ölçtüğü uygulamanın tam exe yolu (RTSS'ten; oyun adını bulmak için).</summary>
        public string LastTargetPath { get; private set; } = "";

        /// <summary>Kaydı bitirir; kayıt yoksa null. Oyun adı boşsa çağıran LastTargetPath'ten bulur.</summary>
        public BenchmarkRun? Stop()
        {
            lock (_lock)
            {
                if (_run == null) return null;
                _timer?.Dispose();
                _timer = null;

                var run = _run;
                _run = null;
                run.DurationSeconds = (DateTime.UtcNow - _startUtc).TotalSeconds;
                run.FrameTimesMs = _frames.ToArray();
                run.FrameTimesExact = _frames.Count > 0;
                LastTargetPath = _targetName;
                return run;
            }
        }

        private void Tick()
        {
            try
            {
                var apps = RtssReader.ReadApps();
                lock (_lock)
                {
                    if (_run == null) return;

                    var app = SelectTarget(apps);
                    if (app != null)
                    {
                        var fresh = _collector.Collect(app.FrameTimeBufPos, app.FrameTimeBuf);
                        _frames.AddRange(fresh);
                        _secondFrames.AddRange(fresh);
                    }
                    _lastApp = app;

                    double elapsed = (DateTime.UtcNow - _startUtc).TotalSeconds;
                    if (elapsed >= _nextSampleAt)
                    {
                        _run.Samples.Add(BuildSample(_nextSampleAt));
                        _secondFrames.Clear();
                        _nextSampleAt += 1;
                    }
                }
            }
            catch { /* tek bir okuma hatası kaydı durdurmasın */ }
        }

        private BenchmarkSample BuildSample(double t)
        {
            var s = new BenchmarkSample { T = t };
            if (_secondFrames.Count > 0)
            {
                double sumMs = _secondFrames.Sum(f => (double)f);
                s.Fps = _secondFrames.Count / (sumMs / 1000.0);
                s.FrameTimeAvgMs = sumMs / _secondFrames.Count;
                s.FrameTimeMaxMs = _secondFrames.Max();
            }
            else if (_lastApp != null)
            {
                // Tampon dolmuyorsa RTSS'in saniyelik sayacı
                s.Fps = _lastApp.PeriodFps;
                if (_lastApp.CurrentFrameTimeUs > 0) s.FrameTimeAvgMs = _lastApp.CurrentFrameTimeUs / 1000.0;
            }

            var snap = _snapshot();
            if (snap != null)
            {
                s.CpuTemp = snap.CpuTemperature;
                s.CpuPower = snap.CpuPowerWatts;
                s.CpuClock = snap.CpuClockMhz;
                s.CpuLoad = snap.CpuLoadPercent;
                if (snap.PrimaryGpu is GpuData g)
                {
                    s.GpuTemp = g.TemperatureCelsius > 0 ? g.TemperatureCelsius : null;
                    s.GpuPower = g.PowerWatts;
                    s.GpuClock = g.CoreClockMhz > 0 ? (float)g.CoreClockMhz : null;
                    s.GpuLoad = g.UsagePercentage;
                }
            }
            return s;
        }

        // Hedef kilitlenir: alt-tab ile Warden'a / tarayıcıya geçmek (RTSS onları da listeler) ölçümü kaydırmasın.
        // Hedef yoksa veya kapandıysa: öndeki pencere RTSS'te varsa o, yoksa RTSS'in son öne gelen 3D uygulaması.
        private RtssApp? SelectTarget(List<RtssApp> apps)
        {
            if (apps.Count == 0) return null;

            var locked = apps.FirstOrDefault(a => a.ProcessId == _targetPid);
            if (locked != null) return locked;

            GetWindowThreadProcessId(GetForegroundWindow(), out uint fg);
            var target = apps.FirstOrDefault(a => a.ProcessId == (int)fg)
                      ?? apps.FirstOrDefault(a => a.ProcessId == RtssReader.LastForegroundProcessId());
            if (target == null) return null;

            if (target.ProcessId != _targetPid)
            {
                _targetPid = target.ProcessId;
                _targetName = target.Name;
                _collector.Reset();   // başka uygulamanın tamponu: konum baştan
            }
            return target;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
