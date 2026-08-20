using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Toki.Theme;

/// <summary>
/// The bits of the card recipe that a CODE-BUILT surface needs and cannot get from a XAML template.
/// </summary>
public static class GlassSurface
{
    /// <summary>
    /// One logical pixel, rounded to a whole number of DEVICE pixels - a fractional-pixel stroke
    /// renders as two half-lit rows (the "soft border"). Never fix this with
    /// RenderOptions.EdgeMode=Aliased: that would harden the corner arcs as well.
    /// </summary>
    public static double Hairline(Visual v)
    {
        var s = 1.0;
        try { s = VisualTreeHelper.GetDpi(v).DpiScaleX; } catch { }
        if (s <= 0) s = 1.0;
        return Math.Max(1, Math.Round(s)) / s;
    }

    /// <summary>
    /// Hold one ROUNDED element's rim on whole device pixels (inner cards, fields, chips). Runs on
    /// Loaded because DPI is unknowable before an HwndSource exists, and re-applies on DpiChanged.
    /// Straight separators must NOT get this - only strokes following a curve go soft.
    /// </summary>
    public static void CrispRim(Border rim)
    {
        Window? host = null;
        void Apply() => rim.BorderThickness = new Thickness(Hairline(rim));
        void OnDpi(object? _, DpiChangedEventArgs __) => Apply();

        rim.Loaded += (_, _) =>
        {
            Apply();
            host = Window.GetWindow(rim);
            if (host is not null) { host.DpiChanged -= OnDpi; host.DpiChanged += OnDpi; }
        };
        // GetWindow is unreliable once detached, so unsubscribe from the window we actually joined.
        rim.Unloaded += (_, _) => { if (host is not null) { host.DpiChanged -= OnDpi; host = null; } };
        if (rim.IsLoaded) Apply();
    }
}

/// <summary>
/// A raised card in the notch's material for code-built surfaces (dropdowns, menus, dialogs).
/// The lift is three stacked shapes, never a DropShadowEffect - an Effect would cost ClearType on
/// every label under it. The card is inset by <see cref="ShadowGutter"/> so the lift survives a
/// Popup's crop; callers placing this in a Popup must subtract the gutter from placement offsets.
/// </summary>
public sealed class GlassCard : Grid
{
    /// <summary>Room around the card for the lift to be drawn in. See the note above about popup
    /// placement: a Popup hosting one of these subtracts this from its offsets.</summary>
    public const double ShadowGutter = 6;

    readonly Border _card;
    readonly Grid _inner = new();
    UIElement? _content;

    public GlassCard(double radius, bool material = false)
    {
        foreach (var (spread, drop, key) in new[] { (3.0, 4.0, "Shadow1"), (2.0, 3.0, "Shadow2"), (1.0, 2.0, "Shadow3") })
        {
            var ring = new Rectangle
            {
                RadiusX = radius, RadiusY = radius, IsHitTestVisible = false,
                Margin = new Thickness(ShadowGutter - spread, ShadowGutter + 2,
                                       ShadowGutter - spread, ShadowGutter - drop),
            };
            ring.SetResourceReference(Shape.FillProperty, key);
            Children.Add(ring);
        }

        _card = new Border
        {
            CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1),
            Margin = new Thickness(ShadowGutter), Child = _inner,
        };
        _card.SetResourceReference(Border.BackgroundProperty, "CardPlateOpaque");
        _card.SetResourceReference(Border.BorderBrushProperty, "CardRim");
        // Concentric with the card: its radius minus the rim it sits inside.
        if (material) _inner.Children.Add(new CardMaterial { Radius = Math.Max(0, radius - 1) });
        Children.Add(_card);

        Loaded += (_, _) => ApplyRim();
    }

    /// <summary>What goes on the card. Added ABOVE the material, which is why this is not simply the
    /// Border's own Child.</summary>
    public UIElement? Content
    {
        get => _content;
        set
        {
            if (_content is not null) _inner.Children.Remove(_content);
            _content = value;
            if (value is not null) _inner.Children.Add(value);
        }
    }

    public Thickness CardPadding { get => _card.Padding; set => _card.Padding = value; }

    public double CardMinWidth { get => _card.MinWidth; set => _card.MinWidth = value; }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyRim();
    }

    void ApplyRim() => _card.BorderThickness = new Thickness(GlassSurface.Hairline(this));
}
