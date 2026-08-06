using System.IO;
using Toki.Llm;
using Toki.Storage;
using Toki.Transcription;
using Xunit;
using TM = Toki.Llm.TaskScheduling.TimeMode;

namespace Toki.Tests.Brain;

/// <summary>
/// The Aug-2 two-meetings screenshot bug: "how many open tasks on 2nd August" answered from ONE
/// topic-matched meeting instead of ALL meetings that day. Root cause: count/quantity words
/// ("many", "count", "number", "total", "much") survived TaskAnswer's topic extraction, so the
/// question took the topic branch (SearchRelevant → best single meeting) instead of the provenance
/// branch (MeetingIdsInRange → every meeting in the window). These tests pin the fix end-to-end
/// through the real Ask pipeline (seeded temp store + fake model), plus frozen-clock parse/mode
/// goldens, and prove a REAL topic + date still scopes to the topic.
/// </summary>
[Collection("NotesQaSeams")]   // shares NotesQa's static seams - never parallel with the other brain tests
public sealed class TaskCountScopeTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"toki-cnt-{Guid.NewGuid():N}.db");
    readonly MeetingStore _store;
    readonly FakeLlm _llm = new();

    // The day both meetings happened - a past day, like the live Aug-2 report (today = Aug 4).
    readonly DateTimeOffset _day = DateTimeOffset.Now.AddDays(-2);

    public TaskCountScopeTests()
    {
        _store = new MeetingStore(_dbPath);
        NotesQa.StoreOverride = _store;
        NotesQa.LlmOverride = _llm;
        UserProfile.SettingsOverride = new AppSettings();
        Seed();
    }

    public void Dispose()
    {
        NotesQa.StoreOverride = null;
        NotesQa.LlmOverride = null;
        UserProfile.SettingsOverride = null;
        try { File.Delete(_dbPath); } catch { }
    }

    sealed class FakeLlm : ILlm
    {
        public string? User;
        public bool Ready => true;
        public Task<string> CompleteAsync(string s, string u, int m, CancellationToken ct = default) => Task.FromResult("ok");
        public async IAsyncEnumerable<string> StreamAsync(string s, string u, int m,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default, IReadOnlyList<string>? stop = null)
        { User = u; yield return "Here's the answer from the notes."; await Task.CompletedTask; }
    }

    MeetingRecord M(DateTimeOffset at, string title, string tldr, params ActionItem[] a) => new()
    {
        Kind = MeetingKind.Meeting, Title = title, StartedAt = at,
        Notes = new NoteSet { Tldr = tldr, Actions = a.ToList() },
        Transcript = [new TranscriptSegment(Speaker.You, 0, 900, tldr)],
    };

    void Seed()
    {
        // TWO meetings on the SAME day, each with open tasks - the live-report shape.
        _store.Save(M(_day.Date.AddHours(10), "SUS Metrics and MR Rate Trends",
            "Reviewed SUS metrics and merge-request rate trends.",
            new ActionItem { Text = "Update the SUS dashboard", Owner = "You" },
            new ActionItem { Text = "Recalculate MR rate baseline", Owner = "Priya" }));
        _store.Save(M(_day.Date.AddHours(15), "SLO Metrics & Open Bug Analysis",
            "Went through SLO metrics and the open bug backlog.",
            new ActionItem { Text = "Triage the open bug backlog", Owner = "You" },
            new ActionItem { Text = "Draft the SLO error budget doc", Owner = "Dev" }));
        // A meeting on ANOTHER day - its tasks must never appear in the date-scoped answer.
        _store.Save(M(DateTimeOffset.Now.AddDays(-9), "Pricing sync",
            "We agreed the one-time $20 price, no subscription.",
            new ActionItem { Text = "Send the pricing deck", Owner = "Ana" }));
    }

    static Task<NotesQa.Answer> Ask(string q)
        => NotesQa.AskStreamAsync(q, _ => { }, _ => { }, default, null, concise: false, screenMeetingId: null);

    // ── THE bug, end-to-end: a date-scoped COUNT/LIST with no real topic covers ALL meetings that day ──
    [Theory]
    [InlineData("how many open tasks on {0}")]
    [InlineData("how many open tasks do I have from {0}")]
    [InlineData("count my open tasks from {0}")]
    [InlineData("number of open tasks on {0}")]
    [InlineData("total open tasks from {0}")]
    [InlineData("how much is on my plate from {0}")]   // "much" + "plate" leaks; self-filtered (You-owned) items
    public async Task DateScopedCount_CoversAllMeetingsThatDay(string template)
    {
        var q = string.Format(template, _day.ToString("MMMM d", System.Globalization.CultureInfo.InvariantCulture));
        var ans = await Ask(q);

        // BOTH same-day meetings' tasks - never just the one FTS liked best.
        Assert.Contains("Update the SUS dashboard", ans.Text);
        Assert.Contains("Triage the open bug backlog", ans.Text);
        // And not the other day's task.
        Assert.DoesNotContain("pricing deck", ans.Text);
        // Never the topic-miss apology ("I couldn't find a meeting about many…").
        Assert.DoesNotContain("couldn't find a meeting", ans.Text);
    }

    // ── an undated count question lists ALL open tasks (previously "many" topic-scoped it) ──
    [Fact]
    public async Task UndatedCount_ListsAllOpenTasks()
    {
        var ans = await Ask("how many open tasks do I have");
        Assert.Contains("Update the SUS dashboard", ans.Text);
        Assert.Contains("Triage the open bug backlog", ans.Text);
        Assert.Contains("pricing deck", ans.Text);
    }

    // ── a REAL topic + date still scopes to the topic-matched meeting only ──
    [Fact]
    public async Task TopicPlusDate_StillScopesToTheTopic()
    {
        var q = $"action items about SLO on {_day.ToString("MMMM d", System.Globalization.CultureInfo.InvariantCulture)}";
        var ans = await Ask(q);
        Assert.Contains("Triage the open bug backlog", ans.Text);
        Assert.DoesNotContain("SUS dashboard", ans.Text);   // the other same-day meeting stays out
    }

    // ── frozen-clock goldens: intent + provenance mode for the count phrasings (NOW = Wed 2026-08-05) ──
    static readonly DateTimeOffset Now = BrainFixture.Now;

    [Theory]
    [InlineData("how many open tasks on 2nd august")]
    [InlineData("how many open tasks do i have from august 2nd")]
    [InlineData("count my open tasks from august 2")]
    [InlineData("number of open tasks on august 2")]
    [InlineData("total open tasks from august 2nd")]
    public void CountPhrasings_ParseToProvenanceTaskQueries(string q)
    {
        var p = QueryUnderstanding.Parse(q, Now);
        Assert.Equal(QueryIntent.ListTasks, p.Intent);
        Assert.True(p.HasDateRange, $"'{q}' should carry the Aug-2 window");
        Assert.Equal(TM.Provenance, TaskScheduling.ModeFor(p.Original, p.RangeStart, p.RangeEnd, Now));
    }

    // ── a count phrasing WITH a deadline cue keeps the due-date view (not provenance) ──
    [Fact]
    public void CountWithDueCue_StaysDueDate()
    {
        var p = QueryUnderstanding.Parse("how many tasks are due this week", Now);
        Assert.Equal(QueryIntent.ListTasks, p.Intent);
        Assert.Equal(TM.DueDate, TaskScheduling.ModeFor(p.Original, p.RangeStart, p.RangeEnd, Now));
    }
}
