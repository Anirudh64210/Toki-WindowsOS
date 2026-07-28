using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Toki.State;

namespace Toki.Views;

public partial class RecordingOpenView : UserControl
{
    public RecordingOpenView()
    {
        InitializeComponent();
        RecordingClock.Attach(this, Timer);
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.4, TimeSpan.FromMilliseconds(950))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
                PlayPop();
            }
            else Dot.BeginAnimation(OpacityProperty, null);
        };
    }

    // A lively spring-pop on Toki each time the pill expands on hover — Toki is alive and
    // popping up to greet you, not a static panel unfolding.
    void PlayPop()
    {
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 };
        var a = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease };
        TokiPop.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        TokiPop.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void OnCollapse(object sender, MouseButtonEventArgs e) => App.State.TryGo(PillState.RecordingBar);

    void OnStop(object sender, RoutedEventArgs e) => App.StopRecording();
}
