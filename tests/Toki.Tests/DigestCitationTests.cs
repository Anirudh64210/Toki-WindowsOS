using System.Collections.Generic;
using System.Linq;
using Toki.Llm;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// The map step is where invention entered.
///
/// It used to emit paraphrased dash bullets, so by the time the writer ran the transcript was gone
/// and it had nothing left to check itself against - a 40-minute meeting's note was written from a
/// paraphrase of a paraphrase, and a name the map step misheard became a fact. It now cites the
/// transcript line every point came from, and <see cref="LlmNoteWriter.ValidateDigest"/> checks the
/// citation actually holds up.
///
/// A citation nothing verifies is decoration, so these tests are the verification of the
/// verification. The balance they pin is the whole difficulty: strict enough that a made-up point
/// cannot cite its way in, loose enough that tidying up spoken words is still allowed.
/// </summary>
public sealed class DigestCitationTests
{
    static IReadOnlyList<TranscriptSegment> Transcript() => new List<TranscriptSegment>
    {
        new(Speaker.Them, 0, 2000, "The database migration finished on August twenty first, two days ahead of schedule."),
        new(Speaker.You, 2000, 4000, "Craig found one issue with the invoice table."),
        new(Speaker.Them, 4000, 6000, "Abhishek patched it the same evening."),
        new(Speaker.You, 6000, 8000, "We are at seventy percent of the quarterly allocation."),
    };

    static (string Text, int First, int Last)? Run(string digest) =>
        LlmNoteWriter.ValidateDigest(digest, Transcript(), 0, 3);

    [Fact]
    public void A_point_that_cites_its_line_survives()
    {
        var r = Run("[1] Craig found one issue with the invoice table");

        Assert.NotNull(r);
        Assert.Contains("Craig found one issue", r!.Value.Text);
    }

    /// <summary>An uncited point is unverifiable, and unverifiable is exactly what this step exists to
    /// stop. It does not get the benefit of the doubt.</summary>
    [Fact]
    public void An_uncited_point_is_dropped()
    {
        Assert.Null(Run("- Craig found one issue with the invoice table"));
    }

    /// <summary>THE CASE THIS WHOLE MECHANISM EXISTS FOR: a plausible sentence, correctly formatted,
    /// citing a real line, that the meeting never contained.</summary>
    [Fact]
    public void A_point_the_cited_line_does_not_support_is_dropped()
    {
        Assert.Null(Run("[1] Priyanka agreed to take over the vendor renewal next quarter"));
    }

    [Fact]
    public void A_citation_outside_the_window_is_dropped()
    {
        Assert.Null(Run("[97] Craig found one issue with the invoice table"));
    }

    /// <summary>The map step is allowed to TRIM AND TIDY spoken words - that is most of its value.
    /// Requiring a verbatim match would reject good extraction and push the model back toward
    /// paraphrase, which is the thing being fixed.</summary>
    [Fact]
    public void A_tidied_line_still_passes()
    {
        var r = Run("[0] database migration finished August twenty first, two days ahead of schedule");
        Assert.NotNull(r);
    }

    /// <summary>A point can straddle two transcript lines after turn-merging, so the neighbours count.
    /// Being strict here would throw away real content to enforce a formatting preference.</summary>
    [Fact]
    public void A_point_supported_by_the_neighbouring_line_passes()
    {
        var r = Run("[1] Abhishek patched the invoice table issue the same evening");
        Assert.NotNull(r);
    }

    /// <summary>Provenance narrows to what actually survived, because a tighter range is a better
    /// evidence link than the whole window.</summary>
    [Fact]
    public void The_range_narrows_to_the_surviving_citations()
    {
        var r = Run(
            "[1] Craig found one issue with the invoice table\n" +
            "[2] Abhishek patched it the same evening");

        Assert.NotNull(r);
        Assert.Equal(1, r!.Value.First);
        Assert.Equal(2, r.Value.Last);
    }

    /// <summary>Good points survive alongside a bad one; one bad citation must not sink the window.</summary>
    [Fact]
    public void A_bad_point_is_dropped_without_taking_the_good_ones_with_it()
    {
        var r = Run(
            "[0] database migration finished two days ahead of schedule\n" +
            "[1] Priyanka signed off on the vendor contract for next year\n" +
            "[3] we are at seventy percent of the quarterly allocation");

        Assert.NotNull(r);
        Assert.DoesNotContain("Priyanka", r!.Value.Text);
        Assert.Contains("migration finished", r.Value.Text);
        Assert.Contains("seventy percent", r.Value.Text);
    }

    /// <summary>Citations are stripped before the writer sees them: they are provenance, not prose.</summary>
    [Fact]
    public void Citations_are_stripped_from_what_the_writer_reads()
    {
        var r = Run("[1] Craig found one issue with the invoice table");

        Assert.NotNull(r);
        Assert.DoesNotContain("[1]", r!.Value.Text);
    }
}
