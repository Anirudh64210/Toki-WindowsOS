using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toki.Calendar;
using Toki.State;

namespace Toki.Views;

public partial class HomeView : UserControl
{
    // Guards against a fatal re-entrancy: growing the notch measures ViewFor(Home), and if that runs
    // DURING this constructor (before PillWindow has cached the instance) it builds another HomeView,
    // recursing forever. So the notch is only ever re-grown AFTER construction finishes.
    bool _ready;

    void Grow() { if (_ready && !App.Headless) App.GrowNotch(); }

    public HomeView()
    {
        InitializeComponent();
        RefreshGreeting();   // adaptive greeting - the actual user, not a hardcoded name
        RefreshGoogle();
        // Keep the card in step if the connection or the events change elsewhere.
        // Subscribe on LOADED (not the ctor): this view is a cached singleton, and a
        // ctor-subscribe + Unloaded-unsubscribe pair goes permanently deaf after the first
        // navigation away (the CalendarView already does it this way).
        Loaded += (_, _) =>
        {
            CalendarService.Instance.ConnectionChanged += OnCalendarChanged;
            UpcomingCache.Instance.Changed += OnCalendarChanged;
        };
        Unloaded += (_, _) =>
        {
            CalendarService.Instance.ConnectionChanged -= OnCalendarChanged;
            UpcomingCache.Instance.Changed -= OnCalendarChanged;
        };
        // Toki pops + blinks when Home is revealed (on hover) - alive, glad to see you.
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) return;
            var ease = new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.6 };
            var a = new System.Windows.Media.Animation.DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease };
            TokiPop.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, a);
            TokiPop.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, a);
            TokiChar.BlinkNow();
            RefreshGreeting();                    // pick up a name changed in the Library settings
            if (!App.Headless) RefreshGoogle();   // refresh events each time Home is shown
        };
        _ready = true;   // construction finished - the notch may now be safely re-grown
    }

    // The greeting is a CACHED view's text; it must be re-read whenever Home is shown, or a name change
    // (Library → Settings) never appears here (the bug the owner hit). Cheap; no layout churn if unchanged.
    void RefreshGreeting()
    {
        var name = Toki.Llm.AppSettings.Current.DisplayName;
        Greeting.Text = string.IsNullOrEmpty(name) ? "Welcome back!" : $"Welcome back, {name}!";
    }

    void OnCalendarChanged() => Dispatcher.Invoke(RefreshGoogle);

    /// <summary>Dev-only: force the CONNECTED layout (account footer + COMING UP) without a real Google
    /// connection, so the measurement diagnostic (--measuretest) can reproduce the account-footer clip
    /// headlessly. <paramref name="withEvents"/> paints sample upcoming rows; otherwise the empty state.</summary>
    // DebugConnected PINS the layout. Without the pin, showing the view in a window fires
    // IsVisibleChanged -> RefreshGoogle, which reads the REAL (disconnected) state and flips the card
    // back to the connect buttons - so the measure harness's two passes were silently measuring two
    // different layouts, and the "clip" it reported was really the height difference between them.
    bool _debugPinned;

    public void DebugConnected(bool withEvents)
    {
        _debugPinned = true;
        ConnectedHost.Visibility = Visibility.Visible;
        ConnectHost.Visibility = Visibility.Collapsed;
        BuildAccountsRow(debugEmail: "saianirudh1111@gmail.com");
        PaintComingUp(withEvents ? Dev.CalendarSamples.Events() : System.Array.Empty<CalEvent>());
    }

    // ── calendar state + COMING UP ──

    void RefreshGoogle()
    {
        if (_debugPinned) return;
        var svc = CalendarService.Instance;
        if (svc.IsConnected)
        {
            ConnectedHost.Visibility = Visibility.Visible;
            ConnectHost.Visibility = Visibility.Collapsed;
            BuildAccountsRow();
            LoadComingUp();
        }
        else
        {
            ConnectedHost.Visibility = Visibility.Collapsed;
            ConnectHost.Visibility = Visibility.Visible;
            ConnectCalLabel.Text = "Connect a calendar";
            ConnectCalBtn.IsEnabled = true;
            BuildAccountsRow();   // the footer exists in both states now - keep its marks honest
        }
        Grow();
    }

    /// <summary>
    /// The account footer, rebuilt from the live connection state.
    ///
    /// The owner's design, asked for twice before it was built: a connect button is a CALL TO ACTION,
    /// and once the account is connected the call is answered - so it shrinks to a small provider mark
    /// (click = manage in Settings). The provider that is NOT yet connected stays reachable as a
    /// compact "+ Outlook" pill right here on Home, because adding a second calendar should not
    /// require knowing which page of Settings it lives on.
    /// </summary>
    void BuildAccountsRow(string? debugEmail = null)
    {
        AccountsRow.Children.Clear();
        var svc = CalendarService.Instance;

        var email = debugEmail
            ?? svc.AccountEmailFor(CalendarProvider.Google)
            ?? svc.AccountEmailFor(CalendarProvider.Outlook);

        var googleOn = debugEmail is not null || svc.GoogleConnected;
        var outlookOn = debugEmail is null && svc.OutlookConnected;

        if (googleOn) AccountsRow.Children.Add(ProviderMark("G", "#4285F4", "Google connected - manage in Settings"));
        if (outlookOn) AccountsRow.Children.Add(ProviderMark("O", "#0F6CBD", "Outlook connected - manage in Settings"));

        if (email is { Length: > 0 })
        {
            var t = new TextBlock
            {
                Text = email, Style = (Style)FindResource("Dim"), FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150,
            };
            AccountsRow.Children.Add(t);
        }

        // The missing provider, one small pill away - only when its sign-in is actually set up, so
        // Home never offers a button whose only outcome is an apology.
        if (!googleOn && svc.IsConfiguredFor(CalendarProvider.Google))
            AccountsRow.Children.Add(AddPill("+ Google", () => ConnectFromFooter(CalendarProvider.Google)));
        if (!outlookOn && debugEmail is null && svc.IsConfiguredFor(CalendarProvider.Outlook))
            AccountsRow.Children.Add(AddPill("+ Outlook", () => ConnectFromFooter(CalendarProvider.Outlook)));
    }

    /// <summary>A connected account, as a small round mark - the shrunken form of its connect button.</summary>
    Button ProviderMark(string letter, string hex, string tip)
    {
        var text = new TextBlock
        {
            Text = letter, FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var surface = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Child = text };
        surface.SetResourceReference(Border.BackgroundProperty, "Chip");
        var b = new Button
        {
            Content = surface, ToolTip = tip, Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0), Focusable = false, Template = BareTemplate(),
        };
        b.Click += (_, _) => App.State.TryGo(PillState.Settings);
        return b;
    }

    Button AddPill(string label, Action go)
    {
        var b = new Button
        {
            Content = label, Style = (Style)FindResource("PillButton"),
            FontSize = 10, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(6, 0, 0, 0),
        };
        b.SetResourceReference(ForegroundProperty, "TextDim");
        b.Click += (_, _) => go();
        return b;
    }

    static ControlTemplate BareTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        t.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        return t;
    }

    async void ConnectFromFooter(CalendarProvider provider)
    {
        try { await CalendarService.Instance.ConnectAsync(provider); }
        catch (GoogleAuthException ex) { App.Log($"footer connect {provider}: {ex.Message}"); }
        catch { }
        RefreshGoogle();
    }

    async void LoadComingUp()
    {
        if (App.Headless) { PaintComingUp(Dev.CalendarSamples.Events()); return; }
        var snap = UpcomingCache.Instance.Snapshot;
        if (snap is not null) PaintComingUp(snap);
        try { PaintComingUp(await UpcomingCache.Instance.GetAsync()); }
        catch { /* keep whatever the snapshot painted; the Calendar page surfaces errors */ }
    }

    void PaintComingUp(IReadOnlyList<CalEvent> all)
    {
        var now = DateTimeOffset.UtcNow;
        var upcoming = all.Where(e => e.EndUtc > now).OrderBy(e => e.StartUtc).Take(3).ToList();
        ComingUp.Children.Clear();
        if (upcoming.Count == 0)
        {
            ComingUp.Children.Add(new TextBlock
            {
                Text = "Nothing coming up.", Style = (Style)FindResource("Dim"), FontSize = 11,
                Margin = new Thickness(2, 2, 0, 2),
            });
        }
        else
        {
            foreach (var e in upcoming)
                ComingUp.Children.Add(CalendarRows.GlanceRow(e, () => App.State.TryGo(PillState.Calendar)));
        }
        Grow();
    }

    // ── buttons ──

    void OnMeeting(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Calendar);

    void OnAccount(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Settings);

    void OnSettings(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Settings);

    void OnTakeNotes(object sender, RoutedEventArgs e) => App.StartQuickNote();

    void OnLibrary(object sender, RoutedEventArgs e)
    {
        // Grow the Library out of the notch: capture the pill's spot BEFORE hiding it, then hand off
        // to the warm window which blooms from that rect.
        var from = App.PillRect();
        App.State.TryGo(PillState.Hidden);
        Toki.LibraryWindow.ShowLibrary(fromRect: from);
    }

    // The one setup row: clicking offers the two providers, and the row disappears once either is
    // connected. Setup is an errand, not furniture - it does not deserve permanent hero-sized buttons.
    void OnConnectCalendar(object sender, RoutedEventArgs e) =>
        NotePicker.Show(ConnectCalBtn, new List<(string, Action?)>
        {
            ("Google Calendar", () => Connect(CalendarProvider.Google)),
            ("Outlook", () => Connect(CalendarProvider.Outlook)),
        });

    async void Connect(CalendarProvider provider)
    {
        var svc = CalendarService.Instance;
        var name = provider == CalendarProvider.Outlook ? "Outlook" : "Google";
        if (!svc.IsConfiguredFor(provider))
        {
            FlashLabel($"{name} needs a one-time setup - see Library › Settings");
            return;
        }

        ConnectCalBtn.IsEnabled = false;
        ConnectCalLabel.Text = "Opening browser…";
        try
        {
            await svc.ConnectAsync(provider);
            RefreshGoogle();          // flips to the connected card + COMING UP
            TokiChar.React();
        }
        catch (GoogleAuthException ex)
        {
            FlashLabel(ex.Message.Length > 44 ? $"Couldn’t connect {name}" : ex.Message);
            RefreshGoogle();
        }
        catch
        {
            FlashLabel($"Couldn’t connect {name}");
            RefreshGoogle();
        }
        finally { ConnectCalBtn.IsEnabled = true; }
    }

    void FlashLabel(string message)
    {
        ConnectCalLabel.Text = message;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        t.Tick += (_, _) => { t.Stop(); ConnectCalLabel.Text = "Connect a calendar"; };
        t.Start();
    }
}
