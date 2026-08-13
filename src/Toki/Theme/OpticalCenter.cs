using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Toki.Theme;

/// <summary>
/// Puts a label in the OPTICAL middle of its button, computed from the font's real metrics.
///
/// **Why a label centred by layout still looks low.** WPF centres a text's *line box*. A line box is
/// taller than the letters: it reserves room for descenders (g, y, p) and for internal leading, and that
/// reserved space is not symmetric. Centre the box and the letters land below the middle, because the
/// empty descender space below them is counted as if it were ink. The eye reads the middle of the
/// CAP-HEIGHT band as the middle of a word, so that is what has to be centred.
///
/// **What this replaces.** The previous fix was `LineHeight = FontSize` plus a hard-coded
/// `TranslateTransform Y="-1"`. Two problems: clamping the line box fights the font instead of measuring
/// it, and a fixed 1px is only ever right at one font size - a correction that should scale with the em
/// was frozen at whatever looked right on one button. Measured across the real notch, every pill label
/// still sat **1.7px low**.
///
/// **The arithmetic**, all in fractions of an em, all read from the actual typeface:
///
///     line box height   = FontFamily.LineSpacing
///     baseline from top = FontFamily.Baseline
///     cap height        = GlyphTypeface.CapsHeight
///
///     optical middle of the letters = Baseline - CapsHeight/2   (from the top of the line box)
///     middle of the line box        = LineSpacing / 2
///     error (positive = too low)    = (Baseline - CapsHeight/2) - LineSpacing/2
///
/// The correction is `-error * FontSize`, so it scales with type size and adapts to any font - including
/// whatever the coming design revamp switches to. Verified in pixels by <c>--hittest</c>, which measures
/// the same thing independently and fails above 1.5px.
/// </summary>
public static class OpticalCenter
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(OpticalCenter),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if ((bool)e.NewValue)
        {
            el.Loaded += Apply;
            el.SizeChanged += Apply;
            if (el.IsLoaded) Nudge(el);
        }
        else
        {
            el.Loaded -= Apply;
            el.SizeChanged -= Apply;
            el.RenderTransform = Transform.Identity;
        }
    }

    static void Apply(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el) Nudge(el);
    }

    static void Nudge(FrameworkElement el)
    {
        var offset = OffsetFor(TextElement.GetFontFamily(el), TextElement.GetFontSize(el),
            TextElement.GetFontStyle(el), TextElement.GetFontWeight(el), TextElement.GetFontStretch(el));

        // SNAP TO WHOLE DEVICE PIXELS. This is the difference between "centred" and "centred and crisp".
        //
        // The raw correction is about 0.77px at 12pt - a SUB-PIXEL vertical offset. A render transform is
        // applied after layout rounding and is not itself rounded, so it drops every glyph off the device
        // pixel grid: the text goes soft, and soft text reads as subtly wrong without looking obviously
        // wrong. The whole point of the exercise is a label that looks right, so the nudge is rounded to
        // a real pixel on the actual display - which is also why it has to be computed here, per element,
        // rather than baked into a style: a 4K panel at 150% and an external monitor at 100% need
        // different numbers, and Toki is per-monitor DPI aware.
        var scale = 1.0;
        try { var dpi = VisualTreeHelper.GetDpi(el); if (dpi.DpiScaleY > 0) scale = dpi.DpiScaleY; } catch { }

        var devicePx = Math.Round(offset * scale, MidpointRounding.AwayFromZero);
        if (devicePx == 0) { el.RenderTransform = Transform.Identity; return; }
        el.RenderTransform = new TranslateTransform(0, devicePx / scale);
    }

    /// <summary>
    /// The vertical nudge, in device-independent pixels, that moves a label from line-box-centred to
    /// cap-height-centred. Negative moves it UP. Public so the type ramp and the tests can both ask.
    /// </summary>
    public static double OffsetFor(FontFamily family, double size,
        FontStyle style, FontWeight weight, FontStretch stretch)
    {
        if (family is null || size <= 0) return 0;
        try
        {
            var face = new Typeface(family, style, weight, stretch);
            if (!face.TryGetGlyphTypeface(out var glyphs)) return 0;

            var lineSpacing = family.LineSpacing;     // em
            var baseline = family.Baseline;           // em, from top of the line box
            var caps = glyphs.CapsHeight;             // em, above the baseline
            if (lineSpacing <= 0 || caps <= 0) return 0;

            var error = (baseline - caps / 2) - lineSpacing / 2;
            return -error * size;
        }
        catch { return 0; }   // an exotic font is not worth crashing a button over
    }
}
