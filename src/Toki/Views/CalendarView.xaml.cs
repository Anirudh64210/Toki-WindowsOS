using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Toki.Calendar;
using Toki.State;

namespace Toki.Views;

/// <summary>
/// The Calendar page: ONE continuous agenda scroll over the ~31-day horizon (week bands separate it -
/// no week paging, so only the back arrow remains), a scroll-synced subtitle + a Today pill that appears
/// when you scroll away, per-event Join, a natural-language create composer, and delete (undo for solo
/// events, confirm for guested). Fixed height + internal scroll. Talks only to the provider-neutral facade + models.
/// </summary>
public partial class CalendarView : UserControl
{
    static bool _lastAddMeet = true;   // sticky across opens within a session

    int _durationMin = 30;
    bool _addMeet;
    QuickEvent? _parsed;
    CalEvent? _pendingUndo;
    CalEvent? _editing;                // non-null while the composer is editing an existing event
    string? _lastRenderSig;            // render-key of the last agenda - skip redundant rebuilds
    bool _entranceDone;                // the entrance stagger runs ONCE, never on a refresh
    readonly List<(FrameworkElement El, string Label, int Count)> _bands = new();  // week separators, for scroll-synced subtitle

    readonly DispatcherTimer _parseDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer _nowTick = new() { Interval = TimeSpan.FromSeconds(60) };
    DispatcherTimer? _undoTimer;
    System.Threading.CancellationTokenSource? _cts;

    public CalendarView()
    {
        InitializeComponent();
        // Rounded 1px rim, snapped to whole device pixels (see GlassSurface.Hairline).
        Theme.GlassSurface.CrispRim(ActionBar);
        _addMeet = _lastAddMeet;
        UpdateMeetChip();
        _parseDebounce.Tick += (_, _) => { _parseDebounce.Stop(); ReparseComposer(); };
        _nowTick.Tick += (_, _) => { if (IsVisible) RenderFromSnapshot(); };

        if (App.Headless) { RenderAgenda(Dev.CalendarSamples.Events()); return; }

        // Subscribe on Loaded / release on Unloaded (the view is cached and reshown). Idempotent so a
        // repeat Loaded can't double-subscribe.
        Loaded += (_, _) =>
        {
            UpcomingCache.Instance.Changed -= OnCacheChanged; UpcomingCache.Instance.Changed += OnCacheChanged;
            CalendarService.Instance.ConnectionChanged -= OnCacheChanged; CalendarService.Instance.ConnectionChanged += OnCacheChanged;
            Theme.ThemeManager.ThemeChanged -= OnThemeChanged; Theme.ThemeManager.ThemeChanged += OnThemeChanged;
        };
        Unloaded += (_, _) =>
        {
            UpcomingCache.Instance.Changed -= OnCacheChanged;
            CalendarService.Instance.ConnectionChanged -= OnCacheChanged;
            Theme.ThemeManager.ThemeChanged -= OnThemeChanged;
            _nowTick.Stop(); _cts?.Cancel(); _cts = null;
        };
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { Refresh(); _nowTick.Start(); }
            else { _nowTick.Stop(); CloseComposer(); HideActionBar(); }
        };
    }

    void OnCacheChanged() => Dispatcher.Invoke(() => { if (IsVisible) RenderFromSnapshot(); });

    // A theme swap must recolour the code-built rows. The render-key carries the theme, so invalidating it
    // and re-rendering rebuilds every row with the new palette (fixes black-on-dark after a light→dark toggle).
    void OnThemeChanged() => Dispatcher.Invoke(() => { _lastRenderSig = null; if (IsVisible) RenderFromSnapshot(); });

    // ── loading / refresh ──

    async void Refresh(bool force = false)
    {
        if (!CalendarService.Instance.IsConnected) { ShowDisconnected(); return; }

        var snap = UpcomingCache.Instance.Snapshot;
        if (snap is not null) RenderAgenda(snap); else ShowLoading();

        try
        {
            var fresh = await UpcomingCache.Instance.GetAsync(force);
            if (!ReferenceEquals(fresh, snap)) RenderAgenda(fresh);   // avoid a double render when the cache was fresh
        }
        catch (GoogleAuthException ex) { ShowError(ex); }
        catch { ShowError(null); }
    }

    void RenderFromSnapshot()
    {
        if (!CalendarService.Instance.IsConnected) { ShowDisconnected(); return; }
        var snap = UpcomingCache.Instance.Snapshot;
        if (snap is not null) RenderAgenda(snap);
    }

    // ── the agenda (one continuous scroll over the whole ~31-day horizon; week bands separate it) ──

    static DateTime WeekStartOf(DateTime d) => d.AddDays(-(int)d.DayOfWeek);   // Sunday-start week

    void RenderAgenda(IReadOnlyList<CalEvent> all)
    {
        var now = DateTimeOffset.Now;
        var upcoming = all.Where(e => e.EndUtc > now).OrderBy(e => e.StartUtc).ToList();

        // Render-key: skip the rebuild when nothing changed (the page refreshes from several sources,
        // and re-staggering identical rows flickers). Today's date is in the key so a midnight rollover
        // rebuilds once; theme too, because code-built rows capture their brushes and MUST rebuild on a swap.
        var sig = string.Join(",", upcoming.Select(e => $"{e.Id}:{e.StartUtc.Ticks}:{e.Summary}:{e.HasMeetLink}"))
                  + "#" + DateTime.Today.Ticks + "@" + Theme.ThemeManager.Current;
        if (sig == _lastRenderSig && StatePanel.Visibility != Visibility.Visible) return;
        _lastRenderSig = sig;

        if (upcoming.Count == 0) { Subtitle.Text = "All clear"; ShowEmpty(); return; }

        HideState();
        Days.Children.Clear();
        _bands.Clear();
        var today = now.Date;
        var stagger = !_entranceDone && !App.Headless;   // entrance animates ONCE, never on refresh
        var delay = 0;

        foreach (var byWeek in upcoming.GroupBy(e => WeekStartOf(e.StartLocal.Date)).OrderBy(g => g.Key))
        {
            // The FIRST week gets a marker with no label. The header subtitle directly above already
            // reads "This week - 5 events", so printing THIS WEEK again as the first line of the
            // list was the same fact twice, in a third all-caps style, eight pixels apart. Later
            // weeks keep their label, because there it is genuinely separating one from the next.
            // The element itself always goes in: it is what the scroll-synced subtitle measures
            // against (see _bands), so it has to be in the tree and laid out even when it is blank.
            var (band, label) = WeekBand(byWeek.Key, today, showLabel: _bands.Count > 0);
            Days.Children.Add(band);
            _bands.Add((band, label, byWeek.Count()));

            foreach (var byDay in byWeek.GroupBy(e => e.StartLocal.Date).OrderBy(g => g.Key))
            {
                var day = byDay.Key;
                Days.Children.Add(DayHeader(day, day == today));
                var dayEvents = byDay.OrderBy(e => e.IsAllDay ? 0 : 1).ThenBy(e => e.StartLocal).ToList();
                var nowInserted = false;
                foreach (var e in dayEvents)
                {
                    if (day == today && !nowInserted && !e.IsAllDay && e.StartLocal > now) { Days.Children.Add(NowLine(now)); nowInserted = true; }
                    var ev = e;
                    var row = CalendarRows.AgendaRow(ev, () => BeginEdit(ev), () => BeginDelete(ev), CanEdit(ev));
                    if (stagger) { Stagger(row, delay); delay += 24; }
                    Days.Children.Add(row);
                }
            }
        }
        _entranceDone = true;
        UpdateSubtitleFromScroll();
    }

    // A week separator + its friendly label (for the scroll-synced subtitle).
    (FrameworkElement, string) WeekBand(DateTime weekStart, DateTime today, bool showLabel)
    {
        var thisWeek = WeekStartOf(today);
        var label = weekStart == thisWeek ? "This week"
                  : weekStart == thisWeek.AddDays(7) ? "Next week"
                  : $"{weekStart:MMM d} – {weekStart.AddDays(6):MMM d}";
        var tb = new TextBlock
        {
            Text = showLabel ? label.ToUpperInvariant() : string.Empty,
            FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 12,
            FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextDim"),
            Margin = showLabel ? new Thickness(2, 16, 0, 2) : default,
        };
        return (tb, label);
    }

    Border DayHeader(DateTime day, bool today)
    {
        var label = day.ToString("ddd, MMM d", CultureInfo.InvariantCulture).ToUpperInvariant();
        // ONE DAY-HEADER STYLE, and today is the SAME style in fuller ink.
        // It was Bold + Amber for today against SemiBold + TextDim for every other day, sitting
        // directly under a THIRD all-caps label for the week: three competing treatments of the same
        // kind of thing in one scrolling column. Owner, 2026-09-27: "calendar notch page has the
        // WORST text styling and symettry ... theres a weird text style for a subheading".
        // Bold is gone on the house rule (Windows 11 drops Bold from the ramp; SemiBold is the
        // emphasis weight). Amber is gone because in the light theme it resolves to #87430A, a dark
        // brown, and colour here was carrying a fact the red now-line already marks - today is the
        // only day with a now-line through it.
        var tb = new TextBlock
        {
            Text = today ? $"TODAY · {label}" : label,
            FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource(today ? "Text" : "TextDim"),
        };
        return new Border
        {
            Child = tb, Margin = new Thickness(2, 14, 0, 4),
            BorderBrush = (Brush)FindResource("Hairline"), BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 5),
        };
    }

    FrameworkElement NowLine(DateTimeOffset now)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var t = new TextBlock
        {
            Text = now.ToString("h:mm", CultureInfo.InvariantCulture), FontSize = 12, Foreground = (Brush)FindResource("Coral"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)FindResource("UiFont"),
        };
        Grid.SetColumn(t, 0);
        // A coral dot at the left end + the line.
        var col1 = new Grid();
        col1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        col1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 5, Height = 5, Fill = (Brush)FindResource("Coral"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        };
        var line = new Border
        {
            Height = 1.5, Background = (Brush)FindResource("Coral"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0), CornerRadius = new CornerRadius(1),
        };
        Grid.SetColumn(dot, 0); Grid.SetColumn(line, 1);
        col1.Children.Add(dot); col1.Children.Add(line);
        Grid.SetColumn(col1, 1);
        grid.Children.Add(t); grid.Children.Add(col1);
        return grid;
    }

    static void Stagger(UIElement el, int delayMs)
    {
        if (App.Headless) return;       // offscreen render: no clock advances, so leave rows fully visible
        if (delayMs > 8 * 24) return;   // only the first several rows animate
        var tt = new TranslateTransform(0, 6);
        el.RenderTransform = tt;
        el.Opacity = 0;
        var begin = TimeSpan.FromMilliseconds(delayMs);
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Theme.Motion.Dur(Theme.Motion.Enter)) { BeginTime = begin });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, Theme.Motion.Dur(Theme.Motion.Enter)) { BeginTime = begin, EasingFunction = Theme.Motion.EaseOut });
    }

    // ── delete + undo/confirm ──

    bool HasGuests(CalEvent e) => e.Attendees.Any(a => !a.IsSelf);

    // Editable unless someone else is the organizer (a guest can't change the host's event).
    static bool CanEdit(CalEvent e) => !e.Attendees.Any(a => a.IsOrganizer && !a.IsSelf);

    void BeginDelete(CalEvent e)
    {
        if (HasGuests(e))
            ShowActionBar("Cancel this meeting? Guests will be notified.",
                ("Delete", true, () => DoDelete(e, allowUndo: false)), ("Keep", false, HideActionBar));
        else
            DoDelete(e, allowUndo: true);   // solo event → delete now, offer Undo
    }

    async void DoDelete(CalEvent e, bool allowUndo)
    {
        HideActionBar();
        UpcomingCache.Instance.Remove(e.Id);
        RenderFromSnapshot();
        try
        {
            _cts ??= new();
            await CalendarService.Instance.DeleteAsync(e.Id, e.Provider, _cts.Token);
            if (allowUndo) OfferUndo(e);
        }
        catch (GoogleAuthException ex)
        {
            UpcomingCache.Instance.Upsert(e);   // put it back - the delete didn't take
            RenderFromSnapshot();
            ShowActionBar($"Couldn’t delete - {ex.Message}", ("OK", false, HideActionBar), null);
        }
        catch (Exception ex)
        {
            // Belt and braces: this is an async void with an OPTIMISTIC removal above - any
            // unexpected throw (cancel, a future client change) must still roll the event
            // back, or it silently vanishes from the UI while still existing at Google.
            App.Log($"calendar delete failed: {ex.Message}");
            UpcomingCache.Instance.Upsert(e);
            RenderFromSnapshot();
            ShowActionBar("Couldn’t delete - try again.", ("OK", false, HideActionBar), null);
        }
    }

    void OfferUndo(CalEvent e)
    {
        _pendingUndo = e;
        ShowActionBar($"Deleted “{e.Summary}”", ("Undo", false, Undo), null);
        _undoTimer?.Stop();
        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _undoTimer.Tick += (_, _) => { _undoTimer!.Stop(); _pendingUndo = null; HideActionBar(); };
        _undoTimer.Start();
    }

    async void Undo()
    {
        _undoTimer?.Stop();
        var e = _pendingUndo; _pendingUndo = null;
        HideActionBar();
        if (e is null) return;
        try
        {
            _cts ??= new();
            var emails = e.Attendees.Where(a => !a.IsSelf && a.Email is not null).Select(a => a.Email!).ToList();
            var recreated = await CalendarService.Instance.CreateAsync(
                new NewEvent(e.Summary, e.StartLocal, e.EndLocal, emails, e.HasMeetLink), e.Provider, _cts.Token);
            UpcomingCache.Instance.Upsert(recreated);
            RenderFromSnapshot();
        }
        catch { /* undo is best-effort */ }
    }

    // ── action bar (undo + confirm) ──

    void HideActionBar() => ShowActionBar(null, null, null);

    void ShowActionBar(string? text, (string label, bool danger, Action action)? primary, (string label, bool danger, Action action)? secondary)
    {
        if (text is null) { ActionBar.Visibility = Visibility.Collapsed; return; }
        ActionText.Text = text;
        Wire(ActionPrimary, primary);
        Wire(ActionSecondary, secondary);
        ActionBar.Visibility = Visibility.Visible;
        var tt = new TranslateTransform(0, 8);
        ActionBar.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, Theme.Motion.Dur(Theme.Motion.Enter)) { EasingFunction = Theme.Motion.EaseOut });
    }

    void Wire(Button b, (string label, bool danger, Action action)? spec)
    {
        if (b.Tag is RoutedEventHandler prev) { b.Click -= prev; b.Tag = null; }
        if (spec is not { } s) { b.Visibility = Visibility.Collapsed; return; }
        b.Visibility = Visibility.Visible;
        b.Content = s.label;
        b.Foreground = (Brush)FindResource(s.danger ? "Danger" : "Text");
        b.Background = s.danger ? (Brush)FindResource("DangerTint") : (Brush)FindResource("Chip");
        RoutedEventHandler h = (_, _) => s.action();
        b.Tag = h;
        b.Click += h;
    }

    // ── state panels ──

    void ShowLoading()
    {
        Days.Children.Clear();
        for (var i = 0; i < 3; i++) Days.Children.Add(CalendarRows.SkeletonRow());
        HideState();
    }

    void ShowEmpty()
    {
        Days.Children.Clear();
        // A quiet calendar glyph, NOT a second Toki: one mascot per surface.
        SetState(EmptyGlyph(), "Nothing coming up - all clear.", ("＋ Plan something", () => OnNew(this, new RoutedEventArgs())));
    }

    static UIElement EmptyGlyph()
    {
        var t = new TextBlock
        {
            Text = "", FontSize = 34, HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        return t;
    }

    void ShowError(GoogleAuthException? ex)
    {
        Days.Children.Clear();
        var reconnect = ex?.NeedsReconnect == true;
        SetState(null, reconnect ? "Google needs you to sign in again." : "Couldn’t reach Google Calendar.",
            (reconnect ? "Reconnect Google" : "Try again", async () =>
            {
                if (reconnect) { try { await CalendarService.Instance.ConnectAsync(); } catch { } }
                Refresh(force: true);
            }));
    }

    void ShowDisconnected()
    {
        Days.Children.Clear();
        // Whichever provider is set up gets the button; the full choice lives on the Settings page.
        var svc = CalendarService.Instance;
        var google = svc.IsConfiguredFor(CalendarProvider.Google);
        var outlook = svc.IsConfiguredFor(CalendarProvider.Outlook);

        if (!google && !outlook)
        {
            SetState(TokiArt(), "Calendar sign-in isn’t set up in this build yet.", null);
            return;
        }

        var provider = google ? CalendarProvider.Google : CalendarProvider.Outlook;
        SetState(TokiArt(), "See your week here.",
            ($"Connect {provider.ShortName()}", async () =>
            {
                try { await svc.ConnectAsync(provider); } catch { }
                Refresh(force: true);
            }));
    }

    void SetState(UIElement? art, string message, (string label, Action action)? cta)
    {
        StatePanel.Children.Clear();
        if (art is not null) StatePanel.Children.Add(art);
        StatePanel.Children.Add(new TextBlock
        {
            Text = message, Style = (Style)FindResource("Body"), FontSize = 13.5, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 260,
        });
        if (cta is { } c)
        {
            var b = new Button
            {
                Style = (Style)FindResource("WhiteButton"), Content = c.label, Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
            };
            b.Click += (_, _) => c.action();
            StatePanel.Children.Add(b);
        }
        StatePanel.Visibility = Visibility.Visible;
        Scroller.Visibility = Visibility.Collapsed;
    }

    void HideState() { StatePanel.Visibility = Visibility.Collapsed; Scroller.Visibility = Visibility.Visible; }

    // One mascot per surface: never a second TokiBlob here.
    static UIElement TokiArt() => EmptyGlyph();

    // ── navigation: one continuous scroll (no week paging) ──

    void OnBack(object s, RoutedEventArgs e) => App.State.TryGo(PillState.Home);

    void OnToday(object s, RoutedEventArgs e) => Scroller.ScrollToTop();

    // The header subtitle tracks whatever week is at the top of the viewport; the Today pill
    // appears only once you've scrolled away from the top.
    void OnScroll(object s, ScrollChangedEventArgs e)
    {
        UpdateSubtitleFromScroll();
        var away = Scroller.VerticalOffset > 24;
        if (away && TodayButton.Visibility != Visibility.Visible)
        {
            TodayButton.Visibility = Visibility.Visible;
            TodayButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Theme.Motion.Dur(Theme.Motion.Micro)) { EasingFunction = Theme.Motion.EaseOut });
        }
        else if (!away && TodayButton.Visibility == Visibility.Visible)
        {
            var a = new DoubleAnimation(TodayButton.Opacity, 0, Theme.Motion.Dur(Theme.Motion.Exit)) { EasingFunction = Theme.Motion.EaseIn };
            a.Completed += (_, _) => { if (Scroller.VerticalOffset <= 24) TodayButton.Visibility = Visibility.Collapsed; };
            TodayButton.BeginAnimation(OpacityProperty, a);
        }
    }

    void UpdateSubtitleFromScroll()
    {
        if (_bands.Count == 0) return;
        var current = _bands[0];
        foreach (var b in _bands)
        {
            try
            {
                var y = b.El.TransformToAncestor(Scroller).Transform(new Point(0, 0)).Y;
                if (y <= 8) current = b; else break;   // the last band that's scrolled to/above the top is the current one
            }
            catch { break; }   // not laid out yet - keep the first band
        }
        Subtitle.Text = $"{current.Label} · {current.Count} event{(current.Count == 1 ? "" : "s")}";
    }

    // ── create composer ──

    void OnNew(object s, RoutedEventArgs e)
    {
        if (Composer.Visibility == Visibility.Visible && _editing is null) { CloseComposer(); return; }
        _editing = null;
        _durationMin = 30; UpdateDurationChip();
        _addMeet = _lastAddMeet; UpdateMeetChip();
        ShowComposer(true);
        PeopleBox.Text = "";
        // Prefilled: today, at the next half hour. A wrong-but-visible default is easier to
        // correct than an empty box is to fill.
        TitleBox.Text = "";
        DateBox.Text = DayOptionLabel(DateTime.Today);
        TimeBox.Text = NextSlot().ToString("h:mm tt");
        ComposerHint.Text = "Type a day and time, or pick them";
        ComposerHint.SetResourceReference(ForegroundProperty, "TextMid");
        ParsePreview.Visibility = Visibility.Collapsed;
        ComposerCreate.Content = "Create";
        _parsed = null; UpdateCreateEnabled();
        TitleBox.Focus();
    }

    void BeginEdit(CalEvent ev)
    {
        _editing = ev;
        _durationMin = Math.Max(15, (int)Math.Round((ev.EndUtc - ev.StartUtc).TotalMinutes));
        UpdateDurationChip();
        _addMeet = ev.HasMeetLink; UpdateMeetChip();
        var emails = ev.Attendees.Where(a => !a.IsSelf && a.Email is not null).Select(a => a.Email!).ToList();
        PeopleBox.Text = string.Join(", ", emails);
        ShowComposer(true);
        ComposerHint.Text = "Edit the title, or type a new day/time to move it.";
        ComposerHint.SetResourceReference(ForegroundProperty, "TextMid");
        ComposerCreate.Content = "Save";
        // Day/Time start EMPTY on edit: the fallback in ReparseComposer keeps the event's original time
        // (shown in the preview), so leaving them blank means "keep the time"; typing a day/time moves it.
        DateBox.Text = ""; TimeBox.Text = "";
        TitleBox.Text = ev.Summary;   // triggers ReparseComposer (keeps the event's time via the edit fallback)
        TitleBox.Focus();
        TitleBox.CaretIndex = TitleBox.Text.Length;
    }

    /// <summary>Render-harness hook: open the composer with sample text so its state is verifiable in a PNG.</summary>
    internal void DebugOpenComposer(string text)
    {
        ShowComposer(true);
        PeopleBox.Text = "ana@work.com";
        TitleBox.Text = "Design sync"; DateBox.Text = "tomorrow"; TimeBox.Text = "2pm";
        ReparseComposer();
    }

    void CloseComposer()
    {
        ShowComposer(false);
        TitleBox.Text = ""; DateBox.Text = ""; TimeBox.Text = ""; PeopleBox.Text = "";
        _editing = null;
    }

    /// <summary>
    /// Enter or leave the create/edit MODE: the agenda goes away, the header says what you are
    /// doing, and the New button steps aside because the thing it opens is already open.
    /// </summary>
    void ShowComposer(bool on)
    {
        Composer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        AgendaHost.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        NewButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on) TodayButton.Visibility = Visibility.Collapsed;
        HeaderTitle.Text = on ? (_editing is null ? "New meeting" : "Edit meeting") : "Calendar";
        // NO INSTRUCTION LINE UNDER THE HEADING. It read "Fill it in and press Create" - a caption
        // narrating a form the reader is already looking at, under a heading that already says "New
        // meeting", above a button that already says "Create". Owner, 2026-09-27: that line is what
        // they meant by the calendar's title card, and by "subtext within headings ... needs to be
        // removed". The subtitle slot stays for the AGENDA, where it carries a real changing fact
        // ("This week - 5 events"); in the composer it is simply empty.
        if (!on) UpdateSubtitleFromScroll();
        else Subtitle.Text = string.Empty;
    }

    void OnComposerCancel(object s, RoutedEventArgs e) => CloseComposer();

    void OnComposerField(object s, TextChangedEventArgs e)
    {
        TitlePlaceholder.Visibility = TitleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _parseDebounce.Stop(); _parseDebounce.Start();
    }

    void OnPeopleChanged(object s, TextChangedEventArgs e) =>
        PeoplePlaceholder.Visibility = PeopleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ── the composer's two pickers ──
    // Both fields take typed words ("tomorrow" beats hunting a date); the picker doubles as
    // documentation of what the field understands.

    /// <summary>The day field opens a month calendar (see <see cref="DayPicker"/>), never a flat list of days.</summary>
    void OnPickDay(object s, RoutedEventArgs e)
    {
        var current = ComposerStart()?.Date ?? DateTime.Today;
        DayPicker.Show(DatePick, current, day =>
        {
            DateBox.Text = DayOptionLabel(day);
            ReparseComposer();
        });
    }

    /// <summary>
    /// Every half hour of the WHOLE day - a time picker's job is to offer the times that exist.
    /// Opens scrolled to the current value so the full day costs nothing to navigate.
    /// </summary>
    void OnPickTime(object s, RoutedEventArgs e)
    {
        var items = new List<(string, Action?)>();
        for (var minutes = 0; minutes < 24 * 60; minutes += 30)
        {
            var label = DateTime.Today.AddMinutes(minutes).ToString("h:mm tt");
            items.Add((label, () => { TimeBox.Text = label; ReparseComposer(); }));
        }
        NotePicker.Show(TimePick, items, NearestHalfHourLabel());
    }

    /// <summary>The half-hour row the time picker should open on: the typed time rounded down, so a
    /// hand-typed "2:40 pm" still lands the scroll beside 2:30 rather than at midnight.</summary>
    string? NearestHalfHourLabel()
    {
        if (ComposerStart() is not { } start) return TimeBox.Text?.Trim();
        var rounded = start.Date.AddMinutes(Math.Floor(start.TimeOfDay.TotalMinutes / 30) * 30);
        return rounded.ToString("h:mm tt");
    }

    /// <summary>What the Day + Time boxes currently resolve to, if they resolve at all.</summary>
    DateTime? ComposerStart()
    {
        var whenText = $"{DateBox.Text} {TimeBox.Text}".Trim();
        if (whenText.Length == 0) return null;
        return QuickEventParser.Parse(whenText, DateTimeOffset.Now)?.Start?.LocalDateTime;
    }

    /// <summary>"Today", "Tomorrow", then a real date - the words people actually use for the next two
    /// days, and an unambiguous date after that.</summary>
    static string DayOptionLabel(DateTime day) =>
        day.Date == DateTime.Today ? "Today"
        : day.Date == DateTime.Today.AddDays(1) ? "Tomorrow"
        : day.ToString("d MMM yyyy");

    /// <summary>The next half hour on the clock - what a meeting created right now almost always means.</summary>
    static DateTime NextSlot()
    {
        var now = DateTime.Now;
        var rounded = now.Date.AddHours(now.Hour).AddMinutes(now.Minute < 30 ? 30 : 60);
        return rounded;
    }

    void OnComposerKey(object s, System.Windows.Input.KeyEventArgs e)
    {
        // Keep Esc/Enter inside the composer - never let them bubble to the notch (Esc would hide it).
        if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; CloseComposer(); }
        else if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; TryCreate(); }
    }

    void ReparseComposer()
    {
        var title = TitleBox.Text?.Trim() ?? "";
        // Day + Time are separate fields; parse them together so natural words still resolve
        // ("tomorrow" + "2pm" → a real start). Either alone is fine (a bare time → today/next; a bare
        // day → needs a time, flagged below).
        var whenText = $"{DateBox.Text} {TimeBox.Text}".Trim();
        var when = whenText.Length == 0 ? null : QuickEventParser.Parse(whenText, DateTimeOffset.Now);

        var start = when?.Start;
        // Editing with no new day/time typed → keep the event's original time.
        if (start is null && _editing is { } ed) start = ed.StartLocal;

        var emails = when?.Emails ?? Array.Empty<string>();
        _parsed = (title.Length > 0 || whenText.Length > 0 || start is not null)
            ? new QuickEvent(title, start, when?.End, emails)
            : null;

        // The end-time chip depends on the start, so it refreshes with every reparse - otherwise it
        // would keep saying "30 min" until the duration was touched, defeating its whole point.
        UpdateDurationChip();

        if (start is { } s)
        {
            var end = s.AddMinutes(_durationMin);
            ParsePreview.Text = $"{DayLabel(s)} · {s:h:mm} – {end:h:mm tt}";
            // Resource REFERENCE, not a captured brush: this line outlives a theme switch.
            ParsePreview.SetResourceReference(ForegroundProperty, "Green");
            ParsePreview.Visibility = Visibility.Visible;
        }
        else if (whenText.Length > 0)
        {
            ParsePreview.Text = "Add a day and a time (e.g. “tomorrow” · “2pm”).";
            ParsePreview.SetResourceReference(ForegroundProperty, "TextMid");
            ParsePreview.Visibility = Visibility.Visible;
        }
        else ParsePreview.Visibility = Visibility.Collapsed;

        // One line, not two: once the preview exists the hint is noise. The hint is the empty state.
        ComposerHint.Visibility = ParsePreview.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        UpdateCreateEnabled();
    }

    static string DayLabel(DateTimeOffset d)
    {
        var day = d.Date; var today = DateTimeOffset.Now.Date;
        if (day == today) return "Today";
        if (day == today.AddDays(1)) return "Tomorrow";
        return d.ToString("ddd, MMM d", CultureInfo.InvariantCulture);
    }

    void UpdateCreateEnabled() =>
        ComposerCreate.IsEnabled = _parsed is { Start: not null } && !string.IsNullOrWhiteSpace(_parsed.Summary);

    /// <summary>
    /// The third field is the END TIME, not an abstract duration: the chip reads as the end time,
    /// and each picker row is "2:30 PM (30 min)" - time and length in one control.
    /// </summary>
    static readonly int[] DurationChoices = [15, 30, 45, 60, 90, 120];

    void OnPickDuration(object s, RoutedEventArgs e)
    {
        var start = ComposerStart();
        var items = new List<(string Label, Action? Pick)>();
        foreach (var choice in DurationChoices)
        {
            var minutes = choice;
            items.Add((EndOptionLabel(start, minutes), () =>
            {
                _durationMin = minutes;
                UpdateDurationChip();
                ReparseComposer();
            }));
        }
        NotePicker.Show(DurationChip, items, EndOptionLabel(start, _durationMin));
    }

    /// <summary>"2:30 PM (30 min)" when the start is known, else just the length.</summary>
    static string EndOptionLabel(DateTime? start, int minutes) =>
        start is { } s ? $"{s.AddMinutes(minutes):h:mm tt}  ({DurLabel(minutes)})" : DurLabel(minutes);

    // The chip shows where the meeting ENDS; falls back to the bare length until a start resolves.
    void UpdateDurationChip() =>
        DurationChip.Content = ComposerStart() is { } s
            ? $"{s.AddMinutes(_durationMin):h:mm tt}  ▾"
            : $"{DurLabel(_durationMin)}  ▾";

    static string DurLabel(int min) => min < 60 ? $"{min} min" : min == 60 ? "1 hour" : min % 60 == 0 ? $"{min / 60} hours" : $"1 h {min - 60} m";

    // A labelled switch, not a chip: a filled chip is ambiguous between "on" and "press to turn on".
    void OnToggleMeet(object s, RoutedEventArgs e)
    {
        _addMeet = MeetToggle.IsChecked == true;
        _lastAddMeet = _addMeet;
    }

    void UpdateMeetChip() => MeetToggle.IsChecked = _addMeet;

    void OnComposerCreate(object s, RoutedEventArgs e) => TryCreate();

    async void TryCreate()
    {
        if (_parsed?.Start is not { } start || string.IsNullOrWhiteSpace(_parsed.Summary)) return;
        var editing = _editing;
        ComposerCreate.IsEnabled = false;
        ComposerCreate.Content = editing is null ? "Creating…" : "Saving…";

        var emails = _parsed.Emails.Concat(SplitEmails(PeopleBox.Text)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ev = new NewEvent(_parsed.Summary, start, start.AddMinutes(_durationMin), emails, _addMeet);
        try
        {
            _cts ??= new();
            var result = editing is null
                ? await CalendarService.Instance.CreateAsync(ev, _cts.Token)
                : await CalendarService.Instance.UpdateAsync(editing.Id, ev, editing.HasMeetLink, editing.Provider, _cts.Token);
            UpcomingCache.Instance.Upsert(result);
            CloseComposer();
            RenderFromSnapshot();
            if (editing is null) Scroller.ScrollToTop();   // reveal the newly-created event
        }
        catch (GoogleAuthException ex) { CreateFailed(ex.Message); }
        catch { CreateFailed(editing is null ? "Couldn’t create - try again." : "Couldn’t save - try again."); }
        finally { ComposerCreate.Content = editing is null ? "Create" : "Save"; UpdateCreateEnabled(); }
    }

    void CreateFailed(string message)
    {
        // An error OUTRANKS the parse preview. The hint line is hidden whenever the preview is up,
        // so failing without re-showing it would report the problem into a collapsed TextBlock.
        ComposerHint.Text = message;
        ComposerHint.SetResourceReference(ForegroundProperty, "Danger");
        ComposerHint.Visibility = Visibility.Visible;
        ParsePreview.Visibility = Visibility.Collapsed;
    }

    static IEnumerable<string> SplitEmails(string? s) =>
        (s ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Where(x => x.Contains('@'));
}
