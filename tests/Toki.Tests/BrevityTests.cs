using System.Collections.Generic;
using Toki.Llm;
using Toki.Storage;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// The note has to be GLANCEABLE: the owner's report was "the to-do list and other deliverables are
/// too long, too many filler words - other notetakers' points are not that long". These pin the
/// shape of a bullet, not the wording of one.
/// </summary>
public class BrevityTests
{
    [Fact]
    public void ReportedSpeechFramesAreDropped()
    {
        Assert.Equal("The pilot runs for four weeks.",
            Brevity.Line("Craig mentioned that the pilot runs for four weeks.", 22));
        Assert.Equal("Pricing is decided before launch.",
            Brevity.Line("It was agreed that pricing is decided before launch.", 22));
        Assert.Equal("The rollout order.",
            Brevity.Line("There was a discussion about the rollout order.", 22));
    }

    [Fact]
    public void HedgesAndFillerGo()
    {
        Assert.Equal("We ship on Friday.",
            Brevity.Line("I think we basically just ship on Friday.", 22));
        Assert.Equal("To cut the wait, we will cache the model.",
            Brevity.Line("In order to cut the wait, we are going to cache the model.", 22));
    }

    [Fact]
    public void TheSummaryIsTightenedTooNotJustTheBullets()
    {
        // MUTATION PROBE: before 2026-08-25, Tighten() skipped note.Tldr entirely, so the summary
        // stayed a 3-4 sentence paragraph. Now it strips hedges and keeps a 2-sentence lead.
        var note = new NoteSet
        {
            Tldr = "The team basically discussed the new KPI. It is really a simple percentage. "
                 + "It aligns with investors. A third sentence that should be dropped."
        };
        Brevity.Tighten(note);
        Assert.Equal("The team discussed the new KPI. It is a simple percentage.", note.Tldr);
    }

    [Fact]
    public void AnEmptySummaryStaysEmptyForTheRendererToDrop() =>
        Assert.Equal("", Brevity.TightenSummary(""));

    [Fact]
    public void AnActionLosesTheOwnerItAlreadyPrints()
    {
        // The row draws the owner in its own capsule; repeating it in the text is a wasted third
        // of the line.
        Assert.Equal("Send the deck to Ana.", Brevity.TightenAction("Priya will send the deck to Ana.", "Priya"));
        Assert.Equal("Send the deck.", Brevity.TightenAction("You need to send the deck.", "You"));
    }

    [Fact]
    public void ADifferentNameIsContentNotAFrame()
    {
        // "Craig will review it" on an item OWNED BY PRIYA is a fact about Craig. Stripping it
        // would attribute the work to the wrong person, which is worse than a long line.
        Assert.Equal("Craig will review the contract.",
            Brevity.TightenAction("Craig will review the contract.", "Priya"));
    }

    [Fact]
    public void ALineWithNoCleanCutIsLeftWhole()
    {
        // A readable long bullet beats a bullet clipped mid-thought.
        const string s = "The vendor renegotiation depends entirely on whether the finance team can free the budget before the quarter closes";
        Assert.Equal(s, Brevity.Line(s, 8));
    }

    [Fact]
    public void SentencesAreKeptWholeWhenTrimming()
    {
        var got = Brevity.Line("We shipped the beta. The rollout starts Monday. Marketing needs the copy by Thursday.", 10);
        Assert.Equal("We shipped the beta. The rollout starts Monday.", got);
    }

    // MUTATION PROBE: fails against a NoteSet that never went through Brevity.Tighten.
    [Fact]
    public void TighteningRunsOverAWholeNote()
    {
        var note = new NoteSet
        {
            Sections = { new NoteSection { Heading = "Key points", Bullets = { "Sarah noted that the API is rate limited." } } },
            Actions = { new ActionItem { Text = "Priya will basically just send the revised deck.", Owner = "Priya" } },
        };

        Brevity.Tighten(note);

        Assert.Equal("The API is rate limited.", note.Sections[0].Bullets[0]);
        Assert.Equal("Send the revised deck.", note.Actions[0].Text);
    }
}
