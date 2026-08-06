using System;
using System.Collections.Generic;
using System.Linq;
using Toki.Calendar;
using Toki.Storage;
using Xunit;

namespace Toki.Tests;

/// <summary>Pressing Edit on a prep card hands you the card's OWN content as a writable note. If that
/// conversion drops an open item or mangles a tick box, the user loses prep they were relying on.</summary>
public sealed class PrepMarkdownTests
{
    static CalEvent Event(string? description = null) => new(
        "e1", "Design review", DateTimeOffset.UtcNow.AddMinutes(9), DateTimeOffset.UtcNow.AddMinutes(39),
        false, null, "https://meet.example/x", null, Array.Empty<CalAttendee>(), description);

    static MeetingRecord Last(string tldr) => new()
    {
        Title = "Design review", Kind = MeetingKind.Meeting,
        StartedAt = DateTimeOffset.Now.AddDays(-2),
        Notes = new NoteSet { Tldr = tldr },
    };

    static PrepContent Content(
        MeetingRecord? last = null,
        IReadOnlyList<ActionItem>? open = null,
        IReadOnlyList<string>? agenda = null) =>
        new(Event(), last, open ?? Array.Empty<ActionItem>(), agenda ?? Array.Empty<string>());

    [Fact]
    public void Open_items_become_real_tick_boxes_with_their_owner()
    {
        var md = PrepBuilder.ToMarkdown(Content(open: new[]
        {
            new ActionItem { Text = "Confirm brand font + hex.", Owner = "Priya" },
            new ActionItem { Text = "Wire the webhook.", Owner = "Sid", Done = true },
            new ActionItem { Text = "No owner here." },
        }));

        Assert.Contains("- [ ] Priya - Confirm brand font + hex.", md);
        Assert.Contains("- [x] Sid - Wire the webhook.", md);
        Assert.Contains("- [ ] No owner here.", md);   // no owner → no dangling em dash

        // And they survive as to-dos through the editor's own format.
        var todos = NoteMarkdown.Parse(md).Where(b => b.Kind == NoteBlockKind.Todo).ToList();
        Assert.Equal(3, todos.Count);
        Assert.Equal(new[] { false, true, false }, todos.Select(t => t.Done));
    }

    [Fact]
    public void Agenda_lines_become_bullets()
    {
        var md = PrepBuilder.ToMarkdown(Content(agenda: new[] { "Lock the ship date", "Owners" }));
        var bullets = NoteMarkdown.Parse(md).Where(b => b.Kind == NoteBlockKind.Bullet).Select(b => b.Text);
        Assert.Equal(new[] { "Lock the ship date", "Owners" }, bullets);
    }

    [Fact]
    public void There_is_always_somewhere_to_type_at_the_end()
    {
        // An editor that opens with the caret inside a heading you didn't write is one you have to
        // tidy before you can use it.
        foreach (var c in new[] { Content(), Content(Last("We shipped it.")), Content(agenda: new[] { "x" }) })
            Assert.EndsWith("## My notes\n", PrepBuilder.ToMarkdown(c));
    }

    [Fact]
    public void A_first_time_meeting_never_invents_a_last_time()
    {
        var md = PrepBuilder.ToMarkdown(Content());
        Assert.DoesNotContain("Last time", md);
        Assert.DoesNotContain("Still open", md);
        Assert.DoesNotContain("Agenda", md);
    }

    [Fact]
    public void A_recap_is_carried_over_when_there_is_one()
    {
        var md = PrepBuilder.ToMarkdown(Content(Last("Blockers are brand tokens and the webhook.")));
        Assert.Contains("## Last time", md);
        Assert.Contains("Blockers are brand tokens and the webhook.", md);
    }

    [Fact]
    public void The_whole_card_round_trips_through_the_editor_format()
    {
        var md = PrepBuilder.ToMarkdown(Content(
            Last("Ship moved to Thursday."),
            new[] { new ActionItem { Text = "Confirm hex.", Owner = "Priya" } },
            new[] { "Lock the ship date" }));

        // What Edit shows is exactly what a save would write back - no drift on the first keystroke.
        Assert.Equal(md.TrimEnd('\n'), NoteMarkdown.Write(NoteMarkdown.Parse(md)));
    }
}
