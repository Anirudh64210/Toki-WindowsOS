using Toki.Llm;
using Xunit;

namespace Toki.Tests.Brain;

/// <summary>
/// Temporal recall golden set (taxonomy §2) — frozen-clock assertions on QueryUnderstanding.Parse:
/// window semantics, recency ranks, past/future, and the specific-past-date rule (§2.3 / Rule D).
/// NOW = Wed 2026-08-05; this week = Mon Aug 3 – Sun Aug 9.
/// </summary>
public sealed class TemporalGoldenTests
{
    static readonly DateTimeOffset Now = BrainFixture.Now;   // Wed 2026-08-05 12:00Z
    static QueryPlan P(string q) => QueryUnderstanding.Parse(q, Now);

    // ── §2.1/§2.7 windowed recall parses to a date range (recap intent) ──
    [Theory]
    [InlineData("what happened this week")]
    [InlineData("recap this week")]
    [InlineData("summarize this week's meetings")]
    [InlineData("what did I do last week")]
    [InlineData("recap last week")]
    [InlineData("what went on this month")]
    [InlineData("sum up last month")]
    [InlineData("what happened yesterday")]
    [InlineData("recap today")]
    [InlineData("what happened on August 2nd")]
    [InlineData("recap Monday")]
    [InlineData("what did we cover Tuesday")]
    [InlineData("meetings in July")]
    [InlineData("what happened in Q2")]
    [InlineData("recap June")]
    [InlineData("between July 1 and July 15")]
    [InlineData("three days ago")]
    [InlineData("the day before yesterday")]
    public void WindowedRecall_HasADateRange(string q)
        => Assert.True(P(q).HasDateRange, $"'{q}' produced no date range");

    // ── past windows resolve to the PAST (meetings are history) ──
    [Theory]
    [InlineData("what did I do last week")]
    [InlineData("recap last month")]
    [InlineData("what happened yesterday")]
    [InlineData("meetings on August 2nd")]
    [InlineData("recap June")]
    public void PastWindow_EndsAtOrBeforeNow(string q)
    {
        var p = P(q);
        Assert.True(p.HasDateRange);
        Assert.True(p.RangeEnd!.Value <= Now.AddDays(1), $"'{q}' end {p.RangeEnd} is in the future");
    }

    // ── specific past date = that calendar day (Rule D.2) ──
    [Theory]
    [InlineData("meetings on July 30", 2026, 7, 30)]
    [InlineData("what happened on August 2nd", 2026, 8, 2)]
    [InlineData("notes from August 1", 2026, 8, 1)]
    [InlineData("the call on July 15th", 2026, 7, 15)]
    public void SpecificDate_ResolvesToThatDay(string q, int y, int m, int d)
    {
        var p = P(q);
        Assert.True(p.HasDateRange, $"'{q}' had no range");
        Assert.Equal(new DateTime(y, m, d), p.RangeStart!.Value.Date);
        Assert.Equal(new DateTime(y, m, d).AddDays(1), p.RangeEnd!.Value.Date);   // one-day, exclusive end
    }

    // ── "last week" is a ~7-day span ending at/before this week's start ──
    [Fact]
    public void LastWeek_IsSevenDays_InThePast()
    {
        var p = P("what did I do last week");
        Assert.InRange((p.RangeEnd!.Value - p.RangeStart!.Value).TotalDays, 6.5, 7.5);
        Assert.True(p.RangeEnd.Value.Date <= new DateTime(2026, 8, 4));
    }

    // ── yesterday = the single prior day ──
    [Fact]
    public void Yesterday_IsAugustFourth()
    {
        var p = P("what happened yesterday");
        Assert.Equal(new DateTime(2026, 8, 4), p.RangeStart!.Value.Date);
    }

    // ── §2.6 recency ranks are NOT date ranges (they're "the latest/oldest meeting") ──
    [Theory]
    [InlineData("summarize my most recent meeting", RecencyDir.Latest)]
    [InlineData("what was my last meeting", RecencyDir.Latest)]
    [InlineData("my latest call", RecencyDir.Latest)]
    [InlineData("what was the newest note", RecencyDir.Latest)]
    [InlineData("my oldest meeting", RecencyDir.Oldest)]
    [InlineData("what was my first meeting", RecencyDir.Oldest)]
    [InlineData("my earliest recording", RecencyDir.Oldest)]
    public void RecencyRank_IsARank_NotADate(string q, RecencyDir dir)
    {
        var p = P(q);
        Assert.Equal(dir, p.Rank);
        Assert.False(p.HasDateRange);
    }

    // ── a bare non-temporal topic question has NO temporal ──
    [Theory]
    [InlineData("what did we decide about pricing")]
    [InlineData("who owns the migration")]
    [InlineData("why was the launch delayed")]
    [InlineData("what are my open tasks")]
    public void NonTemporalQuestion_HasNoTemporal(string q)
        => Assert.False(P(q).HasTemporal, $"'{q}' wrongly parsed a temporal");

    // ── intent for temporal shapes: plural "meetings" list vs single-meeting recap ──
    [Theory]
    [InlineData("what meetings did I have last week", QueryIntent.ListMeetings)]
    [InlineData("list my meetings this week", QueryIntent.ListMeetings)]
    [InlineData("how many meetings this week", QueryIntent.Count)]
    [InlineData("summarize yesterday's meeting", QueryIntent.SummarizeOne)]
    [InlineData("recap my most recent meeting", QueryIntent.SummarizeOne)]
    public void TemporalIntent_Classifies(string q, QueryIntent expected)
        => Assert.Equal(expected, P(q).Intent);

    // ── counting over a window is deterministic Count ──
    [Theory]
    [InlineData("how many meetings did I have this week")]
    [InlineData("how many calls last month")]
    [InlineData("number of meetings in July")]
    public void CountOverWindow_IsCount(string q)
        => Assert.Equal(QueryIntent.Count, P(q).Intent);
}
