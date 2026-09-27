using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// THE DESIGN DECISIONS OF 2026-09-23, PINNED.
///
/// The owner recorded a meeting, looked at the result and said: "there is an ugly yellow color ...
/// the fake glass got faker than ever ... it cannot make weird changes, weird colors". Every fix
/// below came out of photographing all 27 notch states and 11 Library pages in BOTH themes, which
/// is the only way any of them were going to be found - a green suite had been green throughout.
///
/// These are cheap source assertions, not a substitute for looking. They exist so a value that was
/// argued about once does not drift back silently.
/// </summary>
public sealed class DesignTokenTests
{
    static string Root
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "Toki.sln"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("repo root not found");
        }
    }

    static string Src(params string[] parts) => File.ReadAllText(Path.Combine(Root, Path.Combine(parts)));

    static IReadOnlyDictionary<string, (string Dark, string Light)> Palette()
    {
        var text = Src("src", "Toki", "Theme", "ThemeManager.cs");
        var rx = new Regex(@"\(""(?<k>\w+)"",\s*""(?<d>#[0-9A-Fa-f]{6,8})"",\s*""(?<l>#[0-9A-Fa-f]{6,8})""\)");
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (Match m in rx.Matches(text)) map[m.Groups["k"].Value] = (m.Groups["d"].Value, m.Groups["l"].Value);
        return map;
    }

    static double Lin(double c) { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }

    static (int R, int G, int B) Rgb(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 8) h = h[2..];
        return (Convert.ToInt32(h[..2], 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16));
    }

    static double Lum(string hex) { var c = Rgb(hex); return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B); }

    /// <summary>HSV saturation, 0..1. The measure that separates "burnt orange" from "mud".</summary>
    static double Sat(string hex)
    {
        var (r, g, b) = Rgb(hex);
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        return max == 0 ? 0 : (max - min) / (double)max;
    }

    // ── colour ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_light_amber_is_orange_not_mud()
    {
        // #794F1A passed every contrast check and read as a brown stain on the recording timer.
        // Contrast was never the problem, so contrast alone cannot guard it: saturation is the axis
        // that was wrong. ContrastTests still owns the readability half.
        var amber = Palette()["Amber"].Light;
        Assert.True(Sat(amber) > 0.85,
            $"light Amber {amber} is only {Sat(amber):P0} saturated - that is the brown the owner rejected");
    }

    [Fact]
    public void The_switch_knob_does_not_invert_with_the_theme()
    {
        // The knob used WhiteBtn, which flips to near-black in light - exactly when the track turns
        // dark blue. Both values must stay light, because the track is Blue in BOTH themes when on.
        var (dark, light) = Palette()["SwitchKnobOn"];
        Assert.True(Lum(dark) > 0.7, $"SwitchKnobOn dark {dark} is too dark to read on a blue track");
        Assert.True(Lum(light) > 0.7, $"SwitchKnobOn light {light} is too dark to read on a blue track");
    }

    [Fact]
    public void A_secondary_button_on_the_notch_card_is_visible_against_it()
    {
        // Chip was tuned against Surface (#0E0E10). The notch plate is #343130, which Chip lands
        // within a few points of - the NoSpeech "Okay" stopped reading as a button at all.
        var p = Palette();
        foreach (var (chip, plate, theme) in new[]
                 {
                     (p["ChipOnCard"].Dark, "#343130", "dark"),
                     (p["ChipOnCard"].Light, "#FBF9F8", "light"),
                 })
        {
            var d = Math.Abs(Lum(chip) - Lum(plate));
            Assert.True(d > 0.012, $"{theme}: ChipOnCard {chip} is {d:F4} luminance from the plate - invisible");
        }
    }

    // ── one meaning, one colour ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_day_picker_uses_chrome_blue_not_content_pink()
    {
        // Pink is the QUESTIONS content colour, and it is Ask's accent - which is why the loader
        // shimmer moved off it in 2026-08. The day picker was the last chrome using it: selected
        // day, today ring and the Today link all rendered magenta.
        var picker = Src("src", "Toki", "Views", "DayPicker.cs");
        Assert.DoesNotContain("\"Pink\"", picker);
        Assert.Contains("\"Blue\"", picker);
    }

    [Fact]
    public void A_licence_chip_is_not_painted_in_the_time_colour()
    {
        // Amber means time and counts. A licence is neither, and as a filled AmberDark chip it
        // rendered as a pale yellow lozenge on the light About card.
        var about = Src("src", "Toki", "Views", "AboutView.xaml.cs");
        Assert.DoesNotContain("AmberDark", about);
    }

    [Fact]
    public void There_is_one_Join_button_not_an_amber_twin()
    {
        // The imminent Join was an amber-filled StarButton sitting right beside the amber "in N min"
        // countdown - two urgency signals, and a cream pill in the light theme.
        // The KEY, not the word - the comment explaining why it went still names it.
        Assert.DoesNotContain("\"StarButton\"]", Src("src", "Toki", "Views", "CalendarRows.cs"));
        Assert.DoesNotContain("x:Key=\"StarButton\"", Src("src", "Toki", "Theme", "Theme.xaml"));
    }

    // ── affordance ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_library_search_field_says_what_it_is()
    {
        // Empty, it was a bare rectangle - a black void on the dark card beside "+ New note".
        var xaml = Src("src", "Toki", "Views", "LibraryView.xaml");
        Assert.Contains("SearchHint", xaml);
        Assert.Contains("Search your meetings", xaml);
    }

    [Fact]
    public void The_notch_card_carries_no_photograph_of_the_desktop()
    {
        // A blurred screen capture painted OVER the plate (a Border's Background is behind its
        // child, so "under the plate tint" was never true) put the owner's browser on every card.
        Assert.False(File.Exists(Path.Combine(Root, "src", "Toki", "Theme", "ScreenBackdrop.cs")),
            "ScreenBackdrop is back - the notch is photographing the desktop again");
        var pill = Src("src", "Toki", "PillWindow.xaml");
        Assert.DoesNotContain("x:Name=\"Backdrop\"", pill);
    }
}
