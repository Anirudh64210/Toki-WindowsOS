using System;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Toki.State;

namespace Toki.Views;

public partial class RecordingBarView : UserControl
{
    public RecordingBarView()
    {
        InitializeComponent();
        // Each time recording's tiny peek appears, Toki does the flip-and-fall so it ends up
        // hanging upside-down with its eyes peeking over the edge.
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) PlayFlipAndFall(); };
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
