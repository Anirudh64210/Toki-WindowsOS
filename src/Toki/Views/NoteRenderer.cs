using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Toki.Storage;
using Toki.Transcription;

namespace Toki.Views;

/// <summary>
/// Builds a meeting's notes (Summary / Key points / Action items / Transcript) into a
/// panel, shared by the pill Notes view and the Library window. Deliberately clean and
/// MONOTONE — section labels are a quiet uppercase grey (no coloured headers, no icons),
/// generous consistent spacing, so it reads calm and Apple-clean. Colour is reserved for
/// content, never chrome.
/// </summary>
public static class NoteRenderer
{
    static object Res(string key) => Application.Current.FindResource(key);
    static Brush B(string key) => (Brush)Res(key);
    static FontFamily Ui => (FontFamily)Res("UiFont");

    public static void Meeting(Panel body, MeetingRecord m, bool spacious)
    {
        body.Children.Clear();
        var fs = spacious ? 13.5 : 12.0;

        // Generation guard: if this panel is re-rendered for another meeting, any pending
        // deferred transcript work for the old one stops (checks body.Tag still == token).
        var token = new object();
        body.Tag = token;

        if (!string.IsNullOrWhiteSpace(m.Notes.Tldr))
        {
            SectionHeader(body, "SUMMARY", spacious, first: true);
            body.Children.Add(new TextBlock
            {
                Text = m.Notes.Tldr, TextWrapping = TextWrapping.Wrap, FontFamily = Ui,
                FontSize = fs, LineHeight = fs + 11, TextAlignment = TextAlignment.Justify,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDB)),
                Margin = new Thickness(0, 0, 0, 2),
            });
        }

        // "Decisions" storage is repurposed as key points (see NoteSet).
        Section(body, "KEY POINTS", m.Notes.Decisions.Select(d => Bullet(d, fs, spacious)), spacious);
        Section(body, "ACTION ITEMS", m.Notes.Actions.Select(a => ActionRow(a, fs, spacious)), spacious);

        if (m.Transcript.Count > 0)
        {
            SectionHeader(body, "TRANSCRIPT", spacious);
            // Do NOT build the whole transcript synchronously — a long meeting is hundreds
            // to 1000+ segments (~5 visual elements each), and dumping them all into a
            // non-virtualizing panel on the click froze the window open for ~2s. Render a
            // small first batch now (instant open) and stream the rest in on Background
            // priority so the UI thread never blocks. The transcript is below the fold
            // anyway; it fills in as you'd scroll to it.
            RenderTranscriptChunked(body, m.Transcript, fs, spacious, token, 0);
        }
    }

    // First batch is small enough to feel instant; later batches are larger and yield to
    // input between them so scrolling/clicking stays responsive while it fills.
    static void RenderTranscriptChunked(Panel body, IReadOnlyList<TranscriptSegment> segs,
        double fs, bool spacious, object token, int start)
    {
        var batch = start == 0 ? 25 : 80;
        var end = Math.Min(start + batch, segs.Count);
        for (var i = start; i < end; i++) body.Children.Add(TranscriptRow(segs[i], fs, spacious));
        if (end >= segs.Count) return;
        body.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(body.Tag, token)) return; // panel reused for another meeting
            RenderTranscriptChunked(body, segs, fs, spacious, token, end);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    static void Section(Panel body, string title, IEnumerable<FrameworkElement> rows, bool spacious)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        SectionHeader(body, title, spacious);
        foreach (var r in list) body.Children.Add(r);
    }

    // Quiet, monotone section label — small uppercase grey, well spaced. No colour, no icon.
    static void SectionHeader(Panel body, string title, bool spacious, bool first = false) =>
        body.Children.Add(new TextBlock
        {
            Text = title, FontFamily = Ui, FontSize = spacious ? 10.5 : 9.5, FontWeight = FontWeights.SemiBold,
            Foreground = B("TextDim"), Margin = new Thickness(0, first ? 0 : (spacious ? 22 : 17), 0, spacious ? 10 : 8),
        });

    static FrameworkElement Bullet(string text, double fs, bool spacious)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, spacious ? 10 : 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dot = new Ellipse
        {
            Width = 3.5, Height = 3.5, Fill = B("TextDim"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, fs / 2, 12, 0),
        };
        var tb = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontFamily = Ui, FontSize = fs,
            Foreground = B("Text"), LineHeight = fs + 8, TextAlignment = TextAlignment.Justify,
        };
        Grid.SetColumn(tb, 1);
        row.Children.Add(dot);
        row.Children.Add(tb);
        return row;
    }

    static FrameworkElement ActionRow(ActionItem a, double fs, bool spacious)
    {
        var check = new CheckBox
        {
            IsChecked = a.Done, Style = (Style)Res("TokiCheck"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1, 1, 11, 0),
        };
        check.Checked += (_, _) => Toggle(a, true);
        check.Unchecked += (_, _) => Toggle(a, false);

        var text = new TextBlock
        {
            Text = a.Text, TextWrapping = TextWrapping.Wrap, FontFamily = Ui, FontSize = fs,
            Foreground = B("Text"), LineHeight = fs + 7, TextAlignment = TextAlignment.Justify,
        };
        if (a.Done) { text.TextDecorations = TextDecorations.Strikethrough; text.Opacity = 0.5; }

        var right = new StackPanel();
        right.Children.Add(text);
        var tags = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        if (Meaningful(a.Owner)) tags.Children.Add(Tag(a.Owner!));
        if (Meaningful(a.Due)) tags.Children.Add(Tag(a.Due!));
        if (tags.Children.Count > 0) right.Children.Add(tags);

        // A Grid, NOT a horizontal StackPanel: a StackPanel gives the text child INFINITE width, so a
        // long task never wraps and runs off the right edge (you couldn't scroll to see the end). The
        // star column constrains it so it wraps within the notch.
        var row = new Grid { Margin = new Thickness(0, 0, 0, spacious ? 12 : 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        row.Children.Add(check);
        row.Children.Add(right);
        return row;
    }

    static FrameworkElement TranscriptRow(TranscriptSegment seg, double fs, bool spacious)
    {
        // Clean, consistent, generously-spaced turns. Monotone speaker chip on the left
        // (YOU a touch brighter than THEM), the line flowing beside it, left-aligned so it
        // reads like a clear script rather than a cramped two-sided chat.
        var you = seg.Speaker == Speaker.You;
        var chip = new Border
        {
            Background = B(you ? "SurfaceHi" : "Chip"), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, fs / 2 - 7, 10, 0), MinWidth = 40, HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = you ? "YOU" : "THEM", FontSize = 8.5, FontWeight = FontWeights.Bold,
                Foreground = B(you ? "Text" : "TextDim"), FontFamily = Ui, TextAlignment = TextAlignment.Center,
            },
        };
        var text = new TextBlock
        {
            Text = seg.Text, TextWrapping = TextWrapping.Wrap, FontFamily = Ui,
            FontSize = fs, Foreground = B(you ? "Text" : "TextDim"), LineHeight = fs + 8,
        };

        var row = new Grid { Margin = new Thickness(0, 0, 0, spacious ? 14 : 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        row.Children.Add(chip);
        row.Children.Add(text);
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

    // Monotone chip (owner / due) — neutral grey, no colour.
    static Border Tag(string text) => new()
    {
        Background = B("Chip"), CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 2),
        Margin = new Thickness(0, 0, 6, 0),
        Child = new TextBlock { Text = text, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = B("TextDim"), FontFamily = Ui },
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
