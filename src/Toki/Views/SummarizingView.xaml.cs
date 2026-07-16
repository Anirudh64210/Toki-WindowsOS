using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Toki.State;

namespace Toki.Views;

public partial class SummarizingView : UserControl
{
    static readonly string[] Steps =
    [
        "Merging the two streams", "Reading the transcript",
        "Finding action items", "Writing the TL;DR",
    ];
    readonly DispatcherTimer _stepTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    readonly DispatcherTimer _dotTimer = new() { Interval = TimeSpan.FromMilliseconds(380) };
    readonly TranslateTransform _tokiBob = new();
    int _step;
    int _dots;

    public SummarizingView()
    {
        InitializeComponent();
        Toki.RenderTransform = _tokiBob;
        StartAnimations();

        _dotTimer.Tick += (_, _) =>
        {
            _dots = (_dots + 1) % 4;
            Dots.Text = new string('.', _dots);
        };

        // Placeholder pipeline: cycle status lines, then hand over to Notes.
        // M6 replaces this with the real three-pass summarizer, which advances
        // these steps as each pass actually completes.
        _stepTimer.Tick += (_, _) =>
        {
            _step++;
            if (_step < Steps.Length)
            {
                StatusText.Text = Steps[_step];
            }
            else
            {
                _stepTimer.Stop();
                _dotTimer.Stop();
                // A silent meeting saves nothing (lesson 7): friendly empty state.
                App.State.TryGo(App.LastRecordingSilent ? PillState.NoSpeech : PillState.Notes);
            }
        };

        Loaded += (_, _) =>
        {
            _step = 0;
            _dots = 0;
            StatusText.Text = Steps[0];
            _stepTimer.Start();
            _dotTimer.Start();
        };
        Unloaded += (_, _) => { _stepTimer.Stop(); _dotTimer.Stop(); };
    }

    void StartAnimations()
    {
        var loop = RepeatBehavior.Forever;
        var smooth = new SineEase { EasingMode = EasingMode.EaseInOut };

        // Sparkle spins.
        SparkleSpin.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(2400)) { RepeatBehavior = loop });

        // Toki bobs.
        _tokiBob.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -7, TimeSpan.FromMilliseconds(620))
            { AutoReverse = true, RepeatBehavior = loop, EasingFunction = smooth });

        // Glow pulses (scale + opacity) in time with the bob.
        GlowScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.82, 1.12, TimeSpan.FromMilliseconds(1240))
            { AutoReverse = true, RepeatBehavior = loop, EasingFunction = smooth });
        GlowScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.82, 1.12, TimeSpan.FromMilliseconds(1240))
            { AutoReverse = true, RepeatBehavior = loop, EasingFunction = smooth });
        Glow.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(1240))
            { AutoReverse = true, RepeatBehavior = loop, EasingFunction = smooth });

        // A sheet lifts off the stack and settles, like pages being read.
        var lift = new DoubleAnimationUsingKeyFrames { RepeatBehavior = loop, Duration = TimeSpan.FromMilliseconds(1800) };
        lift.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.0)));
        lift.KeyFrames.Add(new EasingDoubleKeyFrame(-9, KeyTime.FromPercent(0.35), smooth));
        lift.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.7), smooth));
        lift.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1.0)));
        TopPageLift.BeginAnimation(TranslateTransform.YProperty, lift);
    }
}
