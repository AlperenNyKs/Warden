using System;
using System.Collections.Generic;
using System.Linq;

namespace Warden
{
    /// <summary>Bir oyun taramasının config'e etkisi (oyun adlarıyla, bildirimde göstermek için).</summary>
    public class GameSyncResult
    {
        public List<string> Added { get; } = new();
        public List<string> Assigned { get; } = new();
        public List<string> Removed { get; } = new();

        public bool HasChanges => Added.Count > 0 || Assigned.Count > 0 || Removed.Count > 0;
    }

    /// <summary>
    /// Tarama sonucunu config'e işler: kaldırılan oyunları siler, yeni oyunları listeye ekler ve
    /// (ayar açıksa) GG'de oyuna özel profili olan oyunlara kural atar. Hem "Tara" butonu hem açılış taraması kullanır.
    /// </summary>
    public static class GameLibrarySync
    {
        /// <param name="presets">GG'nin preset listesi; GG'ye ulaşılamadıysa null (profil atanmaz).</param>
        public static GameSyncResult Apply(AppConfig cfg, IReadOnlyList<DiscoveredGame> games, IReadOnlyList<SonarConfig>? presets)
        {
            var result = new GameSyncResult();

            // Silinen oyunların adı bildirim için silinmeden önce alınır
            var namesBefore = new Dictionary<string, string>(cfg.DiscoveredGameNames, StringComparer.OrdinalIgnoreCase);
            foreach (var exe in cfg.PruneUninstalledGames(games.Select(g => g.ExeName)))
                result.Removed.Add(namesBefore.TryGetValue(exe, out var n) && !string.IsNullOrEmpty(n) ? n : exe);

            foreach (var g in games)
            {
                if (!cfg.DiscoveredGames.Contains(g.ExeName, StringComparer.OrdinalIgnoreCase))
                {
                    cfg.DiscoveredGames.Add(g.ExeName);
                    result.Added.Add(string.IsNullOrEmpty(g.GameName) ? g.ExeName : g.GameName);
                }
                if (!string.IsNullOrEmpty(g.GameName))
                    cfg.DiscoveredGameNames[g.ExeName] = g.GameName;
            }

            if (presets == null || !cfg.AutoAssignGgPresets) return result;

            // Kuralı olan oyunlara ve kullanıcının kuralını sildiği (daha önce atanmış) oyunlara dokunulmaz.
            // Eşleşmeyenler her taramada yeniden denenir: GG sonradan profil eklerse yakalanır.
            var candidates = games.Where(g => !string.IsNullOrEmpty(g.GameName) &&
                                              !cfg.Rules.ContainsKey(g.ExeName) &&
                                              !cfg.GgAutoAssigned.Contains(g.ExeName, StringComparer.OrdinalIgnoreCase))
                                  .ToList();
            var matches = SonarPresetMatcher.MatchGames(candidates.Select(g => g.GameName), presets);
            foreach (var g in candidates)
            {
                if (!matches.TryGetValue(g.GameName, out var preset)) continue;
                cfg.Rules[g.ExeName] = preset.id;
                cfg.GgAutoAssigned.Add(g.ExeName);
                result.Assigned.Add(g.GameName);
            }

            return result;
        }
    }
}
