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

    public TokiBlob()
    {
        InitializeComponent();
        Loaded += (_, _) => { StartBreathing(); ScheduleBlink(); };
        Unloaded += (_, _) => _blinkTimer.Stop();
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

    void ScheduleBlink()
    {
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(2200 + Rng.Next(2600));
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkTimer.Stop();
            Blink(doubleBlink: Rng.Next(4) == 0);
            ScheduleBlink();
        };
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
