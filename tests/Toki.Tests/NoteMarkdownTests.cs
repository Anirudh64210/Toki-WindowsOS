using System.Linq;
using Toki.Llm;
using Toki.Storage;
using Xunit;

namespace Toki.Tests;

/// <summary>The hand-written-note storage format. Markdown is what lands in %APPDATA%\Toki\Notes, so
/// a round-trip that loses a tick-box or renumbers a list wrongly is data loss the user can see.</summary>
public sealed class NoteMarkdownTests
{
    static string Round(string md) => NoteMarkdown.Write(NoteMarkdown.Parse(md));

    // ── the four block kinds the owner asked for ──

    [Theory]
    [InlineData("# Big", NoteBlockKind.Heading, "Big")]
    [InlineData("## Middle", NoteBlockKind.Subheading, "Middle")]
    [InlineData("- a bullet", NoteBlockKind.Bullet, "a bullet")]
    [InlineData("* a bullet", NoteBlockKind.Bullet, "a bullet")]
    [InlineData("1. first", NoteBlockKind.Numbered, "first")]
    [InlineData("7) seventh", NoteBlockKind.Numbered, "seventh")]
    [InlineData("just words", NoteBlockKind.Body, "just words")]
    public void Parses_each_block_kind(string line, NoteBlockKind kind, string text)
    {
        var b = Assert.Single(NoteMarkdown.Parse(line));
        Assert.Equal(kind, b.Kind);
        Assert.Equal(text, b.Text);
    }

    [Theory]
    [InlineData("- [ ] call Ana", false)]
    [InlineData("- [x] call Ana", true)]
    [InlineData("- [X] call Ana", true)]
    [InlineData("* [ ] call Ana", false)]
    public void Parses_todos_with_their_tick_state(string line, bool done)
    {
        var b = Assert.Single(NoteMarkdown.Parse(line));
        Assert.Equal(NoteBlockKind.Todo, b.Kind);
        Assert.Equal("call Ana", b.Text);
        Assert.Equal(done, b.Done);
    }

    [Fact]
    public void A_todo_is_never_mistaken_for_a_plain_bullet()
    {
        // "- [ ] x" is ALSO valid bullet syntax; the checkbox reading has to win or ticks vanish.
        var b = Assert.Single(NoteMarkdown.Parse("- [ ] ship it"));
        Assert.Equal(NoteBlockKind.Todo, b.Kind);
    }

    [Fact]
    public void Blank_lines_are_preserved_because_spacing_is_the_users()
    {
        var blocks = NoteMarkdown.Parse("one\n\n\ntwo");
        Assert.Equal(
            new[] { NoteBlockKind.Body, NoteBlockKind.Blank, NoteBlockKind.Blank, NoteBlockKind.Body },
            blocks.Select(b => b.Kind));
        Assert.Equal("one\n\n\ntwo", NoteMarkdown.Write(blocks));
    }

    // ── round-trips ──

    [Fact]
    public void A_full_note_round_trips_unchanged()
    {
        const string md =
            "# Pricing sync\n" +
            "\n" +
            "## Summary\n" +
            "We agreed to hold the price.\n" +
            "\n" +
            "## Action items\n" +
            "- [x] send the deck\n" +
            "- [ ] confirm with Ana\n" +
            "\n" +
            "## Notes\n" +
            "- one\n" +
            "- two\n" +
            "1. first\n" +
            "2. second";

        Assert.Equal(md, Round(md));
    }

    [Fact]
    public void Round_tripping_twice_changes_nothing_further()
    {
        const string md = "## Bit\n- [ ] a\n- b\n\nplain";
        Assert.Equal(Round(md), Round(Round(md)));
    }

    [Fact]
    public void Windows_line_endings_survive()
    {
        Assert.Equal("- a\n- b", Round("- a\r\n- b"));
    }

    [Fact]
    public void Empty_and_null_are_empty_not_a_crash()
    {
        Assert.Empty(NoteMarkdown.Parse(null));
        Assert.Empty(NoteMarkdown.Parse(""));
        Assert.Equal("", NoteMarkdown.Write(null));
    }

    [Fact]
    public void Saving_repeatedly_never_grows_trailing_blank_lines()
    {
        var once = Round("a note\n\n\n");
        Assert.Equal("a note", once);
        Assert.Equal(once, Round(once));
    }

    // ── numbering ──

    [Fact]
    public void Numbered_runs_are_renumbered_from_one()
    {
        // The user deleted item 2 and inserted another later — output must read 1. 2. 3.
        var md = "5. alpha\n9. beta\n2. gamma";
        Assert.Equal("1. alpha\n2. beta\n3. gamma", Round(md));
    }

    [Fact]
    public void A_break_starts_a_new_numbered_run()
    {
        var md = "1. a\n2. b\n\n1. c";
        Assert.Equal("1. a\n2. b\n\n1. c", Round(md));
    }

    [Fact]
    public void Bullets_between_numbers_reset_the_count()
    {
        Assert.Equal("1. a\n- x\n1. b", Round("1. a\n- x\n4. b"));
    }

    // ── templates ──

    [Fact]
    public void Every_template_produces_a_usable_skeleton()
    {
        foreach (var t in NoteTemplates.All)
        {
            var md = NoteMarkdown.TemplateSkeleton(t);
            var blocks = NoteMarkdown.Parse(md);

            // Summary + the template's own sections + Action items, all as subheadings.
            var heads = blocks.Where(b => b.Kind == NoteBlockKind.Subheading).Select(b => b.Text).ToList();
            Assert.Equal("Summary", heads.First());
            Assert.Equal("Action items", heads.Last());
            foreach (var section in t.Sections) Assert.Contains(section, heads);

            // And it hands the user an empty to-do to start typing into.
            Assert.Contains(blocks, b => b.Kind == NoteBlockKind.Todo && b.Text.Length == 0 && !b.Done);
            Assert.Equal(md, Round(md));
        }
    }

    // ── markers (the "bullets create themselves" bug, 2026-08-06) ──
    //
    // A block carries its marker in its KIND. If the marker also survives in the TEXT, Write emits both
    // and the note grows a dash every time it's saved and reloaded. These lock that shut.

    [Theory]
    [InlineData(NoteBlockKind.Bullet, "- two", "- two")]
    [InlineData(NoteBlockKind.Bullet, "* two", "- two")]
    [InlineData(NoteBlockKind.Numbered, "1. first", "1. first")]
    [InlineData(NoteBlockKind.Todo, "- [ ] call", "- [ ] call")]
    [InlineData(NoteBlockKind.Subheading, "## Agenda", "## Agenda")]
    public void A_marker_is_never_written_twice(NoteBlockKind kind, string text, string expected)
    {
        Assert.Equal(expected, NoteMarkdown.Write(new[] { new NoteBlock(kind, text) }));
    }

    [Theory]
    [InlineData("- - two", "- two")]
    [InlineData("- - - three", "- three")]
    [InlineData("1. 1. first", "1. first")]
    [InlineData("## ## Agenda", "## Agenda")]
    public void A_doubled_marker_collapses_instead_of_growing(string md, string settled)
    {
        // Each save sheds exactly one stray marker and then holds — the opposite of the reported bug,
        // where every save ADDED one. Never longer than what went in, always reaching a fixed point.
        var text = md;
        for (var i = 0; i < 6; i++)
        {
            var next = Round(text);
            Assert.True(next.Length <= text.Length, $"grew: {text} -> {next}");
            text = next;
        }
        Assert.Equal(settled, text);
        Assert.Equal(text, Round(text));
    }

    [Fact]
    public void Ten_save_reload_cycles_change_nothing()
    {
        var md = "# Plan\n\n- one\n- two\n\n1. first\n2. second\n\n- [ ] send the deck\n- [x] booked the room";
        var text = md;
        for (var i = 0; i < 10; i++) text = Round(text);
        Assert.Equal(md, text);
    }

    // ── the leaked WPF list marker (the ACTUAL "bullets create themselves" loop) ──
    //
    // The old editor read a bulleted line back through TextRange.Text, which includes the native List's
    // own marker as "•\t". That got written into the text, re-read with another one in front, and so on —
    // one more bullet per save. This is the owner's real note, seven cycles deep.

    [Fact]
    public void A_note_damaged_by_the_leaked_marker_bug_comes_back_clean()
    {
        const string damaged = "- •\t•\t•\t•\t•\t•\t•\tConfirm launch blockers & owners";
        var b = Assert.Single(NoteMarkdown.Parse(damaged));
        Assert.Equal(NoteBlockKind.Bullet, b.Kind);
        Assert.Equal("Confirm launch blockers & owners", b.Text);
        Assert.Equal("- Confirm launch blockers & owners", Round(damaged));
    }

    [Fact]
    public void Healing_a_damaged_numbered_list_works_the_same_way()
    {
        Assert.Equal("1. open with the numbers", Round("1. 1.\t1.\topen with the numbers"));
    }

    [Theory]
    // A bullet-and-SPACE is a person typing a bullet by hand. Their text is not ours to edit.
    [InlineData("- • a hand-typed bullet", "• a hand-typed bullet")]
    [InlineData("- •rock • roll", "•rock • roll")]
    [InlineData("- 1.5x the budget", "1.5x the budget")]
    [InlineData("- 2026. what a year", "2026. what a year")]
    public void Healing_never_touches_content_the_user_meant(string line, string expected)
    {
        Assert.Equal(expected, Assert.Single(NoteMarkdown.Parse(line)).Text);
    }

    [Theory]
    [InlineData("- thing", NoteBlockKind.Bullet, "thing")]
    [InlineData("1. thing", NoteBlockKind.Numbered, "thing")]
    [InlineData("- [x] thing", NoteBlockKind.Todo, "thing")]
    [InlineData("# thing", NoteBlockKind.Heading, "thing")]
    [InlineData("## thing", NoteBlockKind.Subheading, "thing")]
    public void TryMarker_is_the_one_definition_of_a_marker(string line, NoteBlockKind kind, string rest)
    {
        Assert.True(NoteMarkdown.TryMarker(line, out var k, out var r, out _));
        Assert.Equal(kind, k);
        Assert.Equal(rest, r);
    }

    [Theory]
    [InlineData("plain words")]
    [InlineData("-nospace")]
    [InlineData("1.nospace")]
    [InlineData("#nospace")]
    [InlineData("")]
    [InlineData(null)]
    public void TryMarker_says_no_to_everything_else(string? line)
    {
        Assert.False(NoteMarkdown.TryMarker(line, out _, out _, out _));
    }

    [Fact]
    public void There_is_no_syntax_for_font_or_colour()
    {
        // The owner ruled those out; anything that looks like markup stays literal text.
        var b = Assert.Single(NoteMarkdown.Parse("<span style=\"color:red\">nope</span>"));
        Assert.Equal(NoteBlockKind.Body, b.Kind);
        Assert.Equal("<span style=\"color:red\">nope</span>", b.Text);
    }
}
