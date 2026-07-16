using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toki.State;
using Toki.Transcription;

namespace Toki.Views;

public partial class NotesView : UserControl
{
    public NotesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Populate();
    }

    void Populate()
    {
        Body.Children.Clear();
        var transcript = App.LastTranscript;

        if (transcript is null)
        {
            // Model not installed yet — recording is saved, just not transcribed.
            Subtitle.Text = "recording saved";
            Body.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Body"),
                FontSize = 12,
                Foreground = (Brush)FindResource("TextDim"),
                Margin = new Thickness(0, 12, 0, 0),
                Text = "The speech model isn't installed yet, so this meeting was saved but not transcribed. "
                     + "Install the on-device model to get transcripts and notes.",
            });
            return;
        }

        var youCount = transcript.Count(s => s.Speaker == Speaker.You);
        var themCount = transcript.Count - youCount;
        Subtitle.Text = $"{transcript.Count} lines · you {youCount} · them {themCount}";

        foreach (var seg in transcript)
            Body.Children.Add(SegmentRow(seg));
    }

    FrameworkElement SegmentRow(TranscriptSegment seg)
    {
        var you = seg.Speaker == Speaker.You;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = you ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 9),
        };

        var chip = new Border
        {
            Background = (Brush)FindResource(you ? "BlueDark" : "Chip"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1.5, 5, 1.5),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 7, 0),
            Child = new TextBlock
            {
                Text = you ? "YOU" : "THEM",
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource(you ? "Blue" : "TextDim"),
                FontFamily = (FontFamily)FindResource("UiFont"),
            },
        };

        var text = new TextBlock
        {
            Text = seg.Text,
            Style = (Style)FindResource("Body"),
            FontSize = 11.5,
            MaxWidth = 300,
            TextAlignment = you ? TextAlignment.Right : TextAlignment.Left,
        };

        if (you) { row.Children.Add(text); row.Children.Add(chip); }
        else { row.Children.Add(chip); row.Children.Add(text); }
        return row;
    }

    void OnClose(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Hidden);
}
