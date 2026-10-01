using System;
using System.Diagnostics;
using System.IO;

namespace Warden
{
    public class AfterburnerService
    {
        // Program Files (x86) yolu sabit "C:\" yerine sistemden alınır (farklı sistem sürücüsü / dil desteği)
        private static readonly string[] CandidatePaths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MSI Afterburner", "MSIAfterburner.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MSI Afterburner", "MSIAfterburner.exe")
        };

        private static string? FindExecutable()
        {
            foreach (var path in CandidatePaths)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
            return null;
        }

        public bool IsInstalled() => FindExecutable() != null;

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
