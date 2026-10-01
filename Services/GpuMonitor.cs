using NvAPIWrapper.GPU;
using System;
using System.Linq;
using System.Windows.Threading;

namespace Warden
{
    public class GpuData
    {
        public double CoreClockMhz { get; set; }
        public int TemperatureCelsius { get; set; }
        public int UsagePercentage { get; set; }
    }

    public class GpuMonitor
    {
        private readonly DispatcherTimer _timer;
        private PhysicalGPU? _gpu;

        public event EventHandler<GpuData>? GpuDataUpdated;

        /// <summary>NVIDIA API başarıyla başlatıldıysa true. False ise timer hiç başlatılmaz.</summary>
        public bool IsAvailable => _gpu != null;

        public GpuMonitor()
        {
            try
            {
                NvAPIWrapper.NVIDIA.Initialize();
                _gpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
            }
            catch { /* NVIDIA API mevcut değil veya GPU bulunamadı */ }

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += Timer_Tick;
        }

        public void Start()
        {
            // NVIDIA GPU yoksa timer'ı hiç başlatma — gereksiz tick yok
            if (!IsAvailable) return;
            _timer.Start();
        }

        public void Stop() => _timer.Stop();

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_gpu == null) return;
            try
            {
                double freqMhz = _gpu.CurrentClockFrequencies.GraphicsClock.Frequency / 1000.0;
                int temp  = _gpu.ThermalInformation.ThermalSensors.FirstOrDefault()?.CurrentTemperature ?? 0;
                int usage = (int)(_gpu.UsageInformation.GPU?.Percentage ?? 0);

                GpuDataUpdated?.Invoke(this, new GpuData
                {
                    CoreClockMhz       = freqMhz,
                    TemperatureCelsius = temp,
                    UsagePercentage    = usage
                });
            }
            catch { /* Okuma hatası — sessizce geç */ }
        }
    }
}
