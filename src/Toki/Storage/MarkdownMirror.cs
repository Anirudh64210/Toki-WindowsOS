using System.IO;
using System.Text;
using Toki.Transcription;

namespace Toki.Storage;

/// <summary>
/// Writes each meeting to a plain Markdown file under %APPDATA%\Toki\Notes\ — a
/// user-inspectable, sync-friendly copy of the notes (the research's "every
/// meeting is a local file you own" principle). The DB is the source of truth;
/// this is a mirror + export format.
/// </summary>
public static class MarkdownMirror
{
    public static string NotesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toki", "Notes");

    public static string Write(MeetingRecord m)
    {
        Directory.CreateDirectory(NotesDir);
        var path = Path.Combine(NotesDir, $"{Safe(m.Title)}-{m.Id}.md");
        // Containment guard: Safe() already strips path separators and traversal, but a title is
        // LLM/transcript-derived (attacker-influenceable), so assert the final path really is inside
        // NotesDir before writing — a regression in Safe() becomes a caught error, not an arbitrary write.
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(NotesDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"note path escaped the Notes directory: {full}");
        // The filename embeds the TITLE, and the LLM retitles the meeting after the first
        // mirror write — drop any older mirror of this id first, or every retitle leaves a
        // stale duplicate .md (same id, old name) on disk forever.
        try
        {
            foreach (var old in Directory.GetFiles(NotesDir, $"*-{m.Id}.md"))
                if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
        }
        catch { /* cleanup is best-effort; the fresh write below is what matters */ }
        File.WriteAllText(path, Render(m), Encoding.UTF8);
        return path;
    }

    public static string Render(MeetingRecord m)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {m.Title}").AppendLine();
        sb.Append($"*{m.StartedAt:dddd, MMM d yyyy · h:mm tt}*");
        if (m.DurationMs > 0) sb.Append($" · {m.DurationMs / 60000} min");
        if (!string.IsNullOrWhiteSpace(m.App)) sb.Append($" · {m.App}");
        sb.AppendLine().AppendLine();

        if (m.Kind == MeetingKind.Manual)
        {
            sb.AppendLine(m.Body ?? "");
            return sb.ToString();
        }

        if (!string.IsNullOrWhiteSpace(m.Notes.Tldr))
            sb.AppendLine("## Summary").AppendLine(m.Notes.Tldr).AppendLine();

        // Template middle sections (unified with legacy decisions/questions via EffectiveSections).
        foreach (var sec in m.Notes.EffectiveSections())
        {
            if (sec.Bullets.Count == 0) continue;
            sb.AppendLine($"## {sec.Heading}");
            foreach (var b in sec.Bullets) sb.AppendLine($"- {b}");
            sb.AppendLine();
        }
        if (m.Notes.Actions.Count > 0)
        {
            sb.AppendLine("## To do");
            foreach (var a in m.Notes.Actions)
            {
                var meta = new[] { a.Owner, a.Due }.Where(x => !string.IsNullOrWhiteSpace(x));
                var suffix = meta.Any() ? $" _({string.Join(", ", meta)})_" : "";
                sb.AppendLine($"- [{(a.Done ? "x" : " ")}] {a.Text}{suffix}");
            }
            sb.AppendLine();
        }
        if (m.Transcript.Count > 0)
        {
            sb.AppendLine("## Transcript");
            foreach (var s in m.Transcript)
                sb.AppendLine($"**{(s.Speaker == Speaker.You ? "You" : "Them")}:** {s.Text}").AppendLine();
        }
        return sb.ToString();
    }

    static string Safe(string title)
    {
        var cleaned = new string(title.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' ? c : ' ').ToArray());
        cleaned = string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 60 ? cleaned[..60].Trim() : (cleaned.Length == 0 ? "note" : cleaned);
    }
}
