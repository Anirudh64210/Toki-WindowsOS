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
/// The hand-written note editor - ONE control, reused by the Library's manual notes, the editable prep
/// card and the in-meeting sticky note (owner regroup, 2026-08-05).
///
/// What it offers, and nothing more: three text SIZES, tickable TO-DOS, bullet and numbered LISTS,
/// blank-line SPACING, and one-tap insertion of any of Toki's note TEMPLATES. There is deliberately no
/// font-family and no colour - the owner ruled both out ("nothing outside normal text… just size,
/// templates, spacing, freedom"), and leaving them out is also what keeps the Markdown mirror honest:
/// everything on screen survives a round-trip to a plain .md file.
///
/// ── Structure, and why it changed (2026-08-06) ──
/// Every line is a FLAT <see cref="Paragraph"/> in <c>Document.Blocks</c>, carrying its
/// <see cref="NoteBlockKind"/> and - for a bullet, number or to-do - a leading marker held in an
/// <see cref="InlineUIContainer"/> that is NOT part of the text.
///
/// The first version used WPF's native <see cref="List"/> for bullets and numbers, to get
/// Enter-makes-the-next-item free. That turned out to be the source of the owner-reported bug
/// ("bullets are created by themselves"), in three separate ways:
///   1. A paragraph inside a ListItem is not a child of Document.Blocks, so inserting the next line
///      after it threw, and the "Body" button could never lift a line back OUT of the list.
///   2. The list's marker was invisible to the text model, so a marker the user TYPED ("- ") stayed in
///      the text and was written to Markdown on top of the one the kind already implies - "- - two",
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
    /// it - at 21 the checkbox sat flush against the first letter.</summary>
    const double MarkerWidth = 26;

    /// <summary>The gap between a marker and the word after it. One number, so a bullet, a number and a
    /// tick box all sit the same distance from their own text.</summary>
    const double MarkerGap = 8;

    bool _loading;    // rebuilding from Markdown - the document is ours, not the user's
    bool _mutating;   // our own structural edit - don't re-enter the text-changed repair
    bool _compact;
    public event EventHandler? Changed;

    /// <summary>
    /// How big the note's text is, 1 to 12, as a comfort setting for the person WRITING.
    ///
    /// It scales the whole ramp - body, headings, list gutter and line spacing together - rather than
    /// setting a point size on a selection. Two reasons, and both are the reason Bold/Italic is parked:
    /// Markdown has no way to record "this word is 14pt", so a per-selection size could not survive a
    /// save; and per-Run formatting on this surface is exactly what caused the mosquito-text data loss
    /// (S8) - a line whose Run carried its own FontSize outranked its paragraph and was then written
    /// out as an empty block. The LINE decides how its text looks; this decides how big a line is.
    ///
    /// So the ramp stays semantic (Title / Heading / Body, which are real Markdown and travel with the
    /// file) and this is the dial on top of it.
    /// </summary>
    public int TextSize
    {
        get => _textSize;
        set
        {
            var clamped = Math.Clamp(value, MinTextSize, MaxTextSize);
            if (_textSize == clamped) return;
            _textSize = clamped;
            RestyleForSize();
        }
    }

    public const int MinTextSize = 1;
    public const int MaxTextSize = 12;

    /// <summary>4 is 13px, which is the size this editor shipped at - so an existing note is unchanged
    /// until somebody deliberately moves the dial.</summary>
    public const int DefaultTextSize = 4;

    int _textSize = DefaultTextSize;

    /// <summary>Body text in pixels for a step on the dial. Linear and gentle: 1 is 10px (dense, for a
    /// long note in a small window), 12 is 21px (comfortable across the room).</summary>
    double BodySize => 9 + _textSize;

    /// <summary>Compact toolbar for narrow hosts - the notch is 356px wide, where the full labelled
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
        // Before the first document is built, so nothing has to be restyled after the fact.
        try
        {
            var saved = Llm.AppSettings.Current.NoteTextSize;
            if (saved is >= MinTextSize and <= MaxTextSize) _textSize = saved;
        }
        catch { /* a settings read must never stop a note opening */ }
        BuildToolbar();
        Editor.TextChanged += OnTextChanged;
        Editor.PreviewKeyDown += OnKey;
        // A fresh RichTextBox defaults to a fat paragraph gap; we control spacing per block kind.
        Editor.Document.PagePadding = new Thickness(0);
        Editor.Document.LineHeight = DocLineHeight;
    }

    /// <summary>Leading grows with the type, or a bigger note reads as a tighter one.</summary>
    double DocLineHeight => Math.Round(BodySize * 20.0 / 13.0);

    /// <summary>Re-apply the ramp to every line after the size dial moves. Structural, so it must not
    /// look like the user typed - hence the mutation guard, and no Changed event: the note's TEXT has
    /// not changed, only how big it is drawn.</summary>
    void RestyleForSize()
    {
        _mutating = true;
        try
        {
            Editor.Document.LineHeight = DocLineHeight;
            foreach (var p in Paragraphs()) ApplyStyle(p, DerivedKind(p));
        }
        finally { _mutating = false; }
        Renumber();
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
            var doc = new FlowDocument { PagePadding = new Thickness(0), LineHeight = DocLineHeight };
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

    // The ramp is expressed as RATIOS of the body size, not as three fixed numbers, so the whole
    // document scales together when the size dial moves. The ratios are the sizes it shipped with
    // (19 and 15 against a 13px body), so at the default step nothing moves by a pixel.
    const double HeadingRatio = 19.0 / 13.0;
    const double SubheadingRatio = 15.0 / 13.0;

    void ApplyStyle(Paragraph p, NoteBlockKind kind)
    {
        var body = BodySize;
        var gutter = MarkerWidth * body / 13.0;   // the marker column grows with the text beside it

        p.ClearValue(Paragraph.TextIndentProperty);
        p.ClearValue(Paragraph.LineHeightProperty);   // only Blank sets one; it must not survive a change
        p.ClearValue(Paragraph.LineStackingStrategyProperty);   // ditto - it travels with that LineHeight
        p.FontWeight = FontWeights.Normal;
        switch (kind)
        {
            case NoteBlockKind.Heading:
                p.FontSize = Math.Round(body * HeadingRatio);
                p.FontWeight = FontWeights.SemiBold; p.Margin = new Thickness(0, 14, 0, 4);
                break;
            case NoteBlockKind.Subheading:
                p.FontSize = Math.Round(body * SubheadingRatio);
                p.FontWeight = FontWeights.SemiBold; p.Margin = new Thickness(0, 12, 0, 3);
                break;
            case NoteBlockKind.Blank:
                // A blank line is a BEAT, not a full empty row. Headings already carry a top margin,
                // so rendering the user's blank at full body height stacked into a chasm between
                // sections. LineHeight alone does the shrinking (the document's LineHeight wins
                // otherwise). The FONT SIZE deliberately stays at body size: it used to be 6, and the
                // first character typed into a blank line came out mosquito-sized.
                //
                // LineStackingStrategy is what makes the LineHeight actually count. WPF's default,
                // MaxHeight, takes the LARGER of the requested line height and the font's natural
                // height - so asking a 13px line for 9px of leading quietly got you 17px, and every
                // blank line in a note was nearly twice the gap it was supposed to be. Sections drifted
                // apart and the page read as loose rather than spaced.
                p.FontSize = body;
                p.LineHeight = Math.Round(body * 9.0 / 13.0);
                p.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                break;
            case NoteBlockKind.Bullet:
            case NoteBlockKind.Numbered:
            case NoteBlockKind.Todo:
                // Hanging indent: the marker sits in the gutter and wrapped text lines up under the
                // first word, not under the dot.
                p.FontSize = body; p.Margin = new Thickness(gutter, 1, 0, 1); p.TextIndent = -gutter;
                break;
            default:
                p.FontSize = body; p.Margin = new Thickness(0);
                break;
        }

        // WPF stamps the CARET's formatting onto whatever you type, so a Run born inside a heading (or
        // an old tiny blank line) carries its own explicit FontSize/FontWeight that then outranks the
        // paragraph. Clear it: on this surface the LINE decides how its text looks, always.
        foreach (var inline in p.Inlines)
            if (inline is Run r)
            {
                r.ClearValue(TextElement.FontSizeProperty);
                r.ClearValue(TextElement.FontWeightProperty);
            }
    }

    /// <summary>The leading marker for a kind - a real CheckBox for a to-do, a glyph for a list. Held in
    /// an <see cref="InlineUIContainer"/> so it can never be read back as part of the line's TEXT, which
    /// is what let a typed "- " and the kind's own dash pile up on each other.</summary>
    InlineUIContainer? MakeMarker(NoteBlockKind kind, bool done)
    {
        FrameworkElement child;
        switch (kind)
        {
            case NoteBlockKind.Todo:
            {
                // Not focusable, so typing and arrow keys flow straight past it into the text - the box
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

        // The marker is set RIGHT in its gutter, with one fixed gap before the text.
        //
        // Left-aligning it made every kind sit at the same left edge and therefore a different distance
        // from its own words: a tick box nearly filled the gutter and looked attached, while a bullet
        // dot is four pixels wide and floated a clear 20px away from the line it belonged to. Right
        // alignment is how numbered lists have been set for centuries - "1." and "10." end flush - and
        // it gives every marker the same relationship to its text while keeping one text column, so a
        // list of bullets and to-dos still lines up down the page.
        child.HorizontalAlignment = HorizontalAlignment.Right;
        child.Margin = new Thickness(0, 0, MarkerGap, 0);
        var host = new Border
        {
            Width = MarkerWidth * BodySize / 13.0, Child = child, Background = Brushes.Transparent,
        };
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

        // A blank line someone has typed into is not blank any more. Without this it stayed tagged Blank,
        // kept the short spacing line-height, AND - far worse - NoteMarkdown.Write emits nothing for a
        // Blank block, so the sentence they just typed was thrown away on the next save.
        if (tagged is NoteBlockKind.Blank && TextOf(p).Trim().Length > 0) return NoteBlockKind.Body;

        return tagged ?? NoteBlockKind.Body;
    }

    /// <summary>The line's text - Runs only. Markers live in InlineUIContainers and are excluded by
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

    /// <summary>Re-style any line whose look no longer matches what it is - a paragraph WPF created by
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
    /// characters - which is both what every notes app does AND what stops the marker from surviving
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

    /// <summary>Numbers are POSITIONAL, recomputed on every change - inserting or deleting an item can
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

    /// <summary>Enter is ours, for every line kind - WPF's own split copies a paragraph's formatting but
    /// not its marker, which used to leave a "heading" that wasn't one and a bullet with no dot.
    /// Enter on a marked line makes the NEXT one; Enter on an EMPTY marked line ends the list instead.</summary>
    bool HandleReturn()
    {
        if (Editor.CaretPosition?.Paragraph is not { } p) return false;
        if (p.Parent is not FlowDocument) return false;    // pasted nesting - let WPF handle it
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
    /// line before it - the standard way out of a list, and the one people reach for first.</summary>
    bool HandleBackspace()
    {
        if (Editor.CaretPosition?.Paragraph is not { } p) return false;
        if (!Editor.Selection.IsEmpty) return false;
        if (DerivedKind(p) is not (NoteBlockKind.Bullet or NoteBlockKind.Numbered or NoteBlockKind.Todo)) return false;
        // "At the start" means nothing but the marker sits to the left. Comparing pointers directly is
        // brittle - WPF hands back an equivalent-but-not-identical position depending on which way the
        // caret arrived - so ask what TEXT is to the left instead.
        if ((new TextRange(p.ContentStart, Editor.CaretPosition).Text ?? "").Trim().Length != 0) return false;

        _mutating = true;
        try { SetParagraphKind(p, NoteBlockKind.Body); }
        finally { _mutating = false; }
        Renumber();
        Raise();
        return true;
    }

    // The first position AFTER the marker - the caret must never land in the gutter, or a split would
    // carry the marker into the tail text.
    static TextPointer AfterMarker(Paragraph p, TextPointer caret) =>
        MarkerOf(p) is { } m && caret.CompareTo(m.ElementEnd) < 0 ? m.ElementEnd : caret;

    static TextPointer StartOfText(Paragraph p) => MarkerOf(p)?.ElementEnd ?? p.ContentStart;

    // ── the toolbar ──

    // The style chip and the size chip are rebuilt-in-place labels, so both can show their current
    // value the way Word's toolbar does ("Body", "13") instead of a generic glyph.
    Border? _styleChip, _sizeChip;

    void BuildToolbar()
    {
        Toolbar.Children.Clear();

        // STYLE and SIZE are two controls, not one dropdown with two sections.
        //
        // The first pass merged them behind a single "Aa" button with group headings inside, and the
        // owner's critique was exact: no document editor on earth does that. Word has a Styles control
        // and a Size control; Google Docs has a styles dropdown and a size stepper; they are separate
        // because they are different KINDS of answer - style is per-line structure that travels with
        // the file, size is how big the whole page is drawn. Merging them was tidiness at the cost of
        // matching every editor the user has ever used, which is the one convention a toolbar must
        // never spend.
        if (_compact)
        {
            // Everything the full bar does, in one row that fits the notch. Tooltips carry the words.
            _styleChip = Chip(StyleChipLabel(), "Line style - Title, Heading or Body", ShowStyles);
            _sizeChip = Chip($"{TextSize}", "Text size", ShowSizes);
            Toolbar.Children.Add(_styleChip);
            Toolbar.Children.Add(_sizeChip);
            Toolbar.Children.Add(Chip("☑", "A line with a tick box", () => SetKind(NoteBlockKind.Todo)));
            Toolbar.Children.Add(Chip("•", "Bulleted list", () => SetKind(NoteBlockKind.Bullet)));
            Toolbar.Children.Add(Chip("1.", "Numbered list", () => SetKind(NoteBlockKind.Numbered)));
            Toolbar.Children.Add(Chip("＋", "Insert the sections of one of Toki's note shapes", ShowTemplates));
            return;
        }

        _styleChip = Chip(StyleChipLabel(), "Line style - Title, Heading or Body", ShowStyles);
        _sizeChip = Chip($"Aa {TextSize}  ▾", "Text size for the whole note", ShowSizes);
        Toolbar.Children.Add(_styleChip);
        Toolbar.Children.Add(_sizeChip);
        Toolbar.Children.Add(Gap());
        Toolbar.Children.Add(Chip("☑ To-do", "A line with a tick box", () => SetKind(NoteBlockKind.Todo)));
        Toolbar.Children.Add(Chip("• List", "Bulleted list", () => SetKind(NoteBlockKind.Bullet)));
        Toolbar.Children.Add(Chip("1. Steps", "Numbered list", () => SetKind(NoteBlockKind.Numbered)));
        Toolbar.Children.Add(Gap());
        Toolbar.Children.Add(Chip("Template", "Insert the sections of one of Toki's note shapes", ShowTemplates));
    }

    string StyleChipLabel() => _compact ? "¶" : "Style  ▾";

    /// <summary>
    /// Everything about how big text is, behind one "Aa" button - in two groups, because they are two
    /// different questions and merging them would be the confusing kind of tidy.
    ///
    /// THIS LINE is structure: Title / Heading / Body are real Markdown, they travel with the file, and
    /// they are per-line. TEXT SIZE is comfort: 1 to 12, applies to the whole note, and is remembered
    /// for next time. See <see cref="TextSize"/> for why the numeric dial is not per-selection.
    /// </summary>
    /// <summary>The STYLE dropdown: what this line is. Anchored to its own chip, opened on the current
    /// line's kind, exactly the shape of Word's Styles control.</summary>
    void ShowStyles()
    {
        var current = CaretParagraph() is { } p ? DerivedKind(p) : NoteBlockKind.Body;
        var selectedLabel = current switch
        {
            NoteBlockKind.Heading => "Title",
            NoteBlockKind.Subheading => "Heading",
            _ => "Body",
        };
        NotePicker.Show((FrameworkElement?)_styleChip ?? Toolbar, new List<(string, Action?)>
        {
            ("Title",   () => SetKind(NoteBlockKind.Heading)),
            ("Heading", () => SetKind(NoteBlockKind.Subheading)),
            ("Body",    () => SetKind(NoteBlockKind.Body)),
        }, selectedLabel);
    }

    /// <summary>The SIZE dropdown: how big the note is drawn, 1 to 12. Its own chip, numbers only.</summary>
    void ShowSizes()
    {
        var items = new List<(string Label, Action? Pick)>();
        for (var n = MinTextSize; n <= MaxTextSize; n++)
        {
            var step = n;
            items.Add(($"{step}", () => SetTextSize(step)));
        }
        NotePicker.Show((FrameworkElement?)_sizeChip ?? Toolbar, items, selected: $"{TextSize}");
    }

    /// <summary>Change the size, remember it, and update the chip so the toolbar always states the
    /// current value - a size control that does not show its number is half a control.</summary>
    void SetTextSize(int step)
    {
        TextSize = step;
        try
        {
            var s = Llm.AppSettings.Current;
            if (s.NoteTextSize != TextSize) { s.NoteTextSize = TextSize; s.Save(); }
        }
        catch (Exception ex) { App.Log($"save note text size: {ex.Message}"); }
        RelabelChip(_sizeChip, _compact ? $"{TextSize}" : $"Aa {TextSize}  ▾");
        Editor.Focus();
    }

    /// <summary>The paragraph the caret sits in, for opening the style picker on the right answer.</summary>
    Paragraph? CaretParagraph() => Editor.CaretPosition?.Paragraph;

    static void RelabelChip(Border? chip, string label)
    {
        if (chip?.Child is TextBlock t) t.Text = label;
    }

    static UIElement Gap() => new Border { Width = 10 };

    Border Chip(string label, string tip, Action click)
    {
        var text = new TextBlock
        {
            Text = label, FontSize = 11, FontFamily = (FontFamily)FindResource("UiFont"),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");

        // The whole rounded box is the target, not the two characters inside it. In compact mode these
        // labels are a single glyph ("•", "1.", "Aa"), which is why they need a floor on their size -
        // aiming at a bullet point is not a thing anyone should have to do (owner, 2026-08-06).
        var b = new Border
        {
            Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(9),
            MinWidth = 32, MinHeight = 30,
            Margin = new Thickness(0, 0, 4, 4), Background = Brushes.Transparent,
            Cursor = Cursors.Hand, Child = text, ToolTip = tip,
        };
        b.MouseEnter += (_, _) => b.SetResourceReference(BackgroundProperty, "Chip");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => { click(); Editor.Focus(); };
        return b;
    }

    /// <summary>Re-style every paragraph the selection touches. Works on a selection, not just the
    /// caret, so you can turn five lines into headings in one go - and pressing the SAME button again
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

    void ShowTemplates()
    {
        var items = new List<(string Label, Action? Pick)>();
        foreach (var t in NoteTemplates.All)
        {
            var template = t;
            items.Add((template.Name, () => InsertTemplate(template)));
        }
        ShowPicker(items.ToArray());
    }

    /// <summary>Toki's own themed popup - see <see cref="NotePicker"/>, which is also why the rows are
    /// real Buttons (a Border's mouse-up gets eaten as the popup closes, leaving dead menu items).</summary>
    void ShowPicker((string Label, Action? Pick)[] items) => NotePicker.Show(Toolbar, items);

    /// <summary>Append a template's sections to what's already written - never replace it. Someone who
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

    /// <summary>End of the LAST LINE - not Document.ContentEnd, which sits outside every paragraph and
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

    /// <summary>Type at the caret, exactly as the keyboard would - including the TextChanged pass that
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

    /// <summary>Put the caret at the end of a given line, the way clicking on it would.</summary>
    internal void DebugCaretToLine(int index)
    {
        if (Paragraphs().ElementAtOrDefault(index) is { } p) Editor.CaretPosition = p.ContentEnd;
    }

    /// <summary>The rendered font size of a line - what the eye actually sees.</summary>
    internal double DebugFontSize(int index) =>
        Paragraphs().ElementAtOrDefault(index) is { } p ? p.FontSize : -1;

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
