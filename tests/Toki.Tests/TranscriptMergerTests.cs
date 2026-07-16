using Toki.Transcription;

namespace Toki.Tests;

public class TranscriptMergerTests
{
    static TranscriptSegment You(int ms, string t) => new(Speaker.You, ms, ms + 1500, t);
    static TranscriptSegment Them(int ms, string t) => new(Speaker.Them, ms, ms + 1500, t);

    [Fact]
    public void MergesIntoOneTimelineOrderedByTime()
    {
        var merged = TranscriptMerger.Merge(
            [You(3000, "sounds good to me")],
            [Them(1000, "what do you think")]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(Speaker.Them, merged[0].Speaker);
        Assert.Equal(Speaker.You, merged[1].Speaker);
    }

    [Fact]
    public void DropsYouLineThatEchoesThemWithinWindow()
    {
        // Remote said it (clean on THEM); mic also picked it up (echo on YOU).
        var merged = TranscriptMerger.Merge(
            [You(1200, "brand font and the make webhook url")],
            [Them(1000, "brand font and the make webhook url")]);
        Assert.Single(merged);
        Assert.Equal(Speaker.Them, merged[0].Speaker);
    }

    [Fact]
    public void KeepsYouLineOutsideTheEchoWindow()
    {
        var merged = TranscriptMerger.Merge(
            [You(8000, "brand font and the make webhook url")],
            [Them(1000, "brand font and the make webhook url")]);
        Assert.Equal(2, merged.Count); // 7s apart — not an echo
    }

    [Fact]
    public void ShortInterjectionRequiresExactMatch_NotDeleted()
    {
        // "yeah" on YOU is a real interjection; must NOT be deleted just because
        // "yeah exactly right" is on THEM nearby.
        var merged = TranscriptMerger.Merge(
            [You(1100, "yeah")],
            [Them(1000, "yeah exactly right lets do that")]);
        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, s => s.Speaker == Speaker.You && s.Text == "yeah");
    }

    [Fact]
    public void ShortInterjectionExactMatchIsDropped()
    {
        var merged = TranscriptMerger.Merge(
            [You(1100, "okay!")],
            [Them(1000, "Okay.")]);
        Assert.Single(merged);
        Assert.Equal(Speaker.Them, merged[0].Speaker);
    }

    [Fact]
    public void FuzzyMatchDropsNearDuplicateLongLine()
    {
        var merged = TranscriptMerger.Merge(
            [You(1200, "so what is left before we actually ship this")],
            [Them(1000, "what is left before we ship this")]);
        Assert.Single(merged); // strong word overlap within window -> echo
    }

    [Fact]
    public void NormalizeStripsPunctuationAndCase()
    {
        Assert.Equal("hello there", TranscriptMerger.Normalize("  Hello, There!! "));
    }

    [Fact]
    public void EmptyStreamsProduceEmptyTranscript()
    {
        Assert.Empty(TranscriptMerger.Merge([], []));
    }
}
