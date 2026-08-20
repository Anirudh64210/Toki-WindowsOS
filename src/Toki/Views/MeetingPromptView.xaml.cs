using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Toki.Views;

public partial class MeetingPromptView : UserControl, IWidthAware
{
    // One "Record this meeting" pill, plus a "Join call" pill when a scheduled meeting is on now;
    // each layout also reserves the gap and close puck to the right of the right-hand pill.
    const double PillW = 169, JoinW = 112, Gap = 10, PuckW = 24;
    const double RecordOnlyWidth = PillW + Gap + PuckW;
    const double WithJoinWidth = JoinW + Gap + PillW + Gap + PuckW;

    bool _hasJoin;

    public MeetingPromptView()
    {
        InitializeComponent();

        // 24px, not 20: this is the only way to decline, so it must not be the smallest target in the app.
        var close = PillParts.RoundButton(
            PillParts.Outline(PillParts.Cross(), "TextHi"),
            "Glass", "Not now - don't record this meeting", 24);
        close.Click += OnDismiss;
        CloseHost.Content = close;

        // Hovering anywhere on the strip brings it to full strength; it never drops below 55%.
        MouseEnter += (_, _) => FadeClose(1);
        MouseLeave += (_, _) => FadeClose(0.55);

        Refresh();
        // If the detected app changed while the cached view was off-screen, re-fit once shown.
        // The authoritative update runs in Refresh() BEFORE PillWindow measures this view.
        Loaded += (_, _) => { Refresh(); App.GrowNotch(); };
    }

    /// <summary>One pill, or two when there is a call to join.</summary>
    public double CardWidth => _hasJoin ? WithJoinWidth : RecordOnlyWidth;

    void FadeClose(double to) =>
        CloseHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(to, Theme.Motion.Dur(Theme.Motion.Micro)) { EasingFunction = Theme.Motion.EaseOut });

    /// <summary>Set the prompt from live state. Called by PillWindow right before it measures this view,
    /// so what is measured is what will show. A scheduled meeting with a Meet link adds a Join pill.</summary>
    public void Refresh()
    {
        var cal = App.LiveCalendarMeeting();
        Configure(cal is not null, cal?.Summary, App.CurrentApp?.DisplayName);
    }

    /// <summary>Testable/render-harness seam: force the ad-hoc prompt for a specific raw app identity.</summary>
    public void Refresh(string? rawAppName) => Configure(calendarMatch: false, title: null, rawAppName);

    void Configure(bool calendarMatch, string? title, string? rawAppName)
    {
        _hasJoin = calendarMatch;
        Pills.Children.Clear();

        if (calendarMatch)
        {
            // "Join call" opens the Meet link and records.
            var join = PillParts.ActionPill(PillParts.Videocam(), "Join call", JoinW);
            join.Margin = new Thickness(0, 0, Gap, 0);
            join.ToolTip = string.IsNullOrWhiteSpace(title)
                ? "Open the meeting link and start taking notes"
                : $"Open “{title}” and start taking notes";
            join.Click += OnJoin;
            Pills.Children.Add(join);
        }

        var record = PillParts.ActionPill(PillParts.Videocam(), "Record this meeting", PillW);
        // The detected app's identity lives in the tooltip: knowing WHICH app Toki noticed
        // is what makes the prompt trustworthy.
        var name = string.IsNullOrWhiteSpace(rawAppName) ? null : Detection.AppNames.Friendly(rawAppName);
        record.ToolTip = string.IsNullOrWhiteSpace(name)
            ? "Transcribe this call and write the notes, all on this PC"
            : $"{name} is in a call - transcribe it and write the notes, all on this PC";
        record.Click += OnRecord;
        Pills.Children.Add(record);
    }

    /// <summary>Dev/measuretest seam: force the calendar-match (two-pill) layout to verify it fits.</summary>
    public void DebugCalendarMatch(string title) => Configure(calendarMatch: true, title, null);

    void OnJoin(object sender, RoutedEventArgs e) => App.JoinFromPrompt();

    void OnRecord(object sender, RoutedEventArgs e) => App.AcceptPrompt();

    void OnDismiss(object sender, RoutedEventArgs e) => App.DismissPrompt();
}
