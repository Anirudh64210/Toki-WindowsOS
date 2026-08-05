using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Toki.Theme;

/// <summary>
/// Central MOTION TOKENS — the one place that defines how fast things move and how they ease, so the whole
/// app reads as a single motion system instead of a pile of scattered literals (from docs/ui-revamp-
/// research.md §1/§3e, the Glass / Material / Fluent / Carbon consensus).
///
/// The rule that makes it feel premium: **arriving / expanding motion decelerates (ease-OUT)**; leaving /
/// collapsing motion accelerates away (ease-IN); ease-in-out only for something transforming in place.
/// Durations sit in the researched sweet spots (micro-feedback ~100–150ms, component transitions
/// ~150–250ms, container/view transitions ~250–400ms; nothing over ~350ms reads as sluggish).
///
/// Character/mascot animation (TokiBlob) is deliberately EXEMPT — personality motion may be springy or
/// longer; only functional UI motion is standardised here.
/// </summary>
public static class Motion
{
    // ── durations (ms) ──
    public const int Instant = 90;    // pressed states, hover fills, toggles
    public const int Micro   = 150;   // chips, badges, the jump-to-latest pill, icon swaps
    public const int Enter   = 180;   // a new message / card / list item arriving
    public const int Exit    = 130;   // dismissals, an outgoing view fading
    public const int Morph   = 280;   // the notch growing/shrinking between states (bounds + clip)
    public const int Grand   = 340;   // first open, onboarding steps, the prep-card drop

    public static Duration Dur(int ms) => new(TimeSpan.FromMilliseconds(ms));

    // ── easings (new per call — WPF easing functions are cheap and immutable in use) ──
    /// <summary>Arriving / expanding motion — decelerates to a gentle stop.</summary>
    public static IEasingFunction EaseOut => new CubicEase { EasingMode = EasingMode.EaseOut };
    /// <summary>Leaving / collapsing motion — accelerates away.</summary>
    public static IEasingFunction EaseIn => new CubicEase { EasingMode = EasingMode.EaseIn };
    /// <summary>Only for an element transforming in place (rare); prefer EaseOut for reveals.</summary>
    public static IEasingFunction EaseInOut => new CubicEase { EasingMode = EasingMode.EaseInOut };

    /// <summary>The standard "arrive" translate distance for entering content (opacity 0→1 + rise).</summary>
    public const double EnterRise = 10;
}
