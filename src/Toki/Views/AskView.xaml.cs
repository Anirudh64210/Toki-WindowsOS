using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Toki.State;

namespace Toki.Views;

/// <summary>
/// The push-to-talk voice notch. Assistant answers are plain text (no chat bubbles); only the user's
/// line is differentiated. Grows to content and scrolls past a cap; a scrollable thread keeps the
/// session until the notch closes. Driven by <see cref="App"/> via <see cref="Instance"/>.
/// </summary>
public partial class AskView : UserControl
{
    public static AskView? Instance { get; private set; }

    static FontFamily Ui => (FontFamily)Application.Current.FindResource("UiFont");

    readonly List<Border> _thinkDots = new();   // the 3 "thinking" dots
    DispatcherTimer? _autoHide;
    TextBox? _curAnswer;
    StackPanel? _curStack;
    StackPanel? _curMoreHost;
    string? _moreQuestion;

    // Stick-to-bottom: auto-follow the newest text ONLY while parked at the bottom, so a streaming
    // answer never yanks the reader back down. A user-initiated turn re-pins to the bottom.
    bool _stick = true;

    public AskView()
    {
        InitializeComponent();
        Instance = this;
        // Push-to-talk's own mic, never the meeting's: asking a question mid-recording must not make
        // this wave dance to the call.
        Wave.Meter = Audio.LevelMeter.Voice;
        BuildLoader();
        ThreadScroll.ScrollChanged += OnThreadScroll;
    }

    // Re-evaluate "pinned to bottom" ONLY on a user-driven scroll: content growth changes ExtentHeight
    // and must NOT flip the flag (a user wheel/drag has ExtentHeightChange == 0). The ~120px threshold
    // is generous on purpose - a tight one un-anchors the reader on sub-pixel extent wobble.
    void OnThreadScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && (e.VerticalChange != 0 || e.ViewportHeightChange != 0))
            _stick = ThreadScroll.VerticalOffset >= ThreadScroll.ScrollableHeight - 120;
        // The "↓ Latest" pill shows whenever you've drifted up from the bottom and there's more below.
        ShowJumpPill(!_stick && ThreadScroll.ScrollableHeight > 12);
    }

    bool _pillShown;
    void ShowJumpPill(bool show)
    {
        if (show == _pillShown) return;
        _pillShown = show;
        if (App.Headless) { JumpPill.Visibility = show ? Visibility.Visible : Visibility.Collapsed; JumpPill.Opacity = show ? 1 : 0; return; }
        if (show) JumpPill.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(show ? 1 : 0, Theme.Motion.Dur(Theme.Motion.Micro)) { EasingFunction = Theme.Motion.EaseOut };
        if (!show) anim.Completed += (_, _) => { if (!_pillShown) JumpPill.Visibility = Visibility.Collapsed; };
        JumpPill.BeginAnimation(OpacityProperty, anim);
    }

    void OnJumpToLatest(object sender, RoutedEventArgs e)
    {
        _stick = true;
        ThreadScroll.ScrollToEnd();
        ShowJumpPill(false);
    }

    // Follow the newest content only if parked at the bottom. Deferred to Background priority so it
    // runs AFTER the appended text is measured into the extent; otherwise ScrollToEnd lands a line high.
    void Follow()
    {
        if (!_stick) return;
        Dispatcher.BeginInvoke(new Action(() => { if (_stick) ThreadScroll.ScrollToEnd(); }), DispatcherPriority.Background);
    }

    // A new turn the USER initiated (a question, a command/help reply) always jumps to the bottom.
    void PinToEnd() { _stick = true; Follow(); }

    public void NewSession()
    {
        Thread.Children.Clear();
        _curAnswer = null; _curMoreHost = null; _moreQuestion = null;
        StatusText.Text = "";
        _stick = true;
    }

    // ── phases ──

    public void Listening()
    {
        _autoHide?.Stop();
        StatusText.Text = "Listening…";
        HintText.Text = "Hold Ctrl + Alt + Space and speak";
        HintText.Visibility = Visibility.Visible;
        StatusStrip.Visibility = Visibility.Visible;
        Wave.Visibility = Visibility.Visible; Wave.IsLive = true;
        LoaderHost.Visibility = Visibility.Collapsed; StopLoader();
        if (Toki.IsVisible) Toki.DropIn();
        App.GrowNotch();
    }

    public void Thinking()
    {
        StatusText.Text = "Thinking…";
        HintText.Visibility = Visibility.Collapsed;
        StatusStrip.Visibility = Visibility.Visible;
        Wave.IsLive = false; Wave.Visibility = Visibility.Collapsed;
        LoaderHost.Visibility = Visibility.Visible; StartLoader();
        App.GrowNotch();
    }

    public void Answer(string question)
    {
        StatusText.Text = "";
        HideStrip();
        var userRow = UserRow(question); Thread.Children.Add(userRow); Arrive(userRow);
        var (row, answer, moreHost) = AnswerRow();
        Thread.Children.Add(row); Arrive(row);
        _curAnswer = answer; _curStack = row as StackPanel; _curMoreHost = moreHost; _moreQuestion = null;
        PinToEnd();   // a new question → jump to the newest turn
        App.GrowNotch();
    }

    /// <summary>Show which note this answer is grounded on. Screen-aware answers must SHOW their
    /// scope, or a wrong grounding is invisible. One dim line above the answer, once per turn.</summary>
    public void Scope(string title)
    {
        if (_curStack is null || string.IsNullOrWhiteSpace(title)) return;
        if (_curStack.Children.Count > 0 && _curStack.Children[0] is TextBlock { Tag: "scope" }) return;
        var t = new TextBlock
        {
            Text = $"About: {title}", FontFamily = Ui, FontSize = 10.5,
            Margin = new Thickness(0, 0, 0, 3), Tag = "scope", TextTrimming = TextTrimming.CharacterEllipsis,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        _curStack.Children.Insert(0, t);
        Follow();
        App.GrowNotch();
    }

    public void Token(string t) { if (_curAnswer is not null) { _curAnswer.AppendText(t); Follow(); } }

    // Streaming update: mutate the text IN PLACE and scroll, but do NOT resize the notch. Growing the
    // window on every ~70ms patch was the flicker (research: hold the window, stream into a scrolling
    // viewport; animate size only on state changes). The caller grows on a coarse throttle + at Final.
    public void SetAnswerText(string full)
    {
        if (_curAnswer is null) return;
        _curAnswer.Text = full ?? "";
        Follow();
    }

    public void Final(string text, bool tooLong = false, string? question = null)
    {
        if (_curAnswer is not null) _curAnswer.Text = text ?? "";
        _moreQuestion = tooLong ? question : null;
        if (_curMoreHost is not null)
        {
            _curMoreHost.Children.Clear();
            if (_moreQuestion is { Length: > 0 }) _curMoreHost.Children.Add(MoreLink());
        }
        Follow();
        App.GrowNotch();
        ArmAutoHide();
    }

    public void Failed(string message) { TokiSay(message, dim: true); ArmAutoHide(4000); }

    public void Command(string message)
    {
        _autoHide?.Stop();
        TokiSay(message);
        if (Toki.IsVisible) Toki.React();
        ArmAutoHide(2200);
    }

    public void Help()
        => TokiSay("Ask about your meetings - decisions, tasks, who said what. Or say “schedule a call with Ana "
            + "tomorrow at 3”, “take me to the library”, “open settings”, “new note”, or “go home”.");

    string? _lastSayText;
    FrameworkElement? _lastSayRow;

    // A standalone Toki line (no preceding user turn): commands, errors, help.
    void TokiSay(string message, bool dim = false)
    {
        StatusText.Text = "";
        HideStrip();
        // DEDUPE a repeated identical line. A run of empty captures used to stack three, four, five
        // copies of "I didn't catch that" into a wall; now the same line, if it is still the newest
        // row, just PULSES so the user sees it re-fire without the clutter.
        if (_lastSayText == message && _lastSayRow is not null
            && Thread.Children.Count > 0 && ReferenceEquals(Thread.Children[^1], _lastSayRow))
        {
            Pulse(_lastSayRow);
            PinToEnd();
            return;
        }
        var (row, answer, _) = AnswerRow(dim);
        answer.Text = message;
        Thread.Children.Add(row); Arrive(row);
        _lastSayText = message; _lastSayRow = row;
        PinToEnd();   // a fresh Toki line (command / help / error) → jump to it
        App.GrowNotch();
    }

    // A quick attention blink for a line that repeated - lighter than a full arrive, no layout move.
    static void Pulse(FrameworkElement el)
    {
        if (App.Headless) return;
        el.BeginAnimation(OpacityProperty, null);
        var blink = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(0.35, KeyTime.FromPercent(0)));
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(Theme.Motion.Dur(Theme.Motion.Micro).TimeSpan)));
        el.BeginAnimation(OpacityProperty, blink);
    }

    void HideStrip()
    {
        StatusStrip.Visibility = Visibility.Collapsed;
        Wave.IsLive = false; Wave.Visibility = Visibility.Collapsed;
        LoaderHost.Visibility = Visibility.Collapsed; StopLoader();
    }

    // ── plain-text turns (no bubbles) ──

    // The one standard "arrive" motion. Only NEW rows animate - existing history never moves.
    // Render transform only, so layout is untouched.
    static void Arrive(FrameworkElement el)
    {
        if (App.Headless) return;
        var slide = new TranslateTransform(0, Theme.Motion.EnterRise);
        el.RenderTransform = slide;
        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Theme.Motion.Dur(Theme.Motion.Enter)) { EasingFunction = Theme.Motion.EaseOut });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Theme.Motion.Dur(Theme.Motion.Enter)) { EasingFunction = Theme.Motion.EaseOut });
    }

    FrameworkElement UserRow(string text)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tick = new Border { Width = 3, Height = 13, CornerRadius = new CornerRadius(1.5), Background = (Brush)FindResource("Pink"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 9, 0) };
        var t = SelText(text, "TextDim", 12);
        Grid.SetColumn(t, 1);
        g.Children.Add(tick);
        g.Children.Add(t);
        return g;
    }

    (FrameworkElement row, TextBox answer, StackPanel moreHost) AnswerRow(bool dim = false)
    {
        var answer = SelText("", dim ? "TextDim" : "TextSoft", 13);
        var moreHost = new StackPanel();
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };   // gap to the next turn
        stack.Children.Add(answer);
        stack.Children.Add(moreHost);
        return (stack, answer, moreHost);
    }

    TextBox SelText(string text, string fgKey, double fs)
    {
        var tb = new TextBox
        {
            Text = text, IsReadOnly = true, IsReadOnlyCaretVisible = false, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, FontFamily = Ui, FontSize = fs, TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(0), Cursor = Cursors.IBeam, IsTabStop = false,
            FocusVisualStyle = null,   // no focus outline - it reads as plain text, not a form field
            SelectionOpacity = 0.35,
        };
        tb.SetResourceReference(Control.ForegroundProperty, fgKey);
        tb.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.SelectionBrushProperty, "Pink");
        return tb;
    }

    // Progressive disclosure as an inline link: "See full answer in Library →".
    FrameworkElement MoreLink()
    {
        var link = new TextBlock { Text = "See full answer in Library  →", FontSize = 12, FontFamily = Ui, Cursor = Cursors.Hand, Margin = new Thickness(0, 7, 0, 1) };
        link.SetResourceReference(TextBlock.ForegroundProperty, "Pink");
        link.MouseLeftButtonUp += (_, _) => OnMore();
        return link;
    }

    void OnMore()
    {
        var q = _moreQuestion;
        App.CloseOverlay();   // recording-aware: a live session keeps its bar
        if (!string.IsNullOrEmpty(q)) LibraryWindow.AskInLibrary(q);
    }

    void OnClose(object sender, RoutedEventArgs e) => App.CloseOverlay();

    void ArmAutoHide(int ms = 30000)
    {
        _autoHide ??= new DispatcherTimer();
        _autoHide.Stop();
        _autoHide.Interval = TimeSpan.FromMilliseconds(ms);
        _autoHide.Tick -= AutoHideTick;
        _autoHide.Tick += AutoHideTick;
        _autoHide.Start();
    }

    void AutoHideTick(object? sender, EventArgs e)
    {
        _autoHide?.Stop();
        if (App.State.Current == PillState.Ask) App.CloseOverlay();
    }

    // ── thinking loader ──

    // Three pulsing dots, staggered so the pulse travels across them.
    void BuildLoader()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 8, 0, 3) };
        _thinkDots.Clear();
        for (var i = 0; i < 3; i++)
        {
            var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0), Background = (Brush)FindResource("Pink"), Opacity = 0.3 };
            _thinkDots.Add(dot);
            row.Children.Add(dot);
        }
        LoaderHost.Child = row;
    }

    void StartLoader()
    {
        if (App.Headless) return;
        for (var i = 0; i < _thinkDots.Count; i++)
            _thinkDots[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(560))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 170), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
    }

    void StopLoader() { foreach (var d in _thinkDots) { d.BeginAnimation(OpacityProperty, null); d.Opacity = 0.3; } }
}
