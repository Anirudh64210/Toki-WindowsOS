using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Toki.State;

namespace Toki.Views;

/// <summary>
/// The "working on your notes" loader: Toki stays alive (breathing, blinking,
/// bobbing) while a motif beside it changes through the stages of understanding
/// a meeting — listening, reading, organizing, writing. Clean, rounded, spaced.
/// Placeholder pacing for now; M6 drives the stages off the real passes.
/// </summary>
public partial class SummarizingView : UserControl
{
    sealed record Stage(string Label, Action Start);

    readonly DispatcherTimer _stageTimer = new() { Interval = TimeSpan.FromMilliseconds(1750) };
    readonly DispatcherTimer _dotTimer = new() { Interval = TimeSpan.FromMilliseconds(380) };
    readonly TranslateTransform _tokiBob = new();
    Stage[] _stages = [];
    int _stage;
    int _dots;

    public SummarizingView()
    {
        InitializeComponent();
        Toki.RenderTransform = _tokiBob;
        _stages =
        [
            new("Listening back",   StartListen),
            new("Reading it over",  StartRead),
            new("Finding to-dos",   StartOrganize),
            new("Writing it up",    StartWrite),
        ];

        _dotTimer.Tick += (_, _) => { _dots = (_dots + 1) % 4; Dots.Text = new string('.', _dots); };
        // Loop the stages while real transcription runs in the background; App
        // drives the transition to Notes / NoSpeech when it finishes.
        _stageTimer.Tick += (_, _) =>
        {
            _stage = (_stage + 1) % _stages.Length;
            EnterStage(_stage);
        };

        Loaded += (_, _) =>
        {
            _stage = 0; _dots = 0;
            StartAmbient();
            EnterStage(0);
            _stageTimer.Start();
            _dotTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            _stageTimer.Stop();
            _dotTimer.Stop();
            StopAllMotifs();
            _tokiBob.BeginAnimation(TranslateTransform.YProperty, null);
            GlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            GlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Glow.BeginAnimation(OpacityProperty, null);
        };
    }

    static readonly Duration Fast = new(TimeSpan.FromMilliseconds(300));
    static SineEase Smooth => new() { EasingMode = EasingMode.EaseInOut };
    static readonly RepeatBehavior Loop = RepeatBehavior.Forever;

    void StartAmbient()
    {
        _tokiBob.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -6, new Duration(TimeSpan.FromMilliseconds(700)))
            { AutoReverse = true, RepeatBehavior = Loop, EasingFunction = Smooth });
        var pulse = new DoubleAnimation(0.85, 1.12, new Duration(TimeSpan.FromMilliseconds(1300)))
        { AutoReverse = true, RepeatBehavior = Loop, EasingFunction = Smooth };
        GlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        GlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        Glow.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.5, 1.0, new Duration(TimeSpan.FromMilliseconds(1300)))
            { AutoReverse = true, RepeatBehavior = Loop, EasingFunction = Smooth });
    }

    void EnterStage(int i)
    {
        StatusText.Text = _stages[i].Label;
        StopAllMotifs(); // only the active motif should animate — saves CPU
        var motifs = new FrameworkElement[] { MotifListen, MotifRead, MotifOrganize, MotifWrite };
        for (var m = 0; m < motifs.Length; m++)
            motifs[m].BeginAnimation(OpacityProperty, new DoubleAnimation(m == i ? 1 : 0, Fast));
        _stages[i].Start();

        // Progress fill advances smoothly across the four stages.
        var frac = (i + 1) / (double)_stages.Length;
        var target = Math.Max(10, Track.ActualWidth * frac);
        ProgressFill.BeginAnimation(WidthProperty,
            new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(500)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void StopAllMotifs()
    {
        foreach (var r in new[] { Ring1, Ring2, Ring3 })
        {
            r.BeginAnimation(OpacityProperty, null);
            ((ScaleTransform)r.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ((ScaleTransform)r.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        ReadHighlightMove.BeginAnimation(TranslateTransform.YProperty, null);
        Check1.BeginAnimation(OpacityProperty, null);
        Check2.BeginAnimation(OpacityProperty, null);
        Check3.BeginAnimation(OpacityProperty, null);
        WriteLine.BeginAnimation(WidthProperty, null);
    }

    // ---- motif animations ----

    void StartListen()
    {
        Ring((ScaleTransform)Ring1.RenderTransform, Ring1, 0);
        Ring((ScaleTransform)Ring2.RenderTransform, Ring2, 466);
        Ring((ScaleTransform)Ring3.RenderTransform, Ring3, 933);

        static void Ring(ScaleTransform st, UIElement e, int delayMs)
        {
            var dur = new Duration(TimeSpan.FromMilliseconds(1400));
            var begin = TimeSpan.FromMilliseconds(delayMs);
            var grow = new DoubleAnimation(0.5, 2.6, dur) { RepeatBehavior = Loop, BeginTime = begin };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            e.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.9, 0, dur) { RepeatBehavior = Loop, BeginTime = begin });
        }
    }

    void StartRead()
    {
        ReadHighlightMove.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-18, 18, new Duration(TimeSpan.FromMilliseconds(850)))
            { AutoReverse = true, RepeatBehavior = Loop, EasingFunction = Smooth });
    }

    void StartOrganize()
    {
        Tick(Check1, 0);
        Tick(Check2, 400);
        Tick(Check3, 800);

        static void Tick(UIElement check, int atMs)
        {
            var frames = new DoubleAnimationUsingKeyFrames { RepeatBehavior = Loop, Duration = new Duration(TimeSpan.FromMilliseconds(1650)) };
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(atMs))));
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(atMs + 220))));
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500))));
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1650))));
            check.BeginAnimation(UIElement.OpacityProperty, frames);
        }
    }

    void StartWrite()
    {
        var frames = new DoubleAnimationUsingKeyFrames { RepeatBehavior = Loop, Duration = new Duration(TimeSpan.FromMilliseconds(1400)) };
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(52, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000)), Smooth));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(52, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1250))));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1400))));
        WriteLine.BeginAnimation(WidthProperty, frames);
    }
}
