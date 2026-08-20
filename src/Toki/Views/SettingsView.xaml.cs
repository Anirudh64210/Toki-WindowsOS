using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Toki.Llm;
using Toki.State;
using Toki.Theme;

namespace Toki.Views;

/// <summary>
/// The notch Settings - UI/visibility settings only: theme + ghost in the header, the glassy-notch
/// toggle, and a row that opens the Library's Settings page, where everything else lives
/// (see <see cref="AiSummariesSection"/> and LibraryWindow's SettingsPage).
/// </summary>
public partial class SettingsView : UserControl
{
    bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        Load();
        Loaded += (_, _) => Load();
        UpdateThemeIcon();
        UpdateRecordingIcon();
    }

    void Load()
    {
        _loading = true;
        GlassToggle.IsChecked = AppSettings.Current.GlassyNotch;
        _loading = false;
    }

    // ── appearance (light/dark): one quiet header toggle ──

    void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        var next = ThemeManager.Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        var s = AppSettings.Current;
        s.Theme = next == AppTheme.Light ? "light" : "dark";
        s.Save();
        ThemeManager.Apply(next);        // recolours the whole app live (DynamicResource re-resolves)
        UpdateThemeIcon();
        UpdateRecordingIcon();           // its strike line re-resolves, but rebuild keeps it crisp
    }

    // Show the glyph for the mode you'd switch TO: a sun in dark mode (→ light), a moon in light (→ dark).
    void UpdateThemeIcon()
    {
        var dark = ThemeManager.Current == AppTheme.Dark;
        ThemeToggle.Content = dark ? "" : "";   // Fluent: Brightness (sun, go light) / QuietHours (moon, go dark)
        ThemeToggle.ToolTip = dark ? "Switch to light" : "Switch to dark";
    }

    void OnGlassToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.SetGlassyNotch(GlassToggle.IsChecked == true);
    }

    // ── screen-recording visibility (ghost): one header icon ──

    void OnRecordingToggle(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        s.ShowInRecordings = !s.ShowInRecordings;   // on = visible to recorders (ghost off); off = hidden (default)
        s.Save();
        App.SetShowInRecordings(s.ShowInRecordings);   // applies live, no restart
        UpdateRecordingIcon();
    }

    // A video camera, crossed out when Toki is hidden from recordings (ghost on, the default).
    void UpdateRecordingIcon()
    {
        var hidden = !AppSettings.Current.ShowInRecordings;   // ghost on = not shown in recordings

        var glyph = new TextBlock
        {
            Text = ((char)0xE714).ToString(),   // Fluent: Video
            FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        var strike = new System.Windows.Shapes.Line
        {
            X1 = 4, Y1 = 19, X2 = 20, Y2 = 5, StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Visibility = hidden ? Visibility.Visible : Visibility.Collapsed,
        };
        strike.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Text");
        var box = new Grid { Width = 24, Height = 24 };
        box.Children.Add(glyph);
        box.Children.Add(strike);
        RecordToggle.Content = box;
        RecordToggle.ToolTip = hidden
            ? "Hidden from screen recordings & sharing - tap to show Toki (to record a demo)"
            : "VISIBLE in screen recordings & sharing - tap to hide Toki again";
    }

    // ── everything else lives in the Library's Settings page ──

    void OnOpenLibrarySettings(object sender, RoutedEventArgs e)
    {
        App.CloseOverlay();                       // recording-aware: a live session keeps its bar
        LibraryWindow.OpenHubPage("settings");
    }

    void OnBack(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Home);
}
