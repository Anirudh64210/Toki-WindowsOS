using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toki.State;

namespace Toki.Views;

public partial class RecordingOpenView : UserControl
{
    public RecordingOpenView()
    {
        InitializeComponent();
        RecordingClock.Attach(this, Timer);
    }

    void OnCollapse(object sender, MouseButtonEventArgs e) => App.State.TryGo(PillState.RecordingBar);

    void OnStar(object sender, RoutedEventArgs e)
    {
        // Placeholder: markers land with real transcription (M4).
    }

    void OnStop(object sender, RoutedEventArgs e) => App.StopRecording();
}
