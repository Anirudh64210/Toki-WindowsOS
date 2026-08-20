using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Toki.Theme;

/// <summary>
/// Puts a label in the OPTICAL middle of its button: WPF centres the line box, but the eye centres
/// the cap-height band, so layout-centred labels sit low. The nudge is -((Baseline - CapsHeight/2)
/// - LineSpacing/2) * FontSize, from real font metrics. Verified in pixels by --hittest (fails >1.5px).
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

        // Snap to whole DEVICE pixels: a render transform is applied after layout rounding, so a
        // sub-pixel nudge would soften every glyph. Computed per element because Toki is
        // per-monitor DPI aware - different displays need different numbers.
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
