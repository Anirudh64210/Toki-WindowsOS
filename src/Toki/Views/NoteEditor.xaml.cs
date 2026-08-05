using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Toki.Llm;
using Toki.Storage;

namespace Toki.Views;

/// <summary>
/// The hand-written note editor — ONE control, reused by the Library's manual notes, the editable prep
/// card and the in-meeting scratchpad (owner regroup, 2026-08-05).
///
/// What it offers, and nothing more: three text SIZES, tickable TO-DOS, bullet and numbered LISTS,
/// blank-line SPACING, and one-tap insertion of any of Toki's note TEMPLATES. There is deliberately no
/// font-family and no colour — the owner ruled both out ("nothing outside normal text… just size,
/// templates, spacing, freedom"), and leaving them out is also what keeps the Markdown mirror honest:
/// everything on screen survives a round-trip to a plain .md file.
///
/// Structure: every line is a <see cref="Paragraph"/> tagged with its <see cref="NoteBlockKind"/>.
/// Bullets and numbers use WPF's native <see cref="List"/> so Enter-makes-the-next-item and
/// Tab-indents work for free; a to-do is a tagged paragraph whose first inline is a real CheckBox.
/// <see cref="NoteMarkdown"/> owns the text format; this file owns only the visual shape of it.
/// </summary>
public partial class NoteEditor : UserControl
{
    bool _loading;
    bool _compact;
    public event EventHandler? Changed;

    /// <summary>Compact toolbar for narrow hosts — the notch is 356px wide, where the full labelled
    /// toolbar wraps onto two rows and eats the card. Same capabilities, glyphs instead of words, with
    /// the three text sizes behind one "Aa" button.</summary>
    public bool Compact
    {
        get => _compact;
        set { if (_compact == value) return; _compact = value; BuildToolbar(); }
    }

    public NoteEditor()
    {
        InitializeComponent();
        BuildToolbar();
        Editor.TextChanged += (_, _) => { if (!_loading) Changed?.Invoke(this, EventArgs.Empty); };
        Editor.PreviewKeyDown += OnKey;
        // A fresh RichTextBox defaults to a fat paragraph gap; we control spacing per block kind.
        Editor.Document.PagePadding = new Thickness(0);
        Editor.Document.LineHeight = 20;
    }

    // ── the text in, the text out ──

    /// <summary>The note as Markdown. Setting it rebuilds the document; reading it serialises what's
    /// currently on screen (including which boxes are ticked).</summary>
    public string Markdown
    {
        get => NoteMarkdown.Write(ReadBlocks());
        set => Load(value);
    }

    void Load(string? markdown)
    {
        _loading = true;
        var doc = new FlowDocument { PagePadding = new Thickness(0), LineHeight = 20 };
        var blocks = NoteMarkdown.Parse(markdown);

        // Consecutive bullets (or numbers) collect into ONE native List, which is what makes Enter and
        // Tab behave like every other editor.
        List? run = null;
        NoteBlockKind runKind = NoteBlockKind.Blank;

        foreach (var b in blocks)
        {
            if (b.Kind is NoteBlockKind.Bullet or NoteBlockKind.Numbered)
            {
                if (run is null || runKind != b.Kind)
                {
                    run = new List
                    {
                        MarkerStyle = b.Kind == NoteBlockKind.Bullet ? TextMarkerStyle.Disc : TextMarkerStyle.Decimal,
                        Margin = new Thickness(0, 2, 0, 2),
                        Padding = new Thickness(18, 0, 0, 0),
                    };
                    runKind = b.Kind;
                    doc.Blocks.Add(run);
                }
                run.ListItems.Add(new ListItem(Line(b)));
                continue;
            }

            run = null;
            doc.Blocks.Add(Line(b));
        }

        if (doc.Blocks.Count == 0) doc.Blocks.Add(Line(new NoteBlock(NoteBlockKind.Body, "")));

        Editor.Document = doc;
        _loading = false;
    }

    /// <summary>One line as a styled, tagged paragraph.</summary>
    Paragraph Line(NoteBlock b)
    {
        var p = new Paragraph { Tag = b.Kind, Margin = new Thickness(0) };

        switch (b.Kind)
        {
            case NoteBlockKind.Heading:
                p.FontSize = 19; p.FontWeight = FontWeights.SemiBold; p.Margin = new Thickness(0, 14, 0, 4);
                break;
            case NoteBlockKind.Subheading:
                p.FontSize = 15; p.FontWeight = FontWeights.SemiBold; p.Margin = new Thickness(0, 12, 0, 3);
                break;
            case NoteBlockKind.Blank:
                // A blank line is a BEAT, not a full empty row. Headings already carry a top margin,
                // so rendering the user's blank at full body height stacked into a chasm between
                // sections. Small enough to read as breathing room; still preserved on save.
                // LineHeight, not just FontSize — the document sets LineHeight=20, and that wins over
                // font size unless the paragraph overrides it.
                p.FontSize = 6; p.LineHeight = 9;
                break;
            default:
                p.FontSize = 13;
                break;
        }

        if (b.Kind == NoteBlockKind.Todo) p.Inlines.Add(TodoBox(b.Done));
        if (!string.IsNullOrEmpty(b.Text)) p.Inlines.Add(new Run(b.Text));
        return p;
    }

    /// <summary>The checkbox that leads a to-do line. Not focusable, so typing and arrow keys flow
    /// straight past it into the text — the box is for clicking, the line is for writing.</summary>
    InlineUIContainer TodoBox(bool done)
    {
        var box = new CheckBox
        {
            IsChecked = done,
            Focusable = false,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
        };
        if (TryFindResource("TokiCheck") is Style s) box.Style = s;
        box.Checked += (_, _) => { if (!_loading) Changed?.Invoke(this, EventArgs.Empty); };
        box.Unchecked += (_, _) => { if (!_loading) Changed?.Invoke(this, EventArgs.Empty); };

        return new InlineUIContainer(box) { BaselineAlignment = BaselineAlignment.Center };
    }

    /// <summary>Walk the document back into blocks, in reading order.</summary>
    IReadOnlyList<NoteBlock> ReadBlocks()
    {
        var outp = new List<NoteBlock>();
        foreach (var block in Editor.Document.Blocks) Collect(block, outp, null);
        return outp;
    }

    void Collect(Block block, List<NoteBlock> outp, NoteBlockKind? listKind)
    {
        switch (block)
        {
            case Paragraph p:
                outp.Add(BlockOf(p, listKind));
                break;

            case List list:
            {
                // A list's marker style is the truth about what it is — the user may have toggled it
                // with the toolbar after the paragraphs were created.
                var kind = list.MarkerStyle == TextMarkerStyle.Decimal
                    ? NoteBlockKind.Numbered : NoteBlockKind.Bullet;
                foreach (var item in list.ListItems)
                    foreach (var inner in item.Blocks)
                        Collect(inner, outp, kind);
                break;
            }

            case Section section:
                foreach (var inner in section.Blocks) Collect(inner, outp, listKind);
                break;
        }
    }

    static NoteBlock BlockOf(Paragraph p, NoteBlockKind? listKind)
    {
        var done = false;
        CheckBox? box = null;
        foreach (var inline in p.Inlines)
            if (inline is InlineUIContainer { Child: CheckBox c }) { box = c; done = c.IsChecked == true; break; }

        var text = TextOf(p).Trim();

        // Inside a list, the list wins — that's what the user sees. Otherwise trust the tag, and fall
        // back to the shape of the paragraph itself (a pasted-in document has no tags at all).
        var kind = box is not null ? NoteBlockKind.Todo
                 : listKind ?? (p.Tag as NoteBlockKind?
                   ?? (text.Length == 0 ? NoteBlockKind.Blank : NoteBlockKind.Body));

        // A tagged line the user emptied becomes a blank line, not an orphan "## " with nothing after it.
        if (text.Length == 0 && kind is NoteBlockKind.Heading or NoteBlockKind.Subheading or NoteBlockKind.Body)
            kind = NoteBlockKind.Blank;

        return new NoteBlock(kind, text, done);
    }

    static string TextOf(Paragraph p) =>
        new TextRange(p.ContentStart, p.ContentEnd).Text.Replace("\r", "").Replace("\n", " ");

    // ── typing behaviour ──

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return || Keyboard.Modifiers != ModifierKeys.None) return;
        if (CurrentParagraph() is not { } p) return;
        if ((p.Tag as NoteBlockKind?) != NoteBlockKind.Todo) return;

        // Enter on a to-do makes the NEXT to-do — the whole point of a checklist is not having to
        // press the button again for every line. Enter on an EMPTY to-do ends the list instead.
        var empty = TextOf(p).Trim().Length == 0;
        e.Handled = true;

        if (empty) { Retag(p, NoteBlockKind.Body); StripTodoBox(p); return; }

        var next = Line(new NoteBlock(NoteBlockKind.Todo, ""));
        Editor.Document.Blocks.InsertAfter(p, next);
        Editor.CaretPosition = next.ContentEnd;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    Paragraph? CurrentParagraph() => Editor.CaretPosition?.Paragraph;

    // ── the toolbar ──

    void BuildToolbar()
    {
        Toolbar.Children.Clear();

        if (_compact)
        {
            // Everything the full bar does, in one row that fits the notch. Tooltips carry the words.
            Toolbar.Children.Add(Chip("Aa", "Text size", ShowSizes));
            Toolbar.Children.Add(Chip("☑", "A line with a tick box", ToggleTodo));
            Toolbar.Children.Add(Chip("•", "Bulleted list", () => Apply(EditingCommands.ToggleBullets)));
            Toolbar.Children.Add(Chip("1.", "Numbered list", () => Apply(EditingCommands.ToggleNumbering)));
            Toolbar.Children.Add(Chip("＋", "Insert the sections of one of Toki's note shapes", ShowTemplates));
            return;
        }

        Toolbar.Children.Add(Chip("Title", "Biggest text size", () => SetKind(NoteBlockKind.Heading)));
        Toolbar.Children.Add(Chip("Heading", "Middle text size", () => SetKind(NoteBlockKind.Subheading)));
        Toolbar.Children.Add(Chip("Body", "Normal text", () => SetKind(NoteBlockKind.Body)));
        Toolbar.Children.Add(Gap());
        Toolbar.Children.Add(Chip("☑ To-do", "A line with a tick box", ToggleTodo));
        Toolbar.Children.Add(Chip("• List", "Bulleted list", () => Apply(EditingCommands.ToggleBullets)));
        Toolbar.Children.Add(Chip("1. Steps", "Numbered list", () => Apply(EditingCommands.ToggleNumbering)));
        Toolbar.Children.Add(Gap());
        Toolbar.Children.Add(Chip("Template", "Insert the sections of one of Toki's note shapes", ShowTemplates));
    }

    /// <summary>Compact mode's size picker — the three sizes behind one "Aa" button.</summary>
    void ShowSizes() => ShowPicker(new (string, Action)[]
    {
        ("Title",   () => SetKind(NoteBlockKind.Heading)),
        ("Heading", () => SetKind(NoteBlockKind.Subheading)),
        ("Body",    () => SetKind(NoteBlockKind.Body)),
    });

    static UIElement Gap() => new Border { Width = 10 };

    Border Chip(string label, string tip, Action click)
    {
        var text = new TextBlock
        {
            Text = label, FontSize = 11, FontFamily = (FontFamily)FindResource("UiFont"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");

        var b = new Border
        {
            Padding = new Thickness(9, 5, 9, 5), CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 4, 4), Background = Brushes.Transparent,
            Cursor = Cursors.Hand, Child = text, ToolTip = tip,
        };
        b.MouseEnter += (_, _) => b.SetResourceReference(BackgroundProperty, "Chip");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => { click(); Editor.Focus(); };
        return b;
    }

    void Apply(RoutedUICommand command)
    {
        Editor.Focus();
        command.Execute(null, Editor);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-style every paragraph the selection touches. Works on a selection, not just the
    /// caret, so you can turn five lines into headings in one go.</summary>
    void SetKind(NoteBlockKind kind)
    {
        foreach (var p in SelectedParagraphs())
        {
            StripTodoBox(p);
            Retag(p, kind);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void ToggleTodo()
    {
        var paras = SelectedParagraphs();
        // If everything selected is already a to-do, the button turns them back into plain lines.
        var allTodo = paras.Count > 0 && paras.All(p => (p.Tag as NoteBlockKind?) == NoteBlockKind.Todo);

        foreach (var p in paras)
        {
            if (allTodo) { StripTodoBox(p); Retag(p, NoteBlockKind.Body); continue; }
            if ((p.Tag as NoteBlockKind?) == NoteBlockKind.Todo) continue;

            Retag(p, NoteBlockKind.Todo);
            p.Inlines.InsertBefore(p.Inlines.FirstInline, TodoBox(false));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Apply a kind's look to an existing paragraph, in place (keeps the user's text + caret).</summary>
    static void Retag(Paragraph p, NoteBlockKind kind)
    {
        p.Tag = kind;
        switch (kind)
        {
            case NoteBlockKind.Heading:
                p.FontSize = 19; p.FontWeight = FontWeights.SemiBold; p.Margin = new Thickness(0, 14, 0, 4);
                break;
            case NoteBlockKind.Subheading:
                p.FontSize = 15; p.FontWeight = FontWeights.SemiBold; p.Margin = new Thickness(0, 12, 0, 3);
                break;
            default:
                p.FontSize = 13; p.FontWeight = FontWeights.Normal; p.Margin = new Thickness(0);
                break;
        }
    }

    static void StripTodoBox(Paragraph p)
    {
        var box = p.Inlines.FirstOrDefault(i => i is InlineUIContainer { Child: CheckBox });
        if (box is not null) p.Inlines.Remove(box);
    }

    List<Paragraph> SelectedParagraphs()
    {
        var start = Editor.Selection.Start.Paragraph;
        var end = Editor.Selection.End.Paragraph;
        if (start is null) return end is null ? new List<Paragraph>() : new List<Paragraph> { end };
        if (end is null || ReferenceEquals(start, end)) return new List<Paragraph> { start };

        var all = new List<Paragraph>();
        foreach (var block in Editor.Document.Blocks) Flatten(block, all);

        var i = all.IndexOf(start);
        var j = all.IndexOf(end);
        if (i < 0 || j < 0) return new List<Paragraph> { start };
        if (i > j) (i, j) = (j, i);
        return all.GetRange(i, j - i + 1);
    }

    static void Flatten(Block block, List<Paragraph> outp)
    {
        switch (block)
        {
            case Paragraph p: outp.Add(p); break;
            case List l:
                foreach (var item in l.ListItems)
                    foreach (var inner in item.Blocks) Flatten(inner, outp);
                break;
            case Section s:
                foreach (var inner in s.Blocks) Flatten(inner, outp);
                break;
        }
    }

    // ── templates ──

    void ShowTemplates() =>
        ShowPicker(NoteTemplates.All.Select(t => (t.Name, (Action)(() => InsertTemplate(t)))).ToArray());

    /// <summary>Toki's own themed popup — never a native Windows menu (the Library's sort dropdown
    /// learned that lesson the hard way).</summary>
    void ShowPicker((string Label, Action Pick)[] items)
    {
        var stack = new StackPanel();
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = Toolbar,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
            VerticalOffset = 4,
        };

        foreach (var (label, pick) in items)
        {
            var row = new Border
            {
                Padding = new Thickness(11, 8, 14, 8), CornerRadius = new CornerRadius(7),
                Cursor = Cursors.Hand, Background = Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = label, FontSize = 12,
                    FontFamily = (FontFamily)FindResource("UiFont"),
                    Foreground = (Brush)FindResource("Text"),
                },
            };
            row.MouseEnter += (_, _) => row.SetResourceReference(BackgroundProperty, "Chip");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => { popup.IsOpen = false; pick(); };
            stack.Children.Add(row);
        }

        var box = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(6),
            BorderThickness = new Thickness(1), Child = stack,
        };
        box.SetResourceReference(BackgroundProperty, "SurfaceHi");
        box.SetResourceReference(BorderBrushProperty, "Hairline");
        popup.Child = box;
        NotchPopups.Register(popup);   // an open picker counts as "still on the notch" — don't retract
        popup.Closed += (_, _) => NotchPopups.Unregister(popup);
        popup.IsOpen = true;
    }

    /// <summary>Append a template's sections to what's already written — never replace it. Someone who
    /// has already typed half a page and then picks a shape shouldn't lose the half page.</summary>
    public void InsertTemplate(NoteTemplate template)
    {
        var existing = Markdown.TrimEnd();
        var skeleton = NoteMarkdown.TemplateSkeleton(template);
        Markdown = existing.Length == 0 ? skeleton : existing + "\n\n" + skeleton;
        Changed?.Invoke(this, EventArgs.Empty);
        Editor.Focus();
    }

    /// <summary>Put the caret in the note, ready to type.</summary>
    public void FocusEditor()
    {
        Editor.Focus();
        Editor.CaretPosition = Editor.Document.ContentEnd;
    }
}
