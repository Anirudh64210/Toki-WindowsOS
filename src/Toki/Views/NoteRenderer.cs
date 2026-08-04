using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Toki.Storage;
using Toki.Transcription;

namespace Toki.Views;

/// <summary>
/// Builds a meeting's notes (Summary / Key points / Action items / Transcript) into a
/// panel, shared by the pill Notes view and the Library window. Clean, generous, consistent
/// spacing so it reads calm. Section labels are colour-CODED by meaning (decisions green,
/// actions blue, questions/risks purple — one accent per meaning, per design ⑤); everything
/// else is quiet grey. Text is left-aligned (justify makes rivers in a narrow column).
/// </summary>
public static class NoteRenderer
{
    static object Res(string key) => Application.Current.FindResource(key);
    static Brush B(string key) => (Brush)Res(key);
    static FontFamily Ui => (FontFamily)Res("UiFont");

    // A note is only useful if you can select + copy it. Every piece of note TEXT is a read-only,
    // borderless, transparent TextBox — looks like plain text, but selectable with an I-beam cursor
    // and Ctrl+C. (Bulk copy is the Copy menu; this is for grabbing a line or a phrase.)
    static TextBox Sel(string text, double fs, Brush fg, TextAlignment align = TextAlignment.Left)
    {
        var tb = new TextBox
        {
            Text = text ?? "", IsReadOnly = true, IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = fg, FontFamily = Ui, FontSize = fs, TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(0), Margin = new Thickness(0), TextAlignment = align,
            Cursor = System.Windows.Input.Cursors.IBeam, IsTabStop = false,
            VerticalContentAlignment = VerticalAlignment.Top, FontStretch = FontStretches.Normal,
        };
        return tb;
    }

    static Button ShowFullButton(string text) => new()
    {
        Content = text, Style = (Style)Res("PillButton"), Background = B("Chip"), Foreground = B("TextDim"),
        Padding = new Thickness(13, 7, 13, 7), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 2, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
    };

    public static void Meeting(Panel body, MeetingRecord m, bool spacious)
    {
        body.Children.Clear();
        var fs = spacious ? 13.5 : 12.0;

        // Generation guard: if this panel is re-rendered for another meeting, any pending
        // deferred transcript work for the old one stops (checks body.Tag still == token).
        var token = new object();
        body.Tag = token;

        if (!string.IsNullOrWhiteSpace(m.Notes.Tldr))
        {
            SectionHeader(body, "SUMMARY", spacious, first: true);
            // Justified like the bullets/actions below it, so the summary reads as a clean block, not ragged.
            var s = Sel(m.Notes.Tldr, fs, B("TextSoft"));
            s.Margin = new Thickness(0, 0, 0, 2);
            body.Children.Add(s);
        }

        // Template middle sections (standup → Updates/Blockers, etc.), unified with legacy notes.
        foreach (var sec in m.Notes.EffectiveSections())
            Section(body, sec.Heading.ToUpperInvariant(), sec.Bullets.Select(b => Bullet(b, fs, spacious)), spacious);
        Section(body, "ACTION ITEMS", m.Notes.Actions.Select(a => ActionRow(a, fs, spacious)), spacious);

        if (m.Transcript.Count > 0)
        {
            SectionHeader(body, "TRANSCRIPT", spacious);
            // Show a short PREVIEW (a few lines) — a full hour-long transcript shouldn't just lie
            // there. A "Show full transcript" button reveals the rest on demand (streamed in so it
            // never freezes the window). Most of the time the summary + tasks are all you want.
            var preview = Math.Min(3, m.Transcript.Count);
            for (var i = 0; i < preview; i++) body.Children.Add(TranscriptRow(m.Transcript[i], fs, spacious));
            if (m.Transcript.Count > preview)
            {
                var more = m.Transcript.Count - preview;
                var btn = ShowFullButton($"Show full transcript  ·  {more} more line{(more == 1 ? "" : "s")}");
                body.Children.Add(btn);
                btn.Click += (_, _) =>
                {
                    if (!ReferenceEquals(body.Tag, token)) return;
                    body.Children.Remove(btn);
                    RenderTranscriptChunked(body, m.Transcript, fs, spacious, token, preview);
                };
            }
        }
    }

    // First batch is small enough to feel instant; later batches are larger and yield to
    // input between them so scrolling/clicking stays responsive while it fills.
    static void RenderTranscriptChunked(Panel body, IReadOnlyList<TranscriptSegment> segs,
        double fs, bool spacious, object token, int start)
    {
        var batch = start == 0 ? 25 : 80;
        var end = Math.Min(start + batch, segs.Count);
        for (var i = start; i < end; i++) body.Children.Add(TranscriptRow(segs[i], fs, spacious));
        if (end >= segs.Count) return;
        body.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(body.Tag, token)) return; // panel reused for another meeting
            RenderTranscriptChunked(body, segs, fs, spacious, token, end);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    static void Section(Panel body, string title, IEnumerable<FrameworkElement> rows, bool spacious)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        SectionHeader(body, title, spacious);
        foreach (var r in list) body.Children.Add(r);
    }

    // Section label — small uppercase, well spaced, COLOR-CODED by meaning (design ⑤): decisions green,
    // actions blue, questions/risks purple; everything else quiet grey. Weight = SemiBold, never heavy.
    static void SectionHeader(Panel body, string title, bool spacious, bool first = false) =>
        body.Children.Add(new TextBlock
        {
            Text = title, FontFamily = Ui, FontSize = spacious ? 10.5 : 9.5, FontWeight = FontWeights.SemiBold,
            Foreground = B(HeaderColorKey(title)), Margin = new Thickness(0, first ? 0 : (spacious ? 22 : 17), 0, spacious ? 10 : 8),
        });

    // One meaning → one accent (green=decided, blue=action, purple=open question/risk), else quiet grey.
    static string HeaderColorKey(string title)
    {
        var t = (title ?? "").ToUpperInvariant();
        if (t.Contains("DECISION")) return "Green";
        if (t.Contains("ACTION") || t.Contains("TASK") || t.Contains("TODO") || t.Contains("NEXT STEP")) return "Blue";
        if (t.Contains("QUESTION") || t.Contains("OPEN") || t.Contains("RISK") || t.Contains("BLOCKER")) return "Purple";
        return "TextDim";
    }

    static FrameworkElement Bullet(string text, double fs, bool spacious)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, spacious ? 10 : 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dot = new Ellipse
        {
            Width = 3.5, Height = 3.5, Fill = B("TextDim"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, fs / 2, 12, 0),
        };
        var tb = Sel(text, fs, B("Text"));
        Grid.SetColumn(tb, 1);
        row.Children.Add(dot);
        row.Children.Add(tb);
        return row;
    }

    static FrameworkElement ActionRow(ActionItem a, double fs, bool spacious)
    {
        var check = new CheckBox
        {
            IsChecked = a.Done, Style = (Style)Res("TokiCheck"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1, 1, 11, 0),
        };
        check.Checked += (_, _) => Toggle(a, true);
        check.Unchecked += (_, _) => Toggle(a, false);

        var text = Sel(a.Text, fs, B("Text"));
        if (a.Done) text.Opacity = 0.5;   // done = dimmed (a TextBox can't strike-through, and it stays selectable)

        var right = new StackPanel();
        right.Children.Add(text);
        var tags = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        if (Meaningful(a.Owner)) tags.Children.Add(Tag(a.Owner!));
        if (Meaningful(a.Due)) tags.Children.Add(Tag(a.Due!));
        if (tags.Children.Count > 0) right.Children.Add(tags);

        // A Grid, NOT a horizontal StackPanel: a StackPanel gives the text child INFINITE width, so a
        // long task never wraps and runs off the right edge (you couldn't scroll to see the end). The
        // star column constrains it so it wraps within the notch.
        var row = new Grid { Margin = new Thickness(0, 0, 0, spacious ? 12 : 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        row.Children.Add(check);
        row.Children.Add(right);
        return row;
    }

    static FrameworkElement TranscriptRow(TranscriptSegment seg, double fs, bool spacious)
    {
        // Clean, consistent, generously-spaced turns. Monotone speaker chip on the left
        // (YOU a touch brighter than THEM), the line flowing beside it, left-aligned so it
        // reads like a clear script rather than a cramped two-sided chat.
        var you = seg.Speaker == Speaker.You;
        var chip = new Border
        {
            Background = B(you ? "SurfaceHi" : "Chip"), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, fs / 2 - 7, 10, 0), MinWidth = 40, HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = you ? "YOU" : "THEM", FontSize = 8.5, FontWeight = FontWeights.Bold,
                Foreground = B(you ? "Text" : "TextDim"), FontFamily = Ui, TextAlignment = TextAlignment.Center,
            },
        };
        var text = Sel(seg.Text, fs, B(you ? "Text" : "TextDim"));

        var row = new Grid { Margin = new Thickness(0, 0, 0, spacious ? 14 : 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        row.Children.Add(chip);
        row.Children.Add(text);
        return row;
    }

    // Old rows may carry placeholder junk ("->", "-", "TBD") the model left behind.
    static bool Meaningful(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        var s = v.Trim().Trim('<', '>', '(', ')', '[', ']').Trim().ToLowerInvariant();
        return s.Length > 1 && s is not ("--" or "->" or "n/a" or "na" or "none"
            or "tbd" or "tba" or "unknown" or "unassigned" or "name" or "when");
    }

    // Name/due chip — a bright accent chip (blue), not the old dim grey that vanished. These are
    // important labels, so they read clearly on both themes (accent brushes swap per theme).
    static Border Tag(string text) => new()
    {
        Background = B("BlueDark"), CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 2),
        Margin = new Thickness(0, 0, 6, 0),
        Child = new TextBlock { Text = text, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = B("Blue"), FontFamily = Ui },
    };

    static void Toggle(ActionItem a, bool done)
    {
        a.Done = done;
        if (a.Id > 0) try { App.Store.SetActionDone(a.Id, done); } catch { }
    }

    // ══ Selectable document version (Library note view) ══
    // The WHOLE note is ONE FlowDocument, so a single drag-select spans summary → bullets → tasks →
    // transcript and Ctrl+C copies all of it. (Separate TextBoxes only let you select within one.)
    // Checkboxes stay clickable inside the read-only document (FlowDocumentScrollViewer allows it).
    public static FrameworkElement MeetingDocument(MeetingRecord m, bool spacious, FrameworkElement? topBar = null)
    {
        var fs = spacious ? 13.5 : 12.5;
        var doc = new FlowDocument
        {
            FontFamily = Ui, FontSize = fs, Foreground = B("Text"), Background = Brushes.Transparent,
            PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left,
        };

        // The action toolbar (Copy/Delete/Template) rides at the very top of the SAME scroll, so the
        // scrollbar runs the full pane height from the top instead of starting below a pinned bar.
        if (topBar is not null)
            doc.Blocks.Add(new BlockUIContainer(topBar) { Margin = new Thickness(0, 0, 0, 14) });

        // Title + date/time live at the TOP of the scroll (not a pinned header above it), so the note
        // reads from the very top and the title scrolls WITH the body — and both are selectable as part
        // of the one document (drag-select / Ctrl+C spans title → transcript).
        doc.Blocks.Add(new Paragraph(new Run(m.Title))
        {
            FontSize = spacious ? 22 : 18, FontWeight = FontWeights.Bold, Foreground = B("Text"),
            Margin = new Thickness(0, 0, 0, 3),
        });
        var meta = MeetingMeta(m);
        if (!string.IsNullOrEmpty(meta))
            doc.Blocks.Add(new Paragraph(new Run(meta)) { FontSize = 11, Foreground = B("TextDim"), Margin = new Thickness(0, 0, 0, spacious ? 20 : 15) });

        if (!string.IsNullOrWhiteSpace(m.Notes.Tldr))
        {
            doc.Blocks.Add(DocHeader("SUMMARY", first: true));
            doc.Blocks.Add(new Paragraph(new Run(m.Notes.Tldr)) { Foreground = B("TextSoft"), LineHeight = fs + 9, TextAlignment = TextAlignment.Left, Margin = new Thickness(0, 0, 0, 2) });
        }
        foreach (var sec in m.Notes.EffectiveSections())
        {
            if (sec.Bullets.Count == 0) continue;
            doc.Blocks.Add(DocHeader(sec.Heading.ToUpperInvariant()));
            var list = new List { MarkerStyle = TextMarkerStyle.Disc, Padding = new Thickness(15, 0, 0, 0), Margin = new Thickness(0, 0, 0, 2) };
            foreach (var b in sec.Bullets)
                list.ListItems.Add(new ListItem(new Paragraph(new Run(b)) { LineHeight = fs + 7, Margin = new Thickness(0, 0, 0, 7) }));
            doc.Blocks.Add(list);
        }
        if (m.Notes.Actions.Count > 0)
        {
            doc.Blocks.Add(DocHeader("ACTION ITEMS"));
            foreach (var a in m.Notes.Actions) doc.Blocks.Add(DocAction(a, fs));
        }
        if (m.Transcript.Count > 0)
        {
            doc.Blocks.Add(DocHeader("TRANSCRIPT"));
            var preview = Math.Min(3, m.Transcript.Count);
            for (var i = 0; i < preview; i++) doc.Blocks.Add(DocTranscript(m.Transcript[i], fs));
            if (m.Transcript.Count > preview)
            {
                var more = m.Transcript.Count - preview;
                var btn = new Button
                {
                    Content = $"Show full transcript  ·  {more} more line{(more == 1 ? "" : "s")}", Style = (Style)Res("PillButton"),
                    Background = B("Chip"), Foreground = B("TextDim"), Padding = new Thickness(13, 7, 13, 7), FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand,
                };
                var holder = new BlockUIContainer(btn) { Margin = new Thickness(0, 3, 0, 0) };
                doc.Blocks.Add(holder);
                btn.Click += (_, _) =>
                {
                    // CHUNKED expansion (the panel path pioneered this; the FlowDocument path had
                    // dropped it): a long meeting is thousands of paragraphs, and inserting them
                    // all synchronously froze the UI for seconds. Insert in slices, yielding to
                    // the dispatcher between them, so the document stays responsive while it fills.
                    btn.IsEnabled = false;
                    const int slice = 150;
                    var i = preview;
                    Block after = holder;
                    void InsertSlice()
                    {
                        var end = Math.Min(i + slice, m.Transcript.Count);
                        for (; i < end; i++) { var p = DocTranscript(m.Transcript[i], fs); doc.Blocks.InsertAfter(after, p); after = p; }
                        if (i < m.Transcript.Count)
                            btn.Dispatcher.BeginInvoke(InsertSlice, System.Windows.Threading.DispatcherPriority.Background);
                        else
                            doc.Blocks.Remove(holder);
                    }
                    InsertSlice();
                };
            }
        }

        // A READ-ONLY RichTextBox — not a FlowDocumentScrollViewer. This is the one WPF control
        // whose selection is rock-solid: click anywhere and drag from the very top to the very
        // bottom and it highlights continuously across summary → bullets → to-dos → transcript;
        // Ctrl+A selects the whole note, Ctrl+C copies it. Read-only blocks editing, not selection.
        var rtb = new RichTextBox
        {
            Document = doc, IsReadOnly = true, IsReadOnlyCaretVisible = false, IsDocumentEnabled = true,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            // Padding lives INSIDE the box so the scrollbar sits flush at the pane's right edge while text
            // still clears it; top padding gives the toolbar room, left matches the old pane inset.
            Padding = new Thickness(28, 20, 16, 18),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Foreground = B("Text"), Cursor = Cursors.IBeam,
        };
        // Never the Win32 right-click menu — we don't ship Windows chrome. Ctrl+C / the Copy button do the copying.
        rtb.ContextMenuOpening += (_, e) => e.Handled = true;
        return rtb;
    }

    /// <summary>The plain text a full top-to-bottom select-all of the note document yields —
    /// the real artifact the copy feature must produce. Used by the Copy button and --copytest.</summary>
    public static string FullText(MeetingRecord m)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(NotesText(m));
        if (m.Transcript.Count > 0)
            sb.AppendLine().AppendLine().AppendLine("Transcript").Append(TranscriptText(m));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Duration · full date/time · app — the note's meta line, sitting under the title.</summary>
    static string MeetingMeta(MeetingRecord m) => TimeText.FullMeta(m);

    static Paragraph DocHeader(string title, bool first = false) =>
        new(new Run(title) { FontWeight = FontWeights.SemiBold, Foreground = B("TextDim"), FontSize = 10.5 })
        { Margin = new Thickness(0, first ? 0 : 20, 0, 8) };

    static Paragraph DocAction(ActionItem a, double fs)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 11), LineHeight = fs + 8 };
        var check = new CheckBox { IsChecked = a.Done, Style = (Style)Res("TokiCheck"), Margin = new Thickness(0, 0, 9, 0) };
        check.Checked += (_, _) => Toggle(a, true);
        check.Unchecked += (_, _) => Toggle(a, false);
        p.Inlines.Add(new InlineUIContainer(check) { BaselineAlignment = BaselineAlignment.TextTop });
        p.Inlines.Add(new Run(a.Text) { Foreground = a.Done ? B("TextDim") : B("Text") });
        // Owner / due are IMPORTANT labels — bright, bold, accent-coloured (person = blue, due = amber)
        // so they stand out, with clear spacing. Kept as TEXT (not UI chips) so Copy still captures them.
        if (Meaningful(a.Owner)) p.Inlines.Add(new Run($"   ·   {a.Owner}") { Foreground = B("Blue"), FontWeight = FontWeights.SemiBold });
        if (Meaningful(a.Due)) p.Inlines.Add(new Run($"   ·   {a.Due}") { Foreground = B("Amber"), FontWeight = FontWeights.SemiBold });
        return p;
    }

    static Paragraph DocTranscript(TranscriptSegment seg, double fs)
    {
        var you = seg.Speaker == Speaker.You;
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 9), LineHeight = fs + 7 };
        p.Inlines.Add(new Run($"{(you ? "You" : "Them")}:  ") { FontWeight = FontWeights.SemiBold, Foreground = B(you ? "Text" : "TextDim") });
        p.Inlines.Add(new Run(seg.Text) { Foreground = B(you ? "Text" : "TextDim") });
        return p;
    }

    /// <summary>Plain-text of a meeting's transcript, for the Copy button.</summary>
    public static string TranscriptText(MeetingRecord m) =>
        string.Join(Environment.NewLine, m.Transcript.Select(s =>
            $"{(s.Speaker == Speaker.You ? "You" : "Them")}: {s.Text}"));

    /// <summary>Plain-text of the NOTES (title, summary, template sections, to-dos) for the Copy menu —
    /// clean markdown-ish text the user can paste anywhere.</summary>
    public static string NotesText(MeetingRecord m)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(m.Title).AppendLine();
        if (!string.IsNullOrWhiteSpace(m.Notes.Tldr)) sb.AppendLine("Summary").AppendLine(m.Notes.Tldr).AppendLine();
        foreach (var sec in m.Notes.EffectiveSections())
        {
            if (sec.Bullets.Count == 0) continue;
            sb.AppendLine(sec.Heading);
            foreach (var b in sec.Bullets) sb.AppendLine($"- {b}");
            sb.AppendLine();
        }
        if (m.Notes.Actions.Count > 0)
        {
            sb.AppendLine("To do");
            foreach (var a in m.Notes.Actions)
            {
                var meta = new[] { a.Owner, a.Due }.Where(x => Meaningful(x));
                sb.AppendLine($"- [{(a.Done ? "x" : " ")}] {a.Text}" + (meta.Any() ? $" ({string.Join(", ", meta)})" : ""));
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
