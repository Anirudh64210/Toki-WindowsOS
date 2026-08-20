using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using Toki.Audio;

namespace Toki.Controls;

/// <summary>
/// THE waveform, shared by the recording bar and the Ask notch; the only per-site knobs are
/// <see cref="BarCount"/> and Height. Driven by a real <see cref="LevelMeter"/> so silence looks
/// like silence - the bars must never pretend to hear an empty room. Bars dance in place against a
/// fixed centre-weighted envelope. Motion rules: one 30Hz timer for the row, ScaleY only, nothing
/// allocated per tick, no Effect anywhere near it; the timer stops dead when hidden or not live.
/// </summary>
public sealed class VoiceWave : UserControl
{
    // Geometry. Identical at both sites by design - only the count and the height differ.
    const double BarW = 3, Gap = 3, Radius = 999;

    // Where the bars sit when nobody is talking: dots, not a flat line and not a fake dance.
    const double RestScale = 0.15;

    const int TickMs = 33;                      // ~30Hz, one timer for the whole row

    // Exponential approach per tick. Fast up, slow down - the asymmetry is the whole feel: a voice
    // starts instantly and a room goes quiet gradually, and a symmetric meter reads as mush.
    const double KAttack = 0.55;                // ~125ms to within 5% of a jump
    // ~650ms fall, still 4x slower than the rise. Guarded by --wavetest: the row must settle
    // back to its resting dots soon after the room goes quiet.
    const double KDecay = 0.14;

    const double WobbleDepth = 0.08, WobbleSec = 1.2;   // held vowels shimmer instead of freezing
    const double BreathDepth = 0.03, BreathSec = 3.5;   // the resting dots breathe, like the mascot

    // Headroom for the wobble: without it the tallest bar grows OUT of its row - nothing clips
    // it, it just quietly overlaps the transcript.
    const double Headroom = 1 / (1 + WobbleDepth);

    readonly List<System.Windows.Media.ScaleTransform> _scales = new();
    readonly LevelScale _scale = new();
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };

    double[] _profile = Array.Empty<double>();
    double[] _phase = Array.Empty<double>();
    double[] _cur = Array.Empty<double>();

    DispatcherTimer? _timer;
    long _t0 = Environment.TickCount64;
    int _barCount = 14;
    bool _live;

    public VoiceWave()
    {
        IsHitTestVisible = false;
        // Crisp edges at 3px: without snapping, sub-pixel bar positions smear each bar across two
        // device pixels and the row reads blurry.
        UseLayoutRounding = false;
        SnapsToDevicePixels = true;
        Height = 20;
        Content = _row;
        Build();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Start(); else Stop(); };
    }

    /// <summary>Bars in the row. The recording bar uses 14, Ask uses 9.</summary>
    public int BarCount
    {
        get => _barCount;
        set
        {
            var n = Math.Max(1, value);
            if (n == _barCount) return;
            _barCount = n;
            Build();
        }
    }

    /// <summary>Where the level comes from. Null means no live source: the row rests as dots.</summary>
    public LevelMeter? Meter { get; set; }

    /// <summary>True while this site is listening. False parks the row and stops the timer.</summary>
    public bool IsLive
    {
        get => _live;
        set
        {
            if (_live == value) return;
            _live = value;
            if (value) Start(); else Stop();
        }
    }

    /// <summary>Self-test seam (--wavetest): the bars' CURRENT heights, read off the live control -
    /// "does the timer actually move the bars" is a question only the running control can answer.</summary>
    internal double[] AuditScales()
    {
        var v = new double[_scales.Count];
        for (var i = 0; i < _scales.Count; i++) v[i] = _scales[i].ScaleY;
        return v;
    }

    /// <summary>Self-test seam: the resting height every bar parks at.</summary>
    internal static double AuditRest => RestScale;

    void Build()
    {
        _row.Children.Clear();
        _scales.Clear();
        _profile = WaveProfile.Build(_barCount);
        _phase = new double[_barCount];
        _cur = new double[_barCount];
        for (var i = 0; i < _barCount; i++)
        {
            // An irrational-ish spread, not a straight ramp: a linear phase reads as a marquee.
            _phase[i] = i * 0.37 % 1.0;
            _cur[i] = RestScale;
            var scale = new System.Windows.Media.ScaleTransform(1, RestScale);
            _scales.Add(scale);
            var bar = new Rectangle
            {
                Width = BarW,
                RadiusX = Radius,
                RadiusY = Radius,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(i == 0 ? 0 : Gap, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
            };
            // Resource reference, never a captured brush: a live theme switch must repaint the bars.
            bar.SetResourceReference(Shape.FillProperty, "TextHi");
            _row.Children.Add(bar);
        }
        Width = _barCount * BarW + (_barCount - 1) * Gap;
    }

    void Start()
    {
        if (_timer is not null || !IsVisible || !_live) return;
        // Headless (the render harness) shows the resting row; no timer.
        if (Toki.App.Headless) return;
        _t0 = Environment.TickCount64;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += Tick;
        _timer.Start();
    }

    void Stop()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= Tick;
            _timer = null;
        }
        for (var i = 0; i < _scales.Count; i++)
        {
            _cur[i] = RestScale;
            _scales[i].ScaleY = RestScale;
        }
    }

    void Tick(object? sender, EventArgs e)
    {
        var t = (Environment.TickCount64 - _t0) / 1000.0;
        var level = _scale.Push(Meter?.Level ?? 0f);
        var rest = RestScale * (1 + BreathDepth * Math.Sin(2 * Math.PI * t / BreathSec));
        for (var i = 0; i < _cur.Length; i++)
        {
            var wobble = 1 + WobbleDepth * Math.Sin(2 * Math.PI * (t / WobbleSec + _phase[i]));
            // Never BELOW the resting dots: a bar that collapses to nothing reads as broken, and the
            // dots are the honest "listening, nobody is talking" state.
            var target = Math.Max(rest, level * _profile[i] * wobble * Headroom);
            _cur[i] += (target - _cur[i]) * (target > _cur[i] ? KAttack : KDecay);
            _scales[i].ScaleY = _cur[i];
        }
    }
}

/// <summary>
/// The per-bar ceiling: a cosine dome with a small fixed jitter, computed once per bar count -
/// the envelope is the design, the level only scales it. The jitter hashes the bar index rather
/// than using seeded Random, whose sequence is implementation-defined across .NET versions.
/// </summary>
internal static class WaveProfile
{
    internal const double EndFactor = 0.45;
    internal const double Jitter = 0.12;

    internal static double[] Build(int bars)
    {
        var p = new double[bars];
        var half = (bars - 1) / 2.0;
        var max = 0.0;
        for (var i = 0; i < bars; i++)
        {
            var x = half <= 0 ? 0 : (i - half) / half;                    // -1 at the ends, 0 at the centre
            var dome = EndFactor + (1 - EndFactor) * Math.Cos(x * Math.PI / 2);
            p[i] = dome * (1 + (Hash(i) * 2 - 1) * Jitter);
            if (p[i] > max) max = p[i];
        }
        // Normalised so the tallest bar is exactly 1: the control's Height is then the real height of
        // the row, and the jitter cannot push a bar out of its own box.
        for (var i = 0; i < bars; i++) p[i] /= max;
        return p;
    }

    // xorshift-ish integer hash, 0..1. Deterministic on every runtime, forever.
    internal static double Hash(int i)
    {
        var h = unchecked((uint)i * 2654435761u + 0x9E3779B9u);
        h ^= h >> 15;
        h = unchecked(h * 2246822519u);
        h ^= h >> 13;
        return (h % 10000u) / 10000.0;
    }
}

/// <summary>
/// Turns a raw RMS into a 0..1 bar height: display = sqrt(clamp((rms - noise) / (ref - noise), 0, 1)).
/// Both reference points track recent history because mic levels vary by tens of dB between setups;
/// sqrt because loudness is perceived roughly logarithmically. <see cref="HardFloor"/> and
/// <see cref="MinSpan"/> keep silence honest: a quiet room must stay at level 0, resting dots.
/// </summary>
internal sealed class LevelScale
{
    /// <summary>Below this nothing is speech, whatever the recent history says. About -54dBFS.</summary>
    internal const float HardFloor = 0.002f;
    /// <summary>The smallest gap between noise and reference. Without it a silent ring normalises its
    /// own hiss to full height, which is the fake dancing this control exists to stop.</summary>
    internal const float MinSpan = 0.02f;
    /// <summary>~3s of history at 30Hz - long enough to hold a sentence and the pauses inside it.</summary>
    internal const int Window = 90;

    readonly float[] _ring = new float[Window];
    readonly float[] _sorted = new float[Window];
    // The history starts FULL of silence: an empty ring would normalise the first loud buffer
    // against itself and read it as the noise floor, flattening the row's opening second.
    int _count = Window;
    int _next;

    /// <summary>The quiet end of recent history (10th percentile), floored at <see cref="HardFloor"/>.</summary>
    internal float Noise { get; private set; } = HardFloor;
    /// <summary>The loud end of recent history (95th percentile), at least MinSpan above the noise.</summary>
    internal float Reference { get; private set; } = HardFloor + MinSpan;

    internal float Push(float rms)
    {
        _ring[_next] = rms;
        _next = (_next + 1) % Window;
        // One sort per tick over a reused scratch buffer: 90 floats at 30Hz, and nothing allocated.
        Array.Copy(_ring, _sorted, _count);
        Array.Sort(_sorted, 0, _count);
        Noise = Math.Max(HardFloor, At(0.10));
        Reference = Math.Max(Noise + MinSpan, At(0.95));
        return Curve(rms, Noise, Reference);
    }

    float At(double q) => _sorted[(int)Math.Floor(q * (_count - 1))];

    internal static float Curve(float rms, float noise, float reference)
    {
        var span = Math.Max(reference - noise, MinSpan);
        return (float)Math.Sqrt(Math.Clamp((rms - noise) / span, 0f, 1f));
    }
}
