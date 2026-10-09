using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Warden
{
    /// <summary>
    /// Benchmark kayıtlarını %AppData%\Warden\benchmarks altında tutar: her kayıt için {id}.json (bilgiler + saniyelik
    /// örnekler) ve {id}.ft (kare süreleri, float32 dizisi). UI thread'inden kullanılır.
    /// </summary>
    public sealed class BenchmarkStore
    {
        private readonly string _folder;

        public BenchmarkStore(string folder)
        {
            _folder = folder;
        }

        public void Save(BenchmarkRun run)
        {
            Directory.CreateDirectory(_folder);
            WriteAtomic(JsonPath(run.Id), JsonSerializer.SerializeToUtf8Bytes(run));

            var bytes = new byte[run.FrameTimesMs.Length * sizeof(float)];
            Buffer.BlockCopy(run.FrameTimesMs, 0, bytes, 0, bytes.Length);
            WriteAtomic(FramesPath(run.Id), bytes);
        }

        /// <summary>Tüm kayıtlar, en yeni önce. Okunamayan dosyalar atlanır.</summary>
        public List<BenchmarkRun> LoadAll()
        {
            var runs = new List<BenchmarkRun>();
            if (!Directory.Exists(_folder)) return runs;

            foreach (var json in Directory.EnumerateFiles(_folder, "*.json"))
            {
                try
                {
                    var run = JsonSerializer.Deserialize<BenchmarkRun>(File.ReadAllBytes(json));
                    if (run == null || string.IsNullOrEmpty(run.Id)) continue;
                    string ft = FramesPath(run.Id);
                    if (File.Exists(ft))
                    {
                        var bytes = File.ReadAllBytes(ft);
                        var frames = new float[bytes.Length / sizeof(float)];
                        Buffer.BlockCopy(bytes, 0, frames, 0, frames.Length * sizeof(float));
                        run.FrameTimesMs = frames;
                    }
                    runs.Add(run);
                }
                catch { /* bozuk kayıt diğerlerini engellemesin */ }
            }
            return runs.OrderByDescending(r => r.StartUtc).ToList();
        }

        /// <summary>Kayıt sayısı (otomatik "Kayıt N" etiketi için; dosyaları okumaz).</summary>
        public int Count() => Directory.Exists(_folder) ? Directory.EnumerateFiles(_folder, "*.json").Count() : 0;

        public void Rename(BenchmarkRun run, string label)
        {
            run.Label = label;
            UpdateInfo(run);
        }

        /// <summary>Etiket / turlar değişince yalnızca JSON yeniden yazılır (kare süreleri değişmez).</summary>
        public void UpdateInfo(BenchmarkRun run)
            => WriteAtomic(JsonPath(run.Id), JsonSerializer.SerializeToUtf8Bytes(run));

        public void Delete(string id)
        {
            File.Delete(JsonPath(id));
            File.Delete(FramesPath(id));
        }

        private string JsonPath(string id) => Path.Combine(_folder, id + ".json");
        private string FramesPath(string id) => Path.Combine(_folder, id + ".ft");

        private static void WriteAtomic(string path, byte[] bytes)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
