using System.Diagnostics;
using System.IO;

namespace Toki.Interop;

/// <summary>
/// The ONE place Toki hands a string to the Windows shell. Calendar data is attacker-influenceable
/// and ShellExecute launches any handler (file://, UNC, ms-msdt:, javascript:), so every external
/// open goes through here: only http/https URLs, or a validated in-app path for file reveal.
/// </summary>
public static class SafeShell
{
    /// <summary>Open a web URL in the default browser. Silently refuses anything that isn't an
    /// absolute http/https URL (logged), so a hostile scheme can never reach the shell.</summary>
    public static bool OpenUrl(string? url)
    {
        if (!IsWebUrl(url, out var uri))
        {
            App.Log($"SafeShell: refused non-web url open: {Clip(url)}");
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { App.Log($"SafeShell: open url failed: {ex.Message}"); return false; }
    }

    /// <summary>True iff <paramref name="url"/> is an absolute http/https URL. Public for tests.</summary>
    public static bool IsWebUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        uri = u;
        return true;
    }

    /// <summary>Reveal a file in Explorer - only when it resolves INSIDE one of the app's own data
    /// directories (never an arbitrary path), so a poisoned DB row can't point Explorer elsewhere.</summary>
    public static bool RevealInExplorer(string? path, params string[] allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }
        var ok = allowedRoots.Any(root =>
        {
            try
            {
                var r = Path.GetFullPath(root);
                return full.StartsWith(r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        });
        if (!ok || !File.Exists(full)) { App.Log($"SafeShell: refused reveal outside data dirs: {Clip(path)}"); return false; }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { App.Log($"SafeShell: reveal failed: {ex.Message}"); return false; }
    }

    static string Clip(string? s) => s is null ? "(null)" : s[..Math.Min(80, s.Length)];
}
