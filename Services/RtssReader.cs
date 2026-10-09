using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Warden
{
    /// <summary>RTSS'in bir 3D uygulama için tuttuğu anlık durum.</summary>
    public sealed record RtssApp(int ProcessId, string Name, uint FrameTimeBufPos, uint[] FrameTimeBuf, uint CurrentFrameTimeUs,
                                 uint Time0Ms, uint Time1Ms, uint Frames)
    {
        /// <summary>RTSS'in yaklaşık saniyede bir güncellediği FPS (tampon kullanılamazsa yedek).</summary>
        public double? PeriodFps => Time1Ms > Time0Ms && Frames > 0 ? 1000.0 * Frames / (Time1Ms - Time0Ms) : null;
    }

    /// <summary>
    /// RivaTuner Statistics Server'ın paylaşılan belleğini ("RTSSSharedMemoryV2") okur. Yalnızca okuma yapılır.
    /// Alan konumları RTSS SDK'daki RTSSSharedMemory.h (v2.x) ile aynıdır; uygulama girdisindeki tüm alanlar 4 baytlık
    /// sayı veya char[260] olduğundan hizalama boşluğu yoktur.
    /// </summary>
    public static class RtssReader
    {
        private const string MapName = "RTSSSharedMemoryV2";
        private const uint Signature = 0x52545353;   // 'RTSS'

        // RTSS_SHARED_MEMORY başlığı
        private const int HeaderVersion = 4, HeaderAppEntrySize = 8, HeaderAppArrOffset = 12, HeaderAppArrSize = 16;
        private const int HeaderLastForegroundPid = 68;

        // RTSS_SHARED_MEMORY_APP_ENTRY
        private const int EntryProcessId = 0, EntryName = 4, NameLength = 260;
        private const int EntryTime0 = 268, EntryTime1 = 272, EntryFrames = 276;   // saniyelik FPS sayacı (ms)
        private const int EntryFrameTime = 280;          // dwFrameTime (mikrosaniye)
        private const int EntryFrameTimeBuf = 924;       // dwStatFrameTimeBuf[1024] (v2.5+)
        private const int EntryFrameTimeBufPos = 5020;   // dwStatFrameTimeBufPos
        public const int FrameTimeBufLength = 1024;
        private const uint MinVersionWithBuffer = 0x00020005;

        /// <summary>RTSS çalışmıyorsa veya bellek henüz hazır değilse boş liste döner.</summary>
        public static List<RtssApp> ReadApps()
        {
            var apps = new List<RtssApp>();
            try
            {
                using var map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                if (view.ReadUInt32(0) != Signature) return apps;
                uint version = view.ReadUInt32(HeaderVersion);
                int entrySize = (int)view.ReadUInt32(HeaderAppEntrySize);
                long arrOffset = view.ReadUInt32(HeaderAppArrOffset);
                int arrSize = (int)view.ReadUInt32(HeaderAppArrSize);
                bool hasBuffer = version >= MinVersionWithBuffer && entrySize >= EntryFrameTimeBufPos + 4;

                var nameBytes = new byte[NameLength];
                for (int i = 0; i < arrSize; i++)
                {
                    long entry = arrOffset + (long)i * entrySize;
                    int pid = view.ReadInt32(entry + EntryProcessId);
                    if (pid == 0) continue;

                    view.ReadArray(entry + EntryName, nameBytes, 0, NameLength);
                    int len = Array.IndexOf(nameBytes, (byte)0);
                    string name = Encoding.Default.GetString(nameBytes, 0, len < 0 ? NameLength : len);

                    uint[] buf = Array.Empty<uint>();
                    uint pos = 0;
                    if (hasBuffer)
                    {
                        buf = new uint[FrameTimeBufLength];
                        view.ReadArray(entry + EntryFrameTimeBuf, buf, 0, FrameTimeBufLength);
                        pos = view.ReadUInt32(entry + EntryFrameTimeBufPos);
                    }
                    apps.Add(new RtssApp(pid, name, pos, buf, view.ReadUInt32(entry + EntryFrameTime),
                                         view.ReadUInt32(entry + EntryTime0), view.ReadUInt32(entry + EntryTime1),
                                         view.ReadUInt32(entry + EntryFrames)));
                }
            }
            catch (FileNotFoundException) { }      // RTSS çalışmıyor
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            return apps;
        }

        /// <summary>RTSS'in en son öne gelen 3D uygulama olarak gördüğü işlem (yoksa 0).</summary>
        public static int LastForegroundProcessId()
        {
            try
            {
                using var map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                return view.ReadUInt32(0) == Signature ? view.ReadInt32(HeaderLastForegroundPid) : 0;
            }
            catch { return 0; }
        }
    }
}
