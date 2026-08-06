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
        // Live transcript preview - real segments as Toki hears them (Phase 2).
        Loaded += (_, _) =>
        {
            LiveLine.Text = "Listening…";
            LiveLine.Opacity = 1;
            App.LivePreview += OnLiveSegment;
            // Offer the prep card only when this meeting actually has one - an ad-hoc call Toki merely
            // detected has no calendar event behind it, and a button that opens nothing is worse than
            // no button.
            PrepButton.Visibility = Calendar.PrepWatcher.Armed ? Visibility.Visible : Visibility.Collapsed;
        };
        Unloaded += (_, _) => { App.LivePreview -= OnLiveSegment; };
    }

    // Show the most recent finalized line, fading it in so it reads as "just heard".
    void OnLiveSegment(Toki.Transcription.TranscriptSegment s)
    {
        var text = s.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        LiveLine.Text = text;
        LiveLine.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1.0, Theme.Motion.Dur(Theme.Motion.Enter))
        {
            EasingFunction = Theme.Motion.EaseOut,   // arriving content decelerates (central motion token)
        });
    }

    // A lively spring-pop on Toki each time the pill expands on hover - Toki is alive and
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

    // Open the prep card mid-meeting. Recording is untouched - the mic keeps running behind it, and
    // the card's Back button brings this page straight back.
    void OnPrep(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.PrepCard);
}
