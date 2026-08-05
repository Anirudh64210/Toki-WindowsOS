using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Toki.Storage;
using Xunit;

namespace Toki.Tests;

/// <summary>The prep-card scratch store. This holds text the user typed WITH THEIR OWN HANDS, which
/// makes it the least forgiving thing in the app to get wrong — a regenerable summary can be rebuilt,
/// a lost sentence cannot. Every test runs against a temp file (never the real one).</summary>
[Collection("PrepScratch")]   // one file, one cache — these must not run in parallel with each other
public sealed class PrepScratchTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"toki-scratch-{Guid.NewGuid():N}.json");

    public PrepScratchTests()
    {
        PrepScratch.PathOverride = _path;
        PrepScratch.ResetForTests();
    }

    public void Dispose()
    {
        PrepScratch.PathOverride = null;
        PrepScratch.ResetForTests();
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void Text_survives_a_restart()
    {
        PrepScratch.Set("e1", "- [ ] ask about pricing", "Design review");
        PrepScratch.ResetForTests();                       // as if Toki had been quit and reopened
        Assert.Equal("- [ ] ask about pricing", PrepScratch.Get("e1"));
        Assert.Equal("Design review", PrepScratch.Entry("e1")!.Title);
    }

    [Fact]
    public void Emptying_a_note_forgets_it_rather_than_storing_a_blank()
    {
        PrepScratch.Set("e1", "something");
        PrepScratch.Set("e1", "   ");
        Assert.False(PrepScratch.Has("e1"));
        Assert.Null(PrepScratch.Entry("e1"));
    }

    [Fact]
    public void Unknown_and_null_ids_are_safe()
    {
        Assert.Equal("", PrepScratch.Get(null));
        Assert.Equal("", PrepScratch.Get("nope"));
        Assert.False(PrepScratch.Has(null));
        PrepScratch.Set(null, "x");        // no throw
        PrepScratch.Clear(null);           // no throw
        Assert.Empty(PrepScratch.Awaiting());
    }

    // ── the end-of-meeting offer ──

    [Fact]
    public void A_finished_meeting_puts_written_notes_up_for_a_decision()
    {
        PrepScratch.Set("e1", "we agreed to hold the price");
        PrepScratch.MarkAwaiting("e1", "Pricing sync");

        var pending = PrepScratch.Awaiting();
        var (id, note) = Assert.Single(pending);
        Assert.Equal("e1", id);
        Assert.Equal("Pricing sync", note.Title);
        Assert.True(note.Awaiting);
    }

    [Fact]
    public void A_meeting_you_never_wrote_on_offers_nothing()
    {
        // Nothing typed → nothing to keep. Otherwise every recorded meeting would leave an empty
        // card demanding a decision.
        PrepScratch.MarkAwaiting("e1", "Pricing sync");
        Assert.Empty(PrepScratch.Awaiting());
    }

    [Fact]
    public void Keeping_or_discarding_removes_the_offer()
    {
        PrepScratch.Set("e1", "notes");
        PrepScratch.MarkAwaiting("e1", "Pricing sync");
        PrepScratch.Clear("e1");
        Assert.Empty(PrepScratch.Awaiting());
        Assert.Equal("", PrepScratch.Get("e1"));
    }

    [Fact]
    public void Editing_after_the_meeting_keeps_it_pending()
    {
        // You reopen the card and add a line before deciding — that must not silently cancel the offer.
        PrepScratch.Set("e1", "notes");
        PrepScratch.MarkAwaiting("e1", "Pricing sync");
        PrepScratch.Set("e1", "notes\nand one more thing");

        var (_, note) = Assert.Single(PrepScratch.Awaiting());
        Assert.True(note.Awaiting);
        Assert.Contains("one more thing", note.Text);
        Assert.Equal("Pricing sync", note.Title);   // title isn't lost by a plain text edit
    }

    [Fact]
    public void Pending_offers_come_back_newest_first()
    {
        PrepScratch.Set("old", "a"); PrepScratch.MarkAwaiting("old", "Old");
        System.Threading.Thread.Sleep(5);
        PrepScratch.Set("new", "b"); PrepScratch.MarkAwaiting("new", "New");

        Assert.Equal(new[] { "new", "old" }, PrepScratch.Awaiting().Select(p => p.EventId));
    }

    // ── the file itself ──

    [Fact]
    public void The_first_shipped_format_is_read_not_thrown_away()
    {
        // v1 stored a bare string per event. Anyone who typed notes on that build must not lose them.
        File.WriteAllText(_path, JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, string>
        {
            ["e1"] = "notes from the old format",
        }));
        PrepScratch.ResetForTests();

        Assert.Equal("notes from the old format", PrepScratch.Get("e1"));
    }

    [Fact]
    public void A_corrupt_file_loses_the_notes_but_never_crashes_the_app()
    {
        File.WriteAllText(_path, "{ this is not json");
        PrepScratch.ResetForTests();

        Assert.Equal("", PrepScratch.Get("e1"));
        PrepScratch.Set("e1", "and we can still write");   // recovers rather than staying broken
        Assert.Equal("and we can still write", PrepScratch.Get("e1"));
    }

    [Fact]
    public void A_saved_note_leaves_no_temp_file_behind()
    {
        PrepScratch.Set("e1", "x");
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));   // atomic write swaps, never leaves litter
    }
}
