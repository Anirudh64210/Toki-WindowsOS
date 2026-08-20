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
/// Builds a meeting's notes (Summary / Key points / Action items / Transcript), shared by the pill
/// Notes view and the Library window. Section labels are colour-coded by meaning (one accent per
/// meaning). Text is left-aligned: justify makes rivers in a narrow column.
/// </summary>
public static class NoteRenderer
{
    static object Res(string key) => Application.Current.FindResource(key);
    static Brush B(string key) => (Brush)Res(key);
    static FontFamily Ui => (FontFamily)Res("UiFont");

    // One meaning -> one accent: decisions green, actions teal, open questions pink,
    // transcript amber; everything else quiet.
    static string HeaderColorKey(string title)
    {
        var t = (title ?? "").ToUpperInvariant();
        if (t.Contains("DECISION")) return "Green";
        if (t.Contains("ACTION") || t.Contains("TASK") || t.Contains("TODO") || t.Contains("NEXT STEP")) return "AccentTeal";
        if (t.Contains("QUESTION") || t.Contains("OPEN") || t.Contains("RISK") || t.Contains("BLOCKER")) return "Pink";
        if (t.Contains("TRANSCRIPT")) return "AccentAmber";
        return "TextDim";
    }

    // Old rows may carry placeholder junk ("->", "-", "TBD") the model left behind.
    static bool Meaningful(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        var s = v.Trim().Trim('<', '>', '(', ')', '[', ']').Trim().ToLowerInvariant();
        return s.Length > 1 && s is not ("--" or "->" or "n/a" or "na" or "none"
            or "tbd" or "tba" or "unknown" or "unassigned" or "name" or "when");
    }

    static void Toggle(ActionItem a, bool done)
    {
        a.Done = done;
        if (a.Id > 0) try { App.Store.SetActionDone(a.Id, done); } catch { }
    }

    // The WHOLE note is ONE FlowDocument, so a single drag-select spans summary to transcript.
    // Separate TextBoxes only let you select within one. Checkboxes stay clickable in the read-only document.
    /// <param name="sticky">The meeting's sticky note. Its button goes at the top; the note itself
    /// opens after everything Toki wrote and before the transcript. Supplied by the Library
    /// (which owns storage); this renderer only positions it.</param>
    /// <param name="renameSpeaker">Called with (oldName, newName) when the user corrects a speaker.
    /// Null omits the speaker row; this renderer never touches storage.</param>
    /// <param name="resolving">True while speakers are still resolving in the background; marks the
    /// two parts about to gain names, so they change visibly rather than silently.</param>
    /// <param name="scrollHost">The caller's own page ScrollViewer. When given, the box is EMBEDDED:
    /// no title/meta block, no padding, no scrollbar of its own; the host scrolls it.</param>
    public static FrameworkElement MeetingDocument(MeetingRecord m, bool spacious, FrameworkElement? topBar = null,
        MeetingStickyView? sticky = null, Action<string, string>? renameSpeaker = null, bool resolving = false,
        ScrollViewer? scrollHost = null)
    {
        var embedded = scrollHost is not null;
        var fs = spacious ? 13.5 : 12.5;
        var doc = new FlowDocument
        {
            FontFamily = Ui, FontSize = fs, Foreground = B("Text"), Background = Brushes.Transparent,
            PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left,
        };

        // The action toolbar rides at the top of the SAME scroll, so the scrollbar runs the full
        // pane height instead of starting below a pinned bar.
        if (topBar is not null)
            doc.Blocks.Add(new BlockUIContainer(topBar) { Margin = new Thickness(0, 0, 0, 14) });

        // Title + date/time live at the TOP of the scroll (not a pinned header), so the title scrolls
        // WITH the body and both are selectable as part of the one document. An embedded box skips
        // them: the notch page shows both in its own header.
        if (!embedded)
        doc.Blocks.Add(new Paragraph(new Run(m.Title))
        {
            FontSize = spacious ? 26 : 18, FontWeight = spacious ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = B("Text"), Margin = new Thickness(0, 0, 0, spacious ? 6 : 3),
        });
        var meta = embedded ? "" : MeetingMeta(m);
        if (!string.IsNullOrEmpty(meta))
        {
            // Meta row: the facts as text, then the same status chips the list cards carry.
            var mp = new Paragraph { FontSize = 11, Foreground = B("TextDim"), Margin = new Thickness(0, 0, 0, sticky is null ? (spacious ? 20 : 15) : 12) };
            mp.Inlines.Add(new Run(meta));
            var open = m.Notes?.Actions?.Count(a => !a.Done) ?? 0;
            var people = m.Transcript.Select(t => t.Label).Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (open > 0) { mp.Inlines.Add(new Run("   ")); mp.Inlines.Add(new InlineUIContainer(MetaChip($"{open} open", "AccentAmber")) { BaselineAlignment = BaselineAlignment.Center }); }
            if (people >= 2) { mp.Inlines.Add(new Run("  ")); mp.Inlines.Add(new InlineUIContainer(MetaChip($"{people} people", "TextMid")) { BaselineAlignment = BaselineAlignment.Center }); }
            doc.Blocks.Add(mp);
        }

        // The sticky note's button, at the top under the title; the note it opens is placed further down.
        if (sticky is not null)
            doc.Blocks.Add(new BlockUIContainer(sticky.ToggleButton) { Margin = new Thickness(0, 0, 0, spacious ? 18 : 14) });

        // ── evidence links ──
        // A bullet traceable to a transcript line gets a "↳" that jumps there; an untraceable one
        // gets NOTHING (a wrong link is worse than none). Computed at render time, never stored.
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

        // The play button beside the jump (right-click saves the clip). Absent entirely when the
        // audio is gone: show nothing rather than something that fails when pressed.
        EvidencePlayer? Player(string text)
        {
            // The RANGE, not just the first line (see Evidence.BestRange).
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
            var summaryRail = RailFor("SUMMARY");
            summaryRail.Blocks.Add(new Paragraph(new Run(m.Notes.Tldr)) { Foreground = B("TextSoft"), LineHeight = fs + 9, TextAlignment = TextAlignment.Left, Margin = new Thickness(0, 0, 0, 2) });
            doc.Blocks.Add(summaryRail);
        }
        foreach (var sec in m.Notes.EffectiveSections())
        {
            if (sec.Bullets.Count == 0) continue;
            var heading = sec.Heading.ToUpperInvariant();
            doc.Blocks.Add(DocHeader(heading));
            var list = new List { MarkerStyle = TextMarkerStyle.Disc, Padding = new Thickness(15, 0, 0, 0), Margin = new Thickness(0, 0, 0, 2) };
            foreach (var b in sec.Bullets)
                list.ListItems.Add(new ListItem(WithEvidence(b, fs + 7, new Thickness(0, 0, 0, 7))));
            var secRail = RailFor(heading);
            secRail.Blocks.Add(list);
            doc.Blocks.Add(secRail);
        }
        if (m.Notes.Actions.Count > 0)
        {
            doc.Blocks.Add(DocHeader("ACTION ITEMS"));
            var actionRail = RailFor("ACTION ITEMS");
            // Owners arrive a few seconds later when speakers resolve; say so, rather than let a
            // name appear out of nowhere.
            if (resolving)
                actionRail.Blocks.Add(new BlockUIContainer(new ThinkingDots("working out who owns these"))
                { Margin = new Thickness(0, 0, 0, 4) });
            foreach (var a in m.Notes.Actions) actionRail.Blocks.Add(DocAction(a, fs, Link, Player));
            doc.Blocks.Add(actionRail);
        }

        // THE SPLIT: never nest the editable sticky inside this read-only document. A RichTextBox's
        // text editor claims keyboard and caret services at CLASS level, and a second editor nested
        // inside its document is starved (focus lands, but no caret, typing, or selection). With a
        // sticky the page is three siblings under ONE scroll - notes doc, sticky editor, transcript
        // doc - each document's own scrolling disabled. The trade: drag-selection cannot run THROUGH
        // the sticky.
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
            // The transcript rides the same rail as every other section. Its blocks are inserted and
            // removed as it expands, so they go through this collection rather than the document's.
            var transcriptRail = RailFor("TRANSCRIPT");
            tdoc.Blocks.Add(transcriptRail);
            var tblocks = transcriptRail.Blocks;
            if (resolving)
                tblocks.Add(new BlockUIContainer(new ThinkingDots("telling the speakers apart"))
                { Margin = new Thickness(0, 0, 0, 4) });
            // Who Toki thinks was in the room, each one a click away from being corrected. Absent
            // entirely when the speakers were never told apart, so a plain two-party call is unchanged.
            if (renameSpeaker is not null && SpeakerChips.Build(m.Transcript, renameSpeaker) is { } chips)
                tblocks.Add(new BlockUIContainer(chips) { Margin = new Thickness(0, 0, 0, 6) });
            var preview = Math.Min(3, m.Transcript.Count);
            for (var i = 0; i < preview; i++)
            {
                var p = DocTranscript(m.Transcript[i], fs, m.Corrections);
                transcriptParas[i] = p;     // so an evidence link can find and highlight it
                tblocks.Add(p);
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
                tblocks.Add(holder);

                var expanding = false;
                // Shared by the button and by any evidence link that points past the preview - a link
                // has to be able to fill the transcript in before it can scroll to its line.
                void Expand(Action? done)
                {
                    if (expanding) return;
                    expanding = true;

                    // CHUNKED expansion: a long meeting is thousands of paragraphs, and inserting them
                    // synchronously freezes the UI. Insert in slices, yielding to the dispatcher between them.
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
                            tblocks.InsertAfter(after, p);
                            after = p;
                        }
                        if (i < m.Transcript.Count)
                            btn.Dispatcher.BeginInvoke(InsertSlice, System.Windows.Threading.DispatcherPriority.Background);
                        else
                        {
                            tblocks.Remove(holder);
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

        // A READ-ONLY RichTextBox, not a FlowDocumentScrollViewer: it is the one WPF control with
        // rock-solid continuous selection. Read-only blocks editing, not selection.
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

        // No sticky: one box, selection spanning the whole note. Standalone it scrolls itself;
        // embedded it grows to content and the caller's ScrollViewer is the one scroll.
        if (sticky is null)
        {
            var rtb = MakeReadOnlyBox(doc,
                embedded ? new Thickness(0) : new Thickness(28, 20, 16, 18),
                embedded ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
            host = rtb;          // evidence links scroll this box...
            outerScroll = scrollHost;   // ...through the host page's scroller when embedded
            return rtb;
        }

        // Sticky: three siblings under one scroll (see THE SPLIT above).
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

        // A scroll-disabled RichTextBox still eats the mouse wheel (its internal ScrollViewer marks
        // the event handled and goes nowhere). Re-route the wheel to the outer scroll.
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
    /// A section header: a small round icon chip in the section's colour with the label beside it.
    /// One meaning, one colour, one icon (colour from HeaderColorKey).
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

    /// <summary>
    /// The section RAIL: everything under a heading sits in ONE Section with a 3px accent bar down
    /// its left. One Section, never a border per block: per-block borders gap at every paragraph
    /// margin and the rail comes out dotted instead of continuous.
    /// </summary>
    static Section RailFor(string title)
    {
        var key = HeaderColorKey(title);
        var accent = B(key == "TextDim" ? "TextMid" : key);
        return new Section
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Transparent(accent, 0x1F),
            Padding = new Thickness(16, 2, 0, 0),
            Margin = new Thickness(9, 0, 0, 0),
        };
    }

    /// <summary>The icon per section meaning (Fluent glyphs).</summary>
    static string SectionGlyph(string title)
    {
        var t = (title ?? "").ToUpperInvariant();
        if (t.Contains("DECISION")) return "";                                   // check
        if (t.Contains("ACTION") || t.Contains("TASK") || t.Contains("TODO")) return "";  // checklist
        if (t.Contains("QUESTION") || t.Contains("OPEN")) return "";             // question
        if (t.Contains("TRANSCRIPT")) return "";                                  // speech
        return "";                                                                // list
    }

    /// <summary>An outlined status capsule for the meta row ("2 open" / "3 people"):
    /// tinted fill, tinted 1px rim, accent text.</summary>
    static Border MetaChip(string text, string accentKey)
    {
        var accent = B(accentKey);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1.5, 7, 1.5),
            Background = Transparent(accent, 0x1A), BorderBrush = Transparent(accent, 0x4D), BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text, FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                Foreground = accent, FontFamily = Ui,
            },
        };
        Theme.GlassSurface.CrispRim(chip);   // whole device pixels, or the r8 rim reads as soft
        return chip;
    }

    /// <summary>Transcript avatar: a small circle with the speaker's initial in a colour derived from
    /// the name (never fetched). YOU reads brighter than everyone else. Full name on hover.</summary>
    static Border Avatar(string label, bool you)
    {
        var key = you ? "Text" : SpeakerAccents[StableHash(label) % SpeakerAccents.Length];
        var accent = B(key);
        var initial = string.IsNullOrWhiteSpace(label) ? "?" : char.ToUpperInvariant(label.Trim()[0]).ToString();
        // DiscLabel, not a bare TextBlock: the transcript Paragraph's LineHeight is inherited INTO
        // this Border and would shove the letter to the top of the disc, clipping its cap.
        var letter = PillParts.DiscLabel(initial, 8.5, FontWeights.Bold);
        letter.Foreground = accent;
        return new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
            Background = Transparent(accent, you ? (byte)0x30 : (byte)0x33),
            ToolTip = label, Margin = new Thickness(0, 0, 8, 0),
            Child = letter,
        };
    }

    static readonly string[] SpeakerAccents = { "Pink", "AccentTeal", "Blue", "Green", "Purple", "AccentAmber" };

    // Stable across processes: GetHashCode is randomized per run and would recolour every speaker
    // on every launch.
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
        // Circular tick + real strikethrough on done items (a FlowDocument Run can strike through).
        var check = new CheckBox { IsChecked = a.Done, Style = (Style)Res("CircleCheck"), Margin = new Thickness(0, 0, 9, 0) };
        check.Checked += (_, _) => Toggle(a, true);
        check.Unchecked += (_, _) => Toggle(a, false);
        p.Inlines.Add(new InlineUIContainer(check) { BaselineAlignment = BaselineAlignment.TextTop });
        var text = new Run(a.Text) { Foreground = a.Done ? B("TextDim") : B("Text") };
        if (a.Done) text.TextDecorations = TextDecorations.Strikethrough;
        p.Inlines.Add(text);
        // Search the transcript for the task TEXT alone: the owner/due chips are Toki's formatting,
        // not words anybody said, and feeding them in dilutes the match.
        if (link?.Invoke(a.Text) is { } jump) p.Inlines.Add(jump);
        if (player?.Invoke(a.Text) is { } play)
            p.Inlines.Add(new InlineUIContainer(play) { BaselineAlignment = BaselineAlignment.Center });
        // Owner / due ride below the item as capsule chips (person = blue, due = amber). The Copy
        // button still captures them: its text comes from NotesText, not from this rendering.
        if (Meaningful(a.Owner) || Meaningful(a.Due))
        {
            p.Inlines.Add(new LineBreak());
            // Indented past the check + its gap so the chips sit under the TEXT.
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
        // Avatar circle, then the words. The NAME run stays too: a letter alone is ambiguous when
        // two people share an initial, and drag-select/Ctrl+C should still capture who spoke.
        p.Inlines.Add(new InlineUIContainer(Avatar(seg.Label, you)) { BaselineAlignment = BaselineAlignment.Center });
        p.Inlines.Add(new Run($"{seg.Label}:  ") { FontWeight = FontWeights.SemiBold, Foreground = fg });
        foreach (var run in MarkCorrections(seg.Text, fg, corrections)) p.Inlines.Add(run);
        return p;
    }

    /// <summary>
    /// The transcript with Toki's own edits visible: changed words carry a dotted underline and the
    /// original on hover. Transcript ONLY - the notes stay clean. The mark is honest at meeting level,
    /// not occurrence level: only the changed pairs are stored, so the wording is "also transcribed as".
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
