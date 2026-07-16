using Microsoft.Win32;

namespace Toki.Detection;

/// <summary>
/// Reads which apps are using the microphone *right now* from the Windows
/// capability-access registry — no permission prompt, and it hands us the app's
/// identity. An app is live on the mic when its LastUsedTimeStop == 0.
///
/// Packaged apps sit directly under ...\ConsentStore\microphone\&lt;AUMID&gt;.
/// Classic apps sit under ...\microphone\NonPackaged\&lt;exe path with '#' for '\'&gt;.
/// </summary>
public static class MicUsageReader
{
    const string ConsentPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public static IReadOnlyList<DetectedApp> GetActiveMicApps()
    {
        var result = new List<DetectedApp>();
        // Per-user apps (Zoom, Teams, Chrome/Edge) live in HKCU; a few register
        // per-machine in HKLM. Read both and de-dupe by identity.
        ReadHive(Registry.CurrentUser, result);
        ReadHive(Registry.LocalMachine, result);
        return result
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    static void ReadHive(RegistryKey hive, List<DetectedApp> result)
    {
        using var root = hive.OpenSubKey(ConsentPath);
        if (root is null) return;

        foreach (var name in root.GetSubKeyNames())
        {
            if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                using var nonPackaged = root.OpenSubKey(name);
                if (nonPackaged is null) continue;
                foreach (var appKey in nonPackaged.GetSubKeyNames())
                {
                    using var k = nonPackaged.OpenSubKey(appKey);
                    if (k is not null && IsInUse(k))
                    {
                        var path = appKey.Replace('#', '\\');
                        result.Add(new DetectedApp(path, FileName(path), Packaged: false));
                    }
                }
            }
            else
            {
                using var k = root.OpenSubKey(name);
                if (k is not null && IsInUse(k))
                    result.Add(new DetectedApp(name, PackagedName(name), Packaged: true));
            }
        }
    }

    static bool IsInUse(RegistryKey key)
    {
        // Present + 0 means the app opened the mic and hasn't stopped.
        if (key.GetValue("LastUsedTimeStop") is long stop)
            return stop == 0;
        return false;
    }

    static string FileName(string path)
    {
        var slash = path.LastIndexOf('\\');
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }

    static string PackagedName(string aumid)
    {
        // e.g. "MSTeams_8wekyb3d8bbwe" -> "MSTeams"
        var underscore = aumid.IndexOf('_');
        return underscore > 0 ? aumid[..underscore] : aumid;
    }
}
