using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// A morph must be able to be interrupted.
///
/// Apple's motion guidance puts this first, and it is the difference between an interface that feels
/// alive and one that feels scripted: every animation starts from the PRESENTATION value - what is
/// actually on screen right now - never from the value a fresh animation would start at. Clicking
/// twice, or a meeting arriving while a panel is opening, must continue from where the card is rather
/// than snapping back to a starting pose and replaying.
///
/// These are source assertions rather than behavioural ones because the thing under test is a WPF
/// animation's From value mid-flight, which needs a live composition clock. They are still mutation
/// probes: each one fails against the code as it stood on 2026-09-03 before interruptibility landed.
/// </summary>
public sealed class InterruptibleMorphTests
{
    static string RepoRoot()
    {
        var d = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Toki.sln"))) d = d.Parent;
        return d?.FullName ?? throw new System.InvalidOperationException("repo root not found");
    }

    static string Pill() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "Toki", "PillWindow.xaml.cs"));

    /// <summary>The clip has always read its live Rect; this pins it so a future edit cannot quietly
    /// go back to _cardW/_cardH, which already claim the DESTINATION while a morph is running.</summary>
    [Fact]
    public void The_clip_starts_from_the_live_rect_not_the_target()
    {
        Assert.Matches(@"var live = \(Root\.Clip as RectangleGeometry\)\?\.Rect;", Pill());
    }

    /// <summary>MUTATION PROBE: the zoom used to be `new DoubleAnimation(startScale, 1, ...)` with an
    /// unconditional From, so interrupting a morph snapped the content back to the new morph's
    /// starting scale and re-zoomed from there.</summary>
    [Fact]
    public void The_zoom_starts_from_the_live_scale_when_a_morph_is_already_running()
    {
        var code = Pill();
        Assert.Matches(@"var liveScale = HostScale\.ScaleX;", code);
        Assert.Matches(@"var inFlight = Math\.Abs\(liveScale - 1\) > 0\.0+1;", code);
        Assert.Matches(@"inFlight \? liveScale : startScale", code);
    }

    /// <summary>MUTATION PROBE: the outgoing snapshot used to render `Host.Children[0]`, i.e. only the
    /// incoming page. Interrupting mid-transition then threw away the half-faded old page that was
    /// still on screen. Rendering the host captures what the user is actually looking at.</summary>
    [Fact]
    public void The_outgoing_snapshot_captures_the_whole_host()
    {
        var code = Pill();
        Assert.Contains("FrameworkElement el = Host;", code);
        Assert.DoesNotContain("if (Host.Children[0] is not FrameworkElement el)", code);
    }

    /// <summary>The settle must ride the animation's own completion. It used to be a DispatcherTimer at
    /// ms + 20 - a second clock racing the first, which drifted under load and detached the window
    /// resize from the end of the motion.</summary>
    [Fact]
    public void The_window_settle_is_the_animations_completion_not_a_timer()
    {
        var code = Pill();
        Assert.Matches(@"PinContentForMorph\(unionCardW, unionCardH, w, h, ms, \(\) =>", code);
        // The morph must not reintroduce its own settle timer.
        // Comments stripped first: this file DOCUMENTS the timer it replaced, and a test that reads
        // prose as code fails on its own explanation.
        var codeOnly = Regex.Replace(code, @"//[^\n]*", " ");
        var animateSize = Regex.Match(codeOnly, @"void AnimateSize\(double w, double h, int ms\).*?\n    \}",
            RegexOptions.Singleline);
        Assert.True(animateSize.Success, "AnimateSize not found");
        Assert.DoesNotContain("DispatcherTimer", animateSize.Value);
    }

    /// <summary>Input is never blocked while a transition plays. A morph that swallows clicks is not
    /// interruptible no matter how its values are computed.</summary>
    [Fact]
    public void Nothing_disables_hit_testing_for_the_duration_of_a_morph()
    {
        var code = Pill();
        Assert.DoesNotContain("Root.IsHitTestVisible = false", code);
        Assert.DoesNotContain("IsEnabled = false", code);
    }
}
