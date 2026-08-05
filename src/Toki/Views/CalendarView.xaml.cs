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
/// The Calendar page: ONE continuous agenda scroll over the ~31-day horizon (week bands separate it —
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
    string? _lastRenderSig;            // render-key of the last agenda — skip redundant rebuilds
    bool _entranceDone;                // the entrance stagger runs ONCE, never on a refresh
    readonly List<(FrameworkElement El, string Label, int Count)> _bands = new();  // week separators, for scroll-synced subtitle

    readonly DispatcherTimer _parseDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer _nowTick = new() { Interval = TimeSpan.FromSeconds(60) };
    DispatcherTimer? _undoTimer;
    System.Threading.CancellationTokenSource? _cts;

    public CalendarView()
    {
        InitializeComponent();
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

        // Render-key: skip the whole rebuild when nothing changed. The page refreshes from several sources
        // (snapshot + fetch, cache events, the 60s tick); rebuilding + re-staggering identical rows is what
        // flickered. Today's date is in the key so a midnight rollover forces one rebuild.
        // Theme is in the key too: the rows are code-built and capture their brushes, so a light↔dark swap
        // MUST rebuild them — otherwise the dedup keeps the old theme's text colour (black text on dark).
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
            var (band, label) = WeekBand(byWeek.Key, today);
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
    (FrameworkElement, string) WeekBand(DateTime weekStart, DateTime today)
    {
        var thisWeek = WeekStartOf(today);
        var label = weekStart == thisWeek ? "This week"
                  : weekStart == thisWeek.AddDays(7) ? "Next week"
                  : $"{weekStart:MMM d} – {weekStart.AddDays(6):MMM d}";
        var tb = new TextBlock
        {
            Text = label.ToUpperInvariant(), FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 9.5,
            FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextDim"), Margin = new Thickness(2, 16, 0, 2),
        };
        return (tb, label);
    }

    Border DayHeader(DateTime day, bool today)
    {
        var label = day.ToString("ddd, MMM d", CultureInfo.InvariantCulture).ToUpperInvariant();
        var tb = new TextBlock
        {
            Text = today ? $"TODAY · {label}" : label,
            FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource(today ? "Amber" : "TextDim"),
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
            Text = now.ToString("h:mm", CultureInfo.InvariantCulture), FontSize = 9, Foreground = (Brush)FindResource("Coral"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)FindResource("UiFont"),
        };
        Grid.SetColumn(t, 0);
        // A coral dot at the left end + the line (the Amie "now" marker).
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
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = begin });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(180)) { BeginTime = begin, EasingFunction = ease });
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
            await CalendarService.Instance.DeleteAsync(e.Id, _cts.Token);
            if (allowUndo) OfferUndo(e);
        }
        catch (GoogleAuthException ex)
        {
            UpcomingCache.Instance.Upsert(e);   // put it back — the delete didn't take
            RenderFromSnapshot();
            ShowActionBar($"Couldn’t delete — {ex.Message}", ("OK", false, HideActionBar), null);
        }
        catch (Exception ex)
        {
            // Belt and braces: this is an async void with an OPTIMISTIC removal above — any
            // unexpected throw (cancel, a future client change) must still roll the event
            // back, or it silently vanishes from the UI while still existing at Google.
            App.Log($"calendar delete failed: {ex.Message}");
            UpcomingCache.Instance.Upsert(e);
            RenderFromSnapshot();
            ShowActionBar("Couldn’t delete — try again.", ("OK", false, HideActionBar), null);
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
                new NewEvent(e.Summary, e.StartLocal, e.EndLocal, emails, e.HasMeetLink), _cts.Token);
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
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
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
        var blob = new Controls.TokiBlob();
        var art = new Viewbox { Width = 48, Height = 48, Child = blob, HorizontalAlignment = HorizontalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand };
        art.MouseEnter += (_, _) => blob.React();   // a little delight on hover
        SetState(art, "Nothing coming up — all clear.", ("＋ Plan something", () => OnNew(this, new RoutedEventArgs())));
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
        var configured = CalendarService.Instance.IsConfigured;
        SetState(TokiArt(), configured ? "See your week here." : "Google sign-in isn’t set up in this build yet.",
            configured
                ? ("Connect Google", async () => { try { await CalendarService.Instance.ConnectAsync(); } catch { } Refresh(force: true); })
                : ((string, Action)?)null);
    }

    void SetState(UIElement? art, string message, (string label, Action action)? cta)
    {
        StatePanel.Children.Clear();
        if (art is not null) StatePanel.Children.Add(art);
        StatePanel.Children.Add(new TextBlock
        {
            Text = message, Style = (Style)FindResource("Body"), FontSize = 12, TextAlignment = TextAlignment.Center,
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

    static UIElement TokiArt() => new Viewbox
    {
        Width = 42, Height = 42, Child = new Controls.TokiBlob(), HorizontalAlignment = HorizontalAlignment.Center,
    };

    // ── navigation: one continuous scroll (no week paging) ──

    void OnBack(object s, RoutedEventArgs e) => App.State.TryGo(PillState.Home);

    void OnToday(object s, RoutedEventArgs e) => Scroller.ScrollToTop();

    // The header subtitle tracks whatever week is at the top of the viewport; the Today pill appears only
    // once you've scrolled away from the top (Amie style).
    void OnScroll(object s, ScrollChangedEventArgs e)
    {
        UpdateSubtitleFromScroll();
        var away = Scroller.VerticalOffset > 24;
        if (away && TodayButton.Visibility != Visibility.Visible)
        {
            TodayButton.Visibility = Visibility.Visible;
            TodayButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }
        else if (!away && TodayButton.Visibility == Visibility.Visible)
        {
            var a = new DoubleAnimation(TodayButton.Opacity, 0, TimeSpan.FromMilliseconds(120));
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
            catch { break; }   // not laid out yet — keep the first band
        }
        Subtitle.Text = $"{current.Label} · {current.Count} event{(current.Count == 1 ? "" : "s")}";
    }

    // ── create composer ──

    void OnNew(object s, RoutedEventArgs e)
    {
        if (Composer.Visibility == Visibility.Visible && _editing is null) { CloseComposer(); return; }
        _editing = null;
        _durationMin = 30; DurationChip.Content = "30 min";
        _addMeet = _lastAddMeet; UpdateMeetChip();
        Composer.Visibility = Visibility.Visible;
        PeopleRow.Visibility = Visibility.Collapsed; PeopleBox.Text = "";
        TitleBox.Text = ""; DateBox.Text = ""; TimeBox.Text = "";
        ComposerHint.Text = "e.g. Design sync · tomorrow · 2pm";
        ComposerHint.Foreground = (Brush)FindResource("TextDim");
        ParsePreview.Visibility = Visibility.Collapsed;
        ComposerCreate.Content = "Create";
        _parsed = null; UpdateCreateEnabled();
        TitleBox.Focus();
    }

    void BeginEdit(CalEvent ev)
    {
        _editing = ev;
        _durationMin = Math.Max(15, (int)Math.Round((ev.EndUtc - ev.StartUtc).TotalMinutes));
        DurationChip.Content = DurLabel(_durationMin);
        _addMeet = ev.HasMeetLink; UpdateMeetChip();
        var emails = ev.Attendees.Where(a => !a.IsSelf && a.Email is not null).Select(a => a.Email!).ToList();
        PeopleBox.Text = string.Join(", ", emails);
        PeopleRow.Visibility = emails.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Composer.Visibility = Visibility.Visible;
        ComposerHint.Text = "Edit the title, or type a new day/time to move it.";
        ComposerHint.Foreground = (Brush)FindResource("TextDim");
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
        Composer.Visibility = Visibility.Visible;
        PeopleRow.Visibility = Visibility.Visible;
        PeopleBox.Text = "ana@work.com";
        TitleBox.Text = "Design sync"; DateBox.Text = "tomorrow"; TimeBox.Text = "2pm";
        ReparseComposer();
    }

    void CloseComposer()
    {
        Composer.Visibility = Visibility.Collapsed;
        PeopleRow.Visibility = Visibility.Collapsed;
        TitleBox.Text = ""; DateBox.Text = ""; TimeBox.Text = ""; PeopleBox.Text = "";
        _editing = null;
    }

    void OnComposerCancel(object s, RoutedEventArgs e) => CloseComposer();

    void OnComposerField(object s, TextChangedEventArgs e) { _parseDebounce.Stop(); _parseDebounce.Start(); }

    void OnComposerKey(object s, System.Windows.Input.KeyEventArgs e)
    {
        // Keep Esc/Enter inside the composer — never let them bubble to the notch (Esc would hide it).
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

        if (start is { } s)
        {
            var end = s.AddMinutes(_durationMin);
            ParsePreview.Text = $"{DayLabel(s)} · {s:h:mm} – {end:h:mm tt}";
            ParsePreview.Foreground = (Brush)FindResource("Green");
            ParsePreview.Visibility = Visibility.Visible;
        }
        else if (whenText.Length > 0)
        {
            ParsePreview.Text = "Add a day and a time (e.g. “tomorrow” · “2pm”).";
            ParsePreview.Foreground = (Brush)FindResource("TextDim");
            ParsePreview.Visibility = Visibility.Visible;
        }
        else ParsePreview.Visibility = Visibility.Collapsed;
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

    void OnCycleDuration(object s, RoutedEventArgs e)
    {
        _durationMin = _durationMin switch { < 60 => 60, 60 => 90, _ => 30 };
        DurationChip.Content = DurLabel(_durationMin);
        ReparseComposer();
    }

    static string DurLabel(int min) => min < 60 ? $"{min} min" : min == 60 ? "1 h" : min % 60 == 0 ? $"{min / 60} h" : $"1 h {min - 60} m";

    void OnToggleMeet(object s, RoutedEventArgs e) { _addMeet = !_addMeet; _lastAddMeet = _addMeet; UpdateMeetChip(); }

    void UpdateMeetChip() => SetChipActive(MeetChip, _addMeet);

    // A chip fills with a soft green when active, outlines when not — the fill IS the on/off (no "✓" text).
    void SetChipActive(Button chip, bool on)
    {
        chip.Background = on ? (Brush)FindResource("GreenDark") : Brushes.Transparent;
        chip.Foreground = on ? (Brush)FindResource("Green") : (Brush)FindResource("TextDim");
        chip.BorderBrush = on ? Brushes.Transparent : (Brush)FindResource("Hairline");
    }

    void OnTogglePeople(object s, RoutedEventArgs e)
    {
        var open = PeopleRow.Visibility != Visibility.Visible;
        PeopleRow.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SetChipActive(PeopleChip, open);
        if (open) PeopleBox.Focus();
    }

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
                : await CalendarService.Instance.UpdateAsync(editing.Id, ev, editing.HasMeetLink, _cts.Token);
            UpcomingCache.Instance.Upsert(result);
            CloseComposer();
            RenderFromSnapshot();
            if (editing is null) Scroller.ScrollToTop();   // reveal the newly-created event
        }
        catch (GoogleAuthException ex) { CreateFailed(ex.Message); }
        catch { CreateFailed(editing is null ? "Couldn’t create — try again." : "Couldn’t save — try again."); }
        finally { ComposerCreate.Content = editing is null ? "Create" : "Save"; UpdateCreateEnabled(); }
    }

    void CreateFailed(string message)
    {
        ComposerHint.Text = message;
        ComposerHint.Foreground = (Brush)FindResource("Coral");
    }

    static IEnumerable<string> SplitEmails(string? s) =>
        (s ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Where(x => x.Contains('@'));
}
