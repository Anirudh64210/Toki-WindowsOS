using System.Windows;
using System.Windows.Controls;
using Toki.State;

namespace Toki.Views;

public partial class NoSpeechView : UserControl
{
    public NoSpeechView() => InitializeComponent();

    void OnClose(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Hidden);
}
