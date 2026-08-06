using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Toki.State;

namespace Toki.Views;

/// <summary>
/// The "Built with" screen - open-source attribution. This is a LICENCE OBLIGATION, not decoration:
/// Parakeet is CC-BY-4.0 (name + source + licence link + a note that we modified it), Qwen3 is
/// Apache-2.0, everything else MIT. Each row opens its upstream source. Fixed height + internal scroll.
/// The full licence texts ship alongside the app in THIRD-PARTY-NOTICES.md.
/// </summary>
public partial class AboutView : UserControl
{
    // (Name, what it does for us, who made it, licence, source URL). Grouped by section.
    // Only what Toki actually ships is credited - Whisper is a stubbed fallback and is intentionally absent.
    record Credit(string Name, string Role, string Author, string Licence, string Url);

    static readonly (string Section, Credit[] Items)[] Sections =
    {
        ("MODELS", new[]
        {
            new Credit("Parakeet TDT 0.6b v2", "Speech-to-text · quantised to int8", "NVIDIA",
                       "CC-BY-4.0", "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2"),
            new Credit("Qwen3-4B-Instruct-2507", "On-device notes & chat", "Alibaba Qwen",
                       "Apache-2.0", "https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507"),
            new Credit("bge-small-en-v1.5", "Meaning search", "BAAI",
                       "MIT", "https://huggingface.co/BAAI/bge-small-en-v1.5"),
        }),
        ("ENGINES & LIBRARIES", new[]
        {
            new Credit("sherpa-onnx", "Runs the speech model", "k2-fsa",
                       "Apache-2.0", "https://github.com/k2-fsa/sherpa-onnx"),
            new Credit("ONNX Runtime", "Model inference", "Microsoft",
                       "MIT", "https://github.com/microsoft/onnxruntime"),
            new Credit("LLamaSharp · llama.cpp", "Runs the language model", "SciSharp · G. Gerganov",
                       "MIT", "https://github.com/SciSharp/LLamaSharp"),
            new Credit("NAudio", "Microphone & audio capture", "Mark Heath",
                       "MIT", "https://github.com/naudio/NAudio"),
            new Credit("SQLite", "Local storage", "Microsoft.Data.Sqlite",
                       "MIT · Public Domain", "https://github.com/dotnet/efcore"),
            new Credit("H.NotifyIcon", "Tray icon", "Havendv",
                       "MIT", "https://github.com/HavenDV/H.NotifyIcon"),
            new Credit("SharpCompress", "Model unpacking", "Adam Hathcock",
                       "MIT", "https://github.com/adamhathcock/sharpcompress"),
            new Credit("Recognizers-Text", "Natural-language dates", "Microsoft",
                       "MIT", "https://github.com/microsoft/Recognizers-Text"),
        }),
    };

    public AboutView()
    {
        InitializeComponent();
        Build();
        // Code-built rows capture FindResource brushes by value → rebuild on a theme swap
        // (same pattern as LibraryView/NotesView), or the cached view keeps old-theme colours.
        Toki.Theme.ThemeManager.ThemeChanged += () => Dispatcher.BeginInvoke(() =>
        {
            Body.Children.Clear();
            Build();
        });
    }

    void Build()
    {
        // Intro - the honest, human framing (Toki is on-device; thank the people who made that possible).
        Body.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Secondary"),
            Foreground = (Brush)FindResource("TextDim"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 12, 2, 2),
            Text = "Toki runs entirely on your device, powered by these open models and libraries. "
                 + "Thank you to everyone who made them.",
        });

        foreach (var (section, items) in Sections)
        {
            Body.Children.Add(new TextBlock { Style = (Style)FindResource("SectionHeader"), Text = section });
            foreach (var c in items) Body.Children.Add(Row(c));
        }

        // Footer: where the full texts live + a small credit line.
        Body.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Hairline"), Margin = new Thickness(0, 14, 0, 0) });
        Body.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Dim"), FontSize = 10, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 10, 2, 2),
            Text = "Full licence texts ship with Toki (THIRD-PARTY-NOTICES). Segoe UI Variable and Segoe "
                 + "Fluent Icons are provided by Windows. Made with care in Toki.",
        });
    }

    // One tappable credit row: name + role on the left, a licence chip on the right; the whole row
    // opens the upstream source. Hover lifts the background and reveals an ↗ open-external glyph.
    FrameworkElement Row(Credit c)
    {
        var name = new TextBlock { Style = (Style)FindResource("BodyStrong"), Text = c.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        var sub = new TextBlock { Style = (Style)FindResource("Caption"), Text = $"{c.Role}  ·  {c.Author}",
                                  TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(name);
        left.Children.Add(sub);
        Grid.SetColumn(left, 0);

        // CC-BY / Apache carry real obligations - tint their chips amber so they read as "attribution", not filler.
        var strong = c.Licence.StartsWith("CC-BY") || c.Licence.StartsWith("Apache");
        var chip = new Border
        {
            Background = (Brush)FindResource(strong ? "AmberDark" : "Chip"),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock
            {
                Text = c.Licence, FontSize = 10, FontWeight = FontWeights.SemiBold,
                FontFamily = (FontFamily)FindResource("UiFont"),
                Foreground = (Brush)FindResource(strong ? "Amber" : "TextDim"),
            },
        };
        Grid.SetColumn(chip, 1);

        var arrow = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11,
            Foreground = (Brush)FindResource("TextDim"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0), Opacity = 0,
        };
        Grid.SetColumn(arrow, 2);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        grid.Children.Add(chip);
        grid.Children.Add(arrow);

        var row = new Border
        {
            Child = grid, Padding = new Thickness(8, 9, 8, 9), Margin = new Thickness(-6, 2, -6, 0),
            CornerRadius = new CornerRadius(10), Background = Brushes.Transparent,
            Cursor = Cursors.Hand, ToolTip = c.Url,
        };
        row.MouseEnter += (_, _) => { row.Background = (Brush)FindResource("Chip"); arrow.Opacity = 1; };
        row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; arrow.Opacity = 0; };
        row.MouseLeftButtonUp += (_, _) => Open(c.Url);
        return row;
    }

    static void Open(string url) => Toki.Interop.SafeShell.OpenUrl(url);   // one shell choke point

    void OnBack(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Settings);
}
