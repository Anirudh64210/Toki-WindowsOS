using System;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Toki.State;

namespace Toki.Views;

public partial class RecordingBarView : UserControl
{
    /// <summary>Play the dramatic flip-and-fall only when recording STARTS. On a hover-collapse from
    /// the open screen Toki is already upside-down, so it should just settle - not re-flip (that read
    /// as an abrupt "shut down"). Set by the pill before the peek is shown.</summary>
    public bool FlipOnShow { get; set; } = true;

    public RecordingBarView()
    {
        InitializeComponent();
        // The bar is a PEEK (the floating bubble Toki is hidden), so ITS tiny Toki carries the
        // state's mood: writing while the meeting is being taken down - the mascot narrates
        // "transcribing" here the same way the bubble does on the open panel.
        BarToki.SetMood(Controls.TokiBlob.TokiMood.Writing);
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) return;
            if (FlipOnShow) PlayFlipAndFall();
            else
            {
                // Already recording (collapsing back from the open screen): settle upside-down
                // without re-animating, so the minimise reads as a clean zoom-out, not a flip.
                Flip.BeginAnimation(RotateTransform.AngleProperty, null);
                Fall.BeginAnimation(TranslateTransform.YProperty, null);
                Flip.Angle = 180;
                Fall.Y = 0;
            }
        };
    }

    void PlayFlipAndFall()
    {
        Flip.Angle = 0;
        Fall.Y = -12;
        // Tighter: flip over (0 → 180°) and drop in with a small bounce, settling upside-down
        // so only the eyes peek over the edge (the body is tucked above it).
        Flip.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 180, TimeSpan.FromMilliseconds(420))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } });
        Fall.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-12, 0, TimeSpan.FromMilliseconds(480))
            { EasingFunction = new BounceEase { Bounces = 2, Bounciness = 2.6, EasingMode = EasingMode.EaseOut } });
    }

    // Click also opens the controls (hover is the primary way, handled by the pill's poll).
    void OnExpand(object sender, MouseButtonEventArgs e) => App.State.TryGo(PillState.RecordingOpen);
}
