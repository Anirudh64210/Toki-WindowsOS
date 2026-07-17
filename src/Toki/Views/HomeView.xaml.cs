using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Toki.State;

namespace Toki.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    void OnSettings(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Settings);

    void OnMeeting(object sender, RoutedEventArgs e) => App.StartQuickNote();

    void OnTakeNotes(object sender, RoutedEventArgs e) => App.StartQuickNote();

    void OnLibrary(object sender, RoutedEventArgs e)
    {
        // Grow the window out of the pill's current on-screen spot (the "extends from
        // the pill" morph), then let the pill fade.
        var from = System.Windows.Application.Current.Windows.OfType<Toki.PillWindow>().FirstOrDefault()?.ScreenDipRect();
        Toki.LibraryWindow.ShowLibrary(fromRect: from);
        App.State.TryGo(PillState.Hidden);
    }
}
