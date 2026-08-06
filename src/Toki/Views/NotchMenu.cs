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
        var panel = new StackPanel { Margin = new Thickness(5) };
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, HorizontalOffset = -110,
        };

        foreach (var (label, danger, action) in items)
        {
            var btn = new Button
            {
                Content = label, Cursor = System.Windows.Input.Cursors.Hand, Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6, 14, 6),
                FontFamily = (FontFamily)Application.Current.Resources["UiFont"], FontSize = 11.5,
                Background = Brushes.Transparent,
                Foreground = (Brush)Application.Current.Resources[danger ? "Danger" : "Text"],
                Template = ItemTemplate(),
            };
            btn.Click += (_, _) => { popup.IsOpen = false; action(); };
            panel.Children.Add(btn);
        }

        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["SurfaceHi"],
            BorderBrush = (Brush)Application.Current.Resources["Hairline"], BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11), Child = panel, MinWidth = 168,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 22, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black },
        };
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
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.PaddingProperty, new Thickness(0));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.MarginProperty, new Thickness(10, 6, 14, 6));
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            (Brush)Application.Current.Resources["HoverOverlay"], "Bd"));
        t.Triggers.Add(hover);
        return t;
    }
}
