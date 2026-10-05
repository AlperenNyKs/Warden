using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Warden
{
    /// <summary>
    /// Taranan oyunları SteelSeries GG'deki oyuna özel Sonar preset'leriyle isimden eşleştirir.
    /// GG preset'leri exe bilgisi taşımaz ("Valorant Pro Preset", "Destiny 2 by Bungie" ...), bu yüzden
    /// eşleşme normalize edilmiş adlar üzerinden yapılır. Eşleşme bulunamayan oyun için kural oluşturulmaz.
    /// </summary>
    public static class SonarPresetMatcher
    {
        // Oyuna ait olmayan genel preset'ler: hiçbir oyunla eşleşmemeli
        private static readonly string[] NonGamePrefixes = { "music:", "movie:" };
        private static readonly HashSet<string> NonGameNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "flat", "fps footsteps", "default"
        };

        // Preset adlarında ve mağaza adlarında farklı yazılan seriler (kelime sınırında, normalize sonrası)
        private static readonly (string Long, string Short)[] Abbreviations =
        {
            ("call of duty", "cod"),
            ("counter strike", "cs"),
            ("grand theft auto", "gta"),
        };

        private static readonly Regex PresetSuffix =
            new(@"\s+(pro preset|by\s+.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Oyun adı → preset ID eşleştirmesi. Yalnızca eşleşme bulunan oyunlar sonuçta yer alır.
        /// </summary>
        public static Dictionary<string, SonarConfig> MatchGames(IEnumerable<string> gameNames, IEnumerable<SonarConfig> presets)
        {
            var index = BuildIndex(presets);
            var result = new Dictionary<string, SonarConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in gameNames)
            {
                var match = FindMatch(name, index);
                if (match != null) result[name] = match;
            }
            return result;
        }

        internal static SonarConfig? FindMatch(string gameName, Dictionary<string, SonarConfig> index)
        {
            string key = Normalize(gameName);
            if (key.Length == 0) return null;

            // 1. Tam eşleşme
            if (index.TryGetValue(key, out var exact)) return exact;

            // Yıl eki taşıyan yeniden yayınlar ("Hunt: Showdown 1896"). Yalnızca 4 haneli sayı:
            // "Destiny 2" gibi devam oyunları ilk oyunun preset'ine düşmemeli.
            var yearSuffix = Regex.Match(key, @"^(.+) \d{4}$");
            if (yearSuffix.Success && index.TryGetValue(yearSuffix.Groups[1].Value, out var withoutYear))
                return withoutYear;

            // 2. Mağaza adı yayıncı/ön ek taşıyorsa ("Tom Clancy's Rainbow Six Siege", "Sid Meier's ...")
            //    preset adı oyun adının sonunda kelime sınırında geçmeli. Tek kelimelik preset'ler
            //    ("Rust", "Stray") yanlış eşleşmeye çok açık olduğu için bu adımda kullanılmaz.
            return index
                .Where(kv => kv.Key.Contains(' ') && key.EndsWith(" " + kv.Key, StringComparison.Ordinal))
                .OrderByDescending(kv => kv.Key.Length)
                .Select(kv => kv.Value)
                .FirstOrDefault();
        }

        internal static Dictionary<string, SonarConfig> BuildIndex(IEnumerable<SonarConfig> presets)
        {
            var index = new Dictionary<string, (SonarConfig Preset, int Rank)>(StringComparer.Ordinal);

            foreach (var p in presets)
            {
                if (p.virtualAudioDevice != "game" || string.IsNullOrWhiteSpace(p.name)) continue;

                string raw = p.name.Trim();
                if (NonGamePrefixes.Any(pre => raw.StartsWith(pre, StringComparison.OrdinalIgnoreCase))) continue;
                if (NonGameNames.Contains(raw)) continue;

                string stripped = PresetSuffix.Replace(raw, "");
                string key = Normalize(stripped);
                if (key.Length == 0) continue;

                // Aynı oyun için birden fazla preset olabilir ("Rainbow Six Siege" / "... by Ubisoft").
                // Öncelik: kullanıcının kendi preset'i > ek almayan ad > "Pro Preset" > "by ..."
                int rank = (p.isPreset ? 10 : 0) + (stripped.Length == raw.Length ? 0
                    : raw.EndsWith("pro preset", StringComparison.OrdinalIgnoreCase) ? 1 : 2);

                if (!index.TryGetValue(key, out var current) || rank < current.Rank)
                    index[key] = (p, rank);
            }

            return index.ToDictionary(kv => kv.Key, kv => kv.Value.Preset, StringComparer.Ordinal);
        }

        /// <summary>Küçük harf, yalnızca harf/rakam, tek boşluk; ™/® ve aksanlar atılır, kısaltmalar uygulanır.</summary>
        internal static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            string decomposed = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                if (c == '\'' || c == '’') continue;   // "Baldur's" → "baldurs"
                if (!char.IsLetterOrDigit(c)) { sb.Append(' '); continue; }

                // Harf/rakam geçişinde ayır: "CS2" ile "Counter-Strike 2" aynı biçime ("cs 2") gelsin
                if (sb.Length > 0 && char.IsLetterOrDigit(sb[^1]) && char.IsDigit(sb[^1]) != char.IsDigit(c))
                    sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
            }

            string result = " " + string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)) + " ";
            foreach (var (longForm, shortForm) in Abbreviations)
                result = result.Replace(" " + longForm + " ", " " + shortForm + " ");

            return result.Trim();
        }
    }
}
