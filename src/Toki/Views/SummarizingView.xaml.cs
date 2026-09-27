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
        // Subscribed ONCE, in the constructor. Loaded fires again on every summarize because the view
        // is cached, and a += there stacked one handler per meeting for the life of the process.
        _elapsedTimer.Tick += (_, _) => TickElapsed();

        // Correct BEFORE Loaded, not only on it. A cached view can be measured or photographed
        // without ever being connected to a window (the render harness does exactly that), and a
        // loader showing the XAML placeholder is a loader that is lying about where the pipeline is.
        ShowStage();

        Loaded += (_, _) =>
        {
            StartShimmer(); StartBob(); _dots = 0; _dotTimer.Start();
            _started = DateTime.UtcNow;
            _elapsedTimer.Start(); TickElapsed();
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
            _elapsedTimer.Stop();
            ShimmerMove.BeginAnimation(TranslateTransform.XProperty, null);
            _bob.BeginAnimation(TranslateTransform.YProperty, null);
        };
    }

    void OnStage(object? sender, EventArgs e) => ShowStage();

    /// <summary>The pipeline's stages, in the order App raises them. Index drives the sliver's fill;
    /// an unrecognised stage leaves the fill where it is rather than guessing backwards.</summary>
    static readonly string[] Stages = { "Transcribing", "Identifying speakers", "Writing your notes" };

    /// <summary>When THIS wait began - stamped on Loaded, never at construction. PillWindow caches
    /// one view per state for the life of the process, so a field initialiser here measures UPTIME,
    /// not the summarize: it read "1275m 37s" on a two-minute meeting because the loader had been
    /// built the previous evening (bug, 2026-09-04).</summary>
    DateTime _started = DateTime.UtcNow;
    readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    void ShowStage()
    {
        var stage = App.SummarizeStage;
        if (string.IsNullOrWhiteSpace(stage)) return;
        // ONCE THE MEETING HAS A TITLE, SAY IT. "Writing your notes" is a description of Toki; the
        // title is a fact about the user's meeting, and it proves the model has read it. The stage
        // still drives the progress fill below - only the words change.
        var title = App.SummarizeTitle;
        var titled = !string.IsNullOrWhiteSpace(title);
        StageLabel.Text = titled ? title!.Trim() : stage;
        // The trailing dots say "still working" after a STAGE. After a title they read as part of the
        // words, and next to an ellipsised title they are simply two kinds of dot in a row. The
        // shimmer above is already the heartbeat, so the title stands alone and takes their width.
        Dots.Visibility = titled ? Visibility.Collapsed : Visibility.Visible;

        // Fill a third per stage. Animated, not set: the jump between thirds is the one moment in
        // this whole wait where something visibly ADVANCES, and a snap wastes it.
        var idx = Array.FindIndex(Stages, s => stage.StartsWith(s, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        var target = Track.ActualWidth * (idx + 1) / Stages.Length;
        if (target <= 0) return;
        Progress.BeginAnimation(WidthProperty,
            new DoubleAnimation(target, Theme.Motion.Dur(Theme.Motion.Ms(Theme.Motion.Morph)))
            { EasingFunction = Theme.Motion.ArriveStrong });
    }

    /// <summary>Elapsed time, shown only after 20s. Before that a timer is noise; after it, a wait
    /// with no number reads as a hang. Hidden state is the default so a quick meeting never sees it.</summary>
    void TickElapsed()
    {
        var secs = (int)(DateTime.UtcNow - _started).TotalSeconds;
        if (secs < 20) { Elapsed.Text = ""; return; }
        Elapsed.Text = secs < 60 ? $"{secs}s" : $"{secs / 60}m {secs % 60:00}s";
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
