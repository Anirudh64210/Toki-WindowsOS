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

    // ── P9.1: oldest/first must parse as a recency rank even with "recorded"/"title" chrome around
    //    it (the live run answered "the notes do not mention any recorded meeting dates or titles"). ──
    [Theory]
    [InlineData("what was the oldest meeting that i recorded", RecencyDir.Oldest)]
    [InlineData("what was the first meeting i had", RecencyDir.Oldest)]
    [InlineData("what was my earliest meeting", RecencyDir.Oldest)]
    [InlineData("what was the title of the most recent meeting i had", RecencyDir.Latest)]
    public void RecencyRank_ParsesThroughChrome(string q, RecencyDir expected)
        => Assert.Equal(expected, Parse(q).Rank);

    // ── P9.1: self-assignment shapes without a task noun still route to the task path ──
    [Theory]
    [InlineData("did i get anything?", QueryIntent.ListTasks)]
    [InlineData("was i assigned anything in this meeting", QueryIntent.ListTasks)]
    [InlineData("anything for me?", QueryIntent.ListTasks)]
    [InlineData("did they mention me or give me any tasks in this meeting?", QueryIntent.ListTasks)]
    public void SelfAssignmentShapes_AreTaskQueries(string q, QueryIntent expected)
        => Assert.Equal(expected, Parse(q).Intent);

    // …but ordinary content questions with "get/give" stay lookups (no over-trigger).
    [Theory]
    [InlineData("did i get the budget approved", QueryIntent.LookupFact)]
    [InlineData("what details did we give the vendor", QueryIntent.LookupFact)]
    public void GetGive_ContentQuestions_StayLookups(string q, QueryIntent expected)
        => Assert.Equal(expected, Parse(q).Intent);

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

    // ── screenshot bug: a TOPICAL question mentioning "my meetings" is a LOOKUP, not a list dump ──
    [Theory]
    [InlineData("What was discussed about Kai Sinat in my meetings?")]
    [InlineData("what did we say about the pricing page in my meetings")]
    [InlineData("which meetings mentioned the budget")]
    [InlineData("show me what my meetings said about onboarding")]
    public void TopicInsideAMeetingsMention_IsALookup_NotAList(string q)
        => Assert.Equal(QueryIntent.LookupFact, Parse(q).Intent);

    // Pure list requests (only list-chrome words) must STAY list requests.
    [Theory]
    [InlineData("what meetings did I have last week")]
    [InlineData("show me all my meetings")]
    [InlineData("list my meetings")]
    [InlineData("what did we discuss in my meetings this week")]
    public void PureListRequest_IsStillAList(string q)
        => Assert.Equal(QueryIntent.ListMeetings, Parse(q).Intent);

    // ── the ANALYTICAL route: thinking/synthesis questions get their own intent (broader retrieval +
    //    a reasoning prompt + a real token budget), instead of falling into the narrow lookup path ──
    [Theory]
    [InlineData("What should I focus on this week?")]
    [InlineData("what are my biggest risks or blockers")]
    [InlineData("what is the team worried about")]
    [InlineData("what are the main themes across my SLO meetings")]
    [InlineData("how is the MR rate work progressing")]
    [InlineData("compare what we decided about MR rates vs SLOs")]
    [InlineData("what's still unresolved")]
    [InlineData("did we make progress since last week")]
    [InlineData("what do you think I should prioritize")]
    [InlineData("any advice based on my meetings")]
    public void AnalyticalQuestion_IsAnalyze(string q)
        => Assert.Equal(QueryIntent.Analyze, Parse(q).Intent);

    // The deterministic P8 wins must NOT be stolen by the new route.
    [Theory]
    [InlineData("what did we decide about the survey forms", QueryIntent.LookupFact)]
    [InlineData("who was worried about replication lag", QueryIntent.LookupFact)]   // who-lead = factual
    [InlineData("summarize my last meeting", QueryIntent.SummarizeOne)]
    [InlineData("what happened in my last meeting", QueryIntent.SummarizeOne)]
    [InlineData("what are my open tasks", QueryIntent.ListTasks)]
    [InlineData("was anything assigned to me in this meeting", QueryIntent.ListTasks)]
    [InlineData("show me all my meetings", QueryIntent.ListMeetings)]
    [InlineData("how many meetings in March", QueryIntent.Count)]
    [InlineData("What was discussed about Kai Sinat in my meetings?", QueryIntent.LookupFact)]
    public void AnalyzeRoute_DoesNotStealDeterministicIntents(string q, QueryIntent expected)
        => Assert.Equal(expected, Parse(q).Intent);

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
