using System.IO;
using Toki.Llm;
using Toki.Storage;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Due-date-aware task scoping - the fix for "what tasks do I have for this week" dumping every open
/// task. Two layers: (1) <see cref="TaskScheduling"/> pure-function unit tests (frozen clock, no DB),
/// (2) end-to-end task answers through the <see cref="NotesQa.StoreOverride"/> seam.
/// </summary>
public sealed class TaskSchedulingUnitTests
{
    // Meeting reference date for resolving a free-text due like "Friday".
    static readonly DateTimeOffset Mon = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);   // a Monday
    static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);  // Wednesday

    static OpenAction Task(string text, string? owner, string? due, DateTimeOffset? at = null)
        => new(1, "Sprint sync", at ?? Mon, text, owner, due);

    [Theory]
    [InlineData("Friday", "2026-08-07")]       // the Friday after Monday Aug 3 (future-biased, not last Fri)
    [InlineData("tomorrow", "2026-08-04")]     // meeting + 1 day
    [InlineData("next Monday", "2026-08-10")]
    [InlineData("August 22", "2026-08-22")]    // an explicit date is taken as-is
    public void ParseDue_ResolvesRelativeToMeeting(string due, string expected)
    {
        var d = TaskScheduling.ParseDue(due, Mon);
        Assert.NotNull(d);
        Assert.Equal(DateTime.Parse(expected).Date, d!.Value.Date);
    }

    [Theory]
    [InlineData("ASAP")]
    [InlineData("when ready")]
    [InlineData("TBD")]
    [InlineData(null)]
    [InlineData("")]
    public void ParseDue_ReturnsNull_ForNonDates(string? due)
        => Assert.Null(TaskScheduling.ParseDue(due, Mon));

    [Fact]
    public void Bucket_ThisWeek_SplitsOverdue_Due_AndUndated()
    {
        // Window = this week [Sun Aug 2, Sun Aug 9); now = Wed Aug 5.
        var start = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
        var open = new List<OpenAction>
        {
            Task("Send the deck", "You", "Friday"),                 // due Aug 7 → due this week
            Task("Reply to vendor", "You", "last Monday"),          // due Jul 27 → overdue
            Task("Book the venue", "You", null),                    // undated, from this week's meeting
            Task("Long-term research", "You", null, Mon.AddDays(-30)), // undated, OLD meeting → other
            Task("Ship v2", "You", "next Friday"),                  // due Aug 14 → next week → other
        };
        var b = TaskScheduling.Bucket(open, start, end, Now);

        Assert.Single(b.DueInWindow);
        Assert.Contains(b.DueInWindow, a => a.Text == "Send the deck");
        Assert.Single(b.Overdue);
        Assert.Contains(b.Overdue, a => a.Text == "Reply to vendor");
        Assert.Single(b.UndatedFromWindow);
        Assert.Contains(b.UndatedFromWindow, a => a.Text == "Book the venue");
        Assert.Equal(2, b.OtherOpenCount);   // long-term research + ship v2
    }

    [Fact]
    public void Bucket_NextWeek_HasNoOverdue_NoUndated()
    {
        // Pure-future window: only the due-in-window bucket applies.
        var start = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero);
        var open = new List<OpenAction>
        {
            Task("Ship v2", "You", "next Friday"),        // Aug 14 → due next week
            Task("Reply to vendor", "You", "last Monday"),// overdue - but NOT surfaced for a future window
            Task("Book the venue", "You", null),          // undated - NOT surfaced for a future window
        };
        var b = TaskScheduling.Bucket(open, start, end, Now);

        Assert.Single(b.DueInWindow);
        Assert.Empty(b.Overdue);
        Assert.Empty(b.UndatedFromWindow);
    }

    [Fact]
    public void Bucket_OverdueOnly_NoWindow_CollectsPastDue()
    {
        var open = new List<OpenAction>
        {
            Task("Reply to vendor", "You", "last Monday"),  // overdue
            Task("Send the deck", "You", "Friday"),         // future → not overdue
            Task("Book the venue", "You", null),            // undated → not overdue
        };
        var b = TaskScheduling.Bucket(open, null, null, Now);
        Assert.Single(b.Overdue);
        Assert.Contains(b.Overdue, a => a.Text == "Reply to vendor");
        Assert.Empty(b.DueInWindow);
    }
}

/// <summary>End-to-end: the deadline-scoped task route through the real store seam.</summary>
[Collection("NotesQaSeams")]
public sealed class DueWindowAnswerTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"toki-task-{Guid.NewGuid():N}.db");
    readonly MeetingStore _store;

    public DueWindowAnswerTests()
    {
        _store = new MeetingStore(_dbPath);
        NotesQa.StoreOverride = _store;
        UserProfile.SettingsOverride = new AppSettings();
    }

    public void Dispose()
    {
        NotesQa.StoreOverride = null;
        UserProfile.SettingsOverride = null;
        try { File.Delete(_dbPath); } catch { }
    }

    static Task<NotesQa.Answer> Ask(string q)
        => NotesQa.AskStreamAsync(q, _ => { }, _ => { }, default, null, concise: false, screenMeetingId: null);

    MeetingRecord MeetingOn(DateTimeOffset at, string title, params ActionItem[] actions) => new()
    {
        Kind = MeetingKind.Meeting, Title = title, StartedAt = at,
        Notes = new NoteSet { Tldr = title, Actions = actions.ToList() },
        Transcript = [new TranscriptSegment(Speaker.You, 0, 900, title)],
    };

    // THE reported bug: "what tasks do I have for this week" listed every open task. Now it must
    // scope to this week's deadlines, not every task from every (recent) meeting.
    [Fact]
    public async Task TasksThisWeek_ScopesByDueDate_NotEveryOpenTask()
    {
        var now = DateTimeOffset.Now;
        // MONDAY-anchored, because the product's temporal parser is (NotesQa.WindowLabel). Anchored
        // on Sunday this test failed one day in seven: run it ON a Sunday and "weekStart + 4" lands
        // in the NEXT Monday-start week, so the task it plants is legitimately out of scope.
        var weekStart = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        // A meeting THIS week with a mix of due dates.
        _store.Save(MeetingOn(weekStart.AddDays(1),   // Monday this week
            "Sprint planning",
            new ActionItem { Text = "Send the deck", Owner = "You", Due = weekStart.AddDays(4).ToString("MMMM d") }, // this week
            new ActionItem { Text = "Ship the big refactor", Owner = "You", Due = weekStart.AddDays(20).ToString("MMMM d") })); // 3 weeks out
        // An OLD meeting with an open task - must NOT appear under "this week".
        _store.Save(MeetingOn(now.AddDays(-40), "Ancient sync",
            new ActionItem { Text = "Some old backlog item", Owner = "You", Due = null }));

        var ans = await Ask("what tasks do I have for this week");

        Assert.Contains("Send the deck", ans.Text);
        Assert.DoesNotContain("Some old backlog item", ans.Text);   // the dump is gone (the reported bug)
        Assert.DoesNotContain("big refactor", ans.Text);            // due 3 weeks out, not this week
        // Scope-echo: the answer states the window it interpreted.
        Assert.True(ans.Text.Contains("this week", StringComparison.OrdinalIgnoreCase)
                    || ans.Text.Contains("between", StringComparison.OrdinalIgnoreCase),
                    $"expected a window scope-echo, got: {ans.Text}");
    }

    [Fact]
    public async Task Overdue_ListsOnlyPastDue()
    {
        var now = DateTimeOffset.Now;
        _store.Save(MeetingOn(now.AddDays(-10), "Old planning",
            new ActionItem { Text = "Overdue vendor reply", Owner = "You", Due = now.AddDays(-3).ToString("MMMM d") },
            new ActionItem { Text = "Future launch prep", Owner = "You", Due = now.AddDays(10).ToString("MMMM d") }));

        var ans = await Ask("what's overdue?");

        Assert.Contains("Overdue", ans.Text);
        Assert.Contains("vendor reply", ans.Text);
        Assert.DoesNotContain("launch prep", ans.Text);
    }

    [Fact]
    public async Task NothingDueThisWeek_IsHonest_WithRecovery()
    {
        var now = DateTimeOffset.Now;
        _store.Save(MeetingOn(now.AddDays(-40), "Old sync",
            new ActionItem { Text = "A distant task", Owner = "You", Due = now.AddDays(60).ToString("MMMM d") }));

        var ans = await Ask("what do I have due this week");

        Assert.Contains("Nothing", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("open task", ans.Text, StringComparison.OrdinalIgnoreCase);   // recovery hint
        Assert.DoesNotContain("• A distant task", ans.Text);
    }
}
