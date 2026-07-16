using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Toki.State;
using Toki.Storage;
using Toki.Views;

namespace Toki;

/// <summary>
/// Resizable window showing the whole Library (list) beside the selected note's
/// full content — the "maximise" target and the Home/tray Library button. Manual
/// notes are editable here with an explicit Save; transcripts have a Copy button.
/// </summary>
public partial class LibraryWindow : Window
{
    static LibraryWindow? _open;   // single instance
    long? _selected;

    public static void ShowLibrary(long? focusMeetingId = null)
    {
        if (_open is null)
        {
            _open = new LibraryWindow();
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        _open.Activate();
        _open.Reload(focusMeetingId);
    }

    /// <summary>Open the Library and start a fresh manual note in the editor.</summary>
    public static void ShowNewNote()
    {
        ShowLibrary();
        var id = App.Store.CreateManualNote("Untitled note", "");
        _open!.Reload(id);
    }

    LibraryWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => StyleChrome();
        Loaded += (_, _) => Reload();
    }

    void StyleChrome()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // dark title bar
            if (!App.GhostDisabled)
                SetWindowDisplayAffinity(hwnd, 0x11);               // ghost: not in screen capture
        }
        catch { }
    }

    void Reload(long? focus = null)
    {
        LoadList();
        var toSelect = focus ?? _selected;
        if (toSelect is long id && App.Store.Get(id) is not null) Select(id);
        else if (Cards.Children.Count > 0 && Cards.Children[0] is Border first && first.Tag is long fid) Select(fid);
        else { Detail.Children.Clear(); Detail.Children.Add(Placeholder("Select a meeting or note, or make a new one.")); }
    }

    void LoadList()
    {
        Cards.Children.Clear();
        IReadOnlyList<MeetingSummary> items;
        try { items = App.Store.List(string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text); }
        catch (Exception ex) { App.Log($"library window load: {ex.Message}"); items = []; }

        CountLabel.Text = $"{items.Count} · all on this PC";
        foreach (var m in items) Cards.Children.Add(Card(m));
        if (items.Count == 0)
            Cards.Children.Add(Placeholder(string.IsNullOrWhiteSpace(SearchBox.Text) ? "Nothing yet." : "No matches."));
    }

    Border Card(MeetingSummary m)
    {
        var selected = _selected == m.Id;
        var stack = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new TextBlock { Text = m.Kind == MeetingKind.Manual ? "✎ Note" : (m.App ?? "Meeting"), Style = St("Dim"), FontSize = 9.5 });
        top.Children.Add(new TextBlock { Text = "  ·  " + m.StartedAt.ToString("MMM d"), Style = St("Dim"), FontSize = 9.5 });
        stack.Children.Add(top);
        stack.Children.Add(new TextBlock { Text = m.Title, Style = St("Body"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        if (!string.IsNullOrWhiteSpace(m.Tldr))
            stack.Children.Add(new TextBlock { Text = m.Tldr, Style = St("Dim"), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });

        var card = new Border
        {
            Background = (Brush)FindResource(selected ? "Chip" : "SurfaceHi"),
            CornerRadius = new CornerRadius(11), Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 2, 8), Cursor = Cursors.Hand, Child = stack, Tag = m.Id,
        };
        card.MouseLeftButtonUp += (_, _) => Select(m.Id);
        return card;
    }

    void Select(long id)
    {
        _selected = id;
        LoadList(); // refresh selection highlight
        var m = App.Store.Get(id);
        if (m is null) return;
        if (m.Kind == MeetingKind.Manual) RenderManual(m);
        else RenderMeeting(m);
        DetailScroll.ScrollToTop();
        FadeIn(Detail);
    }

    // Soft cross-fade so switching notes feels smooth, not a hard swap.
    static void FadeIn(UIElement e)
    {
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    void OnBack(object sender, RoutedEventArgs e)
    {
        Close();
        App.State.ForceForDemo(PillState.Home); // bring the pill back
    }

    void RenderMeeting(MeetingRecord m)
    {
        Detail.Children.Clear();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock { Text = m.Title, Style = St("Body"), FontSize = 20, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        titleStack.Children.Add(new TextBlock { Text = Meta(m), Style = St("Dim"), FontSize = 11, Margin = new Thickness(0, 3, 0, 0) });
        header.Children.Add(titleStack);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        if (m.Transcript.Count > 0) actions.Children.Add(SmallButton("Copy transcript", () => CopyTranscript(m)));
        actions.Children.Add(SmallButton("Delete", () => Delete(m.Id), danger: true));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        Detail.Children.Add(header);

        var body = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        NoteRenderer.Meeting(body, m, spacious: true);
        Detail.Children.Add(body);
    }

    void RenderManual(MeetingRecord m)
    {
        Detail.Children.Clear();

        var titleBox = new TextBox { Text = m.Title, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = (Brush)FindResource("Text"), CaretBrush = (Brush)FindResource("Text"), FontSize = 20, FontWeight = FontWeights.Bold, FontFamily = (FontFamily)FindResource("UiFont") };
        Detail.Children.Add(titleBox);
        Detail.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("SurfaceHi"), Margin = new Thickness(0, 8, 0, 0) });

        var bodyBox = new TextBox
        {
            Text = m.Body ?? "", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD5, 0xD5, 0xD3)), CaretBrush = (Brush)FindResource("Text"),
            FontSize = 13, FontFamily = (FontFamily)FindResource("UiFont"), AcceptsReturn = true, AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 320, Margin = new Thickness(0, 12, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        Detail.Children.Add(bodyBox);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var saveBtn = SmallButton("Save", () => { }, primary: true);
        var status = new TextBlock { Style = St("Dim"), FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        ((Button)saveBtn).Click += (_, _) =>
        {
            var title = string.IsNullOrWhiteSpace(titleBox.Text) ? "Untitled note" : titleBox.Text.Trim();
            App.Store.UpdateTitleAndBody(m.Id, title, bodyBox.Text);
            status.Text = "Saved ✓";
            LoadList();
        };
        bar.Children.Add(saveBtn);
        bar.Children.Add(SmallButton("Delete", () => Delete(m.Id), danger: true));
        bar.Children.Add(status);
        Detail.Children.Add(bar);
    }

    void OnNewNote(object sender, RoutedEventArgs e)
    {
        var id = App.Store.CreateManualNote("Untitled note", "");
        Reload(id);
    }

    void OnSearch(object sender, TextChangedEventArgs e) => LoadList();

    void CopyTranscript(MeetingRecord m)
    {
        try { Clipboard.SetText(NoteRenderer.TranscriptText(m)); } catch (Exception ex) { App.Log($"copy failed: {ex.Message}"); }
    }

    void Delete(long id)
    {
        var m = App.Store.Get(id);
        if (MessageBox.Show(this, $"Delete \"{m?.Title}\"? This can't be undone.", "Delete",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        App.Store.Delete(id);
        if (_selected == id) _selected = null;
        Reload();
    }

    // ---- ui helpers ----

    Style St(string key) => (Style)FindResource(key);

    FrameworkElement SmallButton(string label, Action onClick, bool primary = false, bool danger = false)
    {
        var b = new Button
        {
            Content = label, Style = (Style)FindResource(primary ? "CoralButton" : "PillButton"),
            Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(13, 6, 13, 6), FontSize = 11.5,
        };
        if (danger) b.Foreground = (Brush)FindResource("TextDim");
        b.Click += (_, _) => onClick();
        return b;
    }

    TextBlock Placeholder(string text) => new()
    {
        Text = text, Style = St("Dim"), FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 24, 0, 0),
    };

    static string Meta(MeetingRecord m)
    {
        var parts = new List<string>();
        if (m.DurationMs > 0) parts.Add($"{Math.Max(1, m.DurationMs / 60000)} min");
        parts.Add(m.StartedAt.ToString("dddd, MMM d · h:mm tt"));
        if (!string.IsNullOrWhiteSpace(m.App)) parts.Add(m.App!);
        return string.Join("  ·  ", parts);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
