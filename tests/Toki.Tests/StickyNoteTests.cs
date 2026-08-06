using System;
using System.IO;
using System.Linq;
using Toki.Storage;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Sticky notes: the user's own writing about a meeting.
///
/// The rules being locked down here are the ones the owner asked for by name — a note is linked to a
/// meeting BY CHOICE and never by matching titles, a saved note stays saved, and none of this is allowed
/// to leak into the parts of the product that were already working.
/// </summary>
public sealed class StickyNoteTests : IDisposable
{
    readonly string _dir;
    readonly MeetingStore _store;

    public StickyNoteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "toki-sticky-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new MeetingStore(Path.Combine(_dir, "test.db"));
    }

    public void Dispose()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); } catch { }
    }

    long Meeting(string title) => _store.Save(new MeetingRecord
    {
        Title = title, StartedAt = DateTimeOffset.Now, DurationMs = 60_000,
        Transcript = [new TranscriptSegment(Speaker.You, 0, 1000, "hello")],
    });

    // ── the note itself ──

    [Fact]
    public void A_note_exists_from_the_moment_it_is_started()
    {
        // It has to: it's created on the first keystroke precisely so a crash mid-sentence costs nothing.
        var id = _store.CreateSticky("Pricing sync");
        var note = _store.GetSticky(id);
        Assert.NotNull(note);
        Assert.Equal("Pricing sync", note!.Title);
        Assert.Equal("", note.Body);
    }

    [Fact]
    public void What_was_written_stays_written()
    {
        var id = _store.CreateSticky("t", "- [ ] ask about the pilot");
        _store.UpdateSticky(id, "- [x] ask about the pilot\n- [ ] send the deck");
        Assert.Equal("- [x] ask about the pilot\n- [ ] send the deck", _store.GetSticky(id)!.Body);
    }

    [Fact]
    public void Emptying_a_note_does_not_silently_delete_it()
    {
        // Only Delete deletes. Clearing the text is an edit, and the user may be about to type again.
        var id = _store.CreateSticky("t", "something");
        _store.UpdateSticky(id, "");
        Assert.NotNull(_store.GetSticky(id));
    }

    [Fact]
    public void Deleting_a_note_removes_it_and_its_links_and_nothing_else()
    {
        var meetingId = Meeting("Pricing sync");
        var stickyId = _store.CreateSticky("mine", "notes");
        _store.LinkStickyToMeeting(meetingId, stickyId);
        _store.LinkStickyToEvent("evt-1", stickyId);

        _store.DeleteSticky(stickyId);

        Assert.Null(_store.GetSticky(stickyId));
        Assert.Null(_store.StickyForMeeting(meetingId));
        Assert.Null(_store.StickyForEvent("evt-1"));
        Assert.NotNull(_store.Get(meetingId));         // the meeting is untouched
    }

    // ── linking: explicit, never by title ──

    [Fact]
    public void Two_meetings_with_the_SAME_title_do_not_share_a_note()
    {
        // The whole reason links are by id: "Weekly sync" is every meeting anybody has.
        var a = Meeting("Weekly sync");
        var b = Meeting("Weekly sync");
        var note = _store.CreateSticky("Weekly sync", "week one");
        _store.LinkStickyToMeeting(a, note);

        Assert.Equal(note, _store.StickyForMeeting(a));
        Assert.Null(_store.StickyForMeeting(b));
    }

    [Fact]
    public void Renaming_a_meeting_does_not_lose_its_note()
    {
        var meetingId = Meeting("Weekly sync");
        var note = _store.CreateSticky("Weekly sync", "week one");
        _store.LinkStickyToMeeting(meetingId, note);

        _store.UpdateTitleAndBody(meetingId, "Completely different name", null);

        Assert.Equal(note, _store.StickyForMeeting(meetingId));
    }

    [Fact]
    public void One_note_can_run_across_several_meetings()
    {
        // A recurring 1:1 where you keep the same running page — the case that made a per-event JSON
        // file the wrong shape in the first place.
        var week1 = Meeting("1:1 with Ana");
        var week2 = Meeting("1:1 with Ana");
        var note = _store.CreateSticky("Ana", "running notes");
        _store.LinkStickyToMeeting(week1, note);
        _store.LinkStickyToMeeting(week2, note);

        Assert.Equal(note, _store.StickyForMeeting(week1));
        Assert.Equal(note, _store.StickyForMeeting(week2));
    }

    [Fact]
    public void Choosing_another_note_for_an_event_replaces_the_choice_it_does_not_stack()
    {
        var first = _store.CreateSticky("a", "one");
        var second = _store.CreateSticky("b", "two");
        _store.LinkStickyToEvent("evt-9", first);
        _store.LinkStickyToEvent("evt-9", second);
        Assert.Equal(second, _store.StickyForEvent("evt-9"));
    }

    [Fact]
    public void Deleting_a_meeting_keeps_the_note_you_wrote()
    {
        // You may still want it next week. The LINK goes; the writing doesn't.
        var meetingId = Meeting("Pricing sync");
        var note = _store.CreateSticky("mine", "worth keeping");
        _store.LinkStickyToMeeting(meetingId, note);

        _store.Delete(meetingId);

        Assert.Null(_store.StickyForMeeting(meetingId));
        Assert.Equal("worth keeping", _store.GetSticky(note)?.Body);
    }

    [Fact]
    public void Unlinking_detaches_without_deleting()
    {
        var meetingId = Meeting("Pricing sync");
        var note = _store.CreateSticky("mine", "text");
        _store.LinkStickyToMeeting(meetingId, note);
        _store.UnlinkStickyFromMeeting(meetingId);

        Assert.Null(_store.StickyForMeeting(meetingId));
        Assert.NotNull(_store.GetSticky(note));
    }

    [Fact]
    public void An_unlinked_event_or_meeting_simply_has_no_note()
    {
        Assert.Null(_store.StickyForEvent("never-seen"));
        Assert.Null(_store.StickyForEvent(null));
        Assert.Null(_store.StickyForMeeting(4242));
        Assert.Null(_store.MeetingSticky(4242));
    }

    // ── the picker ──

    [Fact]
    public void The_picker_offers_recent_notes_newest_first_and_skips_empty_ones()
    {
        var older = _store.CreateSticky("older", "text");
        System.Threading.Thread.Sleep(5);
        var newer = _store.CreateSticky("newer", "text");
        _store.CreateSticky("blank", "   ");          // nothing to reuse — don't offer it

        var recent = _store.RecentStickies();
        Assert.Equal(new[] { newer, older }, recent.Select(s => s.Id));
    }

    // ── it must not leak into anything that already worked ──

    [Fact]
    public void Sticky_notes_are_not_rows_in_the_Library_list()
    {
        // The owner's complaint: a sticky note appearing among the meetings, which couldn't even be
        // opened. They are a different table now, so the list literally cannot show one.
        var before = _store.List().Count;
        _store.CreateSticky("a note", "with content");
        Assert.Equal(before, _store.List().Count);
    }

    [Fact]
    public void Sticky_notes_are_not_searchable_and_cannot_surface_in_Ask()
    {
        // Deliberate: it's the user's private page. It doesn't steer the summary and it doesn't come
        // back as an answer either — otherwise "it never influences anything" would only be half true.
        var meetingId = Meeting("Pricing sync");
        var note = _store.CreateSticky("mine", "zzyzxsecretword");
        _store.LinkStickyToMeeting(meetingId, note);

        Assert.Empty(_store.SearchRelevant("zzyzxsecretword", 10));
        Assert.Empty(_store.List("zzyzxsecretword"));
    }

    // ── migration off the old JSON file ──

    [Fact]
    public void The_old_prep_scratch_file_is_carried_across_and_then_retired()
    {
        var legacy = Path.Combine(_dir, "prep-scratch.json");
        File.WriteAllText(legacy, """
            {
              "evt-a": { "Text": "- [ ] ask about pricing", "Title": "Pricing sync", "Awaiting": true },
              "evt-b": { "Text": "", "Title": "Empty one" }
            }
            """);
        StickyMigration.PathOverride = legacy;
        try
        {
            StickyMigration.RunOnce(_store);

            var id = _store.StickyForEvent("evt-a");
            Assert.NotNull(id);
            Assert.Equal("- [ ] ask about pricing", _store.GetSticky(id!.Value)!.Body);
            Assert.Equal("Pricing sync", _store.GetSticky(id.Value)!.Title);
            Assert.Null(_store.StickyForEvent("evt-b"));          // nothing was written in it

            // Renamed, never deleted — the user's words survive even if this went wrong.
            Assert.False(File.Exists(legacy));
            Assert.True(File.Exists(legacy + ".migrated"));
        }
        finally { StickyMigration.PathOverride = null; }
    }

    [Fact]
    public void The_very_first_file_format_is_still_readable()
    {
        var legacy = Path.Combine(_dir, "prep-scratch.json");
        File.WriteAllText(legacy, """{ "evt-old": "a bare string, the v1 shape" }""");
        StickyMigration.PathOverride = legacy;
        try
        {
            StickyMigration.RunOnce(_store);
            var id = _store.StickyForEvent("evt-old");
            Assert.NotNull(id);
            Assert.Equal("a bare string, the v1 shape", _store.GetSticky(id!.Value)!.Body);
        }
        finally { StickyMigration.PathOverride = null; }
    }

    [Fact]
    public void Migrating_twice_cannot_duplicate_anything()
    {
        var legacy = Path.Combine(_dir, "prep-scratch.json");
        var content = """{ "evt-a": { "Text": "mine", "Title": "T" } }""";
        StickyMigration.PathOverride = legacy;
        try
        {
            File.WriteAllText(legacy, content);
            StickyMigration.RunOnce(_store);
            File.WriteAllText(legacy, content);      // as if the rename hadn't stuck
            StickyMigration.RunOnce(_store);

            Assert.Single(_store.RecentStickies());
        }
        finally { StickyMigration.PathOverride = null; }
    }

    [Fact]
    public void A_corrupt_legacy_file_is_left_alone_rather_than_thrown_away()
    {
        var legacy = Path.Combine(_dir, "prep-scratch.json");
        File.WriteAllText(legacy, "{ this is not json at all");
        StickyMigration.PathOverride = legacy;
        try
        {
            StickyMigration.RunOnce(_store);         // must not throw
            Assert.True(File.Exists(legacy));        // still there, to be looked at by a human
        }
        finally { StickyMigration.PathOverride = null; }
    }
}
