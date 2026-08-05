using System.Windows;
using System.Windows.Controls;

namespace Toki.Views;

public partial class MeetingPromptView : UserControl
{
    public MeetingPromptView()
    {
        InitializeComponent();
        Refresh();
        // Belt and braces: if the detected app changed while the cached view was off-screen, re-fit
        // the notch to the (possibly re-wrapped) text once shown. The AUTHORITATIVE text update runs
        // in Refresh() BEFORE PillWindow measures the content height — Loaded fires after layout, so
        // updating only here was the real clip bug (height measured on stale one-line text, longer
        // text then wrapped past the reveal clip and the prompt rendered cut off).
        Loaded += (_, _) => { Refresh(); App.GrowNotch(); };
    }

    /// <summary>Set the prompt from live state. Called by PillWindow right before it measures this view's
    /// height, so the measured height always reflects the REAL (possibly wrapped) text. "Both smart": if a
    /// scheduled meeting with a Meet link is happening now, offer to Join the call + Record; otherwise
    /// it's an ad-hoc detected call → Join &amp; take notes (record).</summary>
    public void Refresh()
    {
        var cal = App.LiveCalendarMeeting();
        Configure(cal is not null, cal?.Summary, App.CurrentApp?.DisplayName);
    }

    /// <summary>Testable/render-harness seam: force the ad-hoc prompt for a specific raw app identity.</summary>
    public void Refresh(string? rawAppName) => Configure(calendarMatch: false, title: null, rawAppName);

    void Configure(bool calendarMatch, string? title, string? rawAppName)
    {
        if (calendarMatch)
        {
            // A scheduled call is on right now — "Join call" opens the Meet link; "Record" is secondary.
            Prompt.Text = string.IsNullOrWhiteSpace(title) ? "A meeting is on now — join?" : $"“{title}” is on now — join?";
            JoinBtn.Visibility = Visibility.Visible;
            RecordBtn.Content = "Record";
            RecordBtn.Style = (Style)FindResource("WhiteButton");
        }
        else
        {
            // Ad-hoc detected call — the FRIENDLY app name, never the raw process id (a past live bug).
            var name = string.IsNullOrWhiteSpace(rawAppName) ? null : Detection.AppNames.Friendly(rawAppName);
            Prompt.Text = string.IsNullOrWhiteSpace(name) ? "A meeting's in progress." : $"{name} is in a call.";
            JoinBtn.Visibility = Visibility.Collapsed;
            RecordBtn.Content = "Join & take notes";   // Toki "joins" by taking notes on the call
            RecordBtn.Style = (Style)FindResource("CoralButton");
        }
    }

    /// <summary>Dev/measuretest seam: force the calendar-match (3-button) layout to verify it fits.</summary>
    public void DebugCalendarMatch(string title) => Configure(calendarMatch: true, title, null);

    void OnJoin(object sender, RoutedEventArgs e) => App.JoinFromPrompt();

    void OnRecord(object sender, RoutedEventArgs e) => App.AcceptPrompt();

    void OnDismiss(object sender, RoutedEventArgs e) => App.DismissPrompt();
}
