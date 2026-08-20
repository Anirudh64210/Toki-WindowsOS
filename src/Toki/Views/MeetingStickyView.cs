using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Toki.Storage;

namespace Toki.Views;

/// <summary>
/// A meeting's sticky note, shown inside the meeting's own note. The user's private page: it never
/// steers the summary (see <see cref="Llm.LlmNoteWriter"/>), isn't in the search index, and Toki
/// never writes a word in it. Auto-saves as you type; Delete is two-step (hand-typed text must not
/// be lost to one stray click).
/// </summary>
public sealed class MeetingStickyView : Border
{
    readonly long _meetingId;
    readonly TextBlock _status;
    readonly ContentControl _slot;
    readonly Button _delete;
    long? _stickyId;
    NoteEditor? _editor;
    bool _deleteArmed;

    readonly System.Windows.Threading.DispatcherTimer _save =
        new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };

    static object Res(string key) => Application.Current.FindResource(key);
    static Brush B(string key) => (Brush)Res(key);

    public MeetingStickyView(long meetingId, long? stickyId)
    {
        _meetingId = meetingId;
        _stickyId = stickyId;

        _status = new TextBlock
        {
            Style = (Style)Res("Dim"), FontSize = 10, Opacity = 0, Text = "Saved",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };

        _delete = new Button
        {
            Content = "Delete", Style = (Style)Res("PillButton"), FontSize = 10.5,
            Padding = new Thickness(10, 4, 10, 4), Background = Brushes.Transparent,
            Foreground = B("TextDim"), Cursor = System.Windows.Input.Cursors.Hand,
        };
        _delete.Click += (_, _) => OnDelete();

        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 9) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock
        {
            Text = "STICKY NOTE", FontFamily = (FontFamily)Res("UiFont"), FontSize = 10.5,
            FontWeight = FontWeights.SemiBold, Foreground = B("Amber"), VerticalAlignment = VerticalAlignment.Center,
        });
        left.Children.Add(_status);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(_delete, Dock.Right);
        header.Children.Add(_delete);
        header.Children.Add(left);

        _slot = new ContentControl();

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_slot);

        // An amber hairline, not a filled box: different content, not a warning.
        BorderBrush = B("Amber");
        BorderThickness = new Thickness(1);
        Theme.GlassSurface.CrispRim(this);   // whole device pixels, or the r12 rim reads as soft
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(14, 12, 14, 14);
        Margin = new Thickness(0, 6, 0, 6);
        Child = stack;
        Visibility = Visibility.Collapsed;
        // Transparent, not unset: a null-background Border is invisible to hit testing, so a click in the
        // panel's padding would fall through to the read-only document behind it and take focus away.
        Background = Brushes.Transparent;

        // Clicking anywhere in the panel puts the caret in the note: this panel is hosted INSIDE a
        // read-only RichTextBox, which reclaims keyboard focus and eats keystrokes.
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_editor is null || e.OriginalSource is DependencyObject src && IsInside(src, _delete)) return;
            Dispatcher.BeginInvoke(new Action(FocusEditorNow), System.Windows.Threading.DispatcherPriority.Input);
        };

        _save.Tick += (_, _) => { _save.Stop(); Flush(); };
    }

    /// <summary>True when this meeting already has a note - decides what the button above it says.</summary>
    public bool HasNote => _stickyId is not null;

    Button? _toggle;

    /// <summary>
    /// The button that opens this note, at the top of the meeting note. Not in the Copy/Delete
    /// toolbar: a sticky note is content, not an action performed on the note.
    /// </summary>
    public FrameworkElement ToggleButton
    {
        get
        {
            if (_toggle is not null) return _toggle;

            // Segoe MDL2 "Pinned", tying the button to the amber-edged panel it opens.
            var pin = new TextBlock
            {
                Text = "", FontSize = 11, Foreground = B("Amber"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0),
            };
            pin.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            label.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSoft");

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(pin);
            content.Children.Add(label);

            _toggle = new Button
            {
                Content = content, Style = (Style)Res("PillButton"), Background = B("Chip"),
                Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            _toggle.Click += (_, _) => { Toggle(); RelabelToggle(label); };
            Deleted += () => RelabelToggle(label);
            RelabelToggle(label);
            return _toggle;
        }
    }

    void RelabelToggle(TextBlock label) =>
        label.Text = !HasNote ? "Add a sticky note"
            : Visibility == Visibility.Visible ? "Hide my sticky note" : "My sticky note";

    /// <summary>Show the note, creating an empty one if this meeting hasn't got one yet.</summary>
    public void Reveal()
    {
        if (_stickyId is null)
        {
            try { _stickyId = App.Store.CreateSticky("", ""); App.Store.LinkStickyToMeeting(_meetingId, _stickyId.Value); }
            catch (Exception ex) { App.Log($"sticky create failed: {ex.Message}"); return; }
        }

        if (_editor is null)
        {
            _editor = new NoteEditor { MinHeight = 150 };
            _editor.Markdown = App.Store.GetSticky(_stickyId.Value)?.Body ?? "";
            _editor.Changed += (_, _) =>
            {
                _status.Text = "Saving…"; _status.BeginAnimation(OpacityProperty, null); _status.Opacity = 0.75;
                _save.Stop(); _save.Start();
            };
            _slot.Content = _editor;
        }

        Visibility = Visibility.Visible;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Theme.Motion.Dur(Theme.Motion.Enter))
        { EasingFunction = Theme.Motion.EaseOut });
        BringIntoView();

        // Focus AFTER layout, never in the same breath as adding the editor: Focus() on a control not
        // yet in a laid-out visual tree silently returns false, leaving keyboard focus on the hosting
        // read-only RichTextBox, which swallows every keystroke.
        Dispatcher.BeginInvoke(new Action(FocusEditorNow), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Put the caret in the note. Safe to call repeatedly.</summary>
    void FocusEditorNow()
    {
        if (_editor is null || Visibility != Visibility.Visible) return;
        _editor.FocusEditor();
    }

    static bool IsInside(DependencyObject node, DependencyObject root)
    {
        for (var p = node; p is not null; p = VisualTreeHelper.GetParent(p))
            if (ReferenceEquals(p, root)) return true;
        return false;
    }

    public void Hide() { Flush(); Visibility = Visibility.Collapsed; }

    /// <summary>Self-test seams: open the panel the way the button does, and reach the editor
    /// inside it, so `--stickytest` can drive the sticky note inside the meeting document.</summary>
    internal void DebugReveal() { _stickyId ??= DebugId; Reveal(); }
    internal NoteEditor? DebugEditor => _editor;

    /// <summary>Render harness only: show the panel with planted text and NO database row behind it, so
    /// taking a screenshot can never write into the user's real notes.</summary>
    internal void DebugSeed(string markdown)
    {
        _stickyId = DebugId;                    // never a real row - Flush and Delete both refuse it
        _editor = new NoteEditor { MinHeight = 150 };
        _editor.Markdown = markdown;
        _slot.Content = _editor;
        Visibility = Visibility.Visible;
    }

    public void Toggle()
    {
        if (Visibility == Visibility.Visible) Hide();
        else Reveal();
    }

    /// <summary>The id <see cref="DebugSeed"/> uses. Negative, so it can never collide with a real
    /// AUTOINCREMENT row; every write path checks for it.</summary>
    const long DebugId = -1;

    /// <summary>Write pending edits now. Called from every exit (closing the window, switching notes,
    /// hiding the panel).</summary>
    public void Flush()
    {
        _save.Stop();
        if (_editor is null || _stickyId is not { } id || id == DebugId) return;
        try
        {
            App.Store.UpdateSticky(id, _editor.Markdown);
            _status.Text = "Saved";
            _status.BeginAnimation(OpacityProperty, null);
            _status.Opacity = 1;
            _status.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1200)) { BeginTime = TimeSpan.FromMilliseconds(700) });
        }
        catch (Exception ex) { App.Log($"sticky save failed: {ex.Message}"); }
    }

    /// <summary>Two-step, and it really deletes: the note and every link to it,
    /// or orphan rows are left behind.</summary>
    void OnDelete()
    {
        if (!_deleteArmed)
        {
            _deleteArmed = true;
            _delete.Content = "Sure?";
            _delete.Foreground = B("Red");
            return;
        }

        _save.Stop();
        if (_stickyId is { } id && id != DebugId)
            try { App.Store.DeleteSticky(id); }
            catch (Exception ex) { App.Log($"sticky delete failed: {ex.Message}"); }

        _stickyId = null;
        _editor = null;
        _slot.Content = null;
        _deleteArmed = false;
        _delete.Content = "Delete";
        _delete.Foreground = B("TextDim");
        Visibility = Visibility.Collapsed;
        Deleted?.Invoke();
    }

    /// <summary>Raised after a delete so the button above can go back to reading "Add a sticky note".</summary>
    public event Action? Deleted;
}
