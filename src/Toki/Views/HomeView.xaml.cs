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
        // Adaptive greeting — the actual user, not a hardcoded name.
        var name = Toki.Llm.AppSettings.Current.DisplayName;
        Greeting.Text = string.IsNullOrEmpty(name) ? "Welcome back!" : $"Welcome back, {name}!";
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
        // Toki pops + blinks when Home is revealed (on hover) — alive, glad to see you.
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) return;
            var ease = new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.6 };
            var a = new System.Windows.Media.Animation.DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease };
            TokiPop.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, a);
            TokiPop.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, a);
            TokiChar.BlinkNow();
            if (!App.Headless) RefreshGoogle();   // refresh events each time Home is shown
        };
        _ready = true;   // construction finished — the notch may now be safely re-grown
    }

    void OnCalendarChanged() => Dispatcher.Invoke(RefreshGoogle);

    // ── Google state + COMING UP ──

    void RefreshGoogle()
    {
        var svc = CalendarService.Instance;
        if (svc.IsConnected)
        {
            ConnectedHost.Visibility = Visibility.Visible;
            ConnectHost.Visibility = Visibility.Collapsed;
            AccountEmail.Text = svc.AccountEmail is { Length: > 0 } m ? m : "Google connected";
            LoadComingUp();
        }
        else
        {
            ConnectedHost.Visibility = Visibility.Collapsed;
            ConnectHost.Visibility = Visibility.Visible;
            GoogleLabel.Text = "Connect Google";
            GoogleButton.IsEnabled = true;
        }
        Grow();
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

    // Not connected → run the browser sign-in. (The Meeting button also reaches the Calendar page,
    // which shows its own connect CTA.)
    async void OnGoogleConnect(object sender, RoutedEventArgs e)
    {
        var svc = CalendarService.Instance;
        if (!svc.IsConfigured) { FlashLabel("Google sign-in isn’t set up yet"); return; }

        GoogleButton.IsEnabled = false;
        GoogleLabel.Text = "Opening browser…";
        try
        {
            await svc.ConnectAsync();
            RefreshGoogle();          // flips to the connected card + COMING UP
            TokiChar.React();
        }
        catch (GoogleAuthException ex)
        {
            FlashLabel(ex.Message.Length > 40 ? "Couldn’t connect to Google" : ex.Message);
            RefreshGoogle();
        }
        catch
        {
            FlashLabel("Couldn’t connect to Google");
            RefreshGoogle();
        }
        finally { GoogleButton.IsEnabled = true; }
    }

    void FlashLabel(string message)
    {
        GoogleLabel.Text = message;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400) };
        t.Tick += (_, _) => { t.Stop(); GoogleLabel.Text = "Connect Google"; };
        t.Start();
    }
}
