using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toki.Llm;
using Toki.State;
using Toki.Transcription;

namespace Toki.Views;

public partial class SettingsView : UserControl
{
    bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    void Load()
    {
        _loading = true;
        var s = AppSettings.Current;

        NotesModeSeg.Children.Clear();
        NotesModeSeg.Children.Add(SegButton("On-device", NotesMode.Local, s.NotesMode));
        NotesModeSeg.Children.Add(SegButton("Heuristic", NotesMode.HeuristicOnly, s.NotesMode));
        NotesModeSeg.Children.Add(SegButton("My API key", NotesMode.ApiKey, s.NotesMode));

        GpuToggle.IsChecked = s.GpuLayers >= 0; // on = auto (uses GPU when it fits safely)
        ApiKeyBox.Password = s.ApiKey ?? "";

        LocalOptions.Visibility = s.NotesMode == NotesMode.Local ? Visibility.Visible : Visibility.Collapsed;
        ApiOptions.Visibility = s.NotesMode == NotesMode.ApiKey ? Visibility.Visible : Visibility.Collapsed;

        ModelStatus.Text = s.NotesMode switch
        {
            NotesMode.HeuristicOnly => "Using fast built-in extraction (no AI model).",
            NotesMode.ApiKey => string.IsNullOrWhiteSpace(s.ApiKey)
                ? "Add an API key above to enable sharper summaries."
                : $"Using your API key ({s.ApiModel}).",
            _ => ModelManager.QwenInstalled
                ? "On-device model installed and ready (Qwen3-4B)."
                : "On-device model will download (~2.5 GB) the first time notes are written.",
        };
        _loading = false;
    }

    Border SegButton(string label, NotesMode mode, NotesMode current)
    {
        var on = mode == current;
        var text = new TextBlock
        {
            Text = label, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11,
            FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)FindResource(on ? "Text" : "TextDim"),
            FontFamily = (FontFamily)FindResource("UiFont"),
        };
        var b = new Border
        {
            Background = on ? (Brush)FindResource("Chip") : Brushes.Transparent,
            CornerRadius = new CornerRadius(8), Padding = new Thickness(0, 6, 0, 6),
            Cursor = System.Windows.Input.Cursors.Hand, Child = text,
        };
        b.MouseLeftButtonUp += (_, _) => SetMode(mode);
        return b;
    }

    void SetMode(NotesMode mode)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        s.NotesMode = mode;
        s.Save();
        Load(); // refresh the segmented control + conditional sections
    }

    void OnGpuToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        // On = auto (0): detect VRAM and offload a safe number of layers. Off = force
        // CPU (-1). Takes effect next launch (the model is loaded once at startup).
        s.GpuLayers = GpuToggle.IsChecked == true ? 0 : -1;
        s.Save();
    }

    void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        s.ApiKey = ApiKeyBox.Password;
        s.Save();
    }

    void OnBack(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Home);
}
