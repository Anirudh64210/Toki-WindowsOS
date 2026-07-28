using System.Windows.Controls;
using System.Windows.Input;
using Toki.State;

namespace Toki.Views;

public partial class CollapsedView : UserControl
{
    public CollapsedView() => InitializeComponent();

    // Click the Toki peek → open the remembered page (Home by default), via the pill.
    void OnOpen(object sender, MouseButtonEventArgs e)
    {
        if (App.OpenPeek is { } open) open();
        else App.State.TryGo(PillState.Home);
    }
}
