using System;
using System.IO;
using System.Text.RegularExpressions;
using Toki;
using Toki.Storage;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// The small controls a render catches and a green suite does not: a puck sliced flat by the row it
/// sits in, a red frozen into code where a token was waiting, a triangle that is really a character
/// from whatever font the machine fell back to, and a colour that codes nothing.
///
/// Every check here was written against the BROKEN code first and watched to fail. Several of them
/// have to read the source rather than the running control, because building a WPF view needs an
/// Application with the theme dictionary loaded and this suite deliberately has neither - so where a
/// fact lives in a number in a file, the number in the file is what gets asserted. The live, arranged
/// versions of the same facts are the app's own harnesses (--hittest now fails on any clickable thing
/// that layout is clipping; --render pictures the rest).
/// </summary>
public sealed class ActionButtonTests
{
    static readonly string Root = FindRoot();
    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Toki.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
    static string Src(params string[] parts) => File.ReadAllText(Path.Combine(Root, Path.Combine(parts)));

    /// <summary>The file with its COMMENTS taken out. Every one of these checks is about what the app
    /// paints, and the prose around it names the very things being banned - so a test that read the
    /// comments would fail on the note explaining why the value is gone.</summary>
    static string CodeOnly(string text)
    {
        text = Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);   // XAML
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^\n]*", " ");
    }

    // ── 1. the recording bar's row has to fit the puck on it ──

    [Fact]
    public void The_recording_bar_row_is_tall_enough_for_its_glass_puck()
    {
        var xaml = Src("src", "Toki", "Views", "RecordingOpenView.xaml");
        var row = Regex.Match(xaml, @"x:Name=""BarRow""\s+Margin=""(?<m>[\d,.]+)""\s+Height=""(?<h>[\d.]+)""");
        Assert.True(row.Success, "BarRow's Margin and Height are not where this test expects them");

        var puck = Regex.Match(Src("src", "Toki", "Views", "PillParts.cs"),
            @"public static Button GlassPuck\([^)]*double size = (?<s>[\d.]+)");
        Assert.True(puck.Success, "GlassPuck's default size is not where this test expects it");

        var rowHeight = double.Parse(row.Groups["h"].Value);
        var puckSize = double.Parse(puck.Groups["s"].Value);

        // A control taller than its slot keeps its size and gets a LAYOUT CLIP: the bottom of the disc
        // is cut flat and the shadow rings under it are cropped. It is invisible to every measurement
        // that asks the control how big it is, and obvious in a render.
        Assert.True(rowHeight >= puckSize,
            $"the recording bar row is {rowHeight}px but the puck on it is {puckSize}px - layout will slice the puck");

        // And the pill it adds up to is unchanged: the frame's 50px bar.
        var margin = Array.ConvertAll(row.Groups["m"].Value.Split(','), double.Parse);
        Assert.Equal(50, margin[1] + rowHeight + margin[3]);
    }

    // ── 2. stop is the themed Apple red, and nothing paints a frozen one ──

    [Fact]
    public void Stop_red_is_apples_system_red_in_both_themes()
    {
        var pair = Regex.Match(Src("src", "Toki", "Theme", "ThemeManager.cs"),
            @"\(""StopRed"",\s*""(?<d>#[0-9A-Fa-f]{6})"",\s*""(?<l>#[0-9A-Fa-f]{6})""\)");
        Assert.True(pair.Success, "StopRed is missing from the palette");
        Assert.Equal("#FF453A", pair.Groups["d"].Value.ToUpperInvariant());   // systemRed, dark
        Assert.Equal("#FF3B30", pair.Groups["l"].Value.ToUpperInvariant());   // systemRed, light
        Assert.Contains("<SolidColorBrush x:Key=\"StopRed\" Color=\"#FF453A\"/>",
            Src("src", "Toki", "Theme", "Theme.xaml"));
    }

    [Fact]
    public void No_red_is_frozen_into_code_where_the_token_belongs()
    {
        // #FF114D was the frame's red, read once into a static brush, so the stop disc could never
        // follow a theme switch. #FF2D55 was the token nobody consumed - Apple's systemPink, which is
        // not the clean red the owner asked for.
        foreach (var dead in new[] { "FF114D", "FF2D55" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
                if (Path.GetExtension(file) is not (".cs" or ".xaml")) continue;
                Assert.DoesNotContain(dead, CodeOnly(File.ReadAllText(file)), StringComparison.OrdinalIgnoreCase);
            }
        Assert.DoesNotContain("FF2D55", File.ReadAllText(Path.Combine(Root, "CLAUDE.md")));
    }

    [Fact]
    public void The_stop_disc_takes_its_fill_as_a_resource_key()
    {
        // A brush read out of the resources once is the captured-brush bug: ThemeManager swaps themes by
        // REPLACING the resource, and a captured brush never hears about it.
        var parts = Src("src", "Toki", "Views", "PillParts.cs");
        Assert.Matches(@"RoundButton\(FrameworkElement glyph, string backgroundKey", parts);
        Assert.Contains("b.SetResourceReference(Control.BackgroundProperty, backgroundKey);", parts);
        Assert.Contains("\"StopRed\", \"Stop recording and write the notes\"",
            Src("src", "Toki", "Views", "RecordingOpenView.xaml.cs"));
    }

    // ── 3. a small glyph centres SUB-PIXEL, or it snaps off-centre at 125% ──

    [Fact]
    public void The_stop_glyph_opts_out_of_layout_rounding()
    {
        // At 125% a 10px square in a 24px disc leaves 8.75 device pixels either side. Inherited layout
        // rounding rounds those apart and the square visibly sits off-centre. Both flags off is what
        // VoiceWave already does for the same reason.
        var parts = Src("src", "Toki", "Views", "PillParts.cs");
        Assert.Contains("element.UseLayoutRounding = false;", parts);
        Assert.Contains("element.SnapsToDevicePixels = false;", parts);
        // Every glyph factory opts out: the two stop squares, the play triangle, the outline
        // icons (the chevron leaned left in its puck without it, owner 2026-08-19), and the
        // initial-disc label. A factory without .SubPixel() is a glyph that snaps off-centre
        // at 125% - count both sides so adding one without the other fails here.
        Assert.Equal(3, Regex.Matches(parts, @"public static Path (StopGlyph|PlayGlyph)").Count);
        Assert.Equal(1, Regex.Matches(parts, @"public static Path Outline").Count);
        // The disc initial is a Shape now, not a TextBlock: it is built from the glyph's own
        // geometry so its layout box IS the ink, which centres it without any nudge to keep in
        // sync. See DiscLabelTests.
        Assert.Equal(1, Regex.Matches(parts, @"public static Shape DiscLabel").Count);
        Assert.Equal(5, Regex.Matches(parts, @"\.SubPixel\(\)").Count);
    }

    [Fact]
    public void The_glyph_geometries_are_authored_at_their_final_pixel_size()
    {
        // Stretch.None everywhere means the numbers in the geometry ARE the pixels on screen, so they
        // are worth asserting: 10px stop square on the recording bar, 8px inline, an 8x10 triangle.
        static void Size(System.Windows.Media.Geometry g, double w, double h)
        {
            Assert.Equal(w, g.Bounds.Width, 3);
            Assert.Equal(h, g.Bounds.Height, 3);
        }
        Size(Toki.Views.PillParts.StopSquare(), 10, 10);
        Size(Toki.Views.PillParts.StopSquare(8), 8, 8);
        Size(Toki.Views.PillParts.PlayTriangle(), 8, 10);
    }

    // ── 4/5. the evidence player: paths, not characters; house glass grammar ──

    [Fact]
    public void The_evidence_player_draws_paths_not_font_characters()
    {
        var player = CodeOnly(Src("src", "Toki", "Views", "EvidencePlayer.cs"));
        // The literal characters it used to set as Text. A font glyph's size, side bearings and
        // vertical position come from whichever font the machine falls back to, which is why the old
        // triangle needed a hand-tuned margin to look centred at all.
        Assert.DoesNotContain("▶", player);
        Assert.DoesNotContain("■", player);
        Assert.DoesNotContain("new TextBlock", player);
        Assert.Contains("PillParts.PlayGlyph(\"TextMid\")", player);
        Assert.Contains("PillParts.StopGlyph(\"Pink\")", player);
        // The triangle's optical nudge has to be a render transform, for the same reason the send
        // disc's is: a margin inside a centre-aligned ContentPresenter only moves it half a pixel.
        Assert.Contains("RenderTransform = new TranslateTransform(1, 0)",
            Src("src", "Toki", "Views", "PillParts.cs"));
    }

    [Fact]
    public void The_evidence_player_is_a_quiet_bare_control()
    {
        // The play control used to be a 20px filled disc with a rim (EvidenceChip) - a bulky bead in
        // the middle of a sentence. It is now EvidencePlay: a bare triangle, resting dim, brightening
        // on hover, with the house press-compress. No disc, no rim.
        var player = Src("src", "Toki", "Views", "EvidencePlayer.cs");
        Assert.Contains("Application.Current.Resources[\"EvidencePlay\"]", player);
        // The instant background swap it used to do on MouseEnter/MouseLeave.
        Assert.DoesNotContain("MouseEnter +=", player);
        Assert.DoesNotContain("MouseLeave +=", player);

        var style = Regex.Match(Src("src", "Toki", "Theme", "Theme.xaml"),
            @"<Style x:Key=""EvidencePlay"".*?</Style>", RegexOptions.Singleline);
        Assert.True(style.Success, "the EvidencePlay style is missing");
        var xaml = style.Value;
        Assert.Contains("Background=\"Transparent\"", xaml);
        Assert.DoesNotContain("GlassRim", xaml);   // bare: no rim
        // Hover brightens (130ms in, 90ms back to the resting 0.5) and a press compresses the box -
        // the same feel grammar the rest of Toki uses, just as opacity instead of an overlay.
        Assert.Contains("To=\"1\" Duration=\"0:0:0.13\"", xaml);
        Assert.Contains("To=\"0.5\" Duration=\"0:0:0.09\"", xaml);
        Assert.Contains("To=\"0.82\"", xaml);
    }

    // ── 6. the chat-history delete cross ──

    [Fact]
    public void The_chat_history_delete_is_the_house_cross_path()
    {
        var lib = CodeOnly(Src("src", "Toki", "LibraryWindow.xaml.cs"));
        Assert.DoesNotContain("✕", lib);
        Assert.Contains("Views.PillParts.Outline(Views.PillParts.Cross(), \"TextDim\")", lib);
    }

    // ── 7. the send disc's glyph sits on the disc's centre ──

    [Fact]
    public void The_send_glyph_is_geometry_so_it_needs_no_nudge()
    {
        // SUPERSEDES The_send_glyph_is_nudged_off_its_box_centre (2026-09-27). That test pinned a
        // 1px TranslateTransform correcting the font glyph's uneven side bearings, and its reasoning
        // was right about the cause: a glyph is centred INCLUDING whitespace that differs left and
        // right, and a margin cannot fix it because a centre-aligned ContentPresenter just re-centres
        // the widened box. What it got wrong is that a hand-measured constant was the answer. The
        // owner still read the button as off-centre afterwards.
        //
        // A Path has no bearings: its bounding box IS the drawn shape, so Stretch=Uniform in a fixed
        // box centres it exactly at every DPI with nothing to drift. So the invariant is no longer
        // "the nudge is present", it is "there is nothing here that would NEED a nudge" - same
        // reasoning the play triangle and the back chevron already follow.
        var xaml = Src("src", "Toki", "LibraryWindow.xaml");
        var send = xaml[xaml.IndexOf("x:Name=\"AskBtn\"", StringComparison.Ordinal)..];
        send = send[..send.IndexOf("</Button>", StringComparison.Ordinal)];
        Assert.Contains("<Path", send);
        Assert.Contains("Stretch=\"Uniform\"", send);
        Assert.DoesNotContain("&#xE724;", send);              // no font glyph
        Assert.DoesNotContain("TranslateTransform", send);    // and therefore no correction for one
    }

    // ── 8. the Library list never colours a row by its id ──

    [Fact]
    public void The_library_list_never_colours_a_row_by_id()
    {
        var lib = CodeOnly(Src("src", "Toki", "LibraryWindow.xaml.cs"));
        // HISTORY, kept because it is the reason this test exists. The list once spread six accents
        // across the rows BY ID, so the colour meant nothing and five of the design system's six
        // accents were spent on noise. It was then reduced to two colours coding the note's KIND
        // (blue = recorded meeting, purple = manual note), guarded by asserting that call site.
        //
        // SUPERSEDED 2026-09-03 by the owner-approved card design: the rail is gone entirely,
        // because the meta line already says "Note" or the app's name in words, so the bar was a
        // second and dimmer copy of a fact already on the card. `CardAccentKey` went with it rather
        // than linger as a helper nothing calls.
        //
        // What survives is the rule that outlived both designs: a row's colour may never be a
        // function of its id. Decorative rotation is how the first version happened.
        // ONE ASSERTION THAT IS ACTUALLY TRUE. A first attempt also tried to prove no colour is
        // chosen by id, by excluding the legitimate uses of `m.Id` and searching for the rest. It
        // failed instantly on `App.Store.Get(m.Id)`: a row's id is used constantly for lookups and
        // hit-target tags, so a substring search cannot tell those from a colour lookup. A clever
        // check that misfires is worse than a narrow one that holds, so this guards the named
        // palette the original decorative rotation needed.
        Assert.DoesNotContain("CardAccents", lib);
    }
}
