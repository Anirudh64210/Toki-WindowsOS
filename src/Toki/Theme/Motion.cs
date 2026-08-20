using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Toki.Theme;

/// <summary>
/// Central motion tokens. The rule: arriving/expanding motion decelerates (ease-OUT), leaving/
/// collapsing motion accelerates away (ease-IN), ease-in-out only for in-place transforms.
/// Character animation (TokiBlob) is deliberately exempt; only functional UI motion lives here.
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

    // ── easings (new per call - WPF easing functions are cheap and immutable in use) ──
    /// <summary>Arriving / expanding motion - decelerates to a gentle stop.</summary>
    public static IEasingFunction EaseOut => new CubicEase { EasingMode = EasingMode.EaseOut };
    /// <summary>Leaving / collapsing motion - accelerates away.</summary>
    public static IEasingFunction EaseIn => new CubicEase { EasingMode = EasingMode.EaseIn };
    /// <summary>Only for an element transforming in place (rare); prefer EaseOut for reveals.</summary>
    public static IEasingFunction EaseInOut => new CubicEase { EasingMode = EasingMode.EaseInOut };

    // Strong bezier curves (WPF's stock CubicEase is too weak for overlay-class motion).
    // Drawer is the iOS sheet / Dynamic Island settle - the curve the notch morph rides.
    /// <summary>Strong ease-out for entering elements - fast start, soft landing.</summary>
    public static IEasingFunction ArriveStrong => new BezierEase(0.23, 1.00, 0.32, 1.00);
    /// <summary>Accelerating exit - things leave without ceremony.</summary>
    public static IEasingFunction Depart => new BezierEase(0.30, 0.00, 0.80, 0.15);
    /// <summary>The sheet/notch settle curve (Dynamic Island's) - use for the window size morph.</summary>
    public static IEasingFunction Drawer => new BezierEase(0.32, 0.72, 0.00, 1.00);
    /// <summary>Something already on screen transforming IN PLACE (a glyph turning over, a row
    /// sliding across) - eased at both ends, because it neither arrives nor leaves.</summary>
    public static IEasingFunction Move => new BezierEase(0.77, 0.00, 0.175, 1.00);

    /// <summary>The standard "arrive" translate distance for entering content (opacity 0→1 + rise).</summary>
    public const double EnterRise = 10;
}

/// <summary>A cubic-bezier easing (CSS semantics: fixed endpoints 0,0 and 1,1, two control points) so
/// the researched curves port verbatim. WPF has KeySpline but only inside keyframe animations; this
/// makes the same math available as an IEasingFunction for plain DoubleAnimations.</summary>
public sealed class BezierEase : EasingFunctionBase
{
    readonly double _x1, _y1, _x2, _y2;
    public BezierEase(double x1, double y1, double x2, double y2)
    {
        _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2;
        // The bezier encodes the WHOLE curve (CSS semantics); EasingMode.EaseIn is the raw
        // passthrough to EaseInCore (the default EaseOut would mirror it).
        EasingMode = EasingMode.EaseIn;
    }
    protected override Freezable CreateInstanceCore() => new BezierEase(_x1, _y1, _x2, _y2);
    protected override double EaseInCore(double t)
    {
        // Solve x(u)=t for u (the curve is monotonic in x), then return y(u). A few Newton steps with a
        // bisection fallback is the standard implementation (what browsers do).
        double u = t;
        for (int i = 0; i < 8; i++)
        {
            double x = Bez(u, _x1, _x2) - t;
            if (Math.Abs(x) < 1e-5) break;
            double dx = Deriv(u, _x1, _x2);
            if (Math.Abs(dx) < 1e-6) break;
            u -= x / dx;
            u = Math.Clamp(u, 0, 1);
        }
        return Bez(u, _y1, _y2);
    }
    static double Bez(double u, double p1, double p2)
    {
        double v = 1 - u;
        return 3 * v * v * u * p1 + 3 * v * u * u * p2 + u * u * u;
    }
    static double Deriv(double u, double p1, double p2)
    {
        double v = 1 - u;
        return 3 * v * v * p1 + 6 * v * u * (p2 - p1) + 3 * u * u * (1 - p2);
    }
}
