using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Warden
{
    /// <summary>
    /// Warden açılışta Görev Zamanlayıcı ile "/rl highest" (UAC'siz yönetici) olarak başlatılır.
    /// Exe'nin bulunduğu klasör admin olmayan bir hesap tarafından yazılabiliyorsa, o kullanıcı
    /// yetkisiyle çalışan herhangi bir program Warden.exe'yi ya da yanındaki bir DLL'i değiştirip
    /// sonraki oturum açılışında yönetici olarak çalıştırabilir. Bu sınıf o durumu tespit eder.
    /// </summary>
    public static class InstallLocationGuard
    {
        // Klasöre yeni dosya eklemek (DLL yerleştirme), silmek/yeniden adlandırmak veya izinleri değiştirmek yeterlidir
        private const FileSystemRights DirectoryWriteRights =
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;

        private const FileSystemRights FileWriteRights =
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        // Genel (generic) haklar ACE'lerde ham bit olarak görünebilir: GENERIC_ALL / GENERIC_WRITE
        private const int GenericAll = 0x10000000;
        private const int GenericWrite = 0x40000000;

        private const int MaxFilesToCheck = 2000;

        /// <summary>
        /// Exe klasörü, üst klasörü veya klasördeki dosyalar admin olmayan bir hesap tarafından
        /// değiştirilebiliyorsa true döner. ACL okunamazsa güvenli tarafta kalınır (true).
        /// </summary>
        public static bool IsWritableByNonAdmins(string exePath, out string reason)
        {
            reason = "";
            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
                if (string.IsNullOrEmpty(dir))
                {
                    reason = "Exe klasörü belirlenemedi.";
                    return true;
                }

                var untrusted = GetUntrustedSids();

                var dirInfo = new DirectoryInfo(dir);
                DirectorySecurity dirSec = dirInfo.GetAccessControl();
                if (IsOwnedByUntrusted(dirSec, untrusted))
                {
                    reason = $"Klasörün sahibi standart kullanıcı: {dir}";
                    return true;
                }
                if (HasWriteAccess(dirSec, untrusted, DirectoryWriteRights))
                {
                    reason = $"Klasör standart kullanıcılar tarafından yazılabilir: {dir}";
                    return true;
                }

                // Üst klasörde "alt öğeleri sil" hakkı varsa Warden klasörü silinip yerine sahtesi konabilir
                DirectoryInfo? parent = dirInfo.Parent;
                if (parent != null &&
                    HasWriteAccess(parent.GetAccessControl(), untrusted, FileSystemRights.DeleteSubdirectoriesAndFiles))
                {
                    reason = $"Üst klasörde Warden klasörünü silme/değiştirme izni var: {parent.FullName}";
                    return true;
                }

                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false };
                int checkedCount = 0;
                foreach (string file in Directory.EnumerateFiles(dir, "*", options))
                {
                    if (++checkedCount > MaxFilesToCheck) break;
                    FileSecurity fileSec = new FileInfo(file).GetAccessControl();
                    if (IsOwnedByUntrusted(fileSec, untrusted) || HasWriteAccess(fileSec, untrusted, FileWriteRights))
                    {
                        reason = $"Dosya standart kullanıcılar tarafından değiştirilebilir: {file}";
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                reason = $"İzinler okunamadı ({ex.GetType().Name}: {ex.Message})";
                return true;
            }
        }

        /// <summary>
        /// Yönetici yetkisi olmadan da var olan kimlikler: Everyone, Authenticated Users, Users,
        /// Interactive ve (yükseltilmemiş haliyle saldırgan kodun da sahip olduğu) mevcut kullanıcının kendisi.
        /// </summary>
        private static HashSet<SecurityIdentifier> GetUntrustedSids()
        {
            var set = new HashSet<SecurityIdentifier>
            {
                new(WellKnownSidType.WorldSid, null),
                new(WellKnownSidType.AuthenticatedUserSid, null),
                new(WellKnownSidType.BuiltinUsersSid, null),
                new(WellKnownSidType.InteractiveSid, null)
            };

            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User != null) set.Add(identity.User);
            return set;
        }

        private static bool IsOwnedByUntrusted(FileSystemSecurity security, HashSet<SecurityIdentifier> untrusted)
        {
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner != null && untrusted.Contains(owner);
        }

        private static bool HasWriteAccess(FileSystemSecurity security, HashSet<SecurityIdentifier> untrusted, FileSystemRights dangerous)
        {
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                // Yalnızca alt öğelere miras kalan ACE nesnenin kendisine uygulanmaz
                if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
                if (rule.IdentityReference is not SecurityIdentifier sid || !untrusted.Contains(sid)) continue;

                int mask = (int)rule.FileSystemRights;
                if ((mask & (int)dangerous) != 0 || (mask & (GenericAll | GenericWrite)) != 0)
                    return true;
            }
            return false;
        }
    }
}
