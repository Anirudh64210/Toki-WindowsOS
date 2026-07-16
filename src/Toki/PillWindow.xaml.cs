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
        [PillState.RecordingBar]  = (236, 52),
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

    static readonly HashSet<PillState> PanelStates =
        [PillState.Home, PillState.Library, PillState.Settings, PillState.Notes, PillState.NoSpeech];

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
                Dispatcher.Invoke(() => { PositionBottomCenter(); ApplyRoundedRegion(); CacheWindowRect(); });
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
            if (!App.GhostDisabled)
                SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture); // reliable on non-layered windows
            SetWindowLong(_hwnd, GwlExStyle, GetWindowLong(_hwnd, GwlExStyle) | WsExToolWindow);
            ApplyRoundedRegion(); // DWM won't round a borderless window — clip it ourselves
        }
        catch (Exception ex) { App.Log($"window setup failed (non-fatal): {ex.Message}"); }

        _input = new Interop.InputMonitor(
            isArmed: () => PanelStates.Contains(_state.Current)
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
        if (PanelStates.Contains(next)) _panelShownTick = Environment.TickCount64;

        var (w, h) = Sizes[next];
        var dpi = _hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleY : 1.0;
        var work = PhysicalWorkArea();
        // Never exceed the screen — tall panels clamp and scroll internally.
        var availH = (work.Bottom - work.Top) / dpi - BottomGap - TopMargin;
        Width = w;
        Height = Math.Min(h, availH);

        if (fromHidden) Show();
        PositionBottomCenter();   // physical-pixel placement, DPI-proof
        ApplyRoundedRegion();
        MorphContent();
        CacheWindowRect();
    }

    /// <summary>Clip the rectangular window to a rounded-rect region (physical px).</summary>
    void ApplyRoundedRegion()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (!GetWindowRect(_hwnd, out var r)) return;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            var d = (int)Math.Round(CornerRadius * 2 * dpi); // ellipse diameter
            var rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d);
            SetWindowRgn(_hwnd, rgn, true); // OS takes ownership of the region
        }
        catch (Exception ex) { App.Log($"region failed (non-fatal): {ex.Message}"); }
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
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref RECT pv, uint winIni);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    const uint SpiGetWorkArea = 0x0030;
    const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
}
