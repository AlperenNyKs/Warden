using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Warden
{
    public class SteelSeriesClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private string? _sonarAddress;

        // FileSystemWatcher: GG güncellenince coreProps.json değişir → adresi sıfırla
        private FileSystemWatcher? _corePropsWatcher;
        private CancellationTokenSource? _debounceCts;

        private const int RetryCount = 3;
        private const int BaseRetryDelayMs = 1000; // 1s, 2s (exponential)

        public SteelSeriesClient()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            // GG yüklüyse coreProps.json'ı izlemeye başla
            StartWatchingCoreProps();
        }

        // ── FileSystemWatcher ────────────────────────────────────────────

        private void StartWatchingCoreProps()
        {
            try
            {
                string path = GetCorePropsPath();
                string dir  = Path.GetDirectoryName(path)!;
                string file = Path.GetFileName(path);

                _corePropsWatcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                _corePropsWatcher.Changed += OnCorePropsChanged;
            }
            catch { /* GG yüklü değil veya yol bulunamadı — görmezden gel */ }
        }

        private void OnCorePropsChanged(object sender, FileSystemEventArgs e)
        {
            // GG güncelleme sırasında dosyayı birden fazla kez yazabilir → debounce
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            Task.Delay(2000, token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                    ResetAddress();
            }, TaskContinuationOptions.None);
        }

        // ── Core Props Path ──────────────────────────────────────────────

        public string GetCorePropsPath()
        {
            string[] possiblePaths =
            {
                @"C:\ProgramData\SteelSeries\GG\coreProps.json",
                @"C:\ProgramData\SteelSeries\SteelSeries Engine 3\coreProps.json"
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                    return path;
            }

            throw new FileNotFoundException("SteelSeries coreProps.json file not found in ProgramData paths.");
        }

        public void ResetAddress()
        {
            _sonarAddress = null;
        }

        // ── Address Resolution (Retry + Exponential Backoff) ─────────────

        public async Task<string> GetSonarAddressAsync()
        {
            if (!string.IsNullOrEmpty(_sonarAddress))
                return _sonarAddress;

            Exception? lastException = null;

            for (int attempt = 0; attempt < RetryCount; attempt++)
            {
                if (attempt > 0)
                {
                    // Exponential backoff: 1s, 2s
                    int delayMs = BaseRetryDelayMs * (int)Math.Pow(2, attempt - 1);
                    await Task.Delay(delayMs);
                }

                try
                {
                    await ReadSonarAddressAsync();
                    return _sonarAddress!;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _sonarAddress = null;
                }
            }

            throw new Exception(
                $"SteelSeries GG'ye {RetryCount} denemede bağlanılamadı. Son hata: {lastException?.Message}",
                lastException);
        }

        /// <summary>
        /// coreProps.json → /subApps → webServerAddress zincirini gerçekleştirir.
        /// Başarılı olursa _sonarAddress'i doldurur.
        /// </summary>
        private async Task ReadSonarAddressAsync()
        {
            string corePropsPath = GetCorePropsPath();
            string jsonContent   = await File.ReadAllTextAsync(corePropsPath);

            using var doc  = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            string ggAddress = "";
            if (root.TryGetProperty("ggEncryptedAddress", out var ggAddrProp))
                ggAddress = ggAddrProp.GetString() ?? "";
            else if (root.TryGetProperty("address", out var addrProp))
                ggAddress = addrProp.GetString() ?? "";

            if (string.IsNullOrEmpty(ggAddress))
                throw new Exception("coreProps.json içinde geçerli bir API adresi bulunamadı.");

            string subAppsUrl = $"https://{ggAddress}/subApps";
            HttpResponseMessage response = await _httpClient.GetAsync(subAppsUrl);
            response.EnsureSuccessStatusCode();

            string subAppsJson = await response.Content.ReadAsStringAsync();
            using var subAppsDoc  = JsonDocument.Parse(subAppsJson);
            var subAppsRoot = subAppsDoc.RootElement;

            JsonElement sonarMetadata;
            if (subAppsRoot.TryGetProperty("subApps", out var subAppsElement) &&
                subAppsElement.TryGetProperty("sonar", out var sonarElement) &&
                sonarElement.TryGetProperty("metadata", out var metadataElement))
            {
                sonarMetadata = metadataElement;
            }
            else if (subAppsRoot.TryGetProperty("sonar", out var directSonarElement) &&
                     directSonarElement.TryGetProperty("metadata", out var directMetadataElement))
            {
                sonarMetadata = directMetadataElement;
            }
            else
            {
                throw new Exception("subApps yanıtında Sonar metadata bulunamadı.");
            }

            if (sonarMetadata.TryGetProperty("webServerAddress", out var webServerAddressProp))
            {
                string webAddr = webServerAddressProp.GetString() ?? "";
                if (!webAddr.StartsWith("http://") && !webAddr.StartsWith("https://"))
                    webAddr = $"http://{webAddr}";
                _sonarAddress = webAddr;
                return;
            }

            throw new Exception("Sonar metadata içinde webServerAddress bulunamadı.");
        }

        // ── API Calls ────────────────────────────────────────────────────

        public async Task<string> GetPresetsJsonAsync()
        {
            string baseAddress = await GetSonarAddressAsync();
            string url = $"{baseAddress}/configs";

            HttpResponseMessage response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync();
        }

        public async Task<bool> SetPresetAsync(string presetId)
        {
            string baseAddress = await GetSonarAddressAsync();
            string url = $"{baseAddress}/configs/{presetId}/select";

            var content = new StringContent("", Encoding.UTF8, "application/json");

            try
            {
                HttpResponseMessage response = await _httpClient.PutAsync(url, content);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                // Bağlantı hatası: cache'i temizle, bir sonraki çağrıda yeniden çözümle
                _sonarAddress = null;
                throw;
            }
        }

        public async Task<System.Collections.Generic.List<SonarConfig>> GetConfigsAsync()
        {
            string json = await GetPresetsJsonAsync();
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var list = new System.Collections.Generic.List<SonarConfig>();

            JsonElement array = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("presets", out var p)) array = p;
                else if (root.TryGetProperty("configs", out var c)) array = c;
            }

            if (array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    string id     = "";
                    string name   = "";
                    string device = "";

                    if (item.TryGetProperty("id",   out var idProp))   id   = idProp.GetString()   ?? "";
                    if (item.TryGetProperty("name", out var nameProp)) name = nameProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(id) && item.TryGetProperty("uuid", out var uuidProp))
                        id = uuidProp.GetString() ?? "";
                    if (item.TryGetProperty("virtualAudioDevice", out var devProp))
                        device = devProp.GetString() ?? "";

                    list.Add(new SonarConfig { id = id, name = name, virtualAudioDevice = device });
                }
            }
            return list;
        }

        // ── IDisposable ───────────────────────────────────────────────────

        public void Dispose()
        {
            _corePropsWatcher?.Dispose();
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _httpClient.Dispose();
        }
    }

    public class SonarConfig
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string virtualAudioDevice { get; set; } = "";
    }
}
