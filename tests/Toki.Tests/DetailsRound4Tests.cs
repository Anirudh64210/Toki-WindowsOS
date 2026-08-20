using System;
using System.IO;
using System.Text.RegularExpressions;
using Toki;
using Toki.State;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// The owner's fourth round (2026-08-19, after the film round): the notch's top corner still
/// flickered on the summon (a stale layered surface presented by DWM before the reveal's first
/// frame), and the notes-ready announce ended in a plain swap instead of the character exit the
/// owner asked for ("toki jumps out of the bubble, and flips and goes to the top of the screen").
///
/// Fail-first: the behavioural pins here were watched to fail via targeted mutations of the fixed
/// code (the hand-off pair removed, the timer returned to TryGo, the conceal returned to hiding in
/// its Completed) - one combined cycle, results in the build report. The transition-order pin
/// (measure before mutate) guards the reorder that was verified by frame capture; reverting that
/// reorder wholesale for a fail-first run was judged riskier than the value of the proof, so that
/// one is a regression pin, not a fail-first-proven test - stated here honestly.
/// </summary>
public sealed class DetailsRound4Tests
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

    // ── 1. the notes-ready hand-off lands as a hover pair ──

    [Fact]
    public void The_notes_ready_handoff_lands_as_a_hover_pair()
    {
        // In-place would snap the flying zone away in one frame; the pair gives the landing its
        // cross-fade (zone fades out over the peek's own Toki fading in on the same spot).
        Assert.True(PillWindow.IsHoverPair(PillState.Notes, PillState.Collapsed));
        // And the hide is still a conceal, never a morph pair.
        Assert.False(PillWindow.IsHoverPair(PillState.Notes, PillState.Hidden));
    }

    // ── 2. the announce timer starts the flight, not a bare state flip ──

    [Fact]
    public void The_announce_dwell_ends_in_the_flight_not_a_swap()
    {
        var code = CodeOnly(Src("src", "Toki", "PillWindow.xaml.cs"));
        // The timer's tick hands over to the character beat.
        var tick = Regex.Match(code, @"_notesPeek\.Tick\s*\+=.*?\};", RegexOptions.Singleline);
        Assert.True(tick.Success, "the notes-peek timer's tick was not found");
        Assert.Contains("StartNotesHandoffFlight", tick.Value);
        Assert.DoesNotContain("TryGo(PillState.Collapsed)", tick.Value);
    }

    [Fact]
    public void The_flight_is_character_motion_with_a_full_somersault()
    {
        var code = CodeOnly(Src("src", "Toki", "PillWindow.xaml.cs"));
        var flight = Regex.Match(code, @"void StartNotesHandoffFlight\(\).*?\n    \}", RegexOptions.Singleline);
        Assert.True(flight.Success, "StartNotesHandoffFlight not found");
        // One full turn (the owner's "flips"), the shrink to the peek's 28px, and the landing flip
        // of state - all present.
        Assert.Contains("-360", flight.Value);
        Assert.Contains("28.0 / 44.0", flight.Value);
        Assert.Contains("TryGo(PillState.Collapsed)", flight.Value);
        // Transforms only: the flight must never call layout-affecting sizing on the zone.
        Assert.DoesNotContain("MascotZone.Width", flight.Value);
        Assert.DoesNotContain("MascotZone.Margin", flight.Value);
    }

    // ── 3. the conceal composes an empty frame BEFORE it hides ──

    [Fact]
    public void The_conceal_hides_only_after_an_empty_frame_has_been_presented()
    {
        var code = CodeOnly(Src("src", "Toki", "PillWindow.xaml.cs"));
        var conceal = Regex.Match(code, @"void ConcealUp\(.*?\n    \}", RegexOptions.Singleline);
        Assert.True(conceal.Success, "ConcealUp not found");
        // The Completed handler must NOT call done() directly - it pins the parked values and
        // waits on CompositionTarget.Rendering (the render thread's own heartbeat; a dispatcher
        // hop is not enough, the stub was filmed surviving one).
        Assert.Contains("CompositionTarget.Rendering", conceal.Value);
        var directHide = Regex.IsMatch(conceal.Value,
            @"anim\.Completed\s*\+=\s*\(_, _\)\s*=>\s*\{\s*if \(gen != _clipGen\) return;\s*done\(\);");
        Assert.False(directHide, "ConcealUp hides synchronously in Completed - the layered surface retains a card stub");
        // The audited flag is set on the deferred path (the --revealtest check reads it live).
        Assert.Contains("_parkedEmptyFrame = true", conceal.Value);
    }

    // ── 4. the transition measures before it mutates anything visible ──

    [Fact]
    public void The_transition_measures_content_before_touching_the_screen()
    {
        // Regression pin for the reorder (verified by frame capture): ContentHeight's synchronous
        // stall must run while the OLD state still owns every pixel - before the Host swap and
        // before the surface swap. If someone moves the measure back below the mutations, this
        // catches it.
        var code = CodeOnly(Src("src", "Toki", "PillWindow.xaml.cs"));
        var transition = Regex.Match(code, @"void OnStateChanged\(PillState old, PillState next\).*?\n    \}",
            RegexOptions.Singleline);
        Assert.True(transition.Success, "OnStateChanged not found");
        var measure = transition.Value.IndexOf("ContentHeight(next", StringComparison.Ordinal);
        var hostSwap = transition.Value.IndexOf("Host.Children.Clear()", StringComparison.Ordinal);
        var surface = transition.Value.IndexOf("SetSurface(peek)", StringComparison.Ordinal);
        Assert.True(measure > 0 && hostSwap > 0 && surface > 0, "expected landmarks missing");
        Assert.True(measure < hostSwap, "the content measure runs after the old content is cleared");
        Assert.True(measure < surface, "the content measure runs after the new surface is painted");
    }

    // ── 5. the flight parks what it borrowed ──

    [Fact]
    public void The_flight_park_restores_the_disc_and_its_shadow()
    {
        var code = CodeOnly(Src("src", "Toki", "PillWindow.xaml.cs"));
        var park = Regex.Match(code, @"void ParkFlightTransforms\(bool restoreScale\).*?\n    \}",
            RegexOptions.Singleline);
        Assert.True(park.Success, "ParkFlightTransforms not found");
        // The dissolve hides both for the flight (a held opacity can be silently replaced; a
        // Visibility cannot) - so the park must put Visibility back, or the next announce has no
        // bubble to jump out of.
        Assert.Contains("MascotDisc.Visibility = Visibility.Visible", park.Value);
        Assert.Contains("MascotLift.Visibility = Visibility.Visible", park.Value);
        // And the pop scale belongs to the entrance arm on a landed flight - only aborts touch it.
        var scaleRestore = park.Value.IndexOf("MascotPop.ScaleX", StringComparison.Ordinal);
        var guard = park.Value.IndexOf("if (restoreScale)", StringComparison.Ordinal);
        Assert.True(guard > 0 && scaleRestore > guard, "the pop-scale restore is not guarded by restoreScale");
    }

    // ── 6. the landing hides the card's material with the card ──

    [Fact]
    public void The_handoff_landing_hides_the_material_stack_with_the_card()
    {
        // The material is a CHILD of Root, not its Background; the landing branch bare-ifies the
        // card ahead of the deferred SetSurface and must hide the material too, or a grainy card
        // ghost stands at the landing spot for the deferral window (filmed).
        var code = CodeOnly(Src("src", "Toki", "PillWindow.xaml.cs"));
        var landing = Regex.Match(code,
            @"if \(old == PillState\.Notes && next == PillState\.Collapsed\).*?ScheduleSurface\(next, peek, 240\);",
            RegexOptions.Singleline);
        Assert.True(landing.Success, "the hand-off landing branch was not found");
        Assert.Contains("Material.Visibility = Visibility.Collapsed", landing.Value);
        Assert.Contains("_cardBare = true", landing.Value);
    }
}
