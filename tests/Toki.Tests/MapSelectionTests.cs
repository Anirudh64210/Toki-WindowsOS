using System.Collections.Generic;
using Toki.Llm;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// THE MAP STEP PICKS LINES; IT DOES NOT REWRITE THEM.
///
/// Generation is the entire summarize wait (measured 2026-09-04: a 12-token pass over a 3.4k-char
/// prompt costs 2-3s, a 430-token pass costs 20-58s), and the map phase was 85% of a long meeting.
/// Having the model emit only the line numbers worth keeping, then copying those lines out of the
/// transcript verbatim, cuts a window from ~430 generated tokens to ~70.
///
/// Meeting 107 (22 min, 5 windows) went 60.6s to 25.1s of model time; meeting 128 28.2s to 12.4s.
/// Grounding was unchanged over 5 meetings and action owners came out equal or better, because raw
/// speech carries "Kashish, can you get Koshi?" where a rewrite drops the name.
///
/// Materialise rebuilds the EXACT format the extract path produced, which is why ValidateDigest,
/// TrimToFit and provenance all keep working untouched.
///
/// MUTATION PROBE: dropping the range check makes Out_of_window_citations_are_ignored fail with an
/// argument exception; dropping the dedupe makes Repeats_collapse fail; emitting bare text without
/// the [n] prefix makes The_format_matches_what_validation_expects fail.
/// </summary>
public sealed class MapSelectionTests
{
    static List<TranscriptSegment> Script() =>
    [
        new(Speaker.Them, 0, 1000, "morning everyone"),
        new(Speaker.Them, 1000, 4000, "Kashish can you get Koshi on the hiring numbers"),
        new(Speaker.You, 4000, 5000, "yes by Monday"),
        new(Speaker.Them, 5000, 8000, "we land on one org catalog"),
    ];

    [Fact]
    public void Picked_lines_come_back_verbatim_with_their_citation()
    {
        var got = LlmNoteWriter.Materialise("[1]\n[3]", Script(), 0, 3);
        Assert.NotNull(got);
        // The NAME must survive: it is what lets the note attribute a to-do to a person.
        Assert.Contains("Kashish can you get Koshi on the hiring numbers", got);
        Assert.Contains("we land on one org catalog", got);
        // and the line the model did NOT pick must not appear
        Assert.DoesNotContain("morning everyone", got);
    }

    [Fact]
    public void The_format_matches_what_validation_expects()
    {
        var got = LlmNoteWriter.Materialise("[1]", Script(), 0, 3)!;
        // Same shape the extract path emits, so ValidateDigest/TrimToFit/provenance need no branch.
        Assert.StartsWith("[1] ", got);
        var checkedDigest = LlmNoteWriter.ValidateDigest(got, Script(), 0, 3);
        Assert.NotNull(checkedDigest);
        // A copied line cannot fail its own citation check - that is the grounding win.
        Assert.Equal(1, checkedDigest!.Value.First);
        Assert.Equal(1, checkedDigest.Value.Last);
    }

    [Fact]
    public void Out_of_window_citations_are_ignored()
    {
        // The model can cite a line from a neighbouring window, or hallucinate an index past the end.
        // Both must be dropped rather than indexed, which would throw inside the note pipeline.
        var got = LlmNoteWriter.Materialise("[1] [99] [-2]", Script(), 0, 3);
        Assert.NotNull(got);
        Assert.Single(got!.Split('\n'));
        Assert.StartsWith("[1] ", got);
    }

    [Fact]
    public void A_window_outside_its_own_range_yields_nothing()
    {
        // Window 2 must not be allowed to quote window 1's lines: the provenance range would lie.
        Assert.Null(LlmNoteWriter.Materialise("[0] [1]", Script(), 2, 3));
    }

    [Fact]
    public void Repeats_collapse_and_order_is_restored()
    {
        var got = LlmNoteWriter.Materialise("[3]\n[1]\n[3]", Script(), 0, 3)!;
        var lines = got.Split('\n');
        Assert.Equal(2, lines.Length);
        // Reading order, not the order the model happened to emit.
        Assert.StartsWith("[1] ", lines[0]);
        Assert.StartsWith("[3] ", lines[1]);
    }

    [Fact]
    public void A_reply_with_no_usable_citation_is_a_failed_window()
    {
        // Not an empty digest: "the model ignored the format" and "the window held nothing" are
        // opposite faults and the caller counts them differently.
        Assert.Null(LlmNoteWriter.Materialise("nothing much happened here", Script(), 0, 3));
    }

    [Fact]
    public void The_nothing_marker_passes_straight_through()
    {
        // A genuinely empty window must stay recognisable as empty, not become a failed decode.
        var got = LlmNoteWriter.Materialise("NOTHING", Script(), 0, 3);
        Assert.Equal("NOTHING", got);
    }

    [Fact]
    public void Select_is_the_shipped_default()
    {
        // The flag exists for A/B only. If this flips, every long meeting roughly doubles again.
        Assert.Equal(LlmNoteWriter.MapMode.Select, LlmNoteWriter.Mode);
    }
}
