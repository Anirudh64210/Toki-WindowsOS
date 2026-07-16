using System.Windows.Controls;
using System.Windows.Threading;

namespace Toki.Views;

/// <summary>
/// Shared helper: keeps a TextBlock showing elapsed recording time while its
/// view is on screen. Wall-clock from AppState.RecordingStartedAt (lesson 10).
/// </summary>
static class RecordingClock
{
    public static void Attach(UserControl view, TextBlock target)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            var start = App.State.RecordingStartedAt;
            var elapsed = start is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - start.Value;
            target.Text = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
        };
        view.Loaded += (_, _) => timer.Start();
        view.Unloaded += (_, _) => timer.Stop();
    }
}
