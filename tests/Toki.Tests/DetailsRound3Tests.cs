using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Toki;
using Toki.State;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// The owner's third round of detail bugs (2026-08-19, after the first two fix waves): the white
/// flicker before the mascot on every summon, the loader that opened on a graze and would not get
/// out of the way, the note page bursting open unbidden when notes land, the chevron leaning left
/// in its puck, and the speaker initial riding the top of its disc.
///
/// Every check here was written against the BROKEN code first and watched to fail (one combined
/// revert cycle), then the fix went in. This suite has no Application and no theme dictionary, so
/// facts that live in view construction are asserted as source shape; the live versions are the
/// app's own harnesses and the burst captures in the build report.
/// </summary>
public sealed class DetailsRound3Tests
{
    static readonly string Root = FindRoot();
    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Toki.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
    static string Src(params string[] parts) => File.ReadAllText(Path.Combine(Root, Path.Combine(parts)));
    static string CodeOnly(string text)
    {
        text = Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^\n]*", " ");
    }

    // ── 1. the peek BLOOMS into whatever it opens - never the in-place snap ──

    [Fact]
    public void The_peek_morphs_into_every_state_it_can_open()
    {
        // THE BUG: Collapsed → MeetingPrompt fell through to the in-place path, which snaps the
        // window to full size and fades the content at full size over a surface that has not
        // unfurled - the prompt's white pill ghosted in alone before the mascot (the owner filmed
        // one frame of floating capsules). Only the stayable pages morphed.
        Assert.True(PillWindow.IsHoverPair(PillState.Collapsed, PillState.MeetingPrompt));
        Assert.True(PillWindow.IsHoverPair(PillState.Collapsed, PillState.Home));
        Assert.True(PillWindow.IsHoverPair(PillState.Collapsed, PillState.PrepCard));
        // Hiding is a conceal, not a bloom - it must NOT become a morph pair.
        Assert.False(PillWindow.IsHoverPair(PillState.Collapsed, PillState.Hidden));
    }

    [Fact]
    public void The_loaders_sliver_morphs_into_its_result()
    {
        // Summarizing (a 13px sliver) snapping a full note page open was the same defect at the
        // other end of the pipeline.
        Assert.True(PillWindow.IsHoverPair(PillState.Summarizing, PillState.Notes));
        Assert.True(PillWindow.IsHoverPair(PillState.Summarizing, PillState.NoSpeech));
        Assert.False(PillWindow.IsHoverPair(PillState.Summarizing, PillState.Home));
    }

    // ── 2. a view refresh must never size the box for a transition that has not run ──

    [Fact]
    public void Grow_to_content_is_gated_on_the_processed_state()
    {
        // THE BUG: AppState notifies subscribers in order; App's view refreshes ran BEFORE the
        // window's OnStateChanged, and a refresh-triggered GrowToContent morphed the box to the
        // NEXT state's size while surface and content were still the old state's. The owner's log:
        // "morph 52x42 -> 344x242 (150ms)" BEFORE "transition Collapsed->Home", which left the real
        // transition a no-op and the plate to snap in full-size.
        var pill = CodeOnly(Src("src", "Toki", "PillWindow.xaml.cs"));
        var grow = Regex.Match(pill, @"public void GrowToContent\(\).*?finally", RegexOptions.Singleline);
        Assert.True(grow.Success, "GrowToContent not found");
        Assert.Contains("if (s != _shownState) return;", grow.Value);
        // And the gate is actually maintained: OnStateChanged stamps the state it processed.
        Assert.Contains("_shownState = next;", pill);
    }

    // ── 3. the loader opens on a click only and leaves when you do ──

    [Fact]
    public void The_loader_sliver_opens_on_click_only_and_tucks_on_leave()
    {
        // Owner, 2026-08-19: even a short hover DWELL dropped the loader / "Notes ready" pill
        // open over their work - the sliver sits exactly where a cursor crosses to reach browser
        // tabs. Opening is a CLICK (like the recording peek); the leave-grace still tucks the
        // open loader promptly once you look away.
        var pill = CodeOnly(Src("src", "Toki", "PillWindow.xaml.cs"));
        // Hover must NOT open the tucked loader or notes pill...
        Assert.Matches(@"cur is not \(PillState\.Summarizing or PillState\.Notes\)\) ExpandCurrent", pill);
        // ...a window-level click does (the sliver has no button of its own):
        Assert.Matches(@"_tucked && _state\.Current is PillState\.Summarizing or PillState\.Notes\) ExpandCurrent", pill);
        var grace = double.Parse(Regex.Match(pill, @"LoaderTuckGraceMs = (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(grace, 300, 900);    // prompt, but never edge-flap
        // The leave-driven tuck lives in the hover poll, not the idle timer's 2s beat.
        Assert.Contains("_loaderOffMs", pill);
        Assert.Matches(@"_loaderOffMs >= LoaderTuckGraceMs", pill);
    }

    // ── 4. notes ANNOUNCE; they do not take the screen ──

    [Fact]
    public void A_finished_note_announces_and_then_yields_to_the_peek()
    {
        var pill = CodeOnly(Src("src", "Toki", "PillWindow.xaml.cs"));
        // Entering Notes from the loader must go through the announcement, not the full page:
        Assert.Matches(@"announce = next == PillState\.Notes && old == PillState\.Summarizing", pill);
        Assert.Matches(@"announce \? Views\.NotesReadyView\.CardHeight", pill);
        // The dwell is one named constant (the owner asked for 2 seconds; it is tight on purpose):
        Assert.Matches(@"NotesReadyPeekMs = 2000", pill);
        // Ignored → the ambient peek, so the screen is handed back but the note stays a hover away.
        // Since the fourth round the yield is Toki's exit FLIGHT (the owner: "toki jumps out of the
        // bubble, and flips and goes to the top of the screen"); the timer starts the flight, and
        // the flight's landing performs the TryGo(Collapsed).
        var peekTick = Regex.Match(pill, @"void ArmNotesPeek\(\).*?_notesPeek\.Start\(\);\s*\}", RegexOptions.Singleline);
        Assert.True(peekTick.Success, "ArmNotesPeek not found");
        Assert.Contains("StartNotesHandoffFlight()", peekTick.Value);
        var flight = Regex.Match(pill, @"void StartNotesHandoffFlight\(\).*?\n    \}", RegexOptions.Singleline);
        Assert.True(flight.Success, "StartNotesHandoffFlight not found");
        Assert.Contains("TryGo(PillState.Collapsed)", flight.Value);
        // Never yank the pill from under the pointer - a hovered announcement re-arms instead:
        Assert.Contains("CursorOverPill()", peekTick.Value);
        // And that collapse is legal in the state machine, not a ForceForDemo backdoor:
        var state = new AppState();
        state.ForceForDemo(PillState.Notes);
        Assert.True(state.TryGo(PillState.Collapsed));
    }

    // ── 4b. a sliver STAYS a sliver at rest ──

    [Fact]
    public void The_corner_settle_cannot_unclip_a_sliver()
    {
        // THE BUG (a squircle-wave regression): ApplyCornerGeometry builds the resting clip from
        // Root's ARRANGED box, and for a sliver Root sits on the window's bubble-floor (~50px)
        // while the card asked for 13 - so the settle quietly un-clipped every tucked sliver back
        // to a full card. The loader "didn't minimise" no matter what the timers did. The clamp:
        // more than a pixel of difference means the card is deliberately smaller, and the card wins.
        var pill = CodeOnly(Src("src", "Toki", "PillWindow.xaml.cs"));
        var geom = Regex.Match(pill, @"void ApplyCornerGeometry\(\).*?RimPath\.Visibility = Visibility\.Visible;", RegexOptions.Singleline);
        Assert.True(geom.Success, "ApplyCornerGeometry not found");
        Assert.Contains("rh - _cardH > 1.0", geom.Value);
        Assert.Contains("rw - _cardW > 1.0", geom.Value);
    }

    [Fact]
    public void The_tucked_loader_hides_the_bubble_too()
    {
        // Toki's bubble is a SIBLING of the card, outside its clip - a tucked loader that leaves
        // him (and his say-bubble) standing is not "just the top line" (owner's words).
        var pill = CodeOnly(Src("src", "Toki", "PillWindow.xaml.cs"));
        Assert.Matches(@"next == PillState\.Summarizing && _tucked\)\s*\{\s*MascotZone\.BeginAnimation\(OpacityProperty, null\);\s*MascotZone\.Opacity = 0;", pill);
        // And he comes back when the loader opens.
        var expand = Regex.Match(pill, @"void ExpandCurrent\(\).*?ArmIdle\(\);", RegexOptions.Singleline);
        Assert.True(expand.Success, "ExpandCurrent not found");
        Assert.Contains("MascotZone.BeginAnimation", expand.Value);
    }

    // ── 5. outline glyphs centre sub-pixel, so the chevron cannot lean ──

    [Fact]
    public void Outline_glyphs_opt_out_of_layout_rounding()
    {
        // THE BUG: an 11px chevron in a 26px puck leaves 7.5px margins = 9.375 device px at 125%;
        // layout rounding splits them 9/10 and the glyph leans left (the owner saw it twice).
        // The filled glyphs already carried SubPixel; Outline did not.
        var parts = CodeOnly(Src("src", "Toki", "Views", "PillParts.cs"));
        var outline = Regex.Match(parts, @"public static Path Outline\(.*?SubPixel\(\);", RegexOptions.Singleline);
        Assert.True(outline.Success, "Outline() no longer applies SubPixel()");
    }

    // ── 6. the initial disc's letter is immune to inherited line metrics ──

    [Fact]
    public void Disc_labels_pin_their_own_line_metrics()
    {
        // THE BUG: Paragraph.LineHeight is INHERITED, so the transcript's 19.5px line height
        // reached the TextBlock inside the avatar's 16px disc and inflated its line box - the "Y"
        // rode the top of the circle with its cap clipped (the owner photographed it). Pinning
        // LineHeight to the font size with BlockLineHeight makes the label context-immune;
        // measured on an 8x render, the ink centre moved from 1.4px-from-top to centred.
        var parts = CodeOnly(Src("src", "Toki", "Views", "PillParts.cs"));
        var disc = Regex.Match(parts, @"public static TextBlock DiscLabel\(.*?\.SubPixel\(\);", RegexOptions.Singleline);
        Assert.True(disc.Success, "DiscLabel not found");
        Assert.Contains("LineHeight = fontSize", disc.Value);
        Assert.Contains("LineStackingStrategy = LineStackingStrategy.BlockLineHeight", disc.Value);
        // Both disc sites use it - a bare TextBlock in either is the bug back.
        var avatar = Regex.Match(CodeOnly(Src("src", "Toki", "Views", "NoteRenderer.cs")),
            @"static Border Avatar\(.*?\n    \}", RegexOptions.Singleline);
        Assert.True(avatar.Success, "NoteRenderer.Avatar not found");
        Assert.Contains("PillParts.DiscLabel(", avatar.Value);
        Assert.DoesNotContain("new TextBlock", avatar.Value);
        var speakerDisc = Regex.Match(CodeOnly(Src("src", "Toki", "Views", "RecordingOpenView.xaml.cs")),
            @"static FrameworkElement SpeakerDisc\(.*?\n    \}", RegexOptions.Singleline);
        Assert.True(speakerDisc.Success, "SpeakerDisc not found");
        Assert.Contains("PillParts.DiscLabel(", speakerDisc.Value);
        Assert.DoesNotContain("new TextBlock", speakerDisc.Value);
    }
}
