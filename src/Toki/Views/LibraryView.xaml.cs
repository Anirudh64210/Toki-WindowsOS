using System.Windows;
using System.Windows.Controls;
using Toki.State;

namespace Toki.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    void OnBack(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Home);
}
