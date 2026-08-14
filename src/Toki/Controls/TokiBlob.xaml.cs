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
    DispatcherTimer? _resume;

    bool _running;
    int _reactKind;
    int _fidgetKind;

    public TokiBlob()
    {
        InitializeComponent();
        _blinkTimer.Tick += OnBlinkTick;
        _fidgetTimer.Tick += OnFidgetTick;
        // Only animate while actually on screen - cached-but-hidden views must
        // not burn CPU (this was part of the slowness).
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Run(); else Pause(); };
        Unloaded += (_, _) => Pause();
    }

    void Run()
    {
        if (_running) return;
        _running = true;
        StartBreathing();
        ScheduleBlink();
        ScheduleFidget();
    }

    void OnBlinkTick(object? sender, EventArgs e)
    {
        _blinkTimer.Stop();
        if (!_running) return;
        Blink(doubleBlink: Rng.Next(4) == 0);
        ScheduleBlink();
    }

    // ── fidgets: the "alive when ignored" layer (the motion-and-feel skill: every 30-60s a small
    // autonomous motion, layered over idle, never during a working mood - a thinking/writing Toki
    // is busy, and a wobble mid-scribble would read as a glitch, not charm). ──
    void OnFidgetTick(object? sender, EventArgs e)
    {
        _fidgetTimer.Stop();
        if (!_running) return;
        if (_mood == TokiMood.Idle)
            switch (_fidgetKind++ % 4)
            {
                case 0: Glance(); break;
                case 1: Nod(); break;
                case 2: Wobble(); break;
                default: Blink(doubleBlink: true); break;
            }
        ScheduleFidget();
    }

    void ScheduleFidget()
    {
        _fidgetTimer.Interval = TimeSpan.FromMilliseconds(30_000 + Rng.Next(30_000));
        _fidgetTimer.Start();
    }

    /// <summary>A sideways glance: the eyes drift a few px aside, hold a beat, drift back.</summary>
    void Glance()
    {
        var dir = Rng.Next(2) == 0 ? -3.5 : 3.5;
        var f = new DoubleAnimationUsingKeyFrames();
        void K(double x, int ms) => f.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        K(0, 0); K(dir, 260); K(dir, 1100); K(0, 1400);
        Gaze.BeginAnimation(TranslateTransform.XProperty, f);
    }

    void Pause()
    {
        _running = false;
        _blinkTimer.Stop();
        _fidgetTimer.Stop();
        _resume?.Stop();
        Gaze.BeginAnimation(TranslateTransform.XProperty, null); Gaze.X = 0;
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
        // ~3.5s full cycle at <=2% amplitude (the researched relaxed-breathing numbers - 15-20
        // breaths/min, tiny travel). The old 3s cycle at 4.5% read agitated, not calm.
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

    public enum TokiMood { Idle, Thinking, Writing, Happy }

    TokiMood _mood = TokiMood.Idle;

    /// <summary>
    /// Put Toki in a mood: Thinking (dots bubble, dots pulsing in sequence), Writing (pen scribbling
    /// at his side), Happy (squinted ^^ eyes + sparkles), or Idle. The notch wires these to its
    /// states - thinking while notes are being written, writing while transcribing, happy when the
    /// notes are ready - so the character narrates what the app is doing without a word of text.
    /// Idempotent; switching moods tears the previous overlay down cleanly.
    /// </summary>
    public void SetMood(TokiMood mood)
    {
        if (_mood == mood) return;
        _mood = mood;

        // Tear down everything, then raise the one overlay this mood owns.
        ThinkBubble.Visibility = Visibility.Collapsed;
        Pen.Visibility = Visibility.Collapsed;
        Sparkles.Visibility = Visibility.Collapsed;
        foreach (var d in new[] { Dot1, Dot2, Dot3 }) d.BeginAnimation(OpacityProperty, null);
        PenTilt.BeginAnimation(RotateTransform.AngleProperty, null);
        Sparkles.BeginAnimation(OpacityProperty, null);
        // Un-squint (Happy holds the lids down; every other mood has open eyes).
        LeftBlink.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RightBlink.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        LeftBlink.ScaleY = RightBlink.ScaleY = 1;
        _blinkTimer.Stop();
        if (_running) ScheduleBlink();

        switch (mood)
        {
            case TokiMood.Thinking:
            {
                ThinkBubble.Visibility = Visibility.Visible;
                // The dots pulse in order - the universal "working on it".
                var dots = new[] { Dot1, Dot2, Dot3 };
                for (var i = 0; i < dots.Length; i++)
                    dots[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(420))
                    {
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                        BeginTime = TimeSpan.FromMilliseconds(i * 160),
                    });
                break;
            }
            case TokiMood.Writing:
            {
                Pen.Visibility = Visibility.Visible;
                // A small wrist-scribble, not a wave: ±7° around the nib, quick and continuous.
                PenTilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(98, 112, TimeSpan.FromMilliseconds(240))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
                break;
            }
            case TokiMood.Happy:
            {
                Sparkles.Visibility = Visibility.Visible;
                Sparkles.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(700))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
                // The ^^ squint: lids held most of the way down. The blink timer is stopped above so a
                // scheduled blink can't pop the eyes open mid-smile.
                LeftBlink.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.28, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.HoldEnd });
                RightBlink.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.28, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.HoldEnd });
                _blinkTimer.Stop();
                break;
            }
        }
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
            new DoubleAnimation(-14, 0, TimeSpan.FromMilliseconds(640)) { EasingFunction = new BounceEase { Bounces = 2, Bounciness = 2.6, EasingMode = EasingMode.EaseOut } });
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
            new DoubleAnimation(-16, 0, TimeSpan.FromMilliseconds(560)) { EasingFunction = new BounceEase { Bounces = 2, Bounciness = 2.2, EasingMode = EasingMode.EaseOut } });
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
        Hop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } });
    }

    void Nod()   // two gentle nods
    {
        var f = new DoubleAnimationUsingKeyFrames();
        void K(double y, int ms) => f.KeyFrames.Add(new EasingDoubleKeyFrame(y, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        K(0, 0); K(7, 130); K(0, 260); K(7, 390); K(0, 520);
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

    void Blink(bool doubleBlink)
    {
        // Asymmetric, the way real blinks read on screen: close FAST (60ms, linear), open slower
        // (190ms, decelerating) - a 250ms blink total (the researched natural-on-screen number).
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
