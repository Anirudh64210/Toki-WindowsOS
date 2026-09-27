using Toki.Theme;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// A HIGHLIGHT MAY OWN THE TOP OF A SURFACE. PAST THAT IT BECOMES THE SURFACE.
///
/// The card's top sheen was a fixed 48px band with no upper bound relative to the card. That reads
/// correctly on a 400px panel - an edge catching light - and wrongly on the 52px recording bar,
/// where 48px covers 92% of the card and the whole bar becomes one glossy top-to-bottom gradient.
///
/// Reported by the owner 2026-09-23, in the same breath as the photographed backdrop: "the fake
/// glass got faker than ever ... you literally created a fake shine". The backdrop was the bigger
/// half and is deleted; this is the other half, and it is the one that survives on a small pill.
///
/// MUTATION PROBE: restoring the unbounded `const double SheenHeight = 48` fails
/// A_short_pill_is_not_mostly_shine (52 * 0.34 = 17.7, not 48).
/// </summary>
public sealed class CardMaterialTests
{
    // The real card heights this has to be right for (PillWindow.Sizes).
    const double RecordingBar = 52;    // the open recording pill - where the bug was visible
    const double Loader = 50;
    const double NotesPanel = 320;

    [Fact]
    public void A_short_pill_is_not_mostly_shine()
    {
        var sheen = CardMaterial.SheenFor(RecordingBar);
        Assert.True(sheen < RecordingBar * 0.4,
            $"a {RecordingBar}px bar carries {sheen}px of sheen - that is not an edge catching light, it is a gradient");
    }

    [Fact]
    public void The_loader_sliver_is_not_mostly_shine()
    {
        var sheen = CardMaterial.SheenFor(Loader);
        Assert.True(sheen < Loader * 0.4, $"a {Loader}px loader carries {sheen}px of sheen");
    }

    [Fact]
    public void A_tall_panel_keeps_the_designed_band()
    {
        // The cap must not quietly redesign the panels it was never about. 320 * 0.34 is 108, so the
        // fixed 48 still wins and nothing changes for a note or the Library-sized cards.
        Assert.Equal(48, CardMaterial.SheenFor(NotesPanel));
    }

    [Fact]
    public void The_sheen_never_exceeds_the_card()
    {
        // A morph passes through every height including tiny ones; a sheen taller than its card
        // would be clipped, but it must also never be NEGATIVE or the Border throws.
        foreach (var h in new[] { 1.0, 8.0, 13.0, 14.0, 42.0, 52.0, 120.0, 430.0 })
        {
            var sheen = CardMaterial.SheenFor(h);
            Assert.True(sheen > 0, $"height {h} produced a non-positive sheen");
            Assert.True(sheen <= h, $"height {h} produced a {sheen}px sheen - taller than the card");
        }
    }

    [Fact]
    public void An_unarranged_card_still_gets_a_sheen()
    {
        // Height is NaN/0 until the first arrange; falling to 0 there would flash a sheenless card.
        Assert.Equal(48, CardMaterial.SheenFor(0));
    }
}
