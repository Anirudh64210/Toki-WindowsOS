using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Toki.Views;

/// <summary>
/// A tiny dropdown menu that belongs to the notch - a rounded dark card of buttons anchored under an
/// element. Registers with <see cref="NotchPopups"/> so the notch doesn't tuck/dismiss while it's open
/// (the same treatment the model/provider dropdowns get). Reuses Theme.xaml tokens.
/// </summary>
static class NotchMenu
{
    public static void Show(FrameworkElement anchor, IReadOnlyList<(string Label, bool Danger, Action Action)> items)
    {
        // Card is r14, rows r8: inner radius = outer minus the padding between them.
        var panel = new StackPanel { Margin = new Thickness(6) };
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            // The card sits inset inside its own shadow gutter; both offsets must compensate for it.
            HorizontalOffset = -110 - Theme.GlassCard.ShadowGutter,
            VerticalOffset = -Theme.GlassCard.ShadowGutter,
        };

        foreach (var (label, danger, action) in items)
        {
            var btn = new Button
            {
                Content = label, Cursor = System.Windows.Input.Cursors.Hand, Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6, 14, 6),
                FontFamily = (FontFamily)Application.Current.Resources["UiFont"], FontSize = 12,
                Background = Brushes.Transparent,
                Template = ItemTemplate(),
            };
            // Resource REFERENCE, not a captured brush: a menu left open across a theme swap must repaint.
            btn.SetResourceReference(Control.ForegroundProperty, danger ? "Danger" : "Text");
            btn.Click += (_, _) => { popup.IsOpen = false; action(); };
            panel.Children.Add(btn);
        }

        // GlassCard, never a DropShadowEffect: an Effect here puts every label behind an
        // intermediate render surface and gets cropped by the popup's edge.
        var card = new Theme.GlassCard(14) { CardMinWidth = 168, Content = panel };
        popup.Child = card;

        NotchPopups.Register(popup);
        popup.Closed += (_, _) => NotchPopups.Unregister(popup);
        popup.IsOpen = true;
    }

    // A flat button that lights up on hover - same idiom as IconButton, left-aligned text.
    static ControlTemplate ItemTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.Name = "Bd";
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));   // concentric: card 14 - panel margin 6
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.PaddingProperty, new Thickness(0));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.MarginProperty, new Thickness(10, 6, 14, 6));
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        // DynamicResource, not a brush captured at build time: the hover wash must follow theme swaps.
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            new DynamicResourceExtension("HoverOverlay"), "Bd"));
        t.Triggers.Add(hover);
        return t;
    }
}
