namespace Warden.Tests
{
    public class AppConfigTests
    {
        [Theory]
        [InlineData(0, AppConfig.MinCheckIntervalMs)]
        [InlineData(-5, AppConfig.MinCheckIntervalMs)]
        [InlineData(1000, 1000)]
        [InlineData(999_999, AppConfig.MaxCheckIntervalMs)]
        public void Normalize_ClampsCheckInterval(int input, int expected)
        {
            var cfg = new AppConfig { CheckIntervalMilliseconds = input };
            cfg.Normalize();
            Assert.Equal(expected, cfg.CheckIntervalMilliseconds);
        }

        [Fact]
        public void Normalize_ReplacesNullCollections()
        {
            var cfg = new AppConfig
            {
                Rules = null!,
                DiscoveredGames = null!,
                DiscoveredGameNames = null!,
                DisabledDevices = null!,
                DisabledDeviceNames = null!,
                TelemetryFavorites = null!,
                TelemetryGraphSensors = null!,
                DefaultPresetId = null!
            };

            cfg.Normalize();

            Assert.NotNull(cfg.Rules);
            Assert.NotNull(cfg.DiscoveredGames);
            Assert.NotNull(cfg.DiscoveredGameNames);
            Assert.NotNull(cfg.DisabledDevices);
            Assert.NotNull(cfg.DisabledDeviceNames);
            Assert.NotNull(cfg.TelemetryFavorites);
            Assert.NotNull(cfg.TelemetryGraphSensors);
            Assert.Equal("", cfg.DefaultPresetId);
        }

        [Fact]
        public void Normalize_RulesAreTrimmedDeduplicatedAndCaseInsensitive()
        {
            var cfg = new AppConfig
            {
                // System.Text.Json karşılaştırıcıyı düşürür: büyük/küçük harfe duyarlı sözlükle başla
                Rules = new Dictionary<string, string>
                {
                    ["Game.exe"] = "first",
                    [" game.exe "] = "second",
                    ["   "] = "ignored"
                }
            };

            cfg.Normalize();

            Assert.Single(cfg.Rules);
            Assert.True(cfg.Rules.TryGetValue("GAME.EXE", out string? preset));
            Assert.Equal("second", preset);
        }

        [Fact]
        public void Normalize_DeduplicatesDiscoveredGames()
        {
            var cfg = new AppConfig { DiscoveredGames = new() { "a.exe", "A.EXE", " ", "b.exe" } };
            cfg.Normalize();
            Assert.Equal(new[] { "a.exe", "b.exe" }, cfg.DiscoveredGames);
        }

        [Theory]
        [InlineData("en", "EN")]
        [InlineData("EN", "EN")]
        [InlineData("tr", "TR")]
        [InlineData("de", "TR")]
        [InlineData(null, "TR")]
        public void Normalize_LanguageFallsBackToTurkish(string? input, string expected)
        {
            var cfg = new AppConfig { Language = input! };
            cfg.Normalize();
            Assert.Equal(expected, cfg.Language);
        }

        [Fact]
        public void Normalize_ClampsGpuAndDeviceSettings()
        {
            var cfg = new AppConfig
            {
                TargetProfile = 9,
                CooldownSeconds = 0,
                DeviceDisableDelaySeconds = 10_000,
                TargetMhz = double.NaN
            };

            cfg.Normalize();

            Assert.Equal(5, cfg.TargetProfile);
            Assert.Equal(1, cfg.CooldownSeconds);
            Assert.Equal(300, cfg.DeviceDisableDelaySeconds);
            Assert.Equal(0.0, cfg.TargetMhz);
        }
    }
}
