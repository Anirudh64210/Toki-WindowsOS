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

    /// <summary>Set the prompt text from the currently detected app. Called by PillWindow right
    /// before it measures this view's height, so the measured height always reflects the REAL text.</summary>
    public void Refresh() => Refresh(App.CurrentApp?.DisplayName);

    /// <summary>Testable/render-harness seam: set the prompt for a specific raw app identity.</summary>
    public void Refresh(string? rawAppName)
    {
        // "Slack is in a call. Record it?" when we know the app; generic otherwise. The FRIENDLY name,
        // never the raw process identity ("Com.tinyspeck.slackdesktop" leaking here was a live bug).
        var name = string.IsNullOrWhiteSpace(rawAppName) ? null : Detection.AppNames.Friendly(rawAppName);
        Prompt.Text = string.IsNullOrWhiteSpace(name) ? "Record this meeting?" : $"{name} is in a call. Record it?";
    }

    void OnRecord(object sender, RoutedEventArgs e) => App.AcceptPrompt();

    void OnDismiss(object sender, RoutedEventArgs e) => App.DismissPrompt();
}
