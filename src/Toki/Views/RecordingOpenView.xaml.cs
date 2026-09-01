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

        // The ghost toggle: hide/show Toki in screen captures mid-recording, so a meeting that was
        // started with ghost mode off can be fixed in place. The puck reflects the current state.
        var ghost = PillParts.GlassPuck(GhostGlyph(), GhostTip());
        ghost.Click += OnToggleGhost;
        GhostHost.Content = ghost;

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
            App.LivePreviewPartial += OnLivePartial;
            // Offer the prep card only when this meeting actually has one; a button that opens
            // nothing is worse than no button. Expanded form only.
            RefreshPrepButton();
        };
        Unloaded += (_, _) =>
        {
            App.LivePreview -= OnLiveSegment;
            App.LivePreviewPartial -= OnLivePartial;
            _partials.Clear();
        };
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

    // The streaming preview's per-speaker IN-PROGRESS rows (words as they are spoken). Each commits to
    // a finalized line when its utterance ends. Empty when the slow Parakeet-window fallback is in use.
    readonly System.Collections.Generic.Dictionary<Speaker, (Grid Row, TextBlock Tb)> _partials = new();
    bool _livePulsing;

    const double InProgressOpacity = 0.4;   // dimmer than a committed line (0.6) - "still being heard"

    // A growing in-progress line from the streaming preview: update this speaker's partial row, creating
    // it (at the bottom) on first sight.
    void OnLivePartial(Speaker sp, string text)
    {
        ShowLive();
        text = text?.Trim() ?? "";
        if (text.Length == 0) return;
        if (!_partials.TryGetValue(sp, out var p))
        {
            var (row, tb) = BuildRow(new TranscriptSegment(sp, 0, 0, text), text, InProgressOpacity);
            LiveLines.Children.Add(row);
            Trim();
            p = (row, tb);
            _partials[sp] = p;
            row.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, Theme.Motion.Dur(Theme.Motion.Enter))
            { EasingFunction = Theme.Motion.EaseOut });
        }
        p.Tb.Text = text;
    }

    // A finalized line, arriving as Toki hears it; newest stacks at the bottom. If a streaming partial
    // for this speaker is in flight, COMMIT it in place (it becomes the accurate line) rather than
    // stacking a duplicate; otherwise (Parakeet fallback) add a fresh row.
    void OnLiveSegment(TranscriptSegment s)
    {
        var text = s.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (_partials.TryGetValue(s.Speaker, out var p))
        {
            p.Tb.Text = text;
            p.Tb.Opacity = 0.6;   // committed: matches a normal finalized line
            _partials.Remove(s.Speaker);
            Trim();
            return;
        }
        var row = TranscriptRow(s, text);
        LiveLines.Children.Add(row);
        Trim();
        row.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, Theme.Motion.Dur(Theme.Motion.Enter))
        {
            EasingFunction = Theme.Motion.EaseOut,   // arriving content decelerates (central motion token)
        });
    }

    void Trim()
    {
        while (LiveLines.Children.Count > MaxLiveLines)
        {
            var removed = LiveLines.Children[0];
            LiveLines.Children.RemoveAt(0);
            foreach (var kv in _partials.Where(k => ReferenceEquals(k.Value.Row, removed)).ToList())
                _partials.Remove(kv.Key);   // its row scrolled off the top; drop the stale handle
        }
    }

    // Reveal the "Live" readiness cue and start the dot pulsing, once, when the preview first speaks.
    void ShowLive()
    {
        if (_livePulsing) return;
        _livePulsing = true;
        LiveHeader.Visibility = Visibility.Visible;
        if (App.Headless) return;
        LiveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(750))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    /// <summary>One transcript line: a speaker mark, then text held to 60% opacity so the bar
    /// above stays the loud thing.</summary>
    static FrameworkElement TranscriptRow(TranscriptSegment s, string text) => BuildRow(s, text, 0.6).Row;

    /// <summary>Build a transcript line and return both the row and its text block, so a streaming
    /// PARTIAL line can keep updating the same text block (at <paramref name="opacity"/>) until it
    /// commits.</summary>
    static (Grid Row, TextBlock Tb) BuildRow(TranscriptSegment s, string text, double opacity)
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
            Opacity = opacity,
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
            TextWrapping = TextWrapping.Wrap, MaxHeight = 28, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Resource reference, not a captured brush - see PillParts.Themed.
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextHi");
        Grid.SetColumn(tb, 1);
        row.Children.Add(mark);
        row.Children.Add(tb);
        return (row, tb);
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
        letter.Fill = Brushes.White;
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

    // The SAME icon as Settings (one design across every state): a video camera, struck through when
    // Toki is hidden from screen recordings (ghost on). Ghost on == hidden == GhostDisabled false.
    static FrameworkElement GhostGlyph()
    {
        var hidden = !App.GhostDisabled;   // ghost on = hidden from capture = camera struck through
        var cam = new TextBlock
        {
            Text = ((char)0xE714).ToString(),   // Fluent: Video
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        cam.SetResourceReference(TextBlock.ForegroundProperty, "TextHi");
        var strike = new System.Windows.Shapes.Line
        {
            X1 = 4, Y1 = 19, X2 = 20, Y2 = 5, StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Visibility = hidden ? Visibility.Visible : Visibility.Collapsed,
        };
        strike.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextHi");
        var box = new Grid { Width = 24, Height = 24 };
        box.Children.Add(cam);
        box.Children.Add(strike);
        return box;
    }

    static string GhostTip() => App.GhostDisabled
        ? "Toki shows in screen recordings. Click to hide it (ghost mode)."
        : "Toki is hidden from screen recordings. Click to show it.";

    void OnToggleGhost(object sender, RoutedEventArgs e)
    {
        // Flip: if visible now (GhostDisabled), turn ghost ON; else turn it OFF. SetGhostMode persists
        // and re-asserts WDA_EXCLUDEFROMCAPTURE live, no restart.
        App.SetGhostMode(on: App.GhostDisabled);
        if (sender is Button b) { b.Content = GhostGlyph(); b.ToolTip = GhostTip(); }
    }

    void OnCollapse(object sender, MouseButtonEventArgs e) => App.State.TryGo(PillState.RecordingBar);

    void OnStop(object sender, RoutedEventArgs e) => App.StopRecording();

    // Open the prep card mid-meeting; recording is untouched and Back returns here.
    void OnPrep(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.PrepCard);
}
