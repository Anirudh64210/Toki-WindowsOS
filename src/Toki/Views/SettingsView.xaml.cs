using System.Windows;
using System.Windows.Controls;
using Toki.State;

namespace Toki.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    void OnBack(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Home);
}
