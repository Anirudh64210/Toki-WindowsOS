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
