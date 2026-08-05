using System;
using System.Collections.Generic;
using System.Linq;
using Toki.Storage;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>N7 — pointing a note bullet at the transcript line it came from. The governing rule:
/// a link to the WRONG line is worse than no link, because it makes the note look like it's citing
/// something it isn't. Silence beats a confident mistake.</summary>
public sealed class EvidenceTests
{
    static IReadOnlyList<TranscriptSegment> T(params string[] lines) =>
        lines.Select((l, i) => new TranscriptSegment(
            i % 2 == 0 ? Speaker.You : Speaker.Them, i * 5000, i * 5000 + 4000, l)).ToList();

    static readonly IReadOnlyList<TranscriptSegment> Meeting = T(
        /*0*/ "morning everyone, can you hear me alright",
        /*1*/ "yeah all good, let's get started",
        /*2*/ "so the launch blockers are the brand tokens and the Make webhook",
        /*3*/ "Priya said she'd confirm the brand font and the hex codes",
        /*4*/ "we should move the ship date to Thursday then",
        /*5*/ "agreed, Thursday works for the pricing page too",
        /*6*/ "okay great, talk next week");

    [Fact]
    public void A_bullet_points_at_the_line_that_produced_it()
    {
        Assert.Equal(2, Evidence.BestSegment("Launch blockers are brand tokens and the Make webhook", Meeting));
        Assert.Equal(4, Evidence.BestSegment("Ship date moved to Thursday", Meeting));
    }

    [Fact]
    public void A_paraphrase_still_finds_its_line()
    {
        // The model rewords things; the link has to survive that.
        Assert.Equal(3, Evidence.BestSegment("Priya will confirm the brand font and hex codes", Meeting));
    }

    [Fact]
    public void A_bullet_about_nothing_in_the_transcript_links_to_nothing()
    {
        Assert.Null(Evidence.BestSegment("Budget approval for the new hires in Berlin", Meeting));
        Assert.Null(Evidence.BestSegment("Reviewed the quarterly compliance audit", Meeting));
    }

    [Fact]
    public void Generic_filler_never_earns_a_link()
    {
        // Bullets made of stopwords would otherwise match everything.
        Assert.Null(Evidence.BestSegment("We should do it", Meeting));
        Assert.Null(Evidence.BestSegment("Yes", Meeting));
        Assert.Null(Evidence.BestSegment("It was agreed", Meeting));
    }

    [Fact]
    public void Empty_and_missing_input_is_safe()
    {
        Assert.Null(Evidence.BestSegment(null, Meeting));
        Assert.Null(Evidence.BestSegment("", Meeting));
        Assert.Null(Evidence.BestSegment("   ", Meeting));
        Assert.Null(Evidence.BestSegment("anything at all", null));
        Assert.Null(Evidence.BestSegment("anything at all", Array.Empty<TranscriptSegment>()));
    }

    [Fact]
    public void A_point_split_across_two_utterances_is_found()
    {
        var split = T(
            "the pricing page needs the new tiers",
            "and the renewal terms updating as well");
        // Neither line alone carries the whole bullet; together they do.
        Assert.Equal(0, Evidence.BestSegment("Pricing page needs new tiers and renewal terms updating", split));
    }

    [Fact]
    public void The_tightest_matching_line_wins_over_a_sprawl()
    {
        var t = T(
            "webhook",
            "the Make webhook is the blocker for launch",
            "unrelated chatter about lunch");
        Assert.Equal(1, Evidence.BestSegment("The Make webhook is the launch blocker", t));
    }

    [Fact]
    public void Plurals_do_not_break_the_match()
    {
        var t = T("the blocker is the token refresh");
        Assert.Equal(0, Evidence.BestSegment("Blockers: token refresh", t));
    }

    [Fact]
    public void Greetings_never_win()
    {
        // Line 0 and 6 are pure logistics — no real bullet should ever land on them.
        foreach (var bullet in new[]
                 {
                     "Launch blockers are brand tokens and the Make webhook",
                     "Priya will confirm the brand font and hex codes",
                     "Ship date moved to Thursday",
                 })
        {
            var idx = Evidence.BestSegment(bullet, Meeting);
            Assert.NotNull(idx);
            Assert.DoesNotContain(idx!.Value, new[] { 0, 1, 6 });
        }
    }
}
