using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Toki.Views;

/// <summary>
/// The "working on your notes" loader. Mostly lives tucked as a 13px sliver with a coral
/// shimmer along the top edge; hover opens a tiny bobbing Toki beside a status line.
/// </summary>
public partial class SummarizingView : UserControl
{
    readonly DispatcherTimer _dotTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    readonly TranslateTransform _bob = new();
    int _dots;

    public SummarizingView()
    {
        InitializeComponent();
        Toki.RenderTransform = _bob;
        _dotTimer.Tick += (_, _) => { _dots = (_dots + 1) % 4; Dots.Text = new string('.', _dots); };

        Loaded += (_, _) => { StartShimmer(); StartBob(); _dots = 0; _dotTimer.Start(); };
        Unloaded += (_, _) =>
        {
            _dotTimer.Stop();
            ShimmerMove.BeginAnimation(TranslateTransform.XProperty, null);
            _bob.BeginAnimation(TranslateTransform.YProperty, null);
        };
    }

    static SineEase Smooth => new() { EasingMode = EasingMode.EaseInOut };

    void StartShimmer() =>
        ShimmerMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-90, 260, new Duration(TimeSpan.FromMilliseconds(1150)))
            { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Smooth });

    void StartBob() =>
        _bob.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -3, new Duration(TimeSpan.FromMilliseconds(760)))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Smooth });
}
