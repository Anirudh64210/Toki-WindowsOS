using Toki.Llm;
using Xunit;

namespace Toki.Tests;

/// <summary>The deterministic "understand the query first" layer — the three P2.1 bug fixes live here.</summary>
public sealed class QueryUnderstandingTests
{
    // Fixed reference date so date parsing is stable in CI.
    static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    static QueryPlan Parse(string q) => QueryUnderstanding.Parse(q, Now);

    [Theory]
    [InlineData("summarise my most recent meeting", QueryIntent.SummarizeOne)]
    [InlineData("what happened in my last meeting", QueryIntent.SummarizeOne)]
    [InlineData("what did we decide about the survey forms", QueryIntent.LookupFact)]
    [InlineData("AI tool review and survey follow up", QueryIntent.LookupFact)]   // bug #3: NOT tasks
    [InlineData("what are my open tasks", QueryIntent.ListTasks)]
    [InlineData("action items in the postgres meeting", QueryIntent.ListTasks)]
    [InlineData("what meetings did I have last week", QueryIntent.ListMeetings)]
    [InlineData("how many meetings in March", QueryIntent.Count)]
    [InlineData("who was worried about replication lag", QueryIntent.LookupFact)]
    public void ClassifiesIntent(string query, QueryIntent expected)
        => Assert.Equal(expected, Parse(query).Intent);

    [Fact]
    public void MostRecent_IsARecencyRank_NotADate()   // bug #1
    {
        var p = Parse("summarise my most recent meeting");
        Assert.Equal(RecencyDir.Latest, p.Rank);
        Assert.False(p.HasDateRange);
    }

    [Fact]
    public void SpecificDate_ParsesToThatDay()   // bug #2
    {
        var p = Parse("meeting on July 30");
        Assert.True(p.HasDateRange);
        Assert.Equal(new DateTime(2026, 7, 30), p.RangeStart!.Value.Date);
        Assert.Equal(new DateTime(2026, 7, 31), p.RangeEnd!.Value.Date);   // exclusive end = next day
    }

    [Fact]
    public void LastWeek_ParsesToASevenDayPastRange()
    {
        var p = Parse("what meetings did I have last week");
        Assert.True(p.HasDateRange);
        Assert.True(p.RangeEnd!.Value <= Now, "range should be in the past");
        Assert.InRange((p.RangeEnd!.Value - p.RangeStart!.Value).TotalDays, 6.5, 7.5);
    }

    [Fact]
    public void FollowUpInAMeetingName_DoesNotTriggerTasks()   // bug #3, explicit
        => Assert.NotEqual(QueryIntent.ListTasks, Parse("AI tool review and survey follow up").Intent);

    [Fact]
    public void PlainTopicQuestion_IsLookupFact()
        => Assert.Equal(QueryIntent.LookupFact, Parse("why was the launch delayed").Intent);

    [Fact]
    public void MostRecentMeetingAbout_Summarizes_NotList()   // screenshot bug
        => Assert.Equal(QueryIntent.SummarizeOne, Parse("what was the most recent meeting about").Intent);

    [Fact]
    public void PluralMeetings_IsAListRequest()
        => Assert.Equal(QueryIntent.ListMeetings, Parse("what meetings did I have this week").Intent);

    // ── follow-ups ──
    static ConversationContext Ctx(QueryIntent intent, DateTimeOffset? s = null, DateTimeOffset? e = null, params long[] ids)
        => new() { LastIntent = intent, LastRangeStart = s, LastRangeEnd = e, LastMeetingIds = ids.ToList() };

    [Fact]
    public void BeforeThat_ShiftsRangeEarlier()
    {
        var ctx = Ctx(QueryIntent.Count, new DateTimeOffset(2026, 7, 20, 0, 0, 0, default), new DateTimeOffset(2026, 7, 27, 0, 0, 0, default));
        var p = QueryUnderstanding.ResolveFollowUp("before that", ctx, Now);
        Assert.NotNull(p);
        Assert.Equal(QueryIntent.Count, p!.Intent);
        Assert.Equal(new DateTime(2026, 7, 13), p.RangeStart!.Value.Date);   // one week earlier
        Assert.Equal(new DateTime(2026, 7, 20), p.RangeEnd!.Value.Date);
    }

    [Fact]
    public void WhatAboutActionItems_PinsPriorMeetingAsTasks()
    {
        var p = QueryUnderstanding.ResolveFollowUp("what about the action items", Ctx(QueryIntent.SummarizeOne, ids: 42), Now);
        Assert.NotNull(p);
        Assert.Equal(QueryIntent.ListTasks, p!.Intent);
        Assert.Equal(new List<long> { 42 }, p.PinnedMeetingIds);
    }

    [Fact]
    public void SummarizeIt_PinsPriorMeetingAsSummary()
    {
        var p = QueryUnderstanding.ResolveFollowUp("summarize it", Ctx(QueryIntent.LookupFact, ids: 7), Now);
        Assert.NotNull(p);
        Assert.Equal(QueryIntent.SummarizeOne, p!.Intent);
    }

    [Fact]
    public void NoContext_IsNotAFollowUp()
        => Assert.Null(QueryUnderstanding.ResolveFollowUp("before that", new ConversationContext(), Now));
}
