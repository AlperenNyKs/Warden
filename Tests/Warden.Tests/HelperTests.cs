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
            t.Observe(70, 60, T0.AddMinutes(2));
            t.Observe(88, 79, T0.AddMinutes(3));
            t.Observe(75, 65, T0.AddMinutes(4));

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
            t.Observe(null, 0, T0.AddMinutes(2));   // GPU sıcaklığı okunamadı (0)

            var s = t.OnSessionChanged(null, T0.AddMinutes(5))!;
            Assert.Null(s.MaxCpuTemp);
            Assert.Null(s.MaxGpuTemp);
        }

        [Fact]
        public void SwitchingGames_ReportsThePreviousAndStartsFresh()
        {
            var t = new SessionSummaryTracker();
            t.OnSessionChanged("aces.exe", T0);
            t.Observe(90, 80, T0.AddMinutes(2));

            var first = t.OnSessionChanged("hunt.exe", T0.AddMinutes(10))!;
            Assert.Equal("aces.exe", first.Game);

            t.Observe(60, 50, T0.AddMinutes(12));
            var second = t.OnSessionChanged(null, T0.AddMinutes(20))!;
            Assert.Equal("hunt.exe", second.Game);
            Assert.Equal(60f, second.MaxCpuTemp);   // önceki oyunun değerleri taşınmadı
        }

        [Fact]
        public void ObservationsWithoutASession_AreIgnored()
        {
            var t = new SessionSummaryTracker();
            t.Observe(95, 95, T0);
            t.OnSessionChanged("aces.exe", T0);
            var s = t.OnSessionChanged(null, T0.AddMinutes(2))!;
            Assert.Null(s.MaxCpuTemp);
        }

        [Fact]
        public void FirstMinute_IsLeftOutOfTempStatsButNotDuration()
        {
            var t = new SessionSummaryTracker();
            t.OnSessionChanged("aces.exe", T0);
            t.Observe(45, 40, T0.AddSeconds(10));   // yükleme ekranı: düşük yük
            t.Observe(99, 95, T0.AddSeconds(50));   // shader derleme sıçraması
            t.Observe(80, 70, T0.AddSeconds(60));   // ısınma süresi bitti → sayılır
            t.Observe(84, 74, T0.AddMinutes(5));

            var s = t.OnSessionChanged(null, T0.AddMinutes(10))!;
            Assert.Equal(TimeSpan.FromMinutes(10), s.Duration);
            Assert.Equal(82f, s.AvgCpuTemp);
            Assert.Equal(84f, s.MaxCpuTemp);
            Assert.Equal(74f, s.MaxGpuTemp);
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
            t.Observe(60, 50, T0.AddMinutes(2));
            t.Observe(80, 70, T0.AddMinutes(3));
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

namespace Warden.Tests
{
    public class DiskUsageTests
    {
        private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        private static readonly IReadOnlyDictionary<string, long> NoSizes = new Dictionary<string, long>();

        private static DiscoveredGame G(string exe, string source, long? size, DateTime? lastPlayed, string path = "") => new()
        {
            ExeName = exe, GameName = exe.Replace(".exe", ""), Source = source, SizeBytes = size, LastPlayedUtc = lastPlayed,
            InstallPath = path == "" ? $@"D:\Games\{exe}" : path
        };

        [Fact]
        public void LastPlayed_TakesTheNewerOfStoreAndHistory()
        {
            var games = new[] { G("aces.exe", "Steam", 100, Now.AddDays(-90)) };
            var history = new[] { new GameSession { Game = "ACES.exe", StartUtc = Now.AddDays(-3), DurationSeconds = 3600 } };

            var r = DiskUsage.Build(games, history, NoSizes, Now).Single();

            Assert.Equal(LastPlayedKind.Known, r.LastPlayedKind);
            Assert.Equal(Now.AddDays(-3).AddHours(1), r.LastPlayedUtc);   // oturumun bitişi
            Assert.False(r.Unused);
        }

        [Theory]
        [InlineData(59, false)]
        [InlineData(60, true)]
        public void Unused_After60Days(int daysAgo, bool unused)
        {
            var r = DiskUsage.Build(new[] { G("a.exe", "Steam", 1, Now.AddDays(-daysAgo)) }, Array.Empty<GameSession>(), NoSizes, Now).Single();
            Assert.Equal(unused, r.Unused);
        }

        [Fact]
        public void NoRecord_IsNeverOnSteamButUnknownElsewhere()
        {
            var rows = DiskUsage.Build(new[] { G("steam.exe", "Steam", 2, null), G("epic.exe", "Epic", 1, null) },
                                       Array.Empty<GameSession>(), NoSizes, Now);

            var steam = rows.Single(r => r.ExeName == "steam.exe");
            var epic = rows.Single(r => r.ExeName == "epic.exe");
            Assert.Equal(LastPlayedKind.Never, steam.LastPlayedKind);
            Assert.True(steam.Unused);
            Assert.Equal(LastPlayedKind.Unknown, epic.LastPlayedKind);
            Assert.False(epic.Unused);   // bilinmiyor → yanlışlıkla "oynanmıyor" denmez
        }

        [Fact]
        public void Size_FallsBackToFolderSize_AndLargestComesFirst()
        {
            var games = new[]
            {
                G("small.exe", "Steam", 10, Now),
                G("epic.exe", "Epic", null, Now, @"E:\Epic\Game"),
                G("nosize.exe", "GOG", null, Now),
            };
            // GetFolderSizesAsync ile aynı: büyük/küçük harf duyarsız sözlük
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [@"e:\epic\game"] = 500 };

            var rows = DiskUsage.Build(games, Array.Empty<GameSession>(), sizes, Now);

            Assert.Equal(new[] { "epic.exe", "small.exe", "nosize.exe" }, rows.Select(r => r.ExeName));
            Assert.Equal(500, rows[0].SizeBytes);
            Assert.Null(rows[2].SizeBytes);
            Assert.Equal(@"E:\", rows[0].Drive);
        }

        [Fact]
        public void GamesWithoutInstallPath_AreSkipped()
        {
            var g = G("x.exe", "Xbox", 1, Now);
            g.InstallPath = "";
            Assert.Empty(DiskUsage.Build(new[] { g }, Array.Empty<GameSession>(), NoSizes, Now));
        }

        [Theory]
        [InlineData(90, 1000, true)]
        [InlineData(100, 1000, false)]
        public void DriveSpace_IsLowUnderTenPercent(long free, long total, bool low)
        {
            Assert.Equal(low, new DriveSpace(@"D:\", free, total).IsLow);
        }

        [Theory]
        [InlineData("1791358643", 2026)]
        [InlineData("0", null)]
        [InlineData("", null)]
        public void SteamLastPlayed_Parses(string value, int? year)
        {
            Assert.Equal(year, GameScanner.ParseUnixTime(value)?.Year);
        }
    }
}

namespace Warden.Tests
{
    public class BenchmarkTests
    {
        private static BenchmarkRun Run(float[] frames, params BenchmarkSample[] samples) => new()
        {
            Id = "t", Label = "A", DurationSeconds = samples.Length, FrameTimesMs = frames, Samples = samples.ToList()
        };

        [Fact]
        public void AvgFps_IsFramesOverTime_NotMeanOfFps()
        {
            // 1 sn: 100 kare × 5 ms + 1 sn: 10 kare × 100 ms → 110 kare / 1.5 sn
            var frames = Enumerable.Repeat(5f, 100).Concat(Enumerable.Repeat(100f, 10)).ToArray();
            var s = BenchmarkStats.Compute(Run(frames), warmupSeconds: 0);
            Assert.Equal(110 / 1.5, s.AvgFps!.Value, 6);
            Assert.False(s.Low1Approximate);
        }

        [Fact]
        public void Low1_IsThe99thPercentileFrameTime()
        {
            var frames = Enumerable.Repeat(10f, 99).Append(50f).ToArray();   // 100 kare, en kötü %1 = 50 ms
            var s = BenchmarkStats.Compute(Run(frames), 0);
            Assert.Equal(1000.0 / 10, s.Low1Fps!.Value, 6);                    // nearest-rank: 99. kare 10 ms
            var worse = Enumerable.Repeat(10f, 98).Concat(new[] { 50f, 50f }).ToArray();
            Assert.Equal(1000.0 / 50, BenchmarkStats.Compute(Run(worse), 0).Low1Fps!.Value, 6);
        }

        [Fact]
        public void Warmup_DropsFramesAndSamplesFromTheStart()
        {
            // İlk 1 sn: 10 kare × 100 ms (yükleme), sonra 200 kare × 5 ms
            var frames = Enumerable.Repeat(100f, 10).Concat(Enumerable.Repeat(5f, 200)).ToArray();
            var run = Run(frames,
                new BenchmarkSample { T = 1, GpuTemp = 40 },
                new BenchmarkSample { T = 2, GpuTemp = 70 },
                new BenchmarkSample { T = 3, GpuTemp = 80 });

            var s = BenchmarkStats.Compute(run, warmupSeconds: 1);
            Assert.Equal(200.0, s.AvgFps!.Value, 6);
            Assert.Equal(75.0, s.AvgGpuTemp!.Value, 6);    // T=1 atıldı
            Assert.Equal(2.0, s.MeasuredSeconds, 6);
        }

        [Fact]
        public void WithoutFrameTimes_FallsBackToPerSecondFps_MarkedApproximate()
        {
            var run = Run(Array.Empty<float>(),
                new BenchmarkSample { T = 1, Fps = 100 }, new BenchmarkSample { T = 2, Fps = 60 }, new BenchmarkSample { T = 3, Fps = 140 });
            var s = BenchmarkStats.Compute(run, 0);
            Assert.True(s.Low1Approximate);
            Assert.Equal(100.0, s.AvgFps!.Value, 6);
            Assert.Equal(60.0, s.Low1Fps!.Value, 6);
        }

        [Fact]
        public void TempP95_AndFpsPerWatt()
        {
            var samples = Enumerable.Range(1, 20).Select(i => new BenchmarkSample
            {
                T = i, CpuTemp = 60 + i, CpuPower = 40, GpuPower = 100
            }).ToArray();
            var s = BenchmarkStats.Compute(Run(Enumerable.Repeat(10f, 2000).ToArray(), samples), 0);   // 100 FPS

            Assert.Equal(79.0, s.P95CpuTemp!.Value, 6);          // 20 değer: 19. sıra
            Assert.Equal(1.0, s.FpsPerWatt(EfficiencyBasis.Gpu)!.Value, 6);
            Assert.Equal(2.5, s.FpsPerWatt(EfficiencyBasis.Cpu)!.Value, 6);
            Assert.Equal(100.0 / 140, s.FpsPerWatt(EfficiencyBasis.Total)!.Value, 6);
        }

        [Fact]
        public void FpsPerWatt_NullWhenPowerUnknown()
        {
            var s = BenchmarkStats.Compute(Run(new[] { 10f, 10f }, new BenchmarkSample { T = 1 }), 0);
            Assert.Null(s.FpsPerWatt(EfficiencyBasis.Gpu));
        }

        private static uint[] Buffer(params (int idx, uint us)[] values)
        {
            var b = new uint[RtssReader.FrameTimeBufLength];
            foreach (var (i, v) in values) b[i] = v;
            return b;
        }

        [Fact]
        public void Collector_FirstReadIsEmpty_ThenReturnsNewFramesInOrder()
        {
            var c = new FrameTimeCollector();
            Assert.Empty(c.Collect(2, Buffer((0, 10000), (1, 11000))));
            var fresh = c.Collect(5, Buffer((0, 10000), (1, 11000), (2, 12000), (3, 13000), (4, 14000)));
            Assert.Equal(new[] { 12f, 13f, 14f }, fresh);
        }

        [Fact]
        public void Collector_HandlesWrapAroundAsRingIndex()
        {
            var c = new FrameTimeCollector();
            c.Collect(1022, Buffer());
            var fresh = c.Collect(2, Buffer((1022, 1000), (1023, 2000), (0, 3000), (1, 4000)));
            Assert.Equal(new[] { 1f, 2f, 3f, 4f }, fresh);
        }

        [Fact]
        public void Collector_HandlesMonotonicCounter()
        {
            var c = new FrameTimeCollector();
            c.Collect(5000, Buffer());
            // sayaç 5000 → 5003: kareler 5000..5002 → indeks 5000 % 1024 = 904..906
            var fresh = c.Collect(5003, Buffer((904, 7000), (905, 8000), (906, 9000)));
            Assert.Equal(new[] { 7f, 8f, 9f }, fresh);
        }

        [Fact]
        public void Store_RoundTripsRunsWithFrameTimes()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "warden-bench-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new BenchmarkStore(dir);
                store.Save(new BenchmarkRun
                {
                    Id = "20261009-120000", Label = "Profil 3", Game = "War Thunder", DurationSeconds = 60,
                    StartUtc = new DateTime(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc), FrameTimesExact = true,
                    FrameTimesMs = new[] { 6.9f, 7.1f, 16.6f },
                    Samples = { new BenchmarkSample { T = 1, Fps = 144, GpuPower = 110.5f } }
                });
                Assert.Equal(1, store.Count());

                var run = store.LoadAll().Single();
                Assert.Equal(new[] { 6.9f, 7.1f, 16.6f }, run.FrameTimesMs);
                Assert.Equal(110.5f, run.Samples[0].GpuPower);

                store.Rename(run, "Profil 4");
                Assert.Equal("Profil 4", store.LoadAll().Single().Label);

                store.Delete(run.Id);
                Assert.Empty(store.LoadAll());
            }
            finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
        }

        [Theory]
        [InlineData("F9", System.Windows.Forms.Keys.F9)]
        [InlineData("F12", System.Windows.Forms.Keys.F12)]
        [InlineData("A", System.Windows.Forms.Keys.F10)]     // listede olmayan tuş → varsayılan
        [InlineData(null, System.Windows.Forms.Keys.F10)]
        public void Hotkey_ParsesOnlyAllowedKeys(string? name, System.Windows.Forms.Keys expected)
        {
            Assert.Equal(expected, GlobalHotkey.Parse(name));
        }
    }
}

namespace Warden.Tests
{
    public class BenchmarkCompareTests
    {
        private static readonly DateTime T0 = new(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);

        // Sabit FPS'li kayıt: n saniye, her saniye 'fps' kare; sensörler sabit
        private static BenchmarkRun Run(string label, int minutesAfter, double fps, float gpuTemp, float gpuPower, int seconds = 30) => new()
        {
            Id = label + minutesAfter, Label = label, StartUtc = T0.AddMinutes(minutesAfter), DurationSeconds = seconds,
            FrameTimesMs = Enumerable.Repeat((float)(1000 / fps), (int)(fps * seconds)).ToArray(),
            Samples = Enumerable.Range(1, seconds).Select(t => new BenchmarkSample { T = t, Fps = fps, GpuTemp = gpuTemp, GpuPower = gpuPower }).ToList()
        };

        [Fact]
        public void Group_ByLabelCaseInsensitive_OldestGroupFirst()
        {
            var groups = BenchmarkCompare.Group(new[]
            {
                Run("UV 0.9V", 30, 100, 70, 120), Run("Stok", 0, 90, 80, 150), Run("stok ", 10, 110, 82, 150)
            }, warmupSeconds: 0);

            Assert.Equal(new[] { "Stok", "UV 0.9V" }, groups.Select(g => g.Label));
            Assert.Equal(2, groups[0].Runs.Count);
            var avgFps = BenchmarkCompare.Metrics.Single(m => m.Key == "AvgFps");
            Assert.Equal(100.0, groups[0].Value(avgFps, EfficiencyBasis.Gpu)!.Value, 3);   // (90 + 110) / 2; kare süreleri float
        }

        [Theory]
        [InlineData(100.0, 110.0, 10.0)]
        [InlineData(80.0, 60.0, -25.0)]
        public void PercentDiff_RelativeToBaseline(double baseline, double value, double expected)
        {
            Assert.Equal(expected, BenchmarkCompare.PercentDiff(baseline, value)!.Value, 6);
        }

        [Fact]
        public void PercentDiff_NullWithoutBaseline()
        {
            Assert.Null(BenchmarkCompare.PercentDiff(null, 5));
            Assert.Null(BenchmarkCompare.PercentDiff(0, 5));
        }

        [Fact]
        public void Verdict_FollowsMetricDirection_AndIgnoresNoise()
        {
            var fps = BenchmarkCompare.Metrics.Single(m => m.Key == "AvgFps");
            var temp = BenchmarkCompare.Metrics.Single(m => m.Key == "GpuTemp");
            var clock = BenchmarkCompare.Metrics.Single(m => m.Key == "GpuClock");

            Assert.Equal(1, BenchmarkCompare.Verdict(fps, 5));      // daha çok FPS iyi
            Assert.Equal(-1, BenchmarkCompare.Verdict(temp, 5));    // daha sıcak kötü
            Assert.Equal(1, BenchmarkCompare.Verdict(temp, -5));
            Assert.Equal(0, BenchmarkCompare.Verdict(clock, 10));   // MHz yalnızca bilgi
            Assert.Equal(0, BenchmarkCompare.Verdict(fps, 0.3));    // gürültü bandı
        }

        [Fact]
        public void Series_StartsAfterWarmup_AndAveragesRunsPerSecond()
        {
            var g = BenchmarkCompare.Group(new[] { Run("A", 0, 100, 60, 100, 5), Run("A", 1, 100, 80, 100, 5) }, 2).Single();
            var temp = BenchmarkCompare.ChartMetrics.Single(m => m.Key == "GpuTemp");

            var series = BenchmarkCompare.Series(g, temp, warmupSeconds: 2);

            Assert.Equal(new[] { 1.0, 2.0, 3.0 }, series.Select(p => p.T));   // T=3..5 → 1..3
            Assert.All(series, p => Assert.Equal(70.0, p.Value, 6));
        }

        [Fact]
        public void SummaryCsv_UsesRegionalSeparatorAndDecimals()
        {
            var groups = BenchmarkCompare.Group(new[] { Run("Stok", 0, 100, 80, 150), Run("UV", 5, 110, 70, 120) }, 0);
            var tr = new System.Globalization.CultureInfo("tr-TR");

            string csv = BenchmarkCompare.SummaryCsv(groups, EfficiencyBasis.Gpu, k => k, tr);
            var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal("Metric;Stok (1);UV (1);UV %", lines[0]);
            Assert.Equal("AvgFps;100,0;110,0;10,0", lines[1]);
            Assert.Contains(lines, l => l.StartsWith("GpuTemp;80,0;70,0;-12,5"));
        }

        [Fact]
        public void SamplesCsv_HasOneRowPerSample()
        {
            var groups = BenchmarkCompare.Group(new[] { Run("A", 0, 60, 70, 100, 3), Run("B", 1, 60, 70, 100, 2) }, 0);
            var lines = BenchmarkCompare.SamplesCsv(groups, System.Globalization.CultureInfo.InvariantCulture)
                                        .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(1 + 3 + 2, lines.Length);
            Assert.StartsWith("Label,Run,Game,T,FPS", lines[0]);
        }
    }
}

namespace Warden.Tests
{
    public class BenchmarkClockRangeTests
    {
        [Fact]
        public void ClockMinMax_IgnoreWarmupAndMissingValues()
        {
            var run = new BenchmarkRun
            {
                Id = "c", Label = "A", DurationSeconds = 5,
                Samples =
                {
                    new BenchmarkSample { T = 1, GpuClock = 300, CpuClock = 800 },    // ısınma: boost'a çıkış
                    new BenchmarkSample { T = 2, GpuClock = 1605, CpuClock = 4200 },
                    new BenchmarkSample { T = 3, GpuClock = 1515, CpuClock = 3900 },
                    new BenchmarkSample { T = 4, GpuClock = null, CpuClock = 4000 },  // okunamadı
                    new BenchmarkSample { T = 5, GpuClock = 1560, CpuClock = 4100 },
                }
            };

            var s = BenchmarkStats.Compute(run, warmupSeconds: 1);

            Assert.Equal(1515, s.MinGpuClock);
            Assert.Equal(1605, s.MaxGpuClock);
            Assert.Equal(1560, s.AvgGpuClock);
            Assert.Equal(3900, s.MinCpuClock);
            Assert.Equal(4200, s.MaxCpuClock);
        }

        [Fact]
        public void CompareTable_HasClockMinMaxRows()
        {
            var keys = BenchmarkCompare.Metrics.Select(m => m.Key).ToList();
            Assert.Equal(new[] { "GpuClock", "GpuClockMin", "GpuClockMax", "CpuClock", "CpuClockMin", "CpuClockMax" },
                         keys.Where(k => k.Contains("Clock")));
        }
    }
}
