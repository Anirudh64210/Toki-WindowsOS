using System.Diagnostics;
using System.IO;

namespace Toki.Detection;

/// <summary>Maps a detected mic-using app to a running process id for loopback capture.</summary>
public static class ProcessResolver
{
    /// <summary>Best-effort PID for the app's process tree root; null if it can't be found.</summary>
    public static uint? FindPid(DetectedApp app)
    {
        var exe = app.Id;
        var nameNoExt = Path.GetFileNameWithoutExtension(
            app.Packaged ? app.DisplayName : exe);
        if (string.IsNullOrWhiteSpace(nameNoExt)) return null;

        Process[] byName;
        try { byName = Process.GetProcessesByName(nameNoExt); }
        catch { return null; }
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
}
