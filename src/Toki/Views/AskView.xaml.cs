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
/// The push-to-talk voice notch — a minimal, chrome-light conversation. No title bar (the mascot is the
/// identity); assistant answers are PLAIN TEXT on the surface, not chat bubbles; only the user's line is
/// differentiated (dim, with a small pink tick). Grows to content and scrolls past a cap — never opens big.
/// A scrollable THREAD keeps the whole session until the notch closes. Driven by <see cref="App"/> via
/// <see cref="Instance"/>.
/// </summary>
public partial class AskView : UserControl
{
    public static AskView? Instance { get; private set; }

    static FontFamily Ui => (FontFamily)Application.Current.FindResource("UiFont");

    readonly List<ScaleTransform> _bars = new();
    TranslateTransform? _loaderSlide;
    DispatcherTimer? _autoHide;
    TextBox? _curAnswer;
    StackPanel? _curStack;
    StackPanel? _curMoreHost;
    string? _moreQuestion;

    public AskView()
    {
        InitializeComponent();
        Instance = this;
        BuildWave();
        BuildLoader();
    }

    public void NewSession()
    {
        Thread.Children.Clear();
        _curAnswer = null; _curMoreHost = null; _moreQuestion = null;
        StatusText.Text = "";
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
        Thread.Children.Add(UserRow(question));
        var (row, answer, moreHost) = AnswerRow();
        Thread.Children.Add(row);
        _curAnswer = answer; _curStack = row as StackPanel; _curMoreHost = moreHost; _moreQuestion = null;
        ThreadScroll.ScrollToEnd();
        App.GrowNotch();
    }

    /// <summary>Show which note this answer is grounded on ("About: MR Rate Target…") — screen-aware
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
        App.GrowNotch();
    }

    public void Token(string t) { if (_curAnswer is not null) { _curAnswer.AppendText(t); ThreadScroll.ScrollToEnd(); } }

    public void SetAnswerText(string full)
    {
        if (_curAnswer is null) return;
        _curAnswer.Text = full ?? "";
        ThreadScroll.ScrollToEnd();
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
        ThreadScroll.ScrollToEnd();
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
        => TokiSay("Ask about your meetings — decisions, tasks, who said what. Or say “take me to the library”, "
            + "“open settings”, “new note”, or “go home”.");

    // A standalone Toki line (no preceding user turn): commands, errors, help.
    void TokiSay(string message, bool dim = false)
    {
        StatusText.Text = "";
        HideStrip();
        var (row, answer, _) = AnswerRow(dim);
        answer.Text = message;
        Thread.Children.Add(row);
        ThreadScroll.ScrollToEnd();
        App.GrowNotch();
    }

    void HideStrip()
    {
        StatusStrip.Visibility = Visibility.Collapsed;
        WaveHost.Visibility = Visibility.Collapsed; StopWave();
        LoaderHost.Visibility = Visibility.Collapsed; StopLoader();
    }

    // ── plain-text turns (no bubbles) ──

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
            FocusVisualStyle = null,   // no focus outline — it reads as plain text, not a form field
            SelectionOpacity = 0.35,
        };
        tb.SetResourceReference(Control.ForegroundProperty, fgKey);
        tb.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.SelectionBrushProperty, "Pink");
        return tb;
    }

    // Progressive disclosure as an inline LINK (not a pill button) — "See full answer in Library →".
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
        App.State.ForceForDemo(PillState.Hidden);
        if (!string.IsNullOrEmpty(q)) LibraryWindow.AskInLibrary(q);
    }

    void OnClose(object sender, RoutedEventArgs e) => App.State.ForceForDemo(PillState.Hidden);

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
        if (App.State.Current == PillState.Ask) App.State.ForceForDemo(PillState.Hidden);
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

    void BuildLoader()
    {
        var track = new Grid { Width = 170, Height = 4, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 3) };
        var bg = new Border { Height = 4, CornerRadius = new CornerRadius(2) };
        bg.SetResourceReference(Border.BackgroundProperty, "Chip");
        _loaderSlide = new TranslateTransform();
        var dot = new Border { Width = 50, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)FindResource("Pink"), RenderTransform = _loaderSlide };
        track.Children.Add(bg);
        track.Children.Add(dot);
        LoaderHost.Child = track;
    }

    void StartLoader()
    {
        if (App.Headless || _loaderSlide is null) return;
        _loaderSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 170 - 50, TimeSpan.FromMilliseconds(820))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    void StopLoader() => _loaderSlide?.BeginAnimation(TranslateTransform.XProperty, null);
}
