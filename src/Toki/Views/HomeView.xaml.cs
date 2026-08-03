using System.Windows;
using System.Windows.Controls;
using Toki.Calendar;
using Toki.State;

namespace Toki.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        // Adaptive greeting — the actual user, not a hardcoded name.
        var name = Toki.Llm.AppSettings.Current.DisplayName;
        Greeting.Text = string.IsNullOrEmpty(name) ? "Welcome back!" : $"Welcome back, {name}!";
        RefreshGoogle();
        // Keep the button in step if the connection changes elsewhere (e.g. Settings disconnect,
        // or a dead grant getting cleared on a failed refresh).
        CalendarService.Instance.ConnectionChanged += OnConnectionChanged;
        Unloaded += (_, _) => CalendarService.Instance.ConnectionChanged -= OnConnectionChanged;
        // Toki pops + blinks when Home is revealed (on hover) — alive, glad to see you.
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) return;
            var ease = new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.6 };
            var a = new System.Windows.Media.Animation.DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease };
            TokiPop.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, a);
            TokiPop.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, a);
            TokiChar.BlinkNow();
        };
    }

    void OnConnectionChanged() => Dispatcher.Invoke(RefreshGoogle);

    // Reflect the current Google state on the button: connected → the account email; otherwise the
    // call to action. (The offscreen render harness must not touch this — it reads settings only.)
    void RefreshGoogle()
    {
        var svc = CalendarService.Instance;
        if (svc.IsConnected)
        {
            GoogleLabel.Text = svc.AccountEmail is { Length: > 0 } m ? m : "Google connected";
            GoogleButton.ToolTip = "Google Calendar connected — manage it in Settings";
        }
        else
        {
            GoogleLabel.Text = "Connect Google";
            GoogleButton.ToolTip = "Connect your Google Calendar (sign in in your browser)";
        }
    }

    // Not connected → run the browser sign-in. Connected → hop to Settings (where Disconnect lives).
    async void OnGoogleConnect(object sender, RoutedEventArgs e)
    {
        var svc = CalendarService.Instance;
        if (svc.IsConnected) { App.State.TryGo(PillState.Settings); return; }

        if (!svc.IsConfigured)
        {
            FlashLabel("Google sign-in isn’t set up yet");
            return;
        }

        GoogleButton.IsEnabled = false;
        GoogleLabel.Text = "Opening browser…";
        try
        {
            await svc.ConnectAsync();
            RefreshGoogle();                       // now shows the account email
            TokiChar.React();                      // a little celebratory wobble
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

    // Briefly show a message on the button, then restore its real state.
    void FlashLabel(string message)
    {
        GoogleLabel.Text = message;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400) };
        t.Tick += (_, _) => { t.Stop(); RefreshGoogle(); };
        t.Start();
    }

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
}
