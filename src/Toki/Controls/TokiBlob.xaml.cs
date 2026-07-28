using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Toki.Controls;

/// <summary>
/// The Toki character — a pink blob with two eyes that breathes continuously and
/// blinks at natural, slightly irregular intervals. Self-animating: any view that
/// drops a &lt;TokiBlob/&gt; in gets a living character with no extra wiring.
/// </summary>
public partial class TokiBlob : UserControl
{
    static readonly Random Rng = new();
    readonly DispatcherTimer _blinkTimer = new();

    bool _running;

    public TokiBlob()
    {
        InitializeComponent();
        _blinkTimer.Tick += OnBlinkTick;
        // Only animate while actually on screen — cached-but-hidden views must
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
    }

    void OnBlinkTick(object? sender, EventArgs e)
    {
        _blinkTimer.Stop();
        if (!_running) return;
        Blink(doubleBlink: Rng.Next(4) == 0);
        ScheduleBlink();
    }

    void Pause()
    {
        _running = false;
        _blinkTimer.Stop();
        Breathe.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Breathe.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        LeftBlink.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RightBlink.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    void StartBreathing()
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var y = new DoubleAnimation(1.0, 0.955, TimeSpan.FromMilliseconds(1500))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
        };
        var x = new DoubleAnimation(1.0, 1.03, TimeSpan.FromMilliseconds(1500))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
        };
        Breathe.BeginAnimation(ScaleTransform.ScaleYProperty, y);
        Breathe.BeginAnimation(ScaleTransform.ScaleXProperty, x);
    }

    /// <summary>Blink right now (a cute reaction, e.g. when the pill is hovered). Also
    /// gives a tiny squash-pop so it reads as "Toki noticed you".</summary>
    public void BlinkNow()
    {
        if (!IsVisible) return;
        Blink(doubleBlink: false);
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        Breathe.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1.10, 1.0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        Breathe.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.90, 1.0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        // Resume the gentle breathing afterwards so it doesn't freeze on the popped scale.
        var resume = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        resume.Tick += (_, _) => { resume.Stop(); if (_running) StartBreathing(); };
        resume.Start();
    }

    void ScheduleBlink()
    {
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(2200 + Rng.Next(2600));
        _blinkTimer.Start();
    }

    void Blink(bool doubleBlink)
    {
        var frames = new DoubleAnimationUsingKeyFrames();
        void Close(int atMs)
        {
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(atMs))));
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(0.06, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(atMs + 70))));
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(atMs + 150))));
        }
        Close(0);
        if (doubleBlink) Close(230);

        LeftBlink.BeginAnimation(ScaleTransform.ScaleYProperty, frames);
        RightBlink.BeginAnimation(ScaleTransform.ScaleYProperty, frames.Clone());
    }
}
