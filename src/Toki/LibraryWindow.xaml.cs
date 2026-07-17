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

    public static void ShowLibrary(long? focusMeetingId = null, Rect? fromRect = null)
    {
        var justOpened = _open is null;
        if (_open is null)
        {
            _open = new LibraryWindow();
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        _open.Activate();
        _open.Reload(focusMeetingId);
        if (justOpened) _open.PlayOpen(fromRect);
    }

    /// <summary>
    /// Buttery open: the window's content literally grows out of the pill's on-screen
    /// rectangle to fill the window (a GPU scale+translate on the laid-out content — no
    /// reflow), with a soft fade. Reads as the pill extending into a window rather than a
    /// hard pop. Falls back to a gentle bottom-anchored grow if we don't have the rect.
    /// </summary>
    public void PlayOpen(Rect? fromRect)
    {
        UpdateLayout();
        double ww = ActualWidth, wh = ActualHeight;
        if (ww <= 0 || wh <= 0) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(300));

        double sx0, sy0, tx0, ty0;
        if (fromRect is Rect p && p.Width > 0 && p.Height > 0)
        {
            sx0 = p.Width / ww; sy0 = p.Height / wh;
            tx0 = p.Left - Left; ty0 = p.Top - Top;   // pill's top-left relative to the window
        }
        else { sx0 = 0.86; sy0 = 0.7; tx0 = ww * 0.07; ty0 = wh * 0.15; } // gentle centred-ish grow

        var scale = new ScaleTransform(sx0, sy0);
        var trans = new TranslateTransform(tx0, ty0);
        var grp = new TransformGroup();
        grp.Children.Add(scale); grp.Children.Add(trans);
        RootGrid.RenderTransform = grp;

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sx0, 1, dur) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(sy0, 1, dur) { EasingFunction = ease });
        trans.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(tx0, 0, dur) { EasingFunction = ease });
        trans.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(ty0, 0, dur) { EasingFunction = ease });
        RootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, new Duration(TimeSpan.FromMilliseconds(200))) { EasingFunction = ease });
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

        // Left: a calendar-style date badge (day over month), like the design.
        var badge = new Border
        {
            Background = (Brush)FindResource("Chip"), CornerRadius = new CornerRadius(9),
            Width = 42, Height = 46, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 11, 0),
        };
        var badgeStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        badgeStack.Children.Add(new TextBlock { Text = m.StartedAt.Day.ToString(), Style = St("Body"), FontSize = 17, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        badgeStack.Children.Add(new TextBlock { Text = m.StartedAt.ToString("MMM").ToUpperInvariant(), Style = St("Dim"), FontSize = 8, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) });
        badge.Child = badgeStack;

        // Right: title, meta line, snippet, and count chips.
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = m.Title, Style = St("Body"), FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        content.Children.Add(new TextBlock { Text = CardMeta(m), Style = St("Dim"), FontSize = 9.5, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(m.Tldr))
            content.Children.Add(new TextBlock { Text = m.Tldr, Style = St("Dim"), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 0) });

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        if (m.OpenActions > 0) chips.Children.Add(Chip($"{m.OpenActions} open", "Amber", "AmberDark"));
        if (m.PeopleCount > 0) chips.Children.Add(Chip($"{m.PeopleCount} {(m.PeopleCount == 1 ? "person" : "people")}", "TextDim", "Chip"));
        if (chips.Children.Count > 0) content.Children.Add(chips);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(badge);
        Grid.SetColumn(content, 1);
        row.Children.Add(content);

        var card = new Border
        {
            Background = (Brush)FindResource(selected ? "Chip" : "SurfaceHi"),
            BorderBrush = selected ? (Brush)FindResource("Coral") : Brushes.Transparent,
            BorderThickness = new Thickness(selected ? 1 : 0),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 11, 11, 12),
            Margin = new Thickness(0, 0, 2, 8), Cursor = Cursors.Hand, Child = row, Tag = m.Id,
        };
        card.PreviewMouseLeftButtonDown += (_, e) => OnCardDown(card, e);
        card.PreviewMouseMove += (_, e) => OnCardMove(e);
        card.PreviewMouseLeftButtonUp += (_, e) => OnCardUp(card, m.Id);
        return card;
    }

    // ---- drag-to-reorder (iPhone-home-screen style: pick up, others slide to make space,
    //      release snaps cleanly into the nearest slot — a card never sits between two) ----
    Border? _drag;
    int _dragFrom;
    double _dragStartY;
    bool _dragMoved;
    readonly List<Border> _cards = new();
    readonly List<double> _cardTops = new();
    readonly List<double> _cardHeights = new();

    void OnCardDown(Border card, MouseButtonEventArgs e)
    {
        _drag = card;
        _dragMoved = false;
        _dragStartY = e.GetPosition(Cards).Y;

        // Snapshot cards + their tops/heights so we can compute slots + shifts during the drag.
        Cards.UpdateLayout();
        _cards.Clear(); _cardTops.Clear(); _cardHeights.Clear();
        double top = 0;
        foreach (var child in Cards.Children)
        {
            if (child is not Border b) continue;
            var t = Translate(b);
            t.BeginAnimation(TranslateTransform.YProperty, null);
            t.Y = 0;
            var h = b.ActualHeight + 8; // + bottom margin
            _cards.Add(b); _cardTops.Add(top); _cardHeights.Add(h); top += h;
        }
        _dragFrom = _cards.IndexOf(card);
        card.CaptureMouse();
    }

    void OnCardMove(MouseEventArgs e)
    {
        if (_drag is null || e.LeftButton != MouseButtonState.Pressed || _dragFrom < 0) return;
        var dy = e.GetPosition(Cards).Y - _dragStartY;
        if (!_dragMoved && Math.Abs(dy) < 4) return;
        if (!_dragMoved) { _dragMoved = true; _drag.Opacity = 0.9; Panel.SetZIndex(_drag, 30); }

        Translate(_drag).Y = dy;

        var ins = InsertionIndex(_cardTops[_dragFrom] + _cardHeights[_dragFrom] / 2 + dy);
        var dh = _cardHeights[_dragFrom];
        for (var i = 0; i < _cards.Count; i++)
        {
            if (i == _dragFrom) continue;
            var oi = i < _dragFrom ? i : i - 1;                       // its index among the "others"
            var shift = (i > _dragFrom ? -dh : 0) + (oi >= ins ? dh : 0);
            SlideTo(_cards[i], shift);
        }
    }

    void OnCardUp(Border card, long id)
    {
        if (_drag is null) return;
        card.ReleaseMouseCapture();
        if (!_dragMoved) { _drag = null; Select(id); return; } // a click, not a drag

        var center = _cardTops[_dragFrom] + _cardHeights[_dragFrom] / 2 + Translate(card).Y;
        var ins = InsertionIndex(center);

        // Settle: glide the dragged card into its final slot, THEN commit + rebuild. This is
        // what stops it sitting awkwardly between two rows on release.
        double slotTop = 0; var counted = 0;
        for (var i = 0; i < _cards.Count && counted < ins; i++) { if (i == _dragFrom) continue; slotTop += _cardHeights[i]; counted++; }
        var target = slotTop - _cardTops[_dragFrom];

        var from = _dragFrom;
        _drag = null;
        var settle = new DoubleAnimation(target, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        settle.Completed += (_, _) =>
        {
            card.Opacity = 1; Panel.SetZIndex(card, 0);
            CommitReorder(from, ins);
        };
        Translate(card).BeginAnimation(TranslateTransform.YProperty, settle);
    }

    // Where the dragged card would insert among the OTHER cards (0..n-1), by comparing its
    // centre to the others' closed-up mid-points (dragged card excluded).
    int InsertionIndex(double centerY)
    {
        var idx = 0; double top = 0;
        for (var i = 0; i < _cards.Count; i++)
        {
            if (i == _dragFrom) continue;
            var h = _cardHeights[i];
            if (centerY > top + h / 2) idx++;
            top += h;
        }
        return idx;
    }

    void CommitReorder(int from, int ins)
    {
        var ids = new List<long>();
        foreach (var b in _cards) if (b.Tag is long id) ids.Add(id);
        if (from < 0 || from >= ids.Count) { LoadList(); return; }
        var moved = ids[from];
        ids.RemoveAt(from);
        ids.Insert(Math.Clamp(ins, 0, ids.Count), moved);
        try { App.Store.Reorder(ids); } catch (Exception ex) { App.Log($"reorder persist failed: {ex.Message}"); }
        LoadList(); // rebuild in the new order (always resets every drag transform)
    }

    static TranslateTransform Translate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform t) return t;
        var nt = new TranslateTransform();
        el.RenderTransform = nt;
        return nt;
    }

    static void SlideTo(UIElement el, double y) =>
        Translate(el).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(y, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    Border Chip(string text, string fgKey, string bgKey) => new()
    {
        Background = (Brush)FindResource(bgKey), CornerRadius = new CornerRadius(5),
        Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 6, 0),
        Child = new TextBlock { Text = text, Foreground = (Brush)FindResource(fgKey), FontSize = 9, FontWeight = FontWeights.SemiBold },
    };

    static string CardMeta(MeetingSummary m)
    {
        var parts = new List<string> { m.Kind == MeetingKind.Manual ? "Note" : (m.App ?? "Meeting") };
        if (m.DurationMs > 0) parts.Add($"{Math.Max(1, m.DurationMs / 60000)} min");
        parts.Add(TimeAgo(m.StartedAt));
        return string.Join(" · ", parts);
    }

    static string TimeAgo(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalMinutes < 60) return "just now";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 2) return "yesterday";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} days ago";
        return t.ToString("MMM d");
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
