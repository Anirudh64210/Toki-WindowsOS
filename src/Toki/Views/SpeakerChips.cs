using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Toki.Transcription;

namespace Toki.Views;

/// <summary>
/// The row of people above a transcript, each one renameable.
///
/// This is the correction half of speaker naming, and it is not optional garnish. Toki names voices by
/// reading the conversation, which is right most of the time and wrong some of the time; a feature that
/// is usually right and impossible to fix is worse than one that never guesses. So: one click, type the
/// name, and it applies to every line that voice said - the same "assign once, applies across the whole
/// transcript" affordance every notetaker that ships automatic naming provides.
///
/// A rename is a plain text update. There is no voice model to correct and nothing to remember for next
/// time, which is precisely why it is instant, and why renaming someone here tells Toki nothing about
/// how to recognise them tomorrow.
/// </summary>
public static class SpeakerChips
{
    /// <summary>
    /// Build the row, or null when there is nothing to show (no diarisation, or a plain two-party call
    /// where "You" and "Them" already say everything).
    /// </summary>
    /// <param name="rename">Called with (oldName, newName). The caller owns storage and re-rendering.</param>
    public static FrameworkElement? Build(IReadOnlyList<TranscriptSegment> transcript, Action<string, string> rename)
    {
        var names = transcript
            .Where(s => !string.IsNullOrWhiteSpace(s.Tag))
            .Select(s => s.Tag!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) return null;

        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var name in names) wrap.Children.Add(Chip(name, rename));
        return wrap;
    }

    static FrameworkElement Chip(string name, Action<string, string> rename)
    {
        // A real Button, not a Border with a mouse handler: this sits inside a read-only RichTextBox,
        // which swallows mouse-up on anything that is not a control. The template picker learned this
        // the hard way and was silently dead for a while.
        var btn = new Button
        {
            Style = (Style)Application.Current.FindResource("PillButton"),
            Padding = new Thickness(10, 4, 10, 5),
            Margin = new Thickness(0, 0, 6, 6),
            FontSize = 11,
            Content = name,
            ToolTip = $"Rename {name} everywhere in this transcript",
        };
        btn.Click += (_, _) => ShowRename(btn, name, rename);
        return btn;
    }

    static void ShowRename(FrameworkElement anchor, string current, Action<string, string> rename)
    {
        var box = new TextBox
        {
            Text = current, Width = 170, FontSize = 12.5, Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 0, 8, 0),
        };
        box.SetResourceReference(Control.ForegroundProperty, "Text");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "Text");

        var field = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = box };
        field.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");
        field.SetResourceReference(Border.BorderBrushProperty, "Hairline");

        var save = new Button
        {
            Style = (Style)Application.Current.FindResource("PillButton"),
            Content = "Rename", FontSize = 11.5, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };
        row.Children.Add(field);
        row.Children.Add(save);

        var shell = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = row };
        shell.SetResourceReference(Border.BackgroundProperty, "Surface");
        shell.SetResourceReference(Border.BorderBrushProperty, "Hairline");

        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, VerticalOffset = 4,
            StaysOpen = false, AllowsTransparency = true, Child = shell, IsOpen = true,
        };

        void Commit()
        {
            var typed = box.Text.Trim();
            popup.IsOpen = false;
            // An unchanged or emptied name is a no-op, never a way to blank every line that voice said.
            if (typed.Length == 0 || typed.Equals(current, StringComparison.Ordinal)) return;
            rename(current, typed);
        }

        save.Click += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; popup.IsOpen = false; }
        };

        box.Focus();
        box.SelectAll();
    }
}
