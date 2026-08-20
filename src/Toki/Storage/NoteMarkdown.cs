using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Toki.Storage;

/// <summary>What a single line of a hand-written note is.</summary>
public enum NoteBlockKind
{
    Body,        // ordinary paragraph
    Heading,     // "# "   - the biggest text size
    Subheading,  // "## "  - the middle size
    Bullet,      // "- "
    Numbered,    // "1. "
    Todo,        // "- [ ] " / "- [x] " - a tickable checkbox
    Blank,       // an empty line: the user's own spacing, preserved verbatim
}

/// <summary>One line of a note: its kind, its text, and (for a to-do) whether it's ticked.</summary>
public sealed record NoteBlock(NoteBlockKind Kind, string Text, bool Done = false);

/// <summary>
/// Markdown ⇄ note-block model for the hand-written note editor. Markdown is the storage format so
/// notes stay plain .md files; the syntax is deliberately limited to #/##, checkboxes, lists, and
/// blank lines. Pure (no WPF) so the round-trip is unit-testable; <see cref="Views.NoteEditor"/>
/// maps blocks to a FlowDocument and back.
/// </summary>
public static class NoteMarkdown
{
    /// <summary>Parse note text into blocks. Never throws; anything unrecognised is a Body line.</summary>
    public static IReadOnlyList<NoteBlock> Parse(string? markdown)
    {
        var blocks = new List<NoteBlock>();
        if (string.IsNullOrEmpty(markdown)) return blocks;

        foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { blocks.Add(new NoteBlock(NoteBlockKind.Blank, "")); continue; }

            blocks.Add(TryMarker(line.TrimStart(), out var kind, out var rest, out var done)
                ? new NoteBlock(kind, Heal(rest), done)
                : new NoteBlock(NoteBlockKind.Body, Heal(line.Trim())));
        }

        return blocks;
    }

    /// <summary>
    /// THE single definition of a marker line. <see cref="Parse"/>, <see cref="Write"/>, and the
    /// editor must agree exactly, or saved notes grow duplicate markers on every round-trip.
    /// </summary>
    public static bool TryMarker(string? line, out NoteBlockKind kind, out string rest, out bool done)
    {
        kind = NoteBlockKind.Body; rest = ""; done = false;
        var t = (line ?? "").TrimStart();
        if (t.Length == 0) return false;

        // To-do BEFORE bullet - "- [ ] x" is also a valid bullet, and the checkbox reading wins.
        if (TryTodo(t, out var todoText, out done)) { kind = NoteBlockKind.Todo; rest = todoText; return true; }

        // "## " before "# " - the longer marker has to win.
        if (t.StartsWith("## ", StringComparison.Ordinal)) { kind = NoteBlockKind.Subheading; rest = t[3..].Trim(); return true; }
        if (t.StartsWith("# ", StringComparison.Ordinal)) { kind = NoteBlockKind.Heading; rest = t[2..].Trim(); return true; }

        if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal))
        { kind = NoteBlockKind.Bullet; rest = t[2..].Trim(); return true; }

        if (TryNumbered(t, out var numText)) { kind = NoteBlockKind.Numbered; rest = numText; return true; }

        return false;
    }

    /// <summary>Write blocks back to Markdown. Numbered lists are renumbered from 1 per run, so
    /// inserting or deleting an item never leaves "1. 2. 2. 5.".</summary>
    public static string Write(IEnumerable<NoteBlock>? blocks)
    {
        if (blocks is null) return "";
        var sb = new StringBuilder();
        var n = 0;   // position within the CURRENT run of numbered items

        foreach (var b in blocks)
        {
            if (b.Kind == NoteBlockKind.Numbered) n++; else n = 0;

            var text = (b.Text ?? "").Trim();

            // The Kind carries the marker, so strip exactly one from the text (the precise inverse of
            // what Parse consumes) to keep write/read a fixed point.
            if (b.Kind is not (NoteBlockKind.Body or NoteBlockKind.Blank)
                && TryMarker(text, out _, out var bare, out _)) text = bare;

            sb.Append(b.Kind switch
            {
                NoteBlockKind.Blank      => "",
                NoteBlockKind.Heading    => "# " + text,
                NoteBlockKind.Subheading => "## " + text,
                NoteBlockKind.Bullet     => "- " + text,
                NoteBlockKind.Numbered   => $"{n}. " + text,
                NoteBlockKind.Todo       => (b.Done ? "- [x] " : "- [ ] ") + text,
                _                        => text,
            });
            sb.Append('\n');
        }

        // One trailing newline at most - a note shouldn't grow blank lines every time it's saved.
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Strip WPF List marker glyphs ("•\t", "1.\t") leaked into line text by older saves. The TAB is
    /// what makes stripping safe: WPF serialises markers with it and people don't type it at line
    /// start; a hand-typed "• " is left alone.
    /// </summary>
    static string Heal(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var j = i;
            if (text[j] is '•' or '▪' or '◦' or '‣') j++;
            else
            {
                while (j < text.Length && char.IsDigit(text[j])) j++;      // "1." / "12." from a numbered list
                if (j == i || j >= text.Length || text[j] is not ('.' or ')')) break;
                j++;
            }
            if (j >= text.Length || text[j] != '\t') break;                // no tab → real content, stop
            i = j + 1;
        }
        return i > 0 ? text[i..].TrimStart() : text;
    }

    /// <summary>"- [ ] thing" / "- [x] thing" (also accepts "* [X] thing").</summary>
    static bool TryTodo(string t, out string text, out bool done)
    {
        text = ""; done = false;
        if (t.Length < 5) return false;
        if (t[0] != '-' && t[0] != '*') return false;
        if (t[1] != ' ' || t[2] != '[' || t[4] != ']') return false;

        var mark = t[3];
        if (mark is 'x' or 'X') done = true;
        else if (mark is not (' ' or '\0')) return false;

        text = t.Length > 5 ? t[5..].Trim() : "";
        return true;
    }

    /// <summary>"1. thing" / "12) thing" - any leading integer, so renumbering is ours to decide.</summary>
    static bool TryNumbered(string t, out string text)
    {
        text = "";
        var i = 0;
        while (i < t.Length && char.IsDigit(t[i])) i++;
        if (i == 0 || i + 1 >= t.Length) return false;
        if (t[i] != '.' && t[i] != ')') return false;
        if (t[i + 1] != ' ') return false;

        text = t[(i + 2)..].Trim();
        return true;
    }

    /// <summary>Template skeleton for a hand-written note. Summary and Action items are added here
    /// because <see cref="Llm.NoteTemplate"/> declares only the middle sections; the AI writer adds
    /// those two implicitly, and both note kinds must share one shape.</summary>
    public static string TemplateSkeleton(Llm.NoteTemplate template)
    {
        var sb = new StringBuilder();
        sb.Append("## Summary\n\n");
        foreach (var section in template.Sections)
            sb.Append("## ").Append(section).Append("\n\n");
        sb.Append("## Action items\n");
        sb.Append("- [ ] \n");
        return sb.ToString().TrimEnd('\n');
    }
}
