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

            var t = line.TrimStart();

            // To-do BEFORE bullet — "- [ ] x" is also a valid bullet, and the checkbox reading wins.
            if (TryTodo(t, out var todoText, out var done))
            { blocks.Add(new NoteBlock(NoteBlockKind.Todo, todoText, done)); continue; }

            if (t.StartsWith("## ", StringComparison.Ordinal))
            { blocks.Add(new NoteBlock(NoteBlockKind.Subheading, t[3..].Trim())); continue; }

            if (t.StartsWith("# ", StringComparison.Ordinal))
            { blocks.Add(new NoteBlock(NoteBlockKind.Heading, t[2..].Trim())); continue; }

            if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal))
            { blocks.Add(new NoteBlock(NoteBlockKind.Bullet, t[2..].Trim())); continue; }

            if (TryNumbered(t, out var numText))
            { blocks.Add(new NoteBlock(NoteBlockKind.Numbered, numText)); continue; }

            blocks.Add(new NoteBlock(NoteBlockKind.Body, line.Trim()));
        }

        return blocks;
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
