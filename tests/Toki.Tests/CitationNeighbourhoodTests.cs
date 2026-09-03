using System;
using System.Collections.Generic;
using System.Linq;
using Toki.Llm;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// A LINE IS NOT A UNIT OF MEANING; A FEW SECONDS OF SPEECH IS.
///
/// ValidateDigest accepted the cited line plus one either side, measuring the neighbourhood in
/// LINES. How much speech a line holds varies by a factor of three between meetings, so that check
/// was three times stricter on a fragmented call than on a clean one.
///
/// MEASURED 2026-09-03, two stored meetings, same pipeline, same day:
///
///   meeting 48   468 segments / 26 min = 3.3s a line   -> +/-1 covers ~10s -> kept 25/26, 29/31, 22/23
///   meeting 28  1140 segments / 20 min = 1.05s a line  -> +/-1 covers ~3s  -> kept 4/10, 15/30, 3/9
///
/// Nearly every loss was `unsupported`, not `uncited` or `out-of-range`: the model was citing a real
/// line and the words were simply spread across more lines than the check looked at. The gate was
/// deleting true content rather than catching invention.
///
/// MUTATION PROBE: returning a constant 1 fails Fragmented_speech_earns_a_wider_neighbourhood.
/// </summary>
public sealed class CitationNeighbourhoodTests
{
    /// <summary>A transcript of `count` segments evenly spread over `minutes`.</summary>
    static List<TranscriptSegment> Paced(int count, double minutes)
    {
        var totalMs = (int)(minutes * 60_000);
        var each = Math.Max(1, totalMs / count);
        return Enumerable.Range(0, count)
            .Select(i => new TranscriptSegment(Speaker.Them, i * each, (i + 1) * each, $"line {i} words here"))
            .ToList();
    }

    [Fact]
    public void Fragmented_speech_earns_a_wider_neighbourhood()
    {
        var clean = LlmNoteWriter.NeighbourLines(Paced(468, 26));    // meeting 48: 3.3s a line
        var choppy = LlmNoteWriter.NeighbourLines(Paced(1140, 20));  // meeting 28: 1.05s a line

        Assert.True(choppy > clean,
            $"a call at ~1s a line needs more lines to cover a sentence than one at ~3.3s, got {choppy} vs {clean}");
    }

    [Fact]
    public void The_neighbourhood_covers_a_comparable_amount_of_speech()
    {
        // The point of the change: both meetings should see roughly the same SECONDS of context,
        // which is what a spoken sentence actually occupies.
        foreach (var (count, minutes) in new[] { (468, 26.0), (1140, 20.0), (200, 15.0) })
        {
            var lines = LlmNoteWriter.NeighbourLines(Paced(count, minutes));
            var secondsPerLine = minutes * 60 / count;
            var covered = lines * secondsPerLine;
            Assert.InRange(covered, 2.0, 9.0);
        }
    }

    [Fact]
    public void The_neighbourhood_is_capped_so_the_gate_stays_a_gate()
    {
        // Widening a safety check has a cost: a wide enough haystack lets an invented point find its
        // words somewhere nearby. Even absurdly fragmented input stops at five lines either side.
        Assert.True(LlmNoteWriter.NeighbourLines(Paced(20000, 5)) <= 5);
    }

    [Fact]
    public void A_transcript_without_timing_keeps_the_old_behaviour()
    {
        var untimed = Enumerable.Range(0, 50)
            .Select(i => new TranscriptSegment(Speaker.You, 0, 0, $"line {i}"))
            .ToList();

        Assert.Equal(1, LlmNoteWriter.NeighbourLines(untimed));
    }

    [Fact]
    public void A_one_line_transcript_is_safe()
    {
        Assert.Equal(1, LlmNoteWriter.NeighbourLines(Paced(1, 1)));
        Assert.Equal(1, LlmNoteWriter.NeighbourLines([]));
    }
}
