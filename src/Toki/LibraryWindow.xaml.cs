using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Toki.Calendar;   // for the CalendarProvider display-name extensions
using Toki.State;
using Toki.Storage;
using Toki.Views;

namespace Toki;

/// <summary>How the Library list is ordered. Manual = the drag-reorder order stored in the DB.</summary>
public enum LibrarySort { Newest, Oldest, Longest, Shortest, OpenTasks }

/// <summary>
/// Resizable window showing the whole Library (list) beside the selected note's
/// full content - the "maximise" target and the Home/tray Library button. Manual
/// notes are editable here with an explicit Save; transcripts have a Copy button.
/// </summary>
public partial class LibraryWindow : Window
{
    static LibraryWindow? _open;   // single instance
    long? _selected;

    // Manual notes auto-save as you type (debounced) - no static Save/Cancel buttons.
    readonly System.Windows.Threading.DispatcherTimer _manualDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    Action? _manualFlush; // saves the currently-open manual note; null when a meeting is shown

    // The sticky-note panel inside the meeting note currently on screen. Held so it can be flushed
    // before the note is swapped away or the window closes - the same rule as _manualFlush: text the
    // user typed is never allowed to die in a debounce timer.
    Views.MeetingStickyView? _sticky;

    // The list/detail is only rebuilt when the data actually changed. Between opens the
    // window is kept alive (Hidden), fully rendered - so a plain re-open is just Show(),
    // which is instant (like WisprFlow keeping its window warm). MarkDirty() is called when
    // something external changes the data (a recording saves a meeting, a note is created).
    static bool _dirty = true;
    public static void MarkDirty() => _dirty = true;

    /// <summary>
    /// Open the Library - must feel INSTANT. The window is created once and kept warm
    /// (Hidden between opens, never destroyed), and is pre-warmed off-screen at startup
    /// (<see cref="Warm"/>), so a click never pays window-create + full-layout cost. That
    /// slow, visible "the window builds while the pill is still up, then the pill closes"
    /// handoff was the clunk - a warm window just appears.
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

    /// <summary>Force the window to the foreground even when it was minimized or behind - the Topmost
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
        // "settings" joined the hub in v1-iter2/iter3 (name, AI summaries + API key, calendar, about) -
        // it renders like any other page, and a design review needs it as much as the rest.
        foreach (var page in new[] { "ask", "library", "templates", "settings" })
        {
            w.ShowPage(page, animate: false);
            w.UpdateLayout();
            Dev.RenderHarness.SaveVisual(w.RootGrid, System.IO.Path.Combine(dir, $"hub-{page}.png"));
        }

        // The Calendars block sits well below the fold on the Settings page, so a plain shot of that
        // page never shows the two connection rows - which are exactly what a design review of this
        // change needs to see.
        w.ShowPage("settings", animate: false);
        w.UpdateLayout();
        w.SettingsPage.ScrollToEnd();
        w.UpdateLayout();
        Dev.RenderHarness.SaveVisual(w.RootGrid, System.IO.Path.Combine(dir, "hub-settings-calendars.png"));
        w.SettingsPage.ScrollToHome();

        // Nav hover: proves the whole rounded row is the target, not the label (there is no way to
        // move a real pointer in a headless render, so the state is set directly).
        w.ShowPage("library", animate: false);
        if (w._navButtons.TryGetValue("templates", out var hovered))
            hovered.SetResourceReference(Border.BackgroundProperty, "HoverOverlay");
        w.UpdateLayout();
        Dev.RenderHarness.SaveVisual(w.RootGrid, System.IO.Path.Combine(dir, "hub-nav-hover.png"));
        if (hovered is not null) hovered.Background = Brushes.Transparent;

        // The sticky note open inside a meeting note. Planted in the VIEW only - no row is created, so
        // a screenshot can never leave anything behind in the user's real notes.
        w.ShowPage("library", animate: false);
        w._sticky?.DebugSeed(
            "## Before we start\n- [ ] ask what changed on the pricing page\n- [x] read last week's recap\n\n" +
            "they seemed unsure about the four-week pilot - push for six\n\n" +
            "## To raise\n- the renewal date\n- who owns onboarding now\n\n" +
            "1. open with the numbers\n2. then the pilot\n3. leave time for questions");
        w.UpdateLayout();
        w._sticky?.BringIntoView();     // it sits below the notes - scroll to it so the shot shows it
        w.UpdateLayout();
        Dev.RenderHarness.SaveVisual(w.RootGrid, System.IO.Path.Combine(dir, "hub-library-sticky.png"));

        // A hand-written note, which is the page the owner called "very AI generated". Rendered from an
        // IN-MEMORY record with a negative id: RenderManual only reads what it is handed, so nothing is
        // created, and the debounced auto-save is never scheduled because nothing is typed. A render
        // harness must never be able to write to the real note store.
        w.ShowPage("library", animate: false);
        w.RenderManual(new MeetingRecord
        {
            Id = -1,
            Kind = MeetingKind.Manual,
            Title = "Pricing page rewrite",
            StartedAt = DateTimeOffset.Now,
            Body = "# What we agreed\n\nThe pricing page reads like a spec sheet. People land on it, "
                 + "scan for a number, and leave before the part that explains why the number is what "
                 + "it is.\n\n## To change\n- lead with the one-time price, not the feature grid\n"
                 + "- move the comparison table below the fold\n- cut the third testimonial\n\n"
                 + "## Next\n- [ ] draft the new opening paragraph\n- [x] pull last month's bounce rate\n"
                 + "- [ ] ask Devi for the screenshots\n\n"
                 + "1. rewrite\n2. review together on Thursday\n3. ship Friday if it holds up",
        });
        w.UpdateLayout();
        Dev.RenderHarness.SaveVisual(w.RootGrid, System.IO.Path.Combine(dir, "hub-manual-note.png"));
        w._manualFlush = null;   // belt and braces: nothing pending can fire after the shot

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
        // destroying it - so the NEXT open stays instant too. But on real app exit, let it
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
    /// visual tree - we pay it once, off the critical path). No flash: it's shown far off
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
        _warming = true;  // nothing here is "on screen" - never ground Ask on the pre-warm render
        w.Show();
        w.Reload();       // build the real content once, off-screen (pre-JIT + pre-render)
        _dirty = false;   // content is now current; first real open can skip the rebuild
        w.UpdateLayout();
        w.Hide();
        _warming = false;
        w.ShowInTaskbar = true;
        w.ShowActivated = true;
    }

    // The pill's on-screen rect when the Library was opened - so the window blooms FROM the notch
    // and shrinks back TO it (a connected morph), not from a disembodied centre.
    Rect? _originRect;
    bool _closing;

    /// <summary>
    /// Grow the window out of the notch: the content scales up from the pill's location with a
    /// fade - GPU-composited (a RenderTransform + element Opacity on RootGrid), smooth and cheap.
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
    /// then run <paramref name="done"/> - which hides the window and re-reveals the pill, so the two
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
    /// eases into the new size instead of hard-popping - so the size change reads smooth.</summary>
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

    /// <summary>Retract to the notch and bring the pill back - one continuous motion: the window
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
            // loader on the pill must survive the Library closing - forcing Home over them
            // hid a live mic (and orphaned the loader's pending result).
            var cur = App.State.Current;
            if (cur is PillState.RecordingBar or PillState.RecordingOpen or PillState.Summarizing) return;
            App.State.ForceForDemo(App.IsRecording ? PillState.RecordingBar : PillState.Home);
        });
    }

    /// <summary>Open the hub on a specific page (ask / library / templates) - for assistant commands.
    /// The page rides into ShowLibrary directly, so the window lands there in one step (no Ask-page flash).</summary>
    public static void OpenHubPage(string page) => ShowLibrary(page: page);

    /// <summary>Open the Library Ask and ask <paramref name="question"/> in full - the notch's "See full
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
        // while you've scrolled up to re-read. A user scroll (ExtentHeightChange == 0) updates the flag -
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
        // recolour on their own - rebuild them (and re-apply the title-bar theme) on a theme swap.
        // Code-built UI (list cards, note detail, nav, templates) captures brushes → rebuild on a theme swap.
        Toki.Theme.ThemeManager.ThemeChanged += () => Dispatcher.BeginInvoke(() =>
        {
            StyleChrome(); BuildHub(); Reload();
            if (_page == "settings") LoadLibrarySettings(refreshModels: false);   // recolour only - no network fetch on a theme flip
        });
        _manualDebounce.Tick += (_, _) => { _manualDebounce.Stop(); _manualFlush?.Invoke(); };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); LoadList(); };
        // NOTE: no Loaded→Reload. Show() raises Loaded, and both Warm() and ShowLibrary()
        // already call Reload() explicitly - a Loaded handler here made every open rebuild
        // the whole list+detail TWICE.

        // The list builds its rows with our own Card(), one at a time, only for rows it realises.
        Cards.Build = o => Card((MeetingSummary)o);

        // Notes arrive in two waves: the summary and to-dos as soon as the model has written them, then
        // the speaker names a few seconds later. When the second wave lands for the note that is OPEN,
        // re-read it from the store so the loader is replaced by the real thing in place.
        App.SpeakersResolved += id =>
        {
            if (_selected != id || _page != "library") return;
            if (App.Store.Get(id) is { } fresh) RenderMeeting(fresh);
        };

        // Restore the saved list sort. (A settings file still saying "Manual" from before drag-reorder
        // was removed simply fails to parse and lands on Newest, which is what we want.)
        if (Enum.TryParse<LibrarySort>(Llm.AppSettings.Current.LibrarySort, out var savedSort)) _sort = savedSort;
        UpdateSortButton();

        BuildHub();
        // Land on the Ask page by default (visibility only - the greeting types when actually opened).
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
            // an opt-in ghost toggle; the Library deliberately does not - owner decision.)

            // Maximise cleanly. On a per-monitor-DPI-aware app with a manually-placed window,
            // WPF's default maximise mis-sizes (jumps, or spills under the taskbar) - that was
            // the glitch. Handling WM_GETMINMAXINFO pins the maximised bounds to the current
            // monitor's WORK AREA, so maximise snaps to exactly the right rectangle.
            // Once only: StyleChrome re-runs on every theme swap, and AddHook does not
            // de-duplicate - each toggle silently appended another copy of WndProc.
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
        else if (_items.Count > 0) Select(_items[0].Id, byUser: false);
        else DetailBody.Content = Placeholder("Select a meeting or note, or make a new one.");
    }

    // ══════════════════════ Hub: sidebar nav · Ask landing · Templates · mascot ══════════════════════

    string _page = "ask";
    bool _myNotesOnly;                       // book filter: show only hand-written (manual) notes
    LibrarySort _sort = LibrarySort.Newest;  // list ordering (persisted)
    // What the list currently shows. Under virtualization most rows have no visual, so this - not
    // Cards.Children - is the answer to "what's in the Library" and "which one is first".
    IReadOnlyList<MeetingSummary> _items = [];
    bool _listLoaded;                        // has LoadList run? (was "are there any card visuals?")
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

    /// <summary>
    /// One sidebar nav row. The whole full-width rounded box is the button, not the word inside it.
    ///
    /// It always WAS the whole box (a transparent Border spans the rail), but at rest nothing was drawn,
    /// so an inactive item read as a floating icon and label with no button around them, and there was no
    /// hover state to prove otherwise - owner, 2026-08-06: "the library button is still a button for the
    /// word, it can be a box with the library text". Now the box lights on hover, exactly where it can be
    /// clicked, so what you can hit is what you can see.
    /// </summary>
    Border NavItem(string key, char glyph, string label)
    {
        var icon = new TextBlock { Text = glyph.ToString(), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Width = 22, TextAlignment = TextAlignment.Center };
        var text = new TextBlock { Text = label, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon); row.Children.Add(text);
        var b = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(11, 10, 11, 10),
            Margin = new Thickness(0, 0, 0, 4), MinHeight = 40, Cursor = Cursors.Hand,
            Background = Brushes.Transparent, Child = row,
        };
        // Hover only when this ISN'T the current page - the active row already carries its own fill and
        // must not flicker to a lighter one under the pointer.
        b.MouseEnter += (_, _) => { if (_page != key) b.SetResourceReference(Border.BackgroundProperty, "HoverOverlay"); };
        b.MouseLeave += (_, _) => { if (_page != key) b.Background = Brushes.Transparent; };
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

    // GridLength has no built-in animation, so lerp the pixel width on a timer with an ease - smooth,
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
        if (key == "settings") { LoadNameSettings(); LoadPinnedSettings(); LoadLibrarySettings(); }
        if (key == "library" && !_listLoaded) LoadList();
    }

    static void FadePage(UIElement e)
    {
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ── Settings page: name (the passive place to change it - confirm/cancel, success, live everywhere) ──

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
        LibNameSavedText.Text = shown.Length > 0 ? $"Saved - I'll call you {shown}." : "Saved.";
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

    // ── Settings page: pinned names ──
    //
    // The roster behind speaker naming (T8) and name correction (T2). `AppSettings.PinnedNames` has been
    // READ by NameLexicon since T2, but nothing could write it - the escape hatch existed with no handle
    // on it, so the one fix for a name the library cannot settle by frequency was hand-editing a JSON
    // file. A name typed here is TRUSTED: correction may reach further towards it, and the namer will
    // attach it to a voice on evidence that would not be enough for a name nobody vouched for.
    //
    // Stored verbatim as typed; NameLexicon.Trusted splits on commas, semicolons and newlines.

    void LoadPinnedSettings()
    {
        _nameLoading = true;
        LibPinnedBox.Text = Llm.AppSettings.Current.PinnedNames ?? "";
        LibPinnedActions.Visibility = Visibility.Collapsed;
        LibPinnedSaved.Visibility = Visibility.Collapsed;
        _nameLoading = false;
    }

    void OnLibPinnedChanged(object sender, TextChangedEventArgs e)
    {
        if (_nameLoading) return;
        var changed = (LibPinnedBox.Text ?? "") != (Llm.AppSettings.Current.PinnedNames ?? "");
        LibPinnedActions.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
        if (changed) LibPinnedSaved.Visibility = Visibility.Collapsed;
    }

    void OnLibPinnedSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = Llm.AppSettings.Current;
            var typed = LibPinnedBox.Text ?? "";
            s.PinnedNames = string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();
            s.Save();
        }
        catch (Exception ex) { App.Log($"save pinned names: {ex.Message}"); }

        LibPinnedActions.Visibility = Visibility.Collapsed;
        // Count what Toki will actually use, not what was typed - trailing commas and blank lines are
        // easy to leave behind, and "3 names" confirms the parse as well as the save.
        var n = Transcription.NameLexicon.Trusted().Count;
        LibPinnedSavedText.Text = n switch
        {
            0 => "Saved.",
            1 => "Saved - 1 name Toki will always spell right.",
            _ => $"Saved - {n} names Toki will always spell right.",
        };
        LibPinnedSaved.Visibility = Visibility.Visible;
        if (!App.Headless)
            LibPinnedSaved.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    void OnLibPinnedCancel(object sender, RoutedEventArgs e) => LoadPinnedSettings();

    // ── Settings page: re-clean saved transcripts ──
    //
    // A button, not a startup job. See Storage/TranscriptCleanup for the reasoning. Everything here is
    // about being honest while it runs: the button disables so it cannot be double-started, the status
    // line counts up so a large library does not look frozen, and the result says how many meetings
    // actually changed - including "nothing to change", which is the good outcome and should read that
    // way rather than as a failure.
    bool _recleaning;

    async void OnLibReclean(object sender, RoutedEventArgs e)
    {
        if (_recleaning) return;
        _recleaning = true;
        LibRecleanButton.IsEnabled = false;
        LibRecleanStatus.Visibility = Visibility.Visible;
        LibRecleanStatus.Text = "Reading your meetings...";

        var ui = Dispatcher;
        void Report(int done, int total) =>
            ui.BeginInvoke(new Action(() => LibRecleanStatus.Text = $"Cleaning {done} of {total}..."));

        try
        {
            var result = await Task.Run(() => Storage.TranscriptCleanup.RunAll(App.Store, null, Report));
            LibRecleanStatus.Text = result.MeetingsChanged switch
            {
                0 => $"Nothing to change - all {result.MeetingsScanned} transcripts are already clean.",
                1 => "Done - 1 transcript cleaned up. Your notes and to-dos are unchanged.",
                _ => $"Done - {result.MeetingsChanged} transcripts cleaned up. Your notes and to-dos are unchanged.",
            };
            // The open note may be one of the rewritten ones, and it is holding the old text.
            MarkDirty();
            if (_selected is { } id) Select(id, byUser: false);
        }
        catch (Exception ex)
        {
            App.Log($"re-clean failed: {ex.Message}");
            LibRecleanStatus.Text = "Could not finish. Nothing was changed.";
        }
        finally
        {
            LibRecleanButton.IsEnabled = true;
            _recleaning = false;
        }
    }

    // ── Settings page: everything else (the notch-settings consolidation - DONE 2026-08-04) ──
    //
    // This page is THE home for preferences. All of it goes through the existing one-path
    // App/service calls (no duplicated persistence): theme (App.SwitchTheme), glassy notch
    // (App.SetGlassyNotch), ghost (App.SetGhostMode), Google Calendar (CalendarService.Instance),
    // About (PillState.About), and the AI-summaries mode + BYO-API-key flow - moved WHOLE from the
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

    // ── Settings page: the two calendar connections ──
    //
    // Google and Outlook are independent and can both be on. One method draws either row, so the two
    // can never drift into behaving differently - which is the failure mode when a second provider is
    // bolted on beside a first.
    void RefreshLibGoogle()
    {
        var svc = Toki.Calendar.CalendarService.Instance;
        DrawCalendarRow(Toki.Calendar.CalendarProvider.Google,
            LibGoogleStatus, LibGoogleConnectBtn, LibGoogleDisconnectBtn, LibGoogleMark);
        DrawCalendarRow(Toki.Calendar.CalendarProvider.Outlook,
            LibOutlookStatus, LibOutlookConnectBtn, LibOutlookDisconnectBtn, LibOutlookMark);
        _ = svc;
    }

    static void DrawCalendarRow(Toki.Calendar.CalendarProvider provider,
        TextBlock status, Button connect, Button disconnect, Border mark)
    {
        var svc = Toki.Calendar.CalendarService.Instance;
        var connected = svc.IsConnectedTo(provider);
        var configured = svc.IsConfiguredFor(provider);

        // Connected: the provider mark appears and the button shrinks to "Reconnect" - the row stops
        // being a call to action and becomes a statement of fact you can check at a glance.
        mark.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        disconnect.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

        if (connected)
        {
            status.Text = svc.AccountEmailFor(provider) is { Length: > 0 } who
                ? $"Connected as {who}." : $"Connected to {provider.Name()}.";
            connect.Content = "Reconnect";
            connect.IsEnabled = true;
            connect.Padding = new Thickness(14, 9, 14, 9);
            return;
        }

        status.Text = configured
            ? "Not connected. Sign in to see upcoming meetings and let Toki prep you."
            : $"{provider.ShortName()} needs a one-time setup first - the button explains.";
        connect.Content = $"Connect {provider.ShortName()}";
        // ENABLED even when the sign-in is not configured. A greyed-out button is indistinguishable
        // from a broken one (the owner's exact report), and it cannot say what is missing. Clicking
        // it explains and opens the setup guide instead.
        connect.IsEnabled = true;
        connect.Padding = new Thickness(20, 9, 20, 9);
    }

    void OnLibGoogleConnect(object sender, RoutedEventArgs e) =>
        ConnectCalendar(Toki.Calendar.CalendarProvider.Google, LibGoogleStatus, LibGoogleConnectBtn);

    void OnLibOutlookConnect(object sender, RoutedEventArgs e) =>
        ConnectCalendar(Toki.Calendar.CalendarProvider.Outlook, LibOutlookStatus, LibOutlookConnectBtn);

    async void ConnectCalendar(Toki.Calendar.CalendarProvider provider, TextBlock status, Button button)
    {
        var svc = Toki.Calendar.CalendarService.Instance;
        if (!svc.IsConfiguredFor(provider))
        {
            // Say exactly what is missing and where the fix lives. The one-time setup is registering
            // Toki with the provider (free, ~10 min) - the same step Google needed before its button
            // could work. A greyed button or a bare "not set up" is just a nicer way of being broken;
            // this at least names the next step.
            status.Text = provider == Toki.Calendar.CalendarProvider.Outlook
                ? "One-time setup: register Toki with Microsoft (free, ~10 min) - see docs\\outlook-setup.md, then this button works."
                : "One-time setup: create Toki's Google OAuth client (free) - see docs\\google-setup.md, then this button works.";
            return;
        }
        button.IsEnabled = false;
        status.Text = "Opening your browser - sign in and click Allow…";
        try { await svc.ConnectAsync(provider); }
        catch (Toki.Calendar.GoogleAuthException ex) { status.Text = ex.Message; }
        catch { status.Text = $"Couldn’t connect to {provider.ShortName()}."; }
        finally { button.IsEnabled = true; RefreshLibGoogle(); }
    }

    void OnLibGoogleDisconnect(object sender, RoutedEventArgs e) =>
        DisconnectCalendar(Toki.Calendar.CalendarProvider.Google, LibGoogleDisconnectBtn);

    void OnLibOutlookDisconnect(object sender, RoutedEventArgs e) =>
        DisconnectCalendar(Toki.Calendar.CalendarProvider.Outlook, LibOutlookDisconnectBtn);

    async void DisconnectCalendar(Toki.Calendar.CalendarProvider provider, Button button)
    {
        button.IsEnabled = false;
        try { await Toki.Calendar.CalendarService.Instance.DisconnectAsync(provider); }
        finally { button.IsEnabled = true; RefreshLibGoogle(); }
    }

    // The "Built with" credits live on the notch's About screen (one canonical copy of the licence
    // list) - this row summons it, same as the notch Settings row does.
    void OnLibOpenSource(object sender, MouseButtonEventArgs e) => App.State.ForceForDemo(PillState.About);

    void OnLibAboutHover(object sender, MouseEventArgs e)
    { if (sender is Border b) b.SetResourceReference(Border.BackgroundProperty, "Chip"); }

    void OnLibAboutUnhover(object sender, MouseEventArgs e)
    { if (sender is Border b) b.Background = Brushes.Transparent; }

    // ── Ask landing: hero mascot on a soft glow, a typed greeting, one identity line, chips ──

    // One line that says what Toki IS - shown under the greeting, restored after voice hints.
    const string LandingHint = "Your meeting assistant - decisions, tasks, who said what. All on this PC.";

    static readonly string[] Greetings =
    {
        "Hi {0}, how are you doing?", "Hey {0} - what's on your mind?", "Welcome back, {0}.",
        "Good to see you, {0}.", "Ready when you are, {0}.", "What can I find for you, {0}?",
        "Hi {0} - ask me about your meetings.",
    };

    void RefreshGreeting()
    {
        if (Messages.Children.Count > 0) return;   // a conversation is going - keep it, don't reset to the landing
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

    // Reveal the greeting one character at a time - slow and smooth, like it's being written.
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
        // BACK by the redesign (figma 348:5438 draws exactly this 2x2 chip block; the earlier veto
        // was about the old oval look, reversed with the frame). Four invitations that show the RANGE
        // of what Ask can do - commitments, owners, summaries, decisions.
        Suggestions.Children.Clear();
        Suggestions.MaxWidth = 560;
        foreach (var q in new[]
        {
            "What did I commit to this week?",
            "What's still open from my last meeting?",
            "Summarize yesterday's calls",
            "What did we decide recently?",
        })
            Suggestions.Children.Add(SuggestionChip(q));
        Suggestions.Visibility = Visibility.Visible;
    }

    // Suggestion chips: quiet capsules that light on hover. Real BUTTONS (a Border with a mouse-up
    // inside a page of real buttons is the mistake this codebase has made three times).
    Button SuggestionChip(string text)
    {
        var b = new Button
        {
            Content = text, Style = (Style)FindResource("PillButton"),
            FontSize = 11.5, FontWeight = FontWeights.Normal,
            Padding = new Thickness(15, 8, 15, 9), Margin = new Thickness(4, 0, 4, 8),
        };
        b.SetResourceReference(Button.BackgroundProperty, "SurfaceHi");
        b.SetResourceReference(Button.ForegroundProperty, "TextSoft");
        b.Click += (_, _) => { AskBox.Text = text; OnAsk(this, new RoutedEventArgs()); };
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
                // race the pill's state change - the recording bar appearing IS the confirmation).
                if (App.IsRecording) { CommandReply(q, "Already recording - the bar's up top."); break; }
                App.StartRecordingFromCommand();
                CommandReply(q, "Recording - the bar's up top. Click it when you're done.");
                break;
            case Llm.AppCommand.UseApiModel:      CommandReply(q, App.SwitchNotesMode(Llm.NotesMode.ApiKey)); break;
            case Llm.AppCommand.UseLocalModel:    CommandReply(q, App.SwitchNotesMode(Llm.NotesMode.Local)); break;
            case Llm.AppCommand.UseHeuristicMode: CommandReply(q, App.SwitchNotesMode(Llm.NotesMode.HeuristicOnly)); break;
            case Llm.AppCommand.ThemeLight:       CommandReply(q, App.SwitchTheme(light: true)); break;
            case Llm.AppCommand.ThemeDark:        CommandReply(q, App.SwitchTheme(light: false)); break;
            case Llm.AppCommand.GhostOn:          CommandReply(q, App.SetGhostMode(on: true)); break;
            case Llm.AppCommand.GhostOff:         CommandReply(q, App.SetGhostMode(on: false)); break;
            case Llm.AppCommand.Help:
                CommandReply(q, "I can answer about your meetings - decisions, tasks, who said what - and do quick things like “start taking notes”, “new note”, “open templates”, “switch to dark theme”, or “ghost mode off”. Just ask.");
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
        catch (Exception ex) { App.Log($"ask failed: {ex.Message}"); ans = new Llm.NotesQa.Answer("Something went wrong - try again.", [], false); }
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
            VoiceHint("Couldn't reach the microphone - check Windows mic permissions.");
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
            if (!heard) VoiceHint("Didn't catch anything - tap the mic and speak.");
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

        if (text is null) { VoiceHint("Couldn't transcribe that - try again."); return; }
        if (text.Length == 0) { VoiceHint("Didn't catch that - try again."); return; }

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

    // A small transient hint - on the landing it uses AskHint; in a conversation, a dim Toki note.
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

    // Toki's answer - selectable text + clickable source-meeting chips.
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

    // A fun "cooking up an answer" loader - a Toki-pink bar sliding across + rotating status words.
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
        MyNotesToggle.ToolTip = _myNotesOnly ? "Showing your notes only - tap to show everything" : "Show only my hand-written notes";
    }

    // ── Templates page ──
    void BuildTemplatesPage()
    {
        TemplatesList.Children.Clear();
        // Same PageTitle / PageSubtitle as Settings - the two pages used to pick their own sizes and
        // weights, and the mismatch read as a different typeface.
        TemplatesList.Children.Add(new TextBlock { Text = "Templates", Style = St("PageTitle") });
        TemplatesList.Children.Add(new TextBlock
        {
            Text = "Toki picks the shape that fits each meeting. Open any note to switch it by hand.",
            Style = St("PageSubtitle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 20),
        });

        // Default shape for NEW meetings: Auto-detect, or force one for every meeting.
        var defRow = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        // The ramp's own overline style, not a hand-rolled 9px Bold.
        defRow.Children.Add(new TextBlock { Text = "DEFAULT SHAPE FOR NEW MEETINGS", Style = St("SectionHeader"), Margin = new Thickness(2, 0, 0, 5) });
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
        stack.Children.Add(new TextBlock { Text = t.Name, Style = St("SectionTitle") });
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
    // a Border+MouseLeftButtonUp, which the read-only RichTextBox (the note now lives in) SWALLOWS - so it
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
        else { status.Text = "Couldn't rewrite - no transcript or model available."; status.Visibility = Visibility.Visible; }
    }

    void LoadList()
    {
        IReadOnlyList<MeetingSummary> items;
        try { items = App.Store.List(string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text); }
        catch (Exception ex) { App.Log($"library window load: {ex.Message}"); items = []; }

        if (_myNotesOnly) items = items.Where(m => m.Kind == MeetingKind.Manual).ToList();
        items = SortItems(items);

        CountLabel.Text = _myNotesOnly ? $"{items.Count} · my notes" : $"{items.Count} · all on this PC";

        // (A sticky note used to appear here as its own Keep/Discard card. The owner's verdict was that
        // a row you can't open, sitting among meetings it isn't one of, is just confusing - so a sticky
        // note now lives INSIDE its meeting's note, behind the "Sticky note" button. See RenderMeeting.)
        //
        // Handing the list over is the whole "build" now: MeetingList calls Card() only for the rows it
        // actually realises. The list is kept in _items because the visuals are no longer a reliable
        // census of what's in the Library - most rows have none.
        _items = items;
        _listLoaded = true;
        _cardFills.Clear();   // rebuilt rows register fresh inner-card fills; stale ones must not linger
        Cards.ItemsSource = items;

        CardsEmpty.Text =
            _myNotesOnly ? "No hand-written notes yet - tap + to write one."
            : _sort == LibrarySort.OpenTasks ? "No meetings with open tasks."
            : string.IsNullOrWhiteSpace(SearchBox.Text) ? "Nothing yet." : "No matches.";
        CardsEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Sort the list per the chosen mode. OpenTasks filters to meetings that HAVE open tasks and ranks
    // by how many. (There was a "Manual" mode holding the drag-reorder order; it went with drag.)
    IReadOnlyList<MeetingSummary> SortItems(IReadOnlyList<MeetingSummary> items) => _sort switch
    {
        LibrarySort.Oldest => items.OrderBy(m => m.StartedAt).ToList(),
        LibrarySort.Longest => items.OrderByDescending(m => m.DurationMs).ThenByDescending(m => m.StartedAt).ToList(),
        LibrarySort.Shortest => items.OrderBy(m => m.DurationMs).ThenByDescending(m => m.StartedAt).ToList(),
        LibrarySort.OpenTasks => items.Where(m => m.OpenActions > 0)
            .OrderByDescending(m => m.OpenActions).ThenByDescending(m => m.StartedAt).ToList(),
        _ => items.OrderByDescending(m => m.StartedAt).ToList(),   // Newest, and the fallback
    };

    static readonly (string Label, LibrarySort Mode)[] SortModes =
    {
        ("Newest first", LibrarySort.Newest),
        ("Oldest first", LibrarySort.Oldest),
        ("Longest first", LibrarySort.Longest),
        ("Shortest first", LibrarySort.Shortest),
        ("Most open tasks", LibrarySort.OpenTasks),
    };

    // Toki's own themed popup (never the Win32 ContextMenu chrome) - same surface as the Copy menu,
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
        UpdateSortButton();
        LoadList();
    }

    void UpdateSortButton()
    {
        if (SortBtn is null) return;
        if (_sort != LibrarySort.Newest) SortBtn.Foreground = (Brush)FindResource("Coral");
        else SortBtn.ClearValue(ForegroundProperty);
        SortBtn.ToolTip = "Sort & filter - " + _sort switch
        {
            LibrarySort.Newest => "newest first",
            LibrarySort.Oldest => "oldest first",
            LibrarySort.Longest => "longest first",
            LibrarySort.Shortest => "shortest first",
            LibrarySort.OpenTasks => "most open tasks",
            _ => "newest first",
        };
    }

    // ── perf harness seams (--uitest only; never used by the app) ──

    /// <summary>An offscreen Library wired to the REAL store, for timing the work a click actually does.</summary>
    internal static LibraryWindow PerfHarness()
    {
        var w = new LibraryWindow { Left = -32000, Top = -32000, Width = 960, Height = 640, ShowActivated = false, ShowInTaskbar = false };
        w.Show();
        w.ShowPage("library", animate: false);
        return w;
    }

    internal void PerfShowPage(string key) => ShowPage(key, animate: false);
    internal void PerfLoadList() { LoadList(); UpdateLayout(); }
    internal void PerfQueryOnly() { _ = App.Store.List(null); }
    internal void PerfBuildOnly() { LoadList(); }   // no UpdateLayout: creation cost only
    internal void PerfHighlight(long id) { _selected = id; HighlightSelectedCard(); UpdateLayout(); }
    internal void PerfRenderMeeting(long id)
    {
        if (App.Store.Get(id) is { } m) { RenderMeeting(m); UpdateLayout(); }
    }

    /// <summary>Repaint just the selection, by walking the cards already on screen (each carries its
    /// meeting id in Tag). O(visible rows) brush assignments instead of a full rebuild.
    ///
    /// Only REALISED rows are touched, because off-screen meetings have no visual to paint. That is why
    /// the selected id lives in <c>_selected</c> and <see cref="Card"/> reads it when a row is built:
    /// scrolling the selected meeting back into view rebuilds it already highlighted.</summary>
    void HighlightSelectedCard()
    {
        foreach (var row in Cards.Realised())
        {
            if (row is not Border host || host.Tag is not long id) continue;
            // The fill lives on the INNER card (the redesign's date column sits OUTSIDE it and must
            // never highlight); the outer host is only the hit target + id carrier.
            if (_cardFills.TryGetValue(id, out var fill))
                fill.Background = id == _selected ? (Brush)FindResource("Chip") : Brushes.Transparent;
        }
    }

    /// <summary>
    /// A list row in the redesign's shape (figma 349:6743): the DATE lives OUTSIDE the card as bare
    /// type (day 16 Black over month 8 Bold at 50%), so the list's left edge reads as a calendar;
    /// the card carries a per-meeting accent bar, the title, a meta line, a two-line snippet, and the
    /// "n open" / "n people" chips. Only the selected card has a fill; hover shows a soft one.
    /// </summary>
    Border Card(MeetingSummary m)
    {
        var selected = _selected == m.Id;

        // The bare date column, outside the card.
        var date = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 0, 0), Width = 30 };
        date.Children.Add(new TextBlock
        {
            Text = m.StartedAt.Day.ToString(), FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 16, FontWeight = FontWeights.Black, Foreground = (Brush)FindResource("Text"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        date.Children.Add(new TextBlock
        {
            Text = m.StartedAt.ToString("MMM").ToUpperInvariant(), Style = St("Dim"), FontSize = 8,
            FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0),
        });

        // The per-meeting accent bar inside the card (stable colour per id).
        var accent = new Border
        {
            Width = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 2, 10, 2),
            Background = (Brush)FindResource(CardAccentKey(m.Id)), VerticalAlignment = VerticalAlignment.Stretch,
        };

        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = m.Title, Style = St("Body"), FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        content.Children.Add(new TextBlock { Text = CardMeta(m), Style = St("Dim"), FontSize = 9.5, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(m.Tldr))
            content.Children.Add(new TextBlock { Text = m.Tldr, Style = St("Dim"), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 0) });

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        if (m.OpenActions > 0) chips.Children.Add(Chip($"{m.OpenActions} open", "Amber", "AmberDark"));
        if (m.PeopleCount > 0) chips.Children.Add(Chip($"{m.PeopleCount} {(m.PeopleCount == 1 ? "person" : "people")}", "TextDim", "Chip"));
        if (chips.Children.Count > 0) content.Children.Add(chips);

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.Children.Add(accent);
        Grid.SetColumn(content, 1);
        inner.Children.Add(content);

        var card = new Border
        {
            Background = selected ? (Brush)FindResource("Chip") : Brushes.Transparent,
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 11, 12, 12),
            Cursor = Cursors.Hand, Child = inner,
        };

        var row = new Grid { Margin = new Thickness(0, 0, 2, 3), Tag = m.Id };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(date);
        Grid.SetColumn(card, 2);
        row.Children.Add(card);

        // The OUTER border keeps the id-Tag contract every consumer relies on (selection highlight,
        // perf test, hover) - and the whole row is the click target, date column included.
        var host = new Border { Background = Brushes.Transparent, Child = row, Tag = m.Id, Cursor = Cursors.Hand };
        host.MouseEnter += (_, _) => { if (_selected != m.Id) card.Background = (Brush)FindResource("SurfaceHi"); };
        host.MouseLeave += (_, _) => { if (_selected != m.Id) card.Background = Brushes.Transparent; };
        host.PreviewMouseLeftButtonUp += (_, _) => Select(m.Id);
        _cardFills[m.Id] = card;   // the highlight paints the INNER card, not the outer host
        return host;
    }

    // The inner card fill per meeting id - HighlightSelectedCard repaints these (the outer border's
    // background must stay transparent or the date column would highlight too).
    readonly Dictionary<long, Border> _cardFills = new();

    static readonly string[] CardAccents = { "AccentAmber", "AccentTeal", "Blue", "Green", "Pink", "Purple" };
    static string CardAccentKey(long id) => CardAccents[(int)(((id % 6) + 6) % 6)];

    // (Drag-to-reorder lived here. Removed 2026-08-12 - owner: "it does not add much productivity as I
    //  thought it would". It was also the one feature virtualization silently breaks, because it walked
    //  Cards.Children BY INDEX, and under virtualization those are positions in the realised window
    //  rather than in the list. The stored `sort_order` column and MeetingStore.Reorder are left alone;
    //  nothing writes them now, and the "Manual (drag order)" sort mode went with the feature.)

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
    bool _viewedByUser;             // true when the USER chose it (click / focus) - not an auto-select
    static bool _warming;           // true during the off-screen startup pre-warm (nothing is "on screen")

    /// <summary>The meeting note ACTUALLY on the user's screen right now: the Library window is visible
    /// (not the off-screen warm-up), the rendered note is a real meeting (not a manual note), and the
    /// user chose it (a click or an explicit open - never the list's silent auto-select, which is what
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
        // Move the highlight, do NOT rebuild the list. This used to call LoadList(), so every click on a
        // note re-queried the store and re-created ~500 visual elements for 52 meetings in order to
        // change one background brush. That single line was most of the "slow and clunky" the owner
        // reported (2026-08-11).
        HighlightSelectedCard();
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
        // (Screen-aware Ask grounding is DERIVED from _viewedRecord/_viewedByUser - see VisibleMeeting.)
        // The action toolbar (Template left · Copy/Delete right) rides at the TOP of the scroll document,
        // so the whole note - toolbar, title, body - is one scroll from the very top of the pane.
        var toolbar = new StackPanel();
        var row = new DockPanel { LastChildFill = false };
        // The sticky note lives inside this note (owner, 2026-08-06). Built BEFORE the document, which
        // places its button under the title and the note itself before the transcript.
        _sticky = new Views.MeetingStickyView(m.Id, App.Store.StickyForMeeting(m.Id));

        // The redesign's action group (figma 348:2933, top-right): ONE rounded container holding icon
        // buttons - copy (opens the copy menu, flashes a green check after copying) and a RED trash.
        // Text-label buttons are gone from this pane; the icons carry tooltips.
        var group = new StackPanel { Orientation = Orientation.Horizontal };
        var groupShell = new Border
        {
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(3),
            Child = group, VerticalAlignment = VerticalAlignment.Top,
        };
        groupShell.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");
        groupShell.SetResourceReference(Border.BorderBrushProperty, "Hairline");

        Button ToolIcon(string glyph, string tip, string? fgKey = null)
        {
            var tb = new TextBlock
            {
                Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            // An explicit Foreground on the GLYPH (not the button) survives the style's hover
            // recolour - the trash must stay red under the pointer.
            if (fgKey is not null) tb.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
            var b = new Button { Content = tb, Style = (Style)FindResource("IconButton"), Width = 32, Height = 30, ToolTip = tip };
            group.Children.Add(b);
            return b;
        }

        var copyBtn = ToolIcon("", "Copy this note");
        void CopyFlash(string text)
        {
            if (!CopyText(text)) return;
            var was = copyBtn.Content;
            copyBtn.Content = new TextBlock
            {
                Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13,
                Foreground = (Brush)FindResource("Green"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            t.Tick += (_, _) => { t.Stop(); copyBtn.Content = was; };
            t.Start();
        }
        copyBtn.Click += (_, _) =>
        {
            var items = new List<(string, Action)>
            {
                ("Copy everything", () => CopyFlash(NoteRenderer.FullText(m))),
                ("Summary + to-dos", () => CopyFlash(NoteRenderer.NotesText(m))),
            };
            if (m.Transcript.Count > 0) items.Add(("Transcript only", () => CopyFlash(NoteRenderer.TranscriptText(m))));
            items.Add(("Reveal note file", () => RevealNoteFile(m)));
            ShowMenu(groupShell, items);
        };
        ToolIcon("", "Delete this meeting", "Danger").Click += (_, _) => Delete(m.Id);

        DockPanel.SetDock(groupShell, Dock.Right);
        row.Children.Add(groupShell);

        var status = new TextBlock { Style = St("Dim"), FontSize = 10.5, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        if (m.Kind == MeetingKind.Meeting && m.Transcript.Count > 0)
        {
            var tmpl = TemplatePicker(m, status);
            DockPanel.SetDock(tmpl, Dock.Left);
            row.Children.Add(tmpl);
        }
        toolbar.Children.Add(row);
        toolbar.Children.Add(status);

        var body = NoteRenderer.MeetingDocument(m, spacious: true, topBar: toolbar, sticky: _sticky,
            resolving: App.IsResolvingSpeakers(m.Id),
            renameSpeaker: (from, to) =>
            {
                // One rename, every line that voice said. Re-read from the store rather than patching
                // the in-memory copy, so what is on screen is what was actually written.
                App.Store.RenameSpeaker(m.Id, from, to);
                if (App.Store.Get(m.Id) is { } fresh) { MarkdownMirror.Write(fresh); RenderMeeting(fresh); }
            });
        DetailBody.Content = body;
        // Start at the very TOP once laid out (a fresh RichTextBox occasionally came up scrolled down).
        if (body is RichTextBox rtb)
            Dispatcher.BeginInvoke(new Action(() => rtb.ScrollToHome()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// A hand-written note, laid out as a DOCUMENT rather than a form.
    ///
    /// The owner's note on the old version was that it looked machine-made, and the reasons were
    /// structural rather than decorative:
    ///
    ///   • It ran the full width of the pane. A line of text 900px long is genuinely hard to read - the
    ///     eye loses its place returning to the left edge. Every word processor solves this the same
    ///     way, with a fixed MEASURE and margins either side, and that is most of what makes a page
    ///     look like a page. The column is capped and centred.
    ///   • A hard rule sat under the title, which is what a FORM does between fields. A document
    ///     separates its title from its body with space, and puts the quiet facts (when, how long)
    ///     under the title instead.
    ///   • Delete sat immediately after the last line of writing, so the document appeared to end in a
    ///     button. It now lives in a footer, past a generous gap, with the save state beside it.
    ///
    /// Everything here is spacing, measure and hierarchy - no new colours, no boxes. The full design
    /// and haptics revamp is still coming, and this is the layer underneath it that has to be right
    /// whatever it looks like afterwards.
    /// </summary>
    void RenderManual(MeetingRecord m)
    {
        // (A manual note never grounds Ask - VisibleMeeting filters on Kind == Meeting.)
        // Stretch + MaxWidth is WPF's way of saying "centred, up to this measure".
        var page = new StackPanel
        {
            MaxWidth = MeasureWidth, HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // The title, at the same 22/SemiBold a meeting note uses, so an open note looks the same
        // whoever wrote it. A placeholder sits behind it rather than seeding the box with the words
        // "Untitled note" - text you have to delete before you can type is a small hostile act.
        var titleBox = new TextBox
        {
            Text = m.Title == "Untitled note" ? "" : m.Title,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("Text"), CaretBrush = (Brush)FindResource("Text"),
            FontSize = 22, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("UiFont"),
            Padding = new Thickness(0), TextWrapping = TextWrapping.Wrap,
        };
        var placeholder = new TextBlock
        {
            Text = "Untitled note", FontSize = 22, FontWeight = FontWeights.SemiBold,
            FontFamily = (FontFamily)FindResource("UiFont"), Foreground = (Brush)FindResource("TextDim"),
            Opacity = 0.45, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0),
            Visibility = titleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        var titleLayer = new Grid();
        titleLayer.Children.Add(placeholder);
        titleLayer.Children.Add(titleBox);
        page.Children.Add(titleLayer);

        // The quiet facts, where a document keeps them: under the title, small, dim, never bold.
        var meta = new TextBlock
        {
            Style = St("Dim"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0),
            Text = m.StartedAt.ToLocalTime().ToString("dddd d MMMM yyyy"),
        };
        page.Children.Add(meta);

        // The real editor (N1): sizes, tickable to-dos, lists, spacing, template insertion. The gap
        // above it is the space that replaced the old rule - a document breathes between its title and
        // its first line, it does not draw a border there.
        var bodyBox = new Views.NoteEditor { MinHeight = 420, Margin = new Thickness(0, 26, 0, 0) };
        bodyBox.Markdown = m.Body ?? "";
        page.Children.Add(bodyBox);

        // The footer, well clear of the writing. Save state on the left where it is glanceable, the
        // destructive action on the right where nobody reaches by accident.
        var status = new TextBlock
        {
            Style = St("Dim"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0, Text = "Saved",
        };
        var footer = new Grid { Margin = new Thickness(0, 44, 0, 8) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var deleteButton = SmallButton("Delete", () => Delete(m.Id), danger: true);
        Grid.SetColumn(deleteButton, 1);
        footer.Children.Add(status);
        footer.Children.Add(deleteButton);

        var footerRule = new Border
        {
            Height = 1, Background = (Brush)FindResource("Hairline"), Margin = new Thickness(0, 40, 0, 0),
        };
        page.Children.Add(footerRule);
        page.Children.Add(footer);

        DetailBody.Content = new ScrollViewer
        {
            Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(40, 34, 40, 24),
        };

        _manualFlush = () =>
        {
            var title = string.IsNullOrWhiteSpace(titleBox.Text) ? "Untitled note" : titleBox.Text.Trim();
            try { App.Store.UpdateTitleAndBody(m.Id, title, bodyBox.Markdown); MarkDirty(); }
            catch (Exception ex) { App.Log($"manual note save failed: {ex.Message}"); }
            status.Text = "Saved"; FlashStatus(status);
        };
        void Schedule() { status.Text = "Saving…"; status.Opacity = 0.75; status.BeginAnimation(OpacityProperty, null); _manualDebounce.Stop(); _manualDebounce.Start(); }
        titleBox.TextChanged += (_, _) =>
        {
            placeholder.Visibility = titleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Schedule();
        };
        bodyBox.Changed += (_, _) => Schedule();   // covers typing AND ticking a box
    }

    /// <summary>
    /// The reading measure for a written note, in device-independent pixels.
    ///
    /// Typography's rule of thumb is 45-75 characters a line; at Toki's body size that lands near 700.
    /// Wider than this and the eye starts losing its place on the carriage return, which is the single
    /// biggest difference between text that feels composed and text that feels dumped on a page.
    /// </summary>
    const double MeasureWidth = 720;

    // Show a status word, then let it fade out - "saved" should reassure, not linger as chrome.
    static void FlashStatus(UIElement e)
    {
        e.BeginAnimation(OpacityProperty, null);
        e.Opacity = 1;
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1200)) { BeginTime = TimeSpan.FromMilliseconds(700) });
    }

    // Save any pending manual-note or sticky-note edit immediately (on switching notes or closing the
    // window). Both are text the user typed by hand; neither may be left sitting in a debounce timer.
    void FlushManual()
    {
        try { _sticky?.Flush(); } catch (Exception ex) { App.Log($"sticky flush: {ex.Message}"); }
        _sticky = null;

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

    // Typing "pricing" used to run SEVEN full-text queries and seven complete list rebuilds, one per
    // letter, each one throwing away the visual tree it had just built. Now the keystrokes settle first.
    // 180ms is under the ~200ms that reads as instant, and above a fast typist's inter-key gap.
    readonly System.Windows.Threading.DispatcherTimer _searchDebounce =
        new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(180) };

    void OnSearch(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    void CopyTranscript(MeetingRecord m)
    {
        try { Clipboard.SetText(NoteRenderer.TranscriptText(m)); } catch (Exception ex) { App.Log($"copy failed: {ex.Message}"); }
    }

    // Copy button → a clean THEMED dropdown (never the Win32 ContextMenu): Summary+to-dos, Transcript,
    // or reveal the note's markdown file on disk.
    /// <summary>
    /// Copy, as ONE split button: a single rounded pill with a hairline seam between the label and the
    /// caret.
    ///
    /// It used to be two separate pills a pixel apart, which read as two buttons that happened to be
    /// touching (owner, 2026-08-06: "ugly"). One container with an internal divider is the standard
    /// split-button shape and says what it is - one action, with more options behind the arrow. Each
    /// half still lights independently on hover so it's obvious they do different things.
    /// </summary>
    FrameworkElement CopyButton(MeetingRecord m)
    {
        var mainLabel = new TextBlock
        {
            Text = "Copy", FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            FontFamily = (FontFamily)FindResource("UiFont"),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        mainLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text");

        var main = SeamButton(mainLabel, new Thickness(15, 0, 13, 0), new CornerRadius(13, 0, 0, 13));
        var caretGlyph = new TextBlock
        {
            Text = "▾", FontSize = 10, FontFamily = (FontFamily)FindResource("UiFont"),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        caretGlyph.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        var caret = SeamButton(caretGlyph, new Thickness(10, 0, 10, 0), new CornerRadius(0, 13, 13, 0));

        var seam = new Border { Width = 1, Margin = new Thickness(0, 7, 0, 7) };
        seam.SetResourceReference(Border.BackgroundProperty, "Hairline");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(main.Host);
        row.Children.Add(seam);
        row.Children.Add(caret.Host);

        var shell = new Border
        {
            CornerRadius = new CornerRadius(13), Height = 32, Margin = new Thickness(0, 0, 8, 0),
            Child = row, ClipToBounds = true,
        };
        shell.SetResourceReference(Border.BackgroundProperty, "Chip");

        void Copy(string text) { if (CopyText(text)) FlashLabel(mainLabel, "Copied ✓"); }
        main.Button.Click += (_, _) => Copy(NoteRenderer.FullText(m));
        caret.Button.Click += (_, _) =>
        {
            var items = new List<(string, Action)>
            {
                ("Copy everything", () => Copy(NoteRenderer.FullText(m))),
                ("Summary + to-dos", () => Copy(NoteRenderer.NotesText(m))),
            };
            if (m.Transcript.Count > 0) items.Add(("Transcript only", () => Copy(NoteRenderer.TranscriptText(m))));
            items.Add(("Reveal note file", () => RevealNoteFile(m)));
            ShowMenu(shell, items);
        };
        return shell;
    }

    /// <summary>One half of the split button: a chrome-free Button whose padded box is the hit target,
    /// with its own hover fill so the two halves read as separate actions inside one pill.</summary>
    (Button Button, FrameworkElement Host) SeamButton(FrameworkElement content, Thickness padding, CornerRadius corners)
    {
        var fill = new Border
        {
            Padding = padding, CornerRadius = corners, Background = Brushes.Transparent, Child = content,
        };
        var btn = new Button
        {
            Content = fill, Cursor = Cursors.Hand, Focusable = false,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Template = TransparentButtonTemplate(), VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        btn.MouseEnter += (_, _) => fill.SetResourceReference(Border.BackgroundProperty, "HoverOverlay");
        btn.MouseLeave += (_, _) => fill.Background = Brushes.Transparent;
        return (btn, btn);
    }

    // Brief inline confirmation on a button, then restore its label - so a copy never feels silent.
    static void Flash(Button b, string msg)
    {
        var was = b.Content;
        b.Content = msg;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        t.Tick += (_, _) => { b.Content = was; t.Stop(); };
        t.Start();
    }

    /// <summary>Same confirmation, on a label inside a composed button (the split Copy).</summary>
    static void FlashLabel(TextBlock label, string msg)
    {
        var was = label.Text;
        label.Text = msg;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        t.Tick += (_, _) => { label.Text = was; t.Stop(); };
        t.Start();
    }

    // A rounded, outlined, THEMED popup of hover-able rows - our own menu, matching the app.
    // With `selected`, rows carry a leading check column and the active row shows a pink tick
    // (the sort/filter menu) - still dividers-not-boxes, never the Win32 menu chrome.
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

    // A button that renders exactly its content, no chrome - so our themed rows show through.
    static ControlTemplate TransparentButtonTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        t.VisualTree = cp;
        return t;
    }

    // Returns true only if the text actually reached the clipboard. The Win32 clipboard can be
    // momentarily held by another app (COMException "OpenClipboard failed") - retry a few times.
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

    // Toki already mirrors every note to a Markdown file - surface it (file export the easy way).
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
        // The dialog promises the AUDIO goes too - DiscardMeeting is the one true delete
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
            // Height matched to the split Copy button so a toolbar row lines up instead of stepping.
            Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(15, 0, 15, 0), Height = 32, FontSize = 11.5,
        };
        // Destructive = red TEXT on the same neutral button as Copy (not a loud red-filled pill) - clean,
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
