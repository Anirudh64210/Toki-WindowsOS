using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Toki.State;
using Toki.Storage;
using Toki.Views;

namespace Toki;

/// <summary>
/// Resizable window showing the whole Library (list) beside the selected note's
/// full content — the "maximise" target and the Home/tray Library button. Manual
/// notes are editable here with an explicit Save; transcripts have a Copy button.
/// </summary>
public partial class LibraryWindow : Window
{
    static LibraryWindow? _open;   // single instance
    long? _selected;

    // Manual notes auto-save as you type (debounced) — no static Save/Cancel buttons.
    readonly System.Windows.Threading.DispatcherTimer _manualDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    Action? _manualFlush; // saves the currently-open manual note; null when a meeting is shown

    // The list/detail is only rebuilt when the data actually changed. Between opens the
    // window is kept alive (Hidden), fully rendered — so a plain re-open is just Show(),
    // which is instant (like WisprFlow keeping its window warm). MarkDirty() is called when
    // something external changes the data (a recording saves a meeting, a note is created).
    static bool _dirty = true;
    public static void MarkDirty() => _dirty = true;

    /// <summary>
    /// Open the Library — must feel INSTANT. The window is created once and kept warm
    /// (Hidden between opens, never destroyed), and is pre-warmed off-screen at startup
    /// (<see cref="Warm"/>), so a click never pays window-create + full-layout cost. That
    /// slow, visible "the window builds while the pill is still up, then the pill closes"
    /// handoff was the clunk — a warm window just appears.
    /// </summary>
    public static void ShowLibrary(long? focusMeetingId = null, Rect? fromRect = null)
    {
        EnsureCreated();
        var w = _open!;
        // Rebuild only when data changed (or a specific note was requested); otherwise the
        // already-rendered content is reused and opening is just Show() → instant.
        if (_dirty || focusMeetingId is not null) { w.Reload(focusMeetingId); _dirty = false; }
        if (!w.IsVisible) { w._originRect = fromRect; w.CenterOnWorkArea(); w.Show(); w.PlayOpen(fromRect); }
        // A specific note → open on the Library page showing it; otherwise the Ask landing (fresh greeting).
        w.ShowPage(focusMeetingId is not null ? "library" : "ask", animate: false);
        w.Activate();
    }

    /// <summary>Dev: render the hub's three pages to PNGs (Library window isn't in the pill harness).</summary>
    internal static void RenderShots(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var latest = App.Store.List().FirstOrDefault(x => x.Kind == MeetingKind.Meeting);
            if (latest is not null) App.CurrentMeeting = App.Store.Get(latest.Id);
        }
        catch { }
        var w = new LibraryWindow { Left = -32000, Top = -32000, Width = 960, Height = 640, ShowActivated = false, ShowInTaskbar = false };
        w.Show();
        w.Reload(App.CurrentMeeting?.Id);
        foreach (var page in new[] { "ask", "library", "templates" })
        {
            w.ShowPage(page, animate: false);
            w.UpdateLayout();
            Dev.RenderHarness.SaveVisual(w.RootGrid, System.IO.Path.Combine(dir, $"hub-{page}.png"));
        }

        // Button-centering calibration: the real buttons on a dark surface, big, so vertical
        // centering of the label (and the Back chevron) can be judged precisely.
        var probe = new Border
        {
            Background = (Brush)w.FindResource("Surface"), Padding = new Thickness(28), Width = 460,
        };
        var col = new StackPanel();
        // Overlay a 1px true-center line across each button so vertical centering can be MEASURED,
        // not eyeballed: the label's optical centre should sit on the red line.
        void Add(FrameworkElement b)
        {
            b.HorizontalAlignment = HorizontalAlignment.Left;
            var g = new Grid { Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
            g.Children.Add(b);
            g.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(180, 255, 60, 60)), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
            col.Children.Add(g);
        }
        Add(new Button { Content = "Copy", Style = (Style)w.FindResource("PillButton"), Padding = new Thickness(14, 7, 14, 7), FontSize = 11.5, MinWidth = 120 });
        Add(new Button { Content = "Delete", Style = (Style)w.FindResource("PillButton"), Foreground = (Brush)w.FindResource("Danger"), Padding = new Thickness(13, 6, 13, 6), FontSize = 11.5, MinWidth = 120 });
        Add(new Button { Content = "+  New note", Style = (Style)w.FindResource("WhiteButton"), Padding = new Thickness(0, 9, 0, 9), MinWidth = 220, HorizontalContentAlignment = HorizontalAlignment.Center });
        Add(w.BuildBackButtonProbe());
        probe.Child = col;
        probe.Measure(new Size(460, 400)); probe.Arrange(new Rect(0, 0, 460, 400)); probe.UpdateLayout();
        Dev.RenderHarness.SaveVisual(probe, System.IO.Path.Combine(dir, "buttons.png"));
        w.Close();
    }

    static void EnsureCreated()
    {
        if (_open is not null) return;
        _open = new LibraryWindow();
        _open.Closed += (_, _) => _open = null;   // if it ever really closes, drop the singleton
        // The OS close (X) button hides the window and hands back to the pill, instead of
        // destroying it — so the NEXT open stays instant too. But on real app exit, let it
        // close (else cancelling here would keep the last window alive and block shutdown).
        _open.Closing += (_, e) =>
        {
            if (App.Exiting || System.Windows.Application.Current.Dispatcher.HasShutdownStarted) return;
            e.Cancel = true;
            _open?.HideToPill();
        };
    }

    /// <summary>
    /// Pre-create + fully lay out the window OFF-SCREEN at startup, so even the first real
    /// open is instant (the expensive part is the first measure/arrange/render of the whole
    /// visual tree — we pay it once, off the critical path). No flash: it's shown far off
    /// every monitor and hidden again within the same tick, without activating or a taskbar
    /// button.
    /// </summary>
    public static void Warm()
    {
        if (_open is not null) return;
        EnsureCreated();
        var w = _open!;
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.Left = -32000; w.Top = -32000;
        w.Show();
        w.Reload();       // build the real content once, off-screen (pre-JIT + pre-render)
        _dirty = false;   // content is now current; first real open can skip the rebuild
        w.UpdateLayout();
        w.Hide();
        w.ShowInTaskbar = true;
        w.ShowActivated = true;
    }

    // The pill's on-screen rect when the Library was opened — so the window blooms FROM the notch
    // and shrinks back TO it (a connected morph), not from a disembodied centre.
    Rect? _originRect;
    bool _closing;

    /// <summary>
    /// Grow the window out of the notch: the content scales up from the pill's location with a
    /// fade — GPU-composited (a RenderTransform + element Opacity on RootGrid), smooth and cheap.
    /// NOT the window's Opacity (that forces a layered window + CPU re-blit → stutter). Runs after
    /// Show, non-blocking, so it adds nothing to the instant open cost.
    /// </summary>
    void PlayOpen(Rect? fromRect)
    {
        _closing = false;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(230));
        RootGrid.RenderTransformOrigin = OriginFrom(fromRect);
        var scale = new ScaleTransform(0.9, 0.9);
        RootGrid.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, dur) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, dur) { EasingFunction = ease });
        var fade = new DoubleAnimation(0.0, 1, new Duration(TimeSpan.FromMilliseconds(190))) { EasingFunction = ease };
        fade.Completed += (_, _) => { RootGrid.BeginAnimation(OpacityProperty, null); RootGrid.Opacity = 1; };
        RootGrid.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Shrink the window back toward the notch (mirror of <see cref="PlayOpen"/>) with a fade,
    /// then run <paramref name="done"/> — which hides the window and re-reveals the pill, so the two
    /// motions read as one continuous "back to Toki".</summary>
    void PlayClose(Action done)
    {
        if (_closing) return;   // ignore a double back/close while the retract is already playing
        _closing = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var dur = new Duration(TimeSpan.FromMilliseconds(160));
        RootGrid.RenderTransformOrigin = OriginFrom(_originRect);
        var scale = RootGrid.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        RootGrid.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.9, dur) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.9, dur) { EasingFunction = ease });
        var fade = new DoubleAnimation(RootGrid.Opacity, 0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            done();
            // Reset so the NEXT open starts clean (window is hidden now → no visible flash).
            RootGrid.BeginAnimation(OpacityProperty, null); RootGrid.Opacity = 1;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = 1;
            _closing = false;
        };
        RootGrid.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The pill's centre as a 0..1 origin within the window, so the scale grows/shrinks
    /// toward the notch. Clamped (the notch sits above the window → origin Y pins to the top edge).
    /// Falls back to centre when the pill's rect is unknown (e.g. opened from the tray).</summary>
    Point OriginFrom(Rect? r)
    {
        if (r is not Rect pr || pr.Width <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return new Point(0.5, 0.5);
        var ox = (pr.X + pr.Width / 2 - Left) / ActualWidth;
        var oy = (pr.Y + pr.Height / 2 - Top) / ActualHeight;
        return new Point(Math.Clamp(ox, 0, 1), Math.Clamp(oy, 0, 1));
    }

    /// <summary>Soften the OS maximise/restore snap: the frame resizes instantly (OS), but the content
    /// eases into the new size instead of hard-popping — so the size change reads smooth.</summary>
    void PlayStateSettle()
    {
        if (WindowState == WindowState.Minimized || !IsVisible) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(200));
        RootGrid.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(0.99, 0.99);
        RootGrid.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.99, 1, dur) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.99, 1, dur) { EasingFunction = ease });
        RootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, dur) { EasingFunction = ease });
    }

    void CenterOnWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        var ww = ActualWidth > 0 ? ActualWidth : Width;
        var wh = ActualHeight > 0 ? ActualHeight : Height;
        Left = wa.Left + (wa.Width - ww) / 2;
        Top = wa.Top + (wa.Height - wh) / 2;
    }

    /// <summary>Retract to the notch and bring the pill back — one continuous motion: the window
    /// shrinks toward the pill's spot, then hides (kept warm) as the pill re-reveals at the top edge.</summary>
    void HideToPill()
    {
        FlushManual();          // don't lose an in-progress manual-note edit on close
        // Restore first if maximised, so the retract animates from the normal bounds (not full-screen).
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        PlayClose(() =>
        {
            Hide();
            App.State.ForceForDemo(PillState.Home);   // pill blooms back in (composited RevealDown)
        });
    }

    /// <summary>Open the Library and start a fresh manual note in the editor.</summary>
    public static void ShowNewNote()
    {
        var id = App.Store.CreateManualNote("Untitled note", "");
        ShowLibrary(id);
    }

    LibraryWindow()
    {
        InitializeComponent();
        BackBtn.Content = BackButtonInner();   // composed chevron+label so the "‹" sits centered, not low
        SourceInitialized += (_, _) => StyleChrome();
        // Soften the maximise/restore snap (the "min-to-max sizer" clunk): ease the content into
        // the new size on every WindowState change instead of a hard pop.
        StateChanged += (_, _) => PlayStateSettle();
        // The list cards + note detail are built in code (FindResource brushes), so they don't
        // recolour on their own — rebuild them (and re-apply the title-bar theme) on a theme swap.
        // Code-built UI (list cards, note detail, nav, templates) captures brushes → rebuild on a theme swap.
        Toki.Theme.ThemeManager.ThemeChanged += () => Dispatcher.BeginInvoke(() => { StyleChrome(); BuildHub(); Reload(); });
        _manualDebounce.Tick += (_, _) => { _manualDebounce.Stop(); _manualFlush?.Invoke(); };
        // NOTE: no Loaded→Reload. Show() raises Loaded, and both Warm() and ShowLibrary()
        // already call Reload() explicitly — a Loaded handler here made every open rebuild
        // the whole list+detail TWICE.

        BuildHub();
        // Land on the Ask page by default (visibility only — the greeting types when actually opened).
        AskPage.Visibility = Visibility.Visible;
        LibraryPage.Visibility = Visibility.Collapsed;
        TemplatesPage.Visibility = Visibility.Collapsed;
    }

    void StyleChrome()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var light = Toki.Theme.ThemeManager.Current == Toki.Theme.AppTheme.Light;
            var dark = light ? 0 : 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // title bar follows theme (DWMWA_USE_IMMERSIVE_DARK_MODE)
            int borderColor = light ? 0x00E2E2E2 : 0x00161616;      // window border matches the theme
            DwmSetWindowAttribute(hwnd, 34, ref borderColor, sizeof(int)); // DWMWA_BORDER_COLOR (COLORREF 0x00BBGGRR)
            // The Library is an ordinary window: it is NOT hidden from screen capture. (The pill has
            // an opt-in ghost toggle; the Library deliberately does not — owner decision.)

            // Maximise cleanly. On a per-monitor-DPI-aware app with a manually-placed window,
            // WPF's default maximise mis-sizes (jumps, or spills under the taskbar) — that was
            // the glitch. Handling WM_GETMINMAXINFO pins the maximised bounds to the current
            // monitor's WORK AREA, so maximise snaps to exactly the right rectangle.
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        }
        catch { }
    }

    void Reload(long? focus = null)
    {
        LoadList();
        var toSelect = focus ?? _selected;
        if (toSelect is long id && App.Store.Get(id) is not null) Select(id);
        else if (Cards.Children.Count > 0 && Cards.Children[0] is Border first && first.Tag is long fid) Select(fid);
        else { DetailHeader.Content = null; DetailBody.Content = Placeholder("Select a meeting or note, or make a new one."); }
    }

    // ══════════════════════ Hub: sidebar nav · Ask landing · Templates · mascot ══════════════════════

    string _page = "ask";
    bool _dragEnabled;                       // drag-reorder is OFF by default (no accidental moves)
    readonly Dictionary<string, Border> _navButtons = new();
    static readonly Random _rng = new();
    System.Windows.Threading.DispatcherTimer? _typer;
    int _pokeKind;

    /// <summary>Build the hub's code-driven bits (called at construction and on a theme swap, since
    /// these are built with captured FindResource brushes).</summary>
    void BuildHub()
    {
        BuildNav();
        BuildSuggestions();
        BuildTemplatesPage();
        UpdateDragToggle();
        HighlightNav();
    }

    void BuildNav()
    {
        Nav.Children.Clear();
        _navButtons.Clear();
        Nav.Children.Add(NavItem("ask", (char)0xE8BD, "Ask"));       // chat bubble
        Nav.Children.Add(NavItem("library", (char)0xE8F1, "Library"));// stacked books
        Nav.Children.Add(NavItem("templates", (char)0xE8A5, "Templates")); // document
    }

    Border NavItem(string key, char glyph, string label)
    {
        var icon = new TextBlock { Text = glyph.ToString(), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Width = 22, TextAlignment = TextAlignment.Center };
        var text = new TextBlock { Text = label, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon); row.Children.Add(text);
        var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(11, 9, 11, 9), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = row };
        b.MouseLeftButtonUp += (_, _) => OnNavClick(key);
        _navButtons[key] = b;
        return b;
    }

    // Clicking Library while already on Library toggles the note-list panel (VS Code sidebar feel):
    // collapse it fully to give the note the whole width, click again to bring it back.
    void OnNavClick(string key)
    {
        if (key == "library" && _page == "library") { ToggleList(); return; }
        ShowPage(key);
        if (key == "library" && _listCollapsed) SetList(open: true);   // navigating to Library always shows the list
    }

    bool _listCollapsed;
    double _listWidthPx = 312;

    void ToggleList() => SetList(open: _listCollapsed);

    void SetList(bool open)
    {
        if (open == !_listCollapsed) return;      // already in that state
        if (!open) _listWidthPx = Math.Max(180, ListCol.ActualWidth);   // remember width before closing
        _listCollapsed = !open;
        AnimateCol(ListCol, ListCol.ActualWidth, open ? _listWidthPx : 0);
    }

    // GridLength has no built-in animation, so lerp the pixel width on a timer with an ease — smooth,
    // and cheap (a few frames). Headless just snaps.
    void AnimateCol(System.Windows.Controls.ColumnDefinition col, double from, double to)
    {
        if (App.Headless) { col.Width = new GridLength(to); return; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const double ms = 200;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / ms);
            var v = from + (to - from) * ease.Ease(t);
            col.Width = new GridLength(Math.Max(0, v));
            if (t >= 1.0) { col.Width = new GridLength(Math.Max(0, to)); timer.Stop(); }
        };
        timer.Start();
    }

    void HighlightNav()
    {
        foreach (var (key, b) in _navButtons)
        {
            var active = key == _page;
            if (active) b.Background = (Brush)FindResource("Chip"); else b.Background = Brushes.Transparent;
            if (b.Child is StackPanel sp)
                foreach (var c in sp.Children.OfType<TextBlock>())
                    c.SetResourceReference(TextBlock.ForegroundProperty, active ? "Text" : "TextDim");
        }
    }

    void ShowPage(string key, bool animate = true)
    {
        _page = key;
        AskPage.Visibility = key == "ask" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = key == "library" ? Visibility.Visible : Visibility.Collapsed;
        TemplatesPage.Visibility = key == "templates" ? Visibility.Visible : Visibility.Collapsed;
        HighlightNav();
        UIElement page = key == "ask" ? AskPage : key == "library" ? LibraryPage : TemplatesPage;
        if (animate && !App.Headless) FadePage(page);
        if (key == "ask") RefreshGreeting();
        if (key == "library" && Cards.Children.Count == 0) LoadList();
    }

    static void FadePage(UIElement e)
    {
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ── Ask landing: a rotating greeting that types in, a search bar, quick suggestions ──

    static readonly string[] Greetings =
    {
        "Hi {0}, how are you doing?", "Hey {0} — what's on your mind?", "Welcome back, {0}.",
        "Good to see you, {0}.", "Ready when you are, {0}.", "What can I find for you, {0}?",
        "Hi {0} — ask me about your meetings.",
    };

    void RefreshGreeting()
    {
        if (Messages.Children.Count > 0) return;   // a conversation is going — keep it, don't reset to the landing
        Landing.Visibility = Visibility.Visible;
        Messages.Visibility = Visibility.Collapsed;
        var name = Llm.AppSettings.Current.DisplayName;
        var full = string.Format(Greetings[_rng.Next(Greetings.Length)], string.IsNullOrEmpty(name) ? "there" : name);
        AskHint.Text = "Ask about your meetings — decisions, tasks, who said what.";
        TypeText(Greeting, full);
    }

    // Reveal the greeting one character at a time — slow and smooth, like it's being written.
    void TypeText(TextBlock tb, string full)
    {
        _typer?.Stop();
        if (App.Headless) { tb.Text = full; return; }
        tb.Text = "";
        var i = 0;
        _typer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(28) };
        _typer.Tick += (_, _) => { if (i >= full.Length) { _typer!.Stop(); return; } tb.Text += full[i++]; };
        _typer.Start();
    }

    void BuildSuggestions()
    {
        Suggestions.Children.Clear();
        foreach (var s in new[] { "What did I commit to this week?", "Summarize my last meeting", "Any open tasks?" })
            Suggestions.Children.Add(SuggestionChip(s));
    }

    Border SuggestionChip(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, FontFamily = (FontFamily)FindResource("UiFont") };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        var b = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(11, 7, 11, 7), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, Child = t };
        b.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");
        b.MouseLeftButtonUp += (_, _) => { AskBox.Text = text; OnAsk(this, new RoutedEventArgs()); };
        return b;
    }

    void OnAskKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; OnAsk(sender, e); } }

    bool _asking;

    // Ask a question about the meetings → retrieve relevant notes + answer with the active model (off
    // the UI thread), shown as a conversation with source-meeting chips.
    async void OnAsk(object sender, RoutedEventArgs e)
    {
        var q = AskBox.Text?.Trim();
        if (string.IsNullOrEmpty(q) || _asking) return;
        AskBox.Text = "";
        Landing.Visibility = Visibility.Collapsed;
        Messages.Visibility = Visibility.Visible;
        Messages.Children.Add(UserBubble(q));
        var thinking = LoaderBubble();
        Messages.Children.Add(thinking);
        ScrollChatToEnd();

        _asking = true; AskBtn.IsEnabled = false; MicBtn.IsEnabled = false;
        Llm.NotesQa.Answer ans;
        try { ans = await System.Threading.Tasks.Task.Run(() => Llm.NotesQa.AskAsync(q)); }
        catch (Exception ex) { App.Log($"ask failed: {ex.Message}"); ans = new Llm.NotesQa.Answer("Something went wrong — try again.", [], false); }
        finally { _asking = false; AskBtn.IsEnabled = true; MicBtn.IsEnabled = true; }

        Messages.Children.Remove(thinking);
        Messages.Children.Add(AnswerBubble(ans));
        ScrollChatToEnd();
    }

    void OnMic(object sender, RoutedEventArgs e)
    {
        // Voice input (and the hotkey-opens-mic / "hey Toki" flow) is the next slice.
        if (Landing.Visibility == Visibility.Visible)
            AskHint.Text = "Voice is coming next — type your question for now.";
        else { Messages.Children.Add(TokiBubble("Voice is coming next — type your question for now.", dim: true)); ScrollChatToEnd(); }
    }

    void ScrollChatToEnd() => Dispatcher.BeginInvoke(new Action(() => ChatScroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Background);

    // Chat text is selectable + copyable (read-only transparent TextBox), like a normal chatbot.
    TextBox SelText(string text, double fs, string fgKey)
    {
        var tb = new TextBox
        {
            Text = text ?? "", IsReadOnly = true, IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FontFamily = (FontFamily)FindResource("UiFont"), FontSize = fs, TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(0), Cursor = Cursors.IBeam, IsTabStop = false,
        };
        tb.SetResourceReference(Control.ForegroundProperty, fgKey);
        return tb;
    }

    Border UserBubble(string text)
    {
        var b = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(70, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Right, Child = SelText(text, 13.5, "Text") };
        b.SetResourceReference(Border.BackgroundProperty, "Chip");
        return b;
    }

    // A simple non-selectable Toki notice (system messages like "voice coming soon").
    Border TokiBubble(string text, bool dim = false)
    {
        var t = new TextBlock { Text = text, Style = St(dim ? "Dim" : "Body"), FontSize = dim ? 12.5 : 13.5, TextWrapping = TextWrapping.Wrap };
        var b = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(15, 11, 15, 12), Margin = new Thickness(0, 0, 70, 12), HorizontalAlignment = HorizontalAlignment.Left, Child = t };
        b.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");
        return b;
    }

    // Toki's answer — selectable text + clickable source-meeting chips.
    Border AnswerBubble(Llm.NotesQa.Answer ans)
    {
        var stack = new StackPanel();
        stack.Children.Add(SelText(ans.Text, 13.5, "Text"));
        if (ans.Sources.Count > 0)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 11, 0, 0) };
            chips.Children.Add(new TextBlock { Text = "From", Style = St("Dim"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6) });
            foreach (var s in ans.Sources) chips.Children.Add(SourceChip(s));
            stack.Children.Add(chips);
        }
        var b = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(15, 12, 15, 13), Margin = new Thickness(0, 0, 70, 12), HorizontalAlignment = HorizontalAlignment.Left, Child = stack };
        b.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");
        return b;
    }

    // A fun "cooking up an answer" loader — a Toki-pink bar sliding across + rotating status words.
    Border LoaderBubble()
    {
        var b = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(15, 13, 15, 14), Margin = new Thickness(0, 0, 70, 12), HorizontalAlignment = HorizontalAlignment.Left, Child = BuildLoader() };
        b.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");
        return b;
    }

    static readonly string[] LoaderWords = { "Reading your notes…", "Understanding…", "Extracting…", "Cooking…", "Baking…", "Stewing…", "Roasting…", "Cleaning up…" };

    FrameworkElement BuildLoader()
    {
        var stack = new StackPanel { MinWidth = 200 };
        var status = new TextBlock { Text = LoaderWords[0], FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 9) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        stack.Children.Add(status);

        var trackGrid = new Grid { Width = 190, Height = 4, HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2) };
        track.SetResourceReference(Border.BackgroundProperty, "Chip");
        var dot = new Border { Width = 52, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)FindResource("Pink") };
        var tt = new TranslateTransform();
        dot.RenderTransform = tt;
        trackGrid.Children.Add(track);
        trackGrid.Children.Add(dot);
        stack.Children.Add(trackGrid);

        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 190 - 52, TimeSpan.FromMilliseconds(850))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });

        var i = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        timer.Tick += (_, _) => { i = (i + 1) % LoaderWords.Length; status.Text = LoaderWords[i]; };
        timer.Start();
        stack.Unloaded += (_, _) => timer.Stop();
        return stack;
    }

    Border SourceChip(Llm.NotesQa.Source s)
    {
        var t = new TextBlock { Text = s.Title, FontSize = 10.5, FontFamily = (FontFamily)FindResource("UiFont"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var c = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 3, 9, 4), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Child = t };
        c.SetResourceReference(Border.BackgroundProperty, "Chip");
        c.MouseLeftButtonUp += (_, _) => { Reload(s.Id); ShowPage("library"); };
        return c;
    }

    // ── clickable mascot: a cute reaction that varies each poke ──
    void OnMascotClick(object sender, MouseButtonEventArgs e)
    {
        SideToki.BlinkNow();
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 };
        var (sx, sy) = (_pokeKind++ % 3) switch { 0 => (0.72, 0.72), 1 => (0.6, 0.9), _ => (0.92, 0.58) };
        var dur = TimeSpan.FromMilliseconds(380);
        MascotPop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sx, 1, dur) { EasingFunction = ease });
        MascotPop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(sy, 1, dur) { EasingFunction = ease });
    }

    // ── drag-reorder on/off toggle ──
    void OnToggleDrag(object sender, RoutedEventArgs e) { _dragEnabled = !_dragEnabled; UpdateDragToggle(); }

    void UpdateDragToggle()
    {
        DragToggle.Content = ((char)0xE8CB).ToString();   // Fluent: Sort / reorder lines
        if (_dragEnabled) DragToggle.Foreground = (Brush)FindResource("Coral");
        else DragToggle.ClearValue(ForegroundProperty);
        DragToggle.ToolTip = _dragEnabled
            ? "Reordering ON — drag cards to rearrange them"
            : "Reordering off — tap to drag-reorder your notes";
    }

    // ── Templates page ──
    void BuildTemplatesPage()
    {
        TemplatesList.Children.Clear();
        TemplatesList.Children.Add(new TextBlock { Text = "Templates", Style = St("Body"), FontSize = 22, FontWeight = FontWeights.Bold });
        TemplatesList.Children.Add(new TextBlock
        {
            Text = "Toki picks the shape that fits each meeting automatically. Open any note to switch its template by hand.",
            Style = St("Dim"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16),
        });

        // Default shape for NEW meetings: Auto-detect, or force one for every meeting.
        var defRow = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        defRow.Children.Add(new TextBlock { Text = "DEFAULT SHAPE FOR NEW MEETINGS", Style = St("Dim"), FontSize = 9, FontWeight = FontWeights.Bold, Margin = new Thickness(2, 0, 0, 5) });
        var defDrop = new Views.SearchableDropdown { Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        defDrop.SetItems(new[] { "Auto-detect" }.Concat(Llm.NoteTemplates.All.Select(t => t.Name)),
            Llm.AppSettings.Current.DefaultTemplate ?? "Auto-detect");
        defDrop.Changed += (_, _) =>
        {
            var sel = defDrop.Selected;
            Llm.AppSettings.Current.DefaultTemplate = string.IsNullOrEmpty(sel) || sel == "Auto-detect" ? null : sel;
            Llm.AppSettings.Current.Save();
        };
        defRow.Children.Add(defDrop);
        defRow.Children.Add(new TextBlock { Text = "Auto-detect picks the best shape per meeting (recommended). Or force one shape for every new meeting.", Style = St("Dim"), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 7, 0, 0) });
        TemplatesList.Children.Add(defRow);

        foreach (var t in Llm.NoteTemplates.All) TemplatesList.Children.Add(TemplateCard(t));
    }

    Border TemplateCard(Llm.NoteTemplate t)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = t.Name, Style = St("Body"), FontSize = 14.5, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = char.ToUpper(t.When[0]) + t.When[1..] + ".", Style = St("Dim"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        var secs = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        foreach (var s in new[] { "Summary" }.Concat(t.Sections).Concat(new[] { "Action items" }))
            secs.Children.Add(SectionPill(s));
        stack.Children.Add(secs);
        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14, 16, 15), Margin = new Thickness(0, 0, 0, 10), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");
        return card;
    }

    Border SectionPill(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 10, FontFamily = (FontFamily)FindResource("UiFont") };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(0, 0, 6, 6), Child = t };
        b.SetResourceReference(Border.BackgroundProperty, "Chip");
        return b;
    }

    // ── per-note template picker (manual override → Toki re-writes the note in the chosen shape) ──
    // Compact: a small "Template" caption + dropdown, sits in the top toolbar beside Copy/Delete.
    FrameworkElement TemplatePicker(MeetingRecord m, TextBlock status)
    {
        var wrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        wrap.Children.Add(new TextBlock { Text = "Template", Style = St("Dim"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });

        var drop = new Views.SearchableDropdown { Width = 150, VerticalAlignment = VerticalAlignment.Center };
        drop.SetItems(Llm.NoteTemplates.All.Select(t => t.Name), m.Notes.Template ?? "General");

        drop.Changed += async (_, _) =>
        {
            var name = drop.Selected;
            if (string.IsNullOrWhiteSpace(name) || string.Equals(name, m.Notes.Template ?? "General", StringComparison.OrdinalIgnoreCase)) return;
            if (!Llm.LlmService.Instance.CanWrite())
            {
                status.Text = "Add the on-device model or an API key (Settings) to change templates.";
                status.Visibility = Visibility.Visible;
                return;
            }
            status.Text = $"Rewriting as {name}…"; status.Visibility = Visibility.Visible;
            drop.IsEnabled = false;
            var ok = await App.RewriteNotesAsync(m.Id, name);
            drop.IsEnabled = true;
            if (ok) { var fresh = App.Store.Get(m.Id); if (fresh is not null) RenderMeeting(fresh); }
            else
            {
                // Nothing changed — put the dropdown back to the note's real template so it isn't
                // showing a shape that wasn't actually applied.
                drop.SetItems(Llm.NoteTemplates.All.Select(t => t.Name), m.Notes.Template ?? "General");
                status.Text = "Couldn't rewrite — no transcript or model available.";
                status.Visibility = Visibility.Visible;
            }
        };
        wrap.Children.Add(drop);
        return wrap;
    }

    void LoadList()
    {
        Cards.Children.Clear();
        IReadOnlyList<MeetingSummary> items;
        try { items = App.Store.List(string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text); }
        catch (Exception ex) { App.Log($"library window load: {ex.Message}"); items = []; }

        CountLabel.Text = $"{items.Count} · all on this PC";
        foreach (var m in items) Cards.Children.Add(Card(m));
        if (items.Count == 0)
            Cards.Children.Add(Placeholder(string.IsNullOrWhiteSpace(SearchBox.Text) ? "Nothing yet." : "No matches."));
    }

    Border Card(MeetingSummary m)
    {
        var selected = _selected == m.Id;

        // Left: a calendar-style date badge (day over month), like the design.
        var badge = new Border
        {
            Background = (Brush)FindResource("Chip"), CornerRadius = new CornerRadius(9),
            Width = 42, Height = 46, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 11, 0),
        };
        var badgeStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        badgeStack.Children.Add(new TextBlock { Text = m.StartedAt.Day.ToString(), Style = St("Body"), FontSize = 17, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        badgeStack.Children.Add(new TextBlock { Text = m.StartedAt.ToString("MMM").ToUpperInvariant(), Style = St("Dim"), FontSize = 8, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) });
        badge.Child = badgeStack;

        // Right: title, meta line, snippet, and count chips.
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = m.Title, Style = St("Body"), FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        content.Children.Add(new TextBlock { Text = CardMeta(m), Style = St("Dim"), FontSize = 9.5, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(m.Tldr))
            content.Children.Add(new TextBlock { Text = m.Tldr, Style = St("Dim"), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 0) });

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        if (m.OpenActions > 0) chips.Children.Add(Chip($"{m.OpenActions} open", "Amber", "AmberDark"));
        if (m.PeopleCount > 0) chips.Children.Add(Chip($"{m.PeopleCount} {(m.PeopleCount == 1 ? "person" : "people")}", "TextDim", "Chip"));
        if (chips.Children.Count > 0) content.Children.Add(chips);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(badge);
        Grid.SetColumn(content, 1);
        row.Children.Add(content);

        var card = new Border
        {
            Background = (Brush)FindResource(selected ? "Chip" : "SurfaceHi"),
            // Monotone selection: a soft hairline (not the coral outline, which read as an
            // alarming red box), on the slightly-brighter Chip background. Theme-aware.
            BorderBrush = selected ? (Brush)FindResource("SelectionBorder") : Brushes.Transparent,
            BorderThickness = new Thickness(selected ? 1 : 0),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 11, 11, 12),
            Margin = new Thickness(0, 0, 2, 8), Cursor = Cursors.Hand, Child = row, Tag = m.Id,
        };
        card.PreviewMouseLeftButtonDown += (_, e) => OnCardDown(card, e);
        card.PreviewMouseMove += (_, e) => OnCardMove(e);
        card.PreviewMouseLeftButtonUp += (_, e) => OnCardUp(card, m.Id);
        return card;
    }

    // ---- drag-to-reorder (iPhone-home-screen style: pick up, others slide to make space,
    //      release snaps cleanly into the nearest slot — a card never sits between two) ----
    Border? _drag;
    int _dragFrom;
    double _dragStartY;
    bool _dragMoved;
    readonly List<Border> _cards = new();
    readonly List<double> _cardTops = new();
    readonly List<double> _cardHeights = new();

    void OnCardDown(Border card, MouseButtonEventArgs e)
    {
        _drag = card;
        _dragMoved = false;
        _dragStartY = e.GetPosition(Cards).Y;

        // Snapshot cards + their tops/heights so we can compute slots + shifts during the drag.
        Cards.UpdateLayout();
        _cards.Clear(); _cardTops.Clear(); _cardHeights.Clear();
        double top = 0;
        foreach (var child in Cards.Children)
        {
            if (child is not Border b) continue;
            var t = Translate(b);
            t.BeginAnimation(TranslateTransform.YProperty, null);
            t.Y = 0;
            var h = b.ActualHeight + 8; // + bottom margin
            _cards.Add(b); _cardTops.Add(top); _cardHeights.Add(h); top += h;
        }
        _dragFrom = _cards.IndexOf(card);
        card.CaptureMouse();
    }

    void OnCardMove(MouseEventArgs e)
    {
        if (!_dragEnabled) return;   // reordering is opt-in — a plain click still selects (no move)
        // Reordering only makes sense on the FULL list — a filtered view would renumber just the
        // visible subset and scramble the global order. Under a search, a drag is treated as a click.
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        if (_drag is null || e.LeftButton != MouseButtonState.Pressed || _dragFrom < 0) return;
        var dy = e.GetPosition(Cards).Y - _dragStartY;
        if (!_dragMoved && Math.Abs(dy) < 4) return;
        if (!_dragMoved) { _dragMoved = true; _drag.Opacity = 0.9; Panel.SetZIndex(_drag, 30); }

        Translate(_drag).Y = dy;

        var ins = InsertionIndex(_cardTops[_dragFrom] + _cardHeights[_dragFrom] / 2 + dy);
        var dh = _cardHeights[_dragFrom];
        for (var i = 0; i < _cards.Count; i++)
        {
            if (i == _dragFrom) continue;
            var oi = i < _dragFrom ? i : i - 1;                       // its index among the "others"
            var shift = (i > _dragFrom ? -dh : 0) + (oi >= ins ? dh : 0);
            SlideTo(_cards[i], shift);
        }
    }

    void OnCardUp(Border card, long id)
    {
        if (_drag is null) return;
        card.ReleaseMouseCapture();
        if (!_dragMoved) { _drag = null; Select(id); return; } // a click, not a drag

        var center = _cardTops[_dragFrom] + _cardHeights[_dragFrom] / 2 + Translate(card).Y;
        var ins = InsertionIndex(center);

        // Settle: glide the dragged card into its final slot, THEN commit + rebuild. This is
        // what stops it sitting awkwardly between two rows on release.
        double slotTop = 0; var counted = 0;
        for (var i = 0; i < _cards.Count && counted < ins; i++) { if (i == _dragFrom) continue; slotTop += _cardHeights[i]; counted++; }
        var target = slotTop - _cardTops[_dragFrom];

        var from = _dragFrom;
        _drag = null;
        var settle = new DoubleAnimation(target, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        settle.Completed += (_, _) =>
        {
            card.Opacity = 1; Panel.SetZIndex(card, 0);
            CommitReorder(from, ins);
        };
        Translate(card).BeginAnimation(TranslateTransform.YProperty, settle);
    }

    // Where the dragged card would insert among the OTHER cards (0..n-1), by comparing its
    // centre to the others' closed-up mid-points (dragged card excluded).
    int InsertionIndex(double centerY)
    {
        var idx = 0; double top = 0;
        for (var i = 0; i < _cards.Count; i++)
        {
            if (i == _dragFrom) continue;
            var h = _cardHeights[i];
            if (centerY > top + h / 2) idx++;
            top += h;
        }
        return idx;
    }

    void CommitReorder(int from, int ins)
    {
        var ids = new List<long>();
        foreach (var b in _cards) if (b.Tag is long id) ids.Add(id);
        if (from < 0 || from >= ids.Count) { LoadList(); return; }
        var moved = ids[from];
        ids.RemoveAt(from);
        ids.Insert(Math.Clamp(ins, 0, ids.Count), moved);
        try { App.Store.Reorder(ids); } catch (Exception ex) { App.Log($"reorder persist failed: {ex.Message}"); }
        LoadList(); // rebuild in the new order (always resets every drag transform)
    }

    static TranslateTransform Translate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform t) return t;
        var nt = new TranslateTransform();
        el.RenderTransform = nt;
        return nt;
    }

    static void SlideTo(UIElement el, double y) =>
        Translate(el).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(y, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    Border Chip(string text, string fgKey, string bgKey) => new()
    {
        Background = (Brush)FindResource(bgKey), CornerRadius = new CornerRadius(5),
        Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 6, 0),
        Child = new TextBlock { Text = text, Foreground = (Brush)FindResource(fgKey), FontSize = 9, FontWeight = FontWeights.SemiBold },
    };

    static string CardMeta(MeetingSummary m)
    {
        var parts = new List<string> { m.Kind == MeetingKind.Manual ? "Note" : (m.App ?? "Meeting") };
        if (m.DurationMs > 0) parts.Add($"{Math.Max(1, m.DurationMs / 60000)} min");
        parts.Add(TimeAgo(m.StartedAt));
        return string.Join(" · ", parts);
    }

    static string TimeAgo(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalMinutes < 60) return "just now";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 2) return "yesterday";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} days ago";
        return t.ToString("MMM d");
    }

    void Select(long id)
    {
        FlushManual();          // save the previous manual note before switching away
        _selected = id;
        LoadList(); // refresh selection highlight
        var m = App.Store.Get(id);
        if (m is null) return;
        if (m.Kind == MeetingKind.Manual) RenderManual(m);
        else RenderMeeting(m);
        FadeIn(DetailBody);   // FlowDocument viewer starts at the top on a fresh document
    }

    // Soft cross-fade so switching notes feels smooth, not a hard swap.
    static void FadeIn(UIElement e)
    {
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    void OnBack(object sender, RoutedEventArgs e) => HideToPill(); // hide (stay warm) + bring the pill back

    void RenderMeeting(MeetingRecord m)
    {
        var header = new StackPanel();
        // ONE top toolbar row: template on the left, Copy/Delete on the right — all above the title,
        // out of the note body so it reads cleanly. Title + meta are LEFT-aligned (a note, not a poster).
        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 14) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(CopyButton(m));
        right.Children.Add(SmallButton("Delete", () => Delete(m.Id), danger: true));
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);

        var status = new TextBlock { Style = St("Dim"), FontSize = 10.5, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        if (m.Kind == MeetingKind.Meeting && m.Transcript.Count > 0)
        {
            var tmpl = TemplatePicker(m, status);
            DockPanel.SetDock(tmpl, Dock.Left);
            row.Children.Add(tmpl);
        }
        header.Children.Add(row);
        header.Children.Add(status);
        header.Children.Add(new TextBlock { Text = m.Title, Style = St("Body"), FontSize = 20, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock { Text = Meta(m), Style = St("Dim"), FontSize = 11, Margin = new Thickness(0, 3, 0, 0) });

        DetailHeader.Content = header;
        // The whole note as ONE selectable FlowDocument (drag-select spans everything, Ctrl+C copies all).
        DetailBody.Content = NoteRenderer.MeetingDocument(m, spacious: true);
    }

    void RenderManual(MeetingRecord m)
    {
        DetailHeader.Content = null;
        var panel = new StackPanel();

        var titleBox = new TextBox { Text = m.Title, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = (Brush)FindResource("Text"), CaretBrush = (Brush)FindResource("Text"), FontSize = 20, FontWeight = FontWeights.Bold, FontFamily = (FontFamily)FindResource("UiFont") };
        panel.Children.Add(titleBox);
        panel.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("SurfaceHi"), Margin = new Thickness(0, 8, 0, 0) });

        var bodyBox = new TextBox
        {
            Text = m.Body ?? "", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("TextSoft"), CaretBrush = (Brush)FindResource("Text"),
            FontSize = 13, FontFamily = (FontFamily)FindResource("UiFont"), AcceptsReturn = true, AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 320, Margin = new Thickness(0, 12, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        panel.Children.Add(bodyBox);

        var status = new TextBlock { Style = St("Dim"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = 0, Text = "Saved" };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        bar.Children.Add(SmallButton("Delete", () => Delete(m.Id), danger: true));
        bar.Children.Add(status);
        panel.Children.Add(bar);

        DetailBody.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 26, 0, 0) };

        _manualFlush = () =>
        {
            var title = string.IsNullOrWhiteSpace(titleBox.Text) ? "Untitled note" : titleBox.Text.Trim();
            try { App.Store.UpdateTitleAndBody(m.Id, title, bodyBox.Text); MarkDirty(); }
            catch (Exception ex) { App.Log($"manual note save failed: {ex.Message}"); }
            status.Text = "Saved"; FlashStatus(status);
        };
        void Schedule() { status.Text = "Saving…"; status.Opacity = 0.75; status.BeginAnimation(OpacityProperty, null); _manualDebounce.Stop(); _manualDebounce.Start(); }
        titleBox.TextChanged += (_, _) => Schedule();
        bodyBox.TextChanged += (_, _) => Schedule();
    }

    // Show a status word, then let it fade out — "saved" should reassure, not linger as chrome.
    static void FlashStatus(UIElement e)
    {
        e.BeginAnimation(OpacityProperty, null);
        e.Opacity = 1;
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1200)) { BeginTime = TimeSpan.FromMilliseconds(700) });
    }

    // Save any pending manual-note edit immediately (on switching notes or closing the window).
    void FlushManual()
    {
        if (_manualFlush is null) return;
        _manualDebounce.Stop();
        var f = _manualFlush; _manualFlush = null;
        f();
    }

    void OnNewNote(object sender, RoutedEventArgs e)
    {
        var id = App.Store.CreateManualNote("Untitled note", "");
        Reload(id);
    }

    void OnSearch(object sender, TextChangedEventArgs e) => LoadList();

    void CopyTranscript(MeetingRecord m)
    {
        try { Clipboard.SetText(NoteRenderer.TranscriptText(m)); } catch (Exception ex) { App.Log($"copy failed: {ex.Message}"); }
    }

    // Copy button → a clean THEMED dropdown (never the Win32 ContextMenu): Summary+to-dos, Transcript,
    // or reveal the note's markdown file on disk.
    FrameworkElement CopyButton(MeetingRecord m)
    {
        // Split button: the big "Copy" copies the ENTIRE note (top→bottom) in one click; the ▾ opens
        // the narrower options. The main action is the whole thing because that's what people mean by "copy".
        var main = new Button
        {
            Content = "Copy", Style = (Style)FindResource("PillButton"),
            Padding = new Thickness(14, 7, 12, 7), FontSize = 11.5,
        };
        var caret = new Button
        {
            Content = "▾", Style = (Style)FindResource("PillButton"),
            Padding = new Thickness(9, 7, 11, 7), FontSize = 10, Margin = new Thickness(1, 0, 0, 0),
        };
        void Copy(string text) { if (CopyText(text)) Flash(main, "Copied ✓"); }
        main.Click += (_, _) => Copy(NoteRenderer.FullText(m));
        caret.Click += (_, _) =>
        {
            var items = new List<(string, Action)>
            {
                ("Copy everything", () => Copy(NoteRenderer.FullText(m))),
                ("Summary + to-dos", () => Copy(NoteRenderer.NotesText(m))),
            };
            if (m.Transcript.Count > 0) items.Add(("Transcript only", () => Copy(NoteRenderer.TranscriptText(m))));
            items.Add(("Reveal note file", () => RevealNoteFile(m)));
            ShowMenu(caret, items);
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0), Children = { main, caret } };
    }

    // Brief inline confirmation on a button, then restore its label — so a copy never feels silent.
    static void Flash(Button b, string msg)
    {
        var was = b.Content;
        b.Content = msg;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        t.Tick += (_, _) => { b.Content = was; t.Stop(); };
        t.Start();
    }

    // A rounded, outlined, THEMED popup of hover-able rows — our own menu, matching the app.
    void ShowMenu(FrameworkElement anchor, List<(string Label, Action Act)> items)
    {
        var stack = new StackPanel();
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade, VerticalOffset = 4,
        };
        foreach (var (label, act) in items)
        {
            // A real Button, not a Border+MouseLeftButtonUp: Click fires reliably inside a StaysOpen=false
            // popup (the mouse-up-on-Border approach got swallowed when the popup closed → dead menu items).
            var t = new TextBlock { Text = label, FontSize = 12, FontFamily = (FontFamily)FindResource("UiFont"), VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var row = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 8, 22, 8), Background = Brushes.Transparent, Child = t };
            var btn = new Button { Content = row, Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Focusable = false };
            btn.Template = TransparentButtonTemplate();
            btn.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "Chip");
            btn.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            var captured = act;
            btn.Click += (_, _) => { popup.IsOpen = false; captured(); };
            stack.Children.Add(btn);
        }
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(5), BorderThickness = new Thickness(1), MinWidth = 176, Child = stack };
        box.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");
        box.SetResourceReference(Border.BorderBrushProperty, "Hairline");
        popup.Child = box;
        popup.IsOpen = true;
    }

    // A button that renders exactly its content, no chrome — so our themed rows show through.
    static ControlTemplate TransparentButtonTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        t.VisualTree = cp;
        return t;
    }

    // Returns true only if the text actually reached the clipboard. The Win32 clipboard can be
    // momentarily held by another app (COMException "OpenClipboard failed") — retry a few times.
    bool CopyText(string text)
    {
        text ??= "";
        for (var i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text.Length == 0 ? " " : text); return true; }
            catch (Exception ex) { App.Log($"copy retry {i}: {ex.Message}"); System.Threading.Thread.Sleep(40); }
        }
        return false;
    }

    // Toki already mirrors every note to a Markdown file — surface it (file export the easy way).
    void RevealNoteFile(MeetingRecord m)
    {
        try
        {
            var path = Storage.MarkdownMirror.Write(m);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log($"reveal note failed: {ex.Message}"); }
    }

    void Delete(long id)
    {
        var m = App.Store.Get(id);
        if (!Views.ConfirmDialog.Show(this, "Delete this?",
                $"“{m?.Title}” and its audio will be removed for good. This can't be undone.")) return;
        App.Store.Delete(id);
        if (_selected == id) _selected = null;
        Reload();
    }

    // ---- ui helpers ----

    Style St(string key) => (Style)FindResource(key);

    // The "Back to Toki" content, composed so BOTH the chevron and the label are vertically centered.
    // The chevron is the icon-font ChevronLeft glyph (designed centered on the em), not the "‹" text
    // character (which Segoe sets low). Foreground is inherited from the button.
    static UIElement BackButtonInner()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(new TextBlock
        {
            Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });
        sp.Children.Add(new TextBlock { Text = "Back to Toki", VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    // Calibration-only: a full Back-to-Toki button for the buttons.png probe.
    internal Button BuildBackButtonProbe() => new()
    {
        Content = BackButtonInner(), Style = St("PillButton"), Background = (Brush)FindResource("SurfaceHi"),
        Padding = new Thickness(13, 9, 13, 9), FontSize = 11.5, MinWidth = 220, HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    FrameworkElement SmallButton(string label, Action onClick, bool primary = false, bool danger = false)
    {
        var b = new Button
        {
            Content = label, Style = (Style)FindResource(primary ? "CoralButton" : "PillButton"),
            Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(13, 6, 13, 6), FontSize = 11.5,
        };
        if (danger) { b.Foreground = (Brush)FindResource("Danger"); b.Background = (Brush)FindResource("DangerTint"); }
        b.Click += (_, _) => onClick();
        return b;
    }

    TextBlock Placeholder(string text) => new()
    {
        Text = text, Style = St("Dim"), FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 24, 0, 0),
    };

    static string Meta(MeetingRecord m)
    {
        var parts = new List<string>();
        if (m.DurationMs > 0) parts.Add($"{Math.Max(1, m.DurationMs / 60000)} min");
        parts.Add(m.StartedAt.ToString("dddd, MMM d · h:mm tt"));
        if (!string.IsNullOrWhiteSpace(m.App)) parts.Add(m.App!);
        return string.Join("  ·  ", parts);
    }

    // Pin maximise to the current monitor's work area (fixes the DPI maximise glitch).
    static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024, MONITOR_DEFAULTTONEAREST = 0x2;
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return IntPtr.Zero;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref info)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.x = info.rcWork.left - info.rcMonitor.left;
        mmi.ptMaxPosition.y = info.rcWork.top - info.rcMonitor.top;
        mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
        mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;
        Marshal.StructureToPtr(mmi, lParam, true);
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINTL { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct RECTL { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public POINTL ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECTL rcMonitor, rcWork; public int dwFlags; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
