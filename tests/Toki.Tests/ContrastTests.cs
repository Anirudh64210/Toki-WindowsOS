using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Every colour Toki paints text in must actually be readable on every surface it can land on, in
/// both themes.
///
/// This exists because "the text kind of blends into the background" has now been reported twice on
/// two different causes, and both times the answer was a number nobody had computed. In August 2026
/// a 12% white hover wash on a black notch measured 1.15:1 and was debugged for hours as a broken
/// input layer. Later the same month the light theme shipped with Pink at 1.66:1, AccentAmber at
/// 2.42:1 and no light value at all for Coral or Purple, while the dark card at 50% opacity let
/// white-90% text fall to 2.96:1 over a bright window.
///
/// So the palette is now checked arithmetically rather than by eye:
///   · anything used as TEXT       >= 4.5:1  (WCAG AA for body text)
///   · anything purely DECORATIVE  >= 3.0:1  (AA for non-text contrast)
/// against every opaque surface, AND against the translucent card composited over a black, grey and
/// white desktop - because a see-through card's contrast depends on the user's wallpaper, which is
/// exactly the case that hides from a screenshot.
///
/// If this fails, do not lower the target. Either pick a different colour, or make the surface less
/// transparent - the card's alpha is a contrast decision, not only a look.
/// </summary>
public sealed class ContrastTests
{
    const double TextTarget = 4.5;
    const double DecorTarget = 3.0;

    // Read straight from the shipping palette so the test cannot drift from what the app paints.
    static readonly IReadOnlyDictionary<string, (string Dark, string Light)> Palette = ReadPalette();

    static IReadOnlyDictionary<string, (string, string)> ReadPalette()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "Toki.sln"))) d = d.Parent;
        var root = d?.FullName ?? throw new InvalidOperationException("repo root not found");
        var path = System.IO.Path.Combine(root, "src", "Toki", "Theme", "ThemeManager.cs");
        var text = System.IO.File.ReadAllText(path);
        var rx = new System.Text.RegularExpressions.Regex(
            @"\(""(?<k>\w+)"",\s*""(?<d>#[0-9A-Fa-f]{6,8})"",\s*""(?<l>#[0-9A-Fa-f]{6,8})""\)");
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(text))
            map[m.Groups["k"].Value] = (m.Groups["d"].Value, m.Groups["l"].Value);
        return map;
    }

    // Colours painted as text or as a meaningful glyph.
    static readonly string[] TextKeys =
    {
        "TextHi", "TextMid", "Text", "TextSoft", "TextDim",
        "Amber", "Green", "Blue", "AccentAmber", "AccentTeal", "Pink", "Purple", "Coral", "Danger",
    };

    // Colours that only have to be VISIBLE, not readable: chevrons, dividers, decorative ticks.
    static readonly string[] DecorKeys = { "TextLow" };

    // Every opaque surface any of the above can sit on, including the pale/dark accent chips.
    static readonly string[] SurfaceKeys =
    {
        "Surface", "SurfaceHi", "Chip", "Ink", "GlassOpaque", "AmberDark", "GreenDark", "BlueDark",
    };

    /// <summary>
    /// The checks below read the palette out of the source file, so a parse that silently returned
    /// nothing would make every one of them pass while measuring absolutely nothing. This project
    /// has shipped exactly that before ("a self-test that never produced its real artifact is not a
    /// test"), so the parse is asserted first: every key the other tests name must be present.
    /// </summary>
    [Fact]
    public void ThePaletteWasActuallyParsed()
    {
        Assert.True(Palette.Count >= 30, $"only parsed {Palette.Count} palette entries from ThemeManager.cs");
        foreach (var k in TextKeys.Concat(DecorKeys).Concat(SurfaceKeys).Concat(new[] { "Glass", "CoralText" }))
            Assert.True(Palette.ContainsKey(k), $"palette key '{k}' not found - has ThemeManager.cs been reformatted?");
    }

    [Theory]
    [InlineData(0, "dark")]
    [InlineData(1, "light")]
    public void EveryTextColourIsReadableOnEveryOpaqueSurface(int themeIndex, string themeName)
    {
        var failures = new List<string>();
        foreach (var (key, target) in TextKeys.Select(k => (k, TextTarget))
                                              .Concat(DecorKeys.Select(k => (k, DecorTarget))))
        {
            if (!Palette.TryGetValue(key, out var fg)) continue;
            foreach (var s in SurfaceKeys)
            {
                if (!Palette.TryGetValue(s, out var bg)) continue;
                var r = Ratio(Pick(fg, themeIndex), Pick(bg, themeIndex));
                if (r < target)
                    failures.Add($"{themeName}: {key} on {s} is {r:F2}:1, needs {target}:1");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData(0, "dark")]
    [InlineData(1, "light")]
    public void EveryTextColourSurvivesTheTranslucentCardOverAnyWallpaper(int themeIndex, string themeName)
    {
        // Black, mid grey and white stand in for "whatever the user has behind the notch". The card
        // is semi-transparent, so these are the real backgrounds, not GlassOpaque.
        var desktops = new[] { (0, 0, 0), (128, 128, 128), (255, 255, 255) };
        var glass = Pick(Palette["Glass"], themeIndex);

        var failures = new List<string>();
        foreach (var (key, target) in TextKeys.Select(k => (k, TextTarget))
                                              .Concat(DecorKeys.Select(k => (k, DecorTarget))))
        {
            if (!Palette.TryGetValue(key, out var fg)) continue;
            foreach (var d in desktops)
            {
                var card = Over(glass, (d.Item1, d.Item2, d.Item3));
                var r = Contrast(Over(Pick(fg, themeIndex), card), card);
                if (r < target)
                    failures.Add($"{themeName}: {key} on the glass card over rgb{d} is {r:F2}:1, needs {target}:1");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>A colour that is used as a FILL needs its own label to be readable ON it.</summary>
    [Theory]
    [InlineData("Coral", "CoralText")]
    [InlineData("WhiteBtn", "CtaText")]
    [InlineData("JoinPill", "JoinPillText")]
    public void EveryFilledButtonsLabelIsReadableOnIt(string fillKey, string labelKey)
    {
        for (var theme = 0; theme < 2; theme++)
        {
            var r = Ratio(Pick(Palette[labelKey], theme), Pick(Palette[fillKey], theme));
            Assert.True(r >= TextTarget,
                $"{(theme == 0 ? "dark" : "light")}: {labelKey} on {fillKey} is {r:F2}:1, needs {TextTarget}:1");
        }
    }

    static string Pick((string Dark, string Light) pair, int theme) => theme == 0 ? pair.Dark : pair.Light;

    // ── the WCAG arithmetic ──

    static (double A, int R, int G, int B) Parse(string hex)
    {
        var h = hex.TrimStart('#');
        return h.Length == 8
            ? (int.Parse(h[..2], NumberStyles.HexNumber) / 255.0,
               int.Parse(h.Substring(2, 2), NumberStyles.HexNumber),
               int.Parse(h.Substring(4, 2), NumberStyles.HexNumber),
               int.Parse(h.Substring(6, 2), NumberStyles.HexNumber))
            : (1.0,
               int.Parse(h[..2], NumberStyles.HexNumber),
               int.Parse(h.Substring(2, 2), NumberStyles.HexNumber),
               int.Parse(h.Substring(4, 2), NumberStyles.HexNumber));
    }

    static (double R, double G, double B) Over(string fg, (double R, double G, double B) bg)
    {
        var (a, r, g, b) = Parse(fg);
        return (a * r + (1 - a) * bg.R, a * g + (1 - a) * bg.G, a * b + (1 - a) * bg.B);
    }

    static double Luminance((double R, double G, double B) c)
    {
        static double Channel(double v)
        {
            v /= 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var (l1, l2) = (Luminance(a), Luminance(b));
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    /// <summary>Contrast of a (possibly translucent) foreground against an OPAQUE background hex.</summary>
    static double Ratio(string fg, string bgHex)
    {
        var (_, br, bg, bb) = Parse(bgHex);
        return Contrast(Over(fg, (br, bg, bb)), (br, bg, bb));
    }
}
