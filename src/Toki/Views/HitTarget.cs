using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Toki.Views;

/// <summary>
/// Turns something clickable into something you can actually hit.
///
/// **The bug this exists to prevent.** A `TextBlock` with a click handler paints only its glyphs, so only
/// the glyphs are hit-testable - the gaps between letters and the space above and below them are dead.
/// Put it inside a parent that draws a hover fill and you get the worst possible result: the whole box
/// lights up on hover while only the letters respond to a click. The highlight becomes a lie about where
/// the button is, and the user ends up aiming at text. Reported by the owner (2026-08-12) and confirmed
/// by `Toki.exe --hittest`, which found it on every Calendar action link.
///
/// **The rule: whatever shows the hand cursor must be a box, and the whole box must respond.** A
/// `Background` of `Transparent` is hit-testable; a `Background` left unset is `null` and is NOT. That
/// one-word difference is the entire bug, and it is invisible in a screenshot.
///
/// Kept as one shared helper rather than repeated per site, because a design revamp is coming and one
/// definition is one edit.
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
        // The content must not claim the cursor for itself, or it becomes a second, smaller "button"
        // sitting inside this one - which is exactly the shape of the bug being fixed.
        content.ClearValue(FrameworkElement.CursorProperty);

        return new Border
        {
            Child = content,
            // Transparent, NOT unset. Unset is null, and a null-background Border is invisible to hit
            // testing, which would leave only the glyphs clickable and defeat the whole point.
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
