using System.Text.Json;

namespace Warden.Tests
{
    public class UpdateServiceTests
    {
        private const string Sha = "2358016819d3c8d05427618de8fd54e86b58fd1d71ec785de93d91e21e2d00e0";

        private static string ReleaseJson(
            string tag = "v1.1.0",
            bool prerelease = false,
            string assetName = "Warden-Setup-1.1.0.exe",
            string? url = null,
            string? digest = "sha256:" + Sha)
        {
            url ??= $"https://github.com/AlperenNyKs/Warden/releases/download/{tag}/{assetName}";
            var asset = new Dictionary<string, object?>
            {
                ["name"] = assetName,
                ["browser_download_url"] = url,
                ["size"] = 53272901
            };
            if (digest != null) asset["digest"] = digest;

            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["tag_name"] = tag,
                ["draft"] = false,
                ["prerelease"] = prerelease,
                ["html_url"] = $"https://github.com/AlperenNyKs/Warden/releases/tag/{tag}",
                ["assets"] = new[] { asset }
            });
        }

        [Theory]
        [InlineData("v1.2.3", 1, 2, 3)]
        [InlineData("1.2", 1, 2, 0)]
        [InlineData("V2", 2, 0, 0)]
        [InlineData("v1.0.0-ci.7", 1, 0, 0)]
        [InlineData("1.4.0+abc", 1, 4, 0)]
        public void TryParseTag_ParsesCommonFormats(string tag, int major, int minor, int build)
        {
            Assert.True(UpdateService.TryParseTag(tag, out var v));
            Assert.Equal(new Version(major, minor, build, 0), v);
        }

        [Theory]
        [InlineData("")]
        [InlineData("latest")]
        [InlineData(null)]
        public void TryParseTag_RejectsGarbage(string? tag)
        {
            Assert.False(UpdateService.TryParseTag(tag, out _));
        }

        [Fact]
        public void Evaluate_NewerRelease_IsAvailable()
        {
            var r = UpdateService.Evaluate(ReleaseJson(), new Version(1, 0, 0));

            Assert.Equal(UpdateCheckStatus.UpdateAvailable, r.Status);
            Assert.NotNull(r.Update);
            Assert.Equal(new Version(1, 1, 0, 0), r.Update!.Version);
            Assert.Equal(Sha, r.Update.Sha256);
            Assert.Equal("Warden-Setup-1.1.0.exe", r.Update.AssetName);
        }

        [Theory]
        [InlineData(1, 1, 0)]
        [InlineData(1, 2, 0)]
        public void Evaluate_SameOrOlderRelease_IsUpToDate(int major, int minor, int build)
        {
            var r = UpdateService.Evaluate(ReleaseJson(), new Version(major, minor, build));
            Assert.Equal(UpdateCheckStatus.UpToDate, r.Status);
            Assert.Null(r.Update);
        }

        [Fact]
        public void Evaluate_Prerelease_IsIgnored()
        {
            var r = UpdateService.Evaluate(ReleaseJson(prerelease: true), new Version(1, 0, 0));
            Assert.Equal(UpdateCheckStatus.UpToDate, r.Status);
        }

        [Fact]
        public void Evaluate_MissingDigest_IsRejected()
        {
            var r = UpdateService.Evaluate(ReleaseJson(digest: null), new Version(1, 0, 0));
            Assert.Equal(UpdateCheckStatus.Error, r.Status);
            Assert.Null(r.Update);
        }

        [Theory]
        [InlineData("https://evil.example.com/AlperenNyKs/Warden/releases/download/v1.1.0/Warden-Setup-1.1.0.exe")]
        [InlineData("http://github.com/AlperenNyKs/Warden/releases/download/v1.1.0/Warden-Setup-1.1.0.exe")]
        [InlineData("https://github.com/someone-else/Warden/releases/download/v1.1.0/Warden-Setup-1.1.0.exe")]
        public void Evaluate_ForeignDownloadUrl_IsRejected(string url)
        {
            var r = UpdateService.Evaluate(ReleaseJson(url: url), new Version(1, 0, 0));
            Assert.Equal(UpdateCheckStatus.Error, r.Status);
            Assert.Null(r.Update);
        }

        [Fact]
        public void Evaluate_NoSetupAsset_IsError()
        {
            var r = UpdateService.Evaluate(ReleaseJson(assetName: "source.zip"), new Version(1, 0, 0));
            Assert.Equal(UpdateCheckStatus.Error, r.Status);
        }
    }
}
