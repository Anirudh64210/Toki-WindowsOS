using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Toki.Views;

/// <summary>
/// The 170x50 "Notes ready" pill a finished note tucks away into, with a chevron.
/// PillWindow sets Toki's mood from the state.
/// </summary>
public sealed class NotesReadyView : UserControl
{
    /// <summary>Card size set on the window when the note tucks.</summary>
    public const double CardWidth = 170;
    public const double CardHeight = 50;

    public NotesReadyView()
    {
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        ToolTip = "Your notes are ready - click to read them";

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = "Notes ready", FontSize = 13, FontWeight = FontWeights.SemiBold,
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Resource REFERENCE, not a captured brush: a live theme switch must repaint this.
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextHi");
        row.Children.Add(label);
        var chevron = PillParts.Outline(PillParts.ChevronRight(), "TextMid", 1.7);
        chevron.Margin = new Thickness(6, 1, 0, 0);
        row.Children.Add(chevron);
        Content = row;
    }
}
