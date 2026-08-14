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
/// actions blue, questions/risks purple - one accent per meaning, per design ⑤); everything
/// else is quiet grey. Text is left-aligned (justify makes rivers in a narrow column).
/// </summary>
public static class NoteRenderer
{
    static object Res(string key) => Application.Current.FindResource(key);
    static Brush B(string key) => (Brush)Res(key);
    static FontFamily Ui => (FontFamily)Res("UiFont");

    // A note is only useful if you can select + copy it. Every piece of note TEXT is a read-only,
    // borderless, transparent TextBox - looks like plain text, but selectable with an I-beam cursor
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

        // The notch is where the note is FIRST seen, and the speakers are being worked out at exactly
        // that moment. Say so here too, or the names appear to arrive from nowhere.
        var resolving = m.Id > 0 && App.IsResolvingSpeakers(m.Id);
        if (resolving && m.Notes.Actions.Count > 0)
            body.Children.Add(new ThinkingDots("working out who owns these"));

        if (m.Transcript.Count > 0)
        {
            SectionHeader(body, "TRANSCRIPT", spacious);
            if (resolving) body.Children.Add(new ThinkingDots("telling the speakers apart"));
            // Show a short PREVIEW (a few lines) - a full hour-long transcript shouldn't just lie
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

    // Section label - small uppercase, well spaced, COLOR-CODED by meaning (design ⑤): decisions green,
    // actions blue, questions/risks purple; everything else quiet grey. Weight = SemiBold, never heavy.
    static void SectionHeader(Panel body, string title, bool spacious, bool first = false) =>
        body.Children.Add(new TextBlock
        {
            Text = title, FontFamily = Ui, FontSize = spacious ? 10.5 : 9.5, FontWeight = FontWeights.SemiBold,
            Foreground = B(HeaderColorKey(title)), Margin = new Thickness(0, first ? 0 : (spacious ? 22 : 17), 0, spacious ? 10 : 8),
        });

    // One meaning -> one accent, the REDESIGN's mapping (figma 329:1907): decisions green, actions
    // teal, open questions pink, transcript amber; everything else quiet.
    static string HeaderColorKey(string title)
    {
        var t = (title ?? "").ToUpperInvariant();
        if (t.Contains("DECISION")) return "Green";
        if (t.Contains("ACTION") || t.Contains("TASK") || t.Contains("TODO") || t.Contains("NEXT STEP")) return "AccentTeal";
        if (t.Contains("QUESTION") || t.Contains("OPEN") || t.Contains("RISK") || t.Contains("BLOCKER")) return "Pink";
        if (t.Contains("TRANSCRIPT")) return "AccentAmber";
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
            // Circular, like the Library pane and the figma notes panel - one tick language everywhere.
            IsChecked = a.Done, Style = (Style)Res("CircleCheck"),
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
                // The resolved name once the speakers have been told apart, else YOU/THEM.
                Text = seg.Label.ToUpperInvariant(), FontSize = 8.5, FontWeight = FontWeights.Bold,
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

    // Name/due chip - a bright accent chip (blue), not the old dim grey that vanished. These are
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
    /// <param name="sticky">The meeting's own sticky note. Its BUTTON goes at the top, under the title;
    /// the note itself opens after everything Toki wrote and BEFORE the transcript - the owner's layout:
    /// "it extends between the end of the summary and everything, and the transcript". Supplied by the
    /// Library (which owns storage); this renderer only positions it.</param>
    /// <param name="renameSpeaker">Called with (oldName, newName) when the user corrects a speaker. Null
    /// (the notch, the render harness) simply omits the speaker row - this renderer never touches storage.</param>
    /// <param name="resolving">True while this meeting's speakers are still being worked out in the
    /// background. The note is COMPLETE and readable either way; this only marks the two parts that are
    /// about to gain names, so they change visibly rather than silently.</param>
    public static FrameworkElement MeetingDocument(MeetingRecord m, bool spacious, FrameworkElement? topBar = null,
        MeetingStickyView? sticky = null, Action<string, string>? renameSpeaker = null, bool resolving = false)
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
        // reads from the very top and the title scrolls WITH the body - and both are selectable as part
        // of the one document (drag-select / Ctrl+C spans title → transcript).
        doc.Blocks.Add(new Paragraph(new Run(m.Title))
        {
            // Big and Bold in the Library pane, the redesign's document title (figma 348:2933 draws it
            // ~30px); the notch keeps the smaller ramp.
            FontSize = spacious ? 26 : 18, FontWeight = spacious ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = B("Text"), Margin = new Thickness(0, 0, 0, spacious ? 6 : 3),
        });
        var meta = MeetingMeta(m);
        if (!string.IsNullOrEmpty(meta))
        {
            // The redesign's meta row: the facts as text, then STATUS CHIPS - "N open" (amber) and
            // "N people" (quiet) - the same capsules the list cards carry, so the two panes speak
            // the same language.
            var mp = new Paragraph { FontSize = 11, Foreground = B("TextDim"), Margin = new Thickness(0, 0, 0, sticky is null ? (spacious ? 20 : 15) : 12) };
            mp.Inlines.Add(new Run(meta));
            var open = m.Notes?.Actions?.Count(a => !a.Done) ?? 0;
            var people = m.Transcript.Select(t => t.Label).Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (open > 0) { mp.Inlines.Add(new Run("   ")); mp.Inlines.Add(new InlineUIContainer(MetaChip($"{open} open", "AccentAmber")) { BaselineAlignment = BaselineAlignment.Center }); }
            if (people >= 2) { mp.Inlines.Add(new Run("  ")); mp.Inlines.Add(new InlineUIContainer(MetaChip($"{people} people", "TextMid")) { BaselineAlignment = BaselineAlignment.Center }); }
            doc.Blocks.Add(mp);
        }

        // "My sticky note" - at the top, under the title, where the owner asked for it. The note it
        // opens is placed further down, after everything Toki wrote.
        if (sticky is not null)
            doc.Blocks.Add(new BlockUIContainer(sticky.ToggleButton) { Margin = new Thickness(0, 0, 0, spacious ? 18 : 14) });

        // ── N7: evidence links ──
        // Every bullet that can be traced to a transcript line gets a small "↳" that jumps there and
        // highlights it. Bullets that CAN'T be traced get nothing - a link to the wrong line would make
        // the note look like it's citing something it isn't. Computed here at render time rather than
        // stored, so it works on notes written before this existed and costs nothing on disk.
        RichTextBox? host = null;          // the box holding the TRANSCRIPT paragraphs
        ScrollViewer? outerScroll = null;  // set on the split layout - the one scroll for all three parts
        var transcriptParas = new Dictionary<int, Paragraph>();
        Action<Action?>? expandTranscript = null;

        void JumpTo(int index)
        {
            void Go()
            {
                if (host is null || !transcriptParas.TryGetValue(index, out var target)) return;
                var rect = target.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                if (outerScroll is not null)
                {
                    // Split layout: the transcript box does not scroll itself, so the paragraph's
                    // position is the box's own offset within the page plus the rect.
                    var top = host.TranslatePoint(new Point(0, rect.Top), (FrameworkElement)outerScroll.Content).Y;
                    outerScroll.ScrollToVerticalOffset(Math.Max(0, top - 70));
                }
                else
                    host.ScrollToVerticalOffset(Math.Max(0, host.VerticalOffset + rect.Top - 70));

                // A brief tint, then back - enough to find the line with your eye, not a permanent mark.
                target.Background = B("Chip");
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
                t.Tick += (_, _) => { t.Stop(); target.Background = Brushes.Transparent; };
                t.Start();
            }

            // The transcript renders lazily; if the line we want hasn't been built yet, fill it in first.
            if (transcriptParas.ContainsKey(index) || expandTranscript is null) Go();
            else expandTranscript(Go);
        }

        // The "↳" that jumps to the line this came from - or null when nothing matches well enough to
        // point at honestly. ONE builder, so a bullet and a to-do can never drift apart.
        Hyperlink? Link(string text)
        {
            if (Evidence.BestSegment(text, m.Transcript) is not { } idx) return null;
            var link = new Hyperlink(new Run(" ↳") { FontSize = fs - 1.5 })
            {
                Foreground = B("TextDim"), TextDecorations = null, Cursor = Cursors.Hand,
                ToolTip = "Show where this came from in the transcript",
            };
            link.Click += (_, _) => JumpTo(idx);
            return link;
        }

        // ...and the play button beside it, when the recording is still on disk.
        //
        // The jump is KEPT rather than replaced. Reading is instant, silent, and works in an open office
        // or a meeting; audio is slower and not always socially possible. They answer the same question
        // at different costs, so the line offers both and lets the reader pick: click the arrow to read
        // it, press play to hear it. Right-click the play button to save the clip.
        //
        // Absent entirely when the audio is gone - a deleted meeting takes its recording with it (H1),
        // and older meetings may never have kept one. Same rule as the link itself: show nothing rather
        // than something that fails when pressed.
        EvidencePlayer? Player(string text)
        {
            // The RANGE, not just the first line - the whole reason a real clip once played a stray
            // "That." while the actual evidence sat unplayed in the next two lines (see Evidence.BestRange).
            if (Evidence.BestRange(text, m.Transcript) is not { } r) return null;
            if (r.First < 0 || r.Last >= m.Transcript.Count) return null;
            var range = new List<TranscriptSegment>();
            for (var i = r.First; i <= r.Last; i++) range.Add(m.Transcript[i]);
            return EvidencePlayer.For(m, range, fs);
        }

        Paragraph WithEvidence(string text, double lineHeight, Thickness margin)
        {
            var p = new Paragraph(new Run(text)) { LineHeight = lineHeight, Margin = margin };
            if (Link(text) is { } link) p.Inlines.Add(link);
            if (Player(text) is { } play)
                p.Inlines.Add(new InlineUIContainer(play) { BaselineAlignment = BaselineAlignment.Center });
            return p;
        }

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
                list.ListItems.Add(new ListItem(WithEvidence(b, fs + 7, new Thickness(0, 0, 0, 7))));
            doc.Blocks.Add(list);
        }
        if (m.Notes.Actions.Count > 0)
        {
            doc.Blocks.Add(DocHeader("ACTION ITEMS"));
            // The to-dos are here and readable; who owns them arrives a few seconds later, when the
            // speakers resolve. Say so, rather than let a name appear out of nowhere.
            if (resolving)
                doc.Blocks.Add(new BlockUIContainer(new ThinkingDots("working out who owns these"))
                { Margin = new Thickness(0, 0, 0, 4) });
            // Action items get evidence links too (S7). A to-do is the single most useful line to be
            // able to check - "who agreed to this, and when?" - and it was the one kind of line the
            // first pass left unlinked.
            foreach (var a in m.Notes.Actions) doc.Blocks.Add(DocAction(a, fs, Link, Player));
        }

        // ── THE SPLIT ──
        //
        // The sticky note is an EDITABLE RichTextBox, and the first shipped version parked it inside a
        // BlockUIContainer of this read-only document. Focus landed in it (the sticky self-test proved
        // that much) and then nothing else worked: no caret, no typing, no selection. The owner's
        // verdict - "the note itself is a flop" - was accurate, because a RichTextBox's text editor
        // claims keyboard and caret services at CLASS level, and a second editor nested inside its
        // document is exactly the thing those services starve. The self-test measured focus and called
        // it health; the product needed keystrokes.
        //
        // So the page is no longer one document when a sticky exists. It is three siblings under ONE
        // scroll: the note document (read-only), the sticky editor (a normal panel child, with no
        // editor wrapped around it), and the transcript document (read-only). Each document's own
        // scrolling is disabled so the outer ScrollViewer is the single scroll, which keeps the page
        // feeling like one surface. The cost is honest: a drag-selection can no longer run THROUGH the
        // sticky from summary to transcript - and that trade is correct, because selection across it
        // was only ever possible while typing into it was not.
        var tdoc = doc;
        if (sticky is not null)
        {
            tdoc = new FlowDocument
            {
                FontFamily = Ui, FontSize = fs, Foreground = B("Text"), Background = Brushes.Transparent,
                PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left,
            };
        }

        if (m.Transcript.Count > 0)
        {
            tdoc.Blocks.Add(DocHeader("TRANSCRIPT"));
            if (resolving)
                tdoc.Blocks.Add(new BlockUIContainer(new ThinkingDots("telling the speakers apart"))
                { Margin = new Thickness(0, 0, 0, 4) });
            // Who Toki thinks was in the room, each one a click away from being corrected. Absent
            // entirely when the speakers were never told apart, so a plain two-party call is unchanged.
            if (renameSpeaker is not null && SpeakerChips.Build(m.Transcript, renameSpeaker) is { } chips)
                tdoc.Blocks.Add(new BlockUIContainer(chips) { Margin = new Thickness(0, 0, 0, 6) });
            var preview = Math.Min(3, m.Transcript.Count);
            for (var i = 0; i < preview; i++)
            {
                var p = DocTranscript(m.Transcript[i], fs, m.Corrections);
                transcriptParas[i] = p;     // so an evidence link can find and highlight it
                tdoc.Blocks.Add(p);
            }
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
                tdoc.Blocks.Add(holder);

                var expanding = false;
                // Shared by the button and by any evidence link that points past the preview - a link
                // has to be able to fill the transcript in before it can scroll to its line.
                void Expand(Action? done)
                {
                    if (expanding) return;
                    expanding = true;

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
                        for (; i < end; i++)
                        {
                            var p = DocTranscript(m.Transcript[i], fs, m.Corrections);
                            transcriptParas[i] = p;
                            tdoc.Blocks.InsertAfter(after, p);
                            after = p;
                        }
                        if (i < m.Transcript.Count)
                            btn.Dispatcher.BeginInvoke(InsertSlice, System.Windows.Threading.DispatcherPriority.Background);
                        else
                        {
                            tdoc.Blocks.Remove(holder);
                            // Let the layout settle before anyone measures a paragraph's position.
                            if (done is not null)
                                btn.Dispatcher.BeginInvoke(done, System.Windows.Threading.DispatcherPriority.Loaded);
                        }
                    }
                    InsertSlice();
                }

                expandTranscript = Expand;
                btn.Click += (_, _) => Expand(null);
            }
        }

        // A READ-ONLY RichTextBox - not a FlowDocumentScrollViewer. This is the one WPF control
        // whose selection is rock-solid: click anywhere and drag and it highlights continuously;
        // Ctrl+A selects, Ctrl+C copies. Read-only blocks editing, not selection.
        RichTextBox MakeReadOnlyBox(FlowDocument d, Thickness padding, ScrollBarVisibility scroll)
        {
            var box = new RichTextBox
            {
                Document = d, IsReadOnly = true, IsReadOnlyCaretVisible = false, IsDocumentEnabled = true,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = padding,
                VerticalScrollBarVisibility = scroll, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Foreground = B("Text"), Cursor = Cursors.IBeam,
            };
            // Never the Win32 right-click menu - we don't ship Windows chrome.
            box.ContextMenuOpening += (_, e) => e.Handled = true;
            return box;
        }

        // No sticky → the original single box, its own scrolling, selection spanning the whole note.
        if (sticky is null)
        {
            // Padding lives INSIDE the box so the scrollbar sits flush at the pane's right edge while
            // text still clears it; top padding gives the toolbar room, left matches the pane inset.
            var rtb = MakeReadOnlyBox(doc, new Thickness(28, 20, 16, 18), ScrollBarVisibility.Auto);
            host = rtb;   // evidence links scroll THIS box
            return rtb;
        }

        // Sticky → three siblings under one scroll (see THE SPLIT above). The two documents disable
        // their own scrolling and grow to content, so the ScrollViewer is the only scroll on the page.
        var notesBox = MakeReadOnlyBox(doc, new Thickness(0, 0, 0, 0), ScrollBarVisibility.Disabled);
        var transcriptBox = MakeReadOnlyBox(tdoc, new Thickness(0, 0, 0, 0), ScrollBarVisibility.Disabled);
        host = transcriptBox;

        var page = new StackPanel();
        page.Children.Add(notesBox);
        page.Children.Add(sticky);
        sticky.Margin = new Thickness(0, 14, 0, 0);
        page.Children.Add(transcriptBox);
        transcriptBox.Margin = new Thickness(0, 6, 0, 0);

        var sv = new ScrollViewer
        {
            Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(28, 20, 16, 18),
        };
        outerScroll = sv;

        // A RichTextBox whose scrolling is DISABLED still eats the mouse wheel - its internal
        // ScrollViewer marks the event handled and goes nowhere - so the page freezes wherever the
        // pointer sits over text, which is almost everywhere. Re-route the wheel to the outer scroll.
        void ForwardWheel(RichTextBox from) => from.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        };
        ForwardWheel(notesBox);
        ForwardWheel(transcriptBox);

        return sv;
    }

    /// <summary>The plain text a full top-to-bottom select-all of the note document yields -
    /// the real artifact the copy feature must produce. Used by the Copy button and --copytest.</summary>
    public static string FullText(MeetingRecord m)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(NotesText(m));
        if (m.Transcript.Count > 0)
            sb.AppendLine().AppendLine().AppendLine("Transcript").Append(TranscriptText(m));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Duration · full date/time · app - the note's meta line, sitting under the title.</summary>
    static string MeetingMeta(MeetingRecord m) => TimeText.FullMeta(m);

    /// <summary>
    /// A section header in the redesign's shape (figma 349:6743 / 329:1907): a small round icon chip
    /// in the section's colour, the label beside it in the same colour, and the section content
    /// carrying a thin left accent rule. One meaning, one colour, one icon - the colour code from
    /// HeaderColorKey, now with the glyph the design pairs with each meaning.
    /// </summary>
    static Paragraph DocHeader(string title, bool first = false)
    {
        var key = HeaderColorKey(title);
        var accent = B(key == "TextDim" ? "TextMid" : key);
        var glyph = SectionGlyph(title);

        var icon = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
            Background = Transparent(accent, 0x2E), Margin = new Thickness(0, 0, 7, 0),
            Child = new TextBlock
            {
                Text = glyph, FontFamily = (FontFamily)Res("IconFont"), FontSize = 9.5, Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var p = new Paragraph { Margin = new Thickness(0, first ? 0 : 22, 0, 8) };
        p.Inlines.Add(new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center });
        p.Inlines.Add(new Run(title)
        {
            FontWeight = FontWeights.Bold, Foreground = accent, FontSize = 10,
            BaselineAlignment = BaselineAlignment.Center,
        });
        return p;
    }

    /// <summary>The design's icon per section meaning (Fluent glyphs standing in for the lucide set).</summary>
    static string SectionGlyph(string title)
    {
        var t = (title ?? "").ToUpperInvariant();
        if (t.Contains("DECISION")) return "";                                   // check
        if (t.Contains("ACTION") || t.Contains("TASK") || t.Contains("TODO")) return "";  // checklist
        if (t.Contains("QUESTION") || t.Contains("OPEN")) return "";             // question
        if (t.Contains("TRANSCRIPT")) return "";                                  // speech
        return "";                                                                // list
    }

    /// <summary>An outlined status capsule for the meta row ("2 open" / "3 people") - the redesign's
    /// chip: tinted fill, tinted 1px rim, accent text.</summary>
    static Border MetaChip(string text, string accentKey)
    {
        var accent = B(accentKey);
        return new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1.5, 7, 1.5),
            Background = Transparent(accent, 0x1A), BorderBrush = Transparent(accent, 0x4D), BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text, FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                Foreground = accent, FontFamily = Ui,
            },
        };
    }

    /// <summary>The redesign's transcript avatar: a small circle with the speaker's initial in a colour
    /// derived from the name (never fetched). YOU reads brighter than everyone else. Full name on hover.</summary>
    static Border Avatar(string label, bool you)
    {
        var key = you ? "Text" : SpeakerAccents[StableHash(label) % SpeakerAccents.Length];
        var accent = B(key);
        var initial = string.IsNullOrWhiteSpace(label) ? "?" : char.ToUpperInvariant(label.Trim()[0]).ToString();
        return new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
            Background = Transparent(accent, you ? (byte)0x30 : (byte)0x33),
            ToolTip = label, Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = initial, FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = accent,
                FontFamily = Ui, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    static readonly string[] SpeakerAccents = { "Pink", "AccentTeal", "Blue", "Green", "Purple", "AccentAmber" };

    // Stable across processes - GetHashCode is randomized per run and would recolour every speaker
    // on every launch (the StableHash lesson from the card accents).
    static int StableHash(string s)
    {
        unchecked
        {
            int h = 5381;
            foreach (var c in s.ToUpperInvariant()) h = (h << 5) + h + c;
            return h < 0 ? -h : h;
        }
    }

    /// <summary>The section accent at a chip-tint alpha - the round icon chip's fill.</summary>
    static Brush Transparent(Brush accent, byte alpha)
    {
        if (accent is SolidColorBrush s)
        {
            var c = s.Color;
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
        return accent;
    }

    static Paragraph DocAction(ActionItem a, double fs, Func<string, Hyperlink?>? link = null,
        Func<string, EvidencePlayer?>? player = null)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 11), LineHeight = fs + 8 };
        // The redesign's CIRCULAR tick (hollow ring → filled teal disc), and a real strikethrough on
        // done items - a FlowDocument Run CAN strike through, where the notch's TextBox cannot.
        var check = new CheckBox { IsChecked = a.Done, Style = (Style)Res("CircleCheck"), Margin = new Thickness(0, 0, 9, 0) };
        check.Checked += (_, _) => Toggle(a, true);
        check.Unchecked += (_, _) => Toggle(a, false);
        p.Inlines.Add(new InlineUIContainer(check) { BaselineAlignment = BaselineAlignment.TextTop });
        var text = new Run(a.Text) { Foreground = a.Done ? B("TextDim") : B("Text") };
        if (a.Done) text.TextDecorations = TextDecorations.Strikethrough;
        p.Inlines.Add(text);
        // The task text alone is what's searched for in the transcript - the owner/due chips are Toki's
        // formatting, not words anybody said, and feeding them in only dilutes the match.
        if (link?.Invoke(a.Text) is { } jump) p.Inlines.Add(jump);
        // A to-do is the line people most want to check - "who agreed to this?" - so it is the line
        // that most deserves being able to HEAR the moment, not just read it.
        if (player?.Invoke(a.Text) is { } play)
            p.Inlines.Add(new InlineUIContainer(play) { BaselineAlignment = BaselineAlignment.Center });
        // Owner / due ride BELOW the item as capsule chips (figma 348:2933: "Priya" "Tomorrow" under the
        // text) - accent text keeps them readable (person = blue, due = amber). The Copy BUTTON still
        // captures them: its text comes from the record (NotesText), not from this rendering.
        if (Meaningful(a.Owner) || Meaningful(a.Due))
        {
            p.Inlines.Add(new LineBreak());
            // Indented past the 24px check + its 9px gap so the chips sit under the TEXT, as the frame draws.
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(33, 2, 0, 0) };
            if (Meaningful(a.Owner)) { chips.Children.Add(MetaChip(a.Owner!, "Blue")); chips.Children.Add(new Border { Width = 6 }); }
            if (Meaningful(a.Due)) chips.Children.Add(MetaChip(a.Due!, "Amber"));
            p.Inlines.Add(new InlineUIContainer(chips) { BaselineAlignment = BaselineAlignment.Center });
        }
        return p;
    }

    static Paragraph DocTranscript(TranscriptSegment seg, double fs,
        IReadOnlyList<TextFix>? corrections = null)
    {
        var you = seg.Speaker == Speaker.You;
        var fg = B(you ? "Text" : "TextDim");
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 9), LineHeight = fs + 7 };
        // The redesign's transcript line: a small initial-avatar circle, then the words (figma
        // 348:2933 - "P" / "S" chips). The full name is on the avatar's hover; the NAME run stays
        // too when speakers are actually named, because a letter alone is ambiguous in a 4-person
        // call where two people share an initial - and drag-select/Ctrl+C should still capture who
        // spoke. YOU/THEM placeholders stay as text only (no avatar letter says less than "THEM").
        p.Inlines.Add(new InlineUIContainer(Avatar(seg.Label, you)) { BaselineAlignment = BaselineAlignment.Center });
        p.Inlines.Add(new Run($"{seg.Label}:  ") { FontWeight = FontWeights.SemiBold, Foreground = fg });
        foreach (var run in MarkCorrections(seg.Text, fg, corrections)) p.Inlines.Add(run);
        return p;
    }

    /// <summary>
    /// The transcript, with Toki's own edits visible.
    ///
    /// Toki quietly rewrites two things: names it unified against the library, and the discarded half
    /// of a spoken self-correction. Both make the transcript easier to read and both make it a slightly
    /// edited record of what a person said. So the changed words carry a dotted underline and the
    /// original on hover - the owner's rule is that this shows in the TRANSCRIPT only; the notes stay
    /// clean, because a note peppered with hover marks is harder to read and the transcript is where
    /// anyone goes to check a claim.
    ///
    /// The mark is honest at meeting level rather than occurrence level: Toki stores the pairs it
    /// changed, not which instance it changed, so the wording is "also transcribed as" - true of every
    /// occurrence - instead of claiming this particular word was wrong.
    /// </summary>
    static IEnumerable<Run> MarkCorrections(string? text, Brush fg, IReadOnlyList<TextFix>? corrections)
    {
        text ??= "";
        if (corrections is null || corrections.Count == 0 || text.Length == 0)
            return [new Run(text) { Foreground = fg }];

        // One pattern per render of a line is cheap; the alternation is at most a handful of words.
        var words = corrections
            .Where(f => !string.IsNullOrWhiteSpace(f.To))
            .GroupBy(f => f.To, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (words.Count == 0) return [new Run(text) { Foreground = fg }];

        var pattern = @"\b(?:" + string.Join("|", words.Keys.Select(System.Text.RegularExpressions.Regex.Escape)) + @")\b";
        var runs = new List<Run>();
        var at = 0;
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(text, pattern,
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            if (match.Index > at) runs.Add(new Run(text[at..match.Index]) { Foreground = fg });
            var fix = words[match.Value];
            runs.Add(new Run(match.Value)
            {
                Foreground = fg,
                TextDecorations = Dotted(fg),
                ToolTip = fix.Reason == "repair"
                    ? $"Self-corrected from \"{fix.From}\""
                    : $"Also transcribed as \"{fix.From}\"",
            });
            at = match.Index + match.Length;
        }
        if (at < text.Length) runs.Add(new Run(text[at..]) { Foreground = fg });
        return runs.Count == 0 ? [new Run(text) { Foreground = fg }] : runs;
    }

    /// <summary>A dotted underline in the line's own colour - present enough to notice while reading,
    /// quiet enough not to look like an error.</summary>
    static TextDecorationCollection Dotted(Brush fg)
    {
        var pen = new Pen(fg.Clone(), 1) { DashStyle = new DashStyle([1, 2], 0) };
        pen.Brush.Opacity = 0.55;
        pen.Freeze();
        var d = new TextDecorationCollection
        {
            new TextDecoration { Pen = pen, PenOffset = 1.5, PenOffsetUnit = TextDecorationUnit.Pixel },
        };
        d.Freeze();
        return d;
    }

    /// <summary>Plain-text of a meeting's transcript, for the Copy button.</summary>
    public static string TranscriptText(MeetingRecord m) =>
        string.Join(Environment.NewLine, m.Transcript.Select(s => $"{s.Label}: {s.Text}"));

    /// <summary>Plain-text of the NOTES (title, summary, template sections, to-dos) for the Copy menu -
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
