using Toki.Llm;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// THE EXTRACTION BUDGET IS PER MINUTE, NOT PER WINDOW.
///
/// The map prompt asked for a flat "5 to 12 lines" however much transcript the window held. That
/// makes the budget a property of the CHUNKING, which is an implementation detail, rather than of
/// the meeting, which is the thing being summarised.
///
/// Raising ContextSize to 4096 exposed it. Windows grew 49%, meeting 28 went from seven windows to
/// four, each still capped at the same line budget, and its measured recall fell from 62% to 38%
/// while running 100 seconds faster. It was extracting less of the meeting and finishing sooner.
///
/// MUTATION PROBE: a constant budget fails Scales_with_the_window.
/// </summary>
public sealed class DigestBudgetTests
{
    [Fact]
    public void Scales_with_the_window()
    {
        // The two window sizes the shipped context sizes actually produce.
        var small = LlmNoteWriter.DigestLines(LlmNoteWriter.MaxChunkChars(3072));
        var large = LlmNoteWriter.DigestLines(LlmNoteWriter.MaxChunkChars(4096));

        Assert.True(large > small,
            $"a 49% bigger window must earn more digest lines, got {small} then {large}");
    }

    [Fact]
    public void A_small_window_still_reports_something()
    {
        // Below a handful of lines a window stops saying anything useful about its stretch of talk.
        Assert.True(LlmNoteWriter.DigestLines(200) >= 6);
        Assert.True(LlmNoteWriter.DigestLines(0) >= 6);
    }

    [Fact]
    public void A_huge_window_does_not_invite_padding()
    {
        // Past a point the model pads rather than extracts, and padding is what the citation gate
        // then has to throw away - work done twice to end up where we started.
        Assert.True(LlmNoteWriter.DigestLines(100_000) <= 20);
    }

    [Fact]
    public void The_budget_tracks_transcript_volume_roughly_linearly()
    {
        // Double the transcript in a window, roughly double the points asked for, within the clamp.
        Assert.Equal(LlmNoteWriter.DigestLines(3600) * 2, LlmNoteWriter.DigestLines(7200));
    }
}
