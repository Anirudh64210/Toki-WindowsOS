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

    void RenderNotes(MeetingRecord m)
    {
        if (!string.IsNullOrWhiteSpace(m.Notes.Tldr))
            Body.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Body"), FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xC7)),
                Margin = new Thickness(0, 8, 0, 0), Text = m.Notes.Tldr,
            });

        if (m.Notes.Decisions.Count > 0)
        {
            Body.Children.Add(Header("✓  DECISIONS", "Green"));
            foreach (var d in m.Notes.Decisions) Body.Children.Add(Bullet(d));
        }
        if (m.Notes.Actions.Count > 0)
        {
            Body.Children.Add(Header("→  ACTION ITEMS", "Blue"));
            foreach (var a in m.Notes.Actions) Body.Children.Add(ActionRow(a));
        }
        if (m.Notes.Questions.Count > 0)
        {
            Body.Children.Add(Header("?  OPEN QUESTIONS", "Purple"));
            foreach (var q in m.Notes.Questions) Body.Children.Add(Bullet(q));
        }
        if (m.Transcript.Count > 0)
        {
            Body.Children.Add(Header("▤  TRANSCRIPT", "TextDim"));
            foreach (var s in m.Transcript) Body.Children.Add(TranscriptRow(s));
        }
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

    TextBlock Header(string text, string colorKey) => new()
    {
        Text = text, Style = (Style)FindResource("SectionHeader"),
        Foreground = (Brush)FindResource(colorKey),
    };

    TextBlock Bullet(string text) => new()
    {
        Style = (Style)FindResource("Body"), FontSize = 11.5, Margin = new Thickness(0, 0, 0, 5),
        Text = "•  " + text,
    };

    TextBlock Dim(string text) => new()
    {
        Style = (Style)FindResource("Body"), FontSize = 12,
        Foreground = (Brush)FindResource("TextDim"), Margin = new Thickness(0, 12, 0, 0), Text = text,
    };

    FrameworkElement ActionRow(ActionItem a)
    {
        var check = new CheckBox { IsChecked = a.Done, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 8, 0) };
        check.Checked += (_, _) => Toggle(a, true);
        check.Unchecked += (_, _) => Toggle(a, false);

        var textBlock = new TextBlock { Text = a.Text, Style = (Style)FindResource("Body"), FontSize = 11.5 };
        var tags = new StackPanel { Orientation = Orientation.Horizontal };
        if (!string.IsNullOrWhiteSpace(a.Owner)) tags.Children.Add(Tag(a.Owner!, "Chip", "TextDim"));
        if (!string.IsNullOrWhiteSpace(a.Due)) tags.Children.Add(Tag(a.Due!, "AmberDark", "Amber"));

        var right = new StackPanel();
        right.Children.Add(textBlock);
        if (tags.Children.Count > 0) right.Children.Add(tags);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
        row.Children.Add(check);
        row.Children.Add(right);
        return row;
    }

    Border Tag(string text, string bgKey, string fgKey) => new()
    {
        Background = (Brush)FindResource(bgKey), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 4, 6, 0),
        Child = new TextBlock
        {
            Text = text, FontSize = 9.5, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource(fgKey), FontFamily = (FontFamily)FindResource("UiFont"),
        },
    };

    void Toggle(ActionItem a, bool done)
    {
        a.Done = done;
        if (a.Id > 0) try { App.Store.SetActionDone(a.Id, done); } catch { /* non-fatal */ }
    }

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
