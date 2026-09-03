using System.Windows;
using System.Windows.Controls;

namespace Toki.Theme;

/// <summary>
/// The layered material of the notch card: plate gradient, top sheen, and inner top/bottom edge
/// lights.
///
/// A DWM system backdrop cannot paint here, and the reason is worth keeping straight because it was
/// recorded wrongly once: it is NOT ghost mode. Capture exclusion has no documented or observed
/// interaction with backdrops. The real blockers are that the notch window is
/// AllowsTransparency=True (a per-pixel layered window, which bypasses the frame surface DWM paints
/// into) and that it is never activated, and Microsoft documents acrylic and mica falling back to a
/// solid colour on deactivation. See Theme/WindowBackdrop.cs, which gives the Library window - an
/// ordinary activatable window with neither blocker - the real thing.
///
/// NO GRAIN, and it has now been removed twice. Round-2 research argued for it (noise is a named
/// layer in Microsoft's published acrylic recipe: background, blur, exclusion blend, tint, noise)
/// and it shipped on 2026-09-03 at 128px / 3% with absolute viewport units. The owner rejected it on
/// sight, as a previous session had. Treat it as settled. The eased four-stop luminance falloff in
/// CardPlateOpaque does the anti-banding work grain was brought back for.
///
/// One control, shared by the notch card and the --render harness. No Effects and no animation -
/// safe inside the scale-animated content host.
/// </summary>
public sealed class CardMaterial : Grid
{
    /// <summary>Concentric with the card: outer radius 24 minus the ~1px rim it sits inside.</summary>
    public const double InnerRadius = 23;

    /// <summary>How tall the top glow is. A FIXED band, not a fraction of the card: spread the same
    /// amount of light over a 400px panel and it stops reading as an edge catching it.</summary>
    const double SheenHeight = 48;

    readonly Border _sheen;
    readonly Border _top;
    readonly Border _bottom;
    double _radius = InnerRadius;

    /// <summary>
    /// The radius this stack is drawn at - the HOST card's radius minus the rim it sits inside.
    /// The edge lights are rings; a ring at the wrong radius cuts across the corner arcs.
    /// </summary>
    public double Radius
    {
        get => _radius;
        set
        {
            _radius = value;
            _sheen.CornerRadius = new CornerRadius(value, value, 0, 0);
            _top.CornerRadius = _bottom.CornerRadius = new CornerRadius(value);
        }
    }

    public CardMaterial()
    {
        IsHitTestVisible = false;

        var sheen = _sheen = new Border
        {
            Height = SheenHeight,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(InnerRadius, InnerRadius, 0, 0),
        };
        sheen.SetResourceReference(Border.BackgroundProperty, "CardSheen");

        var top = _top = new Border { CornerRadius = new CornerRadius(InnerRadius), BorderThickness = new Thickness(2) };
        top.SetResourceReference(Border.BorderBrushProperty, "CardInnerTop");

        var bottom = _bottom = new Border { CornerRadius = new CornerRadius(InnerRadius), BorderThickness = new Thickness(2) };
        bottom.SetResourceReference(Border.BorderBrushProperty, "CardInnerBottom");

        Children.Add(sheen);
        Children.Add(top);
        Children.Add(bottom);
    }
}
