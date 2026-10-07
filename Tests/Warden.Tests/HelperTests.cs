namespace Warden.Tests
{
    public class GameScannerTests
    {
        [Theory]
        [InlineData("unins000.exe")]
        [InlineData("UnityCrashHandler64.exe")]
        [InlineData("EasyAntiCheat_Setup.exe")]
        [InlineData("steam.exe")]
        [InlineData("vc_redist.x64.exe")]
        [InlineData("Game-Launcher.exe")]
        [InlineData("ArmaReforgerSteamDiag.exe")]
        public void IsSystemExe_FiltersToolsAndInstallers(string exe)
        {
            Assert.True(GameScanner.IsSystemExe(exe));
        }

        // Kaynak koddaki yorumda belirtilen, eski alt-dize eşleşmesinin yanlışlıkla elediği gerçek oyunlar
        [Theory]
        [InlineData("ACOrigins.exe")]
        [InlineData("ModernWarfare.exe")]
        [InlineData("Swordsman.exe")]
        [InlineData("MirrorsEdge.exe")]
        [InlineData("Peacemaker.exe")]
        [InlineData("TestDriveUnlimited.exe")]
        [InlineData("SteamWorldDig.exe")]
        [InlineData("CrashBandicoot.exe")]
        public void IsSystemExe_KeepsRealGames(string exe)
        {
            Assert.False(GameScanner.IsSystemExe(exe));
        }
    }

    public class AudioDeviceEnforcerTests
    {
        [Fact]
        public void MatchesAnyName_ExactMatchIgnoresCaseAndIcons()
        {
            Assert.True(AudioDeviceEnforcer.MatchesAnyName("🔊 Speakers (Realtek Audio)", new[] { "speakers (realtek audio)" }));
        }

        [Fact]
        public void MatchesAnyName_SonarChannelMatchesRenamedDevice()
        {
            Assert.True(AudioDeviceEnforcer.MatchesAnyName(
                "SteelSeries Sonar - Chat (SteelSeries Sonar Virtual Audio Device)",
                new[] { "SteelSeries Sonar - Chat" }));
        }

        [Fact]
        public void MatchesAnyName_DifferentSonarChannelDoesNotMatch()
        {
            Assert.False(AudioDeviceEnforcer.MatchesAnyName(
                "SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)",
                new[] { "SteelSeries Sonar - Chat" }));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void MatchesAnyName_EmptyDeviceNameNeverMatches(string deviceName)
        {
            Assert.False(AudioDeviceEnforcer.MatchesAnyName(deviceName, new[] { "", "Speakers" }));
        }
    }

    public class ExeNameHelperTests
    {
        [Theory]
        [InlineData("Cyberpunk 2077 (Cyberpunk2077.exe)", "Cyberpunk2077.exe")]
        [InlineData("Game (Remastered) (game.exe)", "game.exe")]
        [InlineData("game.exe", "game.exe")]
        [InlineData("  spaced.exe  ", "spaced.exe")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void ExtractExeName_ReturnsExeFromDisplayText(string? input, string expected)
        {
            Assert.Equal(expected, ExeNameHelper.ExtractExeName(input));
        }
    }
}

namespace Warden.Tests
{
    public class SonarPresetMatcherTests
    {
        private static SonarConfig P(string name, bool isPreset = true, string device = "game") =>
            new() { id = name, name = name, virtualAudioDevice = device, isPreset = isPreset };

        private static readonly SonarConfig[] Presets =
        {
            P("Valorant Pro Preset"), P("CS2 Pro Preset"), P("Destiny 2 by Bungie"),
            P("Rainbow Six Siege by Ubisoft"), P("Rainbow Six Siege"), P("Diablo® IV by Blizzard"),
            P("Helldivers™ 2"), P("Baldur's Gate 3"), P("GTA V"), P("COD: Black Ops 6"),
            P("Rust"), P("Stray"), P("Hunt: Showdown"), P("Destiny"), P("Music: Rock"), P("Movie: Immersion"), P("Flat"), P("FPS Footsteps"),
            P("Chat Preset", device: "chatRender"),
        };

        [Theory]
        [InlineData("VALORANT", "Valorant Pro Preset")]
        [InlineData("Counter-Strike 2", "CS2 Pro Preset")]
        [InlineData("Destiny 2", "Destiny 2 by Bungie")]
        [InlineData("Tom Clancy's Rainbow Six® Siege", "Rainbow Six Siege")]
        [InlineData("Diablo IV", "Diablo® IV by Blizzard")]
        [InlineData("HELLDIVERS™ 2", "Helldivers™ 2")]
        [InlineData("Baldur’s Gate 3", "Baldur's Gate 3")]
        [InlineData("Grand Theft Auto V", "GTA V")]
        [InlineData("Call of Duty: Black Ops 6", "COD: Black Ops 6")]
        [InlineData("Rust", "Rust")]
        [InlineData("Hunt: Showdown 1896", "Hunt: Showdown")]
        public void MatchesGameToItsPreset(string game, string expected)
        {
            var index = SonarPresetMatcher.BuildIndex(Presets);
            Assert.Equal(expected, SonarPresetMatcher.FindMatch(game, index)?.name);
        }

        [Theory]
        [InlineData("Rusty Lake Hotel")]      // tek kelimelik preset sonek eşleşmesinde kullanılmaz
        [InlineData("Stray Souls")]
        [InlineData("Destiny 3")]             // devam oyunu ilk oyunun preset'ine düşmemeli
        [InlineData("Rock Band")]             // "Music: Rock" oyun preset'i değil
        [InlineData("Flat Earth Simulator")]
        [InlineData("Wallpaper Engine")]
        [InlineData("Chat Preset")]           // oyun dışı cihaz preset'i
        public void DoesNotMatchUnrelatedTitles(string game)
        {
            var index = SonarPresetMatcher.BuildIndex(Presets);
            Assert.Null(SonarPresetMatcher.FindMatch(game, index));
        }

        [Fact]
        public void UserPresetWinsOverBuiltIn()
        {
            var index = SonarPresetMatcher.BuildIndex(new[] { P("Valorant Pro Preset"), P("Valorant", isPreset: false) });
            Assert.False(SonarPresetMatcher.FindMatch("VALORANT", index)!.isPreset);
        }
    }
}

namespace Warden.Tests
{
    public class SessionSummaryTrackerTests
    {
        private static readonly DateTime T0 = new(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void EndingASession_ReturnsDurationAndMaxTemps()
        {
            var t = new SessionSummaryTracker();
            Assert.Null(t.OnSessionChanged("aces.exe", T0));
            t.Observe(70, 60);
            t.Observe(88, 79);
            t.Observe(75, 65);

            var s = t.OnSessionChanged(null, T0.AddMinutes(102));

            Assert.NotNull(s);
            Assert.Equal("aces.exe", s!.Game);
            Assert.Equal(TimeSpan.FromMinutes(102), s.Duration);
            Assert.Equal(88f, s.MaxCpuTemp);
            Assert.Equal(79f, s.MaxGpuTemp);
            Assert.False(t.IsActive);
        }

        [Fact]
        public void ShortSession_IsNotReported()
        {
            var t = new SessionSummaryTracker();
            t.OnSessionChanged("aces.exe", T0);
            Assert.Null(t.OnSessionChanged(null, T0.AddSeconds(40)));
        }

        [Fact]
        public void UnreadableTemps_AreIgnored()
        {
            var t = new SessionSummaryTracker();
            t.OnSessionChanged("aces.exe", T0);
            t.Observe(null, 0);   // GPU sıcaklığı okunamadı (0)

            var s = t.OnSessionChanged(null, T0.AddMinutes(5))!;
            Assert.Null(s.MaxCpuTemp);
            Assert.Null(s.MaxGpuTemp);
        }

        [Fact]
        public void SwitchingGames_ReportsThePreviousAndStartsFresh()
        {
            var t = new SessionSummaryTracker();
            t.OnSessionChanged("aces.exe", T0);
            t.Observe(90, 80);

            var first = t.OnSessionChanged("hunt.exe", T0.AddMinutes(10))!;
            Assert.Equal("aces.exe", first.Game);

            t.Observe(60, 50);
            var second = t.OnSessionChanged(null, T0.AddMinutes(20))!;
            Assert.Equal("hunt.exe", second.Game);
            Assert.Equal(60f, second.MaxCpuTemp);   // önceki oyunun değerleri taşınmadı
        }

        [Fact]
        public void ObservationsWithoutASession_AreIgnored()
        {
            var t = new SessionSummaryTracker();
            t.Observe(95, 95);
            t.OnSessionChanged("aces.exe", T0);
            var s = t.OnSessionChanged(null, T0.AddMinutes(2))!;
            Assert.Null(s.MaxCpuTemp);
        }
    }
}

namespace Warden.Tests
{
    public class GameHistoryTests
    {
        private static readonly DateTime T0 = new(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc);

        private static GameSession S(string game, int minutes, float? avgCpu, float? maxCpu, int dayOffset = 0, string name = "") => new()
        {
            Game = game, Name = name, StartUtc = T0.AddDays(dayOffset), DurationSeconds = minutes * 60,
            AvgCpuTemp = avgCpu, MaxCpuTemp = maxCpu
        };

        private static string TempFile() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"warden-history-{Guid.NewGuid():N}.json");

        [Fact]
        public void Tracker_ReportsAverageTemps()
        {
            var t = new SessionSummaryTracker();
            t.OnSessionChanged("aces.exe", T0);
            t.Observe(60, 50);
            t.Observe(80, 70);
            var s = t.OnSessionChanged(null, T0.AddMinutes(5))!;
            Assert.Equal(70f, s.AvgCpuTemp);
            Assert.Equal(60f, s.AvgGpuTemp);
            Assert.Equal(T0, s.StartUtc);
        }

        [Fact]
        public void Aggregate_SumsTimeAndWeightsAverageByDuration()
        {
            var stats = GameHistory.Aggregate(new[]
            {
                S("aces.exe", 90, 70, 85, 0, "War Thunder"),
                S("ACES.exe", 30, 90, 95, 1, "War Thunder"),   // büyük/küçük harf farkı aynı oyun
                S("hunt.exe", 200, null, null, 2, "Hunt"),     // sıcaklık okunamamış
            });

            Assert.Equal("hunt.exe", stats[0].Game);           // en çok oynanan önce
            Assert.Null(stats[0].AvgCpuTemp);

            var wt = stats[1];
            Assert.Equal(TimeSpan.FromMinutes(120), wt.TotalTime);
            Assert.Equal(2, wt.Sessions);
            Assert.Equal(75f, wt.AvgCpuTemp);                  // (70*90 + 90*30) / 120
            Assert.Equal(95f, wt.MaxCpuTemp);
            Assert.Equal(T0.AddDays(1), wt.LastPlayedUtc);
        }

        [Fact]
        public void DisplayName_FallsBackToExeName()
        {
            Assert.Equal("aces", S("aces.exe", 5, null, null).DisplayName);
        }

        [Fact]
        public void Sessions_PersistAndReload()
        {
            string path = TempFile();
            try
            {
                new GameHistory(path).Add(S("aces.exe", 42, 70, 85, 0, "War Thunder"));
                var reloaded = new GameHistory(path).Sessions;
                Assert.Single(reloaded);
                Assert.Equal("War Thunder", reloaded[0].Name);
                Assert.Equal(TimeSpan.FromMinutes(42), reloaded[0].Duration);
            }
            finally { System.IO.File.Delete(path); }
        }

        [Fact]
        public void Add_KeepsOnlyTheNewestSessions()
        {
            string path = TempFile();
            try
            {
                var h = new GameHistory(path);
                for (int i = 0; i < GameHistory.MaxSessions + 5; i++) h.Add(S($"g{i}.exe", 1, null, null, i));
                var sessions = h.Sessions;
                Assert.Equal(GameHistory.MaxSessions, sessions.Count);
                Assert.Equal("g5.exe", sessions[0].Game);      // en eski 5 oturum düştü
            }
            finally { System.IO.File.Delete(path); }
        }

        [Fact]
        public void CorruptFile_StartsEmptyAndKeepsBackup()
        {
            string path = TempFile();
            try
            {
                System.IO.File.WriteAllText(path, "{ not json");
                Assert.Empty(new GameHistory(path).Sessions);
                Assert.True(System.IO.File.Exists(path + ".bak"));
            }
            finally
            {
                System.IO.File.Delete(path);
                System.IO.File.Delete(path + ".bak");
            }
        }
    }
}
