using System.Text.RegularExpressions;

namespace Toki.Detection;

/// <summary>
/// Turns a raw detected app identity ("Com.tinyspeck.slackdesktop", "ms-teams", "chrome.exe",
/// "ZoomIt64_...") into the human name a prompt can say ("Slack is in a call. Record it?").
/// The raw identifier leaking into the meeting prompt was a real live bug: the reverse-DNS name
/// wrapped to two lines and overflowed the prompt's height, so the notch rendered cut off - and it
/// read like debug output. Known apps map exactly; unknown ones are cleaned (reverse-DNS/".exe"/
/// "desktop" stripped) and title-cased instead of shown raw.
/// </summary>
public static class AppNames
{
    // Known meeting/collab apps by any token of their identity (exe name, package family, reverse-DNS
    // segment). Checked token-by-token, so "com.tinyspeck.slackdesktop" hits "slack" via "slackdesktop".
    static readonly (string Key, string Name)[] Known =
    {
        ("slack", "Slack"),
        ("tinyspeck", "Slack"),
        ("teams", "Microsoft Teams"),
        ("msteams", "Microsoft Teams"),
        ("ms-teams", "Microsoft Teams"),
        ("zoom", "Zoom"),
        ("chrome", "Chrome"),
        ("msedge", "Edge"),
        ("firefox", "Firefox"),
        ("brave", "Brave"),
        ("opera", "Opera"),
        ("vivaldi", "Vivaldi"),
        ("arc", "Arc"),
        ("discord", "Discord"),
        ("skype", "Skype"),
        ("webex", "Webex"),
        ("whatsapp", "WhatsApp"),
        ("telegram", "Telegram"),
        ("signal", "Signal"),
        ("meet", "Google Meet"),
        ("gather", "Gather"),
        ("around", "Around"),
        ("mumble", "Mumble"),
        ("teamspeak", "TeamSpeak"),
    };

    /// <summary>Human name for a raw process/package identity. Never returns the raw reverse-DNS form.</summary>
    public static string Friendly(string? raw)
    {
        var r = (raw ?? "").Trim();
        if (r.Length == 0) return "";

        // Normalize: drop ".exe", the AUMID publisher suffix ("_8wekyb3d8bbwe"), and split on the
        // separators reverse-DNS / kebab / snake names use.
        var stem = Regex.Replace(r, @"\.exe$", "", RegexOptions.IgnoreCase);
        var underscore = stem.IndexOf('_');
        if (underscore > 0) stem = stem[..underscore];
        var tokens = stem.Split(new[] { '.', '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);

        // Exact/contained known-app match wins ("slackdesktop" contains "slack"). Containment needs a
        // 5+ char key so a short key can't fire inside an unrelated word ("meetify" must NOT be
        // "Google Meet"); short keys ("zoom", "arc", "meet") match exact tokens only.
        foreach (var t in tokens)
        {
            var lt = t.ToLowerInvariant();
            foreach (var (key, name) in Known)
                if (lt == key || (key.Length >= 5 && lt.Contains(key))) return name;
        }
        // Whole-string fallback for names the tokenizer split oddly.
        var whole = stem.ToLowerInvariant();
        foreach (var (key, name) in Known)
            if (key.Length >= 5 && whole.Contains(key)) return name;

        // Unknown app: strip reverse-DNS chrome ("com"/"org"/"net"/"io", a likely vendor segment) and
        // the "desktop"/"app"/"client" suffix, then title-case what's left - a readable name, never
        // the raw identifier.
        var meaningful = tokens
            .Where(t => !Regex.IsMatch(t, @"^(com|org|net|io|co|www|app|apps|desktop|client|electron|x64|x86|win32|win64)$", RegexOptions.IgnoreCase))
            .Select(t => Regex.Replace(t, @"(desktop|client)$", "", RegexOptions.IgnoreCase))
            .Where(t => t.Length > 0)
            .ToList();
        if (meaningful.Count == 0) return TitleCase(tokens.LastOrDefault() ?? r);
        // Reverse-DNS puts the vendor before the product ("com.tinyspeck.slackdesktop") - the LAST
        // meaningful token is usually the product; keep up to the last two for compound names.
        var take = meaningful.Count > 2 ? meaningful.Skip(meaningful.Count - 2).ToList() : meaningful;
        return string.Join(" ", take.Select(TitleCase));
    }

    static string TitleCase(string t) =>
        t.Length == 0 ? t : char.ToUpperInvariant(t[0]) + t[1..];
}
