using Toki.Storage;
using Toki.Transcription;

namespace Toki.Tests;

public class NoteExtractorTests
{
    static TranscriptSegment Them(int ms, string t) => new(Speaker.Them, ms, ms + 2000, t);
    static TranscriptSegment You(int ms, string t) => new(Speaker.You, ms, ms + 2000, t);

    static readonly TranscriptSegment[] Planted =
    [
        Them(0, "So what analytics platform do we point events at?"),
        You(2000, "We agreed to ship on Thursday and keep email capture optional."),
        Them(4000, "Priya will confirm the brand font tomorrow."),
        You(6000, "I'll wire up the webhook this week."),
        Them(8000, "Should we gate the diagnostic or leave it open?"),
    ];

    [Fact]
    public void ExtractsPlantedDecision()
    {
        var n = NoteExtractor.Extract(Planted);
        Assert.Contains(n.Decisions, d => d.Contains("ship on Thursday", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractsPlantedActionsWithOwners()
    {
        var n = NoteExtractor.Extract(Planted);
        Assert.Contains(n.Actions, a => a.Text.Contains("brand font") && a.Owner == "Priya" && a.Due == "Tomorrow");
        Assert.Contains(n.Actions, a => a.Text.Contains("webhook") && a.Owner == "You" && a.Due == "This week");
    }

    [Fact]
    public void ExtractsPlantedQuestions()
    {
        var n = NoteExtractor.Extract(Planted);
        Assert.Equal(2, n.Questions.Count);
        Assert.Contains(n.Questions, q => q.Contains("analytics platform"));
    }

    [Fact]
    public void TheFloorIsCappedAndDropsGrunts()
    {
        // MUTATION PROBE (meeting 101, 2026-08-19): with no caps, a 17-minute meeting shipped
        // 92 raw "open questions" and 35 half-sentence to-dos as its note. The floor keeps the
        // most substantial few and drops sub-4-word connection checks ("What?", "Is it good?").
        var segs = new List<TranscriptSegment> { Them(0, "Is it good?"), Them(100, "What?") };
        for (var i = 0; i < 40; i++)
        {
            segs.Add(Them(1000 + i * 100, $"Should we route the number {i} pipeline through the new tracker?"));
            segs.Add(You(5000 + i * 100, $"I'll update the number {i} column in the ops tracker sheet."));
        }
        var n = NoteExtractor.Extract(segs);
        Assert.True(n.Questions.Count <= 10, $"{n.Questions.Count} questions survived the cap");
        Assert.True(n.Actions.Count <= 12, $"{n.Actions.Count} actions survived the cap");
        Assert.DoesNotContain(n.Questions, q => q == "Is it good?" || q == "What?");
    }

    [Fact]
    public void FloorSummaryIsNeverRawTranscript()
    {
        // THE LEAK (meetings 118/119, 2026-08-25): when the model can't load, the floor ran, and
        // its "summary" was the transcript's opening sentences pasted in verbatim - conversation
        // dressed up as a written summary. The floor now emits NO summary; the renderer drops the
        // empty section, exactly as the model path does when it has nothing real to say.
        // MUTATION PROBE: restore `notes.Tldr = Tldr(sentences);` and this fails - the summary
        // becomes the raw opening ("...analytics platform...") again.
        var n = NoteExtractor.Extract(Planted);
        Assert.True(string.IsNullOrEmpty(n.Tldr), $"floor emitted a summary: \"{n.Tldr}\"");
        var spoken = string.Join(" ", Planted.Select(s => s.Text));
        foreach (var word in new[] { "analytics platform", "point events", "brand font" })
            Assert.DoesNotContain(word, n.Tldr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("analytics platform", spoken, StringComparison.OrdinalIgnoreCase);   // it WAS said
    }

    [Fact]
    public void RamblingDeclarativeIsNotATodo()
    {
        // THE OTHER HALF OF THE LEAK: the floor used to accept any sentence that CONTAINED an
        // action word, so declaratives ("The latest results confirm ...", "I optimized ...") became
        // to-dos, then the brevity pass truncated them into fragments. A to-do must be SHAPED like a
        // task now.
        var segs = new[]
        {
            You(0, "I optimized the rendering so typing feels smoother and reduced the API calls."),
            Them(2000, "The latest results confirm our earlier throughput numbers."),
            You(4000, "I'll send the deck to Priya tomorrow."),
        };
        var n = NoteExtractor.Extract(segs);
        // MUTATION PROBE: revert the classifier to `ActionCues.Any(lower.Contains)` and both of
        // these fail - "confirm" and the truncated "optimized ..." fragment become to-dos again.
        Assert.DoesNotContain(n.Actions, a => a.Text.Contains("throughput", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(n.Actions, a => a.Text.Contains("optimized", StringComparison.OrdinalIgnoreCase));
        // The genuine commitment still lands.
        Assert.Contains(n.Actions, a => a.Text.Contains("deck", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TitleFromNotesIsSensible()
    {
        var n = NoteExtractor.Extract(Planted);
        var title = NoteExtractor.Title(n, "chrome.exe", new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero));
        Assert.True(title.Length >= 3);
        Assert.DoesNotContain("!!!", title);
    }

    [Fact]
    public void TitleFallsBackToAppAndDateWhenNoNotes()
    {
        var empty = NoteExtractor.Extract([]);
        var title = NoteExtractor.Title(empty, "zoom.exe", new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero));
        Assert.Contains("Zoom", title);
        Assert.Contains("Jul", title);
    }

    [Fact]
    public void EmptyTranscriptYieldsEmptyNotes()
    {
        var n = NoteExtractor.Extract([]);
        Assert.Empty(n.Decisions);
        Assert.Empty(n.Actions);
        Assert.Empty(n.Questions);
    }
}
