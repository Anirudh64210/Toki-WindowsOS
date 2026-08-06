using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
/// card and the in-meeting sticky note (owner regroup, 2026-08-05).
///
/// What it offers, and nothing more: three text SIZES, tickable TO-DOS, bullet and numbered LISTS,
/// blank-line SPACING, and one-tap insertion of any of Toki's note TEMPLATES. There is deliberately no
/// font-family and no colour — the owner ruled both out ("nothing outside normal text… just size,
/// templates, spacing, freedom"), and leaving them out is also what keeps the Markdown mirror honest:
/// everything on screen survives a round-trip to a plain .md file.
///
/// ── Structure, and why it changed (2026-08-06) ──
/// Every line is a FLAT <see cref="Paragraph"/> in <c>Document.Blocks</c>, carrying its
/// <see cref="NoteBlockKind"/> and — for a bullet, number or to-do — a leading marker held in an
/// <see cref="InlineUIContainer"/> that is NOT part of the text.
///
/// The first version used WPF's native <see cref="List"/> for bullets and numbers, to get
/// Enter-makes-the-next-item free. That turned out to be the source of the owner-reported bug
/// ("bullets are created by themselves"), in three separate ways:
///   1. A paragraph inside a ListItem is not a child of Document.Blocks, so inserting the next line
///      after it threw, and the "Body" button could never lift a line back OUT of the list.
///   2. The list's marker was invisible to the text model, so a marker the user TYPED ("- ") stayed in
///      the text and was written to Markdown on top of the one the kind already implies — "- - two",
///      growing another dash on every save.
///   3. A blank line silently split one list into two, restarting the numbering.
/// Flat paragraphs remove all three by construction: every line is addressable, every kind change is a
/// local edit, the marker can never leak into the text, and numbering is recomputed from position on
/// every change so it cannot go stale. Nesting is the only thing lost, and the Markdown format never
/// supported it anyway.
///
/// <see cref="NoteMarkdown"/> owns the text format (including the single definition of what a marker
/// is); this file owns only the visual shape of it.
/// </summary>
public partial class NoteEditor : UserControl
{
    /// <summary>Width of the marker gutter. Text hangs past it, so a wrapped bullet lines up under
    /// itself instead of under its own dot. Wide enough for a tick box or "10." AND a clear gap after
    /// it — at 21 the checkbox sat flush against the first letter.</summary>
    const double MarkerWidth = 26;

    bool _loading;    // rebuilding from Markdown — the document is ours, not the user's
    bool _mutating;   // our own structural edit — don't re-enter the text-changed repair
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
        Editor.TextChanged += OnTextChanged;
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
        try
        {
            var doc = new FlowDocument { PagePadding = new Thickness(0), LineHeight = 20 };
            foreach (var b in NoteMarkdown.Parse(markdown)) doc.Blocks.Add(MakeParagraph(b.Kind, b.Text, b.Done));
            if (doc.Blocks.Count == 0) doc.Blocks.Add(MakeParagraph(NoteBlockKind.Body, "", false));
            Editor.Document = doc;
            Renumber();
        }
        finally { _loading = false; }
    }

    /// <summary>Walk the document back into blocks, in reading order.</summary>
    IReadOnlyList<NoteBlock> ReadBlocks()
    {
        var outp = new List<NoteBlock>();
        foreach (var p in Paragraphs())
        {
            var kind = DerivedKind(p);
            var text = TextOf(p).Trim();
            // A marked line the user emptied is still a to-do/bullet (they're about to type into it);
            // a PLAIN line with nothing on it is the user's own spacing, and is preserved verbatim.
            if (text.Length == 0 && kind is NoteBlockKind.Heading or NoteBlockKind.Subheading or NoteBlockKind.Body)
                kind = NoteBlockKind.Blank;
            outp.Add(new NoteBlock(kind, text, IsTicked(p)));
        }
        return outp;
    }

    // ── one line ──

    Paragraph MakeParagraph(NoteBlockKind kind, string text, bool done)
    {
        var p = new Paragraph { Tag = kind };
        ApplyStyle(p, kind);
        if (MakeMarker(kind, done) is { } m) p.Inlines.Add(m);
        if (!string.IsNullOrEmpty(text)) p.Inlines.Add(new Run(text));
        return p;
    }

    static void ApplyStyle(Paragraph p, NoteBlockKind kind)
    {
        p.ClearValue(Paragraph.TextIndentProperty);
        p.FontWeight = FontWeights.Normal;
        switch (kind)
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
            case NoteBlockKind.Bullet:
            case NoteBlockKind.Numbered:
            case NoteBlockKind.Todo:
                // Hanging indent: the marker sits in the gutter and wrapped text lines up under the
                // first word, not under the dot.
                p.FontSize = 13; p.Margin = new Thickness(MarkerWidth, 1, 0, 1); p.TextIndent = -MarkerWidth;
                break;
            default:
                p.FontSize = 13; p.Margin = new Thickness(0);
                break;
        }
    }

    /// <summary>The leading marker for a kind — a real CheckBox for a to-do, a glyph for a list. Held in
    /// an <see cref="InlineUIContainer"/> so it can never be read back as part of the line's TEXT, which
    /// is what let a typed "- " and the kind's own dash pile up on each other.</summary>
    InlineUIContainer? MakeMarker(NoteBlockKind kind, bool done)
    {
        FrameworkElement child;
        switch (kind)
        {
            case NoteBlockKind.Todo:
            {
                // Not focusable, so typing and arrow keys flow straight past it into the text — the box
                // is for clicking, the line is for writing.
                var box = new CheckBox
                {
                    IsChecked = done, Focusable = false, Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (TryFindResource("TokiCheck") is Style s) box.Style = s;
                box.Checked += (_, _) => Raise();
                box.Unchecked += (_, _) => Raise();
                child = box;
                break;
            }
            case NoteBlockKind.Bullet: child = Glyph("•"); break;
            case NoteBlockKind.Numbered: child = Glyph("1."); break;
            default: return null;
        }

        child.HorizontalAlignment = HorizontalAlignment.Left;   // glyph left, gap right — never flush to the text
        var host = new Border { Width = MarkerWidth, Child = child, Background = Brushes.Transparent };
        return new InlineUIContainer(host) { BaselineAlignment = BaselineAlignment.Center, Tag = kind };
    }

    TextBlock Glyph(string text)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)FindResource("UiFont"),
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        return t;
    }

    static InlineUIContainer? MarkerOf(Paragraph p) =>
        p.Inlines.FirstOrDefault(i => i is InlineUIContainer { Tag: NoteBlockKind }) as InlineUIContainer;

    static bool IsTicked(Paragraph p) =>
        MarkerOf(p)?.Child is Border { Child: CheckBox c } && c.IsChecked == true;

    /// <summary>What this line IS, derived from what's actually on it. The marker is the truth: if the
    /// user selected over it and deleted it, the line really has become a plain one, and pretending
    /// otherwise (from a stale Tag) is how you get a bullet that won't go away.</summary>
    static NoteBlockKind DerivedKind(Paragraph p)
    {
        if (MarkerOf(p)?.Tag is NoteBlockKind marked) return marked;
        var tagged = p.Tag as NoteBlockKind?;
        if (tagged is NoteBlockKind.Bullet or NoteBlockKind.Numbered or NoteBlockKind.Todo) return NoteBlockKind.Body;
        return tagged ?? NoteBlockKind.Body;
    }

    /// <summary>The line's text — Runs only. Markers live in InlineUIContainers and are excluded by
    /// construction, so the text model never sees them.</summary>
    static string TextOf(Paragraph p)
    {
        var sb = new StringBuilder();
        Walk(p.Inlines, sb);
        return sb.ToString().Replace("\r", "").Replace("\n", " ");

        static void Walk(InlineCollection inlines, StringBuilder sb)
        {
            foreach (var i in inlines)
                switch (i)
                {
                    case Run r: sb.Append(r.Text); break;
                    case Span s: Walk(s.Inlines, sb); break;   // pasted content arrives wrapped in Spans
                    case LineBreak: sb.Append(' '); break;
                }
        }
    }

    IEnumerable<Paragraph> Paragraphs()
    {
        var outp = new List<Paragraph>();
        foreach (var b in Editor.Document.Blocks) Flatten(b, outp);
        return outp;
    }

    // Defensive: a paste can still drop a native List or Section into the document. We never CREATE
    // them, but we must be able to read one back.
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

    // ── keeping the document honest ──

    void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _mutating) return;
        _mutating = true;
        try { Normalize(); AutoPromote(); Renumber(); }
        finally { _mutating = false; }
        Raise();
    }

    /// <summary>Re-style any line whose look no longer matches what it is — a paragraph WPF created by
    /// splitting or pasting, or one whose marker the user deleted.</summary>
    void Normalize()
    {
        foreach (var p in Paragraphs())
        {
            var derived = DerivedKind(p);
            if (Equals(p.Tag, derived)) continue;
            p.Tag = derived;
            ApplyStyle(p, derived);
        }
    }

    /// <summary>Typing "- ", "1. ", "# " or "[] " turns the line into the real thing and eats the
    /// characters — which is both what every notes app does AND what stops the marker from surviving
    /// into the text and being written twice.
    ///
    /// Only fires the instant the marker is COMPLETED (nothing typed after it yet). A pasted sentence
    /// that merely happens to open with a dash is left alone rather than restructured under the user.</summary>
    void AutoPromote()
    {
        if (Editor.CaretPosition?.Paragraph is not { } p) return;
        if (p.Parent is not FlowDocument) return;

        var kind = DerivedKind(p);
        var text = TextOf(p);

        // "[] " / "[ ] " / "[x] " on a plain or bulleted line → a tick box. Typing the full
        // "- [ ] " passes through "- " (which becomes a bullet), so this is the second half of it.
        if (kind is NoteBlockKind.Body or NoteBlockKind.Bullet && TryBoxPrefix(text, out var boxDone, out var rest)
            && rest.Length == 0)
        { Retype(p, NoteBlockKind.Todo, boxDone); return; }

        if (kind != NoteBlockKind.Body) return;
        if (!NoteMarkdown.TryMarker(text, out var mk, out var after, out var done)) return;
        if (after.Length > 0) return;
        Retype(p, mk, done);
    }

    static bool TryBoxPrefix(string text, out bool done, out string rest)
    {
        done = false; rest = "";
        var t = text.TrimStart();
        if (t.Length < 3 || t[0] != '[') return false;
        var close = t[1] == ']' ? 1 : t.Length > 2 && t[2] == ']' ? 2 : -1;
        if (close < 0) return false;
        if (close == 2)
        {
            if (t[1] is 'x' or 'X') done = true;
            else if (t[1] != ' ') return false;
        }
        if (close + 1 >= t.Length || t[close + 1] != ' ') return false;
        rest = t[(close + 2)..].Trim();
        return true;
    }

    /// <summary>Replace a line's kind and clear its text, putting the caret ready to type. Used by the
    /// auto-promote, which has just consumed the characters the user typed as the marker.</summary>
    void Retype(Paragraph p, NoteBlockKind kind, bool done)
    {
        p.Inlines.Clear();
        p.Tag = kind;
        ApplyStyle(p, kind);
        if (MakeMarker(kind, done) is { } m) p.Inlines.Add(m);
        Editor.CaretPosition = p.ContentEnd;
    }

    /// <summary>Numbers are POSITIONAL, recomputed on every change — inserting or deleting an item can
    /// never leave "1. 2. 2. 5." behind. A run restarts at 1 after any line that isn't numbered.</summary>
    void Renumber()
    {
        var n = 0;
        foreach (var p in Paragraphs())
        {
            if (DerivedKind(p) != NoteBlockKind.Numbered) { n = 0; continue; }
            n++;
            if (MarkerOf(p)?.Child is Border { Child: TextBlock t }) t.Text = n + ".";
        }
    }

    void Raise() { if (!_loading) Changed?.Invoke(this, EventArgs.Empty); }

    // ── typing behaviour ──

    void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Return) e.Handled = HandleReturn();
        else if (e.Key == Key.Back) e.Handled = HandleBackspace();
    }

    /// <summary>Enter is ours, for every line kind — WPF's own split copies a paragraph's formatting but
    /// not its marker, which used to leave a "heading" that wasn't one and a bullet with no dot.
    /// Enter on a marked line makes the NEXT one; Enter on an EMPTY marked line ends the list instead.</summary>
    bool HandleReturn()
    {
        if (Editor.CaretPosition?.Paragraph is not { } p) return false;
        if (p.Parent is not FlowDocument) return false;    // pasted nesting — let WPF handle it
        if (!Editor.Selection.IsEmpty) return false;       // replacing a selection is WPF's job

        var kind = DerivedKind(p);
        var marked = kind is NoteBlockKind.Bullet or NoteBlockKind.Numbered or NoteBlockKind.Todo;

        _mutating = true;
        try
        {
            if (marked && TextOf(p).Trim().Length == 0)
            {
                // The second Enter on an empty item: leave the list rather than making a fourth
                // empty bullet nobody asked for.
                SetParagraphKind(p, NoteBlockKind.Body);
                Editor.CaretPosition = p.ContentEnd;
                Raise();
                return true;
            }

            // Split at the caret: whatever sits to its right moves down to the new line.
            var caret = AfterMarker(p, Editor.CaretPosition!);
            var tailRange = new TextRange(caret, p.ContentEnd);
            var tail = (tailRange.Text ?? "").Replace("\r", "").Replace("\n", "");
            if (tail.Length > 0) tailRange.Text = "";

            var next = MakeParagraph(marked ? kind : NoteBlockKind.Body, tail, false);
            Editor.Document.Blocks.InsertAfter(p, next);
            Editor.CaretPosition = StartOfText(next);
        }
        finally { _mutating = false; }

        Renumber();
        Raise();
        return true;
    }

    /// <summary>Backspace at the very start of a marked line removes the marker instead of eating the
    /// line before it — the standard way out of a list, and the one people reach for first.</summary>
    bool HandleBackspace()
    {
        if (Editor.CaretPosition?.Paragraph is not { } p) return false;
        if (!Editor.Selection.IsEmpty) return false;
        if (DerivedKind(p) is not (NoteBlockKind.Bullet or NoteBlockKind.Numbered or NoteBlockKind.Todo)) return false;
        // "At the start" means nothing but the marker sits to the left. Comparing pointers directly is
        // brittle — WPF hands back an equivalent-but-not-identical position depending on which way the
        // caret arrived — so ask what TEXT is to the left instead.
        if ((new TextRange(p.ContentStart, Editor.CaretPosition).Text ?? "").Trim().Length != 0) return false;

        _mutating = true;
        try { SetParagraphKind(p, NoteBlockKind.Body); }
        finally { _mutating = false; }
        Renumber();
        Raise();
        return true;
    }

    // The first position AFTER the marker — the caret must never land in the gutter, or a split would
    // carry the marker into the tail text.
    static TextPointer AfterMarker(Paragraph p, TextPointer caret) =>
        MarkerOf(p) is { } m && caret.CompareTo(m.ElementEnd) < 0 ? m.ElementEnd : caret;

    static TextPointer StartOfText(Paragraph p) => MarkerOf(p)?.ElementEnd ?? p.ContentStart;

    // ── the toolbar ──

    void BuildToolbar()
    {
        Toolbar.Children.Clear();

        if (_compact)
        {
            // Everything the full bar does, in one row that fits the notch. Tooltips carry the words.
            Toolbar.Children.Add(Chip("Aa", "Text size", ShowSizes));
            Toolbar.Children.Add(Chip("☑", "A line with a tick box", () => SetKind(NoteBlockKind.Todo)));
            Toolbar.Children.Add(Chip("•", "Bulleted list", () => SetKind(NoteBlockKind.Bullet)));
            Toolbar.Children.Add(Chip("1.", "Numbered list", () => SetKind(NoteBlockKind.Numbered)));
            Toolbar.Children.Add(Chip("＋", "Insert the sections of one of Toki's note shapes", ShowTemplates));
            return;
        }

        Toolbar.Children.Add(Chip("Title", "Biggest text size", () => SetKind(NoteBlockKind.Heading)));
        Toolbar.Children.Add(Chip("Heading", "Middle text size", () => SetKind(NoteBlockKind.Subheading)));
        Toolbar.Children.Add(Chip("Body", "Normal text", () => SetKind(NoteBlockKind.Body)));
        Toolbar.Children.Add(Gap());
        Toolbar.Children.Add(Chip("☑ To-do", "A line with a tick box", () => SetKind(NoteBlockKind.Todo)));
        Toolbar.Children.Add(Chip("• List", "Bulleted list", () => SetKind(NoteBlockKind.Bullet)));
        Toolbar.Children.Add(Chip("1. Steps", "Numbered list", () => SetKind(NoteBlockKind.Numbered)));
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

    /// <summary>Re-style every paragraph the selection touches. Works on a selection, not just the
    /// caret, so you can turn five lines into headings in one go — and pressing the SAME button again
    /// turns them back into plain lines, which is the only way to leave a list with the mouse.</summary>
    void SetKind(NoteBlockKind kind)
    {
        var paras = SelectedParagraphs();
        if (paras.Count == 0) return;

        var allSame = paras.All(p => DerivedKind(p) == kind);
        var target = allSame ? NoteBlockKind.Body : kind;

        _mutating = true;
        try { foreach (var p in paras) SetParagraphKind(p, target); }
        finally { _mutating = false; }

        Renumber();
        Raise();
    }

    /// <summary>Change one line's kind in place, keeping its text, its tick state and the caret.</summary>
    void SetParagraphKind(Paragraph p, NoteBlockKind kind)
    {
        if (DerivedKind(p) == kind) return;
        var done = IsTicked(p);
        if (MarkerOf(p) is { } old) p.Inlines.Remove(old);

        p.Tag = kind;
        ApplyStyle(p, kind);

        if (MakeMarker(kind, done) is { } m)
        {
            if (p.Inlines.FirstInline is { } first) p.Inlines.InsertBefore(first, m);
            else p.Inlines.Add(m);
        }
    }

    List<Paragraph> SelectedParagraphs()
    {
        var all = Paragraphs().ToList();
        var start = Editor.Selection.Start.Paragraph;
        var end = Editor.Selection.End.Paragraph;
        if (start is null && end is null) return all.Count > 0 ? new List<Paragraph> { all[^1] } : new();
        if (start is null) return new List<Paragraph> { end! };
        if (end is null || ReferenceEquals(start, end)) return new List<Paragraph> { start };

        var i = all.IndexOf(start);
        var j = all.IndexOf(end);
        if (i < 0 || j < 0) return new List<Paragraph> { start };
        if (i > j) (i, j) = (j, i);
        return all.GetRange(i, j - i + 1);
    }

    // ── templates ──

    void ShowTemplates() =>
        ShowPicker(NoteTemplates.All.Select(t => (t.Name, (Action)(() => InsertTemplate(t)))).ToArray());

    /// <summary>Toki's own themed popup — see <see cref="NotePicker"/>, which is also why the rows are
    /// real Buttons (a Border's mouse-up gets eaten as the popup closes, leaving dead menu items).</summary>
    void ShowPicker((string Label, Action Pick)[] items) => NotePicker.Show(Toolbar, items);

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
        CaretToEnd();
    }

    /// <summary>End of the LAST LINE — not Document.ContentEnd, which sits outside every paragraph and
    /// a RichTextBox refuses as a caret position.</summary>
    void CaretToEnd()
    {
        var last = Paragraphs().LastOrDefault();
        Editor.CaretPosition = last?.ContentEnd
            ?? Editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
    }

    // ── self-test seams (Dev.SelfTests --notetest; never used by the app) ──

    internal void DebugFocus() { Editor.Focus(); }
    internal void DebugCaretToEnd() => CaretToEnd();

    /// <summary>Type at the caret, exactly as the keyboard would — including the TextChanged pass that
    /// promotes a typed "- " into a real bullet.</summary>
    internal void DebugType(string text)
    {
        var caret = Editor.CaretPosition;
        if (caret.Parent is Run) caret.InsertTextInRun(text);
        else caret.Paragraph?.Inlines.Add(new Run(text));
        // The harness always types at the end of the note, so this is where the caret belongs. (A
        // freshly-added Run's own ContentEnd is not a pointer the RichTextBox will accept.)
        CaretToEnd();
    }

    internal void DebugCaretToLineStart()
    {
        if (Paragraphs().LastOrDefault() is { } p) Editor.CaretPosition = StartOfText(p);
    }

    internal void DebugEnter() => HandleReturn();
    internal void DebugBackspace() => HandleBackspace();
    internal void DebugToolbar(NoteBlockKind kind) => SetKind(kind);
    internal int DebugLineCount() => Paragraphs().Count();

    internal void DebugTick(int lineIndex, bool on)
    {
        var p = Paragraphs().ElementAtOrDefault(lineIndex);
        if (p is not null && MarkerOf(p)?.Child is Border { Child: CheckBox c }) c.IsChecked = on;
    }
}
