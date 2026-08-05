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

/// <summary>How the Library list is ordered. Manual = the drag-reorder order stored in the DB.</summary>
public enum LibrarySort { Newest, Oldest, Longest, Shortest, OpenTasks, Manual }

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
    public static void ShowLibrary(long? focusMeetingId = null, Rect? fromRect = null, string? page = null)
    {
        EnsureCreated();
        var w = _open!;
        // Rebuild only when data changed (or a specific note was requested); otherwise the
        // already-rendered content is reused and opening is just Show() → instant.
        if (_dirty || focusMeetingId is not null) { w.Reload(focusMeetingId); _dirty = false; }
        // Always POP UP: closed → open with the bloom; minimized → restore; already open → bring to front.
        var wasHidden = !w.IsVisible;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        if (wasHidden) { w._originRect = fromRect; w.CenterOnWorkArea(); w.Show(); w.PlayOpen(fromRect); }
        else w.Show();
        // An explicit page (assistant command) wins; a specific note → the Library page showing it;
        // otherwise the Ask landing (fresh greeting). This is what makes "take me to the library" land
        // on the actual notes LIST instead of the Ask page (the wrong-page bug).
        w.ShowPage(page ?? (focusMeetingId is not null ? "library" : "ask"), animate: false);
        w.BringToFront();
    }

    /// <summary>Force the window to the foreground even when it was minimized or behind — the Topmost
    /// flip is the reliable way to steal focus back for our own window without a Win32 hack.</summary>
    void BringToFront()
    {
        Activate();
        var wasTop = Topmost;
        Topmost = true; Topmost = wasTop;
        Focus();
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
        // "settings" joined the hub in v1-iter2/iter3 (name, AI summaries + API key, calendar, about) —
        // it renders like any other page, and a design review needs it as much as the rest.
        foreach (var page in new[] { "ask", "library", "templates", "settings" })
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
        _warming = true;  // nothing here is "on screen" — never ground Ask on the pre-warm render
        w.Show();
        w.Reload();       // build the real content once, off-screen (pre-JIT + pre-render)
        _dirty = false;   // content is now current; first real open can skip the rebuild
        w.UpdateLayout();
        w.Hide();
        _warming = false;
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
            // Restore from REALITY, not a hardcoded Home: a recording bar or a Summarizing
            // loader on the pill must survive the Library closing — forcing Home over them
            // hid a live mic (and orphaned the loader's pending result).
            var cur = App.State.Current;
            if (cur is PillState.RecordingBar or PillState.RecordingOpen or PillState.Summarizing) return;
            App.State.ForceForDemo(App.IsRecording ? PillState.RecordingBar : PillState.Home);
        });
    }

    /// <summary>Open the hub on a specific page (ask / library / templates) — for assistant commands.
    /// The page rides into ShowLibrary directly, so the window lands there in one step (no Ask-page flash).</summary>
    public static void OpenHubPage(string page) => ShowLibrary(page: page);

    /// <summary>Open the Library Ask and ask <paramref name="question"/> in full — the notch's "See full
    /// answer in Library" hand-off (a fuller, non-concise answer).</summary>
    public static void AskInLibrary(string question)
    {
        ShowLibrary();
        _open?.Dispatcher.BeginInvoke(new Action(() =>
        {
            _open.ShowPage("ask");
            _open.OnNewChat(_open, new RoutedEventArgs());   // the hand-off starts a NEW conversation
            _open.AskBox.Text = question;
            _open.OnAsk(_open, new RoutedEventArgs());
        }), System.Windows.Threading.DispatcherPriority.Loaded);
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
        // Stick-to-bottom for the chat thread (same rule as the notch AskView): auto-follow the newest
        // text only while you're already parked at the bottom, so a streaming answer never yanks you down
        // while you've scrolled up to re-read. A user scroll (ExtentHeightChange == 0) updates the flag —
        // ~120px threshold, matching AskView (8px was hair-trigger: sub-pixel extent wobble un-anchored
        // the reader). The "↓ Latest" pill shows whenever you've drifted up and there's more below.
        ChatScroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentHeightChange == 0 && (e.VerticalChange != 0 || e.ViewportHeightChange != 0))
                _chatStick = ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 120;
            ShowChatJumpPill(!_chatStick && ChatScroll.ScrollableHeight > 12);
        };
        try { _chats = _chatStore.Load(); } catch { _chats = new(); }   // past Ask conversations
        SourceInitialized += (_, _) => StyleChrome();
        // Soften the maximise/restore snap (the "min-to-max sizer" clunk): ease the content into
        // the new size on every WindowState change instead of a hard pop.
        StateChanged += (_, _) => PlayStateSettle();
        // The list cards + note detail are built in code (FindResource brushes), so they don't
        // recolour on their own — rebuild them (and re-apply the title-bar theme) on a theme swap.
        // Code-built UI (list cards, note detail, nav, templates) captures brushes → rebuild on a theme swap.
        Toki.Theme.ThemeManager.ThemeChanged += () => Dispatcher.BeginInvoke(() =>
        {
            StyleChrome(); BuildHub(); Reload();
            if (_page == "settings") LoadLibrarySettings(refreshModels: false);   // recolour only — no network fetch on a theme flip
        });
        _manualDebounce.Tick += (_, _) => { _manualDebounce.Stop(); _manualFlush?.Invoke(); };
        // NOTE: no Loaded→Reload. Show() raises Loaded, and both Warm() and ShowLibrary()
        // already call Reload() explicitly — a Loaded handler here made every open rebuild
        // the whole list+detail TWICE.

        // Restore the saved list sort.
        if (Enum.TryParse<LibrarySort>(Llm.AppSettings.Current.LibrarySort, out var savedSort)) _sort = savedSort;
        UpdateSortButton();

        BuildHub();
        // Land on the Ask page by default (visibility only — the greeting types when actually opened).
        AskPage.Visibility = Visibility.Visible;
        LibraryPage.Visibility = Visibility.Collapsed;
        TemplatesPage.Visibility = Visibility.Collapsed;
    }

    bool _wndProcHooked;

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
            // Once only: StyleChrome re-runs on every theme swap, and AddHook does not
            // de-duplicate — each toggle silently appended another copy of WndProc.
            if (!_wndProcHooked)
            {
                _wndProcHooked = true;
                HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            }
        }
        catch { }
    }

    void Reload(long? focus = null)
    {
        LoadList();
        var toSelect = focus ?? _selected;
        // An explicit focus (e.g. "open this note") counts as user intent; re-selecting the previous
        // note keeps its sticky user/auto status; the silent first-card fallback is NOT user-viewed.
        if (toSelect is long id && App.Store.Get(id) is not null) Select(id, byUser: focus is not null);
        else if (Cards.Children.Count > 0 && Cards.Children[0] is Border first && first.Tag is long fid) Select(fid, byUser: false);
        else DetailBody.Content = Placeholder("Select a meeting or note, or make a new one.");
    }

    // ══════════════════════ Hub: sidebar nav · Ask landing · Templates · mascot ══════════════════════

    string _page = "ask";
    bool _dragEnabled;                       // drag-reorder is OFF by default (no accidental moves)
    bool _myNotesOnly;                       // book filter: show only hand-written (manual) notes
    LibrarySort _sort = LibrarySort.Newest;  // list ordering (persisted)
    readonly Dictionary<string, Border> _navButtons = new();
    static readonly Random _rng = new();
    System.Windows.Threading.DispatcherTimer? _typer;

    // ── Ask conversations (persisted history) ──
    readonly Storage.ChatStore _chatStore = new();
    List<Storage.ChatStore.Conversation> _chats = new();
    Storage.ChatStore.Conversation? _conv;   // the active conversation (null = fresh, on the landing)
    Llm.ConversationContext _ctx = new();    // last-turn state for follow-ups ("before that", "its tasks")

    /// <summary>Build the hub's code-driven bits (called at construction and on a theme swap, since
    /// these are built with captured FindResource brushes).</summary>
    void BuildHub()
    {
        BuildNav();
        BuildSuggestions();
        BuildTemplatesPage();
        UpdateDragToggle();
        UpdateMyNotesToggle();
        HighlightNav();
    }

    void BuildNav()
    {
        Nav.Children.Clear();
        _navButtons.Clear();
        Nav.Children.Add(NavItem("ask", (char)0xE8BD, "Ask"));       // chat bubble
        Nav.Children.Add(NavItem("library", (char)0xE8F1, "Library"));// stacked books
        Nav.Children.Add(NavItem("templates", (char)0xE8A5, "Templates")); // document
        Nav.Children.Add(NavItem("settings", (char)0xE713, "Settings"));   // gear
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

    void OnNavClick(string key) => ShowPage(key);

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
        SettingsPage.Visibility = key == "settings" ? Visibility.Visible : Visibility.Collapsed;
        HighlightNav();
        UIElement page = key switch { "ask" => AskPage, "library" => LibraryPage, "templates" => TemplatesPage, _ => SettingsPage };
        if (animate && !App.Headless) FadePage(page);
        if (key == "ask") RefreshGreeting();
        if (key == "settings") { LoadNameSettings(); LoadLibrarySettings(); }
        if (key == "library" && Cards.Children.Count == 0) LoadList();
    }

    static void FadePage(UIElement e)
    {
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ── Settings page: name (the passive place to change it — confirm/cancel, success, live everywhere) ──

    bool _nameLoading;
    void LoadNameSettings()
    {
        _nameLoading = true;
        LibNameBox.Text = Llm.AppSettings.Current.UserName ?? "";
        LibNameActions.Visibility = Visibility.Collapsed;
        LibNameSaved.Visibility = Visibility.Collapsed;
        _nameLoading = false;
    }

    // Confirm/Cancel appear only when there's an unsaved change (and the "saved" note clears on edit).
    void OnLibNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_nameLoading) return;
        var changed = (LibNameBox.Text?.Trim() ?? "") != (Llm.AppSettings.Current.UserName ?? "");
        LibNameActions.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
        if (changed) LibNameSaved.Visibility = Visibility.Collapsed;
    }

    void OnLibNameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && LibNameActions.Visibility == Visibility.Visible) { e.Handled = true; OnLibNameSave(sender, e); }
        else if (e.Key == Key.Escape) { e.Handled = true; OnLibNameCancel(sender, e); }
    }

    void OnLibNameSave(object sender, RoutedEventArgs e)
    {
        var name = LibNameBox.Text?.Trim() ?? "";
        Llm.UserProfile.SetName(name);              // persists through the one settings path
        LibNameActions.Visibility = Visibility.Collapsed;
        var shown = Llm.AppSettings.Current.DisplayName;   // resolved (falls back to the account name if cleared)
        LibNameSavedText.Text = shown.Length > 0 ? $"Saved — I'll call you {shown}." : "Saved.";
        LibNameSaved.Visibility = Visibility.Visible;
        if (!App.Headless)
            LibNameSaved.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        RefreshGreeting();   // the Library Ask greeting updates immediately; the notch Home greeting
                             // re-reads the name every time it's shown (HomeView.RefreshGreeting).
    }

    void OnLibNameCancel(object sender, RoutedEventArgs e)
    {
        LoadNameSettings();   // revert to the stored value, hide the actions
    }

    // ── Settings page: everything else (the notch-settings consolidation — DONE 2026-08-04) ──
    //
    // This page is THE home for preferences. All of it goes through the existing one-path
    // App/service calls (no duplicated persistence): theme (App.SwitchTheme), glassy notch
    // (App.SetGlassyNotch), ghost (App.SetGhostMode), Google Calendar (CalendarService.Instance),
    // About (PillState.About), and the AI-summaries mode + BYO-API-key flow — moved WHOLE from the
    // notch into the reusable Views.AiSummariesSection control hosted below. The notch Settings is
    // now UI/visibility only (owner: "the notch should only have the visibility, UI settings").

    bool _settingsLoading;
    void LoadLibrarySettings(bool refreshModels = true)
    {
        _settingsLoading = true;
        var s = Llm.AppSettings.Current;
        LibThemeToggle.IsChecked = Toki.Theme.ThemeManager.Current == Toki.Theme.AppTheme.Dark;
        LibGlassToggle.IsChecked = s.GlassyNotch;
        LibGhostToggle.IsChecked = s.ShowInRecordings;
        RefreshLibGoogle();
        // The AI block rebuilds its code-built bits (segmented control) too; a theme swap passes
        // refreshModels:false so recolouring never fires a network fetch.
        AiSection.Refresh(refreshModels);
        _settingsLoading = false;
    }

    void OnLibThemeToggle(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        App.SwitchTheme(light: LibThemeToggle.IsChecked != true);   // persists + recolours everything live
    }

    void OnLibGlassToggle(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        App.SetGlassyNotch(LibGlassToggle.IsChecked == true);       // persists + refreshes the notch surface
    }

    void OnLibGhostToggle(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        // Checked = visible in recordings = ghost OFF. SetGhostMode persists + applies live.
        App.SetGhostMode(on: LibGhostToggle.IsChecked != true);
    }

    void RefreshLibGoogle()
    {
        var svc = Toki.Calendar.CalendarService.Instance;
        if (svc.IsConnected)
        {
            LibGoogleStatus.Text = svc.AccountEmail is { Length: > 0 } m
                ? $"Connected as {m}." : "Connected to Google Calendar.";
            LibGoogleConnectBtn.Content = "Reconnect";
            LibGoogleConnectBtn.IsEnabled = true;
            LibGoogleDisconnectBtn.Visibility = Visibility.Visible;
        }
        else
        {
            LibGoogleStatus.Text = svc.IsConfigured
                ? "Not connected. Sign in to see upcoming meetings and let Toki prep you."
                : "Google sign-in isn’t set up in this build yet.";
            LibGoogleConnectBtn.Content = "Connect Google";
            LibGoogleConnectBtn.IsEnabled = svc.IsConfigured;
            LibGoogleDisconnectBtn.Visibility = Visibility.Collapsed;
        }
    }

    async void OnLibGoogleConnect(object sender, RoutedEventArgs e)
    {
        var svc = Toki.Calendar.CalendarService.Instance;
        if (!svc.IsConfigured) { LibGoogleStatus.Text = "Google sign-in isn’t set up in this build yet."; return; }
        LibGoogleConnectBtn.IsEnabled = false;
        LibGoogleStatus.Text = "Opening your browser — sign in and click Allow…";
        try
        {
            await svc.ConnectAsync();
        }
        catch (Toki.Calendar.GoogleAuthException ex) { LibGoogleStatus.Text = ex.Message; }
        catch { LibGoogleStatus.Text = "Couldn’t connect to Google."; }
        finally { LibGoogleConnectBtn.IsEnabled = true; RefreshLibGoogle(); }
    }

    async void OnLibGoogleDisconnect(object sender, RoutedEventArgs e)
    {
        LibGoogleDisconnectBtn.IsEnabled = false;
        try { await Toki.Calendar.CalendarService.Instance.DisconnectAsync(); }
        finally { LibGoogleDisconnectBtn.IsEnabled = true; RefreshLibGoogle(); }
    }

    // The "Built with" credits live on the notch's About screen (one canonical copy of the licence
    // list) — this row summons it, same as the notch Settings row does.
    void OnLibOpenSource(object sender, MouseButtonEventArgs e) => App.State.ForceForDemo(PillState.About);

    void OnLibAboutHover(object sender, MouseEventArgs e)
    { if (sender is Border b) b.SetResourceReference(Border.BackgroundProperty, "Chip"); }

    void OnLibAboutUnhover(object sender, MouseEventArgs e)
    { if (sender is Border b) b.Background = Brushes.Transparent; }

    // ── Ask landing: hero mascot on a soft glow, a typed greeting, one identity line, chips ──

    // One line that says what Toki IS — shown under the greeting, restored after voice hints.
    const string LandingHint = "Your meeting assistant — decisions, tasks, who said what. All on this PC.";

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
        AskHint.Text = LandingHint;
        TypeText(Greeting, full);
        AnimateLanding();
    }

    // Calm, staggered arrival (Motion tokens): hero → identity line → chips, each a small fade-up.
    void AnimateLanding()
    {
        if (App.Headless) return;
        Rise(LandingHero, 0);
        Rise(AskHint, 70);
        var i = 0;
        foreach (var c in Suggestions.Children.OfType<FrameworkElement>()) Rise(c, 140 + i++ * 60);
    }

    static void Rise(FrameworkElement el, int delayMs)
    {
        var slide = new TranslateTransform(0, Toki.Theme.Motion.EnterRise);
        el.RenderTransform = slide;
        el.Opacity = 0;
        var delay = TimeSpan.FromMilliseconds(delayMs);
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Toki.Theme.Motion.Dur(Toki.Theme.Motion.Enter))
        { BeginTime = delay, EasingFunction = Toki.Theme.Motion.EaseOut });
        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(Toki.Theme.Motion.EnterRise, 0, Toki.Theme.Motion.Dur(Toki.Theme.Motion.Enter))
            { BeginTime = delay, EasingFunction = Toki.Theme.Motion.EaseOut });
    }

    // The landing hero mascot reacts to a poke, same as the sidebar one.
    void OnLandingMascotClick(object sender, MouseButtonEventArgs e) => LandingToki.React();

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
        // Owner: the example-question pills read as ugly/uninviting — removed. The landing is just the
        // mascot hero + one identity line; a clean prompt bar below invites the real question.
        Suggestions.Children.Clear();
        Suggestions.Visibility = Visibility.Collapsed;
    }

    // Suggestion chips (Luma-style invitations): full-radius outlined pills that light on hover.
    Border SuggestionChip(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11.5, FontFamily = (FontFamily)FindResource("UiFont") };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextSoft");
        var b = new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(14, 8, 14, 9),
            Margin = new Thickness(4, 0, 4, 8), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1), Child = t,
        };
        b.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");
        b.SetResourceReference(Border.BorderBrushProperty, "Hairline");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Chip");
        b.MouseLeave += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");
        b.MouseLeftButtonUp += (_, _) => { AskBox.Text = text; OnAsk(this, new RoutedEventArgs()); };
        return b;
    }

    void OnAskKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; OnAsk(sender, e); } }

    // A typed assistant command in the Ask box → do the thing (navigation acts as its own confirmation;
    // Help explains what Toki can do).
    void ExecuteLibraryCommand(Llm.AppCommand c, string q)
    {
        switch (c)
        {
            case Llm.AppCommand.OpenLibrary:   ShowPage("library"); break;
            case Llm.AppCommand.OpenAsk:       ShowPage("ask"); break;
            case Llm.AppCommand.OpenTemplates: ShowPage("templates"); break;
            case Llm.AppCommand.OpenSettings:  ShowPage("settings"); break;   // settings live HERE now
            case Llm.AppCommand.NewNote:       OnNewNote(this, new RoutedEventArgs()); break;
            case Llm.AppCommand.NewChat:       OnNewChat(this, new RoutedEventArgs()); break;
            case Llm.AppCommand.GoHome:
            case Llm.AppCommand.Close:         HideToPill(); break;
            case Llm.AppCommand.StartRecording:
                // Start the manual recording on the notch; the Library stays put (hiding it here would
                // race the pill's state change — the recording bar appearing IS the confirmation).
                if (App.IsRecording) { CommandReply(q, "Already recording — the bar's up top."); break; }
                App.StartRecordingFromCommand();
                CommandReply(q, "Recording — the bar's up top. Click it when you're done.");
                break;
            case Llm.AppCommand.UseApiModel:      CommandReply(q, App.SwitchNotesMode(Llm.NotesMode.ApiKey)); break;
            case Llm.AppCommand.UseLocalModel:    CommandReply(q, App.SwitchNotesMode(Llm.NotesMode.Local)); break;
            case Llm.AppCommand.UseHeuristicMode: CommandReply(q, App.SwitchNotesMode(Llm.NotesMode.HeuristicOnly)); break;
            case Llm.AppCommand.ThemeLight:       CommandReply(q, App.SwitchTheme(light: true)); break;
            case Llm.AppCommand.ThemeDark:        CommandReply(q, App.SwitchTheme(light: false)); break;
            case Llm.AppCommand.GhostOn:          CommandReply(q, App.SetGhostMode(on: true)); break;
            case Llm.AppCommand.GhostOff:         CommandReply(q, App.SetGhostMode(on: false)); break;
            case Llm.AppCommand.Help:
                CommandReply(q, "I can answer about your meetings — decisions, tasks, who said what — and do quick things like “start taking notes”, “new note”, “open templates”, “switch to dark theme”, or “ghost mode off”. Just ask.");
                break;
        }
    }

    // A command asked in the chat gets a short confirmation bubble (navigation confirms itself; state
    // changes like a model/theme switch need the words).
    void CommandReply(string q, string message)
    {
        EnsureConversation();
        Landing.Visibility = Visibility.Collapsed; Messages.Visibility = Visibility.Visible;
        Messages.Children.Add(UserBubble(q));
        Messages.Children.Add(TokiBubble(message, dim: true));
        PinChatToEnd();
    }

    bool _asking;

    // ── ask by voice (on-device Parakeet) ──
    Audio.MicCapture? _mic;
    string? _micWav;
    bool _listening;
    volatile bool _heardSpeech;
    long _lastSpeechTick, _listenStartTick;   // x64 long reads/writes are atomic (cross-thread with the tap)
    System.Windows.Threading.DispatcherTimer? _voiceMonitor;
    const float VoiceSpeechThreshold = 0.02f;   // peak amplitude that counts as "speaking"
    const long VoiceSilenceMs = 1800;           // generous end-of-speech gap before auto-stop
    const long VoiceNoSpeechMs = 6000;          // give up if nothing is said
    const long VoiceMaxMs = 30000;              // hard cap on one utterance

    // Ask a question about the meetings → retrieve relevant notes + answer with the active model (off
    // the UI thread), STREAMING the answer into the bubble as it's written, with source-meeting chips.
    async void OnAsk(object sender, RoutedEventArgs e)
    {
        var q = AskBox.Text?.Trim();
        if (string.IsNullOrEmpty(q) || _asking) return;
        AskBox.Text = "";
        // Tier 0: a little assistant command ("open templates", "new note", "go home") runs instantly;
        // a question (even one mentioning those words) falls through to the Q&A (the router vetoes questions).
        if (Llm.CommandRouter.TryMatch(q) is Llm.AppCommand lc && lc != Llm.AppCommand.ShowTasks)
        { ExecuteLibraryCommand(lc, q); return; }
        EnsureConversation();
        Landing.Visibility = Visibility.Collapsed;
        Messages.Visibility = Visibility.Visible;
        Messages.Children.Add(UserBubble(q));
        _conv!.Turns.Add(new Storage.ChatStore.Turn { Role = "user", Text = q });
        SetTitleFromFirstQuestion(q);

        var stream = NewStreamBubble();
        Messages.Children.Add(stream.Root);
        PinChatToEnd();   // a new question → jump to the newest turn, then stream follows only if pinned

        _asking = true; AskBtn.IsEnabled = false; MicBtn.IsEnabled = false;
        // Patch streaming: buffer tokens and flush in ~70ms chunks so words render up in patches, not char-by-char.
        var accSb = new System.Text.StringBuilder();
        var gate = new object();
        var flusher = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(70) };
        flusher.Tick += (_, _) => { string cur; lock (gate) cur = accSb.ToString(); if (cur.Length > 0) stream.SetFinalText(cur); };
        flusher.Start();
        Llm.NotesQa.Answer ans;
        try
        {
            ans = await System.Threading.Tasks.Task.Run(() => Llm.NotesQa.AskStreamAsync(q,
                srcs => Dispatcher.Invoke(() => stream.SetSources(srcs)),
                tok => { lock (gate) accSb.Append(tok); },
                default, _ctx, screenMeetingId: App.CurrentViewedMeeting?.Id));
        }
        catch (Exception ex) { App.Log($"ask failed: {ex.Message}"); ans = new Llm.NotesQa.Answer("Something went wrong — try again.", [], false); }
        finally { _asking = false; AskBtn.IsEnabled = true; MicBtn.IsEnabled = true; flusher.Stop(); }

        // The final answer is authoritative (streamed text may have been a rejected/degenerate draft).
        stream.SetFinalText(ans.Text);
        stream.SetSources(ans.Sources);
        _conv.Turns.Add(new Storage.ChatStore.Turn
        {
            Role = "assistant", Text = ans.Text,
            Sources = ans.Sources.Select(s => new Storage.ChatStore.SourceRef { Id = s.Id, Title = s.Title }).ToList(),
        });
        PersistConversation();
        ScrollChatToEnd();
    }

    // ── conversations: new chat, history, persistence ──

    void EnsureConversation()
    {
        if (_conv is not null) return;
        _conv = new Storage.ChatStore.Conversation { Title = "New chat" };
        _chats.Insert(0, _conv);
    }

    void SetTitleFromFirstQuestion(string q)
    {
        if (_conv is null || _conv.Turns.Count(t => t.Role == "user") != 1) return;   // only the first question titles it
        _conv.Title = q.Length > 46 ? q[..46].TrimEnd() + "…" : q;
        ChatTitle.Text = _conv.Title;
    }

    void PersistConversation()
    {
        if (_conv is null) return;
        _conv.UpdatedAt = DateTimeOffset.Now.ToString("o");
        // Save only non-empty conversations, newest first.
        try { _chatStore.Save(_chats.Where(c => c.Turns.Count > 0)); } catch { }
    }

    void OnNewChat(object sender, RoutedEventArgs e)
    {
        if (_asking) return;
        _conv = null;                       // next question starts a fresh conversation
        _ctx = new();                       // forget follow-up state
        Messages.Children.Clear();
        Messages.Visibility = Visibility.Collapsed;
        Landing.Visibility = Visibility.Visible;
        ChatTitle.Text = "Ask Toki";
        RefreshGreeting();                  // types a fresh greeting (guard passes now that Messages is empty)
    }

    void OnHistory(object sender, RoutedEventArgs e)
    {
        var convos = _chats.Where(c => c.Turns.Count > 0).OrderByDescending(c => c.Updated).ToList();
        if (convos.Count == 0) { ChatTitle.Text = "No past chats yet"; return; }
        ShowChatHistory((Button)sender, convos);
    }

    void LoadConversation(Storage.ChatStore.Conversation c)
    {
        _conv = c;
        _ctx = new();   // a reopened thread starts follow-up tracking fresh
        Messages.Children.Clear();
        foreach (var t in c.Turns)
        {
            if (t.Role == "user") Messages.Children.Add(UserBubble(t.Text));
            else Messages.Children.Add(AnswerBubble(new Llm.NotesQa.Answer(
                t.Text, t.Sources.Select(s => new Llm.NotesQa.Source(s.Id, s.Title)).ToList(), true)));
        }
        Landing.Visibility = Visibility.Collapsed;
        Messages.Visibility = Visibility.Visible;
        ChatTitle.Text = c.Title;
        PinChatToEnd();   // reopening a thread → land at the most recent turn
    }

    // Themed history popup: each past chat (title + when), click to reopen, × to delete. Never the Win32 menu.
    void ShowChatHistory(Button anchor, List<Storage.ChatStore.Conversation> convos)
    {
        var stack = new StackPanel();
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade, VerticalOffset = 6,
        };
        foreach (var c in convos.Take(20))
        {
            var title = new TextBlock { Text = c.Title, FontSize = 12, FontFamily = (FontFamily)FindResource("UiFont"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var when = new TextBlock { Text = Ago(c.Updated), Style = St("Dim"), FontSize = 9.5, Margin = new Thickness(0, 1, 0, 0) };
            var textCol = new StackPanel { Children = { title, when } };
            var del = new Button { Content = "✕", Style = St("PillButton"), Background = Brushes.Transparent, FontSize = 10, Padding = new Thickness(7, 4, 7, 4), VerticalAlignment = VerticalAlignment.Center };
            del.SetResourceReference(ForegroundProperty, "TextDim");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(textCol);
            Grid.SetColumn(del, 1); grid.Children.Add(del);

            var rowBorder = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 8, 8, 8), Background = Brushes.Transparent, Child = grid };
            var row = new Button { Content = rowBorder, Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Focusable = false, Template = TransparentButtonTemplate() };
            row.MouseEnter += (_, _) => rowBorder.SetResourceReference(Border.BackgroundProperty, "Chip");
            row.MouseLeave += (_, _) => rowBorder.Background = Brushes.Transparent;
            var captured = c;
            row.Click += (_, _) => { popup.IsOpen = false; LoadConversation(captured); };
            del.Click += (_, ev) =>
            {
                ev.Handled = true;                       // don't also trigger the row's open-click
                _chats.Remove(captured);
                if (ReferenceEquals(_conv, captured)) OnNewChat(this, ev);
                try { _chatStore.Save(_chats.Where(x => x.Turns.Count > 0)); } catch { }
                stack.Children.Remove(row);
            };
            stack.Children.Add(row);
        }
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360, Content = stack };
        var box = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(6), BorderThickness = new Thickness(1), MinWidth = 280, Child = scroll };
        box.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");
        box.SetResourceReference(Border.BorderBrushProperty, "Hairline");
        popup.Child = box;
        popup.IsOpen = true;
    }

    static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return t.ToString("MMM d");
    }

    // Ask by voice: click the mic → record → click again → transcribe on-device (Parakeet) into the box.
    // Toggle-to-record (not push-to-talk) so it's one clear tap to start and one to stop.
    async void OnMic(object sender, RoutedEventArgs e)
    {
        if (_asking) return;
        if (!_listening && !Transcription.TranscriptionService.ModelReady)
        {
            VoiceHint("Add the on-device speech model (in onboarding) to ask by voice.");
            return;
        }
        if (!_listening) { StartListening(); return; }
        await StopListeningAndTranscribe();
    }

    void StartListening()
    {
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Toki", "voice");
            System.IO.Directory.CreateDirectory(dir);
            _micWav = System.IO.Path.Combine(dir, $"ask-{Guid.NewGuid():N}.wav");
            _mic = new Audio.MicCapture();
            _mic.Start(_micWav);
            var fmt = _mic.Format;
            _heardSpeech = false;
            _lastSpeechTick = _listenStartTick = Environment.TickCount64;
            // Passive level tap (runs on the capture thread) → remember when we last heard speech, for
            // the end-of-speech auto-stop. Never touches the capture itself.
            _mic.OnBuffer = (buf, n) =>
            {
                if (fmt is null) return;
                if (Audio.MicCapture.PeakOf(buf, n, fmt) >= VoiceSpeechThreshold)
                {
                    _lastSpeechTick = Environment.TickCount64;
                    _heardSpeech = true;
                }
            };
            _listening = true;
            ShowVoiceUi(listening: true);
            StartVoiceMonitor();
        }
        catch (Exception ex)
        {
            App.Log($"voice: mic start failed: {ex.Message}");
            _listening = false; HideVoiceUi();
            try { _mic?.Dispose(); } catch { } _mic = null;
            VoiceHint("Couldn't reach the microphone — check Windows mic permissions.");
        }
    }

    // End-of-speech auto-stop, Google/Safari-style: once we've heard speech, stop after a generous silence
    // gap; give up if nothing is said; hard-cap the utterance. Polls the level the tap records.
    void StartVoiceMonitor()
    {
        _voiceMonitor ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(150) };
        _voiceMonitor.Tick -= OnVoiceMonitorTick;
        _voiceMonitor.Tick += OnVoiceMonitorTick;
        _voiceMonitor.Start();
    }

    async void OnVoiceMonitorTick(object? sender, EventArgs e)
    {
        if (!_listening) { _voiceMonitor?.Stop(); return; }
        var now = Environment.TickCount64;
        var stop = (now - _listenStartTick > VoiceMaxMs)
                || (_heardSpeech && now - _lastSpeechTick > VoiceSilenceMs)
                || (!_heardSpeech && now - _listenStartTick > VoiceNoSpeechMs);
        if (stop) await StopListeningAndTranscribe();
    }

    async System.Threading.Tasks.Task StopListeningAndTranscribe()
    {
        if (!_listening && _mic is null) return;   // already stopping (guards the async monitor re-entry)
        _listening = false;
        _voiceMonitor?.Stop();
        var mic = _mic; _mic = null;
        var path = _micWav; _micWav = null;
        var heard = _heardSpeech;
        try { mic?.Stop(); } catch { }
        try { mic?.Dispose(); } catch { }

        if (path is null || !System.IO.File.Exists(path) || !heard)
        {
            HideVoiceUi();
            try { if (path is not null) System.IO.File.Delete(path); } catch { }
            if (!heard) VoiceHint("Didn't catch anything — tap the mic and speak.");
            return;
        }

        ShowVoiceUi(listening: false);   // "Transcribing…" loader
        MicBtn.IsEnabled = false;
        string? text;
        try { text = await System.Threading.Tasks.Task.Run(() => Transcription.TranscriptionService.Instance.TranscribeFile(path)); }
        catch (Exception ex) { App.Log($"ask-bar transcribe failed: {ex.Message}"); text = null; }
        finally
        {
            // An ASR throw must never leave the mic button dead, the loader stuck, or the
            // user's voice snippet sitting in %TEMP%.
            MicBtn.IsEnabled = true;
            HideVoiceUi();
            try { System.IO.File.Delete(path); } catch { }
        }

        if (text is null) { VoiceHint("Couldn't transcribe that — try again."); return; }
        if (text.Length == 0) { VoiceHint("Didn't catch that — try again."); return; }

        // Fill the box (append if there's already text) and focus so you can glance + press Enter.
        AskBox.Text = string.IsNullOrWhiteSpace(AskBox.Text) ? text : AskBox.Text.TrimEnd() + " " + text;
        AskBox.CaretIndex = AskBox.Text.Length;
        ClearVoiceHint();
        AskBox.Focus();
    }

    // Voice overlay states: replace the input box with a pulsing dot + word ("Listening…" coral →
    // "Transcribing…" pink), and tint the mic. HideVoiceUi restores the box.
    void ShowVoiceUi(bool listening)
    {
        VoiceStatusText.Text = listening ? "Listening…" : "Transcribing…";
        VoiceDot.Fill = (Brush)FindResource(listening ? "Coral" : "Pink");
        AskBox.Visibility = Visibility.Collapsed;
        VoiceStatus.Visibility = Visibility.Visible;
        MicBtn.Foreground = (Brush)FindResource("Coral");
        MicBtn.ToolTip = listening ? "Listening… tap to stop" : "Transcribing…";
        PulseDot(true);
    }

    void HideVoiceUi()
    {
        PulseDot(false);
        VoiceStatus.Visibility = Visibility.Collapsed;
        AskBox.Visibility = Visibility.Visible;
        MicBtn.ClearValue(ForegroundProperty);
        MicBtn.ToolTip = "Ask by voice";
    }

    void PulseDot(bool on)
    {
        if (on && !App.Headless)
            VoiceDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(650))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        else { VoiceDot.BeginAnimation(OpacityProperty, null); VoiceDot.Opacity = 1; }
    }

    // A small transient hint — on the landing it uses AskHint; in a conversation, a dim Toki note.
    void VoiceHint(string msg)
    {
        if (Landing.Visibility == Visibility.Visible) AskHint.Text = msg;
        else { Messages.Children.Add(TokiBubble(msg, dim: true)); ScrollChatToEnd(); }
    }

    void ClearVoiceHint()
    {
        if (Landing.Visibility == Visibility.Visible)
            AskHint.Text = LandingHint;
    }

    // Auto-follow the newest text only while pinned to the bottom (see the ScrollChanged wiring in the
    // ctor). Streaming callbacks and generic appends use this; a NEW user turn calls PinChatToEnd first.
    bool _chatStick = true;
    void ScrollChatToEnd()
    {
        if (!_chatStick) return;
        Dispatcher.BeginInvoke(new Action(() => { if (_chatStick) ChatScroll.ScrollToEnd(); }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // A new turn the user initiated (asking a question, restoring a chat) always jumps to the bottom.
    void PinChatToEnd() { _chatStick = true; ScrollChatToEnd(); ShowChatJumpPill(false); }

    // ── the "↓ Latest" pill (same look/motion as the notch AskView's JumpPill) ──

    bool _chatPillShown;
    void ShowChatJumpPill(bool show)
    {
        if (show == _chatPillShown) return;
        _chatPillShown = show;
        if (App.Headless) { ChatJumpPill.Visibility = show ? Visibility.Visible : Visibility.Collapsed; ChatJumpPill.Opacity = show ? 1 : 0; return; }
        if (show) ChatJumpPill.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(show ? 1 : 0, Toki.Theme.Motion.Dur(Toki.Theme.Motion.Micro)) { EasingFunction = Toki.Theme.Motion.EaseOut };
        if (!show) anim.Completed += (_, _) => { if (!_chatPillShown) ChatJumpPill.Visibility = Visibility.Collapsed; };
        ChatJumpPill.BeginAnimation(OpacityProperty, anim);
    }

    void OnChatJumpToLatest(object sender, MouseButtonEventArgs e)
    {
        _chatStick = true;
        ChatScroll.ScrollToEnd();   // SmoothScroll is enabled on ChatScroll, so this glides
        ShowChatJumpPill(false);
    }

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

    // A live answer bubble: shows the cooking loader until the first token, then reveals the answer
    // text and streams tokens into it; source chips appear as soon as retrieval names them.
    sealed class StreamCtl
    {
        public required Border Root;
        public required Action<string> Append;
        public required Action<IReadOnlyList<Llm.NotesQa.Source>> SetSources;
        public required Action<string> SetFinalText;
    }

    StreamCtl NewStreamBubble()
    {
        var loader = BuildLoader();
        var answer = SelText("", 13.5, "Text");
        answer.Visibility = Visibility.Collapsed;
        var chips = new WrapPanel { Margin = new Thickness(0, 11, 0, 0), Visibility = Visibility.Collapsed };
        var stack = new StackPanel { Children = { loader, answer, chips } };
        var b = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(15, 12, 15, 13), Margin = new Thickness(0, 0, 70, 12), HorizontalAlignment = HorizontalAlignment.Left, Child = stack };
        b.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");

        var started = false;
        void Reveal() { if (started) return; started = true; stack.Children.Remove(loader); answer.Visibility = Visibility.Visible; }
        return new StreamCtl
        {
            Root = b,
            Append = tok => { Reveal(); answer.AppendText(tok); ScrollChatToEnd(); },
            SetFinalText = txt => { Reveal(); answer.Text = txt ?? ""; ScrollChatToEnd(); },
            SetSources = srcs =>
            {
                // An empty FINAL source list retracts chips shown during streaming: a miss answer
                // must never sit above "From <meetings>" chips (no citations on a non-answer).
                if (srcs is null || srcs.Count == 0)
                {
                    chips.Children.Clear();
                    chips.Visibility = Visibility.Collapsed;
                    return;
                }
                chips.Children.Clear();
                chips.Children.Add(new TextBlock { Text = "From", Style = St("Dim"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6) });
                foreach (var s in srcs) chips.Children.Add(SourceChip(s));
                chips.Visibility = Visibility.Visible;
            },
        };
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

    // ── clickable mascot: a different fun reaction every poke (squish / hop / wobble / wink / spin /
    //    big-eyes / nod), cycled inside TokiBlob so it feels alive rather than one canned bounce. ──
    void OnMascotClick(object sender, MouseButtonEventArgs e) => SideToki.React();

    // ── "My notes" filter: show only the notes you wrote by hand ──
    void OnToggleMyNotes(object sender, RoutedEventArgs e) { _myNotesOnly = !_myNotesOnly; UpdateMyNotesToggle(); LoadList(); }

    void UpdateMyNotesToggle()
    {
        if (_myNotesOnly) MyNotesToggle.Foreground = (Brush)FindResource("Coral");
        else MyNotesToggle.ClearValue(ForegroundProperty);
        MyNotesToggle.ToolTip = _myNotesOnly ? "Showing your notes only — tap to show everything" : "Show only my hand-written notes";
    }

    // ── drag-reorder on/off toggle ──
    void OnToggleDrag(object sender, RoutedEventArgs e)
    {
        _dragEnabled = !_dragEnabled;
        // Drag reorders the STORED manual order — switch the view to Manual when enabling it, else
        // the cards you drag wouldn't be in the order the drag renumbers.
        if (_dragEnabled && _sort != LibrarySort.Manual) SetSort(LibrarySort.Manual);
        UpdateDragToggle();
    }

    void UpdateDragToggle()
    {
        DragToggle.Content = ((char)0xE76F).ToString();   // Fluent: GripperBarHorizontal (drag handle)
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
            Text = "Toki picks the shape that fits each meeting. Open any note to switch it by hand.",
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
    // A real Button that opens the SAME themed popup as Copy (ShowMenu). The old SearchableDropdown was
    // a Border+MouseLeftButtonUp, which the read-only RichTextBox (the note now lives in) SWALLOWS — so it
    // was "stuck". Real Buttons' Click fires fine inside the document, matching Copy/Delete.
    FrameworkElement TemplatePicker(MeetingRecord m, TextBlock status)
    {
        var btn = new Button
        {
            Style = (Style)FindResource("PillButton"),
            Padding = new Thickness(12, 6, 11, 6), FontSize = 11.5, Margin = new Thickness(0, 0, 8, 0),
            Content = $"Template · {m.Notes.Template ?? "General"}   ▾",
        };
        btn.Click += (_, _) =>
        {
            var items = Llm.NoteTemplates.All
                .Select(t => (t.Name, (Action)(() => ApplyTemplate(m, t.Name, status)))).ToList();
            ShowMenu(btn, items);
        };
        return btn;
    }

    async void ApplyTemplate(MeetingRecord m, string name, TextBlock status)
    {
        if (string.Equals(name, m.Notes.Template ?? "General", StringComparison.OrdinalIgnoreCase)) return;
        if (!Llm.LlmService.Instance.CanWrite())
        {
            status.Text = "Add the on-device model or an API key (Settings) to change templates.";
            status.Visibility = Visibility.Visible;
            return;
        }
        status.Text = $"Rewriting as {name}…"; status.Visibility = Visibility.Visible;
        var ok = await App.RewriteNotesAsync(m.Id, name);
        if (ok) { var fresh = App.Store.Get(m.Id); if (fresh is not null) RenderMeeting(fresh); }
        else { status.Text = "Couldn't rewrite — no transcript or model available."; status.Visibility = Visibility.Visible; }
    }

    void LoadList()
    {
        Cards.Children.Clear();
        IReadOnlyList<MeetingSummary> items;
        try { items = App.Store.List(string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text); }
        catch (Exception ex) { App.Log($"library window load: {ex.Message}"); items = []; }

        if (_myNotesOnly) items = items.Where(m => m.Kind == MeetingKind.Manual).ToList();
        items = SortItems(items);

        CountLabel.Text = _myNotesOnly ? $"{items.Count} · my notes" : $"{items.Count} · all on this PC";
        foreach (var m in items) Cards.Children.Add(Card(m));
        if (items.Count == 0)
            Cards.Children.Add(Placeholder(
                _myNotesOnly ? "No hand-written notes yet — tap + to write one."
                : _sort == LibrarySort.OpenTasks ? "No meetings with open tasks."
                : string.IsNullOrWhiteSpace(SearchBox.Text) ? "Nothing yet." : "No matches."));
    }

    // Sort the list per the chosen mode. Manual = the drag-reorder order (App.Store.List already
    // returns it — sort_order ASC then newest), so it's a pass-through. OpenTasks filters to
    // meetings that HAVE open tasks and ranks by how many.
    IReadOnlyList<MeetingSummary> SortItems(IReadOnlyList<MeetingSummary> items) => _sort switch
    {
        LibrarySort.Newest => items.OrderByDescending(m => m.StartedAt).ToList(),
        LibrarySort.Oldest => items.OrderBy(m => m.StartedAt).ToList(),
        LibrarySort.Longest => items.OrderByDescending(m => m.DurationMs).ThenByDescending(m => m.StartedAt).ToList(),
        LibrarySort.Shortest => items.OrderBy(m => m.DurationMs).ThenByDescending(m => m.StartedAt).ToList(),
        LibrarySort.OpenTasks => items.Where(m => m.OpenActions > 0)
            .OrderByDescending(m => m.OpenActions).ThenByDescending(m => m.StartedAt).ToList(),
        _ => items,   // Manual — keep the store's drag-reorder order
    };

    static readonly (string Label, LibrarySort Mode)[] SortModes =
    {
        ("Newest first", LibrarySort.Newest),
        ("Oldest first", LibrarySort.Oldest),
        ("Longest first", LibrarySort.Longest),
        ("Shortest first", LibrarySort.Shortest),
        ("Most open tasks", LibrarySort.OpenTasks),
        ("Manual (drag order)", LibrarySort.Manual),
    };

    // Toki's own themed popup (never the Win32 ContextMenu chrome) — same surface as the Copy menu,
    // with a check on the active mode.
    void OnSortMenu(object sender, RoutedEventArgs e)
    {
        var current = SortModes.First(m => m.Mode == _sort).Label;
        var items = SortModes
            .Select(m => (m.Label, (Action)(() => SetSort(m.Mode)))).ToList();
        ShowMenu(SortBtn, items, selected: current);
    }

    void SetSort(LibrarySort mode)
    {
        _sort = mode;
        try { var s = Llm.AppSettings.Current; s.LibrarySort = mode.ToString(); s.Save(); } catch { }
        // Drag-reorder only makes sense in Manual order — turn it off when another sort is active so
        // a drag can't renumber a view whose order isn't the stored one.
        if (mode != LibrarySort.Manual && _dragEnabled) { _dragEnabled = false; UpdateDragToggle(); }
        UpdateSortButton();
        LoadList();
    }

    void UpdateSortButton()
    {
        if (SortBtn is null) return;
        if (_sort != LibrarySort.Newest) SortBtn.Foreground = (Brush)FindResource("Coral");
        else SortBtn.ClearValue(ForegroundProperty);
        SortBtn.ToolTip = "Sort & filter — " + _sort switch
        {
            LibrarySort.Newest => "newest first",
            LibrarySort.Oldest => "oldest first",
            LibrarySort.Longest => "longest first",
            LibrarySort.Shortest => "shortest first",
            LibrarySort.OpenTasks => "most open tasks",
            _ => "manual order",
        };
    }

    Border Card(MeetingSummary m)
    {
        var selected = _selected == m.Id;

        // Left: a calendar-style date badge (day over month), like the design.
        var badge = new Border
        {
            // SurfaceHi (not Chip) so the date tile stays visible on BOTH a transparent row and the
            // Chip-filled selected row, without being a heavy box.
            Background = (Brush)FindResource("SurfaceHi"), CornerRadius = new CornerRadius(9),
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

        // Airy list (like the Ask panel): rows are OPEN — no card fill, no boxed boundary. Only the
        // selected row carries a soft fill, and hovering shows a subtle one. This drops the "wall of
        // boxes" that read as tight and heavy.
        var card = new Border
        {
            Background = selected ? (Brush)FindResource("Chip") : Brushes.Transparent,
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 11, 12, 12),
            Margin = new Thickness(0, 0, 2, 3), Cursor = Cursors.Hand, Child = row, Tag = m.Id,
        };
        card.MouseEnter += (_, _) => { if (_selected != m.Id) card.Background = (Brush)FindResource("SurfaceHi"); };
        card.MouseLeave += (_, _) => { if (_selected != m.Id) card.Background = Brushes.Transparent; };
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

    // ── screen-aware Ask grounding state (read via VisibleMeeting; see App.CurrentViewedMeeting) ──
    MeetingRecord? _viewedRecord;   // the record currently rendered in the detail pane
    bool _viewedByUser;             // true when the USER chose it (click / focus) — not an auto-select
    static bool _warming;           // true during the off-screen startup pre-warm (nothing is "on screen")

    /// <summary>The meeting note ACTUALLY on the user's screen right now: the Library window is visible
    /// (not the off-screen warm-up), the rendered note is a real meeting (not a manual note), and the
    /// user chose it (a click or an explicit open — never the list's silent auto-select, which is what
    /// made "this meeting" ground on a note nobody had looked at). Derived, so it can't go stale.</summary>
    internal static MeetingRecord? VisibleMeeting =>
        _open is { } w && w.IsVisible && !_warming && w._viewedByUser
        && w._viewedRecord is { Kind: MeetingKind.Meeting } m ? m : null;

    void Select(long id, bool byUser = true)
    {
        FlushManual();          // save the previous manual note before switching away
        // "User-viewed" is sticky per note: re-selecting the same note during an auto Reload must not
        // demote a note the user genuinely opened.
        _viewedByUser = byUser || (_viewedByUser && _viewedRecord?.Id == id);
        _selected = id;
        LoadList(); // refresh selection highlight
        var m = App.Store.Get(id);
        if (m is null) { _viewedRecord = null; return; }
        _viewedRecord = m;
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
        // (Screen-aware Ask grounding is DERIVED from _viewedRecord/_viewedByUser — see VisibleMeeting.)
        // The action toolbar (Template left · Copy/Delete right) rides at the TOP of the scroll document,
        // so the whole note — toolbar, title, body — is one scroll from the very top of the pane.
        var toolbar = new StackPanel();
        var row = new DockPanel { LastChildFill = false };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(CopyButton(m));
        right.Children.Add(SmallButton("Delete", () => Delete(m.Id), danger: true));
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);

        var status = new TextBlock { Style = St("Dim"), FontSize = 10.5, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        if (m.Kind == MeetingKind.Meeting && m.Transcript.Count > 0)
        {
            var tmpl = TemplatePicker(m, status);
            DockPanel.SetDock(tmpl, Dock.Left);
            row.Children.Add(tmpl);
        }
        toolbar.Children.Add(row);
        toolbar.Children.Add(status);

        var body = NoteRenderer.MeetingDocument(m, spacious: true, topBar: toolbar);
        DetailBody.Content = body;
        // Start at the very TOP once laid out (a fresh RichTextBox occasionally came up scrolled down).
        if (body is RichTextBox rtb)
            Dispatcher.BeginInvoke(new Action(() => rtb.ScrollToHome()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void RenderManual(MeetingRecord m)
    {
        // (A manual note never grounds Ask — VisibleMeeting filters on Kind == Meeting.)
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

        // Fills the pane with its own inset (DetailBody no longer supplies a margin) and scrolls from the top.
        DetailBody.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(28, 24, 16, 16) };

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
    // With `selected`, rows carry a leading check column and the active row shows a pink tick
    // (the sort/filter menu) — still dividers-not-boxes, never the Win32 menu chrome.
    void ShowMenu(FrameworkElement anchor, List<(string Label, Action Act)> items, string? selected = null)
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
            FrameworkElement content = t;
            if (selected is not null)
            {
                var isSel = label == selected;
                if (isSel) t.FontWeight = FontWeights.SemiBold;
                var tick = new TextBlock
                {
                    Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10,
                    Width = 16, VerticalAlignment = VerticalAlignment.Center,
                    Opacity = isSel ? 1 : 0,   // reserve the column so labels align
                };
                tick.SetResourceReference(TextBlock.ForegroundProperty, "Pink");
                var g = new StackPanel { Orientation = Orientation.Horizontal };
                g.Children.Add(tick);
                g.Children.Add(t);
                content = g;
            }
            var row = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 8, 22, 8), Background = Brushes.Transparent, Child = content };
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
    // Reveal only inside the Notes dir (the path is app-generated, but the choke point makes that a
    // guarantee, not an assumption).
    void RevealNoteFile(MeetingRecord m)
    {
        try
        {
            var path = Storage.MarkdownMirror.Write(m);
            Interop.SafeShell.RevealInExplorer(path, Storage.MarkdownMirror.NotesDir);
        }
        catch (Exception ex) { App.Log($"reveal note failed: {ex.Message}"); }
    }

    void Delete(long id)
    {
        var m = App.Store.Get(id);
        if (!Views.ConfirmDialog.Show(this, "Delete this?",
                $"“{m?.Title}” and its audio will be removed for good. This can't be undone.")) return;
        // The dialog promises the AUDIO goes too — DiscardMeeting is the one true delete
        // (DB row + Markdown mirror + recording folder). A bare Store.Delete left the WAVs
        // and the full-transcript mirror on disk after the user was told they were gone.
        if (m is not null) App.DiscardMeeting(m);
        else App.Store.Delete(id);
        if (_selected == id) { _selected = null; _viewedRecord = null; _viewedByUser = false; }
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
        // Destructive = red TEXT on the same neutral button as Copy (not a loud red-filled pill) — clean,
        // still clearly a delete. Red fill is reserved for the final confirm dialog.
        if (danger) b.Foreground = (Brush)FindResource("Danger");
        b.Click += (_, _) => onClick();
        return b;
    }

    TextBlock Placeholder(string text) => new()
    {
        Text = text, Style = St("Dim"), FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 24, 0, 0),
    };

    static string Meta(MeetingRecord m) => Views.TimeText.FullMeta(m);

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
