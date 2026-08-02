using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Toki.State;
using Toki.Storage;

namespace Toki.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        Loaded += (_, _) => { SearchBox.Text = ""; Reload(); };
        // Cards are built in code (FindResource brushes) → rebuild them on a theme swap so they recolour.
        Toki.Theme.ThemeManager.ThemeChanged += () => Dispatcher.BeginInvoke(() => { if (IsLoaded) Reload(); });
    }

    void Reload()
    {
        Cards.Children.Clear();
        IReadOnlyList<MeetingSummary> items;
        try { items = App.Store.List(string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text); }
        catch (Exception ex) { App.Log($"library load failed: {ex.Message}"); items = []; }

        CountLabel.Text = items.Count == 0 ? "nothing yet · all on this PC" : $"{items.Count} · all on this PC";
        if (items.Count == 0)
        {
            Cards.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Dim"), FontSize = 11, Margin = new Thickness(2, 16, 0, 0),
                TextAlignment = TextAlignment.Center,
                Text = string.IsNullOrWhiteSpace(SearchBox.Text)
                    ? "No meetings yet. Record one, or make a new note."
                    : "No matches.",
            });
            return;
        }
        foreach (var m in items) Cards.Children.Add(Card(m));
    }

    Border Card(MeetingSummary m)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var date = new StackPanel();
        date.Children.Add(new TextBlock { Text = m.StartedAt.Day.ToString(), Style = (Style)FindResource("Body"), FontSize = 15, FontWeight = FontWeights.Bold });
        date.Children.Add(new TextBlock { Text = m.StartedAt.ToString("MMM").ToUpperInvariant(), Style = (Style)FindResource("Dim"), FontSize = 8.5 });
        grid.Children.Add(date);

        var body = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        body.Children.Add(new TextBlock { Text = m.Title, Style = (Style)FindResource("Body"), FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var kindWord = m.Kind == MeetingKind.Manual ? "Note" : (m.App ?? "Meeting");
        body.Children.Add(new TextBlock { Text = $"{kindWord} · {Ago(m.StartedAt)}", Style = (Style)FindResource("Dim"), FontSize = 9.5 });
        if (!string.IsNullOrWhiteSpace(m.Tldr))
            body.Children.Add(new TextBlock { Text = m.Tldr, Style = (Style)FindResource("Dim"), FontSize = 10.5, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, MaxHeight = 32, TextTrimming = TextTrimming.CharacterEllipsis });

        var tags = new StackPanel { Orientation = Orientation.Horizontal };
        if (m.OpenActions > 0) tags.Children.Add(Tag($"{m.OpenActions} open", "AmberDark", "Amber"));
        if (m.PeopleCount > 0) tags.Children.Add(Tag($"{m.PeopleCount} {(m.PeopleCount == 1 ? "voice" : "people")}", "Chip", "TextDim"));
        if (tags.Children.Count > 0) body.Children.Add(tags);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var del = new Button { Style = (Style)FindResource("IconButton"), Content = "🗑", FontSize = 11, VerticalAlignment = VerticalAlignment.Top, Opacity = 0.5 };
        del.Click += (_, e) => { e.Handled = true; Delete(m); };
        Grid.SetColumn(del, 2);
        grid.Children.Add(del);

        var card = new Border
        {
            Background = (Brush)FindResource("SurfaceHi"), CornerRadius = new CornerRadius(13),
            Padding = new Thickness(12, 10, 8, 10), Margin = new Thickness(0, 8, 0, 0), Cursor = Cursors.Hand,
            Child = grid,
        };
        card.MouseLeftButtonUp += (_, _) => Open(m);
        return card;
    }

    Border Tag(string text, string bgKey, string fgKey) => new()
    {
        Background = (Brush)FindResource(bgKey), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 6, 6, 0),
        Child = new TextBlock { Text = text, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource(fgKey), FontFamily = (FontFamily)FindResource("UiFont") },
    };

    static string Ago(DateTimeOffset when)
    {
        var d = DateTimeOffset.Now - when;
        if (d.TotalMinutes < 2) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 2) return "yesterday";
        return $"{(int)d.TotalDays} days ago";
    }

    void Open(MeetingSummary m)
    {
        try
        {
            var full = App.Store.Get(m.Id);
            if (full is null) return;
            if (full.Kind == MeetingKind.Manual) { App.OpenManualNote(full); return; }
            App.CurrentMeeting = full;
            App.State.TryGo(PillState.Notes);
        }
        catch (Exception ex) { App.Log($"open meeting failed: {ex.Message}"); }
    }

    void Delete(MeetingSummary m)
    {
        if (!ConfirmDialog.Show(null, "Delete this?",
                $"“{m.Title}” and its audio will be removed for good. This can't be undone.")) return;
        try { App.Store.Delete(m.Id); Toki.LibraryWindow.MarkDirty(); } catch (Exception ex) { App.Log($"delete failed: {ex.Message}"); }
        Reload();
    }

    void OnSearchChanged(object sender, TextChangedEventArgs e) => Reload();

    void OnNewNote(object sender, RoutedEventArgs e) => App.NewManualNote();

    void OnBack(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Home);
}
