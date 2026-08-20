using System.Windows;
using System.Windows.Media;

namespace Toki.Theme;

public enum AppTheme { Dark, Light }

/// <summary>
/// Live light/dark switching: writes the chosen theme's brushes into Application.Resources, and
/// DynamicResource consumers recolour instantly with no rebuild. Default is Dark.
/// </summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Fired after the palette swaps. Views built in CODE (which capture brushes via
    /// FindResource) subscribe to rebuild; XAML DynamicResource consumers don't need it.</summary>
    public static event Action? ThemeChanged;

    // brush key → (dark, light). Dark mirrors the Theme.xaml defaults (re-asserted so a toggle back
    // is exact). EVERY colour that is ever painted as text needs both ends here - a pastel tuned
    // for a near-black card can be invisible on a near-white one.
    static readonly (string Key, string Dark, string Light)[] Palette =
    {
        // Light values are warm, soft Apple off-whites (not stark "x-ray" #FFFFFF); dark values are
        // rich near-blacks. Notch stays pure black in dark (it reads as a hole in the edge).
        ("Surface",          "#0E0E10", "#F4F4F6"),  // main panel background
        ("SurfaceHi",        "#1C1C1E", "#FCFCFE"),  // raised surface / input field (soft white)
        ("Chip",             "#2C2C2E", "#E7E7EC"),  // chips, secondary buttons
        ("Text",             "#F5F5F7", "#1D1D1F"),  // primary text (Apple label near-black)
        ("TextSoft",         "#D8D8DC", "#3A3A3C"),  // body/summary text (a touch softer than primary)
        ("TextDim",          "#ADADB1", "#56565A"),  // secondary/dim text; both ends re-derived for 4.5:1
        ("Ink",              "#161618", "#FBFBFD"),  // Library reading pane (soft, not glaring white)
        // The notch is a tinted mirror: semi-transparent so the desktop bleeds through slightly,
        // but kept fairly opaque so text stays readable and it never washes out to grey.
        ("Notch",            "#D9000000", "#E0FCFCFE"),  // ~85% black / ~88% white - desktop shows faintly through
        ("NotchBorder",      "#40FFFFFF", "#33000000"),  // a soft rim so the glassy edge reads
        ("WhiteBtn",         "#F2F2F2", "#1D1D1F"),  // primary CTA button fill (inverts to near-black)
        ("CtaText",          "#161618", "#FCFCFE"),  // text on the CTA
        // Hover / press feedback: the only affordance a button has before it's clicked. On the
        // pitch-black notch a low-alpha white wash is invisible (12% white on #000 is ~1.15:1),
        // so these stay strong - do not weaken them.
        ("HoverOverlay",     "#38FFFFFF", "#1F000000"),   // dark: 22% white (~RGB 56 on black) · light: 12% black
        ("PressOverlay",     "#57FFFFFF", "#33000000"),   // press is clearly deeper than hover, both ways
        ("Hairline",         "#18FFFFFF", "#16000000"),
        ("SelectionBorder",  "#66FFFFFF", "#33000000"),
        ("CheckBorder",      "#55FFFFFF", "#55000000"),
        ("CheckBorderHover", "#88FFFFFF", "#88000000"),
        ("ScrollThumbBrush",      "#3A3A3D", "#C4C4C8"),
        ("ScrollThumbHoverBrush", "#565659", "#A6A6AB"),
        ("ScrollThumbDragBrush",  "#6A6A6E", "#8E8E93"),
        // Accent tints + the accent text on them. Light values are derived against a 4.5:1
        // contrast target on the darkest surface they can land on (Chip and the pale accent chips).
        ("Amber",            "#E89B3B", "#794F1A"),
        ("AmberDark",        "#3B2F1B", "#F6E8CB"),
        ("Green",            "#3CD68B", "#11623D"),
        ("GreenDark",        "#12351F", "#CDEDDB"),
        ("Blue",             "#68B0F9", "#1C579B"),
        ("BlueDark",         "#12314F", "#D3E6FA"),
        ("Pink",             "#F09CC7", "#982B63"),
        ("Purple",           "#C99CF2", "#6B429C"),
        ("Coral",            "#FF8A7F", "#913B32"),
        ("Danger",           "#FF8989", "#A5231B"),

        // Glass tokens. Dark: warm glass, text as white opacities; light: the same idea in a pale
        // key with black opacities. THE CARD'S 85% OPACITY IS A CONTRAST DECISION: any lower and
        // the wallpaper sets the background, no text colour can hold a floor. At 85% text is
        // >= 8.7:1 and TextMid >= 5.4:1 over ANY desktop, in both themes.
        ("Glass",              "#D9211E1E", "#D9F4F1EF"),
        ("GlassOpaque",        "#2B2828",   "#F2EFED"),
        ("GlassCapsule",       "#66211E1E", "#80EFEBE8"),
        ("GlassCapsuleOpaque", "#252222",   "#E9E5E2"),
        ("GlassBorder",        "#1AFFFFFF", "#1A000000"),
        // Glass-on-glass: a control ON the card is a step of light above it - low-alpha white in
        // dark, low-alpha black in light (more white on a near-white capsule is invisible).
        // Resting / hover / press fills and the 1px rim for anything nested inside the card.
        ("GlassFill",          "#14FFFFFF", "#0F000000"),
        ("GlassFillHover",     "#21FFFFFF", "#1A000000"),
        ("GlassFillPress",     "#2EFFFFFF", "#24000000"),
        ("GlassRim",           "#17FFFFFF", "#14000000"),
        // The three rings of a fake soft shadow under a floating capsule - stacked shapes, NOT a
        // DropShadowEffect: an Effect inside the notch's scale-animated Host renders the subtree
        // in software every frame. Widest and faintest first; light theme runs at ~70% of dark.
        ("Shadow1",            "#0D000000", "#09000000"),
        ("Shadow2",            "#12000000", "#0C000000"),
        ("Shadow3",            "#18000000", "#11000000"),
        // The loader sliver's progress track.
        ("ShimmerTrack",       "#14FFFFFF", "#14000000"),
        ("TextHi",             "#E6FFFFFF", "#E6000000"),
        // Secondary and tertiary text, raised above the design frame's 50%/25% to clear contrast bars.
        ("TextMid",            "#A6FFFFFF", "#A6000000"),
        ("TextLow",            "#73FFFFFF", "#73000000"),
        ("AccentAmber",        "#F5A05B", "#7D4C14"),
        ("AccentTeal",         "#31C7D3", "#0C6168"),
        ("JoinPill",           "#E6FFFFFF", "#E6111111"),
        ("JoinPillText",       "#E6000000", "#E6FFFFFF"),
        // Apple's system red, dark and light. Stop is the ONLY thing painted with it.
        ("StopRed",            "#FF453A", "#FF3B30"),
        // Toki's bubble disc. Dark must be genuinely dark: the card's warm glass a step lighter,
        // with a brighter rim, so the disc reads as its own object beside the card.
        ("MascotBubble",       "#E6383433", "#E6FFFFFF"),
        ("MascotBubbleRim",    "#33FFFFFF", "#26000000"),
        // The label ON a coral fill flips with the fill: near-black on the light salmon (dark
        // theme), white on the deep red (light theme). A single label colour cannot serve both.
        ("CoralText",          "#24100D", "#FFFFFF"),
        // Library list row resting fill, a step brighter under the pointer; selection is Chip.
        ("RowFill",            "#14FFFFFF", "#0F000000"),
        ("RowFillHover",       "#1FFFFFFF", "#17000000"),
        // A control resting ON the sidebar card. Must be OPAQUE: the translucent Glass tokens are
        // tuned for the notch and composite to near-invisibility over the sidebar's own fill.
        ("SidebarButton",      "#3C3837", "#CBC5C0"),
    };

    // Every value above is MEASURED (WCAG relative luminance): body/secondary text >= 4.5:1,
    // decoration >= 3.0:1, against the worst surface it can land on, including the pale accent
    // chips. Any colour ever painted as text needs both ends of the table; and check translucent
    // tokens composited over black AND white - the wallpaper can hide a failing contrast.

    // GRADIENT tokens: a SHAPE plus space-separated #AARRGGBB stops for dark and light, spread
    // evenly start to end. Frozen on build (safe to paint every frame of a morph); rebuilt on
    // every theme swap, because a frozen brush cannot be recoloured in place.
    enum Grad
    {
        Down,        // vertical, first stop at the TOP
        Up,          // vertical, first stop at the BOTTOM
        RadialTop,   // a soft glow centred on the top edge (light comes from above)
        Pulse,       // horizontal, transparent → the colour → transparent (a travelling highlight)
    }

    static readonly (string Key, Grad Kind, string Dark, string Light)[] Gradients =
    [
        // THE PLATE: ~95% opaque so the desktop tints it without contrast ever depending on the
        // wallpaper. The Opaque twin serves "Glassy notch" off - same material, only transparency changes.
        ("CardPlate",       Grad.Down,      "#F2343130 #F52A2726", "#F2FBF9F8 #F5F2EFEE"),
        ("CardPlateOpaque", Grad.Down,      "#FF343130 #FF2A2726", "#FFFBF9F8 #FFF2EFEE"),
        // THE RIM, brighter at the top (specular cue). In light it also separates a near-white
        // card from a near-white app behind it, so it ends on a dark hairline.
        ("CardRim",         Grad.Down,      "#33FFFFFF #26FFFFFF #1AFFFFFF", "#8CFFFFFF #3DFFFFFF #22000000"),
        // THE SHEEN: a soft radial glow at the top centre. The light theme sits at ~75% of the
        // dark value - white on a white plate does almost nothing whatever the alpha.
        ("CardSheen",       Grad.RadialTop, "#14FFFFFF #00FFFFFF", "#0FFFFFFF #00FFFFFF"),
        // The inner edges, painted as a 2px RING so they follow the corner arcs (a straight strip
        // would stick out past them); top carries the highlight, bottom the shade, sides fade.
        ("CardInnerTop",    Grad.Down,      "#40FFFFFF #00FFFFFF", "#4DFFFFFF #00FFFFFF"),
        ("CardInnerBottom", Grad.Up,        "#33000000 #00000000", "#14000000 #00000000"),
        // Toki's bubble at disc scale: a plate lit from above, rim brightest at the crown.
        ("MascotPlate",     Grad.Down,      "#E6403B39 #E6322E2D", "#F2FFFFFF #F2F1ECEA"),
        ("MascotRim",       Grad.Down,      "#4DFFFFFF #2EFFFFFF #1AFFFFFF", "#A6FFFFFF #1F000000 #2E000000"),
        // The loader sliver's travelling highlight; the light value is deepened to read on a pale track.
        ("Shimmer",         Grad.Pulse,     "#F48AAE", "#B03A63"),
        // The Ask page's atmosphere (figma 348:5438): a teal-blue bloom rising from the bottom.
        // The light value is the same hue as a TINT, faint enough that labels still clear 4.5:1.
        ("AskBloom",        Grad.Up,        "#3D103C46 #1E10333E #00000000", "#1F7FB4C4 #0F7FB4C4 #00FFFFFF"),
    ];

    /// <summary>
    /// Recolour the app by REPLACING each themed brush resource (never mutating .Color - a rendered
    /// brush is frozen and throws). MUST run once at startup BEFORE any window is built.
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var app = Application.Current;
        if (app is null) return;
        foreach (var (key, dark, light) in Palette)
            if (ColorConverter.ConvertFromString(theme == AppTheme.Light ? light : dark) is Color c)
                app.Resources[key] = new SolidColorBrush(c);   // replace → DynamicResource consumers re-resolve
        foreach (var (key, kind, dark, light) in Gradients)
            app.Resources[key] = BuildGradient(kind, theme == AppTheme.Light ? light : dark);
        Toki.App.Log($"theme applied: {theme}");
        try { ThemeChanged?.Invoke(); } catch (Exception ex) { Toki.App.Log($"theme changed handler failed: {ex.Message}"); }
    }

    /// <summary>Build one gradient token from its shape and its space-separated #AARRGGBB stops.
    /// Stops are spread evenly from the start of the gradient to its end; a Pulse takes ONE colour and
    /// makes transparent → colour → transparent out of it. Always frozen before it is handed out.</summary>
    static Brush BuildGradient(Grad kind, string stops)
    {
        var cols = stops.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => ColorConverter.ConvertFromString(s) is Color c ? c : Colors.Transparent)
                        .ToArray();
        GradientBrush g = kind switch
        {
            Grad.RadialTop => new RadialGradientBrush
            {
                // Centred ON the top edge, wide and shallow: a light source above the card, not a
                // spotlight inside it.
                Center = new Point(0.5, 0), GradientOrigin = new Point(0.5, 0),
                RadiusX = 0.55, RadiusY = 1.0,
            },
            Grad.Up => new LinearGradientBrush { StartPoint = new Point(0.5, 1), EndPoint = new Point(0.5, 0) },
            Grad.Pulse => new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) },
            _ => new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) },
        };
        if (kind == Grad.Pulse)
        {
            var c = cols.Length > 0 ? cols[0] : Colors.Transparent;
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0.0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xF2, c.R, c.G, c.B), 0.5));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1.0));
        }
        else
            for (var i = 0; i < cols.Length; i++)
                g.GradientStops.Add(new GradientStop(cols[i], cols.Length == 1 ? 0 : (double)i / (cols.Length - 1)));
        g.Freeze();   // frozen: painted every frame of a morph, never mutated
        return g;
    }

    public static AppTheme Parse(string? s) =>
        string.Equals(s, "light", System.StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark;
}
