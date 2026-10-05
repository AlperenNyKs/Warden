using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Warden
{
    public class AfterburnerService
    {
        private const string ExeName = "MSIAfterburner.exe";

        // Tespit pahalı değil ama her profil uygulamasında / durum sayfasında kayıt defteri taranmasın
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);
        private string? _cachedPath;
        private DateTime _cachedAtUtc = DateTime.MinValue;
        private readonly object _cacheLock = new();

        /// <summary>
        /// MSIAfterburner.exe'yi bulur: 1) kaldırma kayıtları (32/64 bit, HKLM/HKCU) 2) çalışan süreç
        /// 3) varsayılan Program Files klasörleri. Eskiden yalnızca 3. adım vardı; farklı klasöre kuranlarda çalışmıyordu.
        /// </summary>
        public string? FindExecutable()
        {
            lock (_cacheLock)
            {
                if (DateTime.UtcNow - _cachedAtUtc < CacheDuration) return _cachedPath;
                _cachedPath = Locate();
                _cachedAtUtc = DateTime.UtcNow;
                return _cachedPath;
            }
        }

        public bool IsInstalled() => FindExecutable() != null;

        private static string? Locate()
        {
            foreach (var dir in RegistryInstallDirs())
            {
                string candidate = Path.Combine(dir, ExeName);
                if (File.Exists(candidate)) return candidate;
            }

            try
            {
                foreach (var p in Process.GetProcessesByName("MSIAfterburner"))
                {
                    using (p)
                    {
                        try
                        {
                            string? path = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                        }
                        catch { /* erişim reddedildi */ }
                    }
                }
            }
            catch { }

            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                string root = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(root)) continue;
                string candidate = Path.Combine(root, "MSI Afterburner", ExeName);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static IEnumerable<string> RegistryInstallDirs()
        {
            const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            var results = new List<string>();

            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var root = baseKey.OpenSubKey(uninstall);
                    if (root == null) continue;

                    foreach (string name in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var app = root.OpenSubKey(name);
                            string display = app?.GetValue("DisplayName") as string ?? "";
                            if (!display.Contains("Afterburner", StringComparison.OrdinalIgnoreCase) &&
                                !name.Contains("Afterburner", StringComparison.OrdinalIgnoreCase))
                                continue;

                            foreach (var value in new[] { "InstallLocation", "DisplayIcon", "UninstallString" })
                            {
                                string? dir = DirectoryFromRegistryValue(app?.GetValue(value) as string);
                                if (dir != null && !results.Contains(dir, StringComparer.OrdinalIgnoreCase))
                                    results.Add(dir);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return results;
        }

        /// <summary>
        /// Kayıt defteri değerinden klasör çıkarır: klasör yolu, "C:\..\x.exe,0" (ikon) veya
        /// "\"C:\..\uninstall.exe\" /S" (komut satırı) biçimlerini destekler.
        /// </summary>
        public static string? DirectoryFromRegistryValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim();

            if (v.StartsWith('"'))
            {
                int end = v.IndexOf('"', 1);
                v = end > 1 ? v.Substring(1, end - 1) : v.Trim('"');
            }
            else
            {
                int exe = v.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exe >= 0) v = v[..(exe + 4)];
            }

            int comma = v.LastIndexOf(',');
            if (comma > 0 && int.TryParse(v[(comma + 1)..].Trim(), out _)) v = v[..comma];

            v = v.Trim().TrimEnd('\\', '/');
            if (v.Length == 0) return null;

            try
            {
                return v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(v) : v;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Afterburner'a profil uygulama komutu gönderir.
        /// Başarılıysa true döner; Afterburner yüklü değilse veya başlatılamazsa false.
        /// </summary>
        public bool ApplyProfile(int profileNumber)
        {
            string? exe = FindExecutable();
            if (exe == null) return false;
            if (profileNumber < 1 || profileNumber > 5) return false;

            try
            {
                using var _ = Process.Start(new ProcessStartInfo
                {
                    FileName        = exe,
                    Arguments       = $"-Profile{profileNumber}",
                    UseShellExecute = true,
                    WindowStyle     = ProcessWindowStyle.Hidden
                });
                return true;
            }
            catch
            {
                return false; // Afterburner başlatılamadı
            }
        }
    }
}
