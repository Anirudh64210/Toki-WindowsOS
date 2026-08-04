using Toki.Llm;
using Xunit;

namespace Toki.Tests.Brain;

/// <summary>
/// Follow-up / anaphora golden set (taxonomy §9): conversation memory resolves "what about last week",
/// "and the one before that", "what about the action items", "summarize it" against the prior turn.
/// Deterministic (QueryUnderstanding.ResolveFollowUp), frozen clock.
/// </summary>
public sealed class FollowupGoldenTests
{
    static readonly DateTimeOffset Now = BrainFixture.Now;

    static ConversationContext Ctx(QueryIntent intent, DateTimeOffset? s = null, DateTimeOffset? e = null, params long[] ids)
        => new() { LastIntent = intent, LastRangeStart = s, LastRangeEnd = e, LastMeetingIds = ids.ToList() };

    // ── "before that / after that" shift the previous window ──
    [Theory]
    [InlineData("before that")]
    [InlineData("the week before")]
    [InlineData("and the one before")]
    [InlineData("the previous one")]
    public void BeforeThat_ShiftsRangeEarlier(string q)
    {
        var ctx = Ctx(QueryIntent.Count, new DateTimeOffset(2026, 7, 20, 0, 0, 0, default), new DateTimeOffset(2026, 7, 27, 0, 0, 0, default));
        var p = QueryUnderstanding.ResolveFollowUp(q, ctx, Now);
        Assert.NotNull(p);
        Assert.True(p!.RangeStart!.Value < new DateTimeOffset(2026, 7, 20, 0, 0, 0, default));
    }

    [Theory]
    [InlineData("after that")]
    [InlineData("the week after")]
    [InlineData("the next one")]
    public void AfterThat_ShiftsRangeLater(string q)
    {
        var ctx = Ctx(QueryIntent.Count, new DateTimeOffset(2026, 7, 20, 0, 0, 0, default), new DateTimeOffset(2026, 7, 27, 0, 0, 0, default));
        var p = QueryUnderstanding.ResolveFollowUp(q, ctx, Now);
        Assert.NotNull(p);
        Assert.True(p!.RangeStart!.Value >= new DateTimeOffset(2026, 7, 27, 0, 0, 0, default));
    }

    // ── aspect switch on the pinned meeting: tasks / summary ──
    [Theory]
    [InlineData("what about the action items")]
    [InlineData("and the action items")]
    [InlineData("what about tasks")]
    [InlineData("its action items")]
    public void WhatAboutTasks_PinsPriorMeetingAsTasks(string q)
    {
        var p = QueryUnderstanding.ResolveFollowUp(q, Ctx(QueryIntent.SummarizeOne, ids: 42), Now);
        Assert.NotNull(p);
        Assert.Equal(QueryIntent.ListTasks, p!.Intent);
        Assert.Contains(42L, p.PinnedMeetingIds!);
    }

    [Theory]
    [InlineData("summarize it")]
    [InlineData("what was it about")]
    [InlineData("tell me more")]
    [InlineData("recap")]
    public void SummarizeIt_PinsPriorMeetingAsSummary(string q)
    {
        var p = QueryUnderstanding.ResolveFollowUp(q, Ctx(QueryIntent.LookupFact, ids: 7), Now);
        Assert.NotNull(p);
        Assert.Equal(QueryIntent.SummarizeOne, p!.Intent);
    }

    // ── bare temporal continuation keeps the previous intent ──
    [Theory]
    [InlineData("this week")]
    [InlineData("last week")]
    [InlineData("in July")]
    public void BareTemporalContinuation_KeepsIntent(string q)
    {
        var ctx = Ctx(QueryIntent.ListMeetings, new DateTimeOffset(2026, 7, 20, 0, 0, 0, default), new DateTimeOffset(2026, 7, 27, 0, 0, 0, default));
        var p = QueryUnderstanding.ResolveFollowUp(q, ctx, Now);
        Assert.NotNull(p);
        Assert.Equal(QueryIntent.ListMeetings, p!.Intent);
        Assert.True(p.HasDateRange);
    }

    // ── no context → NOT a follow-up (must be parsed fresh) ──
    [Theory]
    [InlineData("before that")]
    [InlineData("what about the action items")]
    [InlineData("the next one")]
    public void NoContext_IsNotAFollowUp(string q)
        => Assert.Null(QueryUnderstanding.ResolveFollowUp(q, new ConversationContext(), Now));

    // ── a full question is NOT a follow-up even with context (it stands on its own) ──
    [Theory]
    [InlineData("what did we decide about pricing")]
    [InlineData("how many meetings did I have last month")]
    public void StandaloneQuestion_IsNotHijackedByFollowUp(string q)
    {
        var ctx = Ctx(QueryIntent.SummarizeOne, ids: 5);
        var p = QueryUnderstanding.ResolveFollowUp(q, ctx, Now);
        // Either null (parse fresh) or at least not a blind pin to meeting 5 as a summary.
        Assert.True(p is null || p.Intent != QueryIntent.SummarizeOne || p.PinnedMeetingIds is null || !p.PinnedMeetingIds.Contains(5L),
            $"'{q}' was wrongly captured as a follow-up");
    }
}
