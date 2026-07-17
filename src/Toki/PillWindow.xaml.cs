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
        [PillState.Collapsed]     = (132, 40),
        [PillState.MeetingPrompt] = (300, 74),
        [PillState.RecordingBar]  = (138, 46),
        [PillState.RecordingOpen] = (372, 234),
        [PillState.Summarizing]   = (340, 164),
        [PillState.Notes]         = (424, 438),
        [PillState.NoSpeech]      = (320, 100),
        [PillState.Home]          = (344, 232),
        [PillState.Library]       = (404, 440),
        [PillState.Settings]      = (404, 440),
        [PillState.ManualNote]    = (424, 438),
    };

    const double BottomGap = 14;  // above the taskbar
    const double TopMargin = 12;  // never touch the top edge
    const double CornerRadius = 22;

    // Click-outside dismisses ONLY these ephemeral popups (the launcher menu and the
    // transient "nothing was said" toast). Everything with content you actually work
    // with — Notes, Library, Settings, ManualNote — stays put until you close it with
    // its own button or Esc; a stray click into your real work must never make it
    // vanish. Recording/Summarizing/MeetingPrompt were never dismissable this way.
    static readonly HashSet<PillState> DismissOnClickOutside =
        [PillState.Home, PillState.NoSpeech];

    readonly AppState _state;
    readonly Dictionary<PillState, UserControl> _views = new();
    Interop.InputMonitor? _input;
    long _panelShownTick;
    IntPtr _hwnd;

    // Window bounds in physical pixels, for the (off-thread) click-outside hook.
    int _rectL, _rectT, _rectR, _rectB;

    public PillWindow(AppState state)
    {
        InitializeComponent();
        _state = state;
        _state.StateChanged += (o, n) => Dispatcher.Invoke(() => OnStateChanged(o, n));
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea) && IsVisible)
                Dispatcher.Invoke(() => { PositionBottomCenter(); CacheWindowRect(); });
        };
        LocationChanged += (_, _) => CacheWindowRect();
        SizeChanged += (_, _) => CacheWindowRect();
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
                App.Log($"dismiss: click outside pill (rect {_rectL},{_rectT},{_rectR},{_rectB})");
                _state.ForceForDemo(PillState.Hidden);
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
    /// Place the window bottom-centered using PHYSICAL pixels (Win32), which is
    /// robust to any resolution/DPI — WPF's logical Left/Top mis-scale at 125%+.
    /// </summary>
    void PositionBottomCenter()
    {
        if (_hwnd == IntPtr.Zero) return;
        UpdateLayout();
        if (!GetWindowRect(_hwnd, out var wr)) return;
        int winW = wr.Right - wr.Left, winH = wr.Bottom - wr.Top;
        var work = PhysicalWorkArea();
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        int gap = (int)Math.Round(BottomGap * dpi);
        int x = work.Left + ((work.Right - work.Left) - winW) / 2;
        int y = work.Bottom - winH - gap;
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    static RECT PhysicalWorkArea()
    {
        var r = new RECT();
        SystemParametersInfo(SpiGetWorkArea, 0, ref r, 0); // physical px for a per-monitor-aware app
        if (r.Right <= r.Left) { r.Right = 1920; r.Bottom = 1080; } // sane fallback
        return r;
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
        if (next == PillState.Hidden) { HideAnimated(); return; }

        var fromHidden = old == PillState.Hidden || !IsVisible;
        Host.Children.Clear();
        Host.Children.Add(ViewFor(next));
        if (DismissOnClickOutside.Contains(next)) _panelShownTick = Environment.TickCount64;

        var (w, h) = Sizes[next];
        var dpi = _hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleY : 1.0;
        var work = PhysicalWorkArea();
        // Never exceed the screen — tall panels clamp and scroll internally.
        var availH = (work.Bottom - work.Top) / dpi - BottomGap - TopMargin;
        Width = w;
        Height = Math.Min(h, availH);

        if (fromHidden) { Show(); ApplyGhost(); } // re-apply WDA — hide/show can drop it
        PositionBottomCenter();   // physical-pixel placement, DPI-proof
        MorphContent();
        CacheWindowRect();
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
        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(110)));
        fade.Completed += (_, _) => Hide();
        Host.BeginAnimation(OpacityProperty, fade);
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) _state.ForceForDemo(PillState.Hidden);
    }

    // ---- interop ----
    const int GwlExStyle = -20;
    const int WsExToolWindow = 0x00000080;
    const uint WdaExcludeFromCapture = 0x11;

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int newLong);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref RECT pv, uint winIni);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    const uint SpiGetWorkArea = 0x0030;
    const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    const int DwmwaUseImmersiveDarkMode = 20;
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
