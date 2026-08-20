using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Toki.Controls;

/// <summary>
/// The Toki character - a soft gradient-shaded pink cloud with two eyes that breathes
/// continuously and blinks at natural, slightly irregular intervals. Self-animating: any
/// view that drops a &lt;TokiBlob/&gt; in gets a living character with no extra wiring.
/// It also has a vocabulary of reactions: <see cref="React"/> cycles fun one-off animations
/// (squish / hop / wobble / wink / spin / big-eyes / nod) and <see cref="DropIn"/> is the
/// springy "Hey Toki" hello (drop + double-blink).
/// </summary>
public partial class TokiBlob : UserControl
{
    static readonly Random Rng = new();
    readonly DispatcherTimer _blinkTimer = new();
    readonly DispatcherTimer _fidgetTimer = new();
    readonly DispatcherTimer _saccadeTimer = new();
    readonly DispatcherTimer _watchTimer = new();
    DispatcherTimer? _resume;

    bool _running;
    int _reactKind;
    int _fidgetKind;

    public TokiBlob()
    {
        InitializeComponent();
        ApplyThemeColours();
        Theme.ThemeManager.ThemeChanged += ApplyThemeColours;
        Unloaded += (_, _) => Theme.ThemeManager.ThemeChanged -= ApplyThemeColours;
        _blinkTimer.Tick += OnBlinkTick;
        _fidgetTimer.Tick += OnFidgetTick;
        _saccadeTimer.Tick += OnSaccadeTick;
        _watchTimer.Tick += OnWatchTick;
        _watchTimer.Interval = TimeSpan.FromMilliseconds(66);   // ~15Hz: smooth enough for an eye, cheap
        // Only animate while actually on screen - cached-but-hidden views must not burn CPU.
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Run(); else Pause(); };
        Unloaded += (_, _) => Pause();
    }

    /// <summary>
    /// Toki is drawn for a DARK notch; the near-white feet, crown wash and pale gold sparkles vanish
    /// on a light bubble, so the colours that touch white get a light-theme value. The saturated
    /// middle never changes - the character keeps its edges, not a new identity.
    /// </summary>
    void ApplyThemeColours()
    {
        var light = Theme.ThemeManager.Current == Theme.AppTheme.Light;
        BodyFoot.Color = C(light ? "#4FC0FF" : "#F0FAF9");
        Crown0.Color = C(light ? "#59C7F0FC" : "#DBC7F0FC");
        Crown1.Color = C(light ? "#3DC7F0FC" : "#B0C7F0FC");
        Crown2.Color = C(light ? "#14C7F0FC" : "#33C7F0FC");
        // The sparkle brush lives in a resource dictionary (x:Name gives no field - reach it by
        // key); it is never frozen, so its stops can be recoloured.
        if (Sparkles.Resources["Gold"] is LinearGradientBrush gold && gold.GradientStops.Count >= 2)
        {
            gold.GradientStops[0].Color = C(light ? "#FFC93C" : "#FDE685");
            gold.GradientStops[1].Color = C(light ? "#F59300" : "#FCC942");
        }
    }

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    void Run()
    {
        if (_running) return;
        _running = true;
        StartBreathing();
        ScheduleBlink();
        ScheduleFidget();
        ScheduleSaccade();
        _watchTimer.Start();
        // Pause() stops the mood's looping body motion; coming back on screen must restore it.
        EnterMood(_mood);
    }

    void OnBlinkTick(object? sender, EventArgs e)
    {
        _blinkTimer.Stop();
        if (!_running) return;
        Blink(doubleBlink: Rng.Next(4) == 0);
        ScheduleBlink();
    }

    // The life layer, four layers from constant to rare:
    //   1. WATCH   - the eyes follow the pointer whenever it is near. Always on.
    //   2. SACCADE - when nothing is near, the eyes dart to a new point every 0.9-3.4s.
    //   3. DRIFT   - the whole body floats and leans on a long slow cycle, never truly still.
    //   4. FIDGET  - the bigger vocabulary (nod, wobble, double-blink), every 9-20s.
    // Priority runs downward: the pointer beats a saccade, and a mood's own direction is the base
    // both of them offset FROM (see ApplyGaze) rather than something they fight with.

    // How far the eyes can travel, in units of the 67x52 art board. An eye is 7.05 wide, so 2.6
    // is a third of an eye - a real look, not a twitch.
    const double GazeMaxX = 2.6;
    const double GazeMaxY = 1.8;
    // Beyond this many device pixels the pointer is not "near" and the eyes go back to their own life.
    const double WatchRadius = 460;

    double _gazeBaseX, _gazeBaseY;   // where the MOOD points the eyes
    double _gazeOffX, _gazeOffY;     // where the pointer or a saccade has taken them from there
    bool _watching;                  // the pointer currently owns the eyes

    /// <summary>
    /// Follow the pointer. Polled rather than hooked: the notch is a never-focused window and the
    /// mascot is not hit-testable, so there is no MouseMove to subscribe to.
    /// </summary>
    void OnWatchTick(object? sender, EventArgs e)
    {
        if (!_running || !IsVisible || ActualWidth <= 0) return;
        if (PresentationSource.FromVisual(this) is null) return;   // offscreen harness: no screen space
        if (!GetCursorPos(out var p)) return;

        Point me;
        try { me = PointToScreen(new Point(ActualWidth / 2, ActualHeight * 0.45)); }
        catch { return; }   // torn down between the check above and here

        var dx = p.X - me.X;
        var dy = p.Y - me.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist > WatchRadius)
        {
            // Out of range. Hand the eyes back to their own life, once, rather than every tick.
            if (!_watching) return;
            _watching = false;
            _gazeOffX = _gazeOffY = 0;
            ApplyGaze(420);
            ScheduleSaccade();
            return;
        }

        _watching = true;
        // Normalised by the radius, not the distance: dividing by distance would peg the eyes at
        // maximum the moment the pointer left dead centre.
        var wantX = Math.Clamp(dx / WatchRadius * 2.4, -1, 1) * GazeMaxX;
        var wantY = Math.Clamp(dy / WatchRadius * 2.4, -1, 1) * GazeMaxY;
        // Only re-aim when the target actually moved - a still pointer must not restart animations 15x/s.
        if (Math.Abs(wantX - _gazeOffX) < 0.12 && Math.Abs(wantY - _gazeOffY) < 0.12) return;
        _gazeOffX = wantX;
        _gazeOffY = wantY;
        // Restarted every tick the pointer moves, so this is a chase rate, not a duration.
        ApplyGaze(220);
    }

    // ── saccades: the eyes' own life when nobody is near ──
    void OnSaccadeTick(object? sender, EventArgs e)
    {
        _saccadeTimer.Stop();
        if (!_running) return;
        if (_watching) { ScheduleSaccade(); return; }

        // Two in five land back at centre: eyes leave, look, and come back - a mascot that only
        // ever darts outward looks shifty rather than curious.
        if (Rng.Next(5) < 2) { _gazeOffX = 0; _gazeOffY = 0; }
        else
        {
            var angle = Rng.NextDouble() * Math.PI * 2;
            var reach = 0.45 + Rng.NextDouble() * 0.55;
            _gazeOffX = Math.Cos(angle) * GazeMaxX * reach;
            _gazeOffY = Math.Sin(angle) * GazeMaxY * reach * 0.7;   // eyes travel less vertically
        }
        // 110ms: the smallest move that reads as a dart rather than a drift; a real 30-80ms
        // saccade teleports on a 44px mascot and reads as a glitch.
        ApplyGaze(110);
        // One in six darts comes with a blink, which is what actually happens when you look away.
        if (Rng.Next(6) == 0 && _mood != TokiMood.Happy) Blink(doubleBlink: false);
        ScheduleSaccade();
    }

    void ScheduleSaccade()
    {
        _saccadeTimer.Stop();
        _saccadeTimer.Interval = TimeSpan.FromMilliseconds(900 + Rng.Next(2500));
        _saccadeTimer.Start();
    }

    /// <summary>Move the eyes to the mood's direction plus whatever the pointer or a saccade has added.</summary>
    void ApplyGaze(int ms)
    {
        var d = TimeSpan.FromMilliseconds(ms);
        var ease = Theme.Motion.ArriveStrong;
        Gaze.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(Math.Clamp(_gazeBaseX + _gazeOffX, -GazeMaxX - 1.6, GazeMaxX + 1.6), d)
            { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        Gaze.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(Math.Clamp(_gazeBaseY + _gazeOffY, -GazeMaxY - 1.6, GazeMaxY + 1.6), d)
            { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
    }

    // Fidgets: the bigger, rarer autonomous motions, layered over idle. Never during a working
    // mood - a wobble mid-scribble reads as a glitch, not charm.
    void OnFidgetTick(object? sender, EventArgs e)
    {
        _fidgetTimer.Stop();
        if (!_running) return;
        if (_mood == TokiMood.Idle)
            switch (_fidgetKind++ % 5)
            {
                case 0: LookAround(); break;
                case 1: Nod(); break;
                case 2: Wobble(); break;
                case 3: Perk(); break;
                default: Blink(doubleBlink: true); break;
            }
        ScheduleFidget();
    }

    void ScheduleFidget()
    {
        _fidgetTimer.Interval = TimeSpan.FromMilliseconds(9_000 + Rng.Next(11_000));
        _fidgetTimer.Start();
    }

    /// <summary>A deliberate sweep: look one way, hold, look the other, hold, come back. Longer and
    /// more considered than a saccade - this is Toki taking in the room rather than glancing.</summary>
    void LookAround()
    {
        if (_watching) return;
        var dir = Rng.Next(2) == 0 ? -1 : 1;
        _gazeOffX = dir * GazeMaxX; _gazeOffY = -0.6; ApplyGaze(280);
        Chain(900, () => { if (_watching) return; _gazeOffX = -dir * GazeMaxX * 0.85; ApplyGaze(320); });
        Chain(1900, () => { if (_watching) return; _gazeOffX = 0; _gazeOffY = 0; ApplyGaze(360); });
    }

    /// <summary>Ears-up: a quick small rise and a widen, as if something just caught its attention.</summary>
    void Perk()
    {
        Hop.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-2.4, 0, TimeSpan.FromMilliseconds(520)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 } });
        foreach (var eye in new[] { LeftBlink, RightBlink })
            eye.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.16, TimeSpan.FromMilliseconds(180)) { AutoReverse = true, EasingFunction = Theme.Motion.EaseOut });
    }

    /// <summary>Run something after a delay, and drop it silently if the mascot went away meanwhile.</summary>
    void Chain(int ms, Action act)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); if (_running) act(); };
        t.Start();
    }

    void Pause()
    {
        _running = false;
        _watching = false;
        _blinkTimer.Stop();
        _fidgetTimer.Stop();
        _saccadeTimer.Stop();
        _watchTimer.Stop();
        _resume?.Stop();
        _gazeOffX = _gazeOffY = 0;
        Gaze.BeginAnimation(TranslateTransform.XProperty, null); Gaze.X = 0;
        Gaze.BeginAnimation(TranslateTransform.YProperty, null); Gaze.Y = 0;
        // The mood channel loops FOREVER by design (the sway, the writing dip), so an off-screen or
        // unloaded mascot would keep it running for as long as the app lives if this did not stop it.
        // The mood itself is not forgotten - Run() puts it back through EnterMood.
        MoodBob.BeginAnimation(TranslateTransform.YProperty, null); MoodBob.Y = 0;
        MoodLean.BeginAnimation(RotateTransform.AngleProperty, null); MoodLean.Angle = 0;
        Breathe.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Breathe.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Hop.BeginAnimation(TranslateTransform.YProperty, null); Hop.Y = 0;
        Tilt.BeginAnimation(RotateTransform.AngleProperty, null); Tilt.Angle = 0;
        LeftBlink.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        LeftBlink.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RightBlink.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RightBlink.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    void StartBreathing()
    {
        // ~3.5s full cycle at <=2% amplitude (relaxed breathing: 15-20 breaths/min, tiny travel).
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var y = new DoubleAnimation(1.0, 0.982, TimeSpan.FromMilliseconds(1750))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease,
        };
        var x = new DoubleAnimation(1.0, 1.014, TimeSpan.FromMilliseconds(1750))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease,
        };
        Breathe.BeginAnimation(ScaleTransform.ScaleYProperty, y);
        Breathe.BeginAnimation(ScaleTransform.ScaleXProperty, x);
    }

    // ── Moods (the redesign's Characters row): what Toki is DOING, shown on the mascot itself ──

    /// <summary>
    /// The design's five characters (Assets - Characters, 325:278): plain, thinking (a bubble of
    /// dots), reading (a note in hand - the live-transcription face), writing (the pen), and happy.
    /// </summary>
    public enum TokiMood { Idle, Thinking, Reading, Writing, Happy }

    TokiMood _mood = TokiMood.Idle;

    // Mood transitions never hard-cut: props MORPH (grow out of and shrink back into the body),
    // and the BODY carries the mood too - lean, dip, bounce, eyes on whatever it is doing.
    const int MoodIn = 260;
    const int MoodOut = 170;   // exits at ~70% of the enter, per the motion rules

    /// <summary>
    /// Put Toki in a mood. The notch wires these to its states so the character narrates what the
    /// app is doing without text. Idempotent, and safe to flap: a mood interrupted mid-transition
    /// hands over cleanly.
    /// </summary>
    public void SetMood(TokiMood mood)
    {
        if (_mood == mood) return;
        var leaving = _mood;
        _mood = mood;
        ExitMood(leaving);
        EnterMood(mood);
    }

    void ExitMood(TokiMood leaving)
    {
        switch (leaving)
        {
            case TokiMood.Thinking:
                // The looping dot pulse stops NOW; only the disappearance is deferred.
                foreach (var d in new[] { Dot1, Dot2, Dot3 }) { d.BeginAnimation(OpacityProperty, null); d.Opacity = 1; }
                Shrink(ThinkBubble, ThinkPop, 0.35);
                HideAfter(ThinkBubble, TokiMood.Thinking, MoodOut);
                break;
            case TokiMood.Reading:
                foreach (var d in new[] { NoteDot1, NoteDot2 }) { d.BeginAnimation(OpacityProperty, null); d.Opacity = 1; }
                Shrink(NoteCard, NotePop, 0.35);
                HideAfter(NoteCard, TokiMood.Reading, MoodOut);
                break;
            case TokiMood.Writing:
                PenTilt.BeginAnimation(RotateTransform.AngleProperty, null);
                Shrink(Pen, PenPop, 0.25);
                HideAfter(Pen, TokiMood.Writing, MoodOut);
                break;
            case TokiMood.Happy:
                foreach (var (s, pop, spin) in Sparks())
                {
                    s.BeginAnimation(OpacityProperty, null); s.Opacity = 1;
                    pop.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    pop.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    spin.BeginAnimation(RotateTransform.AngleProperty, null); spin.Angle = 0;
                }
                Shrink(Sparkles, SparkPop, 0.55);
                HideAfter(Sparkles, TokiMood.Happy, MoodOut);
                // Bend back out of the smile.
                MorphEyes(EyeOpen, 200);
                LeftBlink.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                RightBlink.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                LeftBlink.ScaleY = RightBlink.ScaleY = 1;
                if (_running) { _blinkTimer.Stop(); ScheduleBlink(); }
                break;
        }
    }

    void EnterMood(TokiMood mood)
    {
        switch (mood)
        {
            case TokiMood.Idle:
                LookAt(0, 0);
                // Idle is NOT still: a long slow float and lean (~1% of the board, 4-6s periods)
                // read as something suspended in air rather than a sticker.
                Sway(0.9, 5600);
                Bob(-0.65, 4300);
                break;

            case TokiMood.Thinking:
                Grow(ThinkBubble, ThinkPop);
                // The dots pulse in order - the universal "working on it".
                var dots = new[] { Dot1, Dot2, Dot3 };
                for (var i = 0; i < dots.Length; i++)
                {
                    // Parked at the animation's own From: a delayed animation shows the BASE value
                    // until its clock begins. Check this wherever BeginTime appears.
                    dots[i].Opacity = 0.25;
                    dots[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(420))
                    {
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                        BeginTime = TimeSpan.FromMilliseconds(i * 160),
                    });
                }
                // Thinking is an upward posture: eyes toward the bubble, body lifted, slow sway.
                LookAt(1.3, -1.5);
                Bob(-1.0);
                Sway(2.2, 2600);
                break;

            case TokiMood.Reading:
                // The note in hand, for live transcription: its two dots pulse in turn and Toki
                // looks down at it, so it reads as his rather than a decoration.
                Grow(NoteCard, NotePop);
                for (var i = 0; i < 2; i++)
                    (i == 0 ? NoteDot1 : NoteDot2).BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0.30, 1, TimeSpan.FromMilliseconds(720))
                        {
                            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                            BeginTime = TimeSpan.FromMilliseconds(i * 360),
                            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                        });
                NoteDot1.Opacity = 0.30; NoteDot2.Opacity = 0.30;
                LookAt(1.3, 1.6);
                Bob(0.7, 900);
                Sway(0, hold: -1.4);
                break;

            case TokiMood.Writing:
                Grow(Pen, PenPop);
                // A small wrist-scribble pivoting at the NIB, and deliberately IRREGULAR: a 2.2s
                // phrase of uneven strokes with rests, because a perfect sine loop reads as a metronome.
                PenTilt.BeginAnimation(RotateTransform.AngleProperty, Scribble());
                // The body writes too, on the SAME phrase - sharing it makes one motion, not two.
                LookAt(1.3, 1.6);
                MoodBob.BeginAnimation(TranslateTransform.YProperty, ScribbleBob());
                Sway(0, hold: -2.0);
                break;

            case TokiMood.Happy:
                Grow(Sparkles, SparkPop);
                // Each sparkle twinkles, scales and turns on its own clock: four lights out of step
                // read as magic, a canvas fading in unison reads as one flashing lamp.
                var i2 = 0;
                foreach (var (s, pop, spin) in Sparks())
                {
                    var delay = TimeSpan.FromMilliseconds(i2 * 230);
                    var dur = TimeSpan.FromMilliseconds(620 + i2 * 110);
                    var turn = i2 % 2 == 0 ? 26 : -22;   // alternating, so they never look geared together
                    i2++;
                    // Parked at each animation's From, so a staggered clock never previews the base value.
                    s.Opacity = 0.18;
                    pop.ScaleX = pop.ScaleY = 0.5;
                    spin.Angle = -turn / 2.0;
                    s.BeginAnimation(OpacityProperty, new DoubleAnimation(0.18, 1, dur)
                    {
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = delay,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    });
                    var grow = new DoubleAnimation(0.5, 1.14, dur)
                    {
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = delay,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    };
                    pop.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                    pop.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                    spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-turn / 2.0, turn, dur)
                    {
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = delay,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    });
                }
                // The ^^ squint, plus one delighted bounce on the mood channel (not Hop - a tap on the
                // mascot must still be able to make it jump while it is pleased).
                LookAt(0, 0);
                Sway(0);
                HappyBounce();
                // The eyes BEND into the smile - same shape, thirteen points moved. The blink
                // timer is parked so nothing yanks them back open mid-smile.
                MorphEyes(EyeHappy, 260);
                _blinkTimer.Stop();
                break;
        }
    }

    // The eye as ONE shape: thirteen points (a start point and four cubic segments) in the eye's
    // local box. The open eye is an exact ellipse (kappa 0.5523); the smile is the SAME points
    // moved into a caret, so the transition is a real morph, never a shape swap.
    static readonly Point[] EyeOpen =
    [
        new(3.523, 0.000),
        new(5.469, 0.000), new(7.046, 3.415), new(7.046, 7.628),
        new(7.046, 11.842), new(5.469, 15.257), new(3.523, 15.257),
        new(1.577, 15.257), new(0.000, 11.842), new(0.000, 7.628),
        new(0.000, 3.415), new(1.577, 0.000), new(3.523, 0.000),
    ];
    static readonly Point[] EyeHappy =
    [
        new(3.523, 3.200),
        new(5.792, 5.444), new(8.062, 7.688), new(10.400, 10.000),
        new(8.337, 8.980), new(5.724, 7.688), new(3.523, 6.600),
        new(1.324, 7.688), new(-1.288, 8.980), new(-3.350, 10.000),
        new(-1.013, 7.688), new(1.255, 5.444), new(3.523, 3.200),
    ];

    // Where each eye's local box sits on the board (the design's own numbers).
    static readonly Point LeftEyeAt = new(18.988, 14.255);
    static readonly Point RightEyeAt = new(31.490, 14.255);

    void MorphEyes(Point[] to, int ms)
    {
        MorphEye(LEyeFig, [LEyeS1, LEyeS2, LEyeS3, LEyeS4], LeftEyeAt, to, ms);
        MorphEye(REyeFig, [REyeS1, REyeS2, REyeS3, REyeS4], RightEyeAt, to, ms);
    }

    static void MorphEye(PathFigure fig, BezierSegment[] segs, Point at, Point[] to, int ms)
    {
        var d = TimeSpan.FromMilliseconds(ms);
        PointAnimation A(Point p) => new(new Point(p.X + at.X, p.Y + at.Y), d)
            { EasingFunction = Theme.Motion.ArriveStrong, FillBehavior = FillBehavior.HoldEnd };
        fig.BeginAnimation(PathFigure.StartPointProperty, A(to[0]));
        for (var i = 0; i < 4; i++)
        {
            segs[i].BeginAnimation(BezierSegment.Point1Property, A(to[1 + i * 3]));
            segs[i].BeginAnimation(BezierSegment.Point2Property, A(to[2 + i * 3]));
            segs[i].BeginAnimation(BezierSegment.Point3Property, A(to[3 + i * 3]));
        }
    }

    IEnumerable<(UIElement Element, ScaleTransform Pop, RotateTransform Spin)> Sparks()
    {
        yield return (Spark1, Spark1Pop, Spark1Spin);
        yield return (Spark2, Spark2Pop, Spark2Spin);
        yield return (Spark3, Spark3Pop, Spark3Spin);
        yield return (Spark4, Spark4Pop, Spark4Spin);
    }

    // The writing phrase, in milliseconds from the top of the loop; one table so the pen and body
    // cannot drift out of phase. Angles are a small wrist movement around ZERO - the pen's own
    // path already carries the angle it is held at.
    static readonly (int At, double Angle)[] Strokes =
    [
        (0, 0), (120, 3.5), (250, -3), (370, 2.5), (500, -2), (620, 3), (740, -1),
        (860, 0.5), (1010, 0.5),                                  // the hand lifts: a word ends
        (1130, 3.5), (1250, -2.5), (1380, 2), (1500, -1.5), (1620, 1.5),
        (1740, 0), (2200, 0),                                     // longer rest before the next phrase
    ];

    static DoubleAnimationUsingKeyFrames Scribble()
    {
        var f = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        foreach (var (at, angle) in Strokes)
            f.KeyFrames.Add(new EasingDoubleKeyFrame(angle, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at)),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
        return f;
    }

    /// <summary>The body's share of the writing phrase: the same timing, a fraction of the travel,
    /// dipping when the stroke pulls down.</summary>
    static DoubleAnimationUsingKeyFrames ScribbleBob()
    {
        var f = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        foreach (var (at, angle) in Strokes)
            f.KeyFrames.Add(new EasingDoubleKeyFrame(angle * 0.10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at)),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
        return f;
    }

    /// <summary>A prop growing out of the body: from small and clear at its own anchor point, up to
    /// full size with a couple of percent of overshoot.</summary>
    static void Grow(UIElement prop, ScaleTransform pop)
    {
        prop.Visibility = Visibility.Visible;
        prop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(MoodIn - 80)) { EasingFunction = Theme.Motion.ArriveStrong });
        var s = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(MoodIn))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 },
            FillBehavior = FillBehavior.HoldEnd,
        };
        pop.BeginAnimation(ScaleTransform.ScaleXProperty, s);
        pop.BeginAnimation(ScaleTransform.ScaleYProperty, s.Clone());
    }

    /// <summary>A prop shrinking back into the body it came from.</summary>
    static void Shrink(UIElement prop, ScaleTransform pop, double to)
    {
        prop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(MoodOut)) { EasingFunction = Theme.Motion.Depart, FillBehavior = FillBehavior.HoldEnd });
        var s = new DoubleAnimation(to, TimeSpan.FromMilliseconds(MoodOut)) { EasingFunction = Theme.Motion.Depart, FillBehavior = FillBehavior.HoldEnd };
        pop.BeginAnimation(ScaleTransform.ScaleXProperty, s);
        pop.BeginAnimation(ScaleTransform.ScaleYProperty, s.Clone());
    }

    /// <summary>
    /// Collapse a prop once its exit has played - UNLESS its mood has come back in the meantime.
    /// Keyed on the mood rather than on a completion callback because moods can flap (a recording that
    /// stops and restarts, a summary that finishes while the notch is mid-morph), and a completion
    /// handler that fires after its animation has already been replaced hides a prop that is
    /// currently being shown.
    /// </summary>
    void HideAfter(UIElement prop, TokiMood owner, int ms)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_mood == owner) return;
            prop.Visibility = Visibility.Collapsed;
            prop.BeginAnimation(OpacityProperty, null);
            prop.Opacity = 1;
        };
        t.Start();
    }

    /// <summary>
    /// Set the direction the MOOD points the eyes. Saccades and pointer-tracking are offsets FROM
    /// this, never replacements for it - so a writing Toki whose eyes are down on the page still
    /// glances up at your cursor, and comes back to the page rather than to dead centre.
    /// </summary>
    void LookAt(double x, double y)
    {
        _gazeBaseX = x; _gazeBaseY = y;
        ApplyGaze(380);
    }

    /// <summary>A held lean, or a slow sway of +/- <paramref name="degrees"/>.</summary>
    void Sway(double degrees, int periodMs = 0, double hold = 0)
    {
        if (degrees <= 0)
        {
            MoodLean.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(hold, TimeSpan.FromMilliseconds(360)) { EasingFunction = Theme.Motion.ArriveStrong, FillBehavior = FillBehavior.HoldEnd });
            return;
        }
        MoodLean.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-degrees, degrees, TimeSpan.FromMilliseconds(periodMs))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    /// <summary>A held vertical offset, or a repeating dip on <paramref name="periodMs"/>.</summary>
    void Bob(double y, int periodMs = 0)
    {
        if (periodMs <= 0)
        {
            MoodBob.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(y, TimeSpan.FromMilliseconds(420)) { EasingFunction = Theme.Motion.ArriveStrong, FillBehavior = FillBehavior.HoldEnd });
            return;
        }
        MoodBob.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, y, TimeSpan.FromMilliseconds(periodMs))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    void HappyBounce()
    {
        var f = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        void K(double y, int ms) => f.KeyFrames.Add(new EasingDoubleKeyFrame(y, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), new SineEase { EasingMode = EasingMode.EaseOut }));
        K(0, 0); K(-4, 190); K(0, 380); K(-1.5, 500); K(0, 620);
        MoodBob.BeginAnimation(TranslateTransform.YProperty, f);
    }

    /// <summary>Blink right now (a cute reaction, e.g. when the pill is hovered), with a tiny squash-pop.</summary>
    public void BlinkNow()
    {
        if (!IsVisible) return;
        Blink(doubleBlink: false);
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        Breathe.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.10, 1.0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        Breathe.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.90, 1.0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        ResumeBreathingAfter(320);
    }

    /// <summary>The springy "Hey Toki" hello: drop down with a bounce + a double blink, then settle.</summary>
    public void DropIn()
    {
        if (!IsVisible) return;
        Hop.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-7, 0, TimeSpan.FromMilliseconds(640)) { EasingFunction = new BounceEase { Bounces = 2, Bounciness = 2.6, EasingMode = EasingMode.EaseOut } });
        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        Breathe.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(460)) { EasingFunction = pop });
        Breathe.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(460)) { EasingFunction = pop });
        Blink(doubleBlink: true);
        ResumeBreathingAfter(680);
    }

    /// <summary>Cycle a fun one-off reaction each call (for the clickable mascot) - a different
    /// expression/motion every tap so it feels alive, not a single canned bounce.</summary>
    public void React()
    {
        if (!IsVisible) return;
        switch (_reactKind++ % 7)
        {
            case 0: Squish(); break;
            case 1: Hopp(); break;
            case 2: Wobble(); break;
            case 3: Wink(); break;
            case 4: Spin(); break;
            case 5: BigEyes(); break;
            default: Nod(); break;
        }
    }

    // ── reaction vocabulary ──

    void Squish()
    {
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 };
        Breathe.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.22, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        Breathe.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.80, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        Blink(doubleBlink: false);
        ResumeBreathingAfter(400);
    }

    void Hopp()   // a little jump up and back down (breathing continues underneath)
    {
        Hop.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(560)) { EasingFunction = new BounceEase { Bounces = 2, Bounciness = 2.2, EasingMode = EasingMode.EaseOut } });
        Blink(doubleBlink: false);
    }

    void Wobble()
    {
        var f = new DoubleAnimationUsingKeyFrames();
        void K(double a, int ms) => f.KeyFrames.Add(new EasingDoubleKeyFrame(a, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        K(-11, 0); K(11, 130); K(-8, 260); K(5, 380); K(0, 500);
        Tilt.BeginAnimation(RotateTransform.AngleProperty, f);
        Blink(doubleBlink: false);
    }

    void Wink()   // just the left eye, with a cheeky tilt
    {
        var f = new DoubleAnimationUsingKeyFrames();
        f.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        f.KeyFrames.Add(new LinearDoubleKeyFrame(0.06, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        f.KeyFrames.Add(new LinearDoubleKeyFrame(0.06, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230))));
        f.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330))));
        LeftBlink.BeginAnimation(ScaleTransform.ScaleYProperty, f);
        Tilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } });
    }

    void Spin()
    {
        Tilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(640)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseInOut, Amplitude = 0.4 } });
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
        Breathe.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        Breathe.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        ResumeBreathingAfter(660);
    }

    void BigEyes()   // a surprised widen of both eyes + a tiny pop
    {
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        foreach (var eye in new[] { LeftBlink, RightBlink })
        {
            eye.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.35, TimeSpan.FromMilliseconds(230)) { AutoReverse = true, EasingFunction = ease });
            eye.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.30, TimeSpan.FromMilliseconds(230)) { AutoReverse = true, EasingFunction = ease });
        }
        Hop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } });
    }

    void Nod()   // two gentle nods
    {
        var f = new DoubleAnimationUsingKeyFrames();
        void K(double y, int ms) => f.KeyFrames.Add(new EasingDoubleKeyFrame(y, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        K(0, 0); K(3.5, 130); K(0, 260); K(3.5, 390); K(0, 520);
        Hop.BeginAnimation(TranslateTransform.YProperty, f);
    }

    void ResumeBreathingAfter(int ms)
    {
        _resume?.Stop();
        _resume = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _resume.Tick += (_, _) => { _resume!.Stop(); if (_running) StartBreathing(); };
        _resume.Start();
    }

    void ScheduleBlink()
    {
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(2200 + Rng.Next(2600));
        _blinkTimer.Start();
    }

    // The pointer in PHYSICAL pixels, which is the same space PointToScreen returns - so the two can
    // be subtracted without a DPI conversion, on any monitor scale.
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

    void Blink(bool doubleBlink)
    {
        // Asymmetric, the way real blinks read on screen: close fast (60ms, linear), open slower
        // (190ms, decelerating) - 250ms total.
        var frames = new DoubleAnimationUsingKeyFrames();
        void Close(int atMs)
        {
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(atMs))));
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(0.06, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(atMs + 60))));
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(atMs + 250)),
                new SineEase { EasingMode = EasingMode.EaseOut }));
        }
        Close(0);
        if (doubleBlink) Close(330);

        LeftBlink.BeginAnimation(ScaleTransform.ScaleYProperty, frames);
        RightBlink.BeginAnimation(ScaleTransform.ScaleYProperty, frames.Clone());
    }
}
