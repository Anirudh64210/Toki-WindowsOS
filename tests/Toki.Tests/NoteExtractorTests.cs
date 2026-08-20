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
    public void TldrIsNonEmptyAndBounded()
    {
        var n = NoteExtractor.Extract(Planted);
        Assert.False(string.IsNullOrWhiteSpace(n.Tldr));
        Assert.True(n.Tldr.Length <= 245);
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
