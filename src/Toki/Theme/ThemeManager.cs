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
        // THE LIGHT KEY IS WARM, because the glass is. Until 2026-09-26 the light surfaces were
        // COOL (Surface #F4F4F6, Ink #FBFBFD, Chip #E7E7EC all measure red-minus-blue of -2 to -5)
        // while the card material next to them is warm (Glass #F4F1EF, +5). Two colour keys in one
        // theme. At near-black a -2 is invisible, which is exactly why the owner's complaint was
        // theme-specific: "in white theme, the spacing, lines, and everything is just so cheap and
        // UGLY". Every warm-key reference is consistently warm - Granola's canvas #F7F7F2 (+5),
        // Notion's page rgb(247,246,243) (+4).
        //
        // THE PAGE IS THE LIGHTEST THING, AND THE CARDS ARE TINTS ON IT. (2026-09-27)
        //
        // SUPERSEDES the "canvas tinted, card lightest" decision made one day earlier, whose
        // reasoning is kept below because it was sound and will be proposed again:
        //
        //     "Ink (the reading pane) sat on Surface at a lightness difference of about 2 -
        //      invisible - so the note never read as a page resting on a ground, just one
        //      undifferentiated white field. The canvas is now tinted and the reading pane is the
        //      lightest thing on it, which is the Apple grouped-list and Carbon layering pattern."
        //
        // What overturned it: the owner supplied two screenshots of the reference notetaker and
        // every value in them was sampled directly. The reference does the exact opposite, and it
        // is the thing the owner is pointing at. Measured off the reference, by pixel share:
        //     page       #FCFCFB   81.7% of all pixels   <- the LIGHTEST surface, and the ground
        //     card       #F5F4F0    4.8%                 <- a tint DOWN from the page
        //     chip/tint  #EEEBE3    4.8%                 <- a further step down, warm
        //     ink        #1A1A1A    1.8%
        //     hairline   #DAD9D5    0.02%
        // Four greys and one ink for a whole window. The Apple grouped-list pattern the old note
        // cites is a pattern for LISTS OF GROUPS on iOS, and Toki's reading pane is not that: it is
        // one document. Inverting the stack means a card no longer needs a border or a shadow to be
        // legible, so the edge count in the window drops, which is most of what "cheap" was.
        //
        // The warmth finding from 2026-09-26 SURVIVES and is reinforced: every light value here is
        // still warm (red-minus-blue positive), and so is the reference (#FCFCFB +1, #EEEBE3 +11).
        ("Surface",          "#0E0E10", "#FCFCFB"),  // main panel background (the CANVAS, lightest)
        ("SurfaceHi",        "#1C1C1E", "#F5F4F0"),  // raised surface / input field - a tint DOWN from the page
        ("Chip",             "#2C2C2E", "#EEEBE3"),  // chips, secondary buttons (on Surface)
        // A SECONDARY BUTTON ON THE NOTCH CARD. Chip was tuned against Surface (#0E0E10), which is
        // far darker than the notch's plate (#343130) - on the card it lands within a few points of
        // its own background and the button stops reading as a box at all. The owner found this on
        // NoSpeech: "Okay" looked like a line of text. Raised against the PLATE, not the panel.
        ("ChipOnCard",       "#474342", "#EAE6DE"),
        ("Text",             "#F5F5F7", "#1A1A1A"),  // primary text (the reference's ink, sampled)
        ("TextSoft",         "#D8D8DC", "#3A3A3C"),  // body/summary text (a touch softer than primary)
        // THE REFERENCE VALUE DOES NOT FIT THIS CARD, and the measurement is the whole point.
        // TextDim and the alpha ramp's TextMid do measure the same lightness step (L* 62.0 vs 61.3),
        // so the palette really does carry two names for one colour - that finding stands, and the
        // fix belongs in the CODE that picks between them, not here.
        //
        // But the recommended #646464 - where Radix, Geist, Primer and Material independently landed
        // for secondary text - measures 3.74:1 on Toki's light card over a black wallpaper. Those
        // systems assume an OPAQUE surface. Toki's card is Glass at 85%, so what any colour sits on
        // depends on the user's desktop, and that case sets a ceiling the references never had to
        // clear. Solving it: the lightest grey that holds 4.5:1 here is about #575757, and #56565A
        // was already sitting on that limit. It stays. (2026-09-27, the same trap the light amber
        // fell into three days earlier.)
        ("TextDim",          "#ADADB1", "#56565A"),  // secondary/dim; at the translucent card's ceiling
        // The reading pane is the SAME colour as the canvas now, on purpose: in the reference the
        // note is not a card sitting on a page, it IS the page. Kept as its own key because the dark
        // theme still wants the pane a step off the canvas, and because every call site says "Ink"
        // to mean "the surface a note is read on" - which is the right name either way.
        ("Ink",              "#161618", "#FCFCFB"),  // Library reading pane
        // Ink with the alpha DWM acrylic shows through. Not a lighter colour: the material has to
        // read as depth behind the page, while body text keeps a stable ground to sit on. Only used
        // when real acrylic was actually applied - otherwise the window stays fully opaque Ink.
        ("InkScrim",          "#D9161618", "#D9FBFBFD"),
        // The notch is a tinted mirror: semi-transparent so the desktop bleeds through slightly,
        // but kept fairly opaque so text stays readable and it never washes out to grey.
        ("Notch",            "#D9000000", "#E0FCFCFE"),  // ~85% black / ~88% white - desktop shows faintly through
        ("NotchBorder",      "#40FFFFFF", "#33000000"),  // a soft rim so the glassy edge reads
        ("WhiteBtn",         "#F2F2F2", "#1D1D1F"),  // primary CTA button fill (inverts to near-black)
        // THE SWITCH KNOB WHEN ON, AND IT DOES NOT INVERT. The knob used WhiteBtn, which flips to
        // near-black in the light theme - at exactly the moment the track turns dark blue. A black
        // knob on a #1C579B track is the one combination that cannot be read, and it was the light
        // theme's every "on" switch. The knob's job is to contrast with the TRACK, not with the page.
        ("SwitchKnobOn",     "#F2F2F2", "#FFFFFF"),
        ("CtaText",          "#161618", "#FCFCFE"),  // text on the CTA
        // Hover / press feedback: the only affordance a button has before it's clicked. On the
        // pitch-black notch a low-alpha white wash is invisible (12% white on #000 is ~1.15:1),
        // so these stay strong - do not weaken them.
        ("HoverOverlay",     "#38FFFFFF", "#1F000000"),   // dark: 22% white (~RGB 56 on black) · light: 12% black
        ("PressOverlay",     "#57FFFFFF", "#33000000"),   // press is clearly deeper than hover, both ways
        // The reference's rule is #DAD9D5 on a #FCFCFB page, which is 13.5% black - the light value
        // here was 8.6% and simply did not arrive on screen. A rule you cannot see is not restraint,
        // it is a rule you should have deleted; the ones Toki keeps now have to actually read.
        ("Hairline",         "#18FFFFFF", "#22000000"),
        ("SelectionBorder",  "#66FFFFFF", "#33000000"),
        ("CheckBorder",      "#55FFFFFF", "#55000000"),
        ("CheckBorderHover", "#88FFFFFF", "#88000000"),
        ("ScrollThumbBrush",      "#3A3A3D", "#C4C4C8"),
        ("ScrollThumbHoverBrush", "#565659", "#A6A6AB"),
        ("ScrollThumbDragBrush",  "#6A6A6E", "#8E8E93"),
        // Accent tints + the accent text on them. Light values are derived against a 4.5:1
        // contrast target on the darkest surface they can land on (Chip and the pale accent chips).
        // AMBER'S LIGHT VALUE WAS BROWN. #794F1A clears 4.5:1 comfortably and reads as mud, not as
        // "amber = time": the recording timer on a near-white card looked like a stain. Contrast was
        // never the problem; the value was picked for the number and nobody looked at it.
        //
        // #87430A is the same decision made with BOTH eyes open. It is far more SATURATED than the
        // brown it replaces (93% vs 65%, and B=10 against B=26) so it reads as burnt orange, while
        // still clearing 4.68:1 at its worst case.
        //
        // THE BINDING CONSTRAINT IS NOT THE CARD, IT IS THE WALLPAPER. The light card is Glass at 85%
        // (#D9F4F1EF), so over a BLACK desktop the surface amber lands on is much darker than the
        // plate suggests, and that case - not any opaque surface - is what caps the brightness. Two
        // brighter candidates measured fine on every opaque surface and failed there: #B45309 at
        // 3.79:1 and #A04B06 at 3.79:1. ContrastTests caught both, which is what that file is for.
        // There is no bright amber available here while the card stays see-through. (2026-09-23)
        ("Amber",            "#E89B3B", "#87430A"),
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
        // THE DARK MATERIAL IS NEUTRAL, NOT WARM BROWN. #211E1E / #2B2828 / #343130 are warm greys
        // (red exceeds blue by 3-4 points), and at this lightness warm grey reads as aged plastic -
        // the owner, three times: "the fake glass color is not helping, instead making notch look old
        // and ugly". The dark Surface beside it (#0E0E10) is already slightly COOL, so the theme was
        // also carrying two colour keys at once, the same collision the light theme had.
        //
        // Every value below keeps its LIGHTNESS and only loses the warm cast, so contrast, the
        // material stack and the edge lights are all unchanged. The LIGHT theme stays warm on
        // purpose: there the references are one-directional (Granola #F7F7F2, Notion) and warm light
        // reads as paper, where warm dark reads as dust.
        // THE NOTCH CARRIES THE PAGE COLOUR (owner, 2026-09-27: "add the bg color to the notch as
        // well"). In light theme the notch was a warm putty (#F4F1EF) while the Library window it
        // belongs to was a different white again - the two halves of one product in two materials.
        // Light glass is now the reference page colour, so the notch and the window read as cut from
        // the same sheet. It also RAISES the contrast ceiling the translucent card imposes, because
        // a lighter card composited over a dark wallpaper still lands lighter than the old one did.
        ("Glass",              "#D91D1D1F", "#D9FCFCFB"),
        ("GlassOpaque",        "#29292B",   "#FAF9F6"),
        ("GlassCapsule",       "#66211E1E", "#80F3F1EB"),
        ("GlassCapsuleOpaque", "#252222",   "#EEEBE3"),
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
        // TINTED, NOT PURE BLACK. The alphas (3-7%) are already inside every published band; the
        // HUE was the tell. No premium system drops a neutral black shadow on a warm surface - it
        // greys the colour underneath instead of darkening it. Keyed to the card's own warm dark.
        ("Shadow1",            "#0D000000", "#09231F1E"),
        ("Shadow2",            "#12000000", "#0C231F1E"),
        ("Shadow3",            "#18000000", "#11231F1E"),
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
        // Light: the page colour at the top settling one step into the card tint at the bottom, so
        // the plate is the same sheet as the Library window rather than a separate putty.
        ("CardPlate",       Grad.Down,      "#F2313133 #F5272729", "#F2FCFCFB #F5F5F4F0"),
        ("CardPlateOpaque", Grad.Down,      "#FF313133 #FF272729", "#FFFCFCFB #FFF5F4F0"),
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
        ("MascotPlate",     Grad.Down,      "#E63C3C3E #E62F2F31", "#F2FFFFFF #F2F1ECEA"),
        ("MascotRim",       Grad.Down,      "#4DFFFFFF #2EFFFFFF #1AFFFFFF", "#A6FFFFFF #1F000000 #2E000000"),
        // The loader sliver's travelling highlight - brand BLUE now (was pink, which read as "questions",
        // a reserved content colour). Blue = Toki-active chrome (matches the on-toggle). Dark = bright
        // blue on the dark card; light value deepened to read on a pale track.
        ("Shimmer",         Grad.Pulse,     "#68B0F9", "#1C579B"),
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
            {
                // Evenly spaced. An eased distribution (t^1.6) was tried on 2026-09-03 to make the
                // plate read as a lit surface rather than a ramp, and REVERTED the same day: it
                // applied to every Down/Up gradient with more than two stops, which silently
                // included CardRim, and the owner reported the light theme turning grey.
                var t = cols.Length == 1 ? 0 : (double)i / (cols.Length - 1);
                g.GradientStops.Add(new GradientStop(cols[i], t));
            }
        g.Freeze();   // frozen: painted every frame of a morph, never mutated
        return g;
    }

    public static AppTheme Parse(string? s) =>
        string.Equals(s, "light", System.StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark;
}
