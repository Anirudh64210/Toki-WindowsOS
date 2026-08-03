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
/// The Calendar page: an agenda-by-day week view over <see cref="CalendarService"/> with Join, a
/// low-friction natural-language create composer, and delete (undo for solo events, confirm for
/// guested ones). Fixed height + internal scroll. Talks only to the provider-neutral facade + models.
/// </summary>
public partial class CalendarView : UserControl
{
    static bool _lastAddMeet = true;   // sticky across opens within a session

    int _weekOffset;                   // 0 = this week, 1 = next, … (backend is upcoming-only, so never < 0)
    int _durationMin = 30;
    bool _addMeet;
    QuickEvent? _parsed;
    CalEvent? _pendingUndo;
    CalEvent? _editing;                // non-null while the composer is editing an existing event

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

        if (App.Headless) { RenderWeek(Dev.CalendarSamples.Events()); return; }

        // Subscribe on Loaded / release on Unloaded (the view is cached and reshown). Idempotent so a
        // repeat Loaded can't double-subscribe.
        Loaded += (_, _) =>
        {
            UpcomingCache.Instance.Changed -= OnCacheChanged; UpcomingCache.Instance.Changed += OnCacheChanged;
            CalendarService.Instance.ConnectionChanged -= OnCacheChanged; CalendarService.Instance.ConnectionChanged += OnCacheChanged;
        };
        Unloaded += (_, _) =>
        {
            UpcomingCache.Instance.Changed -= OnCacheChanged;
            CalendarService.Instance.ConnectionChanged -= OnCacheChanged;
            _nowTick.Stop(); _cts?.Cancel(); _cts = null;
        };
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { Refresh(); _nowTick.Start(); }
            else { _nowTick.Stop(); CloseComposer(); HideActionBar(); }
        };
    }

    void OnCacheChanged() => Dispatcher.Invoke(() => { if (IsVisible) RenderFromSnapshot(); });

    // ── loading / refresh ──

    async void Refresh(bool force = false)
    {
        if (!CalendarService.Instance.IsConnected) { ShowDisconnected(); return; }

        var snap = UpcomingCache.Instance.Snapshot;
        if (snap is not null) RenderWeek(snap); else ShowLoading();

        try
        {
            var fresh = await UpcomingCache.Instance.GetAsync(force);
            if (!ReferenceEquals(fresh, snap)) RenderWeek(fresh);   // avoid a double render when the cache was fresh
        }
        catch (GoogleAuthException ex) { ShowError(ex); }
        catch { ShowError(null); }
    }

    void RenderFromSnapshot()
    {
        if (!CalendarService.Instance.IsConnected) { ShowDisconnected(); return; }
        var snap = UpcomingCache.Instance.Snapshot;
        if (snap is not null) RenderWeek(snap);
    }

    // ── the week ──

    DateTimeOffset WeekStart => new DateTimeOffset(DateTimeOffset.Now.Date, DateTimeOffset.Now.Offset).AddDays(_weekOffset * 7);

    void RenderWeek(IReadOnlyList<CalEvent> all)
    {
        var start = WeekStart;
        var end = start.AddDays(7);
        var now = DateTimeOffset.Now;

        var inWeek = all.Where(e => e.StartLocal >= start && e.StartLocal < end).ToList();

        WeekRange.Text = FormatRange(start, end.AddDays(-1));
        Subtitle.Text = (_weekOffset == 0 ? "This week" : _weekOffset == 1 ? "Next week" : FormatRange(start, end.AddDays(-1)))
            + $" · {inWeek.Count} event{(inWeek.Count == 1 ? "" : "s")}";
        TodayButton.Visibility = _weekOffset == 0 ? Visibility.Collapsed : Visibility.Visible;
        PrevWeek.IsEnabled = _weekOffset > 0;
        NextWeek.IsEnabled = _weekOffset < 3;

        if (inWeek.Count == 0) { ShowEmpty(); return; }

        HideState();
        Days.Children.Clear();
        var today = now.Date;
        var delay = 0;
        foreach (var byDay in inWeek.GroupBy(e => e.StartLocal.Date).OrderBy(g => g.Key))
        {
            Days.Children.Add(DayHeader(byDay.Key, byDay.Key == today));
            var dayEvents = byDay.OrderBy(e => e.IsAllDay ? 0 : 1).ThenBy(e => e.StartLocal).ToList();
            var nowInserted = false;
            foreach (var e in dayEvents)
            {
                if (byDay.Key == today && !nowInserted && !e.IsAllDay && e.StartLocal > now)
                {
                    Days.Children.Add(NowLine(now));
                    nowInserted = true;
                }
                var row = CalendarRows.AgendaRow(e, ShowEventMenu);
                Stagger(row, delay); delay += 24;
                Days.Children.Add(row);
            }
        }
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
        var line = new Border
        {
            Height = 1.5, Background = (Brush)FindResource("Coral"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0), CornerRadius = new CornerRadius(1),
        };
        Grid.SetColumn(line, 1);
        grid.Children.Add(t); grid.Children.Add(line);
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

    static string FormatRange(DateTimeOffset a, DateTimeOffset b) =>
        a.Month == b.Month ? $"{a:MMM} {a.Day} – {b.Day}" : $"{a:MMM} {a.Day} – {b:MMM} {b.Day}";

    // ── overflow menu + delete ──

    void ShowEventMenu(CalEvent e, FrameworkElement anchor)
    {
        var items = new List<(string, bool, Action)>();
        if (e.HasMeetLink) items.Add(("Join", false, () => { CalendarRows.OpenUrl(e.MeetLink); App.State.ForceForDemo(PillState.Hidden); }));
        if (CanEdit(e)) items.Add(("Edit", false, () => BeginEdit(e)));
        items.Add(("Open in Google Calendar", false, () => CalendarRows.OpenUrl(e.HtmlLink)));
        items.Add(("Delete", true, () => BeginDelete(e)));
        NotchMenu.Show(anchor, items);
    }

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
        SetState(TokiArt(), "Nothing scheduled this week.", null);
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

    // ── week navigation ──

    void OnPrevWeek(object s, RoutedEventArgs e) { if (_weekOffset > 0) { _weekOffset--; RenderFromSnapshot(); } }
    void OnNextWeek(object s, RoutedEventArgs e) { if (_weekOffset < 3) { _weekOffset++; RenderFromSnapshot(); } }
    void OnToday(object s, RoutedEventArgs e) { _weekOffset = 0; RenderFromSnapshot(); }
    void OnBack(object s, RoutedEventArgs e) => App.State.TryGo(PillState.Home);

    // ── create composer ──

    void OnNew(object s, RoutedEventArgs e)
    {
        if (Composer.Visibility == Visibility.Visible && _editing is null) { CloseComposer(); return; }
        _editing = null;
        _durationMin = 30; DurationChip.Content = "30 min";
        _addMeet = _lastAddMeet; UpdateMeetChip();
        Composer.Visibility = Visibility.Visible;
        PeopleRow.Visibility = Visibility.Collapsed; PeopleBox.Text = "";
        QuickBox.Text = "";
        ComposerHint.Text = "Try “Design sync tomorrow 2pm with ana@work.com”";
        ComposerHint.Foreground = (Brush)FindResource("TextDim");
        ParsePreview.Visibility = Visibility.Collapsed;
        ComposerCreate.Content = "Create";
        _parsed = null; UpdateCreateEnabled();
        QuickBox.Focus();
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
        QuickBox.Text = ev.Summary;   // triggers ReparseComposer (keeps the event's time via the edit fallback)
        QuickBox.Focus();
        QuickBox.CaretIndex = QuickBox.Text.Length;
    }

    /// <summary>Render-harness hook: open the composer with sample text so its state is verifiable in a PNG.</summary>
    internal void DebugOpenComposer(string text)
    {
        Composer.Visibility = Visibility.Visible;
        PeopleRow.Visibility = Visibility.Visible;
        PeopleBox.Text = "ana@work.com";
        QuickBox.Text = text;
        ReparseComposer();
    }

    void CloseComposer()
    {
        Composer.Visibility = Visibility.Collapsed;
        PeopleRow.Visibility = Visibility.Collapsed;
        QuickBox.Text = ""; PeopleBox.Text = "";
        _editing = null;
    }

    void OnComposerCancel(object s, RoutedEventArgs e) => CloseComposer();

    void OnQuickText(object s, TextChangedEventArgs e) { _parseDebounce.Stop(); _parseDebounce.Start(); }

    void OnQuickKey(object s, System.Windows.Input.KeyEventArgs e)
    {
        // Keep Esc/Enter inside the composer — never let them bubble to the notch (Esc would hide it).
        if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; CloseComposer(); }
        else if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; TryCreate(); }
    }

    void ReparseComposer()
    {
        var text = QuickBox.Text?.Trim() ?? "";
        var parsed = text.Length == 0 ? null : QuickEventParser.Parse(text, DateTimeOffset.Now);
        // Editing with no new time typed → keep the event's original time; the text is just the title.
        if (_editing is { } ed && parsed?.Start is null)
            _parsed = new QuickEvent(text.Length > 0 ? text : ed.Summary, ed.StartLocal, ed.EndLocal, parsed?.Emails ?? Array.Empty<string>());
        else
            _parsed = parsed;

        if (_parsed?.Start is { } start)
        {
            var end = start.AddMinutes(_durationMin);
            ParsePreview.Text = $"{DayLabel(start)} · {start:h:mm} – {end:h:mm tt}";
            ParsePreview.Foreground = (Brush)FindResource("Green");
            ParsePreview.Visibility = Visibility.Visible;
        }
        else if (text.Length > 0)
        {
            ParsePreview.Text = "When? Add a day and time (e.g. “tomorrow 2pm”).";
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

    void UpdateMeetChip()
    {
        MeetChip.Content = _addMeet ? "Google Meet ✓" : "Google Meet";
        MeetChip.Background = (Brush)FindResource(_addMeet ? "WhiteBtn" : "Chip");
        MeetChip.Foreground = (Brush)FindResource(_addMeet ? "CtaText" : "Text");
    }

    void OnTogglePeople(object s, RoutedEventArgs e)
    {
        PeopleRow.Visibility = PeopleRow.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (PeopleRow.Visibility == Visibility.Visible) PeopleBox.Focus();
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
            if (editing is null) _weekOffset = 0;   // jump to this week to reveal a new event; an edit stays put
            CloseComposer();
            RenderFromSnapshot();
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
