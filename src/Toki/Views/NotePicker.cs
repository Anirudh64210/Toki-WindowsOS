using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Toki.Views;

/// <summary>
/// Toki's own dropdown — never a native Windows menu (the Library's sort menu learned that the hard way,
/// and Windows chrome on a notch that's meant to feel like one object is instantly wrong).
///
/// Two things here are load-bearing, both paid for in bugs:
///   • Each row is a real <see cref="Button"/>, not a Border with a MouseLeftButtonUp handler. In a
///     <c>StaysOpen=false</c> popup the mouse-up is swallowed as the popup closes, and every item is
///     silently dead. Click fires; mouse-up doesn't.
///   • The open popup registers with <see cref="NotchPopups"/>, so the notch counts it as "still being
///     used" and doesn't retract out from under the menu the moment the pointer leaves the card.
/// </summary>
public static class NotePicker
{
    public static void Show(FrameworkElement anchor, IReadOnlyList<(string Label, Action Pick)> items,
        string? selected = null)
    {
        if (items.Count == 0) return;

        var stack = new StackPanel();
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            VerticalOffset = 4,
        };

        foreach (var (label, pick) in items)
        {
            var text = new TextBlock
            {
                Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260,
            };
            text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text");

            FrameworkElement content = text;
            if (selected is not null)
            {
                var isSelected = label == selected;
                if (isSelected) text.FontWeight = FontWeights.SemiBold;
                var tick = new TextBlock
                {
                    Text = "", FontSize = 10, Width = 16, VerticalAlignment = VerticalAlignment.Center,
                    Opacity = isSelected ? 1 : 0,   // reserve the column so every label still lines up
                };
                tick.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
                tick.SetResourceReference(TextBlock.ForegroundProperty, "Pink");
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(tick);
                row.Children.Add(text);
                content = row;
            }

            var surface = new Border
            {
                CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 8, 20, 8),
                Background = Brushes.Transparent, Child = content,
            };
            var button = new Button
            {
                Content = surface, Cursor = Cursors.Hand, Focusable = false,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Template = BareTemplate(),
            };
            button.MouseEnter += (_, _) => surface.SetResourceReference(Border.BackgroundProperty, "Chip");
            button.MouseLeave += (_, _) => surface.Background = Brushes.Transparent;
            var act = pick;
            button.Click += (_, _) => { popup.IsOpen = false; act(); };
            stack.Children.Add(button);
        }

        var box = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(5),
            BorderThickness = new Thickness(1), MinWidth = 176, Child = stack,
        };
        box.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");
        box.SetResourceReference(Border.BorderBrushProperty, "Hairline");
        popup.Child = box;

        NotchPopups.Register(popup);
        popup.Closed += (_, _) => NotchPopups.Unregister(popup);
        popup.IsOpen = true;
    }

    /// <summary>A button that renders exactly its content and no chrome, so our themed rows show through.</summary>
    static ControlTemplate BareTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        t.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        return t;
    }
}
