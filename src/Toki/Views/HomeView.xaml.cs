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
        Toki.LibraryWindow.ShowLibrary();
        App.State.TryGo(PillState.Hidden);
    }
}
