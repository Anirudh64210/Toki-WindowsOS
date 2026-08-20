using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Toki.Views;

/// <summary>
/// Themed borderless confirmation dialog with Cancel / confirm buttons.
/// Returns true only if the user confirms. Esc = cancel, Enter = confirm.
/// </summary>
public static class ConfirmDialog
{
    public static bool Show(Window? owner, string title, string message,
                            string confirmLabel = "Delete", bool danger = true)
    {
        Brush B(string key) => (Brush)Application.Current.FindResource(key);
        var ui = (FontFamily)Application.Current.FindResource("UiFont");

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            Width = 340,
            ShowInTaskbar = false,
            ShowActivated = true,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Topmost = owner is null,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };

        var result = false;
        void Close(bool ok) { result = ok; win.DialogResult = ok; win.Close(); }

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title, FontFamily = ui, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = B("Text"), TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new TextBlock
        {
            Text = message, FontFamily = ui, FontSize = 12, Foreground = B("TextDim"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), LineHeight = 17,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        var cancel = new Button
        {
            Content = "Cancel", Style = (Style)Application.Current.FindResource("PillButton"),
            Background = Brushes.Transparent, Foreground = B("Text"), Padding = new Thickness(14, 7, 14, 7),
        };
        cancel.Click += (_, _) => Close(false);
        var confirm = new Button
        {
            Content = confirmLabel, Style = (Style)Application.Current.FindResource("PillButton"),
            Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0),
            // Destructive actions carry the brand coral; plain actions stay monotone.
            Foreground = danger ? B("Coral") : B("Text"),
        };
        confirm.Click += (_, _) => Close(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        stack.Children.Add(buttons);

        // The window is transparent, so the card's shadow gutter is simply room inside it;
        // a centred dialog has no anchor to compensate for.
        win.Content = new Theme.GlassCard(16)
        {
            CardPadding = new Thickness(20, 18, 20, 18), Content = stack,
        };

        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(false);
            else if (e.Key == Key.Enter) Close(true);
        };

        win.ShowDialog();
        return result;
    }
}
