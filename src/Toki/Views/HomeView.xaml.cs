using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toki.Calendar;
using Toki.State;

namespace Toki.Views;

public partial class HomeView : UserControl
{
    // Re-entrancy guard: growing the notch measures ViewFor(Home); run during this constructor
    // (before PillWindow caches the instance) it builds another HomeView and recurses forever.
    bool _ready;

    void Grow() { if (_ready && !App.Headless) App.GrowNotch(); }

    public HomeView()
    {
        InitializeComponent();

        // Action capsules come from the one shared definition (PillParts) so Home and the idle
        // pills can never drift apart. Width is left to the grid.
        var start = PillParts.ActionPill(PillParts.CalendarGlyph(), "Calendar");
        start.ToolTip = "Your week - and create a meeting";
        start.Click += OnMeeting;
        StartMeetingHost.Content = start;

        var notes = PillParts.ActionPill(PillParts.Notepad(), "Take notes");
        notes.ToolTip = "Start recording this meeting and take notes";
        notes.Click += OnTakeNotes;
        TakeNotesHost.Content = notes;

        RefreshGreeting();
        RefreshGoogle();
        // Subscribe on LOADED, not the ctor: this view is a cached singleton, and a ctor-subscribe
        // + Unloaded-unsubscribe pair goes permanently deaf after the first navigation away.
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
        // Toki pops + blinks when Home is revealed.
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

    // The greeting is a cached view's text; re-read it whenever Home is shown, or a name change
    // in Settings never appears here.
    void RefreshGreeting()
    {
        var name = Toki.Llm.AppSettings.Current.DisplayName;
        Greeting.Text = string.IsNullOrEmpty(name) ? "Welcome back!" : $"Welcome back, {name}!";
    }

    void OnCalendarChanged() => Dispatcher.Invoke(RefreshGoogle);

    /// <summary>Dev-only: force the CONNECTED layout without a real connection, for --measuretest.
    /// <paramref name="withEvents"/> paints sample upcoming rows; otherwise the empty state.</summary>
    // The pin is required: without it, showing the view fires IsVisibleChanged -> RefreshGoogle,
    // which reads the real (disconnected) state and flips the card back to the connect buttons.
    bool _debugPinned;

    public void DebugConnected(bool withEvents)
    {
        _debugPinned = true;
        ConnectedHost.Visibility = Visibility.Visible;
        ConnectHost.Visibility = Visibility.Collapsed;
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
            LoadComingUp();
        }
        else
        {
            ConnectedHost.Visibility = Visibility.Collapsed;
            ConnectHost.Visibility = Visibility.Visible;
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
        // Capture the pill's spot BEFORE hiding it; the Library blooms from that rect.
        var from = App.PillRect();
        App.State.TryGo(PillState.Hidden);
        Toki.LibraryWindow.ShowLibrary(fromRect: from);
    }

    // Lands directly on the Library Settings rows that hold both providers' sign-in state.
    void OnConnectCalendar(object sender, RoutedEventArgs e)
    {
        var from = App.PillRect();
        App.State.TryGo(PillState.Hidden);
        Toki.LibraryWindow.ShowLibrarySettings(fromRect: from);
    }
}
