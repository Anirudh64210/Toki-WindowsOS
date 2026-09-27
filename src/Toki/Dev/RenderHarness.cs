using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Toki.State;
using Toki.Views;

namespace Toki.Dev;

/// <summary>
/// Renders each view to a PNG via RenderTargetBitmap - captures the visual tree
/// directly, so it works even though the live overlay is a transparent (layered)
/// window that plain screen-capture can't see. Run: <c>Toki.exe --render &lt;dir&gt;</c>.
/// </summary>
static class RenderHarness
{
    // (state, width, capHeight). Height is MEASURED from the view's content (matching the runtime's
    // content-tight auto-height) and capped - so these PNGs reflect what you actually see.
    static readonly (PillState State, double W, double Cap)[] Shots =
    [
        // The compact states carry the redesign's own capsule widths - the shots must picture the
        // layout the app actually has.
        (PillState.MeetingPrompt, 197, 300),   // 169 pill + 8 gap + 20 close puck
        (PillState.RecordingOpen, 272, 300),   // the compact bar; the chevron widens it to 344
        (PillState.Summarizing,   220, 300),
        (PillState.Notes,         360, 320),
        (PillState.NoSpeech,      300, 300),
        (PillState.Home,          344, 400),
        (PillState.Library,       404, 440),
        (PillState.Settings,      380, 440),
        (PillState.Ask,           344, 340),
        (PillState.Calendar,      360, 430),
        (PillState.About,         380, 430),
    ];

    public static void RenderAll(string dir)
    {
        Directory.CreateDirectory(dir);
        // Give the Notes screen a real meeting to render (latest in the store).
        try
        {
            var latest = new Storage.MeetingStore().List().FirstOrDefault(x => x.Kind == Storage.MeetingKind.Meeting);
            if (latest is not null) App.CurrentMeeting = new Storage.MeetingStore().Get(latest.Id);
        }
        catch { /* no store yet - Notes renders its fallback */ }
        foreach (var (state, w, cap) in Shots)
            Shot(dir, $"{state}", Build(state), w, cap, PillWindow.IsCapsuleState(state));

        // The tucked LOADER sliver exactly as runtime shows it - the SummarizingView laid out at its
        // full ~50 height but the plate CLIPPED to the top 13px squircle capsule (Root sits at the
        // bubble floor, the sliver is a clip not a resize). The flat Summarizing.png only ever pictures
        // the open pill; this is the surface where the shimmer used to poke past the rounded ends, and
        // the "union" variant reproduces the tuck-morph width that caused it. Guards the capped track.
        SliverShot(dir);
        TitledLoaderShot(dir);

        // The meeting prompt with REAL raw app identities - proves the friendly-name mapping and
        // that a long/wrapping prompt is fully visible at its measured height.
        var slack = new MeetingPromptView(); slack.Refresh("Com.tinyspeck.slackdesktop");
        Shot(dir, "MeetingPrompt-slack", slack, 197, 300, bare: true);

        // The recording bar with its transcript OPEN: the chevron form at the frame's 344 width,
        // with the speaker marks and the top fade.
        var live = new RecordingOpenView();
        live.DebugExpand(
        [
            new Transcription.TranscriptSegment(Transcription.Speaker.Them, 0, 4000,
                "So where are we on the onboarding diagnostic, shippable this week?"),
            new Transcription.TranscriptSegment(Transcription.Speaker.You, 4000, 9000,
                "Tool's done. Twenty checks, results render instantly, no gate.") { Tag = "Priya" },
            new Transcription.TranscriptSegment(Transcription.Speaker.Them, 9000, 11000,
                "Love it. What's left?") { Tag = "Sid" },
            new Transcription.TranscriptSegment(Transcription.Speaker.You, 11000, 15000,
                "Brand tokens and the Make webhook. Both land Thursday."),
        ]);
        Shot(dir, "RecordingOpen-expanded", live, 344, 300);

        // Toki's moods, at the size he is actually shown (a 56px bubble), one shot per mood.
        Shot(dir, "Toki-moods", new MoodStrip(), 360, 120, bare: true);

        // "Menu Bar - Notes Ready": not a PillState, so the state loop above cannot reach it.
        Shot(dir, "NotesReady", new NotesReadyView { Height = NotesReadyView.CardHeight },
             NotesReadyView.CardWidth, NotesReadyView.CardHeight);

        // The Calendar create composer, opened with sample text - proves the parse-preview line renders.
        var composer = new CalendarView(); composer.DebugOpenComposer("Design sync tomorrow 2pm with ana@work.com");
        Shot(dir, "Calendar-composer", composer, 360, 520);

        // The month calendar the Day field opens. Popups are invisible to a headless render, so
        // its content is drawn directly.
        var dayPicker = new Views.DayPickerShot();
        Shot(dir, "Calendar-daypicker", dayPicker, 280, 320);

        // Agenda rows in their hover-EXPANDED state (inline Edit/Open/Delete strip).
        CalendarRows.DebugExpand = true;
        Shot(dir, "Calendar-hover", new CalendarView(), 360, 520);
        CalendarRows.DebugExpand = false;

        // Prep cards: a matched card (last time + still-open + agenda) and a first-time card (honest empty).
        App.CurrentPrep = PrepSamples.Matched();
        Shot(dir, "PrepCard", new PrepCardView(), 356, 360);
        App.CurrentPrep = PrepSamples.FirstTime();
        Shot(dir, "PrepCard-firsttime", new PrepCardView(), 356, 360);

        // The prep card WITH the scratchpad open (N3) - the "your notes" half of the card, which is
        // otherwise a one-line prompt until you click it.
        App.CurrentPrep = PrepSamples.Matched();
        var scratched = new PrepCardView();
        scratched.DebugOpenScratch(Toki.Calendar.PrepBuilder.ToMarkdown(App.CurrentPrep!)
                                   + "ask about the pilot timeline\n- [ ] send pricing before Friday");
        Shot(dir, "PrepCard-scratch", scratched, 356, 620);
        App.CurrentPrep = null;
        var longName = new MeetingPromptView(); longName.Refresh("com.megacorp.videoconferencing.collaboration.desktop");
        Shot(dir, "MeetingPrompt-longname", longName, 197, 300, bare: true);

        // The hand-written note editor (N1) showing its whole vocabulary at once - both text sizes,
        // a ticked and an unticked to-do, a bulleted list and a numbered one. Built standalone with
        // sample text on purpose: this must never touch the real note store to render.
        var editor = new NoteEditor { MinHeight = 380 };
        editor.Markdown =
            "# Pricing sync\n" +
            "\n" +
            "## Summary\n" +
            "Held the price; revisit after the pilot.\n" +
            "\n" +
            "## Action items\n" +
            "- [x] send the deck\n" +
            "- [ ] confirm with Ana\n" +
            "\n" +
            "## Notes\n" +
            "- pilot runs four weeks\n" +
            "- two more logos before we raise\n" +
            "1. draft the pricing page\n" +
            "2. review with the team";
        Shot(dir, "NoteEditor", editor, 520, 560);

        // A NOTE THE MOMENT A TEMPLATE IS PICKED. The owner photographed this and it was a stack of
        // headings with squashed slivers between them and a sliced tick box - nothing to write on and
        // nothing whole on screen. The harness had no picture of a template, so nothing here could
        // have shown it.
        var templated = new NoteEditor { MinHeight = 380 };
        templated.InsertTemplate(Llm.NoteTemplates.All[0]);
        Shot(dir, "NoteEditor-template", templated, 520, 560);

        // First-run onboarding, all three steps. These are the only screens a user sees exactly once,
        // so they're the hardest to capture by hand - and the first thing a design review wants.
        string[] steps = ["1-name", "2-privacy", "3-allset"];
        for (var i = 0; i < steps.Length; i++)
        {
            var ob = new OnboardingView();
            ob.DebugShowStep(i);
            Shot(dir, $"Onboarding-{steps[i]}", ob, 330, 400);
        }
    }

    /// <summary>The tucked loader sliver - full-height SummarizingView, plate clipped to a 13px squircle
    /// capsule (radius clamps to 6.5), rim on the same 13px box. Mirrors the runtime tuck so the
    /// shimmer's containment inside the rounded ends is visible. Rendered twice: "flat" (content =
    /// card) and "union" (content wider than the card, as the tuck morph pins it) - the width that let
    /// the bar escape the rounded ends before the track was capped.</summary>
    /// <summary>The OPEN loader at its real 220x50, with and without a meeting title. The title
    /// replaces "Writing your notes" as soon as the model has written one (~2s into a 14s pass), and
    /// a real title is far wider than the card - this is the shot that shows whether the trim reads
    /// as a title or as damage. Deliberately uses the LONGEST title in the owner's Library.</summary>
    static void TitledLoaderShot(string dir)
    {
        const double card = 220, full = 50;
        var saved = App.SummarizeTitle;
        var stage = App.SummarizeStage;
        App.SummarizeStage = "Writing your notes";
        foreach (var (tag, title) in new[]
                 {
                     ("none", null),
                     ("short", "Hiring Sync"),
                     ("long", "Organization Catalog Simplification and AI Workflow Research"),
                 })
        {
            App.SummarizeTitle = title;
            var view = new SummarizingView { Width = card, Height = full };
            var pill = new Border { Width = card, Height = full };
            pill.SetResourceReference(Border.BackgroundProperty, "CardPlate");
            pill.Clip = Theme.Squircle.Build(new Rect(0, 0, card, full), 24);
            var layered = new Grid();
            layered.Children.Add(new Theme.CardMaterial { Radius = 23 });
            layered.Children.Add(view);
            pill.Child = layered;

            var canvas = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
                Padding = new Thickness(24), Child = pill,
            };
            double tw = card + 48, th = full + 48;
            canvas.Measure(new Size(tw, th));
            canvas.Arrange(new Rect(0, 0, tw, th));
            canvas.UpdateLayout();
            Save(canvas, tw, th, Path.Combine(dir, $"Summarizing_open_title_{tag}.png"));
        }
        App.SummarizeTitle = saved;
        App.SummarizeStage = stage;
    }

    static void SliverShot(string dir)
    {
        const double card = 220, full = 50, sliver = 13;
        // contentW WIDER than the card reproduces the tuck-morph condition (PillWindow pins the content
        // at a union width while the card clip animates). The card plate stays 220, CENTRED. Before the
        // fix the shimmer track stretched to contentW and the bar reached past the card's rounded end.
        foreach (var contentW in new[] { card, 264.0 })
        {
            var view = new SummarizingView { Width = contentW, Height = full };
            var box = new Rect((contentW - card) / 2, 0, card, sliver);   // 220 capsule, centred in content

            var pill = new Border { Width = contentW, Height = full };
            pill.SetResourceReference(Border.BackgroundProperty, "CardPlate");
            pill.Clip = Theme.Squircle.Build(box, 24);   // clip the plate to the 13px capsule
            var layered = new Grid();
            // Match PillWindow.ApplyCornerGeometry: the material's edge-light rings must use the plate's
            // CLAMPED radius (6.5 on a 13px sliver) minus the rim, or their full-card corner arcs cut
            // two crescent "edges" across the sliver.
            var plateR = System.Math.Min(24.0, System.Math.Min(card, sliver) / 2);
            layered.Children.Add(new Theme.CardMaterial { Radius = System.Math.Max(0, plateR - 1) });
            layered.Children.Add(view);
            pill.Child = layered;

            var lifted = new Grid { Width = contentW, Height = full };
            lifted.Children.Add(pill);
            var rim = new System.Windows.Shapes.Path
            {
                Data = Theme.Squircle.Outline(box, 24, 1), StrokeThickness = 1, IsHitTestVisible = false,
            };
            rim.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CardRim");
            lifted.Children.Add(rim);

            var canvas = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
                Padding = new Thickness(24), Child = lifted,
            };
            double tw = contentW + 48, th = full + 48;
            canvas.Measure(new Size(tw, th));
            canvas.Arrange(new Rect(0, 0, tw, th));
            canvas.UpdateLayout();

            // Stamp the shimmer across its travel so mid-sweep containment is visible, not just at rest.
            var kind = contentW > card ? "union" : "flat";
            var move = view.FindName("ShimmerMove") as TranslateTransform;
            foreach (var (tag, x) in new[] { ("rest", -90.0), ("mid", 60.0), ("right", 180.0), ("far", 240.0) })
            {
                if (move is not null) move.X = x;
                canvas.UpdateLayout();
                Save(canvas, tw, th, Path.Combine(dir, $"Summarizing_sliver_{kind}_{tag}.png"));
            }
        }
    }

    /// <summary>Render one view into a pill PNG at its MEASURED content height (mirrors the runtime's
    /// content-tight auto-height, capped).</summary>
    static void Shot(string dir, string name, UserControl view, double w, double cap, bool bare = false)
    {
        // Measure the view's real content height at this width, cap it - mirrors the runtime.
        view.Measure(new Size(w - 2, double.PositiveInfinity));
        var h = Math.Min(cap, Math.Ceiling(view.DesiredSize.Height) + 1);
        // A CAPSULE state has no card behind it (PillWindow makes its card transparent there), so
        // "bare" draws none - these PNGs must never picture a box that is not on screen. The card,
        // material, shadow casters and resting squircle corner below are the REAL ones PillWindow
        // draws, so the shots show the actual material.
        var pill = new Border { Width = w, Height = h };
        if (bare) { pill.Background = Brushes.Transparent; pill.Child = view; }
        else
        {
            pill.SetResourceReference(Border.BackgroundProperty, "CardPlate");
            pill.BorderThickness = new Thickness(1);
            pill.BorderBrush = Brushes.Transparent;   // spacer only; the rim below is the visible one
            pill.Clip = Theme.Squircle.Build(new Rect(0, 0, w, h), 24);
            var layered = new Grid();
            layered.Children.Add(new Theme.CardMaterial());
            layered.Children.Add(view);
            pill.Child = layered;
        }

        // The lift: the key + ambient casters, exactly as PillWindow stacks them.
        var lifted = new Grid { Width = w, Height = h };
        if (!bare)
        {
            lifted.Children.Add(Caster(28, 8, 0.22));
            lifted.Children.Add(Caster(8, 2, 0.12));
        }
        lifted.Children.Add(pill);
        if (!bare)
        {
            var rim = new System.Windows.Shapes.Path
            {
                Data = Theme.Squircle.Outline(new Rect(0, 0, w, h), 24, 1),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            rim.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CardRim");
            lifted.Children.Add(rim);
        }

        // Neutral backdrop so the pill's rounded edges and its shadow are visible in the PNG.
        var canvas = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
            Padding = new Thickness(28),
            Child = lifted,
        };

        var totalW = w + 56;
        var totalH = h + 56;
        canvas.Measure(new Size(totalW, totalH));
        canvas.Arrange(new Rect(0, 0, totalW, totalH));
        canvas.UpdateLayout();

        Save(canvas, totalW, totalH, Path.Combine(dir, $"{name}.png"));
    }

    /// <summary>One of the card's two shadow casters: a card-shaped border filled at 1/255 alpha
    /// (invisible itself, enough silhouette to cast) carrying the blur. Theme-matched to
    /// PillWindow.ApplyShadowDepth - light theme runs both at 70%.</summary>
    static Border Caster(double blur, double depth, double opacity) => new()
    {
        CornerRadius = new CornerRadius(24),
        Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)),
        IsHitTestVisible = false,
        Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = blur, ShadowDepth = depth, Direction = 270, Color = Colors.Black,
            Opacity = opacity * (Toki.Theme.ThemeManager.Current == Toki.Theme.AppTheme.Light ? 0.70 : 1.0),
        },
    };

    /// <summary>
    /// The MOVING shots: each subject rendered at t0 and again once its motion has settled, so the
    /// pair proves the motion ran. A real off-screen host window is REQUIRED: WPF's animation clock
    /// only ticks for a visual tree attached to a presentation source, so a free-standing render
    /// produces byte-identical frames and proves nothing.
    /// </summary>
    public static void RenderAnim(string dir)
    {
        Directory.CreateDirectory(dir);

        const double w = 340, h = 164;
        var pill = new Border
        {
            Background = (Brush)Application.Current.Resources["Surface"],
            CornerRadius = new CornerRadius(26),
            Width = w, Height = h, Child = new SummarizingView(),
        };
        var canvas = Framed(pill);
        var moodCanvas = Framed(new MoodStrip { Width = MoodStrip.ShotW, Height = MoodStrip.ShotH });

        var stack = new StackPanel();
        stack.Children.Add(canvas);
        stack.Children.Add(moodCanvas);

        // Far off-screen rather than hidden: a collapsed or zero-opacity window is skipped by the
        // compositor, which would put us straight back to a dead clock.
        var host = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Topmost = false,
            Left = -4000, Top = -4000, Width = w + 60, Height = h + 260,
            Background = Brushes.Black, Content = stack,
        };
        host.Show();
        host.UpdateLayout();

        SaveDetached(canvas, w + 40, h + 40, Path.Combine(dir, "Summarizing_t0.png"));
        SaveDetached(moodCanvas, MoodStrip.ShotW + 40, MoodStrip.ShotH + 40, Path.Combine(dir, "Toki-moods_t0.png"));

        // 900ms is past every one-shot involved, so the second frame is the settled pose. Looping
        // parts are caught mid-cycle on purpose - a loop at exactly zero looks like a dead one.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            host.UpdateLayout();
            SaveDetached(canvas, w + 40, h + 40, Path.Combine(dir, "Summarizing_settled.png"));
            SaveDetached(moodCanvas, MoodStrip.ShotW + 40, MoodStrip.ShotH + 40, Path.Combine(dir, "Toki-moods_settled.png"));
            host.Close();
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    /// <summary>
    /// Render one element of a laid-out tree, ignoring where layout put it: RenderTargetBitmap
    /// honours the visual's offset within its parent (an offset child renders blank), so paint
    /// through a VisualBrush to re-anchor at 0,0. The brush is live - animated values come through.
    /// </summary>
    static void SaveDetached(Visual visual, double w, double h, string path)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(
                new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, w, h));
        Save(dv, w, h, path);
    }

    static Border Framed(UIElement child) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
        Padding = new Thickness(20), Child = child,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
    };

    /// <summary>Render a laid-out element (e.g. the Library window's RootGrid) to a PNG at its actual size.</summary>
    internal static void SaveVisual(FrameworkElement fe, string path)
    {
        var w = fe.ActualWidth > 0 ? fe.ActualWidth : 960;
        var h = fe.ActualHeight > 0 ? fe.ActualHeight : 640;
        Save(fe, w, h, path);
    }

    static void Save(Visual visual, double w, double h, string path)
    {
        const double scale = 2.0;
        var rtb = new RenderTargetBitmap(
            (int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static UserControl Build(PillState s) => s switch
    {
        PillState.MeetingPrompt => new MeetingPromptView(),
        PillState.RecordingBar  => new RecordingBarView(),
        PillState.RecordingOpen => new RecordingOpenView(),
        PillState.Summarizing   => new SummarizingView(),
        PillState.Notes         => new NotesView(),
        PillState.NoSpeech      => new NoSpeechView(),
        PillState.Home          => new HomeView(),
        PillState.Library       => new LibraryView(),
        PillState.Settings      => new SettingsView(),
        PillState.Ask           => new AskView(),
        PillState.Calendar      => new CalendarView(),
        PillState.PrepCard      => new PrepCardView(),
        PillState.About         => new AboutView(),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };
}

/// <summary>Toki in all four moods at once, each in the 56px bubble the notch actually draws him in,
/// so the overlays can be judged at the size a user sees rather than at art-board scale.</summary>
sealed class MoodStrip : UserControl
{
    // The strip's own size, so the two callers (--render's flat shot and --render-anim's settled
    // pair) cannot drift apart and crop it differently.
    public const double ShotW = 340;   // 5 discs of 56 with 6px either side
    public const double ShotH = 60;

    public MoodStrip()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var mood in new[]
                 {
                     Controls.TokiBlob.TokiMood.Idle, Controls.TokiBlob.TokiMood.Thinking,
                     Controls.TokiBlob.TokiMood.Reading, Controls.TokiBlob.TokiMood.Writing,
                     Controls.TokiBlob.TokiMood.Happy,
                 })
        {
            var blob = new Controls.TokiBlob { Width = 44, Height = 40 };
            blob.SetMood(mood);
            row.Children.Add(new Border
            {
                Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Margin = new Thickness(6, 0, 6, 0),
                // The themed disc, not a literal - so --render --theme light shows what light actually
                // looks like. The gradient plate + gradient rim are what PillWindow draws.
                Background = (Brush)Application.Current.Resources["MascotPlate"],
                BorderBrush = (Brush)Application.Current.Resources["MascotRim"],
                BorderThickness = new Thickness(1),
                Child = blob,
            });
        }
        Content = row;
    }
}

