using Toki.Llm;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// THE NOTE'S LENGTH BELONGS TO THE MEETING, NOT TO THE CHUNKING.
///
/// The budget was `content.Length > 9000 ? 9 : > 4000 ? 7 : 5`, where `content` is the digest handed
/// to the final pass. Wrong quantity twice: it is an implementation detail of how the transcript was
/// cut up, and `MaxChunkChars` caps it, so the `> 9000` branch could never fire and the true ceiling
/// was SEVEN.
///
/// The `General` template has one section ("Key points"), so seven bullets was the whole body of
/// almost every note, for a three-minute call and a ninety-minute review alike.
///
/// MEASURED 2026-09-03 over ten meetings: the regenerated notes claimed 37 hard facts where the
/// stored ones claimed 75. Roughly ninety verified extracted points were being squeezed into seven
/// lines.
///
/// MUTATION PROBE: the old digest-length formula fails Scales_with_what_was_said and
/// A_long_meeting_earns_more_than_the_old_ceiling.
/// </summary>
public sealed class BulletBudgetTests
{
    [Fact]
    public void Scales_with_what_was_said()
    {
        var tiny = LlmNoteWriter.BulletBudget(1_500);      // a few minutes
        var medium = LlmNoteWriter.BulletBudget(20_000);   // ~20 minutes
        var long_ = LlmNoteWriter.BulletBudget(60_000);    // ~an hour

        Assert.True(medium > tiny, $"a 20-minute meeting must earn more than a 2-minute one ({tiny} then {medium})");
        Assert.True(long_ > medium, $"an hour must earn more than 20 minutes ({medium} then {long_})");
    }

    [Fact]
    public void A_long_meeting_earns_more_than_the_old_ceiling()
    {
        // Seven was the real ceiling of the old formula, for every meeting of any length.
        Assert.True(LlmNoteWriter.BulletBudget(60_000) > 7);
    }

    [Fact]
    public void A_short_meeting_stays_tight()
    {
        // Restraint is the design. A three-minute call does not get room it cannot fill, because an
        // unfillable budget is an invitation to pad and padding is what the citation gate then has
        // to throw away.
        Assert.Equal(5, LlmNoteWriter.BulletBudget(1_000));
    }

    [Fact]
    public void The_ceiling_stays_finite()
    {
        // A note is ranked, not exhaustive. Even a marathon meeting gets a readable page, not a
        // transcript with dots in front of it.
        Assert.True(LlmNoteWriter.BulletBudget(5_000_000) <= 14);
    }
}
