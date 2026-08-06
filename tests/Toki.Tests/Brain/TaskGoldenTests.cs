using Toki.Llm;
using Xunit;
using TM = Toki.Llm.TaskScheduling.TimeMode;

namespace Toki.Tests.Brain;

/// <summary>
/// Task-query golden set (taxonomy §3, Rule T) - the provenance-vs-due-date decision (the "August 2nd"
/// fix) as a frozen-clock pure-function suite, plus task routing. NOW = Wed 2026-08-05.
/// </summary>
public sealed class TaskGoldenTests
{
    static readonly DateTimeOffset Now = BrainFixture.Now;
    static QueryPlan P(string q) => QueryUnderstanding.Parse(q, Now);

    static TM Mode(string q)
    {
        var p = P(q);
        return TaskScheduling.ModeFor(p.Original, p.RangeStart, p.RangeEnd, Now);
    }

    // ── Rule T.1: an explicit deadline cue → DUE-DATE, always ──
    [Theory]
    [InlineData("what's due this week")]
    [InlineData("what's due on Friday")]
    [InlineData("anything due tomorrow")]
    [InlineData("what's due today")]
    [InlineData("deadlines this week")]
    [InlineData("what tasks are due next week")]
    [InlineData("what's due by the end of the week")]
    public void DeadlineCue_IsDueDate(string q) => Assert.Equal(TM.DueDate, Mode(q));

    // ── Rule T.3 + §2.3 / the August-2nd fix: a bare PAST date/window (no deadline cue) → PROVENANCE ──
    [Theory]
    [InlineData("what tasks did I have on August 2nd")]
    [InlineData("what tasks did I have on July 30")]
    [InlineData("action items from last week")]
    [InlineData("tasks from yesterday")]
    [InlineData("what did I commit to on Monday")]
    [InlineData("what were the action items from Tuesday")]
    [InlineData("what came out of yesterday's meeting")]
    public void PastDateOrRecall_IsProvenance(string q) => Assert.Equal(TM.Provenance, Mode(q));

    // ── overdue with NO window → DueDate (a deadline view, no meeting-date scope) ──
    [Theory]
    [InlineData("what's overdue")]
    [InlineData("am I behind on anything")]
    [InlineData("what's past due")]
    public void Overdue_IsDueDate(string q) => Assert.Equal(TM.DueDate, Mode(q));

    // ── no time reference at all → None (the plain open-tasks list) ──
    [Theory]
    [InlineData("what are my open tasks")]
    [InlineData("what's on my plate")]
    [InlineData("what do I need to do")]
    [InlineData("my to-dos")]
    public void NoTime_IsNone(string q) => Assert.Equal(TM.None, Mode(q));

    // ── all of these route to the deterministic ListTasks intent ──
    [Theory]
    [InlineData("what are my open tasks")]
    [InlineData("what's on my plate")]
    [InlineData("what do I need to do")]
    [InlineData("my action items")]
    [InlineData("what did I commit to")]
    [InlineData("what's assigned to me")]
    [InlineData("what does Ana owe me")]
    [InlineData("Ana's action items")]
    [InlineData("what's due this week")]
    [InlineData("what's overdue")]
    [InlineData("anything due tomorrow")]
    [InlineData("what tasks did I have on August 2nd")]
    [InlineData("how many open tasks do I have")]   // Count is a task-ish deterministic route
    [InlineData("did I get any action items")]
    [InlineData("was I assigned anything")]
    public void TaskQueries_RouteToTaskIntent(string q)
    {
        var intent = P(q).Intent;
        Assert.True(intent is QueryIntent.ListTasks or QueryIntent.Count, $"'{q}' → {intent}");
    }

    // ── a bare FUTURE date with a task noun (no deadline word) → DueDate (Rule T.4): you can't have
    //    provenance from a meeting that hasn't happened. "next week" is future → DueDate. ──
    [Fact]
    public void FutureWindow_IsDueDate()
        => Assert.Equal(TM.DueDate, Mode("what are my tasks for next week"));

    // ── ModeFor direct unit cases (explicit windows, no parser) ──
    [Fact]
    public void ModeFor_PastWindow_NoCue_IsProvenance()
    {
        var start = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);      // ends before NOW
        Assert.Equal(TM.Provenance, TaskScheduling.ModeFor("action items from last week", start, end, Now));
    }

    [Fact]
    public void ModeFor_DeadlineCue_EvenPastWindow_IsDueDate()
    {
        var start = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(TM.DueDate, TaskScheduling.ModeFor("what was due last week", start, end, Now));
    }
}
