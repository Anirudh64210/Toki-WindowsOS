using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
                Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.4, TimeSpan.FromMilliseconds(950))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
            else Dot.BeginAnimation(OpacityProperty, null);
        };
    }

    void OnCollapse(object sender, MouseButtonEventArgs e) => App.State.TryGo(PillState.RecordingBar);

    void OnStar(object sender, RoutedEventArgs e)
    {
        // Placeholder: markers land with real transcription (M4).
    }

    void OnStop(object sender, RoutedEventArgs e) => App.StopRecording();
}
