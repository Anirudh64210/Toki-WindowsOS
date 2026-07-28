using System.Windows;
using System.Windows.Controls;
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

    void OnSettings(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Settings);

    void OnTakeNotes(object sender, RoutedEventArgs e) => App.StartQuickNote();

    void OnLibrary(object sender, RoutedEventArgs e)
    {
        // Clean, fast handoff: dismiss the pill first, then show the (warm) Library window
        // so it appears instantly instead of visibly building while the pill lingers.
        App.State.TryGo(PillState.Hidden);
        Toki.LibraryWindow.ShowLibrary();
    }
}
