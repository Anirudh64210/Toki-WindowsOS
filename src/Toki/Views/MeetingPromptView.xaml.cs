using System.Windows;
using System.Windows.Controls;

namespace Toki.Views;

public partial class MeetingPromptView : UserControl
{
    public MeetingPromptView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // "Zoom is in a call — record it?" when we know the app; generic otherwise.
            var app = App.CurrentApp?.DisplayName;
            var name = string.IsNullOrWhiteSpace(app) ? null : PrettyName(app!);
            Prompt.Text = name is null ? "Record this meeting?" : $"{name} is in a call — record it?";
        };
    }

    static string PrettyName(string exeOrName)
    {
        var n = exeOrName.Replace(".exe", "", System.StringComparison.OrdinalIgnoreCase);
        return n switch
        {
            "chrome" => "Chrome",
            "msedge" => "Edge",
            "firefox" => "Firefox",
            "brave" => "Brave",
            "Zoom" or "zoom" => "Zoom",
            "ms-teams" or "MSTeams" or "Teams" => "Teams",
            "Discord" or "discord" => "Discord",
            "slack" or "Slack" => "Slack",
            _ => char.ToUpperInvariant(n[0]) + n[1..],
        };
    }

    void OnRecord(object sender, RoutedEventArgs e) => App.AcceptPrompt();

    void OnDismiss(object sender, RoutedEventArgs e) => App.DismissPrompt();
}
