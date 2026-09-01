using System.Collections.Generic;
using Toki.Storage;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Provenance: a written line remembers which transcript lines the model was READING when it wrote
/// it, so the evidence link searches that window instead of the whole meeting. Measured with
/// --linktest: 82.9% to 87.5% precision@1 in the hard condition, sixteen cases gained, none lost.
/// </summary>
public class ProvenanceTests
{
    static TranscriptSegment Line(int i, string text) =>
        new(Speaker.Them, i * 1000, i * 1000 + 900, text);

    // A meeting that says the same words twice, which is exactly when the whole-transcript search
    // picks the wrong one.
    static List<TranscriptSegment> Repetitive() =>
    [
        Line(0, "we should send the pricing deck to the vendor this week"),
        Line(1, "the vendor asked about onboarding"),
        Line(2, "unrelated chatter about lunch"),
        Line(3, "much later we should send the pricing deck to the board instead"),
        Line(4, "and the board meets on friday"),
    ];

    [Fact]
    public void WithoutProvenanceTheSearchCanLandOnEitherCopy()
    {
        var t = Repetitive();
        var hit = Evidence.BestRange("send the pricing deck", t);
        Assert.NotNull(hit);   // it finds A copy; which one is not guaranteed
    }

    // MUTATION PROBE: passes only while BestRange actually honours the recorded window.
    [Fact]
    public void ProvenanceConfinesTheSearchToTheLinesTheModelRead()
    {
        var t = Repetitive();

        var early = Evidence.BestRange("send the pricing deck", t, new SourceSpan { First = 0, Last = 1 });
        Assert.NotNull(early);
        Assert.True(early!.Value.First <= 1, $"expected the early copy, got line {early.Value.First}");

        var late = Evidence.BestRange("send the pricing deck", t, new SourceSpan { First = 3, Last = 4 });
        Assert.NotNull(late);
        Assert.True(late!.Value.First >= 2, $"expected the later copy, got line {late.Value.First}");
    }

    [Fact]
    public void ARecordedWindowGetsOneLineOfSlackBecauseChunksCutMidThought()
    {
        var t = Repetitive();
        // The words live on line 3; the recorded window stops at 2. One line of slack still finds it.
        var hit = Evidence.BestRange("pricing deck to the board", t, new SourceSpan { First = 1, Last = 2 });
        Assert.NotNull(hit);
        Assert.Equal(3, hit!.Value.First);
    }

    [Fact]
    public void ANonsenseWindowFallsBackRatherThanRefusing()
    {
        var t = Repetitive();
        var hit = Evidence.BestRange("send the pricing deck", t, new SourceSpan { First = 99, Last = 4 });
        Assert.NotNull(hit);
    }
}
