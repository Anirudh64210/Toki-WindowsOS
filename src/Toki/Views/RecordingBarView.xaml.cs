using System;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Toki.State;

namespace Toki.Views;

public partial class RecordingBarView : UserControl
{
    /// <summary>Play the flip-and-fall only when recording STARTS; on a hover-collapse Toki is
    /// already upside-down and must just settle. Set by the pill before the peek is shown.</summary>
    public bool FlipOnShow { get; set; } = true;

    public RecordingBarView()
    {
        InitializeComponent();
        // The bar is a peek (the floating bubble Toki is hidden), so its tiny Toki carries the mood.
        BarToki.SetMood(Controls.TokiBlob.TokiMood.Writing);
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) return;
            if (FlipOnShow) PlayFlipAndFall();
            else
            {
                // Collapsing back from the open screen: settle upside-down without re-animating.
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
        // Turn leads, fall follows 60ms late. Character motion is exempt from the 350ms cap
        // (Motion.cs). Transforms only - nothing in this subtree may carry an Effect.
        Flip.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 180, TimeSpan.FromMilliseconds(520))
            { EasingFunction = Theme.Motion.Move });
        Fall.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-12, 0, TimeSpan.FromMilliseconds(480))
            {
                BeginTime = TimeSpan.FromMilliseconds(60),
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
            });
    }

    // Click also opens the controls (hover is the primary way, handled by the pill's poll).
    void OnExpand(object sender, MouseButtonEventArgs e) => App.State.TryGo(PillState.RecordingOpen);
}
