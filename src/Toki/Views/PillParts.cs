using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Toki.Views;

/// <summary>
/// Shared building blocks for the compact notch states, defined once so the five states can't
/// drift apart. Icons are drawn as PATHS (from the lucide set), never picked from Segoe Fluent;
/// solid shapes are built as fills and outline shapes as strokes, by two different helpers.
/// </summary>
public static class PillParts
{
    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static FontFamily UiFont => (FontFamily)Application.Current.Resources["UiFont"];

    /// <summary>
    /// Bind a property to a themed brush the way XAML's DynamicResource does. Never capture the brush:
    /// ThemeManager.Apply switches themes by REPLACING the resource, so a captured brush never repaints.
    /// </summary>
    static T Themed<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    /// <summary>A 40px-tall glass action pill: icon + label. Width is left to the caller.</summary>
    public static Button ActionPill(Geometry icon, string label, double width = double.NaN)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(Solid(icon, 16, "TextHi", new Thickness(0, 0, 8, 0)));
        content.Children.Add(new TextBlock
        {
            Text = label, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, FontFamily = UiFont,
        }.Themed(TextBlock.ForegroundProperty, "TextHi"));
        return new Button
        {
            Style = (Style)Application.Current.Resources["GlassPill"],
            Content = content, Height = 40, Width = width,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
    }

    /// <summary>A 24px round control on the recording bar. Takes a resource KEY, not a brush,
    /// so the disc repaints on a theme switch (see <see cref="Themed{T}"/>).</summary>
    public static Button RoundButton(FrameworkElement glyph, string backgroundKey, string tooltip, double size = 24)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["RoundPuck"],
            ToolTip = tooltip, Width = size, Height = size, Content = glyph,
        };
        b.SetResourceReference(Control.BackgroundProperty, backgroundKey);
        return b;
    }

    /// <summary>A round GLASS puck, so chrome controls still read as real buttons on the glass card;
    /// a transparent disc leaves the glyph floating on nothing.</summary>
    public static Button GlassPuck(FrameworkElement glyph, string tooltip, double size = 26)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["RoundPuck"],
            ToolTip = tooltip, Width = size, Height = size, Content = glyph,
        };
        // Resource reference, not a captured brush - the glass has to repaint on a theme switch.
        b.SetResourceReference(Control.BackgroundProperty, "GlassFill");
        return b;
    }

    /// <summary>A filled icon in a THEMED colour. Takes a resource key, not a brush,
    /// so it follows a live theme switch.</summary>
    public static Path Solid(Geometry data, double size, string fillKey, Thickness margin = default) =>
        new Path
        {
            Data = data, Width = size, Height = size, Stretch = Stretch.Uniform, Margin = margin,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        }.Themed(Shape.FillProperty, fillKey);

    /// <summary>A filled icon in a FIXED colour, for glyphs on a coloured disc of their own,
    /// where the theme must not repaint them.</summary>
    public static Path Solid(Geometry data, double size, Brush fill, Thickness margin = default) => new()
    {
        Data = data, Fill = fill, Width = size, Height = size, Stretch = Stretch.Uniform, Margin = margin,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// Opt a small glyph out of layout rounding (inherited from the window) so it sits on its
    /// container's true centre: at fractional DPI the rounded margins split unevenly and the
    /// glyph snaps visibly off-centre.
    /// </summary>
    static T SubPixel<T>(this T element) where T : FrameworkElement
    {
        element.UseLayoutRounding = false;
        element.SnapsToDevicePixels = false;
        return element;
    }

    /// <summary>The white square on the stop disc, at true size and centred sub-pixel. Fixed colour:
    /// it sits on a red disc of its own, which no theme repaints.</summary>
    public static Path StopGlyph(Brush fill, double size = 10) => new Path
    {
        Data = StopSquare(size), Fill = fill, Stretch = Stretch.None,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    }.SubPixel();

    /// <summary>The same square in a THEMED colour - the stop half of an inline play control, where the
    /// glyph is the only thing carrying the state and has to follow a theme switch.</summary>
    public static Path StopGlyph(string fillKey, double size = 8) =>
        new Path
        {
            Data = StopSquare(size), Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        }.Themed(Shape.FillProperty, fillKey).SubPixel();

    /// <summary>
    /// The play triangle, filled, at true size. The 1px shift right is optical (a right-pointing
    /// triangle carries its mass on the left) and must be a RENDER transform, not a margin:
    /// a margin inside a centre-aligned ContentPresenter widens the content box and re-centres it.
    /// </summary>
    public static Path PlayGlyph(string fillKey)
    {
        // ROUNDED, NOT A SHARD. At 8x10 a mathematically sharp triangle renders three needle points
        // that alias into grey pixels, which is what made this read as cheap beside body text (owner,
        // 2026-09-26: "the play button looks very ugly"). Stroking the same geometry with a round
        // join and painting stroke and fill from one brush softens the corners without a second
        // path: the outline adds half its thickness back, so the triangle is drawn slightly smaller
        // to keep the optical size it had.
        var p = new Path
        {
            Data = PlayTriangleSmall(), Stretch = Stretch.None,
            StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round,
            RenderTransform = new TranslateTransform(1, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        p.Themed(Shape.FillProperty, fillKey).Themed(Shape.StrokeProperty, fillKey);
        return p.SubPixel();
    }

    /// <summary>
    /// An outline icon, drawn at TRUE SIZE with Stretch.None. Never author on a large viewbox and
    /// let Stretch.Uniform scale down: it scales the STROKE with the geometry (a smudge under 1px)
    /// and stretches the authored inset out to the box edges.
    /// </summary>
    public static Path Outline(Geometry data, string strokeKey, double thickness = 1.6) =>
        new Path
        {
            Data = data, StrokeThickness = thickness, Stretch = Stretch.None,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        }.Themed(Shape.StrokeProperty, strokeKey).SubPixel();

    /// <summary>
    /// The single-letter label for an initial disc, drawn as GEOMETRY rather than as text.
    ///
    /// Two attempts at this with a TextBlock both left the letter visibly low, because centring
    /// text centres its LINE BOX and the relationship between that box and the cap ink depends on
    /// the font, the size, and whether a line height was pinned or inherited. A Path's layout box
    /// IS its ink, so centring the Path centres the letter, at any size, in any font, with nothing
    /// to keep in sync. Same reason the play triangle and the tick are house geometry.
    /// </summary>
    public static Shape DiscLabel(string text, double fontSize, FontWeight weight)
    {
        var family = (FontFamily)Application.Current.Resources["UiFont"];
        var ft = new FormattedText(
            string.IsNullOrEmpty(text) ? "?" : text,
            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal),
            fontSize, Brushes.Black, 96);

        var geometry = ft.BuildGeometry(new Point(0, 0));

        // MOVE THE INK TO THE ORIGIN. BuildGeometry lays the outline out relative to the TEXT
        // origin, so the glyph sits a baseline's distance down from (0,0) with empty space above
        // it. A Path with Stretch.None measures from the origin to the far edge of the geometry,
        // which means that empty space is part of its layout box - centring the box then centres
        // the box, not the letter, and the letter rides low. Translating by the negative bounds
        // makes the layout box exactly the ink, which is the whole point of drawing it as a shape.
        var ink = geometry.Bounds;
        geometry.Transform = new TranslateTransform(-ink.X, -ink.Y);
        // Freeze: this is painted on every transcript line and never changes after construction.
        if (geometry.CanFreeze) geometry.Freeze();

        // SubPixel like every other glyph factory: centring a 6px letter in a 16px disc leaves a
        // fractional offset either side, and inherited layout rounding snaps those apart at 125%.
        return new Path
        {
            Data = geometry,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        }.SubPixel();
    }

    /// <summary>
    /// The tint behind a named speaker's initial. DERIVED from the name, never fetched and never
    /// assigned in arrival order (which would repaint everyone when diarisation renames one voice).
    /// The palette is limited to the design system's own accents.
    /// </summary>
    public static Brush SpeakerTint(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        var h = 0;
        foreach (var c in key) h = unchecked(h * 31 + c);
        return SpeakerTints[(h & 0x7FFFFFFF) % SpeakerTints.Length];
    }

    static readonly Brush[] SpeakerTints =
    {
        Frozen(Color.FromRgb(0xEA, 0x72, 0xB0)),   // speaker pink
        Frozen(Color.FromRgb(0x5A, 0xA9, 0xF9)),   // people blue
        Frozen(Color.FromRgb(0x32, 0xC7, 0xD3)),   // teal
        Frozen(Color.FromRgb(0xC0, 0x8A, 0xF0)),   // purple
    };

    // ── icon set (lucide geometry on a 24px viewbox; Stretch.Uniform does the scaling) ──

    /// <summary>lucide/video, solid - the "Start a meeting" / "Record this meeting" camera.</summary>
    public static Geometry Videocam() => G("M16 10.5 L22 7 V17 L16 13.5 Z "
                                         + "M2 8 A2 2 0 0 1 4 6 H14 A2 2 0 0 1 16 8 V16 A2 2 0 0 1 14 18 H4 A2 2 0 0 1 2 16 Z");

    /// <summary>lucide/notepad-text, solid - the "Take notes" pad. The rule lines and the two top
    /// tabs are separate subpaths; EvenOdd punches the lines through the body as holes.</summary>
    public static Geometry Notepad() => G("M5 4 H19 A1 1 0 0 1 20 5 V21 A1 1 0 0 1 19 22 H5 A1 1 0 0 1 4 21 V5 A1 1 0 0 1 5 4 Z "
                                        + "M8 9 H16 V11 H8 Z M8 13 H16 V15 H8 Z M8 17 H12 V19 H8 Z "
                                        + "M7.2 1 H8.8 V4 H7.2 Z M15.2 1 H16.8 V4 H15.2 Z");

    /// <summary>lucide/calendar-days, solid - the "Calendar" action. Two hanger tabs, a header band,
    /// and the day dots punched through by EvenOdd, same construction as Notepad above.</summary>
    public static Geometry CalendarGlyph() => G("M3 5 H21 A1 1 0 0 1 22 6 V21 A1 1 0 0 1 21 22 H3 A1 1 0 0 1 2 21 V6 A1 1 0 0 1 3 5 Z "
                                              + "M2 8.6 H22 V10.2 H2 Z "
                                              + "M6.2 2 H7.8 V6 H6.2 Z M16.2 2 H17.8 V6 H16.2 Z "
                                              + "M6 12.6 H8.4 V15 H6 Z M10.8 12.6 H13.2 V15 H10.8 Z M15.6 12.6 H18 V15 H15.6 Z "
                                              + "M6 17 H8.4 V19.4 H6 Z M10.8 17 H13.2 V19.4 H10.8 Z");

    // ── outline icons, at true size (see Outline) ──

    /// <summary>The stop square, softly cornered. The corner scales with the square so every size
    /// is the same shape.</summary>
    public static Geometry StopSquare(double size = 10) =>
        Freeze(new RectangleGeometry(new Rect(0, 0, size, size), size * 0.26, size * 0.26));

    /// <summary>lucide/play at 8x10 - the evidence player's triangle, authored at its final pixel
    /// size so nothing scales the shape (see Outline).</summary>
    public static Geometry PlayTriangle() => G("M0,0 L8,5 L0,10 Z");

    /// <summary>The play triangle inset by the round join's half-thickness, so stroke plus fill
    /// occupies the same box the sharp one did.</summary>
    public static Geometry PlayTriangleSmall() => G("M0.8,1.4 L6.9,5 L0.8,8.6 Z");

    /// <summary>
    /// lucide/chevron-down at 11x5.5 - the recording bar's disclosure. Starts at 0,0 with no inset
    /// because it gets ROTATED: with Stretch.None a Path measures from the origin, so an inset puts
    /// the element's centre off the glyph's centre and the glyph jumps as it turns.
    /// </summary>
    public static Geometry ChevronDown() => G("M0,0 L5.5,5.5 L11,0");

    /// <summary>lucide/chevron-right at 6x11 - the "Notes ready" arrow.</summary>
    public static Geometry ChevronRight() => G("M1,1 L6,6 L1,11");

    /// <summary>lucide/x at 9x9 - the close puck's cross.</summary>
    public static Geometry Cross() => G("M1,1 L8,8 M8,1 L1,8");

    /// <summary>lucide/eye at ~15x9 - the recording bar's ghost toggle when Toki IS visible in
    /// recordings (an open eye: you can be seen). Authored at true pixel size for Outline.</summary>
    public static Geometry Eye() => G("M0.75,4.5 C4,0.5 11,0.5 14.25,4.5 C11,8.5 4,8.5 0.75,4.5 Z "
                                    + "M7.5,2.8 A1.7,1.7 0 1 0 7.5,6.2 A1.7,1.7 0 1 0 7.5,2.8 Z");

    /// <summary>lucide/eye-off at ~15x9 - the ghost toggle when Toki is HIDDEN from recordings (the
    /// eye struck through). Same almond + pupil, with the slash.</summary>
    public static Geometry EyeOff() => G("M0.75,4.5 C4,0.5 11,0.5 14.25,4.5 C11,8.5 4,8.5 0.75,4.5 Z "
                                       + "M7.5,2.8 A1.7,1.7 0 1 0 7.5,6.2 A1.7,1.7 0 1 0 7.5,2.8 Z "
                                       + "M0.5,8.8 L14.5,0.2");

    static Geometry G(string data)
    {
        var g = Geometry.Parse(data);
        if (g is PathGeometry pg) pg.FillRule = FillRule.EvenOdd;
        g.Freeze();
        return g;
    }

    static Geometry Freeze(Geometry g) { g.Freeze(); return g; }
}
