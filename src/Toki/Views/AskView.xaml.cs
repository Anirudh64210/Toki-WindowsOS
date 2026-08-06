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
/// The push-to-talk voice notch - a minimal, chrome-light conversation. No title bar (the mascot is the
/// identity); assistant answers are PLAIN TEXT on the surface, not chat bubbles; only the user's line is
/// differentiated (dim, with a small pink tick). Grows to content and scrolls past a cap - never opens big.
/// A scrollable THREAD keeps the whole session until the notch closes. Driven by <see cref="App"/> via
/// <see cref="Instance"/>.
/// </summary>
public partial class AskView : UserControl
{
    public static AskView? Instance { get; private set; }

    static FontFamily Ui => (FontFamily)Application.Current.FindResource("UiFont");

    readonly List<ScaleTransform> _bars = new();
    readonly List<Border> _thinkDots = new();   // the 3 "thinking" dots
    DispatcherTimer? _autoHide;
    TextBox? _curAnswer;
    StackPanel? _curStack;
    StackPanel? _curMoreHost;
    string? _moreQuestion;

    // Stick-to-bottom: the thread auto-follows the newest text ONLY while you're already parked at the
    // bottom. The moment you scroll UP to read earlier turns, following stops - so a streaming answer (or
    // a new token) can never yank you back down (the "scroll fights me" glitch). A new turn you initiate
    // (asking a question, a command reply) re-pins to the bottom, because you want to see the new thing.
    bool _stick = true;

    public AskView()
    {
        InitializeComponent();
        Instance = this;
        BuildWave();
        BuildLoader();
        ThreadScroll.ScrollChanged += OnThreadScroll;
    }

    // Re-evaluate "am I pinned to the bottom?" ONLY on a user-driven scroll. Content growth (streaming
    // tokens, an inserted scope line) changes ExtentHeight - we must ignore those, or every token would
    // reset the flag and defeat the point. A user wheel/drag has ExtentHeightChange == 0.
    void OnThreadScroll(object sender, ScrollChangedEventArgs e)
    {
        // Re-evaluate "pinned to bottom" only on a USER scroll (content growth changes ExtentHeight, which
        // must NOT flip the flag). ~120px threshold (research §3a: 8px was hair-trigger - any sub-pixel
        // extent wobble un-anchored the reader).
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

    void OnJumpToLatest(object sender, MouseButtonEventArgs e)
    {
        _stick = true;
        ThreadScroll.ScrollToEnd();
        ShowJumpPill(false);
    }

    // Follow the newest content only if the user is parked at the bottom (see _stick). Deferred to
    // Background priority so it runs AFTER the just-appended text has been measured into the extent -
    // otherwise ScrollToEnd targets the pre-append (too-short) extent and lands a line high.
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
        WaveHost.Visibility = Visibility.Visible; StartWave();
        LoaderHost.Visibility = Visibility.Collapsed; StopLoader();
        if (Toki.IsVisible) Toki.DropIn();
        App.GrowNotch();
    }

    public void Thinking()
    {
        StatusText.Text = "Thinking…";
        HintText.Visibility = Visibility.Collapsed;
        StatusStrip.Visibility = Visibility.Visible;
        WaveHost.Visibility = Visibility.Collapsed; StopWave();
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

    /// <summary>Show which note this answer is grounded on ("About: MR Rate Target…") - screen-aware
    /// answers must SHOW their scope, or a wrong grounding is invisible and trust dies. One line,
    /// dim, above the streaming answer; once per turn.</summary>
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

    public void SetAnswerText(string full)
    {
        if (_curAnswer is null) return;
        _curAnswer.Text = full ?? "";
        Follow();
        App.GrowNotch();
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

    // A standalone Toki line (no preceding user turn): commands, errors, help.
    void TokiSay(string message, bool dim = false)
    {
        StatusText.Text = "";
        HideStrip();
        var (row, answer, _) = AnswerRow(dim);
        answer.Text = message;
        Thread.Children.Add(row); Arrive(row);
        PinToEnd();   // a fresh Toki line (command / help / error) → jump to it
        App.GrowNotch();
    }

    void HideStrip()
    {
        StatusStrip.Visibility = Visibility.Collapsed;
        WaveHost.Visibility = Visibility.Collapsed; StopWave();
        LoaderHost.Visibility = Visibility.Collapsed; StopLoader();
    }

    // ── plain-text turns (no bubbles) ──

    // The one standard "arrive" motion for new content: fade up a few px, ease-out (Motion tokens). Only
    // NEW rows animate - existing history never moves. Render transform only, so layout is untouched.
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

    // Progressive disclosure as an inline LINK (not a pill button) - "See full answer in Library →".
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

    // ── listening waveform ──

    void BuildWave()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < 7; i++)
        {
            var st = new ScaleTransform(1, 0.3) { CenterX = 2, CenterY = 22 };
            var bar = new Border { Width = 4, Height = 22, CornerRadius = new CornerRadius(2), Background = (Brush)FindResource("Pink"), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransform = st };
            _bars.Add(st);
            row.Children.Add(bar);
        }
        WaveHost.Child = row;
    }

    void StartWave()
    {
        if (App.Headless) return;
        for (var i = 0; i < _bars.Count; i++)
            _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.28, 1.0, TimeSpan.FromMilliseconds(360 + i * 40))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 70), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
    }

    void StopWave() { foreach (var st in _bars) { st.BeginAnimation(ScaleTransform.ScaleYProperty, null); st.ScaleY = 0.3; } }

    // ── thinking loader ──

    // Three pulsing dots - the calm "thinking" indicator (Glass/ChatGPT pattern), staggered so the pulse
    // travels across them. Replaces the old sliding bar.
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
