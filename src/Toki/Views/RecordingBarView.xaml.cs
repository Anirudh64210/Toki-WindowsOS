using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Toki.State;

namespace Toki.Views;

public partial class RecordingBarView : UserControl
{
    public RecordingBarView()
    {
        InitializeComponent();
        // A slow, gentle pulse on the recording dot — reads as "live", never a
        // distraction. Runs only while the tiny Toki is visible. (No timer here:
        // the elapsed time lives in the expanded controls, one click away.)
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) PulseDot();
            else Dot.BeginAnimation(OpacityProperty, null);
        };
    }

    void PulseDot()
    {
        var pulse = new DoubleAnimation(1.0, 0.4, TimeSpan.FromMilliseconds(950))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Dot.BeginAnimation(OpacityProperty, pulse);
    }

    void OnExpand(object sender, MouseButtonEventArgs e) => App.State.TryGo(PillState.RecordingOpen);

    void OnStop(object sender, RoutedEventArgs e) => App.StopRecording();
}
