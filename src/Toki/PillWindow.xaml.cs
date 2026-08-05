using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using Toki.State;
using Toki.Views;

namespace Toki;

/// <summary>
/// The single overlay: an opaque, hardware-accelerated, borderless window that IS
/// the pill (DWM rounds its corners). It resizes to the current state's content
/// and sits bottom-center. Not a layered/transparent window — that both forced
/// slow software rendering AND conflicted with ghost mode (WDA_EXCLUDEFROMCAPTURE
/// on a layered window produced the black-rectangle glitch).
/// </summary>
public partial class PillWindow : Window
{
    // Logical-pixel content sizes per state, from Toki-ProductDesign/.
    static readonly Dictionary<PillState, (double W, double H)> Sizes = new()
    {
        // Sizes trimmed smaller overall (owner: "make the notch smaller").
        // NOTE: for the AutoHeight pages (Home/Settings/MeetingPrompt/NoSpeech/RecordingOpen) the
        // HEIGHT below is only a fallback — the real height is measured from the content so every
        // page is concise. Width is always used.
        [PillState.Collapsed]     = (52, 42),    // launcher peek (Toki alone)
        [PillState.MeetingPrompt] = (288, 72),
        [PillState.RecordingBar]  = (34, 14),    // TINY upside-down eyes-peek — never in the way of browser tabs / snip toolbar
        [PillState.RecordingOpen] = (268, 76),   // Glass/Luma-tight: one header row (dot+timer · Stop) + live line
        [PillState.Summarizing]   = (232, 66),   // small, cute working loader (mostly lives as a sliver)
        [PillState.Notes]         = (360, 320),  // scrollable window into the notes
        [PillState.NoSpeech]      = (300, 92),
        [PillState.Home]          = (344, 190),   // widened 320→344 to fit the COMING UP list + Join pills (design ③)
        [PillState.Library]       = (404, 440),
        [PillState.Settings]      = (380, 208),   // FIXED height; UI/visibility-only now (AI/calendar/about moved to the Library Settings page)
        [PillState.ManualNote]    = (392, 410),
        [PillState.Ask]           = (344, 120),   // push-to-talk: compact while listening, grows for the answer (AutoHeight)
        [PillState.Calendar]      = (360, 430),   // FIXED height + internal scroll (auto-height fought the ScrollViewer)
        [PillState.PrepCard]      = (356, 306),   // auto-height: first-time cards are short (design ④)
        [PillState.About]         = (380, 430),   // FIXED height + internal scroll (credits list) — mirrors Settings
        [PillState.Onboarding]    = (330, 250),   // auto-height: steps (name/privacy/done) differ in height
    };

    const double TopGap = 0;        // flush to the top edge — the notch hangs from the top
    const double BottomMargin = 12;  // never grow past the bottom of the work area
    const double CornerRadius = 22;

    // GLASS ONLY: DWM rounds ALL corners of an acrylic window (it can't round just the bottom), which
    // curves the TOP corners that must be flush with the screen edge. So in glass mode the window is
    // extended this many px ABOVE the screen top — the rounded top corners fall off-screen, leaving a
    // straight flush top with the rounded bottom. The content is pushed down by the same amount.
    const double GlassTopBleed = 8;
    int _topBleedPx;   // physical-px version of the current bleed (0 unless glass panel)

    // Panels whose HEIGHT is sized to their actual content (measured), not a hardcoded number — so
    // every page is concise with no dead space below it. The Sizes width is still used; the Sizes
    // height is only a fallback. Scrollable/fixed pages (Notes, Library, ManualNote, Settings) and
    // the peeks keep their fixed height on purpose.
    //
    // Settings is DELIBERATELY not auto-height: its content varies a lot between modes (the API-key
    // view is far taller than the On-device view), and — the real bug — measuring its wrapping text
    // out-of-tree BEFORE the view's first real layout under-reports the height, so the first open
    // came up short (scrollbar) and every later re-open (after one arrange) came up full-size (big
    // notch). A fixed height + the ScrollViewer already in SettingsView makes every open identical.
    static readonly HashSet<PillState> AutoHeight =
        [PillState.Home, PillState.MeetingPrompt, PillState.NoSpeech, PillState.RecordingOpen, PillState.Ask, PillState.PrepCard, PillState.Onboarding];

    // Click-outside dismisses ONLY these ephemeral popups (the launcher menu and the
    // transient "nothing was said" toast). Everything with content you actually work
    // with — Notes, Library, Settings, ManualNote — stays put until you close it with
    // its own button or Esc; a stray click into your real work must never make it
    // vanish. Recording/Summarizing/MeetingPrompt were never dismissable this way.
    // Clicking into your real work fully tucks a stayable page away (→ Hidden). Until then it
    // lives as a hover-able sliver. (Summarizing is excluded — it must stay to reach its
    // result; recording/prompt have their own lifecycles.)
    static readonly HashSet<PillState> DismissOnClickOutside =
        [PillState.Home, PillState.Notes, PillState.NoSpeech, PillState.Settings, PillState.Calendar, PillState.About];

    readonly AppState _state;
    readonly Dictionary<PillState, UserControl> _views = new();
    Interop.InputMonitor? _input;
    long _panelShownTick;
    IntPtr _hwnd;

    // Window bounds in physical pixels, for the (off-thread) click-outside hook.
    int _rectL, _rectT, _rectR, _rectB;

    // The notch surface is the themed "Notch"/"NotchBorder" resources — re-resolved via
    // SetResourceReference (not a cached brush) so a live theme swap recolours the notch too.

    public PillWindow(AppState state)
    {
        InitializeComponent();
        _state = state;
        // Clicking the Toki peek opens the remembered page (Home by default).
        App.OpenPeek = () => { if (_state.Current == PillState.Collapsed) _state.ForceForDemo(_context); };
        // A summarize that finished while the pill was forced elsewhere: retarget the remembered
        // context so an edge-summon restores the RESULT, not a Summarizing loader that never ends.
        App.ResultMissed += t => Dispatcher.Invoke(() => { if (_context == PillState.Summarizing) _context = t; });
        _state.StateChanged += (o, n) => Dispatcher.Invoke(() => OnStateChanged(o, n));
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea) && IsVisible)
                Dispatcher.Invoke(() => { PositionTopCenter(); CacheWindowRect(); });
        };
        LocationChanged += (_, _) => CacheWindowRect();
        // A live theme swap must re-tint the frosted glass (dark frost ⇄ white frost) — the acrylic tint
        // is set via P/Invoke, not a DynamicResource, so it won't recolour on its own.
        Theme.ThemeManager.ThemeChanged += () => Dispatcher.Invoke(() => { if (_blurOn) { EnableAcrylic(); ApplyBlur(true); } });
        // Keep the pill anchored bottom-centre as it grows/shrinks — so an animated resize
        // (the smooth recording expand) stays put instead of drifting from its top-left.
        SizeChanged += (_, _) => { if (IsVisible && _hwnd != IntPtr.Zero) PlaceTopCenter(); CacheWindowRect(); if (_blurOn) ApplyRoundRegion(true); };
        // Fallback for a click that beats the 60ms hover tick: activate on press so the very first
        // click on a button lands (and subsequent wheel/keys route here). Tunnels so it fires before
        // the click reaches the button — activation happens, then the button gets its click.
        PreviewMouseDown += (_, _) => { if (NeedsInput(_state.Current)) TryActivateNotch(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            SetWindowLong(_hwnd, GwlExStyle, GetWindowLong(_hwnd, GwlExStyle) | WsExToolWindow);
            EnableAcrylic(); // real blur-behind so the translucency reads as frosted glass
            // Ghost LAST: WDA_EXCLUDEFROMCAPTURE must be asserted AFTER the ex-style + DWM attribute
            // changes above — window-composition changes are exactly what can drop the affinity.
            ApplyGhost(); // works on this transparent window too
        }
        catch (Exception ex) { App.Log($"window setup failed (non-fatal): {ex.Message}"); }

        _input = new Interop.InputMonitor(
            isArmed: () => DismissOnClickOutside.Contains(_state.Current)
                           && Environment.TickCount64 - _panelShownTick > 500,
            isInsidePanel: PointInWindow,
            dismiss: () => Dispatcher.BeginInvoke(() =>
            {
                // Clicking into your real work TUCKS the page to a sliver (context kept) rather
                // than resetting it — a tucked sliver just stays (its auto-hide takes it away).
                if (!_tucked) TuckCurrent();
            }),
            onWheel: OnHookWheel);
    }

    // A few px of slack so a click on the very EDGE of the notch still reads as "inside" — otherwise a
    // borderline click was judged outside and tucked the panel away, which felt like buttons needing a
    // dead-centre hit. Same slack widens the wheel-capture zone harmlessly.
    const int InsidePad = 6;
    bool PointInWindow(int x, int y) =>
        (_rectR <= _rectL) || (x >= _rectL - InsidePad && x <= _rectR + InsidePad && y >= _rectT - InsidePad && y <= _rectB + InsidePad)
        // A dropdown that dropped BELOW the notch is logically part of it — a click in the model/provider
        // list must never read as "outside" and tuck the panel away mid-selection.
        || Views.NotchPopups.ContainsScreenPoint(x, y);

    void CacheWindowRect()
    {
        if (_hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out var r))
            (_rectL, _rectT, _rectR, _rectB) = (r.Left, r.Top, r.Right, r.Bottom);
    }

    // Global mouse-hook wheel handler (runs on the hook thread). If the pointer is over an interactive
    // notch page, we OWN the wheel: consume it (so the window behind never scrolls) and marshal a smooth
    // scroll of the notch's own ScrollViewer to the UI thread. This is why scrolling works without the
    // notch being the focused window. Returns true to consume.
    bool OnHookWheel(int x, int y, int delta)
    {
        try
        {
            if (!NeedsInput(_state.Current) || !PointInWindow(x, y)) return false;
            Dispatcher.BeginInvoke(() =>
            {
                var sv = CurrentScrollViewer();
                if (sv is not null) Controls.SmoothScroll.Nudge(sv, delta);
            }, System.Windows.Threading.DispatcherPriority.Input);
            return true;   // the notch owns the wheel while you're on it — never scroll the app behind
        }
        catch { return false; }
    }

    // The ScrollViewer of the currently-hosted page (first one in its visual tree), or null.
    ScrollViewer? CurrentScrollViewer()
    {
        if (!_views.TryGetValue(_state.Current, out var view) || view is null) return null;
        return FindScrollViewer(view);
    }

    static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var hit = FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>The pill's current on-screen rectangle in WPF DIPs (for the Library morph).</summary>
    public Rect ScreenDipRect()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(_rectL / dpi.DpiScaleX, _rectT / dpi.DpiScaleY,
                        (_rectR - _rectL) / dpi.DpiScaleX, (_rectB - _rectT) / dpi.DpiScaleY);
    }

    /// <summary>
    /// Place the window TOP-centered (the virtual notch, flush to the top edge) using
    /// PHYSICAL pixels (Win32) — robust to any resolution/DPI, and the top stays fixed so
    /// the pill grows DOWNWARD (blooms out of the top) as it morphs.
    /// </summary>
    void PositionTopCenter()
    {
        if (_hwnd == IntPtr.Zero) return;
        UpdateLayout();
        PlaceTopCenter();
    }

    // Position from the CURRENT physical window size, without forcing layout — safe to call
    // from SizeChanged (which fires inside a layout pass, where UpdateLayout would throw).
    void PlaceTopCenter()
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var wr)) return;
        int winW = wr.Right - wr.Left;
        var work = PhysicalWorkArea();
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        int gap = (int)Math.Round(TopGap * dpi);
        int x = work.Left + ((work.Right - work.Left) - winW) / 2;
        int y = work.Top + gap - _topBleedPx;   // top edge fixed → height grows downward (glass bleeds above)
        // Skip the move when nothing changed — SizeChanged fires this on every frame of a size
        // animation, but for a height-only morph x/y are constant, so the Win32 call is pure waste.
        if (x == _lastPlaceX && y == _lastPlaceY) return;
        _lastPlaceX = x; _lastPlaceY = y;
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }
    int _lastPlaceX = int.MinValue, _lastPlaceY = int.MinValue;

    /// <summary>Set the window's PHYSICAL size AND top-centre position in one Win32 call — used before
    /// Show() on a reveal so the first painted frame is already the right size at the right place (no
    /// stale-width notch flashing off-centre). Sizes from the target DIPs, not the stale current rect.</summary>
    void PlaceExactTopCenter(double dipW, double dipH)
    {
        if (_hwnd == IntPtr.Zero) return;
        var work = PhysicalWorkArea();
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = Math.Max(1, (int)Math.Round(dipW * dpi.DpiScaleX));
        int ph = Math.Max(1, (int)Math.Round(dipH * dpi.DpiScaleY));
        int gap = (int)Math.Round(TopGap * dpi.DpiScaleY);
        int x = work.Left + ((work.Right - work.Left) - pw) / 2;
        int y = work.Top + gap - _topBleedPx;   // glass panels bleed above the screen edge
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, pw, ph, SwpNoZOrder | SwpNoActivate);   // NOT SwpNoSize — set both
        _lastPlaceX = x; _lastPlaceY = y;
    }

    // Work area of the monitor the CURSOR is on — so the notch appears on whichever screen
    // you're using (edge-summon on any monitor's top edge, events on the active screen).
    static RECT PhysicalWorkArea()
    {
        if (GetCursorPos(out var p))
        {
            var mon = MonitorFromPoint(p, MonitorDefaultToNearest);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi) && mi.rcWork.Right > mi.rcWork.Left)
                return mi.rcWork;
        }
        var r = new RECT();
        SystemParametersInfo(SpiGetWorkArea, 0, ref r, 0);
        if (r.Right <= r.Left) { r.Right = 1920; r.Bottom = 1080; }
        return r;
    }

    // Don't summon via the edge while dragging a window (Windows Snap Layouts is in progress)
    // or while a fullscreen app/game/video/presentation is running — stay out of the way.
    static bool EdgeSuppressed()
    {
        if ((GetAsyncKeyState(VkLButton) & 0x8000) != 0) return true;
        return QuietFullscreen();
    }

    /// <summary>True while a fullscreen app / game / presentation is up (the "do not disturb"
    /// signal Windows itself uses for toasts). Shared by the edge-summon, PrepWatcher, and the
    /// meeting-prompt gate — a notch popping over slide 3 of a deck is the overlay cardinal sin.
    /// Suppression only DELAYS a prompt: the detector re-checks every poll and offers the
    /// meeting the moment the user leaves fullscreen.</summary>
    public static bool QuietFullscreen() =>
        SHQueryUserNotificationState(out var st) == 0 &&
        (st == QunsBusy || st == QunsRunningD3dFullScreen || st == QunsPresentationMode);

    UserControl ViewFor(PillState s)
    {
        if (_views.TryGetValue(s, out var v)) return v;
        v = s switch
        {
            PillState.Collapsed     => new CollapsedView(),
            PillState.MeetingPrompt => new MeetingPromptView(),
            PillState.RecordingBar  => new RecordingBarView(),
            PillState.RecordingOpen => new RecordingOpenView(),
            PillState.Summarizing   => new SummarizingView(),
            PillState.Notes         => new NotesView(),
            PillState.NoSpeech      => new NoSpeechView(),
            PillState.Home          => new HomeView(),
            PillState.Library       => new LibraryView(),
            PillState.Settings      => new SettingsView(),
            PillState.ManualNote    => new ManualNoteView(),
            PillState.Ask           => new AskView(),
            PillState.Calendar      => new CalendarView(),
            PillState.PrepCard      => new PrepCardView(),
            PillState.About         => new AboutView(),
            PillState.Onboarding    => new OnboardingView(),
            _ => throw new ArgumentOutOfRangeException(nameof(s)),
        };
        _views[s] = v;
        return v;
    }

    void OnStateChanged(PillState old, PillState next)
    {
        if (next == PillState.Hidden)
        {
            // A result (notes / nothing-was-said) is a ONE-SHOT announcement. Once it's closed,
            // re-summoning must bring back Toki — never the dead result. Forgetting it here is what
            // breaks the "click Okay → hover → nothing-was-said again → forever" loop.
            if (old is PillState.Notes or PillState.NoSpeech) _context = PillState.Home;
            // A short cooldown so a page that just closed off can't instantly re-summon a fresh
            // Toki under a cursor still resting at the top edge (the "both open at once" collision).
            _edgeReadyTick = Environment.TickCount64 + EdgeReadyDelayMs;
            // Disarm the edge-summon: after closing off, the cursor must drop back down into the page
            // and make a fresh reach UP before Toki returns. Without this, a cursor primed earlier
            // (while down in Chrome's content) would re-summon the moment it drifts back to the tabs.
            _edgeArmed = false;
            StartHoverPoll(); HideAnimated(); return; // keep polling: watch the top edge to summon
        }

        var fromHidden = old == PillState.Hidden || !IsVisible;
        var view = ViewFor(next);
        // The recording peek does its dramatic flip only when recording STARTS — on a hover-collapse
        // from the open screen it just settles (see RecordingBarView.FlipOnShow), so minimising is clean.
        if (view is Views.RecordingBarView rbv) rbv.FlipOnShow = old != PillState.RecordingOpen;
        Host.Children.Clear();
        Host.Children.Add(view);
        // Views whose text depends on live app state must be refreshed BEFORE the height is measured
        // below — otherwise the reveal height is computed on stale text and longer text (a wrapped
        // app name in the meeting prompt) renders cut off by the clip (a real live bug).
        if (view is Views.MeetingPromptView mp) mp.Refresh();
        if (view is Views.PrepCardView pc) pc.Refresh();   // paint from App.CurrentPrep BEFORE the height measure
        if (DismissOnClickOutside.Contains(next)) _panelShownTick = Environment.TickCount64;

        var hoverMorph = !fromHidden && IsHoverPair(old, next);
        var collapsing = next is PillState.Collapsed or PillState.RecordingBar;

        // While PEEKING (the resting/recording notch), drop the dark frosted box — Toki is just the
        // character floating from the top edge. But on a hover-COLLAPSE into a peek we KEEP the dark
        // surface and only drop it once the shrink finishes (deferPeekBg), so the box shrinks away
        // smoothly WITH the panel instead of vanishing the instant you hover out (that read as abrupt).
        if (next == PillState.Collapsed) _collapsedTick = Environment.TickCount64; // start the greeting grace
        var peek = next is PillState.Collapsed or PillState.RecordingBar;
        var deferPeekBg = peek && hoverMorph;
        if (!deferPeekBg) SetSurface(peek);

        // A reveal can enter directly as a SLIVER (peek) instead of full open. The LOADER works
        // quietly as a sliver from the very first frame — it never flashes a panel in your face.
        // RESULTS (notes / nothing-was-said) instead announce themselves: they open briefly, then
        // tuck to a sliver on their own (handled below). A re-summon restores a page as a sliver too.
        var enterTucked = (_revealTucked || next == PillState.Summarizing) && IsStayable(next);
        _revealTucked = false;
        _tucked = enterTucked;

        var (w, h) = Sizes[next];
        var dpi = _hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleY : 1.0;
        var work = PhysicalWorkArea();
        // Never exceed the screen — tall panels clamp and scroll internally.
        var availH = (work.Bottom - work.Top) / dpi - TopGap - BottomMargin;
        var fullH = enterTucked ? SliverH : Math.Min(ContentHeight(next, w, h), availH);
        // Glass panels extend above the screen edge (see GlassTopBleed): grow the window by the bleed and
        // push the content down by it, so the off-screen top hides the DWM top-corner rounding.
        var bleed = (Toki.Llm.AppSettings.Current.GlassyNotch && !peek && !enterTucked) ? GlassTopBleed : 0.0;
        _topBleedPx = (int)Math.Round(bleed * dpi);
        Host.Margin = new Thickness(0, bleed, 0, 0);
        var targetH = fullH + bleed;

        // A hover pill expand/collapse (tiny Toki ⇄ Home, or ⇄ recording controls) is
        // animated so it GROWS/SHRINKS smoothly, not a hard size-snap. Other transitions stay instant.
        if (hoverMorph)
        {
            // This path animates size only (no clip). Drop any clip a just-interrupted reveal/conceal
            // left behind, or it would cap this content (a source of the "text cut off" after fast cycling).
            Root.Clip = null; _clipGen++;
            // Collapsing (e.g. recording open → tiny peek) reads best as a smooth zoom-OUT: a slightly
            // longer, eased size retract with the content cross-fading, not a hard snap.
            const int collapseMs = 240;
            AnimateSize(w, targetH, collapsing ? collapseMs : 300);   // SizeChanged re-centres each frame
            Host.BeginAnimation(OpacityProperty, null);
            Host.Opacity = 0;             // the new content fades in as the pill morphs (masks reflow)
            Host.BeginAnimation(OpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(collapsing ? 210 : 240))));
            // Drop the dark surface only once the box has finished shrinking → it fades away with the panel.
            if (deferPeekBg) ScheduleSurface(next, peek, collapseMs);
        }
        else if (fromHidden)
        {
            // Drop DOWN out of the top edge, GPU-COMPOSITED. The old path animated the transparent
            // layered window's Height 8→full, which forced a full window relayout AND a Win32
            // re-center on EVERY frame (the "ugly open"). Now the window is sized to its final
            // bounds ONCE, and the notch unfurls via an animated clip (render-only, no layout, no
            // move) while the content pops in — a clean extend-out-of-the-edge, no jank.
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            Width = w;
            Height = targetH;
            // Clip to ZERO height and hide the content BEFORE Show(), so the very first composited frame
            // is empty — not a full-height black box that then unfurls. That single un-clipped frame was
            // the "black/white glitch" on hover-to-summon. RevealDown animates the clip open from here.
            Root.Clip = new RectangleGeometry(new Rect(0, 0, w, 0));
            Host.Opacity = 0;
            // Force the window to its FINAL physical size + centred position BEFORE Show. Coming from a
            // previously-wider panel, the window was still at the OLD width/left when the first frame
            // painted, so the notch surface flashed off-centre on the LEFT for a frame before snapping to
            // the centred peek — the "asymmetric darker strip on the left" during the drop. Pre-sizing +
            // pre-centring makes the first visible frame already correct.
            PlaceExactTopCenter(w, targetH);
            Show(); ApplyGhost();          // re-apply WDA — hide/show can drop it
            PlaceExactTopCenter(w, targetH);   // re-assert (handles the first-ever show, before the HWND existed)
            if (_blurOn) { ApplyBlur(true); ApplyRoundRegion(true); }   // Show can drop the accent — re-assert glass
            RevealDown(w, targetH, 280);   // animates the 0-height clip open while content settles in
        }
        else
        {
            // In-place morph (visible → visible), no clip on this path — drop any leftover clip from an
            // interrupted reveal/conceal so it can't cap the new content.
            Root.Clip = null; _clipGen++;
            BeginAnimation(WidthProperty, null);   // clear any in-flight resize animation
            BeginAnimation(HeightProperty, null);
            Width = w;
            Height = targetH;
            PositionTopCenter();   // physical-pixel placement, DPI-proof (top-anchored notch)
            MorphContent();
        }
        CacheWindowRect();
        // Re-assert ghost on EVERY state change, not just the hidden→shown reveal. The hover-morph and
        // in-place-morph paths above never re-applied it, and the surface work for panel states
        // (SetSurface → ApplyBlur/ApplyRoundRegion: WCA_ACCENT_POLICY + DWM corner/region changes) can
        // drop WDA_EXCLUDEFROMCAPTURE — which is why ghost held on the recording peek but fell off the
        // Ask/Calendar/Home panels. SetWindowDisplayAffinity is cheap and idempotent.
        ApplyGhost();

        // Poll runs while tucked (Hidden — watch the top edge to summon) and on the peeks
        // (Collapsed launcher peek, recording peek ⇄ open). NOT on Home: Home is a deliberate
        // panel that stays until Esc/click-away — auto-collapsing it would make an explicit
        // summon (launch/hotkey, cursor not on it) vanish instantly.
        // The poll runs while tucked (Hidden — edge summon), on the peeks, and on stayable
        // pages (to detect the hover that brings a tucked sliver back).
        if (next is PillState.Collapsed or PillState.RecordingBar or PillState.RecordingOpen || IsStayable(next))
            StartHoverPoll();
        else StopHoverPoll();

        if (IsStayable(next))
        {
            _context = next;                     // remember the page — re-summoning restores it
            // The loader is a PERSISTENT working sliver: it must not hide itself, it waits for the
            // result (Notes / NoSpeech) to replace it. Everything else counts down.
            if (next == PillState.Summarizing) { StopIdle(); StopAutoHide(); }
            else if (enterTucked) ArmAutoHide(); // entered as a sliver → count down to fully hide
            else ArmIdle();                      // fully open → tuck to a sliver after a beat
        }
        else { StopIdle(); StopAutoHide(); }
    }

    /// <summary>Smoothly animate the window's size (the pill morph) with a gentle ease.
    /// Animates from the CURRENT actual size (explicit From) so a morph that interrupts an
    /// in-flight one can't get stuck holding the old target.</summary>
    void AnimateSize(double w, double h, int ms)
    {
        // Ease-OUT, not ease-in-out: an expanding/morphing notch should decelerate into its new size
        // (Glass's 1−(1−p)³; Material/Fluent agree arriving motion decelerates). Reads noticeably more
        // premium than the old symmetric curve. Central motion token — see Theme/Motion.
        var ease = Theme.Motion.EaseOut;
        var dur = new Duration(TimeSpan.FromMilliseconds(ms));
        var fromW = ActualWidth > 0 ? ActualWidth : w;
        var fromH = ActualHeight > 0 ? ActualHeight : h;
        BeginAnimation(WidthProperty, new DoubleAnimation(fromW, w, dur) { EasingFunction = ease });
        BeginAnimation(HeightProperty, new DoubleAnimation(fromH, h, dur) { EasingFunction = ease });
    }

    /// <summary>Apply the notch surface: a peek is transparent (just Toki floating); an open panel is
    /// the dark frosted box.</summary>
    void SetSurface(bool peek)
    {
        if (peek)
        {
            Root.Background = System.Windows.Media.Brushes.Transparent;
            Root.BorderBrush = System.Windows.Media.Brushes.Transparent;
            ApplyBlur(false); ApplyRoundRegion(false);   // a peek is just Toki floating — no glass
        }
        else if (Toki.Llm.AppSettings.Current.GlassyNotch)
        {
            // REAL frosted glass: an acrylic blur-behind (DWM) dissolves whatever's behind the notch,
            // with a thin dark tint over it for legibility. The Border fill AND rim are transparent so the
            // frost is seamless (a 25%-white rim outlined the glass and read as a floating panel). DWM
            // rounds the corners; DWM's own auto-border is killed in ApplyRoundRegion.
            Root.Background = System.Windows.Media.Brushes.Transparent;
            Root.BorderBrush = System.Windows.Media.Brushes.Transparent;
            ApplyBlur(true); ApplyRoundRegion(true);
        }
        else
        {
            // Opaque tinted-mirror notch (the shipped default). Re-bind to themed resources so a live
            // light/dark swap recolours.
            Root.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Notch");
            Root.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "NotchBorder");
            ApplyBlur(false); ApplyRoundRegion(false);
        }
        // No rim in glass mode (seamless frost); the 1px rim only frames the opaque notch.
        Root.BorderThickness = (peek || Toki.Llm.AppSettings.Current.GlassyNotch) ? default : new Thickness(1, 0, 1, 1);
        Sheen.Visibility = peek ? Visibility.Collapsed : Visibility.Visible;
        // Corner radius is a SINGLE value for both modes so toggling glass never changes the size/shape.
    }

    /// <summary>Apply the peek surface after the collapse finishes, so the dark box shrinks away WITH
    /// the panel instead of vanishing the instant you hover out. No-op if the state changed meanwhile.</summary>
    void ScheduleSurface(PillState forState, bool peek, int ms)
    {
        var t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
            { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); if (_state.Current == forState) SetSurface(peek); };
        t.Start();
    }

    /// <summary>
    /// Hide the pill from screen capture / sharing (WDA_EXCLUDEFROMCAPTURE) — the same
    /// mechanism Electron's setContentProtection uses. It works fine on this transparent
    /// window, but Windows can drop the affinity across hide→show, so we re-apply it
    /// every time the pill is shown.
    /// </summary>
    void ApplyGhost()
    {
        if (_hwnd == IntPtr.Zero) return;
        // Set the affinity in BOTH directions: EXCLUDEFROMCAPTURE hides the notch, NONE reveals it.
        // Applying NONE (not just skipping) matters for the live Settings toggle — turning ghost OFF
        // must actively clear an exclusion that's already on the window.
        try { SetWindowDisplayAffinity(_hwnd, App.GhostDisabled ? WdaNone : WdaExcludeFromCapture); }
        catch (Exception ex) { App.Log($"ghost affinity failed (non-fatal): {ex.Message}"); }
    }

    /// <summary>Re-apply the current ghost setting to the live window (called when the Settings
    /// "Show Toki in screen recordings" toggle changes) — takes effect immediately, no restart.</summary>
    public void RefreshGhost() => ApplyGhost();

    /// <summary>Re-apply the notch surface for the current state (called when the "Glassy notch" toggle
    /// changes) — live, no restart. Peeks stay transparent; only an open panel's surface swaps.</summary>
    public void RefreshSurface()
    {
        var s = _state.Current;
        if (s == PillState.Hidden) { _topBleedPx = 0; ApplyBlur(false); ApplyRoundRegion(false); return; }
        var peek = s is PillState.Collapsed or PillState.RecordingBar;
        SetSurface(peek);
        // Live toggle: add/remove the flush-top bleed on the CURRENT panel (height + content + position)
        // so turning glass on/off updates immediately without a restart.
        var dpi = _hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleY : 1.0;
        var bleed = (Toki.Llm.AppSettings.Current.GlassyNotch && !peek) ? GlassTopBleed : 0.0;
        var delta = bleed - Host.Margin.Top;
        if (Math.Abs(delta) > 0.1)
        {
            Host.Margin = new Thickness(0, bleed, 0, 0);
            _topBleedPx = (int)Math.Round(bleed * dpi);
            Height = Math.Max(1, ActualHeight + delta);
            PositionTopCenter();
        }
    }

    // ── real frosted glass (DWM acrylic blur-behind) for the "Glassy notch" toggle ──
    bool _blurOn;

    /// <summary>Turn the acrylic blur-behind on/off on the live window. When on, whatever is behind the
    /// notch is blurred and tinted into a frosted panel (back text dissolves). Never throws.</summary>
    void ApplyBlur(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        _blurOn = on;
        // Tint follows the theme: a DARK frost in dark mode, a WHITE frost in light mode (AABBGGRR).
        var tint = Theme.ThemeManager.Current == Theme.AppTheme.Light ? 0x9CF6F6FAu : 0x8C141418u;
        var accent = new AccentPolicy
        {
            AccentState = on ? 4 : 0,               // 4 = ACCENT_ENABLE_ACRYLICBLURBEHIND, 0 = disabled
            AccentFlags = 2,
            GradientColor = on ? tint : 0u,
            AnimationId = 0,
        };
        var size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size }; // WCA_ACCENT_POLICY
            SetWindowCompositionAttribute(_hwnd, ref data);
        }
        catch (Exception ex) { App.Log($"blur apply failed (non-fatal): {ex.Message}"); }
        finally { Marshal.FreeHGlobal(ptr); }
        // SetWindowCompositionAttribute(WCA_ACCENT_POLICY) re-composites the window and can drop the
        // capture-exclusion affinity. Every surface swap (state change, theme swap, glass toggle,
        // deferred peek-bg) funnels through here, so re-asserting ghost HERE keeps every notch state —
        // Ask, Calendar, Home, PrepCard, Onboarding, MeetingPrompt, Notes, Summarizing — invisible to
        // screen capture. Idempotent; also correctly applies WDA_NONE when ghost is off.
        ApplyGhost();
    }

    /// <summary>Round the acrylic window's corners. A window REGION can't do this — DWM draws the
    /// acrylic backdrop across the full rectangle behind the window, ignoring the region — so we ask DWM
    /// itself (DWMWA_WINDOW_CORNER_PREFERENCE, Win11) to round the corners, which DOES round the backdrop.
    /// The notch top sits at the screen edge, so its rounding is off-screen; only the bottom reads.</summary>
    void ApplyRoundRegion(bool rounded)
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            SetWindowRgn(_hwnd, IntPtr.Zero, true);   // clear any region from the earlier (wrong) approach
            int pref = rounded ? DwmwcpRound : DwmwcpDoNotRound;
            DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
            // DWM draws a thin border around a rounded window (all four edges) — kill it so the top edge
            // stays flush with the screen (the notch reads as carved into the edge, not a floating pill).
            int border = rounded ? DwmwaColorNone : DwmwaColorDefault;
            DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch (Exception ex) { App.Log($"corner rounding failed (non-fatal): {ex.Message}"); }
        // SetWindowRgn + the DWM corner/border attribute changes above are further composition changes
        // that can shed WDA_EXCLUDEFROMCAPTURE — and this runs AFTER ApplyBlur in every surface swap,
        // so it must re-assert ghost too (last writer wins).
        ApplyGhost();
    }

    /// <summary>
    /// Translucent glass surface. We do NOT use the DWM accent "acrylic" here: it fills
    /// the whole rectangular window (ignoring our rounded corners), which showed as ugly
    /// sharp black corners around the pill. Instead the translucency is the Border's own
    /// semi-transparent fill on a transparent window — genuinely see-through, with clean
    /// rounded corners and no black box. Ghost mode still hides it from capture.
    /// </summary>
    void EnableAcrylic()
    {
        if (Environment.OSVersion.Version.Build < 22000) return;
        try
        {
            var dark = Theme.ThemeManager.Current == Theme.AppTheme.Light ? 0 : 1;
            DwmSetWindowAttribute(_hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch (Exception ex) { App.Log($"dark mode attr failed (non-fatal): {ex.Message}"); }
    }

    void MorphContent()
    {
        // Buttery: content settles in with a soft pop + fade (window bg stays dark).
        HostScale.ScaleX = HostScale.ScaleY = 0.97;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(200));
        HostScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
        HostScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
        Host.Opacity = 0;
        Host.BeginAnimation(OpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(160))));
    }

    void HideAnimated()
    {
        // Retract UP into the top edge, GPU-COMPOSITED: only an animated clip + content fade run —
        // the window is NOT resized per frame (the old close animated Window.Height → relayout every
        // frame). Reads as the notch retracting cleanly into the edge, then hides.
        ConcealUp(ActualWidth, 170, Hide);
    }

    /// <summary>Composited "unfurl": reveal the notch downward via an animated clip (render-only — no
    /// layout, no window resize, no SetWindowPos) while the content settles in. The window must
    /// already be at its final size. The initial 0-height clip is set synchronously so no full-height
    /// dark box flashes before the first frame.</summary>
    void RevealDown(double w, double h, int ms)
    {
        var gen = ++_clipGen;
        var geo = new RectangleGeometry(new Rect(0, 0, w, 0));
        Root.Clip = geo;
        var anim = new RectAnimation(new Rect(0, 0, w, 0), new Rect(0, 0, w, h),
            new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        // Drop the clip so content isn't capped — but ONLY if this reveal is still the latest clip op. A
        // rapid re-open/close can supersede it; a late-firing Completed must not clobber the new clip.
        anim.Completed += (_, _) => { if (gen == _clipGen) Root.Clip = null; };
        geo.BeginAnimation(RectangleGeometry.RectProperty, anim);
        MorphContent();   // content settles in with the soft pop + fade
    }

    /// <summary>Composited "retract": conceal the notch upward via an animated clip + fade (render-only),
    /// then run <paramref name="done"/> (Hide). No per-frame window resize.</summary>
    void ConcealUp(double w, int ms, Action done)
    {
        var gen = ++_clipGen;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        var geo = new RectangleGeometry(new Rect(0, 0, w, h));
        Root.Clip = geo;
        var anim = new RectAnimation(new Rect(0, 0, w, h), new Rect(0, 0, w, 0),
            new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        // Only finish the hide if this conceal is STILL the latest clip op. If a re-open (RevealDown)
        // superseded it, this late Completed must NOT null the new reveal's clip and must NOT Hide() the
        // window we just brought back — that was the "reopen right after close → blank/clipped notch" bug.
        anim.Completed += (_, _) => { if (gen != _clipGen) return; Root.Clip = null; done(); };
        geo.BeginAnimation(RectangleGeometry.RectProperty, anim);
        Host.BeginAnimation(OpacityProperty, null);
        Host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(Math.Max(60, ms - 20)))));
    }

    // Monotonic token: every clip animation (reveal / conceal) captures the current value; its Completed
    // handler no-ops if a newer clip op has since bumped this. Prevents a stale animation that finishes
    // late from clobbering the current clip (the corruption that built up across rapid open/close cycles).
    int _clipGen;

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) App.CloseOverlay();   // recording-aware: never hides a live mic
    }

    // ── Hover-to-expand while recording (WisprFlow-style) ──
    // The recording pill is a tiny alive Toki; resting the pointer on it expands it to the
    // timer + Stop, moving away collapses it back — no click, never in the way.
    //
    // We do NOT use WPF MouseEnter/Leave here: while the pill animates its size and
    // repositions under a stationary cursor, WPF's hit-testing fires phantom leave/enter
    // events, which flip-flopped it open/closed endlessly. Instead we poll the REAL cursor
    // position against the pill's REAL rectangle. Because the expanded rect contains the
    // collapsed one, that gives clean hysteresis: on it → open; off it → closed; no flicker.
    const int HoverPollMs = 60;
    const int ExpandDwellMs = 260;   // recording peek → controls: quick (you want Stop fast)
    const int OpenDwellMs = 850;     // launcher peek → Home: DELIBERATE. A brief hover just shows
                                     // the peek; you must clearly wait (or click) to open the window.
    const int CollapseDelayMs = 120; // close soon after leaving — closing should feel snappy
    const int TuckDelayMs = 180;     // launcher peek tucks away this long after leaving edge+peek
    const int CollapsedGraceMs = 3000; // Toki peek greets for at least this long before it can tuck away
    long _collapsedTick;

    // Phase 2 — reach the top-centre EDGE to summon the notch. Tuned to not fire when you're
    // just reaching for browser tabs / window controls up top: a NARROW centre band, the
    // ABSOLUTE top pixel row, a short dwell (press against the edge), and a velocity gate
    // (ignore fast flings — only a deliberate settle summons it).
    const int EdgeBandHalf = 90;     // half-width of the reveal zone (centre only)
    const int EdgeTopPx = 2;         // within this many px of the very top edge
    const int EdgeDwellMs = 300;     // hold at the edge this long to summon (deliberate, not a passing tab click)
    const int EdgeMaxStep = 40;      // per-tick cursor move above which it's a "fling", not a summon
    const int EdgeReArmBelowPx = 150; // the cursor must drop at least this far below the top edge before
                                      // it can summon again — so a cursor living up on the browser tab
                                      // strip can't keep re-summoning a just-closed-off Toki.
    static readonly bool EdgeSummon = Environment.GetEnvironmentVariable("TOKI_NO_EDGE") is null;

    readonly System.Windows.Threading.DispatcherTimer _hoverPoll =
        new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(HoverPollMs) };
    int _overMs, _offMs, _tuckMs, _edgeMs, _lastX, _lastY;
    bool _hoverWired, _wasOver, _hasLast;
    bool _edgeArmed;   // true once the cursor has dropped clear of the top edge — a deliberate summon
                       // is a reach UP from the page body, never a cursor already parked up top.

    // The two hover pairs: resting pill → revealed view. Same behaviour for the launcher
    // (tiny Toki → Home) and recording (tiny Toki → timer+Stop).
    static PillState? ExpandTarget(PillState s) => s switch
    {
        PillState.Collapsed => PillState.Home,
        PillState.RecordingBar => PillState.RecordingOpen,
        _ => null,
    };
    static PillState? CollapseTarget(PillState s) => s switch
    {
        // Home does NOT auto-collapse — it's dismissed explicitly (Esc/click-away).
        PillState.RecordingOpen => PillState.RecordingBar,
        _ => null,
    };
    static bool IsHoverPair(PillState a, PillState b) =>
        ExpandTarget(a) == b || CollapseTarget(a) == b
        || (a == PillState.Collapsed && IsStayable(b));   // Toki peek → the remembered page blooms open

    void EnsureHoverPoll()
    {
        if (_hoverWired) return;
        _hoverWired = true;
        _hoverPoll.Tick += (_, _) => HoverTick();
    }

    void StartHoverPoll()
    {
        EnsureHoverPoll();
        _overMs = _offMs = _tuckMs = _edgeMs = 0;
        _wasOver = _hasLast = false;
        if (!_hoverPoll.IsEnabled) _hoverPoll.Start();
    }

    void StopHoverPoll()
    {
        if (_hoverPoll.IsEnabled) _hoverPoll.Stop();
    }

    // ── Persistent, context-aware tucked sliver ──
    // A "stayable" page (Home, Notes, the nothing-was-said toast, the loader) does NOT sit
    // fully open on your screen and it does NOT rebuild every time. After ~5s without a hover
    // it TUCKS to a thin sliver (a little showing under the top edge) — the view is kept
    // intact behind it. Hover the sliver and the SAME page slides back down (no reload, state
    // preserved). It stays like that until you click outside (→ fully tucked away / Hidden).
    const double SliverH = 13;
    const int IdleTuckMs = 2500;       // a deliberate page (Home/Settings) tucks this long after you stop hovering
    const int ResultShowMs = 2000;     // a result (Notes / nothing-was-said) announces itself this long, then tucks
    const int AutoHideMs = 9000;       // a tucked sliver fully hides after this (context remembered)
    const int NotesSliverHideMs = 120000; // a ready-notes sliver lingers this long (2 min) so you can come
                                          // back and read it, then self-clears so it can't block a new meeting
    const int EdgeReadyDelayMs = 700;  // after anything tucks to Hidden, wait this long before the edge can re-summon
                                       // — stops a just-hidden sliver from instantly popping a fresh Toki (the "both open" collision)
    long _edgeReadyTick;
    bool _tucked;
    bool _revealTucked;                // next reveal enters as a sliver (peek), not full open
    PillState _context = PillState.Home; // the page Toki remembers — re-summoning restores it
    // Input priority (NOT the DispatcherTimer default of Background) — otherwise these get
    // starved while the app is warming the ASR/LLM models at launch, so pages that show
    // during warm-up (Home, the loader) never tucked. This was the "works in the demo but
    // not in real use" bug.
    readonly System.Windows.Threading.DispatcherTimer _idle =
        new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(IdleTuckMs) };
    readonly System.Windows.Threading.DispatcherTimer _autoHide =
        new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(AutoHideMs) };
    bool _idleWired, _autoHideWired;

    static bool IsStayable(PillState s) =>
        s is PillState.Home or PillState.Notes or PillState.NoSpeech or PillState.Summarizing or PillState.Settings or PillState.Calendar or PillState.About;

    // Pages the user actually clicks/scrolls/types on — everything except the tiny passive peeks.
    // These grab foreground focus on hover so the scroll wheel and clicks land on the notch, not the
    // window behind it. The peeks stay passive (they must never steal focus from a live meeting app).
    static bool NeedsInput(PillState s) =>
        s is not (PillState.Hidden or PillState.Collapsed or PillState.RecordingBar);

    // Bring the notch to the foreground so it receives wheel/keyboard/click input. Because the cursor is
    // over our window we hold the "last input" right, so Activate() (SetForegroundWindow under the hood)
    // is granted rather than merely flashing. Best-effort — never let a focus race throw.
    void TryActivateNotch()
    {
        try { if (!IsActive) Activate(); } catch { /* foreground-lock race — harmless */ }
    }

    void ArmIdle()
    {
        if (!_idleWired)
        {
            _idleWired = true;
            _idle.Tick += (_, _) =>
            {
                _idle.Stop();
                var cur = _state.Current;
                if (!IsStayable(cur) || _tucked) return;
                // Never tuck a page you're actively TYPING in — the cursor rests off the notch while you
                // type in a field (the create composer, the API key box), and idle-tucking then would eat
                // your input. Keep re-arming while a text field has focus.
                if (PillHasFocusedInput()) { ArmIdle(); return; }
                // Only tuck once you've stopped hovering — never yank a page away while you're
                // reading it (hovering re-arms this timer in HoverTick).
                if (!CursorOverPill()) TuckCurrent();
            };
        }
        // Results announce briefly (2s) then tuck; deliberate pages linger a touch longer.
        _idle.Interval = TimeSpan.FromMilliseconds(
            _state.Current is PillState.Notes or PillState.NoSpeech ? ResultShowMs : IdleTuckMs);
        _idle.Stop();
        _idle.Start();
    }

    void StopIdle() { if (_idle.IsEnabled) _idle.Stop(); }

    void ArmAutoHide(int ms = AutoHideMs)
    {
        if (!_autoHideWired)
        {
            _autoHideWired = true;
            _autoHide.Tick += (_, _) => { _autoHide.Stop(); if (_tucked && !CursorOverPill()) App.CloseOverlay(); };
        }
        _autoHide.Interval = TimeSpan.FromMilliseconds(ms);
        _autoHide.Stop();
        _autoHide.Start();
    }

    void StopAutoHide() { if (_autoHide.IsEnabled) _autoHide.Stop(); }

    void TuckCurrent()
    {
        // The LOADER and a READY NOTE keep a hover-able sliver rather than closing off:
        //  • the loader's sliver is its "I'm working" heartbeat (persistent until the result);
        //  • a ready note tucks to a sliver you can leave, go do something, then come back and hover
        //    to read again — but it self-clears after a generous while so it can't block the next
        //    meeting forever.
        if (_state.Current is PillState.Summarizing or PillState.Notes)
        {
            _tucked = true;
            AnimateSize(ActualWidth, SliverH, 200);   // retract to a sliver (top-anchored: hangs a little)
            if (_state.Current == PillState.Notes) ArmAutoHide(NotesSliverHideMs);
            return;
        }
        // EVERYTHING ELSE closes off completely (context kept) rather than leaving a sliver
        // hanging in your way while you work in another app. You bring it back — as whatever
        // Toki was showing — by hovering the top-centre edge. Workflow stays undisturbed.
        App.CloseOverlay();   // recording-aware: a live session collapses to the bar, never vanishes
    }

    void ExpandCurrent()
    {
        _tucked = false;
        StopAutoHide();
        var (w, h) = Sizes[_state.Current];
        var dpi = _hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleY : 1.0;
        var work = PhysicalWorkArea();
        var availH = (work.Bottom - work.Top) / dpi - TopGap - BottomMargin;
        AnimateSize(w, Math.Min(ContentHeight(_state.Current, w, h), availH), 220);
        ArmIdle();
    }

    /// <summary>
    /// The height a panel should grow to. For an auto-height page it's the view's MEASURED content
    /// height (so the page is concise — no dead space below the content), measured at the panel's
    /// fixed width. Other pages use their fixed Sizes height.
    /// </summary>
    double ContentHeight(PillState state, double width, double fallback)
    {
        if (!AutoHeight.Contains(state)) return fallback;
        try
        {
            var v = ViewFor(state);
            v.Measure(new Size(Math.Max(1, width - 2), double.PositiveInfinity));   // −2 for the L/R border
            var m = v.DesiredSize.Height;
            // The design height is a MINIMUM, never a cap (research §3d: measured content grows past it).
            // The content MEASURES accurately (verified via --measuretest), but the notch was still
            // rendering a hair short of it — DPI physical-pixel rounding + the surface chrome shave a few
            // px off the bottom, clipping the last row (the account footer's email + Library button). So
            // add a small, honest bottom breathing pad: enough that the final line always fits with a
            // little air, cheap on pages that don't need it.
            return m > 8 ? Math.Max(fallback, Math.Ceiling(m) + BottomBreath) : fallback;
        }
        catch { return fallback; }
    }

    // Bottom breathing room added to every auto-height page's measured content (see ContentHeight). The
    // content measures correctly; this covers the few px lost to DPI rounding + the notch's bottom chrome
    // so the last line is never clipped and sits with a touch of air above the rounded bottom edge.
    const double BottomBreath = 14;

    /// <summary>Re-measure the current AUTO-HEIGHT view and smoothly grow/shrink the notch to fit — for
    /// when a page's content changes size WITHOUT a state change (the Ask notch going from the short
    /// "listening" state to the taller answer).</summary>
    bool _growing;
    public void GrowToContent()
    {
        // Re-entrancy guard: GrowToContent → ContentHeight → ViewFor could, if a view triggers a grow
        // from its own constructor (before it's cached), recurse forever building duplicate views. Never
        // let this method re-enter itself.
        if (_growing) return;
        var s = _state.Current;
        if (!AutoHeight.Contains(s)) return;
        _growing = true;
        try
        {
            var (w, h) = Sizes[s];
            var dpi = _hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleY : 1.0;
            var work = PhysicalWorkArea();
            var availH = (work.Bottom - work.Top) / dpi - TopGap - BottomMargin;
            var target = Math.Min(ContentHeight(s, w, h), availH);
            if (Math.Abs(target - ActualHeight) < 2) return;
            AnimateSize(w, target, 150);   // snappy so it keeps up as the answer streams in patches
        }
        finally { _growing = false; }
    }

    void HoverTick()
    {
        var cur = _state.Current;

        // Tucked away: watch the top-centre edge to summon the peek.
        if (cur == PillState.Hidden) { EdgeTick(); return; }

        var over = CursorOverPill();

        // Stayable pages: tuck to a sliver after idle; hover the sliver to bring the SAME
        // page back; keep it up while you're on it. Context is preserved (no rebuild).
        if (IsStayable(cur))
        {
            if (_tucked) { if (over) ExpandCurrent(); }               // hover the sliver → same page back
            else if (over && cur != PillState.Summarizing) ArmIdle(); // hovering keeps a page open (loader tucks regardless)
            return;
        }

        // Cute: Toki blinks the moment the pointer lands on the resting peek.
        if (over && !_wasOver && (cur == PillState.Collapsed || cur == PillState.RecordingBar)) BlinkCurrentToki();
        _wasOver = over;

        if (ExpandTarget(cur) is PillState expand)
        {
            // The Toki peek opens your REMEMBERED page (_context, default Home) — deliberately
            // (long dwell, or a click → CollapsedView.OnOpen). The recording peek opens quickly.
            var dwell = cur == PillState.Collapsed ? OpenDwellMs : ExpandDwellMs;
            _offMs = 0;
            _overMs = over ? _overMs + HoverPollMs : 0;
            if (_overMs >= dwell)
            {
                _overMs = 0;
                if (cur == PillState.Collapsed) _state.ForceForDemo(_context); else _state.TryGo(expand);
                return;
            }

            // The LAUNCHER peek (Collapsed) tucks back to Hidden once you leave both the peek
            // and the top edge. (The recording peek stays put — recording is ongoing.)
            if (cur == PillState.Collapsed && !over && !CursorNearTopEdge()
                && Environment.TickCount64 - _collapsedTick > CollapsedGraceMs)
            {
                _tuckMs += HoverPollMs;
                if (_tuckMs >= TuckDelayMs) { _tuckMs = 0; _state.TryGo(PillState.Hidden); }
            }
            else _tuckMs = 0;
        }
        else if (CollapseTarget(cur) is PillState collapse)
        {
            _overMs = 0;
            _offMs = over ? 0 : _offMs + HoverPollMs;
            if (_offMs >= CollapseDelayMs) { _offMs = 0; _state.TryGo(collapse); }
        }
        else StopHoverPoll(); // not a hover state anymore
    }

    // Edge-summon: accumulate dwell only while the cursor is pressed to the very top of the
    // centre band AND moving slowly. A fast reach toward a browser tab won't cross it.
    void EdgeTick()
    {
        if (!EdgeSummon || EdgeSuppressed() || !GetCursorPos(out var p)) { _edgeMs = 0; _hasLast = false; return; }
        var work = PhysicalWorkArea();
        int cx = work.Left + (work.Right - work.Left) / 2;

        // Re-arm the summon only once the cursor has dropped clear of the top edge. A real summon is
        // a reach UP to the notch from the page — NOT a cursor that just lives up on the browser's
        // tab strip. This is what makes "close off" actually STICK: right after Toki closes off your
        // cursor is still up top (on Chrome's tabs), and without this it would instantly re-summon.
        if (p.Y > work.Top + EdgeReArmBelowPx) _edgeArmed = true;

        // Brief cooldown right after a close-off. Still track motion so the dwell starts clean after.
        if (Environment.TickCount64 < _edgeReadyTick) { _edgeMs = 0; _lastX = p.X; _lastY = p.Y; _hasLast = true; return; }

        bool atEdge = p.Y <= work.Top + EdgeTopPx && Math.Abs(p.X - cx) <= EdgeBandHalf;
        int dx = _hasLast ? p.X - _lastX : 0, dy = _hasLast ? p.Y - _lastY : 0;
        bool slow = dx * dx + dy * dy <= EdgeMaxStep * EdgeMaxStep;
        _lastX = p.X; _lastY = p.Y; _hasLast = true;

        if (_edgeArmed && atEdge && slow)
        {
            _edgeMs += HoverPollMs;
            if (_edgeMs >= EdgeDwellMs)
            {
                _edgeMs = 0;
                _edgeArmed = false;   // consume it — the cursor must drop back down before summoning again
                // If Toki is mid-task (writing your notes), reaching the edge brings that back so you
                // can check on it — "access whatever Toki is doing". Otherwise Toki peeks first.
                _state.ForceForDemo(_context == PillState.Summarizing ? PillState.Summarizing : PillState.Collapsed);
            }
        }
        else _edgeMs = 0;
    }

    // Looser top-edge test used to keep the launcher peek alive while the cursor bridges
    // between the peek and the very top edge (so it doesn't tuck the instant you dip up).
    bool CursorNearTopEdge()
    {
        if (!GetCursorPos(out var p)) return false;
        var work = PhysicalWorkArea();
        int cx = work.Left + (work.Right - work.Left) / 2;
        return p.Y <= work.Top + EdgeTopPx + 8 && Math.Abs(p.X - cx) <= EdgeBandHalf + 30;
    }

    // Find the Toki character in the current view and make it blink.
    void BlinkCurrentToki()
    {
        var toki = FindDescendant<Controls.TokiBlob>(Host);
        toki?.BlinkNow();
    }

    static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is T deep) return deep;
        }
        return null;
    }

    // The physical cursor position, tested against the pill's cached physical rectangle.
    bool CursorOverPill()
    {
        // While a notch dropdown is open, keep the panel "held" — the cursor is likely down in the
        // list (below the notch), which must not trigger the idle-tuck.
        if (Views.NotchPopups.AnyOpen) return true;
        if (!GetCursorPos(out var p) || _rectR <= _rectL) return false;
        return p.X >= _rectL && p.X <= _rectR && p.Y >= _rectT && p.Y <= _rectB;
    }

    // True while the user is typing in a field inside the notch (create composer, API key, …) — used to
    // suppress the idle-tuck so a resting cursor off the notch can't close a page mid-input.
    bool PillHasFocusedInput() =>
        IsKeyboardFocusWithin
        && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox;

    // ---- interop ----
    const int GwlExStyle = -20;
    const int WsExToolWindow = 0x00000080;
    const uint WdaExcludeFromCapture = 0x11;
    const uint WdaNone = 0x00;   // visible to capture — ghost mode off

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int newLong);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref RECT pv, uint winIni);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    const uint SpiGetWorkArea = 0x0030;
    const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    const int VkLButton = 0x01, MonitorDefaultToNearest = 0x2;
    // SHQueryUserNotificationState values that mean "don't interrupt" (fullscreen/game/presentation).
    const int QunsBusy = 2, QunsRunningD3dFullScreen = 3, QunsPresentationMode = 4;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    const int DwmwaUseImmersiveDarkMode = 20;
    const int DwmwaWindowCornerPreference = 33;   // Win11: round the window (and its acrylic) corners
    const int DwmwcpDoNotRound = 1, DwmwcpRound = 2;
    const int DwmwaBorderColor = 34;              // colour of the auto-drawn window border
    const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);     // DWMWA_COLOR_NONE — no border
    const int DwmwaColorDefault = unchecked((int)0xFFFFFFFF);  // DWMWA_COLOR_DEFAULT
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Acrylic blur-behind (undocumented but stable since Win10 1803, used by Files/many WPF apps).
    [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)] struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
}
