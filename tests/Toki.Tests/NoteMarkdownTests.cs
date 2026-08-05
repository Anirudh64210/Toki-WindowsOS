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

    [Fact]
    public void There_is_no_syntax_for_font_or_colour()
    {
        // The owner ruled those out; anything that looks like markup stays literal text.
        var b = Assert.Single(NoteMarkdown.Parse("<span style=\"color:red\">nope</span>"));
        Assert.Equal(NoteBlockKind.Body, b.Kind);
        Assert.Equal("<span style=\"color:red\">nope</span>", b.Text);
    }
}
