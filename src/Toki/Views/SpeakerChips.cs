using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Toki.Transcription;

namespace Toki.Views;

/// <summary>
/// The row of people above a transcript, each one renameable. A rename applies to every line that
/// voice said, as a plain text update: there is no voice model to correct and nothing remembered.
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
        // THE LOCAL SPEAKER IS NOT A GUESS. Their stream is the microphone, and Toki already has
        // their name from Settings, so it is shown as a plain label. Offering to rename it was
        // redundant and actively confusing: a two-person call showed two identical-looking rename
        // chips, one of which was the user, and renaming the wrong one pushed their own lines back
        // to a "Speaker N" label. Only voices Toki had to work out are renameable.
        var mine = transcript.Where(s => s.Speaker == Speaker.You)
            .Select(s => s.Tag).Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var others = transcript
            .Where(s => s.Speaker != Speaker.You && !string.IsNullOrWhiteSpace(s.Tag))
            .Select(s => s.Tag!)
            .Where(t => !mine.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var myName = Views.NoteRenderer.SpeakerName(
            transcript.FirstOrDefault(s => s.Speaker == Speaker.You)
            ?? new TranscriptSegment(Speaker.You, 0, 0, ""));
        var hasMine = transcript.Any(s => s.Speaker == Speaker.You);
        // The other party of a call whose "them" lines diarisation never tagged (a plain two-person
        // call, or diarisation that did not run): still offer to name them, applied to every untagged
        // "them" line. Without this a two-party call showed only the "You" chip and the other person
        // could not be named at all.
        var hasUntaggedOther = transcript.Any(s => s.Speaker != Speaker.You && string.IsNullOrWhiteSpace(s.Tag));
        if (others.Count == 0 && !hasMine && !hasUntaggedOther) return null;

        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        if (hasMine) wrap.Children.Add(Chip(myName, rename, quiet: true));
        foreach (var name in others) wrap.Children.Add(Chip(name, rename));
        if (hasUntaggedOther) wrap.Children.Add(Chip("Them", rename));
        return wrap;
    }

    /// <summary>
    /// One renameable speaker tag. A real Button (not a Border+handler): it sits inside a read-only
    /// RichTextBox, which swallows mouse-up on anything that is not a control. ChipButton gives it a
    /// visible RIM so it reads as an editable field even on the light theme, where the old fill-less
    /// pill vanished into the page. <paramref name="quiet"/> is the local speaker ("You"): still
    /// renameable, but dimmer, because their name is not a guess (it comes from Settings).
    /// </summary>
    static FrameworkElement Chip(string name, Action<string, string> rename, bool quiet = false)
    {
        var btn = new Button
        {
            Style = (Style)Application.Current.FindResource("ChipButton"),
            Margin = new Thickness(0, 0, 6, 6),
            Content = name,
            ToolTip = quiet
                ? "This is you. Rename here for this meeting, or in Settings for every meeting."
                : $"Rename {name} everywhere in this transcript",
        };
        btn.SetResourceReference(Control.BackgroundProperty, quiet ? "Chip" : "SurfaceHi");
        btn.SetResourceReference(Control.ForegroundProperty, quiet ? "TextDim" : "Text");
        btn.Click += (_, _) => ShowRename(btn, name, rename);
        return btn;
    }

    static void ShowRename(FrameworkElement anchor, string current, Action<string, string> rename)
    {
        // "You" is a stream label, not a name, and Toki already knows what to call this person from
        // Settings. Offering it pre-filled turns the rename from a question into a confirmation.
        var seed = IsYou(current) ? Llm.AppSettings.Current.DisplayName : current;

        var box = new TextBox
        {
            Text = seed, Width = 170, FontSize = 13.5, Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 0, 8, 0),
            // EXPLICITLY writable. TextBoxBase.IsReadOnly INHERITS, and this field is opened from a
            // chip inside the note's read-only RichTextBox: property inheritance reaches a Popup's
            // child through the logical tree, so without this the field arrives read-only and the
            // name simply cannot be changed. A local value beats an inherited one.
            IsReadOnly = false,
            IsReadOnlyCaretVisible = true,
        };
        box.SetResourceReference(Control.ForegroundProperty, "Text");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "Text");

        var field = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = box };
        Theme.GlassSurface.CrispRim(field);   // whole device pixels, or a rounded rim reads as soft
        field.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");
        field.SetResourceReference(Border.BorderBrushProperty, "Hairline");

        var save = new Button
        {
            Style = (Style)Application.Current.FindResource("PillButton"),
            Content = "Rename", FontSize = 12, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };
        row.Children.Add(field);
        row.Children.Add(save);

        var shell = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = row };
        Theme.GlassSurface.CrispRim(shell);
        shell.SetResourceReference(Border.BackgroundProperty, "Surface");
        shell.SetResourceReference(Border.BorderBrushProperty, "Hairline");

        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, VerticalOffset = 4,
            StaysOpen = false, AllowsTransparency = true, Child = shell, IsOpen = true,
        };
        DebugLastPopup = popup;   // test seam: a Popup is not reachable from the anchor's tree

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

        // FOCUS HAS TO WAIT FOR THE POPUP'S OWN WINDOW. A Popup builds its child and its HWND when
        // IsOpen flips, so a synchronous Focus() here silently does nothing: the caret never
        // arrives, every keystroke goes to the read-only document behind, and the field reads as a
        // label you cannot edit. Focus after the popup reports itself open, at Input priority.
        void TakeFocus()
        {
            popup.Dispatcher.BeginInvoke(new Action(() =>
            {
                box.Focus();
                Keyboard.Focus(box);
                box.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
        popup.Opened += (_, _) => TakeFocus();
        if (popup.IsOpen) TakeFocus();   // already open: Opened fired before this handler attached
    }

    /// <summary>The rename popup most recently opened. For the self-test only: a Popup lives in its
    /// own window, so a harness cannot find it by walking the anchor's visual tree.</summary>
    internal static Popup? DebugLastPopup { get; private set; }

    /// <summary>The local speaker's stream label, in the spellings the pipeline can produce.</summary>
    static bool IsYou(string name) =>
        name.Equals("You", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Me", StringComparison.OrdinalIgnoreCase);
}
