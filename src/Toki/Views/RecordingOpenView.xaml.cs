using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Toki.State;
using Toki.Transcription;

namespace Toki.Views;

public partial class RecordingOpenView : UserControl, IWidthAware
{
    // The two card widths: compact bar, and the expanded transcript panel.
    const double BarWidth = 272;
    const double ExpandedWidth = 344;

    const int MaxLiveLines = 12;

    bool _expanded;

    // Rotation lives on the GLYPH, never the button: the puck's own press-compress transform is the
    // shared style's, and a second RenderTransform on the button would replace it.
    readonly RotateTransform _chevronTurn = new(0);

    public RecordingOpenView()
    {
        InitializeComponent();
        RecordingClock.Attach(this, Timer);

        // The stop disc. Glyph drawn at true size (Stretch.None), never stretched to its box;
        // the fill arrives as a resource KEY so the disc repaints on a theme switch.
        var stop = PillParts.RoundButton(
            PillParts.StopGlyph(Brushes.White),
            "StopRed", "Stop recording and write the notes");
        stop.Click += OnStop;
        StopHost.Content = stop;

        // The disclosure: one chevron on a glass puck; the rotation tells you which way it goes.
        var chevron = PillParts.Outline(PillParts.ChevronDown(), "TextHi");
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        chevron.RenderTransform = _chevronTurn;
        var expand = PillParts.GlassPuck(chevron, "Show what Toki is hearing");
        expand.Click += OnToggleExpand;
        ExpandHost.Content = expand;

        // The wave is fed by the REAL capture: max(you, them), so the other person talking moves it too.
        Wave.Meter = Audio.LevelMeter.Recording;

        IsVisibleChanged += (_, e) =>
        {
            var shown = (bool)e.NewValue;
            Wave.IsLive = shown;
            if (shown) return;
            // Collapsing to the peek always returns to the compact bar; never reopen onto the tall panel.
            SetExpanded(false, animate: false);
        };

        Loaded += (_, _) =>
        {
            App.LivePreview += OnLiveSegment;
            // Offer the prep card only when this meeting actually has one; a button that opens
            // nothing is worse than no button. Expanded form only.
            RefreshPrepButton();
        };
        Unloaded += (_, _) => { App.LivePreview -= OnLiveSegment; };
    }

    /// <summary>The card is the compact bar until the chevron opens the transcript.</summary>
    public double CardWidth => _expanded ? ExpandedWidth : BarWidth;

    void OnToggleExpand(object sender, RoutedEventArgs e) => SetExpanded(!_expanded, animate: true);

    void SetExpanded(bool on, bool animate)
    {
        if (_expanded == on) return;
        _expanded = on;
        LiveHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Timer.FontSize = on ? 12 : 13;
        RefreshPrepButton();
        TurnChevron(on, animate);
        if (animate) (Window.GetWindow(this) as PillWindow)?.GrowToContent();
    }

    // Points down to open, up to close; Motion.Move because the glyph turns in place.
    void TurnChevron(bool up, bool animate)
    {
        var to = up ? 180.0 : 0.0;
        if (!animate || App.Headless)
        {
            _chevronTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            _chevronTurn.Angle = to;
            return;
        }
        _chevronTurn.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(to, Theme.Motion.Dur(200)) { EasingFunction = Theme.Motion.Move });
    }

    void RefreshPrepButton() =>
        PrepButton.Visibility = _expanded && Calendar.PrepWatcher.Armed ? Visibility.Visible : Visibility.Collapsed;

    // A finalized line, arriving as Toki hears it; newest stacks at the bottom.
    void OnLiveSegment(TranscriptSegment s)
    {
        var text = s.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        var row = TranscriptRow(s, text);
        LiveLines.Children.Add(row);
        while (LiveLines.Children.Count > MaxLiveLines) LiveLines.Children.RemoveAt(0);
        row.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, Theme.Motion.Dur(Theme.Motion.Enter))
        {
            EasingFunction = Theme.Motion.EaseOut,   // arriving content decelerates (central motion token)
        });
    }

    /// <summary>One transcript line: a speaker mark, then text held to 60% opacity so the bar
    /// above stays the loud thing.</summary>
    static FrameworkElement TranscriptRow(TranscriptSegment s, string text)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FrameworkElement mark = s.Tag is { Length: > 0 } name
            ? SpeakerDisc(name)                                          // a real person, once diarisation names one
            : StreamChip(s.Speaker == Speaker.You ? "YOU" : "THEM");     // the stream, which is always knowable
        mark.Margin = new Thickness(0, 1, 7, 0);
        mark.VerticalAlignment = VerticalAlignment.Top;

        var tb = new TextBlock
        {
            Text = text, FontSize = 12, LineHeight = 14, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Opacity = 0.6,
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
            TextWrapping = TextWrapping.Wrap, MaxHeight = 28, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Resource reference, not a captured brush - see PillParts.Themed.
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextHi");
        Grid.SetColumn(tb, 1);
        row.Children.Add(mark);
        row.Children.Add(tb);
        return row;
    }

    // "YOU" / "THEM" stream capsule.
    static FrameworkElement StreamChip(string label)
    {
        var tb = new TextBlock
        {
            Text = label, FontSize = 8, FontWeight = FontWeights.SemiBold,
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextHi");
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4), MinWidth = 38, Height = 15,
            Padding = new Thickness(5, 0, 5, 0), Opacity = 0.75, Child = tb,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "GlassCapsule");
        return chip;
    }

    // A named speaker: a disc with the initial, tinted from the name (never fetched, always derived).
    static FrameworkElement SpeakerDisc(string name)
    {
        var initial = char.ToUpperInvariant(name.Trim()[0]).ToString();
        // DiscLabel pins its own line metrics; a bare TextBlock centres its LINE BOX, which leaves
        // a capital's ink sitting high, worse wherever a line height is inherited.
        var letter = PillParts.DiscLabel(initial, 10, FontWeights.SemiBold);
        letter.Foreground = Brushes.White;
        return new Border
        {
            Background = PillParts.SpeakerTint(name), CornerRadius = new CornerRadius(99),
            Width = 16, Height = 16,
            Child = letter,
        };
    }

    /// <summary>Render-harness seam: open the transcript form and fill it with real segments,
    /// so the expanded form can be rendered headlessly.</summary>
    public void DebugExpand(System.Collections.Generic.IEnumerable<TranscriptSegment> lines)
    {
        SetExpanded(true, animate: false);
        foreach (var s in lines) LiveLines.Children.Add(TranscriptRow(s, s.Text.Trim()));
    }

    void OnCollapse(object sender, MouseButtonEventArgs e) => App.State.TryGo(PillState.RecordingBar);

    void OnStop(object sender, RoutedEventArgs e) => App.StopRecording();

    // Open the prep card mid-meeting; recording is untouched and Back returns here.
    void OnPrep(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.PrepCard);
}
