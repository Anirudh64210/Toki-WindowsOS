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
        // Note content is built in code (NoteRenderer/FindResource) → re-render on a theme swap.
        Toki.Theme.ThemeManager.ThemeChanged += () => Dispatcher.BeginInvoke(() => { if (IsLoaded) Populate(); });
        // The speakers land a few seconds after the note does. Re-read the meeting so the loaders are
        // replaced by real names in place, rather than the note quietly differing next time it is opened.
        App.SpeakersResolved += id => Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded || App.CurrentMeeting?.Id != id) return;
            if (App.Store.Get(id) is { } fresh) App.CurrentMeeting = fresh;
            Populate();
        });
    }

    void Populate()
    {
        Body.Children.Clear();
        var meeting = App.CurrentMeeting;

        // Keep-or-discard only makes sense for a real saved meeting.
        ActionBar.Visibility = meeting is not null ? Visibility.Visible : Visibility.Collapsed;

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

    // ONE FlowDocument in a read-only RichTextBox so a drag-select spans the whole note;
    // embedded so the page's ScrollViewer stays the only scroll.
    void RenderNotes(MeetingRecord m)
    {
        var box = NoteRenderer.MeetingDocument(m, spacious: false,
            resolving: m.Id > 0 && App.IsResolvingSpeakers(m.Id), scrollHost: Scroll);
        // A scroll-disabled RichTextBox still eats the wheel; hand it to the page's scroller.
        box.PreviewMouseWheel += (_, e) => { e.Handled = true; Toki.Controls.SmoothScroll.Nudge(Scroll, e.Delta); };
        Body.Children.Add(box);
    }

    void OnCopy(object sender, RoutedEventArgs e)
    {
        if (App.CurrentMeeting is { } m && m.Transcript.Count > 0)
        {
            try
            {
                Clipboard.SetText(NoteRenderer.TranscriptText(m));
                // Feedback checkmark reverts after a moment; a permanent one reads as consumed.
                var prior = CopyBtn.Content;
                CopyBtn.Content = "✓";
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                t.Tick += (_, _) => { t.Stop(); CopyBtn.Content = prior; };
                t.Start();
            }
            catch { }
        }
    }

    void OnMaximise(object sender, RoutedEventArgs e)
    {
        // Consistent with Home → Library, and grow from the notch: capture the pill's spot first.
        var from = App.PillRect();
        App.State.TryGo(PillState.Hidden);
        LibraryWindow.ShowLibrary(App.CurrentMeeting?.Id, fromRect: from);
    }

    static string Meta(MeetingRecord m) => TimeText.CompactMeta(m);

    static string Ago(DateTimeOffset when) => TimeText.Ago(when);

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
                Text = seg.Label.ToUpperInvariant(), FontSize = 8.5, FontWeight = FontWeights.Bold,
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

    // The meeting is auto-saved when it's transcribed, so "Save to Library" just keeps it and closes.
    void OnSaveToLibrary(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Hidden);

    // "Delete" discards it entirely (DB + notes file + audio) - the user decided not to keep it.
    void OnDelete(object sender, RoutedEventArgs e)
    {
        App.DiscardMeeting(App.CurrentMeeting);
        App.State.TryGo(PillState.Hidden);
    }
}
