using System.Windows;
using System.Windows.Controls;

namespace Toki.Theme;

/// <summary>
/// The layered material of the notch card: plate gradient, top sheen, and inner top/bottom edge
/// lights. Blur-behind is impossible on the layered notch window (DWM backdrops are inert there;
/// SetWindowCompositionAttribute frosts the whole window rectangle including the mascot gutter),
/// so the material is faked with layers. Deliberately PLAIN: a tiled grain read as texture and a
/// broad low-alpha gradient banded, so the card carries no fill of its own beyond the plate.
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
