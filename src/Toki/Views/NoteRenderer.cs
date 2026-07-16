using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toki.Storage;
using Toki.Transcription;

namespace Toki.Views;

/// <summary>
/// Builds the visual for a meeting's notes (TL;DR / decisions / to-dos / questions
/// / transcript) into a panel. Shared by the pill Notes view and the resizable
/// Library window so the layout is clean and consistent in both — spacing scales
/// with <paramref name="spacious"/> (window) vs compact (pill).
/// </summary>
public static class NoteRenderer
{
    static object Res(string key) => Application.Current.FindResource(key);
    static Brush B(string key) => (Brush)Res(key);

    public static void Meeting(Panel body, MeetingRecord m, bool spacious)
    {
        body.Children.Clear();
        var fs = spacious ? 13.0 : 11.5;
        var gap = spacious ? 6.0 : 5.0;

        if (!string.IsNullOrWhiteSpace(m.Notes.Tldr))
        {
            SectionHeader(body, "SUMMARY", "Coral", spacious);
            body.Children.Add(new TextBlock
            {
                Text = m.Notes.Tldr, TextWrapping = TextWrapping.Wrap,
                FontFamily = (FontFamily)Res("UiFont"), FontSize = fs + 0.5, LineHeight = fs + 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xD4)),
                Margin = new Thickness(0, 0, 0, spacious ? 4 : 2),
            });
        }

        // "Decisions" storage is repurposed as key points (see NoteSet).
        Section(body, "KEY POINTS", "Green", m.Notes.Decisions.Select(d => Bullet(d, fs, gap)), spacious);
        Section(body, "ACTION ITEMS", "Blue", m.Notes.Actions.Select(a => ActionRow(a, fs, gap, spacious)), spacious);

        if (m.Transcript.Count > 0)
        {
            SectionHeader(body, "TRANSCRIPT", "TextDim", spacious);
            foreach (var s in m.Transcript) body.Children.Add(TranscriptRow(s, fs, spacious));
        }
    }

    static void Section(Panel body, string title, string color, IEnumerable<FrameworkElement> rows, bool spacious)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        SectionHeader(body, title, color, spacious);
        foreach (var r in list) body.Children.Add(r);
    }

    static void SectionHeader(Panel body, string title, string color, bool spacious) =>
        body.Children.Add(new TextBlock
        {
            Text = title, FontFamily = (FontFamily)Res("UiFont"),
            FontSize = spacious ? 11 : 10, FontWeight = FontWeights.Bold,
            Foreground = B(color), Margin = new Thickness(0, spacious ? 20 : 15, 0, spacious ? 9 : 6),
        });

    static TextBlock Bullet(string text, double fs, double gap) => new()
    {
        Text = "•   " + text, TextWrapping = TextWrapping.Wrap,
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"), FontSize = fs,
        Foreground = B("Text"), LineHeight = fs + 7, Margin = new Thickness(2, 0, 0, gap + 2),
    };

    static FrameworkElement ActionRow(ActionItem a, double fs, double gap, bool spacious)
    {
        var check = new CheckBox
        {
            IsChecked = a.Done, Style = (Style)Res("TokiCheck"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1, 1, 11, 0),
        };
        check.Checked += (_, _) => Toggle(a, true);
        check.Unchecked += (_, _) => Toggle(a, false);

        var text = new TextBlock { Text = a.Text, TextWrapping = TextWrapping.Wrap, FontFamily = (FontFamily)Res("UiFont"), FontSize = fs, Foreground = B("Text"), LineHeight = fs + 6 };
        if (a.Done) { text.TextDecorations = TextDecorations.Strikethrough; text.Opacity = 0.55; }

        var right = new StackPanel();
        right.Children.Add(text);
        var tags = new StackPanel { Orientation = Orientation.Horizontal };
        if (Meaningful(a.Owner)) tags.Children.Add(Tag(a.Owner!, "Chip", "TextDim"));
        if (Meaningful(a.Due)) tags.Children.Add(Tag(a.Due!, "AmberDark", "Amber"));
        if (tags.Children.Count > 0) right.Children.Add(tags);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, gap + 5) };
        row.Children.Add(check);
        row.Children.Add(right);
        return row;
    }

    static FrameworkElement TranscriptRow(TranscriptSegment seg, double fs, bool spacious)
    {
        var you = seg.Speaker == Speaker.You;
        var chip = new Border
        {
            Background = B(you ? "BlueDark" : "Chip"), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 8, 0),
            Child = new TextBlock { Text = you ? "YOU" : "THEM", FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = B(you ? "Blue" : "TextDim"), FontFamily = (FontFamily)Res("UiFont") },
        };
        var text = new TextBlock
        {
            Text = seg.Text, TextWrapping = TextWrapping.Wrap, FontFamily = (FontFamily)Res("UiFont"),
            FontSize = fs - 0.5, Foreground = B("Text"), LineHeight = fs + 6,
            MaxWidth = spacious ? 620 : 300, TextAlignment = you ? TextAlignment.Right : TextAlignment.Left,
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = you ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 9),
        };
        if (you) { row.Children.Add(text); row.Children.Add(chip); }
        else { row.Children.Add(chip); row.Children.Add(text); }
        return row;
    }

    // Old rows may carry placeholder junk ("->", "-", "TBD") the model left behind.
    static bool Meaningful(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        var s = v.Trim().Trim('<', '>', '(', ')', '[', ']').Trim().ToLowerInvariant();
        return s.Length > 1 && s is not ("--" or "->" or "n/a" or "na" or "none"
            or "tbd" or "tba" or "unknown" or "unassigned" or "name" or "when");
    }

    static Border Tag(string text, string bgKey, string fgKey) => new()
    {
        Background = B(bgKey), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 2),
        Margin = new Thickness(0, 5, 6, 0),
        Child = new TextBlock { Text = text, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = B(fgKey), FontFamily = (FontFamily)Res("UiFont") },
    };

    static void Toggle(ActionItem a, bool done)
    {
        a.Done = done;
        if (a.Id > 0) try { App.Store.SetActionDone(a.Id, done); } catch { }
    }

    /// <summary>Plain-text of a meeting's transcript, for the Copy button.</summary>
    public static string TranscriptText(MeetingRecord m) =>
        string.Join(Environment.NewLine, m.Transcript.Select(s =>
            $"{(s.Speaker == Speaker.You ? "You" : "Them")}: {s.Text}"));
}
