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

        private static AppConfig ConfigWith(params (string Exe, string Name, string? Preset)[] games)
        {
            var cfg = new AppConfig();
            foreach (var (exe, name, preset) in games)
            {
                cfg.DiscoveredGames.Add(exe);
                cfg.DiscoveredGameNames[exe] = name;
                if (preset != null) cfg.Rules[exe] = preset;
            }
            return cfg;
        }

        [Fact]
        public void Prune_RemovesUninstalledScannedGamesAndTheirRules()
        {
            var cfg = ConfigWith(("pubg.exe", "PUBG", "p1"), ("aces.exe", "War Thunder", "p2"));
            cfg.ScannedGames = new() { "pubg.exe", "aces.exe" };

            var removed = cfg.PruneUninstalledGames(new[] { "ACES.exe" });

            Assert.Equal(new[] { "pubg.exe" }, removed);
            Assert.Equal(new[] { "aces.exe" }, cfg.DiscoveredGames);
            Assert.False(cfg.Rules.ContainsKey("pubg.exe"));
            Assert.False(cfg.DiscoveredGameNames.ContainsKey("pubg.exe"));
            Assert.True(cfg.Rules.ContainsKey("aces.exe"));
        }

        [Fact]
        public void Prune_KeepsManuallyAddedGames()
        {
            var cfg = ConfigWith(("manual.exe", "My Game", "p1"), ("aces.exe", "War Thunder", null));
            cfg.ScannedGames = new() { "aces.exe" };

            Assert.Empty(cfg.PruneUninstalledGames(new[] { "aces.exe" }));
            Assert.True(cfg.Rules.ContainsKey("manual.exe"));
        }

        [Fact]
        public void Prune_LegacyConfigTreatsNamedEntriesAsScanned()
        {
            // Eski config: ScannedGames yok. Adı boş kayıt manuel/kural satırından eklenmiştir.
            var cfg = ConfigWith(("pubg.exe", "PUBG", "p1"), ("noname.exe", "", "p2"));

            var removed = cfg.PruneUninstalledGames(new[] { "aces.exe" });

            Assert.Equal(new[] { "pubg.exe" }, removed);
            Assert.True(cfg.Rules.ContainsKey("noname.exe"));
            Assert.Equal(new[] { "aces.exe" }, cfg.ScannedGames);
        }

        [Fact]
        public void Prune_EmptyScanRemovesNothing()
        {
            var cfg = ConfigWith(("pubg.exe", "PUBG", "p1"));
            cfg.ScannedGames = new() { "pubg.exe" };

            Assert.Empty(cfg.PruneUninstalledGames(Array.Empty<string>()));
            Assert.True(cfg.Rules.ContainsKey("pubg.exe"));
        }
    }
}

namespace Warden.Tests
{
    public class GameLibrarySyncTests
    {
        private static DiscoveredGame G(string exe, string name) => new() { ExeName = exe, GameName = name, Source = "Steam" };
        private static readonly SonarConfig[] Presets =
        {
            new() { id = "wt", name = "War Thunder", virtualAudioDevice = "game", isPreset = true },
            new() { id = "hunt", name = "Hunt: Showdown", virtualAudioDevice = "game", isPreset = true },
        };

        [Fact]
        public void Apply_AddsNewGamesAndAssignsGgProfiles()
        {
            var cfg = new AppConfig();
            var r = GameLibrarySync.Apply(cfg, new[] { G("aces.exe", "War Thunder"), G("squad.exe", "Squad") }, Presets);

            Assert.Equal(new[] { "War Thunder", "Squad" }, r.Added);
            Assert.Equal(new[] { "War Thunder" }, r.Assigned);
            Assert.Equal("wt", cfg.Rules["aces.exe"]);
            Assert.False(cfg.Rules.ContainsKey("squad.exe"));
        }

        [Fact]
        public void Apply_DoesNotReassignAProfileTheUserDeleted()
        {
            var cfg = new AppConfig();
            var games = new[] { G("aces.exe", "War Thunder") };
            GameLibrarySync.Apply(cfg, games, Presets);
            cfg.Rules.Remove("aces.exe");

            var r = GameLibrarySync.Apply(cfg, games, Presets);

            Assert.False(r.HasChanges);
            Assert.False(cfg.Rules.ContainsKey("aces.exe"));
        }

        [Fact]
        public void Apply_KeepsExistingRulesAndSkipsAssignmentWithoutGg()
        {
            var cfg = new AppConfig();
            cfg.Rules["aces.exe"] = "mine";
            var games = new[] { G("aces.exe", "War Thunder"), G("hunt.exe", "Hunt: Showdown 1896") };

            var r = GameLibrarySync.Apply(cfg, games, presets: null);
            Assert.Empty(r.Assigned);

            GameLibrarySync.Apply(cfg, games, Presets);   // GG sonradan açıldı
            Assert.Equal("mine", cfg.Rules["aces.exe"]);
            Assert.Equal("hunt", cfg.Rules["hunt.exe"]);
        }

        [Fact]
        public void Apply_AutoAssignOffAddsGamesOnly()
        {
            var cfg = new AppConfig { AutoAssignGgPresets = false };
            var r = GameLibrarySync.Apply(cfg, new[] { G("aces.exe", "War Thunder") }, Presets);

            Assert.Single(r.Added);
            Assert.Empty(cfg.Rules);
        }

        [Fact]
        public void Apply_ReinstalledGameGetsProfileAgain()
        {
            var cfg = new AppConfig();
            GameLibrarySync.Apply(cfg, new[] { G("aces.exe", "War Thunder"), G("hunt.exe", "Hunt: Showdown") }, Presets);
            cfg.Rules.Remove("aces.exe");                                                    // kullanıcı sildi

            var removed = GameLibrarySync.Apply(cfg, new[] { G("hunt.exe", "Hunt: Showdown") }, Presets);   // oyun kaldırıldı
            Assert.Equal(new[] { "War Thunder" }, removed.Removed);

            var r = GameLibrarySync.Apply(cfg, new[] { G("aces.exe", "War Thunder"), G("hunt.exe", "Hunt: Showdown") }, Presets);
            Assert.Equal(new[] { "War Thunder" }, r.Assigned);
        }
    }
}
