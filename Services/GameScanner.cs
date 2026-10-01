using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace Warden
{
    public class DiscoveredGame
    {
        public string ExeName { get; set; } = "";
        public string GameName { get; set; } = "";
        public string Source { get; set; } = "";  // "Steam", "Epic", "GOG", "EA", "Xbox"
    }

    public static class GameScanner
    {
        public static List<DiscoveredGame> ScanAllGames()
        {
            var results = new Dictionary<string, DiscoveredGame>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in ScanSteam())       AddIfNew(results, g);
            foreach (var g in ScanEpicGames())   AddIfNew(results, g);
            foreach (var g in ScanGOG())         AddIfNew(results, g);
            foreach (var g in ScanEAApp())       AddIfNew(results, g);
            foreach (var g in ScanUbisoft())     AddIfNew(results, g);
            foreach (var g in ScanXboxApp())     AddIfNew(results, g);
            foreach (var g in ScanRiotGames())   AddIfNew(results, g);

            return results.Values
                          .OrderBy(g => g.GameName)
                          .ToList();
        }

        private static void AddIfNew(Dictionary<string, DiscoveredGame> dict, DiscoveredGame g)
        {
            if (!string.IsNullOrEmpty(g.ExeName) && !dict.ContainsKey(g.ExeName))
                dict[g.ExeName] = g;
        }

        // ─── Steam ────────────────────────────────────────────────────────────
        private static List<DiscoveredGame> ScanSteam()
        {
            var results = new List<DiscoveredGame>();
            try
            {
                // Find Steam installation
                string? steamPath = null;
                using var steamKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam") ??
                                     Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                if (steamKey != null)
                    steamPath = steamKey.GetValue("SteamPath") as string;

                if (string.IsNullOrEmpty(steamPath)) return results;

                // Read libraryfolders.vdf to get all library paths
                var libFolders = new List<string> { Path.Combine(steamPath, "steamapps") };
                string vdfPath = Path.Combine(steamPath, "config", "libraryfolders.vdf");
                if (!File.Exists(vdfPath))
                    vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");

                if (File.Exists(vdfPath))
                {
                    string content = File.ReadAllText(vdfPath);
                    // Parse VDF for "path" values
                    foreach (var line in content.Split('\n'))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                        {
                            string[] parts = trimmed.Split('"');
                            if (parts.Length >= 4)
                            {
                                string p = parts[3].Replace("\\\\", "\\");
                                string steamapps = Path.Combine(p, "steamapps");
                                if (Directory.Exists(steamapps) && !libFolders.Contains(steamapps, StringComparer.OrdinalIgnoreCase))
                                    libFolders.Add(steamapps);
                            }
                        }
                    }
                }

                // Scan each library for appmanifest_*.acf files
                foreach (string lib in libFolders)
                {
                    if (!Directory.Exists(lib)) continue;
                    foreach (string acf in Directory.GetFiles(lib, "appmanifest_*.acf"))
                    {
                        try
                        {
                            string acfContent = File.ReadAllText(acf);
                            string appId = ExtractVdfValue(acfContent, "appid");
                            string gameName = ExtractVdfValue(acfContent, "name");
                            string installDir = ExtractVdfValue(acfContent, "installdir");
                            if (string.IsNullOrEmpty(gameName) || string.IsNullOrEmpty(installDir)) continue;
                            if (IsSteamNonGame(appId, gameName)) continue;

                            string gameFolder = Path.Combine(lib, "common", installDir);
                            if (!Directory.Exists(gameFolder)) continue;

                            // Find main executable: pick the largest .exe at root level
                            string? mainExe = FindMainExe(gameFolder, gameName);
                            if (mainExe != null)
                            {
                                results.Add(new DiscoveredGame
                                {
                                    ExeName = mainExe,
                                    GameName = gameName,
                                    Source = "Steam"
                                });
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return results;
        }

        // ─── Epic Games ───────────────────────────────────────────────────────
        private static List<DiscoveredGame> ScanEpicGames()
        {
            var results = new List<DiscoveredGame>();
            try
            {
                string manifestsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Epic", "EpicGamesLauncher", "Data", "Manifests");

                if (!Directory.Exists(manifestsPath)) return results;

                foreach (string itemFile in Directory.GetFiles(manifestsPath, "*.item"))
                {
                    try
                    {
                        string json = File.ReadAllText(itemFile);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        string displayName = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() ?? "" : "";
                        string launchExe = root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() ?? "" : "";
                        string installLoc = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() ?? "" : "";

                        // Skip launchers
                        if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(launchExe)) continue;
                        if (displayName.Contains("Launcher", StringComparison.OrdinalIgnoreCase)) continue;
                        if (launchExe.Contains("Launcher", StringComparison.OrdinalIgnoreCase)) continue;

                        string exeName = Path.GetFileName(launchExe);
                        if (string.IsNullOrEmpty(exeName)) continue;

                        results.Add(new DiscoveredGame
                        {
                            ExeName = exeName,
                            GameName = displayName,
                            Source = "Epic"
                        });
                    }
                    catch { }
                }
            }
            catch { }
            return results;
        }

        // ─── GOG ──────────────────────────────────────────────────────────────
        private static List<DiscoveredGame> ScanGOG()
        {
            var results = new List<DiscoveredGame>();
            try
            {
                using var gogKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games") ??
                                   Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                if (gogKey == null) return results;

                foreach (string subKeyName in gogKey.GetSubKeyNames())
                {
                    try
                    {
                        using var gameKey = gogKey.OpenSubKey(subKeyName);
                        if (gameKey == null) continue;

                        string gameName = gameKey.GetValue("gameName") as string ?? gameKey.GetValue("GAMENAME") as string ?? "";
                        string exePath = gameKey.GetValue("exe") as string ?? gameKey.GetValue("EXE") as string ?? "";
                        string gamePath = gameKey.GetValue("path") as string ?? gameKey.GetValue("PATH") as string ?? "";

                        if (string.IsNullOrEmpty(gameName)) continue;

                        string? exeName = null;
                        if (!string.IsNullOrEmpty(exePath))
                            exeName = Path.GetFileName(exePath);
                        else if (!string.IsNullOrEmpty(gamePath) && Directory.Exists(gamePath))
                            exeName = FindMainExe(gamePath, gameName);

                        if (string.IsNullOrEmpty(exeName)) continue;

                        // Filter out launchers/installers
                        if (IsSystemExe(exeName)) continue;

                        results.Add(new DiscoveredGame
                        {
                            ExeName = exeName,
                            GameName = gameName,
                            Source = "GOG"
                        });
                    }
                    catch { }
                }
            }
            catch { }
            return results;
        }

        // ─── EA App / Origin ──────────────────────────────────────────────────
        private static List<DiscoveredGame> ScanEAApp()
        {
            var results = new List<DiscoveredGame>();
            try
            {
                // EA stores game manifests in ProgramData
                string eaManifests = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "EA", "EADesktop", "Manifests");

                string[] roots = { eaManifests,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Origin", "LocalContent") };

                foreach (string manifestDir in roots)
                {
                    if (!Directory.Exists(manifestDir)) continue;
                    foreach (string mf in Directory.GetFiles(manifestDir, "*.mfst", SearchOption.AllDirectories))
                    {
                        try
                        {
                            string content = File.ReadAllText(mf);
                            // MFST files use URL-encoded key=value pairs
                            // Look for &dipinstallpath= and &id=
                            string? installPath = ExtractQueryParam(content, "dipinstallpath");
                            string? gameId = ExtractQueryParam(content, "id");

                            if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath)) continue;

                            // gameId "Origin.OFR.50.0001234" gibi okunamaz bir kimlik → kurulum klasörü adı daha anlamlı
                            string folderName = GetFolderDisplayName(installPath);
                            string? exeName = FindMainExe(installPath, folderName);
                            if (string.IsNullOrEmpty(exeName)) continue;

                            results.Add(new DiscoveredGame
                            {
                                ExeName = exeName,
                                GameName = string.IsNullOrEmpty(folderName) ? (gameId ?? exeName) : folderName,
                                Source = "EA"
                            });
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return results;
        }

        // ─── Ubisoft Connect ──────────────────────────────────────────────────
        private static List<DiscoveredGame> ScanUbisoft()
        {
            var results = new List<DiscoveredGame>();
            try
            {
                using var ubisoftKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs") ??
                                       Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
                if (ubisoftKey == null) return results;

                foreach (string subKeyName in ubisoftKey.GetSubKeyNames())
                {
                    try
                    {
                        using var gameKey = ubisoftKey.OpenSubKey(subKeyName);
                        if (gameKey == null) continue;

                        string? installDir = gameKey.GetValue("InstallDir") as string;
                        if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) continue;

                        string folderName = GetFolderDisplayName(installDir);
                        string? exeName = FindMainExe(installDir, folderName);
                        if (string.IsNullOrEmpty(exeName)) continue;

                        results.Add(new DiscoveredGame
                        {
                            ExeName = exeName,
                            GameName = string.IsNullOrEmpty(folderName) ? Path.GetFileNameWithoutExtension(exeName) : folderName,
                            Source = "Ubisoft"
                        });
                    }
                    catch { }
                }
            }
            catch { }
            return results;
        }

        // ─── Xbox / Microsoft Store ───────────────────────────────────────────
        private static List<DiscoveredGame> ScanXboxApp()
        {
            var results = new List<DiscoveredGame>();
            try
            {
                // Xbox/GamePass games are typically in WindowsApps - we need to check registry
                using var xboxKey = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\GamingServices\PackageRepository\Root");
                if (xboxKey == null) return results;

                foreach (string subKeyName in xboxKey.GetSubKeyNames())
                {
                    try
                    {
                        using var gameKey = xboxKey.OpenSubKey(subKeyName);
                        if (gameKey == null) continue;

                        string? path = gameKey.GetValue("Root") as string;
                        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;

                        string? exeName = FindMainExe(path, "");
                        if (string.IsNullOrEmpty(exeName)) continue;

                        results.Add(new DiscoveredGame
                        {
                            ExeName = exeName,
                            GameName = Path.GetFileNameWithoutExtension(exeName),
                            Source = "Xbox"
                        });
                    }
                    catch { }
                }
            }
            catch { }
            return results;
        }

        // ─── Riot Games ───────────────────────────────────────────────────────
        private static List<DiscoveredGame> ScanRiotGames()
        {
            var results = new List<DiscoveredGame>();
            try
            {
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string riotMetadata = Path.Combine(programData, "Riot Games", "Metadata");
                if (Directory.Exists(riotMetadata))
                {
                    foreach (var dir in Directory.GetDirectories(riotMetadata))
                    {
                        string gameId = Path.GetFileName(dir);
                        string yamlPath = Path.Combine(dir, $"{gameId}.product_settings.yaml");
                        if (gameId.StartsWith("Riot Client", StringComparison.OrdinalIgnoreCase)) continue;
                        if (File.Exists(yamlPath))
                        try
                        {
                            string content = File.ReadAllText(yamlPath);
                            string installPath = "";
                            foreach (var line in content.Split('\n'))
                            {
                                if (line.Contains("product_install_full_path:"))
                                {
                                    installPath = line.Split(new[] { "product_install_full_path:" }, StringSplitOptions.None)[1].Trim().Trim('"', '\'');
                                    break;
                                }
                            }
                            if (Directory.Exists(installPath))
                            {
                                string displayName = gameId.Replace(".live", "").Replace("_", " ");
                                string? exeName = FindMainExe(installPath, displayName);
                                if (!string.IsNullOrEmpty(exeName))
                                {
                                    results.Add(new DiscoveredGame
                                    {
                                        ExeName = exeName,
                                        GameName = displayName,
                                        Source = "Riot Games"
                                    });
                                }
                            }
                        }
                        catch { /* Tek bir bozuk yaml tüm Riot taramasını durdurmasın */ }
                    }
                }
            }
            catch { }
            return results;
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        // Kurulum klasöründe oyun olmayan exe'lerin bulunduğu alt klasörler
        private static readonly HashSet<string> IgnoredSubfolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "_commonredist", "commonredist", "redist", "redists", "redistributable", "redistributables",
            "directx", "vcredist", "dotnet", "support", "installer", "installers", "__installer",
            "prereqs", "prerequisites", "easyanticheat", "battleye", "eac", "crashreporter", "tools"
        };

        /// <summary>
        /// Find the main game executable in a folder. Prefers an exe whose name matches the game name,
        /// otherwise the largest exe, filtering out common non-game executables and redist folders.
        /// </summary>
        private static string? FindMainExe(string folder, string gameName)
        {
            try
            {
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 4
                };

                var exes = Directory.EnumerateFiles(folder, "*.exe", options)
                    .Where(f => !IsSystemExe(Path.GetFileName(f)) && !IsInIgnoredSubfolder(folder, f))
                    .Select(f => (Path: f, Size: SafeLength(f)))
                    .OrderByDescending(x => x.Size)
                    .Select(x => x.Path)
                    .ToList();

                if (exes.Count == 0) return null;

                // If game name provided, try to find an exe that matches the name
                string cleanName = NormalizeForMatch(gameName);
                if (cleanName.Length >= 3)
                {
                    var nameMatch = exes.FirstOrDefault(e =>
                    {
                        string en = NormalizeForMatch(Path.GetFileNameWithoutExtension(e));
                        // Çok kısa exe adları ("a", "go") her oyun adının içinde geçebilir → en az 4 karakter şartı
                        return en.Length > 0 &&
                               (en.Contains(cleanName) || (en.Length >= 4 && cleanName.Contains(en)));
                    });

                    if (nameMatch != null) return Path.GetFileName(nameMatch);
                }

                return Path.GetFileName(exes[0]);
            }
            catch { return null; }
        }

        private static long SafeLength(string file)
        {
            try { return new FileInfo(file).Length; }
            catch { return 0; }
        }

        private static string NormalizeForMatch(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        private static bool IsInIgnoredSubfolder(string root, string file)
        {
            string? dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir)) return false;

            string relative = Path.GetRelativePath(root, dir);
            if (relative == ".") return false;

            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (IgnoredSubfolders.Contains(segment)) return true;
            }
            return false;
        }

        private static string GetFolderDisplayName(string path)
        {
            try
            {
                return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch { return ""; }
        }

        private static bool IsSteamNonGame(string appId, string name)
        {
            // 228980 = Steamworks Common Redistributables; Proton / Linux Runtime gibi araçlar da oyun değildir
            if (appId == "228980") return true;
            return name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("SteamVR", StringComparison.OrdinalIgnoreCase);
        }

        // Exe adının herhangi bir yerinde geçmesi güvenli olan (oyun adlarında pratikte geçmeyen) parçalar
        private static readonly string[] SystemExeSubstrings =
        {
            "unins", "setup", "install", "redist", "dxsetup", "oalinst", "prereq",
            "crashhandler", "crashreport", "crashpad", "crashsender", "bugreport", "errorreport",
            "easyanticheat", "battleye", "beservice", "anticheat", "launcher", "updater", "patcher",
            "helper", "physx", "dotnet", "cefprocess", "cefsharp", "qtwebengine", "webview",
            "benchmark", "configurator", "service", "daemon", "touchup", "cleanup", "repair",
            "diagnos", "mapeditor", "worldeditor", "leveleditor", "dedicated", "overlay", "inject",
            "notification", "uploader", "downloader"
        };

        // CamelCase kelime / ayraç parçası olarak tam eşleştiğinde eleme yapılan kelimeler.
        // Eskiden tüm liste alt-dize olarak aranıyordu ve gerçek oyunları eliyordu:
        // "origin" → ACOrigins.exe, "mod" → ModernWarfare.exe, "word" → Swordsman.exe,
        // "edge" → MirrorsEdge.exe, "eac" → Peacemaker.exe, "test" → TestDriveUnlimited.exe ...
        private static readonly HashSet<string> SystemExeTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "update", "patch", "report", "reporter", "support", "tool", "tools", "config",
            "uninstall", "debug", "compiler", "server", "editor", "sdk", "devkit", "mod", "mods",
            "cef", "qt5", "qt6", "xna", "eac", "msvc", "msvcp", "msvcr", "vcredist",
            "steamwebhelper", "galaxyclient", "eadesktop", "ubisoftconnect", "epicgames", "epicgameslauncher",
            "python", "pythonw", "java", "javaw", "jre", "jdk",
            "nvidia", "amd", "intel", "geforce", "radeon", "microsoft",
            "chrome", "firefox", "msedge", "opera", "browser",
            "discord", "slack", "skype", "telegram", "spotify", "vlc",
            "winword", "excel", "outlook", "onenote", "powerpnt", "adobe", "acrobat", "photoshop",
            "antivirus", "malware", "defender", "firewall",
            "7z", "7zg", "7zfm", "winrar", "unrar", "unzip", "notepad", "mspaint", "powershell", "conhost"
        };

        // Oyun adlarının içinde kelime olarak geçebilecek genel kelimeler: yalnızca exe adının tamamı
        // (veya '-', '_', ' ' ile ayrılmış bir parçası) bu kelimeyse elenir. Ör. "Steam.exe" elenir,
        // "SteamWorldDig.exe" / "CrashBandicoot.exe" / "AgentOfMayhem.exe" elenmez.
        private static readonly HashSet<string> SystemExeExactNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "origin", "gog", "upc", "ubisoft", "crash", "test", "build", "compile", "register",
            "remove", "repair", "hook", "agent", "tray", "node", "ruby", "brave", "teams", "zoom",
            "media", "player", "photo", "image", "office", "archive", "zip", "calc", "cmd", "vc", "be"
        };

        private static readonly System.Text.RegularExpressions.Regex TokenRegex =
            new(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static bool IsSystemExe(string exeName)
        {
            string baseName = Path.GetFileNameWithoutExtension(exeName);
            string lower = baseName.ToLowerInvariant();

            foreach (var part in SystemExeSubstrings)
            {
                if (lower.Contains(part)) return true;
            }

            if (SystemExeTokens.Contains(lower) || SystemExeExactNames.Contains(lower)) return true;

            foreach (var piece in baseName.Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (SystemExeTokens.Contains(piece) || SystemExeExactNames.Contains(piece)) return true;

                foreach (System.Text.RegularExpressions.Match m in TokenRegex.Matches(piece))
                {
                    if (SystemExeTokens.Contains(m.Value)) return true;
                }
            }

            return false;
        }

        private static string ExtractVdfValue(string content, string key)
        {
            string search = $"\"{key}\"";
            int idx = content.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            int valStart = content.IndexOf('"', idx + search.Length);
            if (valStart < 0) return "";
            int valEnd = content.IndexOf('"', valStart + 1);
            if (valEnd < 0) return "";
            return content.Substring(valStart + 1, valEnd - valStart - 1);
        }

        private static string? ExtractQueryParam(string content, string param)
        {
            string search = param + "=";
            int idx = content.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int start = idx + search.Length;
            int end = content.IndexOf('&', start);
            string val = end < 0 ? content.Substring(start) : content.Substring(start, end - start);
            return Uri.UnescapeDataString(val.Trim());
        }
    }
}
