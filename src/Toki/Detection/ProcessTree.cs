using System.Runtime.InteropServices;

namespace Toki.Detection;

/// <summary>Parent/child process relationships, via a Toolhelp snapshot.</summary>
public static class ProcessTree
{
    /// <summary>Map of process id → parent process id for all running processes.</summary>
    public static Dictionary<uint, uint> ParentMap()
    {
        var map = new Dictionary<uint, uint>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandle) return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do { map[entry.th32ProcessID] = entry.th32ParentProcessID; }
                while (Process32Next(snapshot, ref entry));
            }
        }
        finally { CloseHandle(snapshot); }
        return map;
    }

    /// <summary>
    /// Given several PIDs belonging to one app, return the tree root — the PID
    /// whose parent isn't itself one of the app's PIDs. Tapping the root with
    /// INCLUDE_TARGET_PROCESS_TREE captures children (e.g. a browser's audio renderer).
    /// </summary>
    public static uint RootOf(IReadOnlyCollection<uint> appPids, Dictionary<uint, uint> parents)
    {
        var set = appPids.ToHashSet();
        foreach (var pid in appPids)
        {
            if (!parents.TryGetValue(pid, out var parent) || !set.Contains(parent))
                return pid; // its parent is outside the app → this is a root
        }
        return appPids.First();
    }

    const uint TH32CS_SNAPPROCESS = 0x00000002;
    static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll")]
    static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll")]
    static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
