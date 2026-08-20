using System.Windows;
using System.Windows.Media;

namespace Toki.Theme;

/// <summary>
/// The card's corner as a continuous curve (Figma's exact squircle parameterisation; smoothing 0
/// degrades to a plain rounded rectangle). At r24/0.6 it departs from a circular corner by under
/// half a pixel - it changes the corner's character, not its silhouette; screenshots won't show it.
/// Geometry is built once and FROZEN, for resting cards only: the notch's clip animations must
/// keep their RectangleGeometry, never rebuild a squircle per frame.
/// </summary>
public static class Squircle
{
    /// <summary>Figma's "60% corner smoothing", the setting that reproduces iOS geometry.</summary>
    public const double DefaultSmoothing = 0.6;

    /// <summary>
    /// The same shape as a stroked OUTLINE. A pen straddles the path, so the geometry is inset by
    /// half the stroke and its radius reduced to match: stroked at <paramref name="strokeThickness"/>
    /// it then covers exactly the band a Border of that thickness would paint, inside the same box.
    /// </summary>
    public static Geometry Outline(Rect box, double radius, double strokeThickness,
                                   double smoothing = DefaultSmoothing)
    {
        var half = strokeThickness / 2;
        var inner = new Rect(box.X + half, box.Y + half,
                             Math.Max(0, box.Width - strokeThickness), Math.Max(0, box.Height - strokeThickness));
        return Build(inner, radius - half, smoothing);
    }

    /// <summary>
    /// The shape itself, frozen. <paramref name="radius"/> and <paramref name="smoothing"/> both
    /// degrade gracefully on a box too small to hold them (see the clamps below), so a sliver-height
    /// card gets a sane capsule instead of self-intersecting corners.
    /// </summary>
    public static Geometry Build(Rect box, double radius, double smoothing = DefaultSmoothing)
    {
        if (box.Width <= 0 || box.Height <= 0) return Geometry.Empty;

        // CSS border-radius rule: a corner may never claim more than half the shorter side, or the
        // two corners on one edge overlap. The notch's sliver states (13px tall) hit this.
        var budget = Math.Min(box.Width, box.Height) / 2;
        var r = Math.Max(0, Math.Min(radius, budget));
        var s = Math.Max(0, Math.Min(smoothing, 1));
        // Smoothing costs edge length (the curve starts (1 + s) * r from the corner); trade
        // smoothing away rather than radius so the silhouette stays what the caller asked for.
        if (r > 0) s = Math.Min(s, Math.Max(0, budget / r - 1));

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            double x0 = box.Left, y0 = box.Top, x1 = box.Right, y1 = box.Bottom;

            if (r <= 0)
            {
                ctx.BeginFigure(new Point(x0, y0), true, true);
                ctx.LineTo(new Point(x1, y0), true, false);
                ctx.LineTo(new Point(x1, y1), true, false);
                ctx.LineTo(new Point(x0, y1), true, false);
            }
            else
            {
                // figma-squircle's figure 11.1: p is how far from the corner the straight edge ends;
                // a, b, c and d place the two control points and the arc's entry point.
                var p = (1 + s) * r;
                var arcMeasure = 90 * (1 - s);
                var arcLen = Math.Sin(Rad(arcMeasure / 2)) * r * Math.Sqrt(2);
                var alpha = (90 - arcMeasure) / 2;
                var beta = 45 * s;
                var cc = r * Math.Tan(Rad(alpha / 2)) * Math.Cos(Rad(beta));
                var d = cc * Math.Tan(Rad(beta));
                var b = (p - arcLen - cc - d) / 3;
                var a = 2 * b;

                // Each corner is the same construction in its own frame: u points along the incoming
                // edge INTO the corner, v along the outgoing edge away from it.
                void Corner(Point k, Vector u, Vector v)
                {
                    var entry = k - (p - a - b - cc) * u + d * v;
                    if (s > 0)
                        ctx.BezierTo(k - (p - a) * u, k - (p - a - b) * u, entry, true, true);
                    var exit = entry + arcLen * (u + v);
                    ctx.ArcTo(exit, new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                    if (s > 0)
                        ctx.BezierTo(exit + d * u + cc * v, exit + d * u + (b + cc) * v,
                                     exit + d * u + (a + b + cc) * v, true, true);
                }

                Vector right = new(1, 0), down = new(0, 1), left = new(-1, 0), up = new(0, -1);
                ctx.BeginFigure(new Point(x1 - p, y0), true, true);
                Corner(new Point(x1, y0), right, down);
                ctx.LineTo(new Point(x1, y1 - p), true, false);
                Corner(new Point(x1, y1), down, left);
                ctx.LineTo(new Point(x0 + p, y1), true, false);
                Corner(new Point(x0, y1), left, up);
                ctx.LineTo(new Point(x0, y0 + p), true, false);
                Corner(new Point(x0, y0), up, right);
            }
        }
        g.Freeze();   // frozen: a resting shape, shared and never mutated
        return g;
    }

    static double Rad(double deg) => deg * Math.PI / 180;
}
