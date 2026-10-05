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
