using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Toki;
using Toki.State;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// The five behaviour bugs the owner could see and a green suite could not (2026-08-19): a second Toki
/// popping on collapse, the first expand starting halfway through, the transcribing loader sitting a
/// card-gap too low and never minimising, the peeking Toki's eyes sliced off by the card edge, and a
/// flip that read as a shudder.
///
/// Every check here was written against the BROKEN code first and watched to fail, then the fix went
/// in. Where a fact lives in a number in a file (an animation duration, a Viewbox margin) the number
/// in the file is what gets asserted - this suite has no Application and no theme dictionary, so it
/// cannot build a WPF view. The live versions of the same facts are the app's own harnesses
/// (--revealtest, --measuretest) and a burst capture of the running notch.
/// </summary>
public sealed class NotchBehaviourTests
{
    static readonly string Root = FindRoot();
    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Toki.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
    static string Src(params string[] parts) => File.ReadAllText(Path.Combine(Root, Path.Combine(parts)));
    static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>The file with its COMMENTS taken out - every check below is about what the app DOES,
    /// and the prose around each one names the very value being banned.</summary>
    static string CodeOnly(string text)
    {
        text = Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);   // XAML
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^\n]*", " ");
    }

    // ── 1. the idle timer must never die under the cursor ──

    [Fact]
    public void A_timer_that_fires_while_you_hover_re_arms_itself()
    {
        // THE BUG: one timer, stopped on every tick. The hovering branch declined to tuck and returned
        // WITHOUT restarting it, so a cursor resting on the page at the instant it fired killed idle
        // tucking for the rest of that state. Ordinary pages hid it because the hover poll re-arms on
        // every poll; the transcribing loader is not a hover pair, so nothing re-armed it and it just
        // stayed open. Anything other than ReArm here is the bug back.
        Assert.Equal(PillWindow.IdleAction.ReArm,
            PillWindow.IdleDecision(PillState.Summarizing, tucked: false, typing: false, hovering: true));
        Assert.Equal(PillWindow.IdleAction.ReArm,
            PillWindow.IdleDecision(PillState.Home, tucked: false, typing: false, hovering: true));
    }

    [Fact]
    public void Typing_re_arms_and_a_page_left_alone_tucks()
    {
        // The two branches either side of the fix, so it cannot be "fixed" by making everything re-arm.
        Assert.Equal(PillWindow.IdleAction.ReArm,
            PillWindow.IdleDecision(PillState.Home, tucked: false, typing: true, hovering: false));
        Assert.Equal(PillWindow.IdleAction.Tuck,
            PillWindow.IdleDecision(PillState.Home, tucked: false, typing: false, hovering: false));
        Assert.Equal(PillWindow.IdleAction.Tuck,
            PillWindow.IdleDecision(PillState.Summarizing, tucked: false, typing: false, hovering: false));
        // Already a sliver, or a state that never tucks at all: the timer is allowed to stay dead.
        Assert.Equal(PillWindow.IdleAction.Stand,
            PillWindow.IdleDecision(PillState.Home, tucked: true, typing: false, hovering: false));
        Assert.Equal(PillWindow.IdleAction.Stand,
            PillWindow.IdleDecision(PillState.RecordingBar, tucked: false, typing: false, hovering: false));
    }

    [Fact]
    public void The_loader_re_tucks_on_the_short_result_beat()
    {
        // The loader is a STATUS, not a page to read: once you stop hovering it goes back to its sliver
        // on the 2s result beat, not after the 9s a deliberate page gets.
        Assert.Equal(PillWindow.IdleIntervalMs(PillState.Notes), PillWindow.IdleIntervalMs(PillState.Summarizing));
        Assert.Equal(2000, PillWindow.IdleIntervalMs(PillState.Summarizing));
        Assert.Equal(9000, PillWindow.IdleIntervalMs(PillState.Home));
    }

    // ── 2. the loader hangs at the same height as the recording it follows ──

    [Fact]
    public void The_transcribing_loader_is_flush_like_the_recording_bar()
    {
        // Summarizing is the same session as the bar it replaces. On the default 12px hover the strip
        // dropped a whole card-gap the moment the meeting ended, which read as the notch sagging.
        Assert.Equal(PillWindow.TopGapFor(PillState.RecordingBar), PillWindow.TopGapFor(PillState.Summarizing));
        Assert.Equal(2, PillWindow.TopGapFor(PillState.Summarizing));
        // And nothing else moved: a panel still hovers, the summon peek is still flush with the edge.
        Assert.Equal(12, PillWindow.TopGapFor(PillState.Home));
        Assert.Equal(0, PillWindow.TopGapFor(PillState.Collapsed));
    }

    // ── 3. the peeking Toki's eyes fit inside the peek ──

    [Fact]
    public void The_recording_peeks_eyes_land_inside_the_card()
    {
        var blob = Src("src", "Toki", "Controls", "TokiBlob.xaml");
        var board = Regex.Match(blob, @"<Grid x:Name=""Root"" Width=""(?<w>[\d.]+)"" Height=""(?<h>[\d.]+)""");
        Assert.True(board.Success, "TokiBlob's board size is not where this test expects it");
        var boardW = Num(board.Groups["w"].Value);
        var boardH = Num(board.Groups["h"].Value);

        // The eye's real extent, read off the art: the figure starts at its top and the second bezier
        // ends at its bottom. Not a comment, and not a number copied into this file.
        var top = Regex.Match(blob, @"x:Name=""LEyeFig""[^>]*StartPoint=""[\d.]+,(?<y>[\d.]+)""");
        var bottom = Regex.Match(blob, @"x:Name=""LEyeS2""[^>]*Point3=""[\d.]+,(?<y>[\d.]+)""");
        Assert.True(top.Success && bottom.Success, "the eye path is not where this test expects it");
        var eyeTop = Num(top.Groups["y"].Value);
        var eyeBottom = Num(bottom.Groups["y"].Value);

        var bar = Src("src", "Toki", "Views", "RecordingBarView.xaml");
        var box = Regex.Match(bar,
            @"x:Name=""TokiBox"" Width=""(?<w>[\d.]+)""[^>]*?Margin=""0,(?<m>-?[\d.]+),0,0""",
            RegexOptions.Singleline);
        Assert.True(box.Success, "the peek Viewbox's width and margin are not where this test expects them");
        var viewW = Num(box.Groups["w"].Value);
        var marginTop = Num(box.Groups["m"].Value);

        // The Viewbox scales the whole board uniformly to its width; the peek Toki is then rotated 180
        // about the box's own centre, so the eye row - low on an upright face - ends up near the TOP.
        var scale = viewW / boardW;
        var viewH = boardH * scale;
        var eyeTopFlipped = viewH - eyeBottom * scale + marginTop;
        var eyeBottomFlipped = viewH - eyeTop * scale + marginTop;

        var cardH = PillWindow.DesignCardSize(PillState.RecordingBar).H;
        // The card sets ClipToBounds, so anything above y=0 is not "peeking", it is GONE. At 30 wide
        // with a -13 top margin the eye row ran from -2.93 to +3.90: the top three pixels of both eyes
        // were cut off and what was left read as two dashes.
        Assert.True(eyeTopFlipped > 0,
            $"the peek's eyes start at y {eyeTopFlipped:F2} - above the card, so the card clips them");
        Assert.True(eyeBottomFlipped < cardH,
            $"the peek's eyes end at y {eyeBottomFlipped:F2}, past the {cardH}px card");
        // And a whole eye is visible, not a sliver of one: the flipped row is ~6px of a 14px card.
        Assert.True(eyeBottomFlipped - eyeTopFlipped > 5,
            $"only {eyeBottomFlipped - eyeTopFlipped:F2}px of eye is showing");
    }

    // ── 4. the flip is one turn, not a shudder ──

    [Fact]
    public void The_peeks_flip_is_one_eased_turn_with_a_single_settle()
    {
        var code = CodeOnly(Src("src", "Toki", "Views", "RecordingBarView.xaml.cs"));

        // A bounce is read by its biggest hop. On a 27px character falling 12px, two hops at
        // Bounciness 2.6 are two or three pixels of jitter - and the flip's own BackEase overshot the
        // angle past 180 at the same time, so the turn wobbled while the body twitched.
        Assert.DoesNotContain("BounceEase", code);

        var flip = Regex.Match(code,
            @"RotateTransform\.AngleProperty,\s*new DoubleAnimation\(0, 180, TimeSpan\.FromMilliseconds\((?<ms>\d+)\)\)\s*\{\s*EasingFunction = (?<ease>[\w.]+)");
        Assert.True(flip.Success, "the flip animation is not where this test expects it");
        Assert.Equal(520, int.Parse(flip.Groups["ms"].Value));
        // A glyph turning over is transforming IN PLACE - it neither arrives nor leaves - so it eases
        // at both ends (motion skill §2).
        Assert.Equal("Theme.Motion.Move", flip.Groups["ease"].Value);

        var fall = Regex.Match(code,
            @"TranslateTransform\.YProperty,\s*new DoubleAnimation\(-12, 0, TimeSpan\.FromMilliseconds\((?<ms>\d+)\)\)\s*\{\s*BeginTime = TimeSpan\.FromMilliseconds\((?<begin>\d+)\),\s*EasingFunction = new BackEase \{ EasingMode = EasingMode\.EaseOut, Amplitude = (?<amp>[\d.]+) \}");
        Assert.True(fall.Success, "the fall animation is not where this test expects it");
        var fallMs = int.Parse(fall.Groups["ms"].Value);
        var beginMs = int.Parse(fall.Groups["begin"].Value);
        Assert.Equal(0.5, Num(fall.Groups["amp"].Value));
        // The turn LEADS and the drop follows it, and the whole flow lands together at ~540ms.
        Assert.True(beginMs is > 0 and <= 80, $"the fall starts at {beginMs}ms - it should trail the turn");
        Assert.Equal(540, beginMs + fallMs);

        // A delayed animation shows the element's BASE value until its clock starts, so the base has to
        // BE the From or the delay is a preview of the ending (the armed-entrance lesson).
        Assert.Contains("Fall.Y = -12;", code);
    }

    // ── 5. the collapse takes Toki with it, and the first open is paid for in advance ──

    [Fact]
    public void Collapsing_into_a_peek_fades_the_bubble_Toki_out()
    {
        var pill = CodeOnly(Src("src", "Toki", "PillWindow.xaml.cs"));
        // The card's disappearance is deferred 240ms so it shrinks away with the panel - but the mascot
        // zone is a SIBLING of the card, so it stayed at full opacity while the peek's own little Toki
        // was already cross-fading in underneath. Two Tokis, then the disc vanished in one frame.
        var deferred = Regex.Match(pill,
            @"if \(deferPeekBg\)\s*\{(?<body>.*?)ScheduleSurface\(next, peek, collapseMs\);",
            RegexOptions.Singleline);
        Assert.True(deferred.Success, "the deferred-peek-surface branch is not where this test expects it");
        var body = deferred.Groups["body"].Value;
        Assert.Contains("MascotZone.BeginAnimation(OpacityProperty", body);
        Assert.Contains("Theme.Motion.Depart", body);   // he leaves on the same curve the retract uses

        // And the deferred SetSurface re-parks the entrance, so the NEXT panel still gets his spring
        // instead of inheriting a zone a finished fade is holding at zero.
        var sched = Regex.Match(pill, @"void ScheduleSurface\([^)]*\)\s*\{(?<body>.*?)\n    \}",
            RegexOptions.Singleline);
        Assert.True(sched.Success, "ScheduleSurface is not where this test expects it");
        Assert.Contains("ArmMascotReveal();", sched.Groups["body"].Value);
    }

    [Fact]
    public void The_first_open_is_warmed_at_launch_not_inside_the_animation()
    {
        var pill = Src("src", "Toki", "PillWindow.xaml.cs");
        // WPF animation clocks are wall-clock: 50-150ms of building HomeView and rasterising the
        // mascot INSIDE the first morph means the first frame that renders is already a third of the
        // way through it. The head of the animation is never drawn.
        Assert.Contains("internal void WarmFirstOpen()", pill);
        var warm = Regex.Match(pill, @"internal void WarmFirstOpen\(\)\s*\{(?<body>.*?)\n    \}",
            RegexOptions.Singleline);
        Assert.True(warm.Success, "WarmFirstOpen is not where this test expects it");
        var body = warm.Groups["body"].Value;
        Assert.Contains("if (App.Headless) return;", body);          // never under a self-test
        Assert.Contains("ViewFor(s)", body);
        Assert.Contains("new Controls.TokiBlob()", body);
        Assert.Contains("PillState.Collapsed, PillState.Home", body);
        Assert.DoesNotContain("Show()", body);                        // shows nothing, steals no focus
        Assert.DoesNotContain("Activate()", body);

        // On an idle frame after startup, alongside the Library's warm - never competing with the
        // first summon.
        var app = CodeOnly(Src("src", "Toki", "App.xaml.cs"));
        Assert.Matches(@"_pill\?\.WarmFirstOpen\(\)\),\s*System\.Windows\.Threading\.DispatcherPriority\.ApplicationIdle",
            app);
    }
}
