using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Toki.Views;

/// <summary>
/// The command menu a note opens when you type "/", at the caret.
///
/// It replaces a permanent chip toolbar that sat above every note. The toolbar was always there,
/// took a row of a 356px panel, and named things badly ("Steps", "Style", "Aa 10"), which is the
/// shape of interface that explains its own capabilities instead of getting out of the way. The
/// rule this follows is Notion's: a blank line is a blank line with a faint hint, and the menu
/// exists the instant you ask for it and not before.
///
/// Filtering is by what you type after the slash. Up and down move, Enter or a click choose,
/// Escape or a space cancels. Every row is a real Button: this lives over a text editor, and a
/// container with a mouse handler has its mouse-up eaten as the popup closes.
/// </summary>
public sealed class SlashMenu
{
    public sealed record Command(string Label, string Hint, string Glyph, Action Run);

    readonly Popup _popup;
    readonly StackPanel _rows = new();
    readonly List<Command> _all;
    readonly Action _clearTyping;
    List<Command> _shown = [];
    int _selected;

    public bool IsOpen => _popup.IsOpen;

    /// <summary>What the user has typed after the slash, used to filter.</summary>
    public string Filter { get; private set; } = "";

    /// <param name="clearTyping">Takes the "/" and whatever was typed after it back out of the
    /// document. It belongs to the MENU, not to one of the ways of choosing: the keyboard used to do
    /// this for itself and the mouse did not, so every command picked with the pointer left a stray
    /// "/" (or a whole "/template") sitting in the note. One commit path, one cleanup.</param>
    public SlashMenu(UIElement anchor, IEnumerable<Command> commands, Action clearTyping)
    {
        _all = commands.ToList();
        _clearTyping = clearTyping;

        var shell = new Border
        {
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            Padding = new Thickness(5), MinWidth = 232, MaxHeight = 296,
            Child = new ScrollViewer
            {
                Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };
        shell.SetResourceReference(Border.BackgroundProperty, "Surface");
        shell.SetResourceReference(Border.BorderBrushProperty, "Hairline");
        Theme.GlassSurface.CrispRim(shell);

        _popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Absolute,
            StaysOpen = false, AllowsTransparency = true, Child = shell,
        };
    }

    /// <summary>Open at a rectangle in SCREEN coordinates - the caret's own box, so the menu appears
    /// where the writing is rather than where the chrome is.</summary>
    public void Open(Rect caretOnScreen)
    {
        Filter = "";
        _popup.HorizontalOffset = caretOnScreen.Left;
        _popup.VerticalOffset = caretOnScreen.Bottom + 4;
        Rebuild();
        _popup.IsOpen = true;
    }

    public void Close() { _popup.IsOpen = false; Filter = ""; }

    /// <summary>Extend the filter with a typed character. Returns false when nothing matches any
    /// more, which is the caller's cue to close: a menu showing nothing is a menu in the way.</summary>
    public bool Type(char c)
    {
        Filter += c;
        Rebuild();
        return _shown.Count > 0;
    }

    public void Backspace()
    {
        if (Filter.Length > 0) Filter = Filter[..^1];
        Rebuild();
    }

    public void Move(int delta)
    {
        if (_shown.Count == 0) return;
        _selected = (_selected + delta + _shown.Count) % _shown.Count;
        Paint();
    }

    /// <summary>Run the highlighted command. Returns false when there is nothing to run.</summary>
    public bool Commit()
    {
        if (_shown.Count == 0) return false;
        return Run(_shown[Math.Clamp(_selected, 0, _shown.Count - 1)]);
    }

    /// <summary>THE one way a command runs, whichever way it was chosen. The typing comes out of the
    /// document BEFORE the command touches it, so the command sees the line as it was before the
    /// menu opened.</summary>
    bool Run(Command cmd)
    {
        _clearTyping();
        Close();
        cmd.Run();
        return true;
    }

    void Rebuild()
    {
        // Match on LETTERS ONLY, both sides. Someone typing "todo" is looking for "To-do", and
        // someone typing "numbered" should not have to guess whether the label says "1." first.
        var f = Key(Filter);
        _shown = f.Length == 0
            ? _all
            : _all.Where(c => Key(c.Label).Contains(f, StringComparison.Ordinal)
                              || Key(c.Hint).Contains(f, StringComparison.Ordinal)).ToList();
        _selected = 0;

        _rows.Children.Clear();
        foreach (var cmd in _shown) _rows.Children.Add(Row(cmd));
        Paint();
    }

    static string Key(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    Button Row(Command cmd)
    {
        var glyph = new TextBlock
        {
            Text = cmd.Glyph, FontSize = 13.5, Width = 20,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");

        var label = new TextBlock { Text = cmd.Label, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        label.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");

        var hint = new TextBlock
        {
            Text = cmd.Hint, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 1, 0, 0),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        hint.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");

        var row = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(glyph, Dock.Left);
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(hint, Dock.Right);
        row.Children.Add(glyph);
        row.Children.Add(label);
        row.Children.Add(hint);

        var b = new Button
        {
            Content = row, Style = (Style)Application.Current.FindResource("RowButton"),
            Padding = new Thickness(8, 7, 8, 7), Cursor = Cursors.Hand, Tag = cmd,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        b.Click += (_, _) => Run(cmd);
        b.MouseEnter += (_, _) => { _selected = _shown.IndexOf(cmd); Paint(); };
        return b;
    }

    /// <summary>Self-test seam: choose a row the way the POINTER does, through the row's own Click.
    /// The keyboard and the mouse used to be two different commit paths and only one of them cleaned
    /// up after itself, so a test that only ever pressed Enter could not see it.</summary>
    internal bool DebugClickRow(string label)
    {
        foreach (var child in _rows.Children)
            if (child is Button { Tag: Command c } b && c.Label == label)
            {
                b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                return true;
            }
        return false;
    }

    void Paint()
    {
        for (var i = 0; i < _rows.Children.Count; i++)
            if (_rows.Children[i] is Button b)
            {
                if (i == _selected) b.SetResourceReference(Control.BackgroundProperty, "Chip");
                else b.Background = Brushes.Transparent;
            }
    }
}
