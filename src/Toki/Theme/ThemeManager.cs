using System.Windows;
using System.Windows.Media;

namespace Toki.Theme;

public enum AppTheme { Dark, Light }

/// <summary>
/// Live light/dark switching. Every themeable colour in Theme.xaml is a Color resource that the
/// brushes bind to with DynamicResource; this just writes the right Color values into
/// Application.Resources for the chosen theme, and — because DynamicResource is a live link — every
/// brush (and thus every control, even those using {StaticResource}) recolours instantly with no
/// rebuild and no per-view code. Default is Dark (Toki's signature black notch); Light is a clean,
/// Apple-like near-white.
/// </summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Fired after the palette swaps. Views built in CODE (which capture brushes via
    /// FindResource and so don't re-resolve on their own) subscribe to rebuild/recolour — the Library
    /// list + detail, the pill Library, the Notes content. XAML DynamicResource consumers don't need it.</summary>
    public static event Action? ThemeChanged;

    // brush key → (dark, light). Dark mirrors the Theme.xaml defaults (re-asserted so a toggle back
    // is exact); Light is the inverted, contrast-checked palette. Accents that don't invert (Coral,
    // Pink, Purple) are absent on purpose — they're constant brushes.
    static readonly (string Key, string Dark, string Light)[] Palette =
    {
        // Light values are warm, soft Apple off-whites (not stark "x-ray" #FFFFFF); dark values are
        // rich near-blacks. Notch stays pure black in dark (it reads as a hole in the edge).
        ("Surface",          "#0E0E10", "#F4F4F6"),  // main panel background
        ("SurfaceHi",        "#1C1C1E", "#FCFCFE"),  // raised surface / input field (soft white)
        ("Chip",             "#2C2C2E", "#E7E7EC"),  // chips, secondary buttons
        ("Text",             "#F5F5F7", "#1D1D1F"),  // primary text (Apple label near-black)
        ("TextSoft",         "#D8D8DC", "#3A3A3C"),  // body/summary text (a touch softer than primary)
        ("TextDim",          "#98989D", "#86868B"),  // secondary/dim text (Apple secondary label)
        ("Ink",              "#161618", "#FBFBFD"),  // Library reading pane (soft, not glaring white)
        // The notch is a TINTED MIRROR: semi-transparent (alpha < FF) so the desktop colours behind it
        // bleed through slightly — a see-through slab, not a blur. Kept fairly opaque so text stays
        // readable and it never washes out to grey (the reason it went opaque before).
        ("Notch",            "#D9000000", "#E0FCFCFE"),  // ~85% black / ~88% white — desktop shows faintly through
        ("NotchBorder",      "#40FFFFFF", "#33000000"),  // a soft rim so the glassy edge reads
        ("WhiteBtn",         "#F2F2F2", "#1D1D1F"),  // primary CTA button fill (inverts to near-black)
        ("CtaText",          "#161618", "#FCFCFE"),  // text on the CTA
        ("HoverOverlay",     "#1FFFFFFF", "#14000000"),
        ("PressOverlay",     "#33FFFFFF", "#24000000"),
        ("Hairline",         "#18FFFFFF", "#16000000"),
        ("SelectionBorder",  "#66FFFFFF", "#33000000"),
        ("CheckBorder",      "#55FFFFFF", "#55000000"),
        ("CheckBorderHover", "#88FFFFFF", "#88000000"),
        ("ScrollThumbBrush",      "#3A3A3D", "#C4C4C8"),
        ("ScrollThumbHoverBrush", "#565659", "#A6A6AB"),
        ("ScrollThumbDragBrush",  "#6A6A6E", "#8E8E93"),
        // Accent tints + the accent text on them. Text darkens on light for contrast on pale tints.
        ("Amber",            "#E89B3C", "#9A6614"),
        ("AmberDark",        "#3B2F1B", "#F6E8CB"),
        ("Green",            "#3DD68C", "#1E8A55"),
        ("GreenDark",        "#12351F", "#CDEDDB"),
        ("Blue",             "#5AA9F9", "#2A72C7"),
        ("BlueDark",         "#12314F", "#D3E6FA"),
    };

    /// <summary>
    /// Recolour the whole app to the given theme by REPLACING each themed brush resource in the app's
    /// top-level dictionary with a fresh brush of the new colour. Every consumer references these via
    /// {DynamicResource}, so replacing the resource makes them re-resolve and repaint instantly — no
    /// rebuild, no per-view code. (We do NOT mutate a brush's .Color: WPF freezes brushes once they've
    /// been rendered, and a frozen brush throws "read-only state" on mutation — replacing sidesteps
    /// that entirely.) MUST run once at startup BEFORE windows are built so consumers bind to these.
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var app = Application.Current;
        if (app is null) return;
        foreach (var (key, dark, light) in Palette)
            if (ColorConverter.ConvertFromString(theme == AppTheme.Light ? light : dark) is Color c)
                app.Resources[key] = new SolidColorBrush(c);   // replace → DynamicResource consumers re-resolve
        Toki.App.Log($"theme applied: {theme}");
        try { ThemeChanged?.Invoke(); } catch (Exception ex) { Toki.App.Log($"theme changed handler failed: {ex.Message}"); }
    }

    public static AppTheme Parse(string? s) =>
        string.Equals(s, "light", System.StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark;
}
