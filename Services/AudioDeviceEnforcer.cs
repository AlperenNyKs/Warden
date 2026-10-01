using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace Warden
{
    public static class AudioDeviceEnforcer
    {
        private static readonly Guid ClsidPolicyConfig = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

        [Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr ppFormat);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bDefault, out IntPtr ppFormat);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bDefault, out long pmftDefaultPeriod, out long pmftMinimumPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ref long pmftPeriod);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr pMode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, out IntPtr pv);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bVisible);
        }

        public static bool SetDeviceState(string deviceId, bool disable)
        {
            bool visible = !disable;
            object? comObj = null;
            try
            {
                Type? type = Type.GetTypeFromCLSID(ClsidPolicyConfig);
                if (type == null) return false;

                comObj = Activator.CreateInstance(type);
                if (comObj is IPolicyConfig policy)
                {
                    int hr = policy.SetEndpointVisibility(deviceId, visible);
                    Debug.WriteLine($"[AudioEnforcer] Device {deviceId} visibility set to {visible}. Result: 0x{hr:X8}");
                    return hr == 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioEnforcer/Error] {ex.Message}");
            }
            finally
            {
                if (comObj != null && Marshal.IsComObject(comObj))
                {
                    Marshal.ReleaseComObject(comObj);
                }
            }
            return false;
        }

        public static void ApplyDisabledDevices(List<string> disabledDeviceIds)
        {
            if (disabledDeviceIds == null || disabledDeviceIds.Count == 0) return;
            foreach (var id in disabledDeviceIds)
            {
                SetDeviceState(id, disable: true);
            }
        }

        /// <summary>
        /// Hem ID hem de Cihaz Adı (FriendlyName) bazında eşleşen aktif cihazları otomatik devre dışı bırakır.
        /// SteelSeries GG güncellendiğinde ID (GUID) değişse bile isim üzerinden yakalar ve devre dışı bırakır.
        /// </summary>
        public static void EnforceDisabledDevices(List<string> disabledDeviceIds, List<string> disabledDeviceNames)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var activeDevices = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);

                var idSet = new HashSet<string>(disabledDeviceIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                var nameSet = new HashSet<string>(disabledDeviceNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

                foreach (var device in activeDevices)
                {
                    // MMDevice COM nesnesi tutar; her 30 sn'lik denetimde sızıntı olmaması için dispose edilir
                    using (device)
                    {
                        string id = device.ID;
                        string name = SafeFriendlyName(device);

                        bool shouldDisable = idSet.Contains(id) || MatchesAnyName(name, nameSet);
                        if (shouldDisable)
                        {
                            Debug.WriteLine($"[AudioEnforcer/AutoEnforce] Disabling active device: {name} ({id})");
                            SetDeviceState(id, disable: true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioEnforcer/AutoEnforce/Error] {ex.Message}");
            }
        }

        public static bool MatchesAnyName(string deviceName, IEnumerable<string> targetNames)
        {
            if (string.IsNullOrWhiteSpace(deviceName) || targetNames == null) return false;
            string cleanDeviceName = CleanDeviceName(deviceName);

            foreach (var target in targetNames)
            {
                if (string.IsNullOrWhiteSpace(target)) continue;
                string cleanTarget = CleanDeviceName(target);

                if (cleanDeviceName.Equals(cleanTarget, StringComparison.OrdinalIgnoreCase))
                    return true;

                // SteelSeries sanal aygıtları için akıllı kısmi eşleşme:
                // Örn: hedefte "SteelSeries Sonar - Chat" varsa ve yeni aygıt adı da bunu içeriyorsa yakala
                if (cleanTarget.StartsWith("SteelSeries Sonar -", StringComparison.OrdinalIgnoreCase))
                {
                    int hyphenIndex = cleanTarget.IndexOf('-');
                    string channelPart = cleanTarget.Substring(hyphenIndex + 1).Trim();
                    int parenIndex = channelPart.IndexOf('(');
                    if (parenIndex > 0) channelPart = channelPart.Substring(0, parenIndex).Trim();

                    if (!string.IsNullOrEmpty(channelPart) &&
                        cleanDeviceName.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase) &&
                        cleanDeviceName.Contains(channelPart, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Çıkarılmış (Unplugged) bazı aygıtlarda özellik deposu okunamaz ve FriendlyName exception fırlatır;
        /// bu durumda tüm liste boş dönmesin diye aygıt ID'si gösterilir.
        /// </summary>
        private static string SafeFriendlyName(MMDevice device)
        {
            try { return device.FriendlyName; }
            catch { return device.ID; }
        }

        public static string CleanDeviceName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Replace("🔊", "").Replace("🎤", "").Trim();
        }

        /// <summary>
        /// Returns all audio endpoints (Playback + Recording).
        /// isRender=true → Playback (🔊), isRender=false → Recording (🎤)
        /// </summary>
        public static List<(string Id, string FriendlyName, bool IsRender)> GetAllAudioDevices()
        {
            var result = new List<(string, string, bool)>();
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var states = DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged;

                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, states))
                {
                    using (d)
                    {
                        if (seen.Add(d.ID))
                            result.Add((d.ID, SafeFriendlyName(d), true));
                    }
                }

                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, states))
                {
                    using (d)
                    {
                        if (seen.Add(d.ID))
                            result.Add((d.ID, SafeFriendlyName(d), false));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioEnforcer/List] {ex.Message}");
            }

            return result;
        }
    }
}
