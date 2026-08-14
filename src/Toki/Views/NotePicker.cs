using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Toki.Views;

/// <summary>
/// Toki's own dropdown - never a native Windows menu (the Library's sort menu learned that the hard way,
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
    /// <summary>
    /// Show a dropdown under <paramref name="anchor"/>. An item with a NULL action is a group heading
    /// rather than a choice: it draws as a quiet label (with a hairline above it, unless it is first)
    /// and cannot be clicked. That is how one button can hold two different questions - "what is this
    /// line" and "how big is the text" - without the two sets of answers running together.
    /// </summary>
    public static void Show(FrameworkElement anchor, IReadOnlyList<(string Label, Action? Pick)> items,
        string? selected = null)
    {
        if (items.Count == 0) return;

        var stack = new StackPanel();
        FrameworkElement? selectedRow = null;
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            VerticalOffset = 4,
        };

        foreach (var (label, pick) in items)
        {
            if (pick is null)
            {
                if (stack.Children.Count > 0)
                {
                    var rule = new Border { Height = 1, Margin = new Thickness(11, 5, 11, 4) };
                    rule.SetResourceReference(Border.BackgroundProperty, "Hairline");
                    stack.Children.Add(rule);
                }
                var heading = new TextBlock
                {
                    Text = label, FontSize = 10, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(11, 3, 11, 4), IsHitTestVisible = false,
                };
                heading.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
                heading.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
                stack.Children.Add(heading);
                continue;
            }

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
            if (selected is not null && label == selected) selectedRow = button;
            stack.Children.Add(button);
        }

        // A long list (every half hour of a whole day) has to scroll rather than run off the screen.
        // Short lists are unaffected: the ScrollViewer sizes to its content until it hits the cap.
        var scroll = new ScrollViewer
        {
            Content = stack, MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        // Open ON the current value, not at the top. A 48-row time list that always opens at midnight
        // makes the user scroll to where they already are before they can change anything - which is
        // most of why a long dropdown feels worse than a short one.
        if (selectedRow is not null)
            scroll.Loaded += (_, _) => selectedRow.BringIntoView();
        var box = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(5),
            BorderThickness = new Thickness(1), MinWidth = 176, Child = scroll,
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
