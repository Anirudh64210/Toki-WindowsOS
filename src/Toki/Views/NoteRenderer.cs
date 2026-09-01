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

    // Why the current notes are not model-written, if that is worth telling the user.
    enum NotesFallback { None, ApiNoKey, LocalFailed }

    /// <summary>
    /// Decide whether to show a fallback banner. LocalFailed = these notes are the heuristic FLOOR
    /// and a real model COULD do better (the leak scenario: the model failed to load, so the floor
    /// ran). ApiNoKey = the user picked the API model but saved no key, so notes were written
    /// on-device - good notes, a gentle privacy-clean nudge, not an error. A note the user
    /// deliberately asked to keep heuristic (Heuristic-only mode) gets NEITHER: CanWrite is false.
    /// </summary>
    static NotesFallback FallbackState(MeetingRecord m)
    {
        if (m.Transcript is null || m.Transcript.Count == 0) return NotesFallback.None;
        var svc = Llm.LlmService.Instance;
        var s = Llm.AppSettings.Current;

        // A floor note has no template and is not the model's canned low-substance verdict. (Model
        // notes carry a template name; the low-substance note carries the canned summary string.)
        var isFloor = m.Notes is { } n
            && string.IsNullOrEmpty(n.Template)
            && n.Tldr != Llm.LlmNoteWriter.LowSubstanceTldr;

        if (isFloor && svc.CanWrite()) return NotesFallback.LocalFailed;
        if (s.NotesMode == Llm.NotesMode.ApiKey && string.IsNullOrWhiteSpace(s.ApiKey))
            return NotesFallback.ApiNoKey;
        return NotesFallback.None;
    }

    static FrameworkElement FallbackBanner(NotesFallback kind, MeetingRecord m, Action showTranscript, Action? refreshNotes)
    {
        var svc = Llm.LlmService.Instance;
        var accentKey = kind == NotesFallback.LocalFailed ? "AccentAmber" : "TextMid";
        var lead = kind == NotesFallback.LocalFailed ? "Basic notes" : "Private, on this device";
        var body = kind == NotesFallback.LocalFailed
            ? (svc.LocalPoisoned
                ? "AI note-writing stopped working this session, so these are basic on-device notes. Restart Toki to re-enable it, then try again. Check these against the transcript."
                : "The AI writer was not available, so Toki fell back to basic on-device extraction. These are lifted from what was said, so check them against the transcript.")
            : "You selected the \"My API key\" option, but no key is saved, so your notes were written on-device by the local model. Nothing left your computer. Add a key in Settings to use your API model.";

        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = Ui, FontSize = 12, Foreground = B("TextMid"), LineHeight = 17 };
        text.Inlines.Add(new Run(lead + "   ") { FontWeight = FontWeights.SemiBold, Foreground = B(accentKey) });
        text.Inlines.Add(new Run(body));

        var stack = new StackPanel();
        stack.Children.Add(text);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        if (m.Transcript.Count > 0)
        {
            var showBtn = new Button { Style = (Style)Res("ChipButton"), Content = "Show transcript" };
            showBtn.Click += (_, _) => showTranscript();
            btns.Children.Add(showBtn);
        }
        if (kind == NotesFallback.LocalFailed && refreshNotes is not null && svc.CanWrite())
        {
            var tryBtn = new Button { Style = (Style)Res("ChipButton"), Content = "Try again with the on-device model", Margin = new Thickness(8, 0, 0, 0) };
            tryBtn.Click += async (_, _) =>
            {
                tryBtn.IsEnabled = false; tryBtn.Content = "Working";
                var res = await App.RegenerateNotesAsync(m.Id);
                if (res == App.RegenNotesResult.Rewritten) { refreshNotes(); return; }   // rebuild swaps in the model notes
                tryBtn.IsEnabled = true; tryBtn.Content = "Try again";
                text.Inlines.Clear();
                text.Inlines.Add(new Run("Still basic notes   ") { FontWeight = FontWeights.SemiBold, Foreground = B("AccentAmber") });
                text.Inlines.Add(new Run(res == App.RegenNotesResult.NeedsRestart
                    ? "AI note-writing cannot recover without a restart. Close and reopen Toki, then try again."
                    : "Could not run the on-device model this time. Check the transcript, or try once more."));
                if (res == App.RegenNotesResult.NeedsRestart) tryBtn.Visibility = Visibility.Collapsed;
            };
            btns.Children.Add(tryBtn);
        }
        if (btns.Children.Count > 0) stack.Children.Add(btns);

        return new Border
        {
            Background = B("Chip"), BorderBrush = B("Hairline"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12),
            Child = stack,
        };
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
    /// <param name="dismissSection">Called with a section heading the user wants gone. Offered on
    /// the carried-forward section only; null omits the control. Storage is the caller's job.</param>
    /// <param name="refreshNotes">Rebuild the note view. The fallback banner's "try again" calls it
    /// after a successful regeneration so the floor notes are replaced by the model's; null omits the
    /// retry button (Show transcript still works).</param>
    public static FrameworkElement MeetingDocument(MeetingRecord m, bool spacious, FrameworkElement? topBar = null,
        MeetingStickyView? sticky = null, Action<string, string>? renameSpeaker = null, bool resolving = false,
        ScrollViewer? scrollHost = null, Action<string>? dismissSection = null, Action? refreshNotes = null)
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

        // ── fallback banner ──
        // When these notes are the heuristic FLOOR (the AI writer was unavailable), say so honestly
        // at the very top and offer the transcript + a retry - never pass chopped-up conversation off
        // as an AI summary. Inserted before everything else so it is the first thing read.
        if (FallbackState(m) is var _fb && _fb != NotesFallback.None)
        {
            var banner = new BlockUIContainer(FallbackBanner(_fb, m, () => JumpTo(0), refreshNotes))
            { Margin = new Thickness(0, 0, 0, embedded ? 14 : 16) };
            if (doc.Blocks.FirstBlock is { } _first) doc.Blocks.InsertBefore(_first, banner);
            else doc.Blocks.Add(banner);
        }

        // The one quiet play control beside a note line (right-click saves the clip). Each point is
        // resolved on its OWN - no cross-line disjoint pass - and the point's TEXT is handed to the
        // player so the clip narrows to just the phrase that carries it (EvidenceAudio.Window). Two
        // points that genuinely share words may overlap a little; none swallows the others. Absent
        // entirely when the audio is gone: show nothing rather than a control that fails when pressed.
        EvidencePlayer? Player(string text, SourceSpan? source = null)
        {
            if (Evidence.BestRange(text, m.Transcript, source) is not { } rr) return null;
            if (rr.First < 0 || rr.Last >= m.Transcript.Count) return null;
            var range = new List<TranscriptSegment>();
            for (var i = rr.First; i <= rr.Last; i++) range.Add(m.Transcript[i]);
            return EvidencePlayer.For(m, range, fs, text);
        }

        // A bullet, as a HANGING-INDENT paragraph (not a WPF List, whose marker floats off the text
        // line): a real drawn disc sits in a fixed marker column, the text starts at that column, and
        // wrapped lines align under the text - never under the dot. The disc is a drawn Ellipse, not a
        // font "•", which is oversized and vertically wrong (that was the "bullet not on the text
        // line" bug). Selection still runs across bullets: they are all paragraphs of one document.
        Paragraph WithEvidence(string text, double lineHeight, Thickness margin, SourceSpan? source = null)
        {
            const double col = 20;   // marker column = text indent
            var p = new Paragraph
            {
                LineHeight = lineHeight, TextIndent = -col,
                Margin = new Thickness(margin.Left + col, margin.Top, margin.Right, margin.Bottom),
            };
            p.Inlines.Add(new InlineUIContainer(BulletDisc(col)) { BaselineAlignment = BaselineAlignment.Center });
            p.Inlines.Add(new Run(text) { Foreground = B("Text") });
            if (Player(text, source) is { } play)
                p.Inlines.Add(new InlineUIContainer(play) { BaselineAlignment = BaselineAlignment.Center });
            return p;
        }

        // ONE GUTTER, no decorative rail. A section is its coloured header (icon + accent title) and
        // its rows, both starting at the same left edge, grouped by SPACE (heading-above 24,
        // heading-below 8, rows 8) rather than a bar down the side. Space groups; a line is a last
        // resort (see the symmetry-and-spacing and design-taste skills).
        if (!string.IsNullOrWhiteSpace(m.Notes.Tldr))
        {
            doc.Blocks.Add(DocHeader("Summary", fs, first: true));
            doc.Blocks.Add(new Paragraph(new Run(m.Notes.Tldr))
            { Foreground = B("TextSoft"), LineHeight = fs + 7, TextAlignment = TextAlignment.Left, Margin = new Thickness(0, 0, 0, 0) });
        }
        foreach (var sec in m.Notes.EffectiveSections())
        {
            if (sec.Bullets.Count == 0) continue;
            var dismiss = dismissSection is not null
                          && string.Equals(sec.Heading, Toki.Llm.OpenFromLastTime.Heading, StringComparison.OrdinalIgnoreCase)
                ? DismissButton(() => dismissSection(sec.Heading)) : null;
            doc.Blocks.Add(DocHeader(sec.Heading, fs, trailing: dismiss));
            // Round-dot bullets, hanging indent, tight (6) within the list. Last bullet carries no
            // bottom margin - the next heading's top margin owns that gap (no double-owned space).
            for (var i = 0; i < sec.Bullets.Count; i++)
            {
                var last = i == sec.Bullets.Count - 1;
                doc.Blocks.Add(WithEvidence(sec.Bullets[i], fs + 7, new Thickness(0, 0, 0, last ? 0 : 6), sec.SourceFor(i)));
            }
        }
        if (m.Notes.Actions.Count > 0)
        {
            doc.Blocks.Add(DocHeader("Action items", fs));
            // Owners arrive a few seconds later when speakers resolve; say so, rather than let a
            // name appear out of nowhere.
            if (resolving)
                doc.Blocks.Add(new BlockUIContainer(new ThinkingDots("working out who owns these"))
                { Margin = new Thickness(0, 0, 0, 6) });
            for (var i = 0; i < m.Notes.Actions.Count; i++)
                doc.Blocks.Add(DocAction(m.Notes.Actions[i], fs, Player, last: i == m.Notes.Actions.Count - 1));
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
            // THE TRANSCRIPT IS A DISCLOSURE, not a section that is always open. It is the longest
            // thing on the page and the least often read, and a note the reader has to scroll past
            // to reach anything is not glanceable. Collapsed, the row still answers "what is in
            // here?" - the count, and the chevron - which is the whole test of a good collapsed
            // state. Evidence links still open it on their way to a line.
            var expanded = false;
            var chev = new TextBlock
            {
                Text = "", FontFamily = (FontFamily)Res("IconFont"), FontSize = 10,
                Foreground = B("TextDim"), VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var countLabel = new TextBlock
            {
                Text = $"{m.Transcript.Count} line{(m.Transcript.Count == 1 ? "" : "s")}",
                FontFamily = (FontFamily)Res("UiFont"), FontSize = 11, Foreground = B("TextDim"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            };

            var accent = B("AccentAmber");
            var icon = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
                Background = Transparent(accent, 0x2E), Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "", FontFamily = (FontFamily)Res("IconFont"), FontSize = 9.5, Foreground = accent,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(icon);
            var title = new TextBlock
            {
                Text = "TRANSCRIPT", FontFamily = (FontFamily)Res("UiFont"), FontWeight = FontWeights.Bold,
                FontSize = 10, Foreground = accent, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(title, 1); head.Children.Add(title);
            Grid.SetColumn(countLabel, 2); head.Children.Add(countLabel);
            Grid.SetColumn(chev, 3); head.Children.Add(chev);

            // A real Button spanning the row: the whole strip is the control, so the hit target,
            // the hover box and the visible box are one rectangle.
            var toggle = new Button
            {
                Content = head, Style = (Style)Res("RowButton"),
                Padding = new Thickness(10, 9, 10, 9), Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = "Show or hide what was said",
            };
            tdoc.Blocks.Add(new BlockUIContainer(toggle) { Margin = new Thickness(0, 22, 0, 0) });

            // NO RAIL on the transcript. The 3px accent bar tells you which heading a bullet belongs
            // to when several sections sit in one column; the transcript is a disclosure with its own
            // button above it, so the bar just draws a line down the side of the speech.
            var transcriptBody = new Section { Padding = new Thickness(2, 4, 0, 0), Margin = new Thickness(9, 0, 0, 0) };
            tdoc.Blocks.Add(transcriptBody);
            var tblocks = transcriptBody.Blocks;

            var filling = false;
            void Fill(Action? done)
            {
                if (filling) return;
                filling = true;
                if (resolving)
                    tblocks.Add(new BlockUIContainer(new ThinkingDots("telling the speakers apart"))
                    { Margin = new Thickness(0, 0, 0, 4) });
                // Who Toki thinks was in the room, each one a click away from being corrected.
                if (renameSpeaker is not null && SpeakerChips.Build(m.Transcript, renameSpeaker) is { } chips)
                    tblocks.Add(new BlockUIContainer(chips) { Margin = new Thickness(0, 6, 0, 6) });

                // CHUNKED: a long meeting is thousands of paragraphs, and inserting them
                // synchronously freezes the window. Insert in slices, yielding between them.
                const int slice = 150;
                var i = 0;
                void InsertSlice()
                {
                    var end = Math.Min(i + slice, m.Transcript.Count);
                    for (; i < end; i++)
                    {
                        var p = DocTranscript(m.Transcript[i], fs, m.Corrections);
                        transcriptParas[i] = p;
                        tblocks.Add(p);
                    }
                    if (i < m.Transcript.Count)
                        toggle.Dispatcher.BeginInvoke(InsertSlice, System.Windows.Threading.DispatcherPriority.Background);
                    else if (done is not null)
                        toggle.Dispatcher.BeginInvoke(done, System.Windows.Threading.DispatcherPriority.Loaded);
                }
                InsertSlice();
            }

            void Open(Action? done)
            {
                if (expanded) { done?.Invoke(); return; }
                expanded = true;
                chev.RenderTransform = new RotateTransform(180);
                countLabel.Text = "Hide";
                Fill(done);
            }

            void Close()
            {
                expanded = false; filling = false;
                chev.RenderTransform = null;
                countLabel.Text = $"{m.Transcript.Count} line{(m.Transcript.Count == 1 ? "" : "s")}";
                tblocks.Clear();
                transcriptParas.Clear();
            }

            expandTranscript = Open;
            toggle.Click += (_, _) => { if (expanded) Close(); else Open(null); };
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
                // Standalone (Library) scrolls itself: reserve the slim bar ALWAYS (Visible, not Auto)
                // so expanding the transcript never narrows the column and reflows the summary.
                embedded ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Visible);
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
            // Visible, not Auto: reserve the slim bar always so the column width is stable (see the
            // standalone box above). Only the Library uses this sticky path, so there is no nested scroll.
            Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
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

    // A section header, Granola/Apple style: NO icon, NO colour, sentence case, differentiated from
    // the body by WEIGHT (semibold) and a touch of size, separated by whitespace (24 above, 8 below).
    // Research 2026-08-25 (Granola + Apple Notes/Bear/Craft): headers are body-ish size, semibold,
    // sentence case, full ink, no rule and no icon; the icon chips were the "cheap emoji" look.
    static Paragraph DocHeader(string title, double fs, bool first = false, UIElement? trailing = null)
    {
        // Heading-above 24 (between sections), heading-below 8 (to its own content): the 3:1 the
        // reader sees as "this heading owns what follows".
        var p = new Paragraph { Margin = new Thickness(0, first ? 0 : 24, 0, 8) };
        p.Inlines.Add(new Run(SentenceCase(title))
        {
            FontWeight = FontWeights.SemiBold, Foreground = B("Text"), FontSize = fs + 1,
        });
        if (trailing is not null)
            p.Inlines.Add(new InlineUIContainer(trailing) { BaselineAlignment = BaselineAlignment.Center });
        return p;
    }

    /// <summary>First letter up, the rest lower: "ACTION ITEMS" / "Action Items" -> "Action items".
    /// Keeps a heading from reading as a shouting all-caps label or forced Title Case.</summary>
    static string SentenceCase(string s)
    {
        var t = (s ?? "").Trim();
        return t.Length == 0 ? t : char.ToUpperInvariant(t[0]) + t[1..].ToLowerInvariant();
    }

    /// <summary>The bullet marker: a small drawn disc in a fixed-width column, so text hangs at the
    /// column edge. A drawn Ellipse, never the font "•" (oversized and vertically wrong).</summary>
    static FrameworkElement BulletDisc(double column)
    {
        var disc = new System.Windows.Shapes.Ellipse
        {
            Width = 4.5, Height = 4.5, Fill = B("TextDim"),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
        };
        // The disc sits ~7px into the gutter; the holder fills the column so the text starts at `column`.
        return new Border { Width = column, Child = disc, Padding = new Thickness(7, 0, 0, 0) };
    }

    /// <summary>
    /// The "not relevant" control on a carried-forward section. Title text alone can never decide
    /// reliably whether two meetings are the same recurring one, so the other half of the answer is
    /// making the correction cheap: one click removes the section from this note for good.
    /// A real Button, per the standing rule - and it sits inside a read-only RichTextBox, where a
    /// container with a mouse handler would have its mouse-up eaten by the selection machinery.
    /// </summary>
    static Button DismissButton(Action onDismiss)
    {
        var b = new Button
        {
            Style = (Style)Res("BareButton"),
            Content = new TextBlock
            {
                Text = "Not relevant", FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                Foreground = B("TextLow"), FontFamily = (FontFamily)Res("UiFont"),
            },
            Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Remove this section from this meeting's note",
        };
        b.Click += (_, _) => onDismiss();
        return b;
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
        letter.Fill = accent;
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

    static Paragraph DocAction(ActionItem a, double fs, Func<string, SourceSpan?, EvidencePlayer?>? player = null,
        bool last = false)
    {
        // The CircleCheck is a 24px hit target with an 18px ring centred in it (3px of built-in
        // padding each side), so the column IS 24 and the text hangs there: ring left edge near the
        // gutter, ~3px of the tick's own padding as the gap to the text. Wrapped lines align under the
        // text, not the tick.
        const double col = 24;
        var p = new Paragraph
        {
            LineHeight = fs + 7, TextIndent = -col,
            Margin = new Thickness(col, 0, 0, last ? 0 : 8),
        };
        var check = new CheckBox { IsChecked = a.Done, Style = (Style)Res("CircleCheck") };
        check.Checked += (_, _) => Toggle(a, true);
        check.Unchecked += (_, _) => Toggle(a, false);
        p.Inlines.Add(new InlineUIContainer(check) { BaselineAlignment = BaselineAlignment.Center });
        var text = new Run(a.Text) { Foreground = a.Done ? B("TextDim") : B("Text") };
        if (a.Done) text.TextDecorations = TextDecorations.Strikethrough;
        p.Inlines.Add(text);
        // Owner / due as INLINE MUTED TEXT, not chips (Granola/Apple: dates live in the prose, no
        // pills, no colour). "  Priya  ·  Fri" trailing the task, dimmed. The Copy button still
        // captures them: its text comes from NotesText, not from this rendering.
        var meta = ActionMeta(a);
        if (meta.Length > 0)
            p.Inlines.Add(new Run(meta) { Foreground = B("TextDim"), FontSize = fs - 1.5 });
        // The one quiet play control. Search the transcript for the task TEXT alone.
        if (player?.Invoke(a.Text, a.Source) is { } play)
            p.Inlines.Add(new InlineUIContainer(play) { BaselineAlignment = BaselineAlignment.Center });
        return p;
    }

    /// <summary>The owner/due tail for a to-do, as plain muted prose: "   Priya  ·  Fri".</summary>
    static string ActionMeta(ActionItem a)
    {
        var parts = new List<string>();
        // "You" is the implicit owner in the user's own meeting - showing it on every task is noise.
        if (Meaningful(a.Owner) && !a.Owner!.Trim().Equals("You", StringComparison.OrdinalIgnoreCase))
            parts.Add(a.Owner!.Trim());
        if (Meaningful(a.Due)) parts.Add(a.Due!.Trim());
        return parts.Count == 0 ? "" : " " + string.Join("  ·  ", parts);
    }

    /// <summary>
    /// What to print against a line. The local stream is the ONE speaker Toki already knows by name,
    /// because the user typed it in Settings, so it prints that instead of the stream word "You".
    /// A tag the user set for this meeting still wins - their edit beats the default.
    /// </summary>
    internal static string SpeakerName(TranscriptSegment seg)
    {
        if (seg.Speaker != Speaker.You) return seg.Label;
        if (!string.IsNullOrWhiteSpace(seg.Tag) && !IsStreamWord(seg.Tag!)) return seg.Tag!;
        var mine = Llm.AppSettings.Current.DisplayName;
        return string.IsNullOrWhiteSpace(mine) ? "You" : mine;
    }

    /// <summary>A label that is a stream, not a person: it carries no name the user chose.</summary>
    internal static bool IsStreamWord(string s) =>
        s.Equals("You", StringComparison.OrdinalIgnoreCase) || s.Equals("Them", StringComparison.OrdinalIgnoreCase);

    static Paragraph DocTranscript(TranscriptSegment seg, double fs,
        IReadOnlyList<TextFix>? corrections = null)
    {
        var you = seg.Speaker == Speaker.You;
        var fg = B(you ? "Text" : "TextDim");
        var name = SpeakerName(seg);
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 9), LineHeight = fs + 7 };
        // Avatar circle, then the words. The NAME run stays too: a letter alone is ambiguous when
        // two people share an initial, and drag-select/Ctrl+C should still capture who spoke.
        p.Inlines.Add(new InlineUIContainer(Avatar(name, you)) { BaselineAlignment = BaselineAlignment.Center });
        p.Inlines.Add(new Run($"{name}:  ") { FontWeight = FontWeights.SemiBold, Foreground = fg });
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
