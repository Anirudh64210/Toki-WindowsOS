using System.Runtime.InteropServices;

namespace Toki.Interop;

/// <summary>
/// Physical-RAM probe. Toki has two heavy models — the ~2.5GB notes LLM and the
/// ~630MB speech recognizer. On a machine with plenty of RAM they can both stay
/// resident; on a small machine (the dev PC is 6GB) keeping both loaded starves
/// transcription (native out-of-memory crash) and makes the whole PC clunky from
/// paging. This tells the app which world it's in.
/// </summary>
public static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    const double Bytes2GB = 1024.0 * 1024.0 * 1024.0;

    /// <summary>Total and currently-available physical RAM in GB. (0,0) if the query fails.</summary>
    public static (double totalGB, double availGB) Physical()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0);
        return (m.ullTotalPhys / Bytes2GB, m.ullAvailPhys / Bytes2GB);
    }
}
