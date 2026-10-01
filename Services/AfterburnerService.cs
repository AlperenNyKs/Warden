using System;
using System.Diagnostics;
using System.IO;

namespace Warden
{
    public class AfterburnerService
    {
        private const string AfterburnerPath = @"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe";

        public bool IsInstalled() => File.Exists(AfterburnerPath);

        public void ApplyProfile(int profileNumber)
        {
            if (!IsInstalled()) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName       = AfterburnerPath,
                    Arguments      = $"-Profile{profileNumber}",
                    UseShellExecute = true,
                    WindowStyle    = ProcessWindowStyle.Hidden
                });
            }
            catch { /* Afterburner başlatılamadı */ }
        }
    }
}
