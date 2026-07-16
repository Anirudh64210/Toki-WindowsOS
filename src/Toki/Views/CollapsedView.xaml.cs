using System.Windows.Controls;
using System.Windows.Input;
using Toki.State;

namespace Toki.Views;

public partial class CollapsedView : UserControl
{
    public CollapsedView() => InitializeComponent();

    void OnOpen(object sender, MouseButtonEventArgs e) => App.State.TryGo(PillState.Home);
}
