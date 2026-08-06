using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Toki.Storage;

/// <summary>What a single line of a hand-written note is.</summary>
public enum NoteBlockKind
{
    Body,        // ordinary paragraph
    Heading,     // "# "   — the biggest text size
    Subheading,  // "## "  — the middle size
    Bullet,      // "- "
    Numbered,    // "1. "
    Todo,        // "- [ ] " / "- [x] " — a tickable checkbox
    Blank,       // an empty line: the user's own spacing, preserved verbatim
}

/// <summary>One line of a note: its kind, its text, and (for a to-do) whether it's ticked.</summary>
public sealed record NoteBlock(NoteBlockKind Kind, string Text, bool Done = false);

/// <summary>
/// Markdown ⇄ note-block model for the hand-written note editor.
///
/// Markdown is the STORAGE FORMAT on purpose: Toki already mirrors every note as a real .md file in
/// <c>%APPDATA%\Toki\Notes\</c>, so a hand-written note stays openable in Obsidian or any text editor
/// instead of becoming a proprietary blob — that's the "all data local, and yours" promise, kept.
/// It also gives us exactly the feature set the owner asked for and no more: sizes (#/##), checkboxes
/// (- [ ]), lists, and blank-line spacing. There is deliberately NO font-family or colour syntax,
/// because those aren't offered (owner, 2026-08-05: "nothing outside normal text… just size,
/// templates, spacing, freedom").
///
/// This type is PURE — no WPF — so the round-trip is unit-testable. <see cref="Views.NoteEditor"/>
/// turns these blocks into a FlowDocument and back.
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
    /// Does this line START with one of Toki's markers, and if so which kind is it and what comes after?
    ///
    /// THE single definition of "this text looks like a list item / heading / tick box". Three callers
    /// need to agree exactly: <see cref="Parse"/> (reading a file), <see cref="Write"/> (which strips a
    /// marker the text carries so the Kind's own marker can never be written twice), and the editor
    /// (which promotes "- " into a real bullet as you type). They used to disagree, and the result was
    /// the reported bug: a line the user typed a dash in front of came back as "- - thing", growing a
    /// marker every time the note was saved and reloaded.
    /// </summary>
    public static bool TryMarker(string? line, out NoteBlockKind kind, out string rest, out bool done)
    {
        kind = NoteBlockKind.Body; rest = ""; done = false;
        var t = (line ?? "").TrimStart();
        if (t.Length == 0) return false;

        // To-do BEFORE bullet — "- [ ] x" is also a valid bullet, and the checkbox reading wins.
        if (TryTodo(t, out var todoText, out done)) { kind = NoteBlockKind.Todo; rest = todoText; return true; }

        // "## " before "# " — the longer marker has to win.
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

            // A marked block carries its marker in its KIND, so the text must not carry one too. Strip
            // exactly ONE — the precise inverse of the one Parse consumes — so writing and reading are
            // a fixed point. Without this a bullet reading "- two" was written as "- - two" and grew
            // another dash on every save ("bullets are created by themselves").
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

        // One trailing newline at most — a note shouldn't grow blank lines every time it's saved.
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Remove list-marker GLYPHS that a previous version leaked into a line's own text, so notes written
    /// by it come back clean instead of carrying the damage forever.
    ///
    /// The bug (fixed 2026-08-06): the editor built bullets from WPF's native <c>List</c>, and
    /// <c>TextRange.Text</c> on a paragraph inside a list includes the list's marker, serialised as
    /// "•\t". So reading a line gave back "•\tAgenda item", which was then written as "- •\tAgenda item",
    /// re-read as "•\t•\tAgenda item"… one more bullet on every single save. The owner's note reached
    /// seven.
    ///
    /// The TAB is what makes this safe to strip: it's how WPF serialises a marker, and it is not
    /// something a person types at the start of a note line. A "•" followed by a space is left alone —
    /// that's someone writing a bullet by hand, and their text is not ours to edit.
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

    /// <summary>"1. thing" / "12) thing" — any leading integer, so renumbering is ours to decide.</summary>
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

    /// <summary>The Markdown for one of Toki's note templates, ready to insert: a heading per section
    /// with a blank line under it to type into, and an Action items section that starts with an empty
    /// to-do. Used by the editor's "Template" button so a hand-written note can take the same shape as
    /// an AI-written one.
    ///
    /// Summary and Action items are added here because a <see cref="Llm.NoteTemplate"/> only declares
    /// the sections IN BETWEEN — the AI writer adds those two implicitly, so a hand-written note has to
    /// as well, or the two kinds of note wouldn't look like the same product.</summary>
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
