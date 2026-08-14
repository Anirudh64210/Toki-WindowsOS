using System.IO;
using Toki.Llm;
using Toki.Storage;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Carrying a recurring meeting's unfinished business into the next note.
///
/// The section is useful exactly to the degree that it is trustworthy, so most of these are about the
/// cases where it must say NOTHING: a first-ever meeting, an unrelated one, a task already done, and a
/// task this meeting produced anyway.
/// </summary>
public sealed class OpenFromLastTimeTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"toki-carry-{Guid.NewGuid():N}.db");
    readonly MeetingStore _store;

    public OpenFromLastTimeTests() => _store = new MeetingStore(_dbPath);

    public void Dispose() { try { File.Delete(_dbPath); } catch { } }

    MeetingRecord Save(string title, DateTimeOffset when, params ActionItem[] actions)
    {
        var m = new MeetingRecord
        {
            Kind = MeetingKind.Meeting,
            Title = title,
            StartedAt = when,
            Notes = new NoteSet { Tldr = $"Notes for {title}.", Actions = actions.ToList() },
        };
        _store.Save(m);
        return m;
    }

    static ActionItem Todo(string text, string? owner = null, bool done = false) =>
        new() { Text = text, Owner = owner, Done = done };

    [Fact]
    public void An_unfinished_task_is_carried_into_the_next_occurrence()
    {
        Save("Pricing sync", DateTimeOffset.Now.AddDays(-7),
            Todo("rewrite the pricing page opener", "Devi"),
            Todo("pull the bounce rate", done: true));

        var today = Save("Pricing sync", DateTimeOffset.Now, Todo("book the design review"));

        var section = OpenFromLastTime.Build(today, _store);

        Assert.NotNull(section);
        Assert.Equal(OpenFromLastTime.Heading, section!.Heading);
        var only = Assert.Single(section.Bullets);
        Assert.Contains("rewrite the pricing page opener", only);
        Assert.Contains("Devi", only);          // who still owes it is half the point
    }

    [Fact]
    public void A_first_ever_meeting_carries_nothing()
    {
        var first = Save("Pricing sync", DateTimeOffset.Now, Todo("book the design review"));
        Assert.Null(OpenFromLastTime.Build(first, _store));
    }

    [Fact]
    public void An_unrelated_earlier_meeting_is_not_treated_as_last_time()
    {
        Save("Board budget review", DateTimeOffset.Now.AddDays(-3), Todo("send the forecast"));
        var today = Save("Pricing sync", DateTimeOffset.Now);
        Assert.Null(OpenFromLastTime.Build(today, _store));
    }

    [Fact]
    public void A_completed_task_is_never_resurrected()
    {
        Save("Pricing sync", DateTimeOffset.Now.AddDays(-7), Todo("pull the bounce rate", done: true));
        var today = Save("Pricing sync", DateTimeOffset.Now);
        Assert.Null(OpenFromLastTime.Build(today, _store));
    }

    [Fact]
    public void A_task_this_meeting_produced_is_not_listed_twice()
    {
        Save("Pricing sync", DateTimeOffset.Now.AddDays(-7), Todo("Rewrite the pricing page opener"));
        // Same task, different wording of the same words - it must not appear as both new and carried.
        var today = Save("Pricing sync", DateTimeOffset.Now, Todo("rewrite the pricing page opener!"));
        Assert.Null(OpenFromLastTime.Build(today, _store));
    }

    [Fact]
    public void The_most_recent_prior_occurrence_wins()
    {
        Save("Pricing sync", DateTimeOffset.Now.AddDays(-30), Todo("the old thing"));
        Save("Pricing sync", DateTimeOffset.Now.AddDays(-7), Todo("the recent thing"));
        var today = Save("Pricing sync", DateTimeOffset.Now);

        var section = OpenFromLastTime.Build(today, _store);

        Assert.NotNull(section);
        Assert.Contains("the recent thing", Assert.Single(section!.Bullets));
    }

    [Fact]
    public void A_later_meeting_is_never_the_previous_one()
    {
        Save("Pricing sync", DateTimeOffset.Now.AddDays(3), Todo("something from the future"));
        var today = Save("Pricing sync", DateTimeOffset.Now);
        Assert.Null(OpenFromLastTime.Build(today, _store));
    }

    [Fact]
    public void A_manual_note_never_carries_anything()
    {
        Save("Pricing sync", DateTimeOffset.Now.AddDays(-7), Todo("rewrite the opener"));
        var note = new MeetingRecord { Kind = MeetingKind.Manual, Title = "Pricing sync", StartedAt = DateTimeOffset.Now };
        _store.Save(note);
        Assert.Null(OpenFromLastTime.Build(note, _store));
    }

    [Fact]
    public void A_long_backlog_is_capped_rather_than_dumped()
    {
        Save("Pricing sync", DateTimeOffset.Now.AddDays(-7),
            Todo("one"), Todo("two"), Todo("three"), Todo("four"), Todo("five"), Todo("six"));
        var today = Save("Pricing sync", DateTimeOffset.Now);

        var section = OpenFromLastTime.Build(today, _store);

        Assert.NotNull(section);
        Assert.InRange(section!.Bullets.Count, 1, 4);
    }
}
