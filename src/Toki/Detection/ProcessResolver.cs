using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Toki.Detection;

/// <summary>Maps a detected mic-using app to a running process id for loopback capture.</summary>
public static class ProcessResolver
{
    /// <summary>Best-effort PID for the app's process tree root; null if it can't be found.</summary>
    public static uint? FindPid(DetectedApp app)
    {
        // Packaged (Store) apps: the consent-store key IS the package family name, so ask
        // Windows which processes belong to that package. Guessing a process name from the
        // AUMID prefix fails when they differ (e.g. new Teams runs as ms-teams.exe).
        if (app.Packaged)
        {
            var packagedPids = PidsInPackage(app.Id);
            if (packagedPids.Count == 1) return packagedPids[0];
            if (packagedPids.Count > 1)
            {
                try { return ProcessTree.RootOf(packagedPids, ProcessTree.ParentMap()); }
                catch { return packagedPids[0]; }
            }
            // Fall through to the name-based path as a last resort.
        }

        var exe = app.Id;
        var nameNoExt = Path.GetFileNameWithoutExtension(
            app.Packaged ? app.DisplayName : exe);
        if (string.IsNullOrWhiteSpace(nameNoExt)) return null;

        Process[] byName;
        try { byName = Process.GetProcessesByName(nameNoExt); }
        catch { return null; }
        try
        {
            if (byName.Length == 0) return null;

            // For classic apps, keep only those whose module path matches the detected
            // exe (distinguishes e.g. two different apps that share a process name).
            var matching = byName.AsEnumerable();
            if (!app.Packaged)
            {
                var pathMatched = byName.Where(p =>
                {
                    try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                }).ToList();
                if (pathMatched.Count > 0) matching = pathMatched;
            }

            var pids = matching.Select(p => (uint)p.Id).ToList();
            if (pids.Count == 0) return null;
            if (pids.Count == 1) return pids[0];

            // Multiple processes (e.g. a browser with many renderers): tap the tree
            // ROOT so INCLUDE_TARGET_PROCESS_TREE reaches the audio-rendering child.
            try { return ProcessTree.RootOf(pids, ProcessTree.ParentMap()); }
            catch { return pids[0]; }
        }
        finally
        {
            foreach (var p in byName) p.Dispose();
        }
    }

    /// <summary>All running PIDs whose package family name matches (packaged apps only).</summary>
    static List<uint> PidsInPackage(string familyName)
    {
        var pids = new List<uint>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return pids; }
        try
        {
            foreach (var p in all)
            {
                var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    uint len = 0;
                    // First call sizes the buffer; APPMODEL_ERROR_NO_PACKAGE = classic process.
                    if (GetPackageFamilyName(h, ref len, null) != ERROR_INSUFFICIENT_BUFFER || len == 0)
                        continue;
                    var sb = new StringBuilder((int)len);
                    if (GetPackageFamilyName(h, ref len, sb) == 0 &&
                        sb.ToString().Equals(familyName, StringComparison.OrdinalIgnoreCase))
                        pids.Add((uint)p.Id);
                }
                finally { CloseHandle(h); }
            }
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
        return pids;
    }

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? familyName);
}
