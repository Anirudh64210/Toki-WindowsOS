using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toki.State;
using Toki.Storage;
using Toki.Transcription;

namespace Toki.Views;

public partial class NotesView : UserControl
{
    public NotesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Populate();
        // Re-render when notes are upgraded (heuristic → LLM) while on screen.
        App.NotesUpdated += () => { if (IsLoaded) Populate(); };
    }

    void Populate()
    {
        Body.Children.Clear();
        var meeting = App.CurrentMeeting;

        if (meeting is not null)
        {
            Title.Text = meeting.Title;
            Subtitle.Text = Meta(meeting);
            RenderNotes(meeting);
            return;
        }

        // No structured notes (e.g., model not installed): fall back to raw transcript.
        Title.Text = "Transcript";
        var transcript = App.LastTranscript;
        if (transcript is null)
        {
            Subtitle.Text = "recording saved";
            Body.Children.Add(Dim("The speech model isn't installed yet, so this meeting was saved but not "
                + "transcribed. Install the on-device model to get transcripts and notes."));
            return;
        }
        Subtitle.Text = $"{transcript.Count} lines";
        foreach (var seg in transcript) Body.Children.Add(TranscriptRow(seg));
    }

    void RenderNotes(MeetingRecord m) => NoteRenderer.Meeting(Body, m, spacious: false);

    void OnCopy(object sender, RoutedEventArgs e)
    {
        if (App.CurrentMeeting is { } m && m.Transcript.Count > 0)
        {
            try { Clipboard.SetText(NoteRenderer.TranscriptText(m)); CopyBtn.Content = "✓"; } catch { }
        }
    }

    void OnMaximise(object sender, RoutedEventArgs e)
    {
        // Consistent with Home → Library: dismiss the pill first, then show the warm window
        // so it opens instantly and smoothly (no old overlapping handoff).
        App.State.TryGo(PillState.Hidden);
        LibraryWindow.ShowLibrary(App.CurrentMeeting?.Id);
    }

    static string Meta(MeetingRecord m)
    {
        var parts = new List<string>();
        if (m.DurationMs > 0) parts.Add($"{Math.Max(1, m.DurationMs / 60000)} min");
        parts.Add(Ago(m.StartedAt));
        if (!string.IsNullOrWhiteSpace(m.App)) parts.Add(m.App!);
        return string.Join(" · ", parts);
    }

    static string Ago(DateTimeOffset when)
    {
        var d = DateTimeOffset.Now - when;
        if (d.TotalMinutes < 2) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 2) return "yesterday";
        return $"{(int)d.TotalDays} days ago";
    }

    TextBlock Dim(string text) => new()
    {
        Style = (Style)FindResource("Body"), FontSize = 12,
        Foreground = (Brush)FindResource("TextDim"), Margin = new Thickness(0, 12, 0, 0), Text = text,
    };

    FrameworkElement TranscriptRow(TranscriptSegment seg)
    {
        var you = seg.Speaker == Speaker.You;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = you ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var chip = new Border
        {
            Background = (Brush)FindResource(you ? "BlueDark" : "Chip"),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1.5, 5, 1.5),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 7, 0),
            Child = new TextBlock
            {
                Text = you ? "YOU" : "THEM", FontSize = 8.5, FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource(you ? "Blue" : "TextDim"), FontFamily = (FontFamily)FindResource("UiFont"),
            },
        };
        var text = new TextBlock
        {
            Text = seg.Text, Style = (Style)FindResource("Body"), FontSize = 11,
            MaxWidth = 300, TextAlignment = you ? TextAlignment.Right : TextAlignment.Left,
        };
        if (you) { row.Children.Add(text); row.Children.Add(chip); }
        else { row.Children.Add(chip); row.Children.Add(text); }
        return row;
    }

    void OnClose(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Hidden);
}
