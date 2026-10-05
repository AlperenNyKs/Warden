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
