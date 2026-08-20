using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Toki.Views;

/// <summary>
/// Turns something clickable into something you can actually hit. Whatever shows the hand cursor
/// must be a box, and the whole box must respond: a `Transparent` Background is hit-testable,
/// an unset (null) one is not.
/// </summary>
public static class HitTarget
{
    /// <summary>Comfortable minimum for a pointer. The design floors are 32-34px; this is the floor
    /// below which `--hittest` calls a target too small to aim at.</summary>
    public const double MinSide = 26;

    /// <summary>
    /// Wrap content in a transparent, rounded, hand-cursored box that is hit-testable edge to edge.
    /// Attach click and hover handlers to the RETURNED border, never to the content inside it.
    /// </summary>
    public static Border Wrap(FrameworkElement content, double corner = 8,
        double padX = 8, double padY = 4, string? tooltip = null)
    {
        content.VerticalAlignment = VerticalAlignment.Center;
        // The content must not claim the cursor, or it becomes a second smaller "button" inside this one.
        content.ClearValue(FrameworkElement.CursorProperty);

        return new Border
        {
            Child = content,
            // Transparent, NOT unset: a null-background Border is invisible to hit testing.
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(corner),
            Padding = new Thickness(padX, padY, padX, padY),
            MinHeight = MinSide,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tooltip,
        };
    }

    /// <summary>Wrap, and give it the standard hover wash. Returns the border so the caller can add its
    /// own click handler.</summary>
    public static Border Hoverable(FrameworkElement content, double corner = 8,
        double padX = 8, double padY = 4, string? tooltip = null)
    {
        var box = Wrap(content, corner, padX, padY, tooltip);
        box.MouseEnter += (_, _) => box.SetResourceReference(Border.BackgroundProperty, "HoverOverlay");
        box.MouseLeave += (_, _) => box.Background = Brushes.Transparent;
        return box;
    }
}
