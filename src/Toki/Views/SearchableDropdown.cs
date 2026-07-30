using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Toki.Views;

/// <summary>
/// A compact dropdown: one 34px pill row showing the current pick, which opens a floating popup
/// with a type-to-filter search box (shown only when the list is long) and a scrollable list.
/// The popup is its own top-level window, so a long model list never grows the notch. Built in
/// code so it drops into any host and styles itself from app resources.
/// </summary>
public sealed class SearchableDropdown : Border
{
    readonly TextBlock _valueText = new();
    readonly Popup _popup = new();
    readonly TextBox _search = new();
    readonly StackPanel _rows = new();
    readonly Border _searchBox;
    List<string> _items = new();
    string? _selected;
    string _placeholder = "";

    // Show the search box only once the list is long enough to warrant it.
    const int SearchThreshold = 8;

    public event EventHandler? Changed;

    public SearchableDropdown()
    {
        Background = Res<Brush>("SurfaceHi");
        CornerRadius = new CornerRadius(9);
        Height = 34;
        Padding = new Thickness(11, 0, 9, 0);
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _valueText.VerticalAlignment = VerticalAlignment.Center;
        _valueText.TextTrimming = TextTrimming.CharacterEllipsis;
        _valueText.FontFamily = Res<FontFamily>("UiFont");
        _valueText.FontSize = 11.5;
        _valueText.Foreground = Res<Brush>("Text");

        var chevron = new TextBlock
        {
            Text = "▾", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            FontSize = 10, Foreground = Res<Brush>("TextDim"),
        };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(_valueText);
        grid.Children.Add(chevron);
        Child = grid;

        // ── popup ──
        _searchBox = new Border
        {
            Background = Res<Brush>("Surface"), CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 0, 9, 0), Margin = new Thickness(0, 0, 0, 6), Height = 32,
        };
        _search.Background = Brushes.Transparent;
        _search.BorderThickness = new Thickness(0);
        _search.Foreground = Res<Brush>("Text");
        _search.CaretBrush = Res<Brush>("Text");
        _search.FontFamily = Res<FontFamily>("UiFont");
        _search.FontSize = 11.5;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => RebuildRows(_search.Text);
        _searchBox.Child = _search;

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 220, Content = _rows,
        };
        var stack = new StackPanel();
        stack.Children.Add(_searchBox);
        stack.Children.Add(scroll);

        var box = new Border
        {
            Background = Res<Brush>("SurfaceHi"), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6), Child = stack,
            Effect = null,
        };
        _popup.Child = box;
        _popup.PlacementTarget = this;
        _popup.Placement = PlacementMode.Bottom;
        _popup.StaysOpen = false;
        _popup.AllowsTransparency = true;
        _popup.PopupAnimation = PopupAnimation.Fade;
        _popup.VerticalOffset = 4;

        MouseLeftButtonUp += (_, _) => Toggle();
        _popup.Opened += (_, _) =>
        {
            box.MinWidth = ActualWidth;
            if (_searchBox.Visibility == Visibility.Visible)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _search.Focus()));
        };
    }

    static T Res<T>(string key) => (T)Application.Current.Resources[key];

    public string? Selected => _selected;

    /// <summary>Replace the options and show <paramref name="select"/> (or the placeholder).</summary>
    public void SetItems(IEnumerable<string> items, string? select, string placeholder = "")
    {
        _items = items?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new();
        _placeholder = placeholder;
        _selected = _items.FirstOrDefault(x => string.Equals(x, select, StringComparison.OrdinalIgnoreCase));
        _valueText.Text = _selected ?? (select ?? placeholder);
        _valueText.Foreground = Res<Brush>(_selected is null && string.IsNullOrEmpty(select) ? "TextDim" : "Text");
    }

    void Toggle()
    {
        if (_popup.IsOpen) { _popup.IsOpen = false; return; }
        if (_items.Count == 0) return;
        _searchBox.Visibility = _items.Count > SearchThreshold ? Visibility.Visible : Visibility.Collapsed;
        _search.Text = "";
        RebuildRows("");
        _popup.IsOpen = true;
    }

    void RebuildRows(string filter)
    {
        _rows.Children.Clear();
        var q = filter?.Trim() ?? "";
        var matches = _items.Where(m => q.Length == 0 || m.Contains(q, StringComparison.OrdinalIgnoreCase));
        foreach (var item in matches)
            _rows.Children.Add(BuildRow(item));
    }

    Border BuildRow(string item)
    {
        var isSel = string.Equals(item, _selected, StringComparison.OrdinalIgnoreCase);
        var text = new TextBlock
        {
            Text = item, FontFamily = Res<FontFamily>("UiFont"), FontSize = 11.5,
            FontWeight = isSel ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Res<Brush>("Text"), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var row = new Border
        {
            Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(7),
            Background = isSel ? Res<Brush>("Chip") : Brushes.Transparent,
            Cursor = Cursors.Hand, Child = text,
        };
        row.MouseEnter += (_, _) => { if (!isSel) row.Background = Res<Brush>("Chip"); };
        row.MouseLeave += (_, _) => { if (!isSel) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; Choose(item); };
        return row;
    }

    void Choose(string item)
    {
        _popup.IsOpen = false;
        if (string.Equals(item, _selected, StringComparison.OrdinalIgnoreCase)) return;
        _selected = item;
        _valueText.Text = item;
        _valueText.Foreground = Res<Brush>("Text");
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
