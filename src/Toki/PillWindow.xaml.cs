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

/// <summary>The single overlay: a borderless top-center notch window that morphs between states
/// and hosts one view at a time, plus the floating mascot bubble beside the card.</summary>
public partial class PillWindow : Window
{
    // Logical-pixel content sizes per state. For AutoHeight pages the HEIGHT is only a fallback -
    // the real height is measured from the content. Width is always used.
    /// <summary>ONE WIDTH FOR EVERY PAGE.
    ///
    /// The panels used to be 330, 344, 356, 360, 380, 392 and 404 wide - seven widths across nine
    /// pages. The card is centred on the screen, so every navigation moved BOTH edges and re-wrapped
    /// every line inside. The owner, 2026-09-26: "toki readjusts and re renders everytime a notch
    /// state is changed, when we move from one notch page to another, that is simply bad design, and
    /// a real premium product never works like that."
    ///
    /// They are right, and the reason is not performance - the views are cached and the morph is
    /// already one composited clip. It is that a container which changes shape for every page is not
    /// a container; it reads as a new window each time. A notch is furniture. It holds still while
    /// its contents change.
    ///
    /// Height still varies, because height is what CONTENT legitimately controls, and it grows
    /// downward from a fixed top edge - one axis moving instead of three.
    ///
    /// PEEKS AND STATUS PILLS ARE NOT PAGES and keep their own sizes: the launcher peek, the record
    /// prompt, the recording bar and the loader are glanceable chrome sized to their content, and
    /// making them 380 wide would put a mostly-empty card over the user's work.</summary>
    internal const double PanelW = 380;

    static readonly Dictionary<PillState, (double W, double H)> Sizes = new()
    {
        [PillState.Collapsed]     = (52, 42),    // launcher peek (Toki alone)
        [PillState.MeetingPrompt] = (169, 40),   // "Record this meeting" pill
        [PillState.RecordingBar]  = (34, 14),    // TINY eyes-peek - never in the way of browser tabs / snip toolbar
        [PillState.RecordingOpen] = (272, 48),   // recording bar. RecordingOpenView owns the live
                                                 // width (IWidthAware): its chevron widens it to 344.
        [PillState.Summarizing]   = (220, 50),   // "Tidying up your notes..." pill (lives mostly as a sliver)
        [PillState.Notes]         = (PanelW, 320),  // scrollable window into the notes
        [PillState.NoSpeech]      = (PanelW, 92),
        [PillState.Home]          = (PanelW, 190),   // fits the COMING UP list + Join pills
        [PillState.Library]       = (PanelW, 440),
        [PillState.Settings]      = (PanelW, 120),   // auto-height; 120 is only the floor
        [PillState.ManualNote]    = (PanelW, 410),
        [PillState.Ask]           = (PanelW, 120),   // push-to-talk: compact while listening, grows for the answer (AutoHeight)
        [PillState.Calendar]      = (PanelW, 430),   // FIXED height + internal scroll (auto-height fought the ScrollViewer)
        // 293, not 306: Inter sets these rows a little tighter than Segoe UI Variable did, so the
        // old floor held 13px of space the card never filled and every prep card carried a band of
        // dead air under its last row (--measuretest reports it by name). A floor is a claim about
        // CONTENT, so it has to be re-measured whenever the typeface changes.
        [PillState.PrepCard]      = (PanelW, 293),   // auto-height: first-time cards are short
        [PillState.About]         = (PanelW, 430),   // FIXED height + internal scroll (credits list) - mirrors Settings
        // AUTO-HEIGHT: each step sizes to its own content (name ~188, privacy ~212, all-set ~137), so
        // no page is padded or clipped. 130 is only the floor. The step-to-step size change is SNAPPED,
        // not animated (see SnapResize) - the next page simply appears at its size, so the user never
        // watches the card actively resize.
        // NOT PanelW. The one-width rule exists so NAVIGATING between pages does not reshape the
        // card; onboarding is a standalone first-run flow you never navigate away from and never
        // come back to, so it has no neighbour to be consistent with. It is also better narrow: a
        // 330 measure keeps the three short steps readable instead of stretching two sentences
        // across a panel built for notes. Widening it to 380 collapsed the height difference
        // between the name and privacy steps (219 vs 226), which --revealtest caught.
        [PillState.Onboarding]    = (330, 130),
    };

    // ── the HOVERING card ──
    // The card floats a little below the top edge and the window is bigger than it: a left zone
    // where Toki floats beside it, and right/bottom room for the shadow. EVERY size in this file
    // speaks CARD dimensions; the two conversion helpers below are the only place window
    // dimensions are derived, so the pads can never be added twice.
    // Two flush exceptions: RECORDING sits AT the top (a status strip, not a floating panel), and
    // the COLLAPSED edge-summon peek is FLUSH with the edge.
    const double TopGapDefault = 12;
    const double TopGapRecording = 2;
    double TopGap => TopGapFor(_state.Current);

    /// <summary>How far below the top edge a state's card hangs. Summarizing shares the flush arm:
    /// the loader is the same session as the recording it follows and must not sit lower than it.</summary>
    internal static double TopGapFor(PillState s) => s switch
    {
        PillState.RecordingBar or PillState.RecordingOpen or PillState.Summarizing => TopGapRecording,
        PillState.Collapsed => 0,
        _ => TopGapDefault,
    };
    const double BottomMargin = 12;  // never grow past the bottom of the work area
    const double CornerRadius = 24;
    const double CardPadL = 68;      // Toki's floating zone, left of the card (58px bubble + 10px air)
    const double CardPadR = 20;      // shadow room
    const double CardPadB = 24;      // shadow room
    static double WinW(double cardW) => cardW + CardPadL + CardPadR;
    // The window must always be tall enough to hold TOKI's 58px bubble, not just the card - a
    // sliver-height card would otherwise clip the circle. The extra is transparent pad below the
    // card; the card itself does not change size or position.
    const double MascotZoneH = 58;   // must match MascotZone's Height in the XAML
    static double WinH(double cardH) => Math.Max(cardH + CardPadB, MascotZoneH + 8);
    static double CardW(double winW) => Math.Max(1, winW - CardPadL - CardPadR);
    static double CardH(double winH) => Math.Max(1, winH - CardPadB);

    // GLASS ONLY: DWM rounds ALL corners of an acrylic window (it can't round just the bottom), which
    // curves the TOP corners that must be flush with the screen edge. So in glass mode the window is
    // extended this many px ABOVE the screen top - the rounded top corners fall off-screen, leaving a
    // straight flush top with the rounded bottom. The content is pushed down by the same amount.

    // Panels whose HEIGHT is measured from their actual content, so every page is concise with no
    // dead space below it. The Sizes width is still used; the Sizes height is only a fallback.
    // Scrollable/fixed pages (Notes, Library, ManualNote) and the peeks keep their fixed height on purpose.
    static readonly HashSet<PillState> AutoHeight =
        [PillState.Home, PillState.MeetingPrompt, PillState.NoSpeech, PillState.RecordingOpen, PillState.Ask,
         PillState.PrepCard, PillState.Onboarding, PillState.Settings];

    // Auto-height states whose step-to-step size change must be INSTANT, not the 150ms morph the Ask
    // notch uses. Onboarding is a stepped flow: each page has its own height, but the resize between
    // them must not be a visible animation - the next page appears already at its size.
    static readonly HashSet<PillState> SnapResize = [PillState.Onboarding];

    /// <summary>States whose VIEW draws the visible capsules itself (separate floating pills with
    /// air between them) rather than sitting inside the one glass card. The window still supplies
    /// the mascot bubble and the morph; it just stops drawing a surface.</summary>
    static readonly HashSet<PillState> DrawsOwnCapsules = [PillState.MeetingPrompt];

    /// <summary>Does this state draw its own capsules instead of sitting on the glass card? The
    /// render harness has to ask, or its PNGs show a card the running app does not draw.</summary>
    internal static bool IsCapsuleState(PillState s) => DrawsOwnCapsules.Contains(s);

    // Click-outside fully tucks ONLY these pages away (→ Hidden, context kept). Summarizing is
    // excluded - it must stay to reach its result; recording/prompt have their own lifecycles.
    static readonly HashSet<PillState> DismissOnClickOutside =
        [PillState.Home, PillState.Notes, PillState.NoSpeech, PillState.Settings, PillState.Calendar, PillState.About];

    readonly AppState _state;
    readonly Dictionary<PillState, UserControl> _views = new();
    Interop.InputMonitor? _input;
    long _panelShownTick;
    IntPtr _hwnd;

    // Window bounds in physical pixels, for the (off-thread) click-outside hook.
    int _rectL, _rectT, _rectR, _rectB;

    // The notch surface is the themed "Notch"/"NotchBorder" resources - re-resolved via
    // SetResourceReference (not a cached brush) so a live theme swap recolours the notch too.

    public PillWindow(AppState state)
    {
        InitializeComponent();
        _state = state;
        LogClicksThatMissAButton();
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
        // A live theme swap must re-tint the frosted glass (dark frost ⇄ white frost) - the acrylic tint
        // is set via P/Invoke, not a DynamicResource, so it won't recolour on its own.
        Theme.ThemeManager.ThemeChanged += () => Dispatcher.Invoke(() =>
        {
            if (_blurOn) { EnableAcrylic(); ApplyBlur(true); }
            ApplyShadowDepth();   // the card's two shadows lighten in the light theme
        });
        ApplyShadowDepth();
        // Keep the pill anchored top-centre as it grows/shrinks - an animated resize must stay
        // put instead of drifting from its top-left.
        SizeChanged += (_, _) => { if (IsVisible && _hwnd != IntPtr.Zero) PlaceTopCenter(); CacheWindowRect(); if (_blurOn) ApplyRoundRegion(true); };
        // The resting squircle clip and rim are built for the card's ARRANGED box, so rebuild them
        // whenever that box changes. Root.SizeChanged covers all cases and never fires per
        // animation frame - the window is sized twice per transition, not per frame.
        Root.SizeChanged += (_, _) => ApplyCornerGeometry();
        DpiChanged += (_, _) => ApplyCornerGeometry();   // a 1 DIP rim is a different number of device px
        // Fallback for a click that beats the 60ms hover tick: activate on press so the very first
        // click on a button lands. Tunnels, so activation happens before the button gets its click.
        PreviewMouseDown += (_, _) => { if (NeedsInput(_state.Current)) TryActivateNotch(); };
        // The tucked loader sliver and the "Notes ready" pill open on click only (see HoverTick).
        MouseLeftButtonUp += (_, _) =>
        {
            if (_tucked && _state.Current is PillState.Summarizing or PillState.Notes) ExpandCurrent();
        };
    }

    /// <summary>Diagnostic: log every click on the notch that does NOT land on a button, with what
    /// it hit and whether a button was nearby. Measures the real window at the real click - one line
    /// in %TEMP%\toki-m1.log per missed click, nothing at all when buttons behave.</summary>
    void LogClicksThatMissAButton()
    {
        PreviewMouseDown += (_, e) =>
        {
            try
            {
                var p = e.GetPosition(this);
                var hit = System.Windows.Media.VisualTreeHelper.HitTest(this, p)?.VisualHit;

                // Did the click land on something that ACTS like a button? ButtonBase alone is not
                // enough (the collapsed peek opens on a Border with a mouse handler) - a log that
                // reports healthy behaviour as broken is worse than no log.
                for (var q = hit; q is not null; q = System.Windows.Media.VisualTreeHelper.GetParent(q))
                    if (q is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Documents.Hyperlink
                        || (q is FrameworkElement fe2 && fe2.Cursor == Cursors.Hand)) return;

                var chain = new List<string>();
                for (var q = hit; q is not null && chain.Count < 5; q = System.Windows.Media.VisualTreeHelper.GetParent(q))
                    chain.Add(q is FrameworkElement { Name.Length: > 0 } fe ? $"{q.GetType().Name}:{fe.Name}" : q.GetType().Name);

                // Was a button NEARBY? Then the click was in a button's box but missed its hit area.
                var near = NearestButton(p);
                App.Log($"click miss at ({p.X:F0},{p.Y:F0}) state={_state.Current} "
                    + $"hit=[{(chain.Count > 0 ? string.Join(" < ", chain) : "nothing")}]"
                    + (near is null ? "" : $" NEAR={near}"));
            }
            catch { /* diagnostics must never break a click */ }
        };

        // Same for HOVER: for a real WPF Button, hover and click share one hit test - if a point
        // inside a button does not set IsMouseOver, something else is drawing the highlight.
        long lastHoverLog = 0;
        string? lastHoverKey = null;
        PreviewMouseMove += (_, e) =>
        {
            try
            {
                var p = e.GetPosition(this);
                foreach (var b in Descendants(this).OfType<System.Windows.Controls.Primitives.ButtonBase>())
                {
                    if (!b.IsVisible || b.ActualWidth <= 0) continue;
                    Point local;
                    try { local = TranslatePoint(p, b); } catch { continue; }
                    // INSET, not the raw rectangle: with large corner radii a point inside the RECT
                    // can be genuinely outside the SHAPE - only unambiguous points count.
                    if (!WellInside(local, b)) continue;

                    // Inside the button's box. If the button itself is unaware, THAT is the bug.
                    if (b.IsMouseOver) return;

                    var key = $"{b.Name}|{b.Content as string}";
                    var now = Environment.TickCount64;
                    if (key == lastHoverKey && now - lastHoverLog < 1000) return;
                    lastHoverKey = key; lastHoverLog = now;

                    App.Log($"hover miss: inside {b.GetType().Name}:{(b.Name.Length > 0 ? b.Name : b.Content as string ?? "?")} "
                        + $"({b.ActualWidth:F0}x{b.ActualHeight:F0}) at local ({local.X:F0},{local.Y:F0}) "
                        + $"but IsMouseOver=false, IsHitTestVisible={b.IsHitTestVisible}, "
                        + $"DirectlyOver={(Mouse.DirectlyOver?.GetType().Name ?? "null")}");
                    return;
                }
            }
            catch { /* diagnostics must never break the pointer */ }
        };
    }

    /// <summary>Name of the closest button whose box contains this point, if any.</summary>
    string? NearestButton(Point p)
    {
        foreach (var b in Descendants(this).OfType<System.Windows.Controls.Primitives.ButtonBase>())
        {
            if (!b.IsVisible || b.ActualWidth <= 0) continue;
            Point local;
            try { local = TranslatePoint(p, b); } catch { continue; }
            if (WellInside(local, b))
                return $"{b.GetType().Name}:{(b.Name.Length > 0 ? b.Name : b.Content as string ?? "?")} "
                     + $"({b.ActualWidth:F0}x{b.ActualHeight:F0}) at local ({local.X:F0},{local.Y:F0})";
        }
        return null;
    }

    /// <summary>Is this point unambiguously within a rounded button, rather than merely within its
    /// rectangle? Better to miss an edge case than to report a healthy button as broken.</summary>
    static bool WellInside(Point local, FrameworkElement b)
    {
        var inset = Math.Min(10, Math.Min(b.ActualWidth, b.ActualHeight) / 3);
        return local.X > inset && local.Y > inset
            && local.X < b.ActualWidth - inset && local.Y < b.ActualHeight - inset;
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
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
            // changes above - window-composition changes are exactly what can drop the affinity.
            ApplyGhost(); // works on this transparent window too
        }
        catch (Exception ex) { App.Log($"window setup failed (non-fatal): {ex.Message}"); }
        ApplyCornerGeometry();   // the DPI is only knowable once the HWND exists

        _input = new Interop.InputMonitor(
            isArmed: () => DismissOnClickOutside.Contains(_state.Current)
                           && Environment.TickCount64 - _panelShownTick > 500,
            isInsidePanel: PointInWindow,
            dismiss: () => Dispatcher.BeginInvoke(() =>
            {
                // Clicking into your real work TUCKS the page to a sliver (context kept) rather
                // than resetting it - a tucked sliver just stays (its auto-hide takes it away).
                if (!_tucked) TuckCurrent();
            }),
            onWheel: OnHookWheel);
    }

    // A few px of slack so a click on the very EDGE of the notch still reads as "inside" -
    // otherwise a borderline click tucks the panel away. Same slack widens the wheel-capture zone.
    const int InsidePad = 6;
    bool PointInWindow(int x, int y) =>
        (_rectR <= _rectL) || (x >= _rectL - InsidePad && x <= _rectR + InsidePad && y >= _rectT - InsidePad && y <= _rectB + InsidePad)
        // A dropdown that dropped BELOW the notch is logically part of it - a click in the model/provider
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
            return true;   // the notch owns the wheel while you're on it - never scroll the app behind
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

    /// <summary>Place the window TOP-centered in PHYSICAL pixels (Win32). The top stays fixed so
    /// the pill grows DOWNWARD as it morphs.</summary>
    void PositionTopCenter()
    {
        if (_hwnd == IntPtr.Zero) return;
        UpdateLayout();
        PlaceTopCenter();
    }

    // Position from the CURRENT physical window size, without forcing layout - safe to call
    // from SizeChanged (which fires inside a layout pass, where UpdateLayout would throw).
    void PlaceTopCenter()
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var wr)) return;
        int winW = wr.Right - wr.Left;
        var work = PhysicalWorkArea();
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        int gap = (int)Math.Round(TopGap * dpi);
        int skew = (int)Math.Round((CardPadL - CardPadR) / 2 * dpi);
        int x = work.Left + ((work.Right - work.Left) - winW) / 2 - skew;
        int y = work.Top + gap;
        // Skip the move when nothing changed. NOTE this reads the window's CURRENT physical width,
        // so it must only run when the Win32 size is already correct: right after setting WPF's
        // Width (layout merely dirty) it centres for the OLD size - a visible sideways jump.
        // SizeWindowNow exists for that case.
        if (x == _lastPlaceX && y == _lastPlaceY) return;
        _lastPlaceX = x; _lastPlaceY = y;
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }
    int _lastPlaceX = int.MinValue, _lastPlaceY = int.MinValue;

    /// <summary>Set the window's PHYSICAL size AND top-centre position in one Win32 call - used before
    /// Show() on a reveal so the first painted frame is already the right size at the right place (no
    /// stale-width notch flashing off-centre). Sizes from the target DIPs, not the stale current rect.</summary>
    void PlaceExactTopCenter(double dipW, double dipH)
    {
        if (_hwnd == IntPtr.Zero) return;
        var work = PhysicalWorkArea();
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = Math.Max(1, (int)Math.Round(WinW(dipW) * dpi.DpiScaleX));
        int ph = Math.Max(1, (int)Math.Round(WinH(dipH) * dpi.DpiScaleY));
        int gap = (int)Math.Round(TopGap * dpi.DpiScaleY);
        // Centre the CARD, not the window: the mascot pad makes the window asymmetric, so the window
        // shifts left by half the pad difference to put the card itself on the screen's centreline.
        int skew = (int)Math.Round((CardPadL - CardPadR) / 2 * dpi.DpiScaleX);
        int x = work.Left + ((work.Right - work.Left) - pw) / 2 - skew;
        int y = work.Top + gap;
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, pw, ph, SwpNoZOrder | SwpNoActivate);   // NOT SwpNoSize - set both
        _lastPlaceX = x; _lastPlaceY = y;
    }

    // Work area of the monitor the CURSOR is on - so the notch appears on whichever screen
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
    // or while a fullscreen app/game/video/presentation is running - stay out of the way.
    static bool EdgeSuppressed()
    {
        if ((GetAsyncKeyState(VkLButton) & 0x8000) != 0) return true;
        return QuietFullscreen();
    }

    /// <summary>True while a fullscreen app / game / presentation is up (the signal Windows itself
    /// uses for toasts). Shared by the edge-summon, PrepWatcher, and the meeting-prompt gate.
    /// Suppression only DELAYS a prompt - the detector re-offers on the next poll.</summary>
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

    /// <summary>Build the first peek/panel views and the mascot on an idle frame, before the first
    /// expand needs them. WPF animation clocks are wall-clock, not frame-counted: 50-150ms of
    /// synchronous cold build inside the transition means the head of the animation is never drawn.</summary>
    internal void WarmFirstOpen()
    {
        if (App.Headless) return;
        try
        {
            // Construct the mascot and render it once off-screen, so geometry, gradients and the
            // bitmap cache are realised on an idle frame rather than the first frame of a morph.
            if (MascotHost.Content is null)
            {
                var blob = new Controls.TokiBlob();
                blob.Measure(new Size(44, 40));
                blob.Arrange(new Rect(0, 0, 44, 40));
                blob.UpdateLayout();
                try
                {
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        44, 40, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(blob);
                }
                catch (Exception ex) { App.Log($"notch warm (mascot raster, non-fatal): {ex.Message}"); }
                MascotHost.Content = blob;
            }

            // The two views the first summon walks through. Only ones that are not already live -
            // a view the notch is showing belongs to its parent's layout pass, not to ours.
            foreach (var s in new[] { PillState.Collapsed, PillState.Home })
            {
                var v = ViewFor(s);
                if (VisualTreeHelper.GetParent(v) is not null) continue;
                var (w, h) = CardSize(s);
                v.Measure(new Size(Math.Max(1, w - 2), double.PositiveInfinity));
                v.Arrange(new Rect(new Point(0, 0), v.DesiredSize));
                v.UpdateLayout();
            }
            App.Log("notch warm: first-open views and the mascot are built");
            WarmRemainingPages();
        }
        catch (Exception ex) { App.Log($"notch warm (non-fatal): {ex.Message}"); }
    }

    /// <summary>
    /// BUILD EVERY OTHER PAGE BEFORE IT IS ASKED FOR, one per idle frame.
    ///
    /// Only Collapsed and Home were ever warmed. Every other page was built and measured for the
    /// FIRST time inside the transition itself, by `ContentHeight` - which the transition's own
    /// comment already describes as "a synchronous 50-150ms stall on a cold view". That stall is
    /// the whole glitch the owner kept seeing: "there is still a tenth of a second mini glitch"
    /// (2026-09-27). It is not a rendering artifact, it is the UI thread stopping, and it happens
    /// exactly ONCE per page per session, which is why it felt random rather than reproducible.
    ///
    /// ONE PER IDLE CALLBACK, never a loop. Building nine views in a row would move the stall rather
    /// than remove it - the point is that each build lands on a frame where nothing is animating.
    /// A page the notch is currently showing is skipped: it belongs to its parent's layout pass.
    /// </summary>
    void WarmRemainingPages()
    {
        var queue = new Queue<PillState>(new[]
        {
            PillState.Settings, PillState.Notes, PillState.Library, PillState.Calendar,
            PillState.ManualNote, PillState.Ask, PillState.About, PillState.PrepCard,
            PillState.NoSpeech, PillState.MeetingPrompt, PillState.RecordingOpen, PillState.Summarizing,
        });

        void Next()
        {
            if (queue.Count == 0) { App.Log("notch warm: every page is built - no cold measure left"); return; }
            var s = queue.Dequeue();
            try
            {
                if (_views.ContainsKey(s) || s == _state.Current) { Next(); return; }
                var v = ViewFor(s);
                if (VisualTreeHelper.GetParent(v) is null)
                {
                    var (w, h) = CardSize(s);
                    v.Measure(new Size(Math.Max(1, w - 2), double.PositiveInfinity));
                    v.Arrange(new Rect(new Point(0, 0), v.DesiredSize));
                    v.UpdateLayout();
                }
            }
            catch (Exception ex) { App.Log($"notch warm ({s}, non-fatal): {ex.Message}"); }
            Dispatcher.BeginInvoke(new Action(Next), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        Dispatcher.BeginInvoke(new Action(Next), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    void OnStateChanged(PillState old, PillState next)
    {
        if (next == PillState.Hidden)
        {
            // A result (notes / nothing-was-said) is a ONE-SHOT announcement: once closed,
            // re-summoning must bring back Toki, never the dead result.
            if (old is PillState.Notes or PillState.NoSpeech) _context = PillState.Home;
            // A short cooldown so a page that just closed can't instantly re-summon under a cursor
            // still resting at the top edge.
            _edgeReadyTick = Environment.TickCount64 + EdgeReadyDelayMs;
            // Disarm the edge-summon: the cursor must drop back down and make a fresh reach UP
            // before Toki returns.
            _edgeArmed = false;
            _shownState = next;
            StartHoverPoll(); HideAnimated(); return; // keep polling: watch the top edge to summon
        }

        var fromHidden = old == PillState.Hidden || !IsVisible;
        var view = ViewFor(next);
        // The recording peek flips only when recording STARTS - a hover-collapse just settles
        // (see RecordingBarView.FlipOnShow).
        if (view is Views.RecordingBarView rbv) rbv.FlipOnShow = old != PillState.RecordingOpen;

        // Views whose text depends on live app state must be refreshed BEFORE the height is
        // measured below - otherwise longer text renders cut off by a height computed on stale text.
        if (view is Views.MeetingPromptView mp) mp.Refresh();
        // Onboarding is a FLOW held in a cached view: rewind it on entry, before the height
        // measure, so the card is sized for step one.
        if (view is Views.OnboardingView ob) ob.ResetToStart();
        if (view is Views.PrepCardView pc) pc.Refresh();   // paint from App.CurrentPrep BEFORE the height measure

        // ONE ANIMATED PATH. This used to be `IsHoverPair(old, next)` - an allowlist of five state
        // pairs - and everything outside it fell through to a branch that teleported. Widening the
        // allowlist was the first fix and it was wrong: it left TWO paths that had to be kept in
        // step, and they immediately drifted. deferPeekBg below still said `&& hoverMorph`, so a
        // newly-animated collapse stripped the card's glass and the mascot on frame one and then
        // shrank an empty box. That is the glitch.
        //
        // So there is no second path now. Every visible-to-visible transition animates the same way,
        // through the same surface deferral and the same mascot handling; SnapResize stays the one
        // explicit opt-out for callers that genuinely must not animate (onboarding steps).
        var hoverMorph = !fromHidden;
        var collapsing = next is PillState.Collapsed or PillState.RecordingBar;
        var peek = next is PillState.Collapsed or PillState.RecordingBar;
        var deferPeekBg = peek && hoverMorph;

        // A reveal can enter directly as a SLIVER instead of full open: the loader works quietly as
        // a sliver from the first frame; results announce briefly then tuck (handled below).
        var enterTucked = (_revealTucked || next == PillState.Summarizing) && IsStayable(next);
        _revealTucked = false;
        _tucked = enterTucked;

        // A note fresh out of the loader ANNOUNCES itself instead of opening the full page over the
        // user's work: the "Notes ready" pill stands for NotesReadyPeekMs, a click opens the note,
        // and being ignored hands off to the ambient peek (the note stays one hover away).
        var announce = next == PillState.Notes && old == PillState.Summarizing;
        if (announce) _tucked = true;

        // ═══ ALL MEASURING BEFORE ANY VISUAL CHANGE ═══
        // ContentHeight builds and measures the NEXT view - a synchronous 50-150ms stall on a cold
        // view. Measure while the OLD state still owns every pixel, then do all the visual
        // mutations in one uninterrupted run so they compose in a single frame.
        var (w, h) = CardSize(next);
        if (announce) w = Views.NotesReadyView.CardWidth;
        var dpi = _hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleY : 1.0;
        var work = PhysicalWorkArea();
        // Never exceed the screen - tall panels clamp and scroll internally.
        var availH = (work.Bottom - work.Top) / dpi - TopGap - BottomMargin;
        var fullH = announce ? Views.NotesReadyView.CardHeight
                  : enterTucked ? SliverH : Math.Min(ContentHeight(next, w, h), availH);

        // ═══ HIDE THE CONTENT BEFORE IT CAN BE SEEN ═══
        // The sizing work below calls SetWindowPos - a synchronous Win32 call that can let the
        // window paint. Zero the opacity BEFORE the view is added, so the finished panel can never
        // flash for a frame at full size and opacity.
        // ═══ KEEP A PICTURE OF THE OUTGOING PAGE ═══
        //
        // Taken BEFORE anything is cleared or resized, while the old page still owns every pixel.
        // Without it the card spends the whole morph showing the NEW page cropped to a rectangle
        // that is still the old size, which is what the owner kept reporting as "glitch resize" and
        // "the text is cut off": the clip was animating correctly, but what it was clipping was a
        // page arranged for a different card. Pinning the layout to the target (2026-09-03) fixed
        // the reflow at the end and did nothing for this, because the crop is the artifact.
        //
        // A raster, not the live visual: it costs one cheap render, it cannot re-layout underneath
        // the animation, and it holds the old page exactly as the user last saw it.
        // ═══ NEVER PHOTOGRAPH A PEEK THAT IS ONLY TOKI ═══
        //
        // CollapsedView's entire content is a TokiBlob - the peek IS him. Cross-fading out of it
        // therefore leaves a PICTURE of Toki dissolving at the card's centre while the real bubble
        // springs in at the panel's left gutter: two of him, in two places, on the same frames. That
        // is the flicker, and it is why delaying the bubble did not cure it - the duplicate is the
        // outgoing snapshot, not the timing.
        //
        // There is nothing worth carrying across from a mascot-only peek anyway: the card behind it
        // is bare, so the cross-fade has no content to preserve. Skipping the snapshot leaves exactly
        // one Toki on screen for the whole transition, which is the standing one-Toki-per-surface rule.
        var ghost = old == PillState.Collapsed ? null : SnapshotContent();

        Host.BeginAnimation(OpacityProperty, null);
        Host.Opacity = 1;   // the CHILDREN cross-fade now, not the host
        Host.Children.Clear();
        // The REVEAL path fades the whole Host (there is nothing to cross-fade from), so the view
        // must start opaque there or it would be multiplied to nothing and never appear.
        view.Opacity = fromHidden ? 1 : 0;
        Host.Children.Add(view);
        if (ghost is not null) Host.Children.Add(ghost);   // on top, dissolving away
        if (DismissOnClickOutside.Contains(next)) _panelShownTick = Environment.TickCount64;
        App.Log($"transition {old}->{next} path={(fromHidden ? "reveal" : hoverMorph ? "hover-morph" : "in-place")}"
              + $" visible={IsVisible} card={_cardW:F0}x{_cardH:F0}");

        // While PEEKING, drop the dark frosted box - Toki is just the character floating from the
        // top edge. On a hover-COLLAPSE into a peek, keep the surface until the shrink finishes
        // (deferPeekBg) so the box shrinks away WITH the panel instead of vanishing instantly.
        if (next == PillState.Collapsed) _collapsedTick = Environment.TickCount64; // start the greeting grace
        if (!deferPeekBg) SetSurface(peek);

        // The tucked LOADER hides the mascot too: the bubble is a SIBLING of the card, outside its
        // clip, so slivering the card alone leaves him standing. Opacity, not Visibility -
        // SetSurface owns Visibility, and its restore path puts him back.
        if (next == PillState.Summarizing && _tucked)
        {
            MascotZone.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, Theme.Motion.Dur(180)) { EasingFunction = Theme.Motion.Depart });
        }

        if (announce) ShowNotesReady();   // the pill stands in for the page while it announces
        Host.Margin = new Thickness(0);
        var targetH = fullH;

        // A hover pill expand/collapse is animated so it GROWS/SHRINKS smoothly, not a hard
        // size-snap. Other transitions stay instant.
        if (hoverMorph)
        {
            // The clip is NOT cleared here: it is the only record of WHERE THE CARD CURRENTLY IS,
            // and a morph that interrupts another needs it or it snaps. PinContentForMorph replaces
            // it (and clears it itself on the one path where it sets no new one).
            _clipGen++;
            // ═══ THE HAND-OFF LANDING (notes-ready flight → peek) ═══
            // The flight already concealed the pill and the peek's surface is bare - there is NO
            // card to morph. Make the card bare NOW, open it at the peek's geometry in one silent
            // step, and cross-fade the peek's own Toki in under the zone's fade-out. The window
            // recentre shifts window-anchored children, so the flyer's X is compensated.
            if (old == PillState.Notes && next == PillState.Collapsed)
            {
                _cardBare = true;   // SettleCorners must not draw a rim around a card that is leaving
                Root.Background = System.Windows.Media.Brushes.Transparent;
                // The material stack is a CHILD of Root, not its Background - it must be hidden
                // here too, or a faint card ghost stays at the landing spot until the deferred swap.
                Material.Visibility = Visibility.Collapsed;
                PinCorners();
                ReleaseClip();
                Root.Clip = null;
                // Computed, not read back: Window.Left updates asynchronously on WM_MOVE, so
                // reading it right after the resize sees the OLD value.
                var slide = (_cardW - w) / 2.0;
                SetCardSize(w, targetH);
                SizeWindowNow(w, targetH);
                SettleCorners();
                var flyerX = MascotShift.X;
                MascotShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
                MascotShift.X = flyerX - slide;
                CrossFade(view, 210);
                MascotZone.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, Theme.Motion.Dur(180)) { EasingFunction = Theme.Motion.Depart });
                ScheduleSurface(next, peek, 240);
            }
            else
            {
                // Collapsing reads best as a smooth zoom-OUT: an eased size retract with the
                // content cross-fading, not a hard snap.
                const int collapseMs = 240;
                var morphMs = collapsing ? collapseMs : 300;
                // A change in how far the card hangs below the top edge rides the same curve as the
                // size, instead of the window jumping. MeetingPrompt (12) to RecordingBar (2) was a
                // 10px teleport at the exact moment the user had just clicked.
                GlideTopGap(old, next, morphMs);
                AnimateSize(w, targetH, morphMs);   // card clip morphs; window holds still
                // The outgoing page dissolves while the incoming one arrives, both inside the
                // morphing clip - so the card never shows a page cropped to the wrong size.
                CrossFade(view, collapsing ? 210 : 240);
                // Drop the dark surface only once the box has finished shrinking → it fades away with the panel.
                if (deferPeekBg)
                {
                    // The mascot zone is a sibling of the card: without fading it here, the bubble
                    // Toki and the peek's own Toki show side by side for the shrink, then the disc
                    // pops off in one frame. He leaves WITH the card on the retract curve.
                    // Opacity only: nothing here re-runs layout.
                    MascotZone.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0, Theme.Motion.Dur(180)) { EasingFunction = Theme.Motion.Depart });
                    ScheduleSurface(next, peek, collapseMs);
                }
            }
        }
        else if (fromHidden)
        {
            // Drop DOWN out of the top edge, GPU-COMPOSITED: size the window to its final bounds
            // ONCE, then unfurl via an animated clip (render-only). Never animate window bounds -
            // that forces a relayout and a Win32 re-center on every frame.
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            Width = WinW(w);
            Height = WinH(targetH);
            SetCardSize(w, targetH);
            // Clip to ZERO height and hide the content BEFORE Show(), so the very first composited
            // frame is empty - not a full-height box. RevealDown animates the clip open from here.
            PinCorners();   // the shape belongs to DriveClip until the settle
            DriveClip(new RectangleGeometry(new Rect(0, 0, w, 0)));
            Host.Opacity = 0;
            PlaceMascot(targetH, 0);       // Toki starts at this state's spot - no glide on a fresh reveal
            // Lay out and force the FINAL physical size/position BEFORE Show(): on a hidden window
            // WPF has not applied Width/Height, so the first frame would carry the previous state's
            // arrangement. (PlaceExactTopCenter, not SizeWindowNow: a hidden window has no pending
            // layout pass to hand the resize to.)
            UpdateLayout();
            PlaceExactTopCenter(w, targetH);
            // Snapshot what the entrance starts FROM at the instant the window is shown - sampling
            // live races the wall-clock animation, so a busy machine fails a correct build.
            _entranceArm = (_clipBox?.Rect.Height ?? -1,
                            MascotZone.Opacity, MascotPop.ScaleX, Host.Opacity);
            Show(); ApplyGhost();          // re-apply WDA - hide/show can drop it
            PlaceExactTopCenter(w, targetH);   // re-assert (handles the first-ever show, before the HWND existed)
            if (_blurOn) { ApplyBlur(true); ApplyRoundRegion(true); }   // Show can drop the accent - re-assert glass
            RevealDown(w, targetH, 280);   // animates the 0-height clip open while content settles in
        }
        CacheWindowRect();
        // Re-assert ghost on EVERY state change: the surface work (WCA_ACCENT_POLICY + DWM
        // corner/region changes) can drop WDA_EXCLUDEFROMCAPTURE. Cheap and idempotent.
        ApplyGhost();

        // The poll runs while tucked (Hidden - edge summon), on the peeks, and on stayable
        // pages (to detect the hover that brings a tucked sliver back).
        if (next is PillState.Collapsed or PillState.RecordingBar or PillState.RecordingOpen || IsStayable(next))
            StartHoverPoll();
        else StopHoverPoll();

        if (IsStayable(next))
        {
            _context = next;                     // remember the page - re-summoning restores it
            // The loader is a PERSISTENT working sliver: it must not hide itself, it waits for the
            // result (Notes / NoSpeech) to replace it. Everything else counts down.
            if (next == PillState.Summarizing) { StopIdle(); StopAutoHide(); }
            else if (announce) ArmNotesPeek();   // the announcement dwells, then yields the screen
            else if (enterTucked) ArmAutoHide(); // entered as a sliver → count down to fully hide
            else ArmIdle();                      // fully open → tuck to a sliver after a beat
        }
        else { StopIdle(); StopAutoHide(); }
        _shownState = next;   // GrowToContent's gate: this state's transition has now been processed
    }

    // The state whose transition OnStateChanged last processed. AppState notifies its subscribers in
    // order, so between "the state changed" and "this window reacted" there is a gap in which view
    // refreshes must not size the box (see GrowToContent).
    PillState _shownState = PillState.Hidden;

    /// <summary>Morph the notch to hold a CARD of (w,h); the window's pads are added here, once.
    /// The card animates; the WINDOW does not - animating window bounds is a Win32 move plus a
    /// layered-surface reallocation per frame.</summary>
    void AnimateSize(double w, double h, int ms)
    {
        PlaceMascot(h, ms);
        var fromW = ActualWidth > 0 ? ActualWidth : WinW(w);
        var fromH = ActualHeight > 0 ? ActualHeight : WinH(h);
        // The shadow caster carries the window's only Effect; resizing it per frame would
        // software-re-render the blur every frame. Hide it for the ride, fade it back on settle.
        ShadowCaster.BeginAnimation(OpacityProperty, null);
        ShadowCaster.Opacity = 0;

        // THE WINDOW IS SIZED ONCE, TO THE UNION of where it is and where it is going, then held
        // still for the whole transition - bounds set twice per transition (here and at the end)
        // instead of on every frame.
        var unionCardW = Math.Max(CardW(fromW), w);
        var unionCardH = Math.Max(CardH(fromH), h);

        // THE CLIP GOES ON BEFORE THE WINDOW GROWS: SizeWindowNow ends in SetWindowPos, which is
        // synchronous and can let the window paint - unclipped, that paints the whole panel at
        // full size one frame before the motion begins.
        var gen = _pinGen + 1;

        // ═══ THE SETTLE IS THE ANIMATION'S OWN COMPLETION, NOT A TIMER ═══
        //
        // This used to be a DispatcherTimer set to ms + 20, i.e. a second clock racing the first.
        // Two consequences, and the owner felt both as "the resizer". The 20ms was a deliberate gap
        // in which the card had finished moving and the window had not yet resized, so the resize
        // was always a SEPARATE, LATER event rather than the end of the motion. And a DispatcherTimer
        // is wall-clock and best-effort: under load - which is exactly when notes are generating -
        // it drifts, so the gap grew and the resize detached from the animation entirely.
        //
        // Hanging it off the clip animation's Completed makes the last frame of the motion and the
        // window resize the same moment. There is no longer a resize event to see.
        PinContentForMorph(unionCardW, unionCardH, w, h, ms, () =>
        {
            if (gen != _pinGen) return;   // a newer morph owns the window now
            // ═══ THE LAST GLITCH: THE CLIP'S ORIGIN IS RELATIVE TO THE UNION BOX ═══
            //
            // Throughout the morph the window is held at the UNION size and the card is CENTRED in
            // it, so the clip carries a left inset of (unionW - toW) / 2. The instant the window
            // shrinks to the target, that inset is nonsense: Root is now exactly toW wide, and a
            // clip offset half the old slack to the right crops the card and shows a sliver of bare
            // window beside it. SettleCorners fixes it, but it runs at Loaded priority - a frame
            // LATER - so every morph ended with one frame of the card cropped and shifted. That is
            // the residual "resize glitch" that survived pinning the layout and cross-fading the
            // content, and it is why it read as a snap at the very end rather than during.
            //
            // Re-origin the clip in the SAME dispatcher run as the resize so they compose into one
            // frame; SettleCorners still follows to swap the arc for the squircle.
            DriveClip(new RectangleGeometry(new Rect(0, 0, w, h)));

            // Settle onto the exact target: one resize, after the motion has finished.
            SizeWindowNow(w, h);
            // The squircle waits for THIS resize: until the window is on its target the card is
            // still centred in the union box, and a shape built there is built for the wrong size.
            SettleCorners();
        });
        SizeWindowNow(unionCardW, unionCardH);

        // THE LIFT COMES BACK WITH THE CARD, NOT AFTER IT. Fading the shadow in once the morph had
        // already finished made a second, later event out of it - the card landed, then a moment
        // afterwards it grew a shadow, which reads as the window adjusting itself. It now starts at
        // ~65% through the motion so it is home on the same frame the card is.
        if (ShadowCaster.Visibility == Visibility.Visible)
            ShadowCaster.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, Theme.Motion.Dur(Theme.Motion.Ms((int)(ms * 0.35))))
                {
                    BeginTime = TimeSpan.FromMilliseconds(ms * 0.65),
                    EasingFunction = Theme.Motion.EaseOut,
                });
    }

    /// <summary>Resize and re-centre the window in ONE Win32 call, computed from the TARGET card
    /// size. Setting WPF's Width only marks layout dirty, so positioning from GetWindowRect right
    /// after centres for the stale size and jumps sideways a frame later.</summary>
    void SizeWindowNow(double cardW, double cardH)
    {
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        Width = WinW(cardW);
        Height = WinH(cardH);
        // Move to where a card of this size BELONGS, computed from the target rather than read back
        // from the window, then let WPF's own pending layout pass do the resize. Computing x from
        // the target needs neither a GetWindowRect readback (stale width, sideways jump) nor a
        // synchronous UpdateLayout (dropped frames).
        MoveTopCenterFor(cardW);
    }

    /// <summary>Move (never resize) the window to where a card of <paramref name="cardW"/> sits
    /// top-centred. Position comes from the TARGET size, so this is correct before WPF has applied
    /// it - unlike <see cref="PlaceTopCenter"/>, which reads the window's current bounds.</summary>
    /// <summary>
    /// Make a change in the card's distance from the top edge GLIDE instead of teleport.
    ///
    /// States hang at different gaps: panels at 12, the recording strip and the loader flush at 2,
    /// the collapsed peek at 0. Accepting a meeting goes MeetingPrompt (12) to RecordingBar (2), and
    /// the window simply jumped 10px up in one frame - a small move, but an abrupt one at the exact
    /// moment the user has just clicked, which is when they are watching hardest.
    ///
    /// Animating the WINDOW's position would be the obvious fix and the wrong one: a layered window
    /// pays a native move plus a DWM surface update per frame. So the window still jumps once, and
    /// the CARD is offset by the same amount and glides back to zero - composited, no layout, and
    /// visually identical to the card having moved.
    /// </summary>
    void GlideTopGap(PillState from, PillState to, int ms)
    {
        var delta = TopGapFor(to) - TopGapFor(from);
        if (Math.Abs(delta) < 0.5) return;

        RootShift.BeginAnimation(TranslateTransform.YProperty, null);
        if (Theme.Motion.Reduced) { RootShift.Y = 0; return; }   // no spatial travel under reduced motion

        // The window has already been told to sit at the NEW gap, so start the card where the OLD
        // gap put it and let it come home.
        RootShift.Y = -delta;
        RootShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, Theme.Motion.Dur(ms)) { EasingFunction = Theme.Motion.Drawer });
    }

    /// <summary>
    /// A frozen picture of whatever the card is showing right now, at the size it is showing it.
    /// Null when there is nothing to photograph (first show, hidden window, zero-sized content).
    /// </summary>
    Image? SnapshotContent()
    {
        // HOST, not Host.Children[0]. Photographing the incoming page alone was fine for a morph that
        // starts from rest, and wrong for one that INTERRUPTS another: mid-transition the card is
        // showing a half-faded new page under a half-faded old one, and capturing only the first of
        // them threw away exactly the picture the user was looking at. Rendering the host captures
        // what is actually on screen, whatever stage the previous morph had reached.
        //
        // RenderTargetBitmap renders a visual in its OWN coordinate space, so Host's RenderTransform
        // (the zoom) is deliberately not baked in - otherwise the next morph would scale a picture
        // that was already scaled.
        if (!IsVisible || Host.Children.Count == 0) return null;
        FrameworkElement el = Host;
        if (el.ActualWidth < 2 || el.ActualHeight < 2) return null;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(el.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(el.ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(el);
            rtb.Freeze();
            return new Image
            {
                Source = rtb,
                Width = el.ActualWidth,
                Height = el.ActualHeight,
                Stretch = Stretch.Fill,
                // Top-centre, matching where the clip puts the card inside the union box, so the
                // outgoing picture stays registered with the incoming page while both move.
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
            };
        }
        catch { return null; }   // a snapshot is a nicety; never let it break a transition
    }

    /// <summary>
    /// Dissolve the outgoing picture out while the incoming page comes in, both inside the morphing
    /// clip. This is the difference between a container that MORPHS and one that resizes with a
    /// cropped page inside it.
    ///
    /// The exit is deliberately shorter than the enter and runs on the depart curve: the old page
    /// should be gone before the new one is fully readable, or the two read as double-exposed.
    /// </summary>
    void CrossFade(FrameworkElement view, int inMs)
    {
        var ghost = Host.Children.Count > 1 ? Host.Children[^1] as Image : null;

        var ghostPresent = Host.Children.Count > 1 && Host.Children[^1] is Image;
        // A HAND-OFF, NOT A DISSOLVE. With both fades starting together there were three or four
        // frames where two different pages were equally readable, which the owner saw as the old
        // state hanging around. The incoming page now waits until the outgoing one is most of the
        // way gone, so at any instant one page is dominant. Measured at 60fps: the overlap drops
        // from about 4 frames to about 1.
        var lead = ghostPresent ? TimeSpan.FromMilliseconds(Theme.Motion.Ms((int)(inMs * 0.22))) : TimeSpan.Zero;

        view.BeginAnimation(OpacityProperty, null);
        view.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, Theme.Motion.Dur(Theme.Motion.Ms(inMs)))
            { BeginTime = lead, EasingFunction = Theme.Motion.ArriveStrong });

        if (ghost is null) return;
        // THE OLD PAGE LEAVES FIRST, AND QUICKLY. At 62% of the enter it was still legible a third
        // of the way into the morph, so two sets of words overlapped and the owner read it as the
        // previous state hanging around. It is a hand-off, not a dissolve between equals: the old
        // page only has to cover the frame where the new one is still transparent.
        //
        // 35%, and on the depart curve so most of it is gone in the first few frames. This is the
        // enter/exit asymmetry the house rules already state - users watch things arrive, not leave.
        var outMs = Math.Max(70, (int)(inMs * 0.35));
        var fade = new DoubleAnimation(1, 0, Theme.Motion.Dur(Theme.Motion.Ms(outMs)))
        { EasingFunction = Theme.Motion.Depart };
        fade.Completed += (_, _) => Host.Children.Remove(ghost);
        ghost.BeginAnimation(OpacityProperty, fade);
    }

    void MoveTopCenterFor(double cardW)
    {
        if (_hwnd == IntPtr.Zero) return;
        var work = PhysicalWorkArea();
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = Math.Max(1, (int)Math.Round(WinW(cardW) * dpi.DpiScaleX));
        int gap = (int)Math.Round(TopGap * dpi.DpiScaleY);
        // Centre the CARD, not the window: the mascot pad makes the window asymmetric, so the window
        // shifts left by half the pad difference to put the card on the screen's centreline.
        int skew = (int)Math.Round((CardPadL - CardPadR) / 2 * dpi.DpiScaleX);
        int x = work.Left + ((work.Right - work.Left) - pw) / 2 - skew;
        int y = work.Top + gap;
        if (x == _lastPlaceX && y == _lastPlaceY) return;
        _lastPlaceX = x; _lastPlaceY = y;
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    int _pinGen;

    /// <summary>Hold the CONTENT at its final size while the card's clip animates, so the content is
    /// laid out ONCE and the card simply uncovers it - animating window bounds re-runs layout every
    /// frame and the content visibly reflows. Pin and clip are both released on completion, so the
    /// settled panel is a normally-laid-out tree again.</summary>
    void PinContentForMorph(double unionW, double unionH, double toW, double toH, int ms, Action? onSettled = null)
    {
        // The one path that sets no new clip: clear whatever is there so a stale one cannot cap the
        // content (the caller no longer clears it, because clearing it destroys the current size).
        if (unionW <= 1 || unionH <= 1) { _pinGen++; PinCorners(); ReleaseClip(); Root.Clip = null; SettleCorners(); onSettled?.Invoke(); return; }

        // ═══ START FROM WHERE THE CARD ACTUALLY IS ═══
        // _cardW/_cardH is written at the START of a morph, so while one is in flight it already
        // claims the DESTINATION - a superseding morph must read the LIVE clip's Rect (its current
        // animated value) or it produces a from==to animation and the card snaps.
        var live = _clipBox?.Rect;
        var fromW = live is { Width: > 1 } lw ? lw.Width : Math.Max(1, _cardW > 0 ? _cardW : toW);
        var fromH = live is { Height: > 1 } lh ? lh.Height : Math.Max(1, _cardH > 0 ? _cardH : toH);
        var gen = ++_pinGen;

        // ═══ THE CONTENT IS LAID OUT AT ITS FINAL SIZE, ONCE ═══
        //
        // This used to lay it out at the UNION size, and that is what the owner saw as "glitch
        // resize". Two artifacts came out of it, both visible frame-by-frame in their recording:
        // going from Settings to Home, the new page was arranged at the WIDER of the two widths and
        // then clipped, so its heading sat hard against the card edge with the text cut off; and
        // when the morph completed, Host went back to NaN and the whole page RE-LAID-OUT in one
        // frame, which is the "and then it readjusts" they described.
        //
        // Arranging at the target instead means the page is correct on the first frame and identical
        // on the last, so there is nothing to readjust. Centre-and-top matches where the clip puts
        // the card inside the union box, so content and card stay registered while both move.
        Host.Width = toW;
        Host.Height = toH;
        Host.HorizontalAlignment = HorizontalAlignment.Center;
        Host.VerticalAlignment = VerticalAlignment.Top;

        // ═══ AND IT ZOOMS ═══
        //
        // With layout fixed, the card still needs to read as one surface changing size rather than
        // two pages swapped behind a moving window. The page scales from the size ratio toward 1, so
        // a bigger page zooms IN and a smaller page zooms OUT, which is the Dynamic Island behaviour.
        //
        // DAMPED to 30% and clamped: the raw ratio between the peek and a tall panel is over 4x, and
        // animating that literally is a lurch, not a zoom. A few percent carries the direction, which
        // is all the eye needs. Origin is top-centre because the card is anchored to the top edge.
        var raw = toH > 1 ? fromH / toH : 1.0;
        var startScale = Math.Clamp(1 + (raw - 1) * 0.30, 0.94, 1.06);

        // ═══ START FROM WHERE IT IS, NOT FROM WHERE A FRESH MORPH WOULD ═══
        //
        // Read the LIVE scale before clearing the animation, and use it when a morph is already in
        // flight. Interrupting one transition with another - which is just clicking twice, or a
        // meeting arriving while a panel is opening - otherwise snapped the content back to the new
        // morph's starting scale and re-zoomed from there. This is the same discipline the clip rect
        // has always followed (it reads the live Rect above), and it is the principle Apple's own
        // motion guidance puts first: animate from the presentation value, never the target.
        var liveScale = HostScale.ScaleX;
        var inFlight = Math.Abs(liveScale - 1) > 0.001;

        Host.RenderTransformOrigin = new Point(0.5, 0);
        HostScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HostScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (Theme.Motion.Reduced) { HostScale.ScaleX = HostScale.ScaleY = 1; }
        else
        {
            var zoomFrom = inFlight ? liveScale : startScale;
            var zoom = new DoubleAnimation(zoomFrom, 1, Theme.Motion.Dur(ms)) { EasingFunction = Theme.Motion.Drawer };
            HostScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
            HostScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
        }

        // The card is CENTRED horizontally in the union box and TOP-anchored: the clip is inset by
        // half the slack on each side and grows downward.
        static Rect Centred(double w, double h, double unionW) => new((unionW - w) / 2, 0, w, h);
        var from = Centred(fromW, fromH, unionW);
        var to = Centred(toW, toH, unionW);

        // One log line per morph: says whether the card grew from where it was or simply appeared
        // at its full size.
        App.Log($"morph clip {from.Width:F0}x{from.Height:F0} -> {to.Width:F0}x{to.Height:F0}"
              + $"  union {unionW:F0}x{unionH:F0}  ({ms}ms)");
        var geo = new RectangleGeometry(from);
        PinCorners();   // keep ApplyCornerGeometry off the shape; DriveClip owns it for the ride
        DriveClip(geo);
        _clipGen++;   // supersede any reveal/conceal clip still in flight

        var anim = new RectAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            EasingFunction = Theme.Motion.Drawer,
        };
        anim.Completed += (_, _) =>
        {
            if (gen != _pinGen) return;   // a newer morph owns the content now
            // Releasing to Stretch is now a NO-OP visually: the page was already arranged at exactly
            // this size, so nothing moves. That is the whole point of pinning to the target rather
            // than the union - the old code re-laid-out here and the jump was visible.
            Host.Width = Host.Height = double.NaN;
            Host.HorizontalAlignment = HorizontalAlignment.Stretch;
            Host.VerticalAlignment = VerticalAlignment.Stretch;
            HostScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            HostScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            HostScale.ScaleX = HostScale.ScaleY = 1;   // land exactly on 1, or text stays resampled
            // NOT null. The window is still at the UNION size for another ~20ms (AnimateSize's
            // settle timer owns the resize), and Root stretches - so dropping the clip here paints
            // the card at the union width, fully plated, for a frame or two. Collapsing Home (344)
            // to the peek (52) flashed back to 344px every single time. Hold the target rect until
            // the settle timer has actually resized the window; SettleCorners replaces it there.
            DriveClip(new RectangleGeometry(to));
            // No SettleCorners here: AnimateSize's settle timer owns it, once the window has
            // actually reached the target size. And put Toki back: the shift is the clip's inset
            // WHILE the window is held at the union size - once the clip is dropped it is nonsense,
            // and leaving it parks the bubble on top of the card after any shrink.
            MascotShift.BeginAnimation(TranslateTransform.XProperty, null);
            MascotShift.X = 0;
            // The window settle runs HERE, on the animation's own completion, not on a timer racing
            // it. See AnimateSize.
            onSettled?.Invoke();
        };
        geo.BeginAnimation(RectangleGeometry.RectProperty, anim);

        // Toki tracks the card's LEFT EDGE on the same curve, so the air gap stays constant.
        // X here is the inset the clip introduces; PlaceMascot owns Y.
        var mascotFrom = (unionW - fromW) / 2;
        var mascotTo = (unionW - toW) / 2;
        if (Math.Abs(mascotFrom - mascotTo) < 0.5)
        {
            MascotShift.BeginAnimation(TranslateTransform.XProperty, null);
            MascotShift.X = mascotTo;
        }
        else
        {
            MascotShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(mascotFrom, mascotTo, new Duration(TimeSpan.FromMilliseconds(ms)))
                { EasingFunction = Theme.Motion.Drawer });
        }

        _cardW = toW; _cardH = toH;
    }

    // The card's CURRENT size in DIPs - the thing the notch actually draws, as opposed to the
    // window, which is now deliberately bigger than it and set only twice per transition.
    double _cardW, _cardH;

    /// <summary>The card fills the window's card area exactly (the normal, non-morphing case), so
    /// there is no clip inset and Toki sits at his usual place against its left edge.</summary>
    void SetCardSize(double w, double h)
    {
        _cardW = w; _cardH = h;
        _pinGen++;                       // any in-flight morph no longer owns the geometry
        Host.Width = Host.Height = double.NaN;
        Host.HorizontalAlignment = HorizontalAlignment.Stretch;
        Host.VerticalAlignment = VerticalAlignment.Stretch;
        MascotShift.BeginAnimation(TranslateTransform.XProperty, null);
        MascotShift.X = 0;
    }

    /// <summary>Glide Toki's bubble to the current state's spot: centered on a compact pill,
    /// bottom-left on the expanded recording panel, level with the header on tall panels.
    /// Composition-only (translate), never Margin.</summary>
    void PlaceMascot(double cardH, int ms)
    {
        const double zone = 58;   // MascotZone height (XAML)
        // The height test comes FIRST: the 48px recording bar wants Toki centred like every other
        // compact pill; only the tall expanded panel drops him to the bottom-left corner.
        double y = _state.Current switch
        {
            // A compact pill is SHORTER than the bubble, so "centred" means the circle overlaps the
            // card's top and bottom equally. The window has no top pad, so 0 is the highest the zone
            // can sit without its crown being clipped - which leaves the bubble hanging below a 48px
            // bar. The old code clamped to the glass TOP BLEED instead of 0 and pushed it 8px lower
            // still; that is the sag the owner reported, and the bleed is gone now.
            _ when cardH <= 90      => 0,
            // Flush with the card's floor, not 8px past it. "Pokes into the shadow pad" read as
            // sagging off the corner on the real panel.
            PillState.RecordingOpen => Math.Max(0, cardH - zone),
            _                       => 8,                                  // tall panel: level with the header row
        };
        if (ms <= 0)
        {
            MascotShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            MascotShift.Y = y;
            return;
        }
        MascotShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(y, Theme.Motion.Dur(ms)) { EasingFunction = Theme.Motion.Drawer });
    }

    /// <summary>Apply the notch surface: a peek is transparent (just Toki floating); an open panel is
    /// the dark frosted box.</summary>
    void SetSurface(bool peek)
    {
        // A CAPSULE state draws its own free-floating pills, so the window's card gets out of the
        // way: no glass, no rim, no shadow. Unlike a peek, Toki still floats beside them.
        var capsule = DrawsOwnCapsules.Contains(_state.Current);
        _cardBare = peek || capsule;
        if (peek || capsule)
        {
            Root.Background = System.Windows.Media.Brushes.Transparent;
            ApplyBlur(false); ApplyRoundRegion(false);   // a peek is just Toki floating - no glass
        }
        else
        {
            // The hovering card cannot use window-level DWM acrylic: the accent fills the whole
            // rectangular window, so the frost would paint the mascot/shadow pads too. The glass
            // reading comes from the card's own semi-transparent tint instead. Both modes get the
            // full material; "Glassy notch" is purely a transparency switch.
            Root.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
                Toki.Llm.AppSettings.Current.GlassyNotch ? "CardPlate" : "CardPlateOpaque");
            ApplyBlur(false); ApplyRoundRegion(false);
        }
        // The rim, corner radius, clip and Toki's disc rim are ONE decision (ApplyCornerGeometry).
        // Setting BorderBrush here as well is how a double rim gets shipped.
        ApplyCornerGeometry();
        // Toki floats beside the card on panel AND capsule states; only the peek carries its own Toki.
        var mascotWasHidden = MascotZone.Visibility != Visibility.Visible;
        // ORDER MATTERS: park the entrance at its START values, THEN build the character, THEN make
        // the zone visible, THEN animate - Visibility first paints Toki at his final state for a
        // frame. Also un-pin a zone a finished collapse fade left at 0 (re-expand inside the
        // deferred-surface window skips the deferred call). Only the already-showing case: a zone
        // that WAS hidden is about to play the full armed entrance.
        if (!peek && !mascotWasHidden)
        {
            MascotZone.BeginAnimation(OpacityProperty, null);
            MascotZone.Opacity = 1;
        }
        if (!peek && mascotWasHidden) ArmMascotReveal();
        if (!peek && MascotHost.Content is null) MascotHost.Content = new Controls.TokiBlob();
        MascotZone.Visibility = peek ? Visibility.Collapsed : Visibility.Visible;
        if (!peek && mascotWasHidden) RevealMascot();
        // A capsule state's pills cast their own shadows - the card-shaped caster must not add a
        // second one behind the gaps between them.
        ShadowCaster.Visibility = peek || capsule ? Visibility.Collapsed : Visibility.Visible;
        // The floating Toki NARRATES the state.
        if (MascotHost.Content is Controls.TokiBlob blob)
            blob.SetMood(_state.Current switch
            {
                // Owner-set mapping: Toki WRITES while recording, holds the NOTE while transcribing.
                PillState.RecordingBar or PillState.RecordingOpen => Controls.TokiBlob.TokiMood.Writing,
                PillState.Summarizing => Controls.TokiBlob.TokiMood.Reading,
                // First run and a finished note are the two moments Toki is genuinely pleased.
                PillState.Notes or PillState.Onboarding => Controls.TokiBlob.TokiMood.Happy,
                _ => Controls.TokiBlob.TokiMood.Idle,
            });
        Material.Visibility = _cardBare ? Visibility.Collapsed : Visibility.Visible;
        // Corner radius is a SINGLE value for both modes so toggling glass never changes the size/shape.
    }

    /// <summary>The card's rim, in DIPs, rounded to a WHOLE number of device pixels - a stroke on a
    /// fraction of a pixel draws as two half-lit rows (a soft edge). Never
    /// RenderOptions.EdgeMode=Aliased: that would harden the r24 curves as well.</summary>
    double RimThickness()
    {
        var s = _hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleX : 1.0;
        if (s <= 0) s = 1.0;
        return Math.Max(1, Math.Round(s)) / s;
    }

    // ═══ THE CORNER HAS TWO MODES ═══
    // At REST: a squircle (Theme/Squircle.cs) - a stroked Path outline plus a matching clip, since
    // a Border's CornerRadius can only draw a circular arc. During a MORPH: plain arc corners -
    // the animated RectangleGeometry clip would shave a squircle rim, and rebuilding a squircle per
    // frame is a per-frame allocation. One method owns all four properties, so a double rim or a
    // rimless frame is impossible by construction.
    bool _cornersPinned = true;   // a morph owns the shape until something settles it
    bool _cardBare = true;        // peek / capsule states draw no card at all
    bool? _borderRimOn;           // avoids re-establishing the DynamicResource on every transition

    /// <summary>Hand the card's shape to the morph machinery: arc rim, arc corners, no squircle.</summary>
    // ═══════════════ THE SILHOUETTE NEVER CHANGES SHAPE MID-MOTION ═══════════════
    //
    // Until 2026-09-27 every clip animation - the reveal, the retract, and every page morph - ran on
    // a RectangleGeometry with CIRCULAR corner radii, while the card AT REST wore a squircle. So the
    // corner silhouette snapped arc -> squircle twice on every single transition.
    //
    // Squircle.cs claims the two "depart by under half a pixel ... screenshots won't show it", and
    // that is true only of the maximum deviation measured along the corner diagonal. Measured on
    // film, 20px down from the top edge, the squircle sits THREE PIXELS further out per side,
    // because smoothing starts the curve (1 + s) * r = 38px from the corner where the arc starts at
    // 24px. Frame-by-frame the corner visibly rounds off as a transition begins and flattens as it
    // ends. That is what the owner has now reported twice as "it still resizes for a split second"
    // - it is not a resize at all, and the width never changes (the morph log reads 380 -> 380).
    //
    // So the clip box is now just DATA: an animated rect nothing is clipped by directly. Root.Clip
    // is rebuilt as a squircle from it every frame, which also lets the real squircle rim stay
    // visible throughout instead of handing over to Root's arc Border. Building a StreamGeometry of
    // a dozen segments per frame is far cheaper than the layout pass the clip exists to avoid;
    // --fpstest is the check, and it is why the rect is still the thing that animates.
    RectangleGeometry? _clipBox;
    bool _clipDriving;
    Rect _lastPainted;
    int _clipStill;

    /// <summary>Animate this rect and paint the card as a squircle of it on every frame.</summary>
    void DriveClip(RectangleGeometry box)
    {
        _clipBox = box;
        _clipStill = 0;
        _lastPainted = Rect.Empty;
        PaintClipShape();
        if (_clipDriving) return;
        _clipDriving = true;
        System.Windows.Media.CompositionTarget.Rendering += OnClipFrame;
    }

    /// <summary>Stop repainting and hand the shape back to <see cref="ApplyCornerGeometry"/>.</summary>
    void ReleaseClip()
    {
        _clipBox = null;
        if (!_clipDriving) return;
        _clipDriving = false;
        System.Windows.Media.CompositionTarget.Rendering -= OnClipFrame;
    }

    void OnClipFrame(object? sender, EventArgs e)
    {
        // SELF-PARKING. Every caller is supposed to release, and one of them will eventually forget;
        // a per-frame handler that outlives its animation is a battery leak nobody would ever see.
        // Six identical frames means nothing is animating this any more, so unhook. DriveClip hooks
        // straight back up, so parking early costs a single subscription, never a dropped frame.
        if (_clipBox is null) { ReleaseClip(); return; }
        if (!PaintClipShape() && ++_clipStill > 6)
        {
            _clipDriving = false;
            System.Windows.Media.CompositionTarget.Rendering -= OnClipFrame;
        }
    }

    /// <summary>Returns true if the shape actually changed this frame.</summary>
    bool PaintClipShape()
    {
        if (_clipBox is not { } box) return false;
        var r = box.Rect;              // the animation's CURRENT value
        if (r == _lastPainted) return false;
        _lastPainted = r;
        _clipStill = 0;

        if (r.Width <= 0.5 || r.Height <= 0.5)
        {
            // A zero-height reveal frame, or a fully shut retract. Empty, not null: null means "no
            // clip at all" and would paint the whole card.
            Root.Clip = System.Windows.Media.Geometry.Empty;
            RimPath.Visibility = Visibility.Collapsed;
            return true;
        }

        Root.Clip = Theme.Squircle.Build(r, CornerRadius);
        Root.CornerRadius = default;   // the clip is the only thing rounding the plate
        SetBorderRim(false);
        // The rim can follow the real corner now. It used to hide for the ride precisely BECAUSE the
        // clip was a rounded rectangle and a squircle rim would have been cut at the corners; with
        // the clip a squircle of the same box, the two agree and there is nothing to hide from.
        if (_cardBare) { RimPath.Visibility = Visibility.Collapsed; return true; }
        var rim = RimThickness();
        RimPath.Data = Theme.Squircle.Outline(r, CornerRadius, rim);
        RimPath.StrokeThickness = rim;
        RimPath.Visibility = Visibility.Visible;
        return true;
    }

    void PinCorners() { _cornersPinned = true; ApplyCornerGeometry(); }

    /// <summary>Hand the shape back AFTER the pending layout pass, never synchronously: every settle
    /// point runs while a resize is still queued, so reading Root's arranged size here would build
    /// the shape for the size the card is leaving. Idempotent with Root.SizeChanged.</summary>
    void SettleCorners()
    {
        _cornersPinned = false;
        Dispatcher.BeginInvoke(new Action(ApplyCornerGeometry), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    void ApplyCornerGeometry()
    {
        var rim = RimThickness();
        MascotDisc.BorderThickness = new Thickness(rim);
        Root.BorderThickness = _cardBare ? default : new Thickness(rim);

        // A peek or a capsule state has no card: no rim either way, and no clip - the views draw
        // their own free-floating pills right out to the corners of the box.
        if (_cardBare)
        {
            ReleaseClip();
            Root.Clip = null;
            Root.CornerRadius = new System.Windows.CornerRadius(CornerRadius);
            SetBorderRim(false);
            RimPath.Visibility = Visibility.Collapsed;
            return;
        }

        // The shape is built from Root's ARRANGED box, never from _cardW/_cardH: the arranged box is
        // what WPF gave, and building from the request would inset the card by a fraction of a
        // pixel on every edge (the soft-edge defect).
        var rw = Root.ActualWidth;
        var rh = Root.ActualHeight;
        // ...but the arranged box is only trusted for the sub-pixel difference it absorbs. For a
        // SLIVER the two differ by design (the card asks for 13px while Root sits on the window's
        // bubble-floor at ~50), and building from the floor would un-clip the sliver back to a full
        // card. More than a pixel of difference means the card is deliberately smaller: card wins.
        if (_cardH > 0 && rh - _cardH > 1.0) rh = _cardH;
        if (_cardW > 0 && rw - _cardW > 1.0) rw = _cardW;

        // Keep the material's edge-lights CONCENTRIC with the plate. The plate's corner clamps to
        // half the shorter side (a 13px sliver rounds at 6.5, not 24), but the material's inner
        // top/bottom light rings stay at their full-card radius unless told otherwise - and a ring at
        // the wrong radius cuts its corner arc ACROSS the sliver as two crescent "edges" inside the
        // squircle (the loader-peek artifact the owner kept seeing). Match the ring radius to the
        // plate's clamped radius, minus the rim it sits inside, so the arcs hug the real corners.
        var plateRadius = Math.Min(CornerRadius, Math.Min(rw, rh) / 2);
        Material.Radius = Math.Max(0, plateRadius - rim);
        // PINNED means a clip animation owns the shape: DriveClip is repainting Root.Clip and
        // RimPath as a squircle of the animating box every frame, and anything written here would
        // be a frame of the WRONG size fighting it (this runs from Root.SizeChanged, which fires
        // mid-morph). Material.Radius above still applies - the plate's edge lights are not part of
        // the clip. Formerly this branch installed the circular arc, which is what produced the
        // shape pop; it now leaves the shape entirely alone.
        if (_cornersPinned) return;
        ReleaseClip();   // past this point the resting shape is authoritative

        // A box too small to hold the corner at all: the arc is the honest fallback, and a squircle
        // clamped this hard is indistinguishable from it anyway.
        if (rw <= 4 * rim || rh <= 4 * rim)
        {
            Root.Clip = null;
            Root.CornerRadius = new System.Windows.CornerRadius(CornerRadius);
            SetBorderRim(true);
            RimPath.Visibility = Visibility.Collapsed;
            return;
        }

        var box = new Rect(0, 0, rw, rh);
        Root.Clip = Theme.Squircle.Build(box, CornerRadius);
        Root.CornerRadius = default;   // the clip is the only thing rounding the plate now
        SetBorderRim(false);
        RimPath.Data = Theme.Squircle.Outline(box, CornerRadius, rim);
        RimPath.StrokeThickness = rim;
        RimPath.Visibility = Visibility.Visible;
    }

    /// <summary>Root's own 1px border: the rim during a morph, invisible whenever RimPath draws it.
    /// The thickness stays either way - it is what insets the material, so toggling it would shift
    /// the whole card's content by a pixel on every settle.</summary>
    void SetBorderRim(bool on)
    {
        if (_borderRimOn == on) return;
        _borderRimOn = on;
        if (on) Root.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "CardRim");
        else Root.BorderBrush = System.Windows.Media.Brushes.Transparent;
    }

    /// <summary>Shadow weight per theme (light runs both casters at 70%). Set in code because a
    /// DropShadowEffect's Opacity is a number, not a brush, and cannot follow a DynamicResource.</summary>
    void ApplyShadowDepth()
    {
        var k = Theme.ThemeManager.Current == Theme.AppTheme.Light ? 0.70 : 1.0;
        if (KeyShadow.Effect is System.Windows.Media.Effects.DropShadowEffect key) key.Opacity = 0.22 * k;
        if (AmbientShadow.Effect is System.Windows.Media.Effects.DropShadowEffect amb) amb.Opacity = 0.12 * k;
    }

    // ── seams for --revealtest ──
    // Read-only views of what must be at the START of the entrance when the notch becomes visible.
    // The entrance cannot be screenshotted (layered window) and is over in a tenth of a second, so
    // the only way to check it is to ask the real window.

    /// <summary>The card's clip rect, or null if it is not clipped. Should be zero-height on reveal.</summary>
    internal Rect? AuditCardClip() => _clipBox?.Rect;

    /// <summary>
    /// True when the card is currently clipped to a SQUIRCLE rather than a rounded rectangle.
    /// The invariant this guards: the silhouette must be the same shape at rest and in motion. A
    /// RectangleGeometry here means something has gone back to circular corners for the ride, which
    /// is a 3px-per-side shape pop at both ends of every transition (the defect the owner reported
    /// twice as "it resizes for a split second").
    /// </summary>
    internal bool AuditClipIsSquircle() => Root.Clip is not null && Root.Clip is not RectangleGeometry;

    /// <summary>Toki's opacity and scale at this instant. Both should be at their FROM on reveal.</summary>
    internal (double Opacity, double Scale) AuditMascotEntrance() => (MascotZone.Opacity, MascotPop.ScaleX);

    /// <summary>The content host's opacity. Should be 0 on reveal - it fades in behind the clip.</summary>
    internal double AuditContentOpacity() => Host.Opacity;

    // Clip height, mascot opacity, mascot scale and content opacity as they stood when the window
    // was last shown. See --revealtest.
    (double Clip, double MascotOpacity, double MascotScale, double Content) _entranceArm = (-1, -1, -1, -1);

    /// <summary>What the last reveal STARTED from. Deterministic - captured at Show(), not sampled.</summary>
    internal (double Clip, double MascotOpacity, double MascotScale, double Content) AuditEntranceArm() => _entranceArm;

    /// <summary>The cached view for a state, so a test can drive the real one the notch will show.</summary>
    internal UserControl AuditView(PillState s) => ViewFor(s);

    /// <summary>Is the mascot zone showing? Must be false once the notch has hidden, or the window's
    /// last composed frame keeps a picture of Toki in it (see ParkAfterHide).</summary>
    internal bool AuditMascotShowing() => MascotZone.Visibility == Visibility.Visible;

    /// <summary>Park Toki at the START of his entrance BEFORE anything makes him visible. A
    /// BeginTime-delayed animation does not hold its From while waiting - the property shows the
    /// BASE value - so anything animated with BeginTime needs its base value set to the From,
    /// or the delay is a preview of the ending.</summary>
    void ArmMascotReveal()
    {
        MascotPop.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        MascotPop.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        MascotZone.BeginAnimation(OpacityProperty, null);
        MascotPop.ScaleX = MascotPop.ScaleY = RevealFromScale;
        MascotZone.Opacity = 0;
    }

    const double RevealFromScale = 0.55;

    // The card leads; Toki springs in ~90ms behind with a little overshoot. Scale and opacity only,
    // so this stays on the composition thread.
    void RevealMascot()
    {
        // TOKI'S CLOCK MUST START WITH THE WINDOW, NOT BEFORE IT.
        //
        // This is reached from SetSurface, which runs EARLY in OnStateChanged - before UpdateLayout,
        // before PlaceExactTopCenter and before Show(). On a cold first open the layout pass between
        // here and the first painted frame can outlast the 90ms BeginTime below, so Toki was already
        // mid-spring on the frame he first became visible: he appeared part-grown instead of
        // arriving. --revealtest cannot catch it, because during a BeginTime delay the properties
        // still read their base values and the snapshot looks correct either way.
        //
        // Render priority runs after the current layout and composition batch, which is after Show()
        // on every path that reaches here.
        // ARM SYNCHRONOUSLY, START LATER. The deferral below is what keeps Toki's spring clock with
        // the window, but on any path where the zone was ALREADY visible (ArmMascotReveal is only
        // called when it was hidden) it left him painted at full opacity and full size for the frame
        // or two before the deferred call ran, and then the animation snapped him back to 0.55 scale
        // to spring again. That is the flicker the owner saw opening Home from the peek: Toki appears,
        // vanishes, and grows back.
        MascotZone.BeginAnimation(OpacityProperty, null);
        MascotZone.Opacity = 0;
        MascotPop.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        MascotPop.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        MascotPop.ScaleX = MascotPop.ScaleY = RevealFromScale;

        Dispatcher.BeginInvoke(new Action(StartMascotReveal), System.Windows.Threading.DispatcherPriority.Render);
    }

    void StartMascotReveal()
    {
        if (MascotZone.Visibility != Visibility.Visible) return;   // a newer state owns the surface now

        // Reduced motion: he still arrives, he just does not spring. Overshoot is the part that
        // makes people ill; a fade keeps the cause-and-effect that removing it entirely would lose.
        if (Theme.Motion.Reduced)
        {
            MascotPop.ScaleX = MascotPop.ScaleY = 1;
            MascotZone.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, Theme.Motion.Dur(Theme.Motion.Ms(180))) { EasingFunction = Theme.Motion.EaseOut });
            return;
        }

        // ═══ WAIT FOR THE OLD TOKI TO LEAVE ═══
        //
        // A peek draws its OWN small Toki inside its content, because the floating bubble is hidden
        // in that state. Opening a panel from a peek therefore has two of him for a moment: the
        // peek's, still on screen inside the outgoing page, and the bubble springing in at the
        // panel's gutter. Filmed at 60fps they are in different places on the same frames - the old
        // one at the card's right, the new one arriving at the left - which is what reads as Toki
        // flickering rather than arriving, and it breaks the one-Toki-per-surface rule mid-motion.
        //
        // 170ms clears the cross-fade's hand-off (the outgoing page is on a ~35% exit), so the peek's
        // Toki is gone before the bubble starts. He arrives a beat later, which is also correct
        // ordering: the card is the container, the character follows it.
        // 70, not 170. The 170 existed to let a DUPLICATE Toki fade before this one arrived; with
        // the mascot-only peek no longer photographed there is no duplicate to wait for, and the long
        // delay left a visible hole - filmed at 60fps, about ten frames with no Toki at all, which
        // reads as him blinking out rather than following the card. He now arrives just behind it.
        const int delayMs = 70;
        var pop = new DoubleAnimation(RevealFromScale, 1, TimeSpan.FromMilliseconds(360))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 },
        };
        MascotPop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        MascotPop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        MascotZone.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = Theme.Motion.EaseOut,
        });
    }

    /// <summary>Apply the peek surface after the collapse finishes, so the dark box shrinks away WITH
    /// the panel instead of vanishing the instant you hover out. No-op if the state changed meanwhile.</summary>
    void ScheduleSurface(PillState forState, bool peek, int ms)
    {
        var t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
            { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_state.Current != forState) return;
            SetSurface(peek);
            // The collapse faded the mascot zone out and SetSurface has just hidden it. Park the
            // entrance back at its START values here, or the next panel inherits a zero-opacity zone
            // with a finished animation still holding it down and Toki never springs in again.
            if (peek) ArmMascotReveal();
        };
        t.Start();
    }

    /// <summary>Hide the pill from screen capture / sharing (WDA_EXCLUDEFROMCAPTURE). Windows can
    /// drop the affinity across hide→show and composition writes, so re-apply on every show.</summary>
    void ApplyGhost()
    {
        if (_hwnd == IntPtr.Zero) return;
        // Set the affinity in BOTH directions: EXCLUDEFROMCAPTURE hides the notch, NONE reveals it.
        // Applying NONE (not just skipping) matters for the live Settings toggle - turning ghost OFF
        // must actively clear an exclusion that's already on the window.
        try { SetWindowDisplayAffinity(_hwnd, App.GhostDisabled ? WdaNone : WdaExcludeFromCapture); }
        catch (Exception ex) { App.Log($"ghost affinity failed (non-fatal): {ex.Message}"); }
    }

    /// <summary>Re-apply the current ghost setting to the live window (called when the Settings
    /// "Show Toki in screen recordings" toggle changes) - takes effect immediately, no restart.</summary>
    public void RefreshGhost() => ApplyGhost();

    /// <summary>Re-apply the notch surface for the current state (called when the "Glassy notch" toggle
    /// changes) - live, no restart. Peeks stay transparent; only an open panel's surface swaps.</summary>
    public void RefreshSurface()
    {
        var s = _state.Current;
        if (s == PillState.Hidden) { ApplyBlur(false); ApplyRoundRegion(false); return; }
        SetSurface(s is PillState.Collapsed or PillState.RecordingBar);
        // "Glassy notch" is now PURELY a transparency switch on the plate. It used to also grow the
        // panel 8px upward so a photographed backdrop could run to the screen edge; that backdrop is
        // gone (see PillWindow.xaml) and the bleed went with it, along with the 8px it was pushing
        // Toki's bubble down by.
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
        // SetWindowCompositionAttribute(WCA_ACCENT_POLICY) re-composites the window and can drop
        // the capture-exclusion affinity. Every surface swap funnels through here, so re-assert
        // ghost HERE. Idempotent; also correctly applies WDA_NONE when ghost is off.
        ApplyGhost();
    }

    /// <summary>Round the acrylic window's corners. A window REGION can't do this - DWM draws the
    /// acrylic backdrop across the full rectangle behind the window, ignoring the region - so we ask DWM
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
            // DWM draws a thin border around a rounded window (all four edges) - kill it so the top edge
            // stays flush with the screen (the notch reads as carved into the edge, not a floating pill).
            int border = rounded ? DwmwaColorNone : DwmwaColorDefault;
            DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch (Exception ex) { App.Log($"corner rounding failed (non-fatal): {ex.Message}"); }
        // SetWindowRgn + the DWM attribute changes above are further composition changes that can
        // shed WDA_EXCLUDEFROMCAPTURE - re-assert ghost here too (last writer wins).
        ApplyGhost();
    }

    /// <summary>Window chrome for the glass look. The translucency itself is the Border's own
    /// semi-transparent fill - DWM accent acrylic fills the whole rectangular window, ignoring
    /// the rounded corners.</summary>
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
        // Content settles in with a soft pop + fade.
        HostScale.ScaleX = HostScale.ScaleY = 0.97;
        var ease = Theme.Motion.ArriveStrong;
        var dur = new Duration(TimeSpan.FromMilliseconds(200));
        HostScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
        HostScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
        Host.Opacity = 0;
        Host.BeginAnimation(OpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(160))));
    }

    void HideAnimated()
    {
        // Retract UP into the top edge, GPU-COMPOSITED: only an animated clip + content fade run -
        // the window is never resized per frame.
        // 180ms. Filmed, the reveal's VISIBLE travel is over in about 180ms too (the 280 it is
        // given is mostly the tail of a curve that is already 99% done), so matching the number on
        // paper would have made the close feel longer than the open, not shorter. Match what the
        // eye sees, not what the constant says.
        ConcealUp(CardW(ActualWidth), 180, Hide);
    }

    /// <summary>Composited "unfurl": reveal the notch downward via an animated clip (render-only - no
    /// layout, no window resize, no SetWindowPos) while the content settles in. The window must
    /// already be at its final size. The initial 0-height clip is set synchronously so no full-height
    /// dark box flashes before the first frame.</summary>
    void RevealDown(double w, double h, int ms)
    {
        var gen = ++_clipGen;
        var geo = new RectangleGeometry(new Rect(0, 0, w, 0));
        PinCorners();
        DriveClip(geo);
        var anim = new RectAnimation(new Rect(0, 0, w, 0), new Rect(0, 0, w, h),
            new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = Theme.Motion.Drawer };
        // The clip only masks ROOT - the shadow caster is a sibling, so without this the full-size
        // shadow would sit there before the card has unfurled over it. Shadow follows the settle.
        ShadowCaster.BeginAnimation(OpacityProperty, null);
        ShadowCaster.Opacity = 0;
        // Drop the clip so content isn't capped - but ONLY if this reveal is still the latest clip op. A
        // rapid re-open/close can supersede it; a late-firing Completed must not clobber the new clip.
        anim.Completed += (_, _) =>
        {
            if (gen != _clipGen) return;
            ReleaseClip();
            Root.Clip = null;
            SettleCorners();
            if (ShadowCaster.Visibility == Visibility.Visible)
                ShadowCaster.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(1, Theme.Motion.Dur(Theme.Motion.Micro)) { EasingFunction = Theme.Motion.EaseOut });
        };
        geo.BeginAnimation(RectangleGeometry.RectProperty, anim);
        MorphContent();   // content settles in with the soft pop + fade
    }

    /// <summary>Composited "retract": conceal the notch upward via an animated clip + fade (render-only),
    /// then run <paramref name="done"/> (Hide). No per-frame window resize.</summary>
    void ConcealUp(double w, int ms, Action done)
    {
        var gen = ++_clipGen;
        // CardH, not the raw window height: Root is CardPadB shorter than the window, so clipping
        // at window height starts the retract 24px above the card and the first ~36% of the
        // conceal is a dead frame (the peek is 42 tall inside a 66 tall window).
        var h = CardH(ActualHeight > 0 ? ActualHeight : Height);
        var geo = new RectangleGeometry(new Rect(0, 0, w, h));
        PinCorners();   // and it stays pinned: ParkAfterHide leaves the clip shut
        DriveClip(geo);
        var anim = new RectAnimation(new Rect(0, 0, w, h), new Rect(0, 0, w, 0),
            new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = Theme.Motion.Retract };
        // THE CONTENT LEAVES WITH THE CARD. The clip is a MASK: on its own it eats the card from the
        // bottom up while the text inside stands perfectly still, which is the other half of why the
        // close read as a cut. The reveal has always paired its clip with MorphContent() so the
        // content settles in; the retract had no counterpart at all. Now the content rides up a
        // little as the card furls, so the two halves of the gesture match.
        // Transform only, never layout: this is a RenderTransform on the already-composited host.
        HostShift.BeginAnimation(TranslateTransform.YProperty, null);
        HostShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -Theme.Motion.Rise, new Duration(TimeSpan.FromMilliseconds(ms)))
            { EasingFunction = Theme.Motion.Retract });
        // The shadow leaves FIRST (a shadow with no caster is the tell of a cheap fade).
        ShadowCaster.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, Theme.Motion.Dur(Math.Max(60, ms - 60))) { EasingFunction = Theme.Motion.Depart });
        // TOKI LEAVES WITH THE CARD: the clip masks only Root, and the mascot is a SIBLING of it,
        // so without this fade the bubble stands at full opacity through the whole retract.
        MascotZone.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, Theme.Motion.Dur(Math.Max(60, ms - 40))) { EasingFunction = Theme.Motion.Depart });
        // Only finish the hide if this conceal is STILL the latest clip op: a superseding re-open's
        // clip must not be nulled and its window must not be hidden.
        //
        // ═══ THE RETAINED-SURFACE STUB ═══
        // Completed fires when the animation CLOCK ends - possibly a frame BEFORE the fully-shut
        // clip has been composed into the layered surface Windows retains for this HWND. Hiding
        // right then retains a card stub, which DWM replays for a frame on the next Show(). So:
        // pin every leaving element at its parked value NOW, wait until the composition is
        // genuinely empty, and only then hide. The gen re-check matters twice: a reveal that
        // supersedes during that wait must keep its window.
        _parkedEmptyFrame = false;
        anim.Completed += (_, _) =>
        {
            if (gen != _clipGen) return;
            geo.BeginAnimation(RectangleGeometry.RectProperty, null);
            geo.Rect = new Rect(0, 0, w, 0);
            Host.BeginAnimation(OpacityProperty, null); Host.Opacity = 0;
            MascotZone.BeginAnimation(OpacityProperty, null); MascotZone.Opacity = 0;
            ShadowCaster.BeginAnimation(OpacityProperty, null); ShadowCaster.Opacity = 0;
            // A dispatcher hop is NOT enough: priorities only order UI-THREAD work, and the render
            // THREAD presents asynchronously. CompositionTarget.Rendering is the honest signal -
            // subscribing forces WPF to produce frames, so waiting two cannot stall. After the
            // second, the surface Windows retains across Hide() truly holds nothing.
            var frames = 0;
            EventHandler? tick = null;
            tick = (_, _) =>
            {
                if (gen != _clipGen) { System.Windows.Media.CompositionTarget.Rendering -= tick; return; }
                if (++frames < 2) return;
                System.Windows.Media.CompositionTarget.Rendering -= tick;
                _parkedEmptyFrame = true;
                done(); ParkAfterHide();
            };
            System.Windows.Media.CompositionTarget.Rendering += tick;
        };
        geo.BeginAnimation(RectangleGeometry.RectProperty, anim);
        // ═══ THE CONTENT WAS NOT FADING. IT WAS BEING CUT. ═══
        // This read `BeginAnimation(OpacityProperty, null)` and then started a To-only fade to 0.
        // Clearing an animation does not freeze the property at what you can see - it reverts it to
        // its LOCAL value, and MorphContent leaves that local value at 0 (it sets Host.Opacity = 0
        // and then animates UP to 1, so 1 was only ever the animation's doing). So the null dropped
        // the content to invisible in a single frame, and the "fade" that followed ran 0 -> 0 and
        // was never visible at all.
        //
        // Filmed at 60fps: content fully legible at t=0, completely gone at t=16ms, with the card
        // still at full height behind it. That is the other half of "it just cuts back up" (owner,
        // 2026-09-27) - half was the easing curve, half was this.
        //
        // Reading Host.Opacity FIRST gives the current animated value (1), and a From/To animation
        // replaces the running one without a null. Never null an animation you are about to replace.
        Host.BeginAnimation(OpacityProperty,
            new DoubleAnimation(Host.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(Math.Max(60, ms - 20))))
            { EasingFunction = Theme.Motion.Retract });
    }

    /// <summary>Put the notch back to its "before it opens" state the moment it hides, so the last
    /// thing the window ever composed is EMPTY - the layered surface Windows retains for this HWND
    /// is replayed by DWM on the next Show() until the first new frame lands. Hiding the mascot here
    /// is also what lets SetSurface re-arm his entrance on every open.</summary>
    void ParkAfterHide()
    {
        // The clip is deliberately NOT cleared: it stays shut. Every path that shows this window
        // sets its own clip first, so nothing depends on it being null while hidden.
        ArmMascotReveal();
        // Put the retract's content lift back to zero, or the next open starts with its content
        // already sitting high inside the card and settles from the wrong place.
        HostShift.BeginAnimation(TranslateTransform.YProperty, null);
        HostShift.Y = 0;
        MascotZone.Visibility = Visibility.Collapsed;
        ShadowCaster.BeginAnimation(OpacityProperty, null);
        ShadowCaster.Opacity = 0;
        Host.BeginAnimation(OpacityProperty, null);
        Host.Opacity = 0;
    }

    // Monotonic token: every clip animation captures the current value; its Completed handler
    // no-ops if a newer clip op has since bumped this, so a late-finishing animation cannot
    // clobber the current clip.
    int _clipGen;

    // True once the conceal has composed a genuinely empty frame BEFORE Hide() - the layered surface
    // Windows retains for this HWND then holds nothing, so the next Show() cannot flash a stale card
    // stub. Audited by --revealtest.
    bool _parkedEmptyFrame;
    internal bool AuditParkedEmptyFrame() => _parkedEmptyFrame;

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) App.CloseOverlay();   // recording-aware: never hides a live mic
    }

    // ── Hover-to-expand ──
    // Do NOT use WPF MouseEnter/Leave here: while the pill animates its size under a stationary
    // cursor, WPF's hit-testing fires phantom leave/enter events (endless open/close flip-flop).
    // Poll the REAL cursor against the pill's REAL rectangle instead; the expanded rect contains
    // the collapsed one, giving clean hysteresis.
    const int HoverPollMs = 60;
    const int LoaderTuckGraceMs = 540; // the open loader tucks this long after the cursor leaves it
    int _loaderOffMs;   // accumulated time the cursor has been off the open loader
    const int ExpandDwellMs = 260;   // recording peek → controls: quick (you want Stop fast)

    // HOVER INTENT, 2026-09-23. The owner: "random hovers it opens up, random hovers it don't ...
    // theres no system in place for how toki is activated". The dwell below was the ONLY defence, so
    // it had to be long, and a long dwell is itself the thing that makes an intended hover fail.
    //
    // The model is hoverIntent's, not a timer's: an ARRIVAL is a cursor that SLOWS DOWN inside the
    // target. A transit is a cursor that keeps moving. Published default is 6px per 100ms (~60px/s);
    // at this 60ms tick that is ~4px, which is tighter than a hand resting on a mouse actually holds,
    // so PeekMaxStep sits at 10px/tick (~167px/s) - still 4x tighter than the edge summon's fling
    // gate, because the peek is a much smaller target than the whole top edge.
    //
    // With speed and direction carrying the rejection, the dwell no longer has to. 850ms was nearly
    // double NN/g's upper bound for exposing hidden content (300-500ms) and every millisecond of it
    // was a chance to fail an intended hover.
    const int OpenDwellMs = 360;     // launcher peek → Home, once the cursor has actually settled
    const int PeekMaxStep = 10;      // per-tick cursor move above which the cursor is passing THROUGH
    const int PeekReEntryGraceMs = 300; // a jitter out and straight back keeps the dwell it had earned
    const int PeekExitSlackPx = 20;  // leaving is harder than arriving - see CursorOverVisiblePeek
    const int CollapseDelayMs = 120; // close soon after leaving - closing should feel snappy
    const int RecordingFoldMs = 2200; // the recording bar is opened by a CLICK, so it folds away on
                                      // its own schedule, not the instant the pointer leaves it
    const int TuckDelayMs = 180;     // launcher peek tucks away this long after leaving edge+peek
    const int CollapsedGraceMs = 3000; // Toki peek greets for at least this long before it can tuck away
    long _collapsedTick;

    // Edge-summon tuning: must not fire when reaching for browser tabs / window controls - a
    // NARROW centre band, the absolute top pixel row, a short dwell, and a velocity gate.
    const int EdgeBandHalf = 90;     // half-width of the reveal zone (centre only)
    const int EdgeTopPx = 2;         // within this many px of the very top edge
    const int EdgeDwellMs = 300;     // hold at the edge this long to summon (deliberate, not a passing tab click)
    const int EdgeMaxStep = 40;      // per-tick cursor move above which it's a "fling", not a summon
    const int EdgeReArmBelowPx = 150; // the cursor must drop at least this far below the top edge before
                                      // it can summon again - so a cursor living up on the browser tab
                                      // strip can't keep re-summoning a just-closed-off Toki.
    static readonly bool EdgeSummon = Environment.GetEnvironmentVariable("TOKI_NO_EDGE") is null;

    readonly System.Windows.Threading.DispatcherTimer _hoverPoll =
        new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(HoverPollMs) };
    int _overMs, _offMs, _tuckMs, _edgeMs, _lastX, _lastY;
    readonly State.HoverIntent _peekIntent = new();   // "is this an arrival?" - see HoverIntent
    int _stepPx;       // how far the cursor moved on this tick - the arrival/transit signal
    int _stepY;        // and its vertical component: a cursor diving DOWN past the notch is leaving
    bool _hoverWired, _wasOver, _hasLast;
    bool _edgeArmed;   // true once the cursor has dropped clear of the top edge - a deliberate summon
                       // is a reach UP from the page body, never a cursor already parked up top.

    // The two hover pairs: resting pill → revealed view.
    static PillState? ExpandTarget(PillState s) => s switch
    {
        PillState.Collapsed => PillState.Home,
        PillState.RecordingBar => PillState.RecordingOpen,
        _ => null,
    };
    static PillState? CollapseTarget(PillState s) => s switch
    {
        // Home does NOT auto-collapse - it's dismissed explicitly (Esc/click-away).
        PillState.RecordingOpen => PillState.RecordingBar,
        _ => null,
    };
    internal static bool IsHoverPair(PillState a, PillState b) =>
        ExpandTarget(a) == b || CollapseTarget(a) == b
        // The peek BLOOMS into whatever it opens - the in-place path would snap to full size over
        // a surface that has not unfurled.
        || (a == PillState.Collapsed && b != PillState.Hidden)
        // The loader's 13px sliver morphs into its result instead of snapping a full page open.
        || (a == PillState.Summarizing && b is PillState.Notes or PillState.NoSpeech)
        // The notes-ready hand-off: the flight lands and the zone cross-fades out over the peek's
        // own Toki on the same spot (deferPeekBg).
        || (a == PillState.Notes && b == PillState.Collapsed);

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
        _peekIntent.Reset();
        _wasOver = _hasLast = false;
        if (!_hoverPoll.IsEnabled) _hoverPoll.Start();
    }

    void StopHoverPoll()
    {
        if (_hoverPoll.IsEnabled) _hoverPoll.Stop();
    }

    // ── Persistent, context-aware tucked sliver ──
    // A "stayable" page does NOT sit fully open forever: after idling it TUCKS to a thin sliver
    // with the view kept intact behind it. Hovering the sliver slides the SAME page back down
    // (no reload, state preserved) until a click outside fully tucks it away (Hidden).
    const double SliverH = 13;
    // A page you OPENED ON PURPOSE gets long enough to be used - a short interval races the user.
    const int IdleTuckMs = 9000;       // a deliberate page (Home/Settings) tucks this long after you stop hovering
    const int ResultShowMs = 2000;     // a result (Notes / nothing-was-said) announces itself this long, then tucks
    const int AutoHideMs = 9000;       // a tucked sliver fully hides after this (context remembered)
    const int NotesSliverHideMs = 120000; // a ready-notes sliver lingers this long (2 min) so you can come
                                          // back and read it, then self-clears so it can't block a new meeting
    const int EdgeReadyDelayMs = 700;  // after anything tucks to Hidden, wait this long before the edge can re-summon
                                       // - stops a just-hidden sliver from instantly popping a fresh Toki (the "both open" collision)
    long _edgeReadyTick;
    bool _tucked;
    bool _revealTucked;                // next reveal enters as a sliver (peek), not full open
    PillState _context = PillState.Home; // the page Toki remembers - re-summoning restores it
    // Input priority (NOT the DispatcherTimer default of Background) - Background timers get
    // starved while the app warms the ASR/LLM models at launch, so pages shown during warm-up
    // would never tuck.
    readonly System.Windows.Threading.DispatcherTimer _idle =
        new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(IdleTuckMs) };
    readonly System.Windows.Threading.DispatcherTimer _autoHide =
        new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(AutoHideMs) };
    bool _idleWired, _autoHideWired;

    // PrepCard is stayable: it is a page you open and write on, so it must stay put while you're on it.
    static bool IsStayable(PillState s) =>
        s is PillState.Home or PillState.Notes or PillState.NoSpeech or PillState.Summarizing or PillState.Settings or PillState.Calendar or PillState.About or PillState.PrepCard;

    // Pages the user actually clicks/scrolls/types on - everything except the tiny passive peeks.
    // These grab foreground focus on hover so the scroll wheel and clicks land on the notch, not the
    // window behind it. The peeks stay passive (they must never steal focus from a live meeting app).
    static bool NeedsInput(PillState s) =>
        s is not (PillState.Hidden or PillState.Collapsed or PillState.RecordingBar);

    // Bring the notch to the foreground for wheel/keyboard/click input. With the cursor over our
    // window we hold the "last input" right, so Activate() is granted rather than merely flashing.
    // Best-effort - never let a focus race throw.
    void TryActivateNotch()
    {
        try { if (!IsActive) Activate(); } catch { /* foreground-lock race - harmless */ }
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
                switch (IdleDecision(cur, _tucked, PillHasFocusedInput(), CursorOverPill()))
                {
                    case IdleAction.Stand: return;
                    case IdleAction.ReArm: ArmIdle(); return;
                }

                // Say WHY, with the numbers: "did the notch think the pointer had left, and was it
                // right?" answered in one log line.
                if (GetCursorPos(out var c))
                    App.Log($"idle tuck: {cur} after {IdleIntervalMs(cur)}ms - cursor ({c.X},{c.Y}) "
                        + $"vs notch [{_rectL},{_rectT}..{_rectR},{_rectB}] (+{InsidePad}px slack)");
                TuckCurrent();
            };
        }
        _idle.Interval = TimeSpan.FromMilliseconds(IdleIntervalMs(_state.Current));
        _idle.Stop();
        _idle.Start();
    }

    /// <summary>What the idle timer does when it fires. Pure, so it can be tested without a window.</summary>
    internal enum IdleAction
    {
        /// <summary>Nothing to tuck - let the single timer stay dead.</summary>
        Stand,
        /// <summary>Not now, but ask again - the timer MUST be restarted or it never fires again.</summary>
        ReArm,
        /// <summary>Tuck the page away.</summary>
        Tuck,
    }

    /// <summary>The idle-tuck decision. There is ONE idle timer and it stops itself on every tick:
    /// any branch that declines to tuck without re-arming kills it for the rest of the state.</summary>
    internal static IdleAction IdleDecision(PillState cur, bool tucked, bool typing, bool hovering)
    {
        if (!IsStayable(cur) || tucked) return IdleAction.Stand;
        // Never tuck a page you're actively TYPING in - the cursor rests off the notch while you
        // type, and idle-tucking then would eat your input.
        if (typing) return IdleAction.ReArm;
        // Only tuck once you've stopped hovering - never yank a page away while you're reading it.
        if (hovering) return IdleAction.ReArm;
        return IdleAction.Tuck;
    }

    /// <summary>How long a state stays fully open before it tucks to a sliver. Results (and the
    /// loader - a status, not a page to read) announce briefly; deliberate pages linger longer.</summary>
    internal static int IdleIntervalMs(PillState s) =>
        s is PillState.Notes or PillState.NoSpeech or PillState.Summarizing ? ResultShowMs : IdleTuckMs;

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

    /// <summary>How long the "Notes ready" announcement stands before Toki yields the screen.
    /// Deliberately tight - the pill is an announcement, not a prompt, and the note stays one
    /// hover away afterwards.</summary>
    const int NotesReadyPeekMs = 2000;
    readonly System.Windows.Threading.DispatcherTimer _notesPeek = new();
    bool _notesPeekWired;

    void ArmNotesPeek()
    {
        if (!_notesPeekWired)
        {
            _notesPeekWired = true;
            _notesPeek.Tick += (_, _) =>
            {
                _notesPeek.Stop();
                // Only the announcement it was armed for: a click already expanded it (_tucked
                // false) or the state moved on - either way the timer's moment has passed.
                if (_state.Current != PillState.Notes || !_tucked) return;
                // Never yank the pill out from under the pointer - ask again in a beat.
                if (CursorOverPill()) { _notesPeek.Start(); return; }
                StartNotesHandoffFlight();
            };
        }
        _notesPeek.Interval = TimeSpan.FromMilliseconds(NotesReadyPeekMs);
        _notesPeek.Stop();
        _notesPeek.Start();
    }

    // One-shot guard: the announce timer cannot fire twice, but nothing else may start a second
    // flight while one is in the air.
    bool _mascotFlying;
    readonly System.Windows.Threading.DispatcherTimer _flightPark = new();
    bool _flightParkWired;

    /// <summary>How long Toki's notes-ready exit flight takes. Character motion (Motion.cs names the
    /// exemption from the 350ms cap): long enough to read as a somersault, short enough that the
    /// screen is his ~0.8s after the announce ends.</summary>
    internal const int FlightMs = 560;

    /// <summary>The notes-ready exit: the disc dissolves, the blob hops, somersaults once and flies
    /// to exactly where the collapsed peek's own Toki rests, the pill concealing beneath him. On
    /// landing the state flips to Collapsed (a hover pair, so the zones cross-fade on the same spot);
    /// the note stays one click away. Transforms and opacity only - nothing here runs layout.</summary>
    void StartNotesHandoffFlight()
    {
        if (_mascotFlying) return;
        _mascotFlying = true;

        // Where the peek's Toki will rest, in THIS window's coordinates: the Collapsed card is
        // centred on the work area, TopGapDefault below its top, and CollapsedView centres a 28px
        // Toki in it - so the flight also shrinks the 44px bubble blob to 28/44.
        var dpi = _hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleX : 1.0;
        var workArea = PhysicalWorkArea();
        var (_, ph) = CardSize(PillState.Collapsed);
        var targetX = (workArea.Left + workArea.Right) / 2.0 / dpi;
        var targetY = workArea.Top / dpi + TopGapDefault + ph / 2.0;
        var toX = targetX - (Left + 29);   // zone is 58 wide at the window's left edge; centre = 29
        var toY = targetY - Top - 29;
        var fromY = MascotShift.Y;

        // The disc AND its contact shadow (a sibling ellipse) dissolve under the hop, then go fully
        // HIDDEN for the rest of the flight: a held opacity can be silently replaced by any restore
        // path; Visibility cannot be resurrected by an opacity write. The park puts both back.
        var dissolve = new DoubleAnimation(0, Theme.Motion.Dur(140)) { EasingFunction = Theme.Motion.Depart };
        dissolve.Completed += (_, _) =>
        {
            if (!_mascotFlying) return;
            MascotDisc.Visibility = Visibility.Hidden;
            MascotLift.Visibility = Visibility.Hidden;
        };
        MascotDisc.BeginAnimation(OpacityProperty, dissolve);
        MascotLift.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, Theme.Motion.Dur(140)) { EasingFunction = Theme.Motion.Depart });

        var glide = new CubicEase { EasingMode = EasingMode.EaseInOut };
        MascotShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(toX, Theme.Motion.Dur(FlightMs)) { EasingFunction = glide });

        // A small hop DOWN first (out of the bubble), then the rise to the edge.
        var hop = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(FlightMs)) };
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(fromY + 10,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(toY,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(FlightMs)),
            new CubicEase { EasingMode = EasingMode.EaseInOut }));
        hop.Completed += (_, _) =>
        {
            _mascotFlying = false;
            if (_state.Current == PillState.Notes && _tucked)
            {
                // Land: hand the character to the Collapsed peek. The zone fades out over the
                // peek's own Toki fading in on this very spot; once the deferred surface swap has
                // hidden the zone (ScheduleSurface, 240ms), the timer below parks the flight
                // transforms. MascotPop is NOT parked here - ArmMascotReveal owns it from the swap.
                _state.TryGo(PillState.Collapsed);
                _flightPark.Interval = TimeSpan.FromMilliseconds(320);
                _flightPark.Stop();
                _flightPark.Start();
            }
            else
            {
                // The state moved on mid-flight (a click or hover opened the note): put the bubble
                // straight back together in its gutter - no arm ran, so the scale is ours to fix.
                ParkFlightTransforms(restoreScale: true);
            }
        };
        MascotShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, hop);

        // One full somersault on the way up, and the shrink to the peek's 28px.
        MascotFlip.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation(-360, Theme.Motion.Dur(FlightMs)) { EasingFunction = glide });
        var shrink = new DoubleAnimation(28.0 / 44.0, Theme.Motion.Dur(FlightMs)) { EasingFunction = glide };
        MascotPop.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        MascotPop.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);

        if (!_flightParkWired)
        {
            _flightParkWired = true;
            _flightPark.Tick += (_, _) => { _flightPark.Stop(); ParkFlightTransforms(restoreScale: false); };
        }

        // The pill conceals under him while he flies - card, content and shadow only; the zone IS
        // the flight. The landing transition replaces this clip through its own generation bump.
        _clipGen++;
        var cw = CardW(ActualWidth);
        var chh = CardH(ActualHeight > 0 ? ActualHeight : Height);   // card, not window (see ConcealUp)
        var geo = new RectangleGeometry(new Rect(0, 0, cw, chh));
        PinCorners();
        DriveClip(geo);
        geo.BeginAnimation(RectangleGeometry.RectProperty,
            new RectAnimation(new Rect(0, 0, cw, chh), new Rect(0, 0, cw, 0),
                new Duration(TimeSpan.FromMilliseconds(380))) { EasingFunction = Theme.Motion.Depart });
        ShadowCaster.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, Theme.Motion.Dur(300)) { EasingFunction = Theme.Motion.Depart });
        Host.BeginAnimation(OpacityProperty, null);
        Host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(340))));
    }

    /// <summary>Put the flight's transforms away. The somersault angle, the sideways shift and the
    /// disc are always the flight's own to park; the POP SCALE belongs to ArmMascotReveal once a
    /// landing's surface swap has armed the next entrance, so only an aborted flight restores it.</summary>
    void ParkFlightTransforms(bool restoreScale)
    {
        MascotFlip.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        MascotFlip.Angle = 0;
        MascotShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        MascotShift.X = 0;
        MascotDisc.BeginAnimation(OpacityProperty, null);
        MascotDisc.Opacity = 1;
        MascotDisc.Visibility = Visibility.Visible;
        MascotLift.BeginAnimation(OpacityProperty, null);
        MascotLift.Opacity = 1;
        MascotLift.Visibility = Visibility.Visible;
        if (restoreScale)
        {
            MascotPop.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            MascotPop.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            MascotPop.ScaleX = MascotPop.ScaleY = 1;
        }
    }

    void TuckCurrent()
    {
        // The LOADER and a READY NOTE keep a hover-able sliver rather than closing off: the
        // loader's sliver is its "working" heartbeat; a ready note stays retrievable but
        // self-clears after a while so it can't block the next meeting.
        if (_state.Current is PillState.Summarizing or PillState.Notes)
        {
            _tucked = true;
            if (_state.Current == PillState.Notes)
            {
                // A ready note tucks to the "Notes ready >" pill rather than a blank 13px strip.
                ShowNotesReady();
                AnimateSize(Views.NotesReadyView.CardWidth, Views.NotesReadyView.CardHeight, 200);
                FadeHostIn(180);
                ArmAutoHide(NotesSliverHideMs);
                return;
            }
            // Toki leaves WITH the card: tucked, the loader is just the top line (see OnStateChanged).
            MascotZone.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, Theme.Motion.Dur(180)) { EasingFunction = Theme.Motion.Depart });
            AnimateSize(CardW(ActualWidth), SliverH, 200);   // retract to a sliver (CARD dims in, pads re-added inside)
            return;
        }
        // EVERYTHING ELSE closes off completely (context kept); the top-centre edge brings it back.
        App.CloseOverlay();   // recording-aware: a live session collapses to the bar, never vanishes
    }

    /// <summary>The DESIGN card size for a state, straight from the table and with no live view
    /// override - so a test can measure a view's art against the box it has to fit inside.</summary>
    internal static (double W, double H) DesignCardSize(PillState s) => Sizes[s];

    /// <summary>The card size for a state: the Sizes table, except that a cached view implementing
    /// <see cref="Views.IWidthAware"/> owns its own width. Never builds a view.</summary>
    (double W, double H) CardSize(PillState s)
    {
        var (w, h) = Sizes[s];
        if (_views.TryGetValue(s, out var v) && v is Views.IWidthAware wa) w = wa.CardWidth;
        return (w, h);
    }

    // The tucked "Notes ready" pill. Deliberately NOT a PillState of its own: the state stays
    // Notes (so the note, scroll position and Keep-or-Discard choice survive the tuck) - only what
    // the card DRAWS changes while tucked.
    Views.NotesReadyView? _notesReady;

    void ShowNotesReady()
    {
        // A click anywhere on the pill opens the note - the window-level handler (constructor)
        // covers it; hover never opens it (HoverTick).
        _notesReady ??= new Views.NotesReadyView();
        Host.BeginAnimation(OpacityProperty, null);
        Host.Opacity = 0;   // fade in under the size morph - never swap content at full opacity
        Host.Children.Clear();
        Host.Children.Add(_notesReady);
    }

    /// <summary>Put the real view back if the "Notes ready" pill is standing in for it.</summary>
    void RestoreTuckedView()
    {
        if (_notesReady is null || !Host.Children.Contains(_notesReady)) return;
        Host.BeginAnimation(OpacityProperty, null);
        Host.Opacity = 0;   // fade in under the size morph - never swap content at full opacity
        Host.Children.Clear();
        Host.Children.Add(ViewFor(_state.Current));
    }

    /// <summary>Fade the content host back in under a size morph. Paired with the Opacity = 0 the
    /// tuck/expand swaps set: content must never appear at full opacity in one frame.</summary>
    void FadeHostIn(int ms) =>
        Host.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, Theme.Motion.Dur(ms)) { EasingFunction = Theme.Motion.ArriveStrong });

    void ExpandCurrent()
    {
        _tucked = false;
        StopAutoHide();
        // The tucked loader hid Toki's bubble (just the top line); opening it brings him back with
        // the card, on the arrive curve.
        if (_state.Current == PillState.Summarizing)
            MascotZone.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(1, Theme.Motion.Dur(180)) { EasingFunction = Theme.Motion.EaseOut });
        RestoreTuckedView();
        var (w, h) = CardSize(_state.Current);
        var dpi = _hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleY : 1.0;
        var work = PhysicalWorkArea();
        var availH = (work.Bottom - work.Top) / dpi - TopGap - BottomMargin;
        AnimateSize(w, Math.Min(ContentHeight(_state.Current, w, h), availH), 220);
        FadeHostIn(200);
        ArmIdle();
    }

    /// <summary>
    /// The height a panel should grow to. For an auto-height page it's the view's MEASURED content
    /// height (so the page is concise - no dead space below the content), measured at the panel's
    /// fixed width. Other pages use their fixed Sizes height.
    /// </summary>
    /// <summary>
    /// The breath exists so DPI rounding and surface chrome cannot clip the LAST ROW of a page whose
    /// content wraps. The recording bar has no last row to clip: it is one fixed-height 26px row
    /// inside fixed 12px margins, and it measures exactly.
    ///
    /// So the flat 14 was pure dead glass there. The row measures 50, the card was built at 64, the
    /// content is top-anchored, and the result is a bar sitting visibly high in its own box with a
    /// strip of empty plate beneath it. A rule that protects wrapping text should not tax a fixed row.
    /// 4 still absorbs the rounding it was written for.
    /// </summary>
    static double BreathFor(PillState state) => state switch
    {
        // A fixed-height row measures exactly and has no last line to clip.
        PillState.RecordingOpen or PillState.RecordingBar => 2,
        _ => BottomBreath,
    };

    double ContentHeight(PillState state, double width, double fallback)
    {
        if (!AutoHeight.Contains(state)) return fallback;
        try
        {
            var v = ViewFor(state);
            v.Measure(new Size(Math.Max(1, width - 2), double.PositiveInfinity));   // −2 for the L/R border
            var m = v.DesiredSize.Height;
            // The design height is a MINIMUM, never a cap. DPI rounding + the surface chrome shave
            // a few px off the bottom, so add breathing room or the last row is clipped.
            return m > 8 ? Math.Max(fallback, Math.Ceiling(m) + BreathFor(state)) : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>Self-test seams for --measuretest: the auto-height set, the breath and the Sizes floor.
    /// A floor set ABOVE a page's real content pads the card with space the page never uses, which is
    /// invisible to every check that only asks whether a page is clipped.</summary>
    internal static bool DebugIsAutoHeight(PillState s) => AutoHeight.Contains(s);
    internal static double DebugBottomBreath => BottomBreath;
    internal static double DebugSizeFloor(PillState s) => Sizes.TryGetValue(s, out var v) ? v.H : 0;

    // Bottom breathing room added to every auto-height page's measured content (see ContentHeight):
    // covers the few px lost to DPI rounding + the notch's bottom chrome.
    /// <summary>
    /// Slack added below a measured page so DPI rounding cannot shave the last line off.
    ///
    /// WAS 14, and the owner is right that it showed: DPI rounding costs one or two pixels, not
    /// fourteen, so every auto-height panel in the notch carried a strip of empty plate under its
    /// content. Content is top-anchored, so that strip does not read as symmetric padding - it reads
    /// as the card being slightly too big for what is in it, on every page.
    ///
    /// 6 is what the rounding actually needs, with room to spare. If a page ever clips its last row,
    /// the bug is in that page's Measure, not in this number.
    /// </summary>
    const double BottomBreath = 6;

    /// <summary>Re-measure the current AUTO-HEIGHT view and smoothly grow/shrink the notch to fit - for
    /// when a page's content changes size WITHOUT a state change (the Ask notch going from the short
    /// "listening" state to the taller answer).</summary>
    bool _growing;
    public void GrowToContent()
    {
        // Re-entrancy guard: a view triggering a grow from its own constructor (before it's cached)
        // would recurse forever building duplicate views.
        if (_growing) return;
        var s = _state.Current;
        // Never size for a state whose TRANSITION has not run yet: AppState notifies subscribers in
        // order and view-refresh handlers run BEFORE this window's OnStateChanged, so a
        // refresh-triggered grow would morph the box to the next state's size while surface and
        // content are still the old state's. A grow is only an ADJUSTMENT to the state on screen.
        if (s != _shownState) return;
        if (!AutoHeight.Contains(s)) return;
        _growing = true;
        try
        {
            var (w, h) = CardSize(s);
            // The view's content just changed (the onboarding step advanced, the Ask answer streamed
            // in). Flush the pending layout FIRST: ContentHeight measures the view out-of-tree, and an
            // out-of-tree Measure() on a view already hosted in the visual tree returns the PREVIOUS
            // content's height until a layout pass has run - which sized the card for the page it just
            // replaced and clipped the taller one.
            ViewFor(s).UpdateLayout();
            var dpi = _hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleY : 1.0;
            var work = PhysicalWorkArea();
            var availH = (work.Bottom - work.Top) / dpi - TopGap - BottomMargin;
            var target = Math.Min(ContentHeight(s, w, h), availH);
            // Compare in WINDOW space on BOTH axes: target/w are CARD sizes, ActualHeight/ActualWidth
            // are WINDOW sizes (card + pads). Comparing the card target against the window height was
            // an apples-to-oranges test that read "no change" whenever two steps' heights differed by
            // ~CardPadB - which silently skipped the onboarding name->privacy grow and clipped it.
            // Width as well as height: the recording bar's chevron widens the card without changing
            // its height, and comparing height alone would swallow that.
            if (Math.Abs(WinH(target) - ActualHeight) < 2 && Math.Abs(WinW(w) - ActualWidth) < 2) return;
            if (SnapResize.Contains(s))
            {
                // Stepped flow (onboarding): resize INSTANTLY, composed in the same frame as the new
                // page's content, so the card never appears to animate its size. No morph, no clip
                // machinery - one synchronous resize onto the target.
                SetCardSize(w, target);
                PlaceMascot(target, 0);
                SizeWindowNow(w, target);
                SettleCorners();
                return;
            }
            AnimateSize(w, target, 150);   // snappy so it keeps up as the answer streams in patches
        }
        finally { _growing = false; }
    }

    /// <summary>How far the cursor moved since the last tick. ONE measurement, shared by the edge
    /// summon and the peek, so the two cannot disagree about whether the cursor is moving. Runs every
    /// tick including the ones that return early, or the next reading would be a jump.</summary>
    void TrackCursorStep()
    {
        if (!GetCursorPos(out var p)) { _hasLast = false; _stepPx = 0; _stepY = 0; return; }
        if (_hasLast)
        {
            var dx = p.X - _lastX; var dy = p.Y - _lastY;
            _stepY = dy;
            _stepPx = (int)Math.Round(Math.Sqrt((double)dx * dx + (double)dy * dy));
        }
        else { _stepPx = 0; _stepY = 0; }
        _lastX = p.X; _lastY = p.Y; _hasLast = true;
    }

    void HoverTick()
    {
        TrackCursorStep();
        var cur = _state.Current;

        // Tucked away: watch the top-centre edge to summon the peek.
        if (cur == PillState.Hidden) { EdgeTick(); return; }

        // A PEEK is judged against the few pixels it actually draws; a panel against the whole
        // window (see CursorOverVisiblePeek for why the two rules differ).
        var isPeek = cur is PillState.Collapsed or PillState.RecordingBar;
        // 3px to arrive (enough that the edge of a small peek is reachable, not so much that the
        // gutter comes back); PeekExitSlackPx to stay, once the dwell has already been earned.
        var over = isPeek ? CursorOverVisiblePeek(_peekIntent.DwellMs > 0 ? PeekExitSlackPx : 3) : CursorOverPill();

        // Stayable pages: tuck to a sliver after idle; hover the sliver to bring the SAME
        // page back; keep it up while you're on it. Context is preserved (no rebuild).
        if (IsStayable(cur))
        {
            if (_tucked)
            {
                // The loader's sliver and the "Notes ready" pill open on CLICK only - they sit in
                // the top-centre band every cursor crosses on the way to browser tabs. A page the
                // user tucked themselves re-opens on hover: that hover is a deliberate return.
                if (over && cur is not (PillState.Summarizing or PillState.Notes)) ExpandCurrent();
            }
            else if (cur == PillState.Summarizing)
            {
                // The open loader tucks when the cursor LEAVES, on a short grace - a status strip
                // has nothing to read for long. The idle timer stays as a backstop.
                _loaderOffMs = over ? 0 : _loaderOffMs + HoverPollMs;
                if (_loaderOffMs >= LoaderTuckGraceMs)
                {
                    _loaderOffMs = 0;
                    App.Log("loader tuck: cursor left the loader");
                    TuckCurrent();
                }
            }
            else if (over) ArmIdle();               // hovering keeps a page open
            return;
        }
        _loaderOffMs = 0;

        // Cute: Toki blinks the moment the pointer lands on the resting peek.
        if (over && !_wasOver && (cur == PillState.Collapsed || cur == PillState.RecordingBar)) BlinkCurrentToki();
        _wasOver = over;

        if (ExpandTarget(cur) is PillState expand)
        {
            // The RECORDING peek opens on CLICK only (RecordingBarView.OnExpand): a bar unfolding
            // because the pointer passed the top of the screen mid-meeting is an interruption.
            // The launcher peek keeps its long deliberate dwell.
            var hoverOpens = cur == PillState.Collapsed;
            _offMs = 0;

            // ARRIVING, OR PASSING THROUGH? HoverIntent owns that judgement; this only feeds it the
            // cursor's motion and asks.
            if (!hoverOpens) _peekIntent.Reset();
            if (hoverOpens && _peekIntent.Tick(over, _stepPx, _stepY, HoverPollMs,
                                               PeekMaxStep, OpenDwellMs, PeekReEntryGraceMs))
            {
                // Hovering the resting peek opens the remembered page - UNLESS a prep card is armed
                // for a meeting about to start. The card never takes the screen by itself; hover is
                // the only way it appears before a meeting.
                if (cur == PillState.Collapsed)
                    _state.ForceForDemo(Calendar.PrepWatcher.Armed ? PillState.PrepCard : _context);
                else _state.TryGo(expand);
                return;
            }

            // The LAUNCHER peek (Collapsed) tucks back to Hidden once you leave both the peek
            // and the top edge. (The recording peek stays put - recording is ongoing.)
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
            // The recording bar is opened on purpose, so it must not slam shut the instant the
            // pointer drifts off. It folds away after a couple of seconds; a click closes it now.
            var delay = cur == PillState.RecordingOpen ? RecordingFoldMs : CollapseDelayMs;
            if (_offMs >= delay) { _offMs = 0; _state.TryGo(collapse); }
        }
        else StopHoverPoll(); // not a hover state anymore
    }

    // Edge-summon: accumulate dwell only while the cursor is pressed to the very top of the
    // centre band AND moving slowly. A fast reach toward a browser tab won't cross it.
    void EdgeTick()
    {
        // _hasLast/_lastX are TrackCursorStep's now - clearing them here would make the next tick
        // read a jump of zero and call a fling "slow".
        if (!EdgeSummon || EdgeSuppressed() || !GetCursorPos(out var p)) { _edgeMs = 0; return; }
        var work = PhysicalWorkArea();
        int cx = work.Left + (work.Right - work.Left) / 2;

        // Re-arm only once the cursor has dropped clear of the top edge: a real summon is a reach
        // UP from the page, not a cursor living on the browser's tab strip - without this a
        // just-closed Toki instantly re-summons.
        if (p.Y > work.Top + EdgeReArmBelowPx) _edgeArmed = true;

        // Brief cooldown right after a close-off. Motion keeps being tracked by TrackCursorStep, so
        // the dwell starts clean after it without this having to bookkeep anything.
        if (Environment.TickCount64 < _edgeReadyTick) { _edgeMs = 0; return; }

        bool atEdge = p.Y <= work.Top + EdgeTopPx && Math.Abs(p.X - cx) <= EdgeBandHalf;
        // The SHARED step (TrackCursorStep), not a second private one: two copies of "is the cursor
        // moving" drift apart the moment one caller returns early, and this one used to skip its own
        // update on the suppressed paths.
        bool slow = _stepPx <= EdgeMaxStep;

        if (_edgeArmed && atEdge && slow)
        {
            _edgeMs += HoverPollMs;
            if (_edgeMs >= EdgeDwellMs)
            {
                _edgeMs = 0;
                _edgeArmed = false;   // consume it - the cursor must drop back down before summoning again
                // If Toki is mid-task (writing notes), reaching the edge brings that back;
                // otherwise Toki peeks first.
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

    /// <summary>Is the pointer on (or near) the notch? Decides whether a page may tuck itself away.
    /// Must use the SAME edge slack as <see cref="PointInWindow"/> - a page vanishing while somebody
    /// is moving towards it is the most hostile thing this window can do.</summary>
    bool CursorOverPill()
    {
        // While a notch dropdown is open, keep the panel "held" - the cursor is likely down in the
        // list (below the notch), which must not trigger the idle-tuck.
        if (Views.NotchPopups.AnyOpen) return true;
        if (!GetCursorPos(out var p) || _rectR <= _rectL) return false;
        return PointInWindow(p.X, p.Y);
    }

    /// <summary>Is the pointer on the peek's own drawn pixels, not the (much larger) window around
    /// them? A peek judged by the window drops open when the pointer is nowhere near it. Panels keep
    /// the forgiving window test - the mascot gutter is genuinely part of them.</summary>
    /// <summary>Is the cursor on the peek? <paramref name="slack"/> is how far outside the drawn card
    /// still counts.
    ///
    /// ENTER AND EXIT ARE NOT THE SAME RECTANGLE. Opening is judged tightly, so a cursor that merely
    /// grazes the peek does not start the clock. Staying open is judged loosely, because an element
    /// that is already up should be harder to dismiss than it was to summon - a 1px drift closing a
    /// panel is the flicker every hover menu in the world has had to fix. Win32 ships the same
    /// asymmetry (tooltip 500ms in, 5000ms autopop) and so does every published mega-menu.</summary>
    bool CursorOverVisiblePeek(int slack)
    {
        if (!GetCursorPos(out var p) || _rectR <= _rectL) return false;
        var dpi = _hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this) : new DpiScale(1, 1);
        var padL = (int)Math.Round(CardPadL * dpi.DpiScaleX);
        var padR = (int)Math.Round(CardPadR * dpi.DpiScaleX);

        // The CARD, not the window's card area: during a transition the window is held at the
        // union of both sizes, so the window alone would report a peek the size of the panel it
        // just collapsed out of.
        var areaL = _rectL + padL;
        var areaW = Math.Max(1, (_rectR - padR) - areaL);
        var cardWpx = _cardW > 0 ? (int)Math.Round(_cardW * dpi.DpiScaleX) : areaW;
        var cardHpx = _cardH > 0 ? (int)Math.Round(_cardH * dpi.DpiScaleY) : (_rectB - _rectT);
        var left = areaL + Math.Max(0, (areaW - cardWpx) / 2);   // the card is centred in that area

        return p.X >= left - slack && p.X <= left + cardWpx + slack
            && p.Y >= _rectT - slack && p.Y <= _rectT + cardHpx + slack;
    }

    // True while the user is typing in a field inside the notch (create composer, API key, …) - used to
    // suppress the idle-tuck so a resting cursor off the notch can't close a page mid-input.
    bool PillHasFocusedInput() =>
        IsKeyboardFocusWithin
        && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox;

    // ---- interop ----
    const int GwlExStyle = -20;
    const int WsExToolWindow = 0x00000080;
    const uint WdaExcludeFromCapture = 0x11;
    const uint WdaNone = 0x00;   // visible to capture - ghost mode off

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
    const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);     // DWMWA_COLOR_NONE - no border
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
