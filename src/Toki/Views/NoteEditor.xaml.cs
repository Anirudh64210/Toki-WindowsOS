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
/// The hand-written note editor - ONE control, reused by the Library's manual notes, the editable
/// prep card and the in-meeting sticky note. Offers three text sizes, tickable to-dos, bullet and
/// numbered lists, blank-line spacing, and template insertion; no font-family and no colour, so
/// everything on screen survives a round-trip to a plain .md file.
///
/// Every line is a FLAT <see cref="Paragraph"/> in <c>Document.Blocks</c>, carrying its
/// <see cref="NoteBlockKind"/> and (for a bullet, number or to-do) a leading marker held in an
/// <see cref="InlineUIContainer"/> that is NOT part of the text. Never go back to WPF's native
/// <see cref="List"/>: its paragraphs are not children of Document.Blocks (inserts throw, lines
/// can't be lifted out), its marker is invisible to the text model (typed markers pile up in the
/// Markdown), and a blank line splits the list and restarts numbering.
///
/// <see cref="NoteMarkdown"/> owns the text format (including the single definition of what a
/// marker is); this file owns only the visual shape of it.
/// </summary>
public partial class NoteEditor : UserControl
{
    /// <summary>Width of the marker gutter, as a RATIO of the body size, so it holds its proportion
    /// at every text size. Text hangs past it, so a wrapped bullet lines up under itself instead of
    /// under its own dot. 1.75em is the compromise between a bullet's 24 and a tick box's 30 at a
    /// 16px body (Word indents 24, browsers 40 which reads slack in a 600px column).</summary>
    const double MarkerRatio = 1.5;

    /// <summary>
    /// The transparent padding TokiCheck keeps around its visible box, as a ratio of that control:
    /// its template is a 24px hit pad around an 18px box, so the ink starts 3px in. Backed out of the
    /// left margin so a tick box, a bullet and a number all start on the SAME left edge. Guarded by
    /// --notetest ("every marker starts on the same left edge"), which is what catches this if the
    /// template's numbers ever change.
    /// </summary>
    const double CheckInkInset = 3.0;

    bool _loading;    // rebuilding from Markdown - the document is ours, not the user's
    bool _mutating;   // our own structural edit - don't re-enter the text-changed repair
    bool _compact;
    public event EventHandler? Changed;

    /// <summary>
    /// How big the note's text is, 1 to 12. Scales the whole ramp (body, headings, gutter, leading)
    /// together, never a point size on a selection: Markdown cannot record per-word size, and
    /// per-Run FontSize outranks the paragraph and has caused data loss. The LINE decides how its
    /// text looks; this decides how big a line is.
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

    /// <summary>4 is 13px: an existing note is unchanged until somebody moves the dial.</summary>
    /// <summary>Step 7 is a 16px body. Sixteen is where iA, Notion and Bear all land for a desktop
    /// reading column, and 13 (the old default) is a UI-chrome size, not a reading size.</summary>
    public const int DefaultTextSize = 7;

    int _textSize = DefaultTextSize;

    /// <summary>Body text in pixels for a step on the dial: 1 is 10px, 12 is 21px.</summary>
    double BodySize => 9 + _textSize;

    /// <summary>Compact toolbar for narrow hosts, where the full labelled toolbar wraps onto two
    /// rows. Same capabilities, glyphs instead of words.</summary>
    public bool Compact
    {
        get => _compact;
        set { if (_compact == value) return; _compact = value; BuildToolbar(); }
    }

    /// <summary>
    /// Whether the editor scrolls ITSELF. On by default, because a sticky note and the prep
    /// scratchpad are boxes of a fixed size. A full note page turns it OFF: nested inside the page's
    /// own scroll it made two scrolling boxes, and which one moved depended on where the pointer
    /// happened to be. With it off the editor measures to its whole content and the page scrolls.
    /// </summary>
    public bool OwnScrolling
    {
        get => Editor.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;
        set => Editor.VerticalScrollBarVisibility = value ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    bool _jotting;

    /// <summary>
    /// JOTTING mode: bullets only. A sticky note is a scratchpad you keep beside the meeting, not a
    /// document - offering headings, text sizes, numbered steps and a template picker on it is a
    /// toolbar explaining capabilities nobody wanted there. Bullets are the one thing a scratchpad
    /// actually needs.
    /// </summary>
    public bool Jotting
    {
        get => _jotting;
        set { if (_jotting == value) return; _jotting = value; BuildToolbar(); }
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
        StyleHint();
        Loaded += (_, _) => ShowHint();
        Editor.TextChanged += OnTextChanged;
        Editor.PreviewKeyDown += OnKey;
        DataObject.AddPastingHandler(Editor, OnPaste);
        // A fresh RichTextBox defaults to a fat paragraph gap; we control spacing per block kind.
        Editor.Document.PagePadding = new Thickness(0);
        Editor.Document.LineHeight = DocLineHeight;
    }

    /// <summary>
    /// The height a line of body text takes ON ITS OWN, from the font. This is what a marked line is
    /// pinned to, so a bullet, a number and a to-do sit on exactly the rhythm a plain paragraph does.
    ///
    /// It is NOT DocLineHeight, and that is worth knowing: a FlowDocument's default line stacking is
    /// MaxHeight, which sizes each line to its own content and ignores LineHeight entirely. So the
    /// document's 1.5em leading has never been in force - the real leading has always been the
    /// typeface's own (about 1.33em). Matching that is the change that costs nothing; raising every
    /// line to 1.5em would re-space every note ever written and is the owner's call, not a side
    /// effect of fixing a list.
    /// </summary>
    double NaturalLineHeight => ((FontFamily)FindResource("UiFont")).LineSpacing * BodySize;

    /// <summary>Leading grows with the type, or a bigger note reads as a tighter one.</summary>
    /// <summary>1.5x the body. Butterick puts readable leading at 120-145 percent, and a 66-character
    /// measure wants the upper end; at a 16px body this is the canonical 24.</summary>
    double DocLineHeight => Math.Round(BodySize * 1.5);

    /// <summary>Re-apply the ramp to every line after the size dial moves. Structural, so it must not
    /// look like the user typed - hence the mutation guard, and no Changed event: the note's TEXT has
    /// not changed, only how big it is drawn.</summary>
    void RestyleForSize()
    {
        _mutating = true;
        try
        {
            Editor.Document.LineHeight = DocLineHeight;
            StyleHint();
            foreach (var p in Paragraphs())
            {
                var kind = DerivedKind(p);
                ApplyStyle(p, kind);
                // AND REBUILD THE MARKER. Its gutter, its dot, its digits and its tick box are all
                // sized from the body size, and restyling the paragraph alone left every one of them
                // at the size it was born at - so moving the dial resized the words and left the
                // markers behind, at the wrong scale and off the band.
                if (MarkerOf(p) is { } stale)
                {
                    var ticked = IsTicked(p);
                    p.Inlines.Remove(stale);
                    if (MakeMarker(kind, ticked) is { } fresh)
                    {
                        // A marked line the user has emptied has no first inline left to insert
                        // before, and InsertBefore(null, ...) throws.
                        if (p.Inlines.FirstInline is { } first) p.Inlines.InsertBefore(first, fresh);
                        else p.Inlines.Add(fresh);
                    }
                }
            }
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

    void Load(string? markdown) => LoadBlocks(NoteMarkdown.Parse(markdown));

    /// <summary>Rebuild the whole document from lines. The one place a document is made, so anything
    /// that produces lines (loading a note, inserting a template) lands identically.</summary>
    void LoadBlocks(IReadOnlyList<NoteBlock> blocks)
    {
        _loading = true;
        try
        {
            var doc = new FlowDocument { PagePadding = new Thickness(0), LineHeight = DocLineHeight };
            foreach (var b in blocks) doc.Blocks.Add(MakeParagraph(b.Kind, b.Text, b.Done));
            if (doc.Blocks.Count == 0) doc.Blocks.Add(MakeParagraph(NoteBlockKind.Body, "", false));
            Editor.Document = doc;
            Renumber();
        }
        finally { _loading = false; }
        // Loading suppresses TextChanged, so the hint has to be settled HERE too. Without this it
        // is decided once on an empty control and then never again, and it sits on top of the note.
        ShowHint();
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

    // The ramp is RATIOS of the body size, not fixed numbers, so the whole document scales together;
    // at the default step nothing moves by a pixel.
    const double HeadingRatio = 19.0 / 13.0;
    const double SubheadingRatio = 15.0 / 13.0;

    void ApplyStyle(Paragraph p, NoteBlockKind kind)
    {
        var body = BodySize;
        var gutter = Math.Round(body * MarkerRatio);   // the marker column grows with the text beside it

        p.ClearValue(Paragraph.TextIndentProperty);
        p.FontWeight = FontWeights.Normal;

        // EVERY line sets its own line height, and every line uses BlockLineHeight.
        //
        // A FlowDocument's default stacking is MaxHeight, which sizes each line to its own content
        // and IGNORES LineHeight entirely - so Document.LineHeight was inert and the note's leading
        // has always been whatever the typeface asked for (about 1.33em), not the 1.5 the code has
        // claimed since it was written. 1.5 is what Notion, Apple Notes and Bear all set a reading
        // column at, and it is the single biggest reason a page of text reads as calm.
        p.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        p.LineHeight = DocLineHeight;

        switch (kind)
        {
            // THE HEADING OWNS THE SPACE ABOVE IT, AND NOTHING OWNS THE SPACE BELOW.
            // That is Notion's rule (measured: H2 margin-top about 26px at a 16px body, no
            // margin-bottom at all), and it is why a heading reads as belonging to the text under it.
            // Toki used to put 0.62em below as well, so a heading followed by an empty line - which is
            // exactly what a template is - opened a gap of nearly seventy pixels that read as a hole.
            // Headings set their own tighter leading (1.3) or a two-line title looks unstitched.
            case NoteBlockKind.Heading:
                p.FontSize = Math.Round(body * HeadingRatio);
                p.FontWeight = FontWeights.SemiBold;
                p.Margin = new Thickness(0, Math.Round(body * 1.75), 0, 0);
                p.LineHeight = Math.Round(p.FontSize * 1.3);
                break;
            case NoteBlockKind.Subheading:
                p.FontSize = Math.Round(body * SubheadingRatio);
                p.FontWeight = FontWeights.SemiBold;
                p.Margin = new Thickness(0, Math.Round(body * 1.5), 0, 0);
                p.LineHeight = Math.Round(p.FontSize * 1.3);
                break;

            // A BLANK LINE IS A WHOLE LINE.
            // SUPERSEDES the 2026-08 decision that a blank line should be "a beat, not a full empty
            // row" (it was drawn at 9/13 of a line). Every editor a person has used - Notepad, Apple
            // Notes, Notion, Word - gives you a full empty line when you press Enter twice, and a
            // third of a line is both a gap you cannot predict and a target you can barely click.
            // The owner asked for a notepad; a notepad does this.
            // FONT SIZE must stay at body size, or the first character typed into a blank comes out
            // tiny.
            case NoteBlockKind.Blank:
                p.FontSize = body;
                p.Margin = new Thickness(0);
                break;

            case NoteBlockKind.Bullet:
            case NoteBlockKind.Numbered:
            case NoteBlockKind.Todo:
                // Hanging indent: the marker sits in the gutter and wrapped text lines up under the
                // first word, not under the dot.
                // Items in one list sit closer than paragraphs do: 0.25em either side is 8px of
                // air between items at a 16px body, which reads as one object rather than a stack.
                //
                // The pinned line height above is doing a second job here: a marker is a UIElement in
                // the line, so without it the line grows to whatever the marker needs and a to-do row
                // ends up taller than the bullet row above it. A marker taller than the line overflows
                // it and is NOT clipped - only its own host box ever clipped it.
                p.FontSize = body;
                p.Margin = new Thickness(gutter, Math.Round(body * 0.25), 0, Math.Round(body * 0.25));
                p.TextIndent = -gutter;
                break;

            default:
                p.FontSize = body;
                p.Margin = new Thickness(0);
                break;
        }

        // WPF stamps the CARET's formatting onto whatever you type, so a Run can carry its own
        // FontSize/FontWeight that outranks the paragraph. Clear it: the LINE decides, always.
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
            // A DOT, drawn, not typed. A "•" character is a glyph whose ink sits high in its own
            // box, at whatever size the font feels like, so it floated above the words beside it and
            // did not grow when the note's text size did. An ellipse is the ink itself: diameter and
            // position are ours, in ems, so it scales with the line and lands on the same optical
            // band as the text.
            case NoteBlockKind.Bullet: child = Dot(); break;
            case NoteBlockKind.Numbered: child = Glyph("1."); break;
            default: return null;
        }

        // The marker is set RIGHT in its gutter with one fixed gap before the text, so "1." and
        // "10." end flush and every marker sits the same distance from its own words.
        // A dot and a tick box are SHAPES: centring the box centres the ink. A number is not - digits
        // stand ON a baseline with the font's descent as empty space below them, so centring its box
        // leaves the digits floating above their own line. The number is aligned by BASELINE instead.
        var baselineLock = kind == NoteBlockKind.Numbered;

        // LEFT, not right. Right-aligning inside the gutter gave every marker a DIFFERENT left edge -
        // a 5px dot started 13px in and a 24px tick box started at 0 - so a list mixing bullets and
        // tick boxes had a ragged left margin and did not read as one object. Left is the edge the
        // headings and paragraphs above it already use, and the text still starts at a fixed column,
        // so nothing after the marker moves.
        child.HorizontalAlignment = HorizontalAlignment.Left;
        child.VerticalAlignment = baselineLock ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        child.Margin = new Thickness(kind == NoteBlockKind.Todo ? -CheckInkInset : 0, 0, 0, 0);

        // THE HOST NEVER CONSTRAINS THE MARKER'S HEIGHT.
        //
        // It used to be given exactly the text's cap height, to put the marker on the same optical
        // band as the capitals beside it. That worked as ARITHMETIC and failed as a picture: an
        // InlineUIContainer clips its child to the host's box, so a 24px tick box in an 11px host
        // lost two thirds of its ink and "1." lost the top of the digit. Every marker on screen was
        // a slice. The alignment self-test measured the child's CENTRE, which was correct to a
        // tenth of a pixel the whole time - it never asked how much of the marker survived.
        //
        // So the box sizes to what it holds and constrains nothing. What keeps a to-do row the same
        // height as a bullet row is not the marker any more, it is the PARAGRAPH: every marked line
        // is pinned to the document's line height in ApplyStyle, so a tall marker overflows its line
        // instead of stretching it. The line does not clip - only the host ever did.
        var host = new Border
        {
            Width = Math.Round(BodySize * MarkerRatio),
            Child = child,
            Background = Brushes.Transparent,
        };

        // BASELINE, not Center: Center aligns to the middle of the LINE BOX, which includes the room
        // a font reserves for descenders, so every marker rode high above its own words. Baseline
        // puts the host's BOTTOM on the text baseline - which leaves a host taller than the cap band
        // riding high by exactly half the difference. The sink corrects that, and it is a
        // RenderTransform because that moves ink WITHOUT moving the layout box: resizing the box is
        // what clipped the marker in the first place.
        var sink = new TranslateTransform();
        host.RenderTransform = sink;

        // The number rides back UP against that sink by the difference, so its digits land on the
        // text's baseline rather than half a line below it. A transform again, not a margin: a margin
        // would change the host's size, which is what SizeChanged is reading.
        var lift = new TranslateTransform();
        if (baselineLock) child.RenderTransform = lift;
        host.SizeChanged += (_, _) =>
        {
            sink.Y = (host.ActualHeight - CapHeight()) / 2;
            if (baselineLock) lift.Y = Descent() - sink.Y;
        };

        return new InlineUIContainer(host) { BaselineAlignment = BaselineAlignment.Baseline, Tag = kind };
    }


    /// <summary>The cap height of the body text, in pixels. Measured from the typeface, never
    /// guessed: it is the band a capital letter occupies, and it is what a marker should match.</summary>
    double CapHeight()
    {
        var family = (FontFamily)FindResource("UiFont");
        var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var caps = face.TryGetGlyphTypeface(out var g) ? g.CapsHeight : 0.7;
        return Math.Round(BodySize * caps);
    }

    /// <summary>The bullet: an ellipse sized in ems, vertically centred in the cap band.</summary>
    FrameworkElement Dot()
    {
        // 0.3em: a browser's own list disc is about 0.35em, and below 4px a dot disappears on
        // a dark surface while past 6px it starts reading as decoration.
        var d = Math.Max(4, Math.Round(BodySize * 0.3));
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = d, Height = d,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextDim");
        return dot;
    }

    TextBlock Glyph(string text)
    {
        // The SAME size as the words it labels: a number that is smaller than its own line reads as
        // a footnote, and it stopped scaling when the note's text size changed.
        // NO LineHeight override: the box has to keep the font's own ascent and descent, because its
        // BASELINE is what the marker is aligned by.
        var t = new TextBlock
        {
            Text = text, FontSize = BodySize,
            FontFamily = (FontFamily)FindResource("UiFont"),
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        return t;
    }

    /// <summary>The empty space a line of this text keeps BELOW the baseline, in pixels. Font metrics,
    /// never a guess - it is what separates a text box's bottom edge from the line it stands on.</summary>
    double Descent()
    {
        var family = (FontFamily)FindResource("UiFont");
        return Math.Max(0, (family.LineSpacing - family.Baseline) * BodySize);
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

        // A blank line someone has typed into is not blank any more. Without this,
        // NoteMarkdown.Write emits nothing for a Blank block and the typed sentence is lost on save.
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

    /// <summary>
    /// PASTE ARRIVES AS TEXT, never as someone else's formatting.
    ///
    /// A RichTextBox's default paste brings the SOURCE's fonts, sizes, colours and tables into the
    /// note, so one paste from a browser permanently ruins a page that has no font or colour
    /// controls to fix it with. The note's line kinds are the only formatting it has, so pasted
    /// text is read the same way typing is: each line's own leading marker decides what it becomes.
    ///
    /// Two rules from editors that have shipped this: blank runs COLLAPSE (splitting on one or more
    /// newlines, never producing empty blocks), and a paste into a line that already has content
    /// must not re-type that line - it is inserted as words, not as a new block.
    /// </summary>
    void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        var text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string
                   ?? e.SourceDataObject.GetData(DataFormats.Text) as string;
        if (string.IsNullOrEmpty(text)) { e.CancelCommand(); return; }

        e.CancelCommand();   // never let the rich-text paste run
        InsertPastedText(text);
    }

    internal void InsertPastedText(string text)
    {
        var normalised = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var blocks = NoteMarkdown.Parse(normalised)
            .Where(b => b.Kind != NoteBlockKind.Blank || b.Text.Length > 0)
            .ToList();
        if (blocks.Count == 0) return;

        var caret = Editor.CaretPosition;
        var target = caret?.Paragraph;
        if (caret is null || target is null) return;

        // ONE line pasted into a line that already has words is just words. Insert it VERBATIM:
        // running it through the markdown parser trims it, and a paste that silently eats its own
        // leading space joins two words together.
        var lineHasWords = TextOf(target).Trim().Length > 0;
        if (lineHasWords && !normalised.Contains('\n'))
        {
            _mutating = true;
            try { Editor.BeginChange(); caret.InsertTextInRun(normalised); Editor.EndChange(); }
            finally { _mutating = false; }
            Raise();
            ShowHint();
            return;
        }

        _mutating = true;
        try
        {
            Editor.BeginChange();   // ONE undo step for the whole paste, not one per line

            // The first pasted line joins the line the caret is in: pasting words into a sentence
            // must not turn that sentence into a bullet.
            var lineIsEmpty = TextOf(target).Trim().Length == 0;
            if (lineIsEmpty && blocks.Count > 0)
            {
                var first = blocks[0];
                var replacement = MakeParagraph(first.Kind, first.Text, first.Done);
                Editor.Document.Blocks.InsertAfter(target, replacement);
                Editor.Document.Blocks.Remove(target);
                target = replacement;
            }
            else
            {
                caret.InsertTextInRun(blocks[0].Text);
                target = caret.Paragraph ?? target;
            }

            foreach (var b in blocks.Skip(1))
            {
                var p = MakeParagraph(b.Kind, b.Text, b.Done);
                Editor.Document.Blocks.InsertAfter(target, p);
                target = p;
            }

            SetCaret(target.ContentEnd);
            Renumber();
            Editor.EndChange();
        }
        finally { _mutating = false; }
        Raise();
        ShowHint();
    }

    void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _mutating) return;
        _mutating = true;
        // ONE undo unit for the whole repair pass. These are DERIVED edits - restyling a pasted
        // paragraph, promoting a typed "- ", renumbering a list - and an editor that records them
        // separately makes Ctrl+Z undo Toki's tidying instead of the user's typing, twice.
        try { Editor.BeginChange(); Normalize(); AutoPromote(); Renumber(); Editor.EndChange(); }
        finally { _mutating = false; }
        ShowHint();
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
        SetCaret(p.ContentEnd);
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
        // THE SLASH MENU OWNS THE KEYBOARD WHILE IT IS OPEN. Arrow keys move the highlight, Enter
        // runs, Escape cancels - and none of those may also reach the document underneath, or a
        // Return would split the paragraph behind the menu.
        if (_slash is { IsOpen: true })
        {
            switch (e.Key)
            {
                case Key.Down: _slash.Move(1); e.Handled = true; return;
                case Key.Up: _slash.Move(-1); e.Handled = true; return;
                case Key.Return or Key.Tab:
                    e.Handled = _slash.Commit();   // the menu drops the typed "/" itself
                    return;
                case Key.Escape: _slash.Close(); e.Handled = true; return;
                case Key.Space: _slash.Close(); return;   // a space means it was prose, not a command
                case Key.Back:
                    // Backspacing over the slash itself closes the menu; the document handles the key.
                    if (_slash.Filter.Length == 0) _slash.Close(); else _slash.Backspace();
                    return;
                default:
                    if (e.Key is >= Key.A and <= Key.Z && !_slash.Type(char.ToLowerInvariant((char)('a' + (e.Key - Key.A)))))
                        _slash.Close();
                    return;
            }
        }

        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Return) e.Handled = HandleReturn();
        else if (e.Key == Key.Back) e.Handled = HandleBackspace();
        else if (e.Key == Key.OemQuestion && OpensCommands()) OpenSlash();
    }

    /// <summary>
    /// A slash only means "commands" at the START of a line, exactly as Notion does it. Mid-sentence
    /// it is a slash, and a menu popping up while somebody types "and/or" is the interface fighting
    /// the writing.
    /// </summary>
    bool OpensCommands()
    {
        if (Editor.CaretPosition is not { } caret) return false;
        if (caret.Paragraph is not { } p) return false;
        var before = new TextRange(StartOfText(p), caret).Text;
        return string.IsNullOrWhiteSpace(before);
    }

    void OpenSlash()
    {
        _slash ??= new SlashMenu(Editor, Commands(), DropSlashText);
        var rect = Editor.CaretPosition?.GetCharacterRect(LogicalDirection.Forward) ?? default;
        var topLeft = Editor.PointToScreen(new Point(rect.Left, rect.Top));
        var device = PresentationSource.FromVisual(Editor)?.CompositionTarget;
        var onScreen = device is not null
            ? device.TransformFromDevice.Transform(topLeft)
            : topLeft;
        // Open just below the caret's own box, not below the control: the menu belongs where the
        // writing is.
        _slash.Open(new Rect(onScreen.X, onScreen.Y, 1, rect.Height <= 0 ? 16 : rect.Height));
    }

    /// <summary>Remove the "/" and whatever was typed after it, so running a command leaves the line
    /// exactly as it was before the menu opened.</summary>
    void DropSlashText()
    {
        if (_slash is null || Editor.CaretPosition is not { } caret) return;
        var back = _slash.Filter.Length + 1;
        var from = caret.GetPositionAtOffset(-back, LogicalDirection.Backward);
        if (from is null) return;
        var range = new TextRange(from, caret);
        if (range.Text.StartsWith('/')) range.Text = "";
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
                SetCaret(p.ContentEnd);
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
            SetCaret(StartOfText(next));
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


    /// <summary>
    /// The commands behind "/" - the whole formatting vocabulary of a note, named for what they DO.
    /// "Steps" became "Numbered list", "Style" split into the three block names it was hiding, and
    /// the text size left the writing surface entirely because it is a page preference, not a
    /// per-line command.
    /// </summary>
    List<SlashMenu.Command> Commands()
    {
        var list = new List<SlashMenu.Command>
        {
            new("To-do", "tick box", "☐", () => SetKind(NoteBlockKind.Todo)),
            new("Bulleted list", "- ", "•", () => SetKind(NoteBlockKind.Bullet)),
            new("Numbered list", "1. ", "1.", () => SetKind(NoteBlockKind.Numbered)),
        };
        // A sticky note is a scratchpad, not a document: bullets are the whole vocabulary it needs.
        if (_jotting) return list;

        list.Add(new("Text", "plain line", "¶", () => SetKind(NoteBlockKind.Body)));
        list.Add(new("Heading", "big", "H1", () => SetKind(NoteBlockKind.Heading)));
        list.Add(new("Subheading", "medium", "H2", () => SetKind(NoteBlockKind.Subheading)));
        foreach (var t in NoteTemplates.All)
        {
            var template = t;
            list.Add(new($"{template.Name} template", "insert sections", "☷", () => InsertTemplate(template)));
        }
        return list;
    }

    SlashMenu? _slash;

    /// <summary>The hint stands in for the first line of writing, so it is drawn at the size and on
    /// the rhythm that writing will be. It was pinned at 13px while the body moved to 16, so the
    /// placeholder was visibly smaller than the text it was promising.</summary>
    void StyleHint()
    {
        Hint.FontSize = BodySize;
        Hint.LineHeight = DocLineHeight;
        Hint.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        PlaceHint();
    }

    /// <summary>
    /// Put the hint where the writing will be, clear of the cursor.
    ///
    /// MEASURED: the editor draws its first character at x=5, not 0 - a RichTextBox keeps a content
    /// offset of its own. The hint was pinned at x=1, so the placeholder started four pixels LEFT of
    /// the caret and the cursor was drawn inside the W of "Write". Reading the origin from the
    /// document rather than hard-coding it also survives the text-size dial, which moves it.
    /// </summary>
    /// <summary>
    /// Where this editor actually draws the first character, in DIPs from its own left edge. It is
    /// NOT zero: a RichTextBox keeps a content offset of its own (5px in Toki's), so anything laid
    /// out BESIDE the editor and expected to share its left edge - a note's title, above it - has to
    /// be told this number rather than assume 0.
    /// </summary>
    public double TextOrigin =>
        Editor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward)
             ?.GetCharacterRect(LogicalDirection.Forward).Left ?? 0;

    void PlaceHint()
    {
        var origin = Editor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward)
                          ?.GetCharacterRect(LogicalDirection.Forward).Left ?? 0;
        if (origin <= 0) return;   // not laid out yet - leave the last good value in place
        Hint.Margin = new Thickness(origin + SystemParameters.CaretWidth + 1, 0, 0, 0);
    }

    /// <summary>Self-test seam: where the hint's text begins, and where the caret sits on an empty
    /// note. The hint must clear the caret, or its first letter is drawn through the cursor.</summary>
    internal (double Hint, double Caret) DebugHintAndCaret()
    {
        var hint = Hint.Margin.Left;
        var caret = Editor.CaretPosition?.GetCharacterRect(LogicalDirection.Forward).Left ?? 0;
        return (hint, caret);
    }

    /// <summary>The one hint a blank note carries. It disappears the moment anything is typed, and
    /// it lives OUTSIDE the document, so it can never be deleted by the user or saved into the note.</summary>
    void ShowHint()
    {
        var blank = string.IsNullOrWhiteSpace(new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text);
        Hint.Visibility = blank ? Visibility.Visible : Visibility.Collapsed;
        if (blank) PlaceHint();
    }

    void BuildToolbar()
    {
        // Nothing to build any more: the commands are created on demand when the menu opens, so a
        // mode change (compact, jotting) cannot leave a stale row behind.
        _slash?.Close();
        _slash = null;
    }

    string StyleChipLabel() => _compact ? "¶" : "Style  ▾";

    /// <summary>The SIZE dropdown: how big the note is drawn, 1 to 12. Its own chip, numbers only.</summary>
    void ShowSizes()
    {
        var items = new List<(string Label, Action? Pick)>();
        for (var n = MinTextSize; n <= MaxTextSize; n++)
        {
            var step = n;
            items.Add(($"{step}", () => SetTextSize(step)));
        }
        NotePicker.Show(Editor, items, selected: $"{TextSize}");
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
        Editor.Focus();
    }

    /// <summary>The paragraph the caret sits in, for opening the style picker on the right answer.</summary>
    Paragraph? CaretParagraph() => Editor.CaretPosition?.Paragraph;


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
    /// real Buttons (a Border's mouse-up gets eaten as the popup closes, leaving dead menu items).
    /// Anchored to the EDITOR: anchored to a full-width toolbar, as this was, the popup opened
    /// across the whole control and sat on top of the writing.</summary>
    void ShowPicker((string Label, Action? Pick)[] items) => NotePicker.Show(Editor, items);

    /// <summary>Append a template's sections to what's already written - never replace it. Someone who
    /// has already typed half a page and then picks a shape shouldn't lose the half page.</summary>
    public void InsertTemplate(NoteTemplate template)
    {
        // BLOCKS, not Markdown. Going out through Markdown and back in collapses the template's empty
        // body lines into blank ones (Markdown cannot tell them apart), which is what turned a shape
        // to fill in into a stack of headings with squashed slivers between them.
        var blocks = ReadBlocks().ToList();
        while (blocks.Count > 0 && blocks[^1].Kind == NoteBlockKind.Blank) blocks.RemoveAt(blocks.Count - 1);
        var appending = blocks.Count > 0;
        if (appending) blocks.Add(new NoteBlock(NoteBlockKind.Blank, ""));
        var first = blocks.Count;
        blocks.AddRange(NoteMarkdown.TemplateBlocks(template));

        LoadBlocks(blocks);
        Changed?.Invoke(this, EventArgs.Empty);

        // The caret lands on the template's FIRST writable line, not at the end of the note: a
        // template is an invitation to type, and it should not make you go and find where.
        Editor.Focus();
        var landing = Paragraphs().Skip(first).FirstOrDefault(x => DerivedKind(x) is not NoteBlockKind.Subheading and not NoteBlockKind.Blank);
        if (landing is not null) SetCaret(StartOfText(landing));
        else CaretToEnd();
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
        SetCaret(last?.ContentEnd
            ?? Editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward));
    }

    // ── the caret ──

    /// <summary>
    /// The ONE way to move the caret in code. WPF caches the x that a vertical arrow key aims for,
    /// and does not reset that cache when CaretPosition is set programmatically (dotnet/wpf#3757),
    /// so the first Up or Down after Toki moves the caret lands at the left edge instead of under
    /// the text.
    ///
    /// This used to reach into WPF's internals by reflection to clear the cached value. It worked
    /// in development and did NOTHING in the shipped build: the app publishes self-contained, so it
    /// carries its own PresentationFramework, the private field was not found there, and the catch
    /// swallowed it silently. The harness only caught it once the packaged exe was run.
    ///
    /// So: no internals. Nudging the caret one character with the real editing commands makes WPF
    /// recompute the column from where the caret actually is, which is the same thing the cache
    /// exists to remember. If the round trip cannot return to the exact spot, the position wins and
    /// only the column is lost.
    /// </summary>
    void SetCaret(TextPointer? position)
    {
        if (position is null) return;
        Editor.CaretPosition = position;
        RefreshCaretColumn(position);
    }

    void RefreshCaretColumn(TextPointer position)
    {
        try
        {
            // Right then back, unless there is nothing to the right, in which case left then back.
            if (position.GetNextInsertionPosition(LogicalDirection.Forward) is not null)
            {
                EditingCommands.MoveRightByCharacter.Execute(null, Editor);
                EditingCommands.MoveLeftByCharacter.Execute(null, Editor);
            }
            else if (position.GetNextInsertionPosition(LogicalDirection.Backward) is not null)
            {
                EditingCommands.MoveLeftByCharacter.Execute(null, Editor);
                EditingCommands.MoveRightByCharacter.Execute(null, Editor);
            }

            // The position is the promise; the column is the bonus. If the nudge landed elsewhere
            // (an atomic marker, a document edge), put it back.
            if (Editor.CaretPosition is null || Editor.CaretPosition.CompareTo(position) != 0)
                Editor.CaretPosition = position;
        }
        catch { Editor.CaretPosition = position; }
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
        if (Paragraphs().ElementAtOrDefault(index) is { } p) SetCaret(p.ContentEnd);
    }

    /// <summary>The rendered font size of a line - what the eye actually sees.</summary>
    internal double DebugFontSize(int index) =>
        Paragraphs().ElementAtOrDefault(index) is { } p ? p.FontSize : -1;

    internal void DebugCaretToLineStart()
    {
        if (Paragraphs().LastOrDefault() is { } p) SetCaret(StartOfText(p));
    }

    /// <summary>Where the caret sits horizontally, in editor pixels. This is the number a vertical
    /// arrow key is supposed to keep.</summary>
    internal double DebugCaretX =>
        Editor.CaretPosition?.GetCharacterRect(LogicalDirection.Forward).Left ?? -1;

    /// <summary>Move down one visual line exactly as the Down arrow key does.</summary>
    internal void DebugMoveDownLine() => EditingCommands.MoveDownByLine.Execute(null, Editor);

    /// <summary>Put the caret partway into a line, the way clicking mid-word does - and through the
    /// same code path the editor itself uses when it moves the caret.</summary>
    internal void DebugCaretToOffset(int line, int offset)
    {
        if (Paragraphs().ElementAtOrDefault(line) is not { } p) return;
        SetCaret(StartOfText(p).GetPositionAtOffset(offset, LogicalDirection.Forward) ?? p.ContentEnd);
    }

    internal void DebugEnter() => HandleReturn();
    internal void DebugBackspace() => HandleBackspace();
    internal void DebugToolbar(NoteBlockKind kind) => SetKind(kind);

    /// <summary>Click at a point inside the editor, as a mouse does, and report where the caret
    /// landed (the paragraph index). -1 when it landed nowhere.</summary>
    internal int DebugClickAt(Point pointInEditor)
    {
        var pos = Editor.GetPositionFromPoint(pointInEditor, snapToText: true);
        if (pos is null) return -1;
        SetCaret(pos);
        var paras = Paragraphs().ToList();
        var caretPara = Editor.CaretPosition?.Paragraph;
        return caretPara is null ? -1 : paras.IndexOf(caretPara);
    }

    /// <summary>A point inside a given line, for the click test: a little in from its left edge.</summary>
    internal Point DebugPointInLine(int line)
    {
        if (Paragraphs().ElementAtOrDefault(line) is not { } p) return new Point(-1, -1);
        var r = p.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        return new Point(r.Left + 6, r.Top + r.Height / 2);
    }

    /// <summary>The body size in pixels at the current step. The harness asserts against THIS, not
    /// a literal: pinning the number in two places is how a default change breaks a test that was
    /// never about the default.</summary>
    internal double DebugBodySize => BodySize;
    internal double DebugHintFontSize => Hint.FontSize;

    /// <summary>Every line's kind, in reading order, as the DOCUMENT has it - not as Markdown would
    /// have it. A template's empty writing lines are Body in the document and collapse to Blank the
    /// moment they go through Markdown, so reading the markdown cannot tell you whether the template
    /// handed the user anywhere to type.</summary>
    internal IReadOnlyList<NoteBlockKind> DebugLineKinds() => Paragraphs().Select(DerivedKind).ToList();

    /// <summary>
    /// The PITCH from this line to the next: top of one row to top of the row under it. This, not a
    /// line's own height, is what "even spacing" means to a reader, and it is the only one of the two
    /// that is comparable across kinds - GetCharacterRect returns the CHARACTER's box on a plain line
    /// and the whole line's box on a line carrying a marker, so measuring heights compares two
    /// different things and reports a difference that is not on screen.
    /// </summary>
    internal double DebugLinePitch(int line)
    {
        var ps = Paragraphs().ToList();
        if (line < 0 || line + 1 >= ps.Count) return double.NaN;
        var a = ps[line].ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var b = ps[line + 1].ContentStart.GetCharacterRect(LogicalDirection.Forward);
        return b.Top - a.Top;
    }

    /// <summary>Where a line's marker ink starts, relative to the editor's left edge. All three
    /// markers must agree, or a mixed list has a ragged left margin.</summary>
    internal double DebugMarkerLeft(int line)
    {
        if (Paragraphs().ElementAtOrDefault(line) is not { } p) return double.NaN;
        if (MarkerOf(p)?.Child is not Border host || host.Child is not FrameworkElement marker) return double.NaN;

        // THE INK, not the control. A tick box is a 24px hit pad around an 18px box, so its control
        // starts 3px to the LEFT of where its ink does - measuring the control would report the three
        // markers as aligned at the exact moment their visible edges are not, which is the same
        // mistake that let a clipped marker measure as perfectly centred.
        if (marker is CheckBox box && box.Template?.FindName("Box", box) is FrameworkElement ink) marker = ink;

        try { return marker.TransformToAncestor(Editor).TransformBounds(new Rect(marker.RenderSize)).Left; }
        catch { return double.NaN; }
    }

    internal int DebugCaretLine()
    {
        var paras = Paragraphs().ToList();
        var caretPara = Editor.CaretPosition?.Paragraph;
        return caretPara is null ? -1 : paras.IndexOf(caretPara);
    }

    /// <summary>
    /// How far the marker's INK centre sits from the middle of the text's cap band on the same line,
    /// in pixels. Positive means the marker is below the text's band, negative above. Zero is the
    /// answer; anything past about a pixel is the "bullet and text are not on the same line" the
    /// owner reported. Measured on the live document, because this is a layout fact, not a setting.
    /// </summary>
    internal double DebugMarkerOffset(int line)
    {
        if (Paragraphs().ElementAtOrDefault(line) is not { } p) return double.NaN;
        if (MarkerOf(p)?.Child is not FrameworkElement marker) return double.NaN;
        var textRect = StartOfText(p).GetCharacterRect(LogicalDirection.Forward);
        if (textRect.Height <= 0) return double.NaN;

        Rect inkInEditor;
        try { inkInEditor = marker.TransformToAncestor(Editor).TransformBounds(new Rect(marker.RenderSize)); }
        catch { return double.NaN; }
        if (inkInEditor.Height <= 0) return double.NaN;

        // The text's cap band: from the baseline up by the cap height. GetCharacterRect gives the
        // line box, whose bottom is the descender line, so walk up from the baseline.
        var family = (FontFamily)FindResource("UiFont");
        var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var caps = face.TryGetGlyphTypeface(out var g) ? g.CapsHeight : 0.7;
        var baseline = textRect.Top + family.Baseline * p.FontSize;
        var capCentre = baseline - caps * p.FontSize / 2;

        return (inkInEditor.Top + inkInEditor.Bottom) / 2 - capCentre;
    }

    /// <summary>
    /// How much of a marker falls OUTSIDE the box that holds it, in pixels - zero when the whole
    /// marker is on screen. This is the check the alignment test was missing: for months every
    /// marker was measured as perfectly centred (to a tenth of a pixel) while two thirds of the tick
    /// box and the top of every digit were being clipped away by a host given the text's cap height.
    /// Centred and VISIBLE are different questions and both have to be asked.
    /// </summary>
    internal double DebugMarkerOverflow(int line)
    {
        if (Paragraphs().ElementAtOrDefault(line) is not { } p) return double.NaN;
        if (MarkerOf(p)?.Child is not Border host) return double.NaN;
        if (host.Child is not FrameworkElement marker) return double.NaN;
        if (host.ActualHeight <= 0) return double.NaN;

        // Ask the marker what it needs with NOTHING in its way. Reading DesiredSize as it stands is
        // useless here: the host's constraint has already clamped it, so a marker squeezed into a
        // third of its size reports the squeezed size and the check passes on the very bug it exists
        // to catch. Measure unconstrained, then put the layout back.
        marker.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var need = marker.DesiredSize;
        marker.InvalidateMeasure();
        host.InvalidateMeasure();
        Editor.UpdateLayout();

        if (need.Height <= 0) return double.NaN;
        return Math.Max(0, need.Height - host.ActualHeight);
    }

    // ── slash-menu seams ──
    /// <summary>Press "/" the way the keyboard does, including the guard that a slash only opens
    /// commands at the start of a line.</summary>
    internal bool DebugSlash()
    {
        if (!OpensCommands()) return false;
        OpenSlash();
        DebugType("/");
        return _slash?.IsOpen == true;
    }

    internal bool DebugSlashOpen => _slash?.IsOpen == true;

    /// <summary>Pick a command with the POINTER, not the keyboard.</summary>
    internal bool DebugSlashClick(string label) => _slash?.DebugClickRow(label) == true;

    /// <summary>Type into the open menu's filter, as the key handler does.</summary>
    internal bool DebugSlashType(string text)
    {
        foreach (var c in text)
        {
            if (_slash is null || !_slash.Type(c)) return false;
            DebugType(c.ToString());
        }
        return true;
    }

    /// <summary>Run the highlighted command, exactly as Enter does: drop the typed "/filter" first.</summary>
    internal bool DebugSlashCommit()
    {
        DropSlashText();
        return _slash?.Commit() == true;
    }

    internal string DebugSlashLabels() =>
        string.Join(", ", Commands().Select(c => c.Label));
    internal int DebugLineCount() => Paragraphs().Count();

    internal void DebugTick(int lineIndex, bool on)
    {
        var p = Paragraphs().ElementAtOrDefault(lineIndex);
        if (p is not null && MarkerOf(p)?.Child is Border { Child: CheckBox c }) c.IsChecked = on;
    }
}
