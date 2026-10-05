using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Warden
{
    public enum UpdateCheckStatus
    {
        UpToDate,
        UpdateAvailable,
        /// <summary>Release bilgisi alınamadı (repo gizli, hiç sürüm yok veya istek reddedildi).</summary>
        Unavailable,
        Error
    }

    public sealed record UpdateInfo(
        Version Version,
        string Tag,
        string AssetName,
        Uri DownloadUrl,
        string Sha256,
        long Size,
        Uri ReleasePage);

    public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update, string Message);

    /// <summary>
    /// GitHub Releases üzerinden yeni sürüm kontrolü, setup indirme (SHA256 doğrulamalı) ve sessiz kurulum.
    /// Warden yönetici olarak çalıştığı için indirilen setup da yönetici olarak çalışır; bu yüzden:
    ///  - yalnızca bu reponun release adresinden HTTPS ile indirilir,
    ///  - GitHub'ın release asset için verdiği SHA256 özeti zorunludur ve doğrulanır,
    ///  - doğrulama ile çalıştırma arasında dosya kilitli tutulur (değiştirilemez/silinemez).
    /// </summary>
    public sealed class UpdateService : IDisposable
    {
        public const string Owner = "AlperenNyKs";
        public const string Repo = "Warden";

        private static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
        private static readonly string AllowedDownloadPrefix = $"/{Owner}/{Repo}/releases/download/";

        private readonly HttpClient _http;

        public UpdateService()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,   // github.com → release-assets.githubusercontent.com (HTTPS)
                AutomaticDecompression = DecompressionMethods.All
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Warden", CurrentVersion.ToString(3)));
        }

        /// <summary>Çalışan Warden'ın sürümü (CI etiketten -p:Version ile verir).</summary>
        public static Version CurrentVersion
        {
            get
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
                return Normalize(v);
            }
        }

        /// <summary>"v1.2.3", "1.2", "1.2.3-ci.4" gibi etiketleri Version'a çevirir.</summary>
        public static bool TryParseTag(string? tag, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(tag)) return false;

            string t = tag.Trim();
            if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
            int dash = t.IndexOfAny(new[] { '-', '+' });
            if (dash >= 0) t = t[..dash];

            if (!Version.TryParse(t.Contains('.') ? t : t + ".0", out var parsed)) return false;
            version = Normalize(parsed);
            return true;
        }

        /// <summary>Karşılaştırma için Build/Revision -1 yerine 0 olur (1.2 == 1.2.0).</summary>
        private static Version Normalize(Version v)
            => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

        public static bool IsAllowedDownloadUrl(Uri? url)
            => url != null &&
               url.Scheme == Uri.UriSchemeHttps &&
               url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
               url.AbsolutePath.StartsWith(AllowedDownloadPrefix, StringComparison.OrdinalIgnoreCase);

        public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode is HttpStatusCode.NotFound)
                    return new(UpdateCheckStatus.Unavailable, null, "Release bulunamadı (repo gizli olabilir veya henüz sürüm yok).");
                if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                    return new(UpdateCheckStatus.Unavailable, null, $"GitHub isteği reddetti ({(int)resp.StatusCode}); daha sonra tekrar denenecek.");
                resp.EnsureSuccessStatusCode();

                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Evaluate(json, CurrentVersion);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new(UpdateCheckStatus.Error, null, ex.Message);
            }
        }

        /// <summary>GitHub "latest release" JSON'unu değerlendirir (ağdan bağımsız, test edilebilir).</summary>
        public static UpdateCheckResult Evaluate(string releaseJson, Version current)
        {
            using var doc = JsonDocument.Parse(releaseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ||
                root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                return new(UpdateCheckStatus.UpToDate, null, "Son sürüm taslak/ön sürüm; atlandı.");

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!TryParseTag(tag, out var latest))
                return new(UpdateCheckStatus.Error, null, $"Geçersiz sürüm etiketi: '{tag}'");

            if (latest <= Normalize(current))
                return new(UpdateCheckStatus.UpToDate, null, $"Güncel (son sürüm {tag}).");

            Uri releasePage = root.TryGetProperty("html_url", out var h) && Uri.TryCreate(h.GetString(), UriKind.Absolute, out var hp)
                ? hp
                : new Uri($"https://github.com/{Owner}/{Repo}/releases/tag/{Uri.EscapeDataString(tag)}");

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return new(UpdateCheckStatus.Error, null, $"{tag} sürümünde dosya yok.");

            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.StartsWith("Warden-Setup-", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var downloadUrl) || !IsAllowedDownloadUrl(downloadUrl))
                    return new(UpdateCheckStatus.Error, null, $"Beklenmeyen indirme adresi: {url}");

                string digest = a.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 7 + 64)
                    return new(UpdateCheckStatus.Error, null, "Setup için SHA256 özeti yok; güvenlik nedeniyle otomatik kurulmaz.");

                long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;
                var info = new UpdateInfo(latest, tag, name, downloadUrl, digest[7..].ToLowerInvariant(), size, releasePage);
                return new(UpdateCheckStatus.UpdateAvailable, info, $"Yeni sürüm: {tag}");
            }

            return new(UpdateCheckStatus.Error, null, $"{tag} sürümünde Warden-Setup-*.exe bulunamadı.");
        }

        /// <summary>
        /// Setup'ı indirir, SHA256'yı doğrular ve sessiz kurulumu başlatır; setup bitene kadar bekler.
        /// Başarılı kurulumda setup Warden'ı kapatır, yani bu metot normalde geri dönmez.
        /// Geri dönerse kurulum iptal edildi ya da başarısız oldu demektir (setup'ın çıkış kodu döner).
        /// </summary>
        public async Task<int> DownloadAndInstallAsync(UpdateInfo update, IProgress<double>? progress, Action<string> log,
                                                       Action onInstallerStarted, CancellationToken ct = default)
        {
            if (!IsAllowedDownloadUrl(update.DownloadUrl))
                throw new InvalidOperationException("İndirme adresi doğrulanamadı.");

            string dir = Path.Combine(Path.GetTempPath(), "Warden-Update");
            Directory.CreateDirectory(dir);
            // Rastgele ad + CreateNew: önceden konmuş bir dosyanın üzerine yazılmaz/kullanılmaz
            string path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(update.AssetName)}-{Guid.NewGuid():N}.exe");

            log($"[Update] Downloading {update.DownloadUrl} -> {path}");
            using (var resp = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? update.Size;

                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    readTotal += read;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)readTotal / total));
                }
            }

            // Doğrulama ve çalıştırma aynı kilit altında: FileShare.Read yazma/silme/yeniden adlandırmayı engeller,
            // böylece hash kontrolünden sonra (setup kendini açıp çıkarırken de) dosya değiştirilemez.
            using var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            string actual = Convert.ToHexString(await SHA256.HashDataAsync(lockStream, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (actual != update.Sha256)
            {
                log($"[Update] SHA256 mismatch! expected {update.Sha256}, got {actual}");
                lockStream.Dispose();
                TryDelete(path);
                throw new InvalidDataException("SHA256 mismatch");
            }
            log("[Update] SHA256 verified, starting silent setup.");

            // /SILENT: sihirbaz yok, yalnızca ilerleme penceresi. PawnIO görevi güncellemede zorlanmaz.
            // Setup çalışan Warden'ı kapatır ve kurulum bitince yeni sürümü başlatır ([Run] Check: WizardSilent).
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS=\"!pawnio\"",
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("Setup başlatılamadı.");

            onInstallerStarted();
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            int exitCode = proc.ExitCode;
            log($"[Update] Setup exited with code {exitCode} while Warden was still running (cancelled or failed).");
            lockStream.Dispose();
            TryDelete(path);
            return exitCode;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        /// <summary>Önceki güncellemelerden kalan setup dosyalarını temizler.</summary>
        public static void CleanupOldDownloads()
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "Warden-Update");
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, "Warden-Setup-*.exe"))
                    TryDelete(f);
            }
            catch { }
        }

        public void Dispose() => _http.Dispose();
    }
}
