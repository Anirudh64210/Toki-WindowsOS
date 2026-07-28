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
        [PillState.Collapsed]     = (52, 42),    // launcher peek (Toki alone)
        [PillState.MeetingPrompt] = (288, 72),
        [PillState.RecordingBar]  = (52, 22),    // minimal upside-down eyes-peek — clear of browser tabs
        [PillState.RecordingOpen] = (284, 116),  // compact timer + stop
        [PillState.Summarizing]   = (320, 156),
        [PillState.Notes]         = (360, 320),  // smaller — no longer half the screen
        [PillState.NoSpeech]      = (300, 96),
        [PillState.Home]          = (320, 220),
        [PillState.Library]       = (404, 440),
        [PillState.Settings]      = (384, 312),
        [PillState.ManualNote]    = (392, 410),
    };

    const double TopGap = 0;        // flush to the top edge — the notch hangs from the top
    const double BottomMargin = 12;  // never grow past the bottom of the work area
    const double CornerRadius = 22;

    // Click-outside dismisses ONLY these ephemeral popups (the launcher menu and the
    // transient "nothing was said" toast). Everything with content you actually work
    // with — Notes, Library, Settings, ManualNote — stays put until you close it with
    // its own button or Esc; a stray click into your real work must never make it
    // vanish. Recording/Summarizing/MeetingPrompt were never dismissable this way.
    // Clicking into your real work fully tucks a stayable page away (→ Hidden). Until then it
    // lives as a hover-able sliver. (Summarizing is excluded — it must stay to reach its
    // result; recording/prompt have their own lifecycles.)
    static readonly HashSet<PillState> DismissOnClickOutside =
        [PillState.Home, PillState.Notes, PillState.NoSpeech, PillState.Settings];

    readonly AppState _state;
    readonly Dictionary<PillState, UserControl> _views = new();
    Interop.InputMonitor? _input;
    long _panelShownTick;
    IntPtr _hwnd;

    // Window bounds in physical pixels, for the (off-thread) click-outside hook.
    int _rectL, _rectT, _rectR, _rectB;

    Brush? _surfaceBg, _surfaceBorder;   // the dark frosted notch fill (hidden while peeking)

    public PillWindow(AppState state)
    {
        InitializeComponent();
        _surfaceBg = Root.Background;
        _surfaceBorder = Root.BorderBrush;
        _state = state;
        // Clicking the Toki peek opens the remembered page (Home by default).
        App.OpenPeek = () => { if (_state.Current == PillState.Collapsed) _state.ForceForDemo(_context); };
        _state.StateChanged += (o, n) => Dispatcher.Invoke(() => OnStateChanged(o, n));
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea) && IsVisible)
                Dispatcher.Invoke(() => { PositionTopCenter(); CacheWindowRect(); });
        };
        LocationChanged += (_, _) => CacheWindowRect();
        // Keep the pill anchored bottom-centre as it grows/shrinks — so an animated resize
        // (the smooth recording expand) stays put instead of drifting from its top-left.
        SizeChanged += (_, _) => { if (IsVisible && _hwnd != IntPtr.Zero) PlaceTopCenter(); CacheWindowRect(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            ApplyGhost(); // WDA_EXCLUDEFROMCAPTURE — works on this transparent window too
            SetWindowLong(_hwnd, GwlExStyle, GetWindowLong(_hwnd, GwlExStyle) | WsExToolWindow);
            EnableAcrylic(); // real blur-behind so the translucency reads as frosted glass
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
            }));
    }

    bool PointInWindow(int x, int y) =>
        (_rectR <= _rectL) || (x >= _rectL && x <= _rectR && y >= _rectT && y <= _rectB);

    void CacheWindowRect()
    {
        if (_hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out var r))
            (_rectL, _rectT, _rectR, _rectB) = (r.Left, r.Top, r.Right, r.Bottom);
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
        int y = work.Top + gap;   // top edge fixed → height grows downward
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
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
        if (SHQueryUserNotificationState(out var st) == 0 &&
            (st == QunsBusy || st == QunsRunningD3dFullScreen || st == QunsPresentationMode)) return true;
        return false;
    }

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
            _ => throw new ArgumentOutOfRangeException(nameof(s)),
        };
        _views[s] = v;
        return v;
    }

    void OnStateChanged(PillState old, PillState next)
    {
        if (next == PillState.Hidden) { StartHoverPoll(); HideAnimated(); return; } // keep polling: watch the top edge to summon

        var fromHidden = old == PillState.Hidden || !IsVisible;
        Host.Children.Clear();
        Host.Children.Add(ViewFor(next));
        if (DismissOnClickOutside.Contains(next)) _panelShownTick = Environment.TickCount64;

        // While PEEKING (the resting/recording notch), drop the dark frosted box — Toki is
        // just the character floating from the top edge. The dark surface only appears once
        // the notch pops open into a usable panel.
        if (next == PillState.Collapsed) _collapsedTick = Environment.TickCount64; // start the greeting grace
        var peek = next is PillState.Collapsed or PillState.RecordingBar;
        Root.Background = peek ? System.Windows.Media.Brushes.Transparent : _surfaceBg;
        Root.BorderBrush = peek ? System.Windows.Media.Brushes.Transparent : _surfaceBorder;
        Root.BorderThickness = peek ? default : new Thickness(1, 0, 1, 1);
        Sheen.Visibility = peek ? Visibility.Collapsed : Visibility.Visible;

        // A reveal can enter directly as a SLIVER (peek) instead of full open — the edge-summon
        // restores your last page as a hover-able sliver, and RESULTS (notes / nothing-was-said)
        // arrive as a quiet sliver you hover to open, never a panel dropped in your face.
        var enterTucked = (_revealTucked || next is PillState.Notes or PillState.NoSpeech) && IsStayable(next);
        _revealTucked = false;
        _tucked = enterTucked;

        var (w, h) = Sizes[next];
        var dpi = _hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleY : 1.0;
        var work = PhysicalWorkArea();
        // Never exceed the screen — tall panels clamp and scroll internally.
        var availH = (work.Bottom - work.Top) / dpi - TopGap - BottomMargin;
        var targetH = enterTucked ? SliverH : Math.Min(h, availH);

        // A hover pill expand/collapse (tiny Toki ⇄ Home, or ⇄ recording controls) is
        // animated so it GROWS/SHRINKS smoothly, not a hard size-snap. Closing is quicker
        // than opening so dismissing feels snappy. Other transitions stay instant.
        var hoverMorph = !fromHidden && IsHoverPair(old, next);
        var collapsing = next is PillState.Collapsed or PillState.RecordingBar;

        if (hoverMorph)
        {
            AnimateSize(w, targetH, collapsing ? 190 : 300);   // SizeChanged re-centres each frame
            Host.BeginAnimation(OpacityProperty, null);
            Host.Opacity = 0;             // the new content fades in as the pill morphs (masks reflow)
            Host.BeginAnimation(OpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(collapsing ? 150 : 240))));
        }
        else if (fromHidden)
        {
            // Drop DOWN out of the top edge: start tucked (tiny height) and grow to full,
            // content fading in — the notch slides into view.
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            Width = w;
            Height = 8;
            Show(); ApplyGhost();          // re-apply WDA — hide/show can drop it
            PlaceTopCenter();
            AnimateSize(w, targetH, 280);  // SizeChanged re-anchors top each frame → grows down
            Host.BeginAnimation(OpacityProperty, null);
            Host.Opacity = 0;
            Host.BeginAnimation(OpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(240))));
        }
        else
        {
            BeginAnimation(WidthProperty, null);   // clear any in-flight resize animation
            BeginAnimation(HeightProperty, null);
            Width = w;
            Height = targetH;
            PositionTopCenter();   // physical-pixel placement, DPI-proof (top-anchored notch)
            MorphContent();
        }
        CacheWindowRect();

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
            if (enterTucked) ArmAutoHide();      // entered as a sliver → count down to fully hide
            else ArmIdle();                      // fully open → tuck to a sliver after idle
        }
        else { StopIdle(); StopAutoHide(); }
    }

    /// <summary>Smoothly animate the window's size (the pill morph) with a gentle ease.
    /// Animates from the CURRENT actual size (explicit From) so a morph that interrupts an
    /// in-flight one can't get stuck holding the old target.</summary>
    void AnimateSize(double w, double h, int ms)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(ms));
        var fromW = ActualWidth > 0 ? ActualWidth : w;
        var fromH = ActualHeight > 0 ? ActualHeight : h;
        BeginAnimation(WidthProperty, new DoubleAnimation(fromW, w, dur) { EasingFunction = ease });
        BeginAnimation(HeightProperty, new DoubleAnimation(fromH, h, dur) { EasingFunction = ease });
    }

    /// <summary>
    /// Hide the pill from screen capture / sharing (WDA_EXCLUDEFROMCAPTURE) — the same
    /// mechanism Electron's setContentProtection uses. It works fine on this transparent
    /// window, but Windows can drop the affinity across hide→show, so we re-apply it
    /// every time the pill is shown.
    /// </summary>
    void ApplyGhost()
    {
        if (_hwnd == IntPtr.Zero || App.GhostDisabled) return;
        try { SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture); }
        catch (Exception ex) { App.Log($"ghost affinity failed (non-fatal): {ex.Message}"); }
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
            var dark = 1;
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
        // Tuck back UP into the top edge: retract the height (top is fixed, so the bottom
        // slides up) while the content fades — reads as the notch sliding away.
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        BeginAnimation(HeightProperty, new DoubleAnimation(Math.Max(ActualHeight, 8), 8,
            new Duration(TimeSpan.FromMilliseconds(170))) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(150)));
        fade.Completed += (_, _) => { BeginAnimation(HeightProperty, null); Hide(); };
        Host.BeginAnimation(OpacityProperty, fade);
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) _state.ForceForDemo(PillState.Hidden);
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
    const int EdgeDwellMs = 220;     // hold at the edge this long to summon
    const int EdgeMaxStep = 40;      // per-tick cursor move above which it's a "fling", not a summon
    static readonly bool EdgeSummon = Environment.GetEnvironmentVariable("TOKI_NO_EDGE") is null;

    readonly System.Windows.Threading.DispatcherTimer _hoverPoll =
        new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(HoverPollMs) };
    int _overMs, _offMs, _tuckMs, _edgeMs, _lastX, _lastY;
    bool _hoverWired, _wasOver, _hasLast;

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
    const int IdleTuckMs = 2500;       // a page tucks to a sliver this long after you stop hovering
    const int SummarizeShowMs = 2800;  // the loader shows this long, then tucks to a working sliver
    const int AutoHideMs = 9000;       // a tucked sliver fully hides after this (context remembered)
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
        s is PillState.Home or PillState.Notes or PillState.NoSpeech or PillState.Summarizing or PillState.Settings;

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
                // The loader tucks after a brief flash regardless of hover; other pages only
                // tuck once you've stopped hovering them.
                if (cur == PillState.Summarizing || !CursorOverPill()) TuckCurrent();
            };
        }
        _idle.Interval = TimeSpan.FromMilliseconds(_state.Current == PillState.Summarizing ? SummarizeShowMs : IdleTuckMs);
        _idle.Stop();
        _idle.Start();
    }

    void StopIdle() { if (_idle.IsEnabled) _idle.Stop(); }

    void ArmAutoHide()
    {
        if (!_autoHideWired)
        {
            _autoHideWired = true;
            _autoHide.Tick += (_, _) => { _autoHide.Stop(); if (_tucked && !CursorOverPill()) _state.ForceForDemo(PillState.Hidden); };
        }
        _autoHide.Stop();
        _autoHide.Start();
    }

    void StopAutoHide() { if (_autoHide.IsEnabled) _autoHide.Stop(); }

    void TuckCurrent()
    {
        _tucked = true;
        AnimateSize(ActualWidth, SliverH, 200);   // retract to a sliver (top-anchored: hangs a little)
        ArmAutoHide();                            // a tucked sliver fully hides after a while (context kept)
    }

    void ExpandCurrent()
    {
        _tucked = false;
        StopAutoHide();
        var (w, h) = Sizes[_state.Current];
        var dpi = _hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleY : 1.0;
        var work = PhysicalWorkArea();
        var availH = (work.Bottom - work.Top) / dpi - TopGap - BottomMargin;
        AnimateSize(w, Math.Min(h, availH), 220);
        ArmIdle();
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
        bool atEdge = p.Y <= work.Top + EdgeTopPx && Math.Abs(p.X - cx) <= EdgeBandHalf;
        int dx = _hasLast ? p.X - _lastX : 0, dy = _hasLast ? p.Y - _lastY : 0;
        bool slow = dx * dx + dy * dy <= EdgeMaxStep * EdgeMaxStep;
        _lastX = p.X; _lastY = p.Y; _hasLast = true;

        if (atEdge && slow)
        {
            _edgeMs += HoverPollMs;
            // Toki peeks FIRST (the cute character) — you then click/hover it to open your page.
            if (_edgeMs >= EdgeDwellMs) { _edgeMs = 0; _state.ForceForDemo(PillState.Collapsed); }
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
        if (!GetCursorPos(out var p) || _rectR <= _rectL) return false;
        return p.X >= _rectL && p.X <= _rectR && p.Y >= _rectT && p.Y <= _rectB;
    }

    // ---- interop ----
    const int GwlExStyle = -20;
    const int WsExToolWindow = 0x00000080;
    const uint WdaExcludeFromCapture = 0x11;

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
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
