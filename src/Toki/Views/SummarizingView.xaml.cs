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

        Loaded += (_, _) =>
        {
            StartShimmer(); StartBob(); _dots = 0; _dotTimer.Start();
            // NAME THE STAGE. A single unchanging phrase across a wait that can run for minutes is
            // indistinguishable from a hung app; the pipeline now says whether it is transcribing or
            // writing, and this reads it. Subscribed on load and dropped on unload, because this view
            // is rebuilt per state and a lingering handler would keep it alive.
            ShowStage();
            App.SummarizeStageChanged += OnStage;
        };
        Unloaded += (_, _) =>
        {
            App.SummarizeStageChanged -= OnStage;
            _dotTimer.Stop();
            ShimmerMove.BeginAnimation(TranslateTransform.XProperty, null);
            _bob.BeginAnimation(TranslateTransform.YProperty, null);
        };
    }

    void OnStage(object? sender, EventArgs e) => ShowStage();

    void ShowStage()
    {
        var stage = App.SummarizeStage;
        if (!string.IsNullOrWhiteSpace(stage)) StageLabel.Text = stage;
    }

    static SineEase Smooth => new() { EasingMode = EasingMode.EaseInOut };

    const double ShimmerWidth = 70;

    // Sweep the highlight across the WHOLE track: it enters fully off the left edge (-ShimmerWidth) and
    // leaves fully off the right (the track's width), so it covers the bar end to end at ANY width. Read
    // the width live rather than hardcoding a range that only reaches part of a wider sliver.
    void StartShimmer()
    {
        var w = Track.ActualWidth;
        if (w <= 1) return;   // not laid out yet - OnTrackSizeChanged will start it
        ShimmerMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-ShimmerWidth, w, new Duration(TimeSpan.FromMilliseconds(1150)))
            { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Smooth });
    }

    // The sliver's width is only known after layout (and can change with the card), so (re)start the
    // sweep whenever the track resizes - the range must track the real width to stay edge-to-edge.
    void OnTrackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && IsLoaded) StartShimmer();
    }

    void StartBob() =>
        _bob.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -3, new Duration(TimeSpan.FromMilliseconds(760)))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Smooth });
}
