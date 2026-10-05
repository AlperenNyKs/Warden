using System.Globalization;
using System.IO;

namespace Warden.Tests
{
    public class TempAlarmMonitorTests
    {
        private static readonly DateTime T0 = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

        private static TempAlarmMonitor NewMonitor() => new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10));

        [Fact]
        public void DoesNotFire_BeforeSustainElapses()
        {
            var m = NewMonitor();
            Assert.Empty(m.Evaluate(T0, ("CPU", 95f, 90f)));
            Assert.Empty(m.Evaluate(T0.AddSeconds(9), ("CPU", 95f, 90f)));
        }

        [Fact]
        public void Fires_WhenAboveLimitForSustain()
        {
            var m = NewMonitor();
            m.Evaluate(T0, ("CPU", 95f, 90f));
            var alerts = m.Evaluate(T0.AddSeconds(10), ("CPU", 96f, 90f));

            var alert = Assert.Single(alerts);
            Assert.Equal("CPU", alert.Source);
            Assert.Equal(96f, alert.Temperature);
            Assert.Equal(90f, alert.Limit);
        }

        [Fact]
        public void DropBelowLimit_ResetsSustainTimer()
        {
            var m = NewMonitor();
            m.Evaluate(T0, ("GPU", 90f, 85f));
            m.Evaluate(T0.AddSeconds(5), ("GPU", 80f, 85f));     // sıçrama bitti
            m.Evaluate(T0.AddSeconds(6), ("GPU", 90f, 85f));     // yeniden başladı
            Assert.Empty(m.Evaluate(T0.AddSeconds(14), ("GPU", 90f, 85f)));
            Assert.Single(m.Evaluate(T0.AddSeconds(16), ("GPU", 90f, 85f)));
        }

        [Fact]
        public void Cooldown_PreventsRepeatedAlerts()
        {
            var m = NewMonitor();
            m.Evaluate(T0, ("CPU", 95f, 90f));
            Assert.Single(m.Evaluate(T0.AddSeconds(10), ("CPU", 95f, 90f)));
            Assert.Empty(m.Evaluate(T0.AddMinutes(5), ("CPU", 95f, 90f)));
            Assert.Single(m.Evaluate(T0.AddSeconds(10).AddMinutes(10), ("CPU", 95f, 90f)));
        }

        [Fact]
        public void MissingReading_NeverFires_AndSourcesAreIndependent()
        {
            var m = NewMonitor();
            m.Evaluate(T0, ("CPU", null, 90f), ("GPU", 99f, 85f));
            var alerts = m.Evaluate(T0.AddSeconds(10), ("CPU", null, 90f), ("GPU", 99f, 85f));
            Assert.Equal("GPU", Assert.Single(alerts).Source);
        }
    }

    public class SessionRecorderTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "warden-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static TelemetrySnapshot Snapshot(DateTime time, params (string Id, string Category, string Name, string Unit, float Value)[] sensors)
            => new()
            {
                Timestamp = time,
                AllSensors = sensors.Select(s => new TelemetrySensorItem
                {
                    Id = s.Id, Category = s.Category, Name = s.Name, Unit = s.Unit, Value = s.Value
                }).ToList()
            };

        [Fact]
        public void Separator_AvoidsDecimalSeparatorClash()
        {
            Assert.Equal(";", SessionRecorder.Separator(new CultureInfo("tr-TR")));
            Assert.Equal(",", SessionRecorder.Separator(new CultureInfo("en-US")));
        }

        [Theory]
        [InlineData("plain", "plain")]
        [InlineData("a;b", "\"a;b\"")]
        [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
        public void Escape_QuotesWhenNeeded(string input, string expected)
        {
            Assert.Equal(expected, SessionRecorder.Escape(input, ";"));
        }

        [Theory]
        [InlineData("Counter-Strike 2.exe", "Counter-Strike 2.exe")]
        [InlineData("a/b\\c:d*e?", "a_b_c_d_e_")]
        [InlineData("   ", "session")]
        public void SanitizeFileName_RemovesInvalidCharacters(string input, string expected)
        {
            Assert.Equal(expected, SessionRecorder.SanitizeFileName(input));
        }

        [Fact]
        public void Write_ProducesHeaderAndRows_WithFixedColumns()
        {
            var tr = new CultureInfo("tr-TR");
            using var rec = new SessionRecorder(_dir, tr);
            string file = rec.Start("cs2.exe", automatic: true);
            Assert.True(rec.IsRecording);
            Assert.True(rec.IsAutomatic);

            var t = new DateTime(2026, 10, 5, 21, 30, 0);
            rec.Write(Snapshot(t,
                ("gpu/temp", "GPU", "GPU Sıcaklığı", "°C", 71.5f),
                ("cpu/temp", "CPU", "CPU Sıcaklığı", "°C", 64.25f)));
            // İkinci satırda sensör sırası farklı ve biri eksik: sütunlar ilk satıra göre kalmalı
            rec.Write(Snapshot(t.AddSeconds(2),
                ("cpu/temp", "CPU", "CPU Sıcaklığı", "°C", 65f)));
            rec.Stop();
            Assert.False(rec.IsRecording);

            var lines = File.ReadAllLines(file);
            Assert.Equal(3, lines.Length);
            Assert.Equal("Zaman;CPU - CPU Sıcaklığı (°C);GPU - GPU Sıcaklığı (°C)", lines[0]);
            Assert.Equal("2026-10-05 21:30:00;64,25;71,5", lines[1]);
            Assert.Equal("2026-10-05 21:30:02;65;", lines[2]);
        }
    }

    public class AfterburnerServiceTests
    {
        [Theory]
        [InlineData(@"C:\Program Files (x86)\MSI Afterburner\", @"C:\Program Files (x86)\MSI Afterburner")]
        [InlineData(@"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe,0", @"C:\Program Files (x86)\MSI Afterburner")]
        [InlineData("\"D:\\Tools\\MSI Afterburner\\uninstall.exe\" /S", @"D:\Tools\MSI Afterburner")]
        [InlineData(@"E:\Apps\Afterburner\uninstall.exe", @"E:\Apps\Afterburner")]
        public void DirectoryFromRegistryValue_HandlesCommonFormats(string value, string expected)
        {
            Assert.Equal(expected, AfterburnerService.DirectoryFromRegistryValue(value));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void DirectoryFromRegistryValue_EmptyIsNull(string? value)
        {
            Assert.Null(AfterburnerService.DirectoryFromRegistryValue(value));
        }
    }

    public class AppConfigTempLimitTests
    {
        [Fact]
        public void Normalize_ClampsTemperatureLimits()
        {
            var cfg = new AppConfig { CpuTempLimit = 10, GpuTempLimit = 500 };
            cfg.Normalize();
            Assert.Equal(AppConfig.MinTempLimit, cfg.CpuTempLimit);
            Assert.Equal(AppConfig.MaxTempLimit, cfg.GpuTempLimit);
        }

        [Fact]
        public void Defaults_AreSensible()
        {
            var cfg = new AppConfig();
            Assert.True(cfg.TempAlarmEnabled);
            Assert.Equal(90, cfg.CpuTempLimit);
            Assert.Equal(85, cfg.GpuTempLimit);
            Assert.False(cfg.AutoRecordGameSessions);
            Assert.False(cfg.FirstRunCompleted);
        }
    }
}
