using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toki.State;

namespace Toki.Views;

public partial class RecordingBarView : UserControl
{
    public RecordingBarView()
    {
        InitializeComponent();
        RecordingClock.Attach(this, Timer);
    }

    void OnExpand(object sender, MouseButtonEventArgs e) => App.State.TryGo(PillState.RecordingOpen);

    void OnStop(object sender, RoutedEventArgs e) => App.StopRecording();
}
