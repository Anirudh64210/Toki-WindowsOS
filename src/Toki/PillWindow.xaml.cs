using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Toki.State;
using Toki.Views;

namespace Toki;

/// <summary>
/// The single overlay window. A transparent host stays fixed at bottom-center
/// (transparent pixels are click-through); only the inner Shell border morphs
/// its width/height between states - never a second overlay window.
/// In Collapsed, Toki peeks over the pill's top edge and rises on hover.
/// </summary>
public partial class PillWindow : Window
{
    // Logical-pixel sizes per state, from Toki-ProductDesign/.
    static readonly Dictionary<PillState, (double W, double H)> Sizes = new()
    {
        [PillState.Collapsed]     = (112, 30),
        [PillState.MeetingPrompt] = (300, 74),
        [PillState.RecordingBar]  = (236, 52),
        [PillState.RecordingOpen] = (372, 234),
        [PillState.Summarizing]   = (340, 164),
        [PillState.Notes]         = (424, 438),
        [PillState.NoSpeech]      = (320, 100),
        [PillState.Home]          = (344, 232),
        [PillState.Library]       = (404, 440),
        [PillState.Settings]      = (404, 440),
    };

    const double PeekRest = 12;   // Toki sitting low, just peeking
    const double PeekRisen = 0;   // Toki lifted out on hover

    // Panel states the user reads: clicking outside or pressing Esc dismisses
    // them. Recording/summarizing/prompt are deliberately excluded — they must
    // not vanish on a stray click.
    static readonly HashSet<PillState> PanelStates =
        [PillState.Home, PillState.Library, PillState.Settings, PillState.Notes, PillState.NoSpeech];

    readonly AppState _state;
    readonly Dictionary<PillState, UserControl> _views = new();
    readonly TranslateTransform _peekSlide = new(0, PeekRest);
    Interop.InputMonitor? _input;
    long _panelShownTick;   // for a brief grace period against instant dismissal

    public PillWindow(AppState state)
    {
        InitializeComponent();
        PeekBlob.RenderTransform = _peekSlide;
        _state = state;
        _state.StateChanged += (o, n) => Dispatcher.Invoke(() => OnStateChanged(o, n));
        PositionBottomCenter();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea))
                Dispatcher.Invoke(PositionBottomCenter);
        };
    }

    // ---- Ghost mode: visible to the user, invisible to screen capture/share ----

    const int GwlExStyle = -20;
    const int WsExToolWindow = 0x00000080;   // keep out of alt-tab / task switcher
    const uint WdaExcludeFromCapture = 0x11;  // Win10 2004+: DWM omits window from captures

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int newLong);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            // Ghost: Toki won't appear in screen recordings or shared screens,
            // so it never leaks onto a call it's quietly taking notes on.
            SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
            SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow);
        }
        catch (Exception ex) { App.Log($"ghost-mode setup failed (non-fatal): {ex.Message}"); }

        // Dismiss on a real click outside the pill, or Esc — via global hooks,
        // never focus events (which fire spuriously and flickered the pill shut).
        _input = new Interop.InputMonitor(
            isDismissable: () => PanelStates.Contains(_state.Current)
                                 && Environment.TickCount64 - _panelShownTick > 500,
            isInsidePanel: IsPointInsideShell,
            dismiss: () => Dispatcher.BeginInvoke(() => _state.ForceForDemo(PillState.Hidden)));
    }

    bool IsPointInsideShell(Point screenDevicePoint)
    {
        try
        {
            const double margin = 6;
            var tl = Shell.PointToScreen(new Point(0, 0));
            var br = Shell.PointToScreen(new Point(Shell.ActualWidth, Shell.ActualHeight));
            return screenDevicePoint.X >= tl.X - margin && screenDevicePoint.X <= br.X + margin
                && screenDevicePoint.Y >= tl.Y - margin && screenDevicePoint.Y <= br.Y + margin;
        }
        catch { return true; } // if unsure, don't dismiss
    }

    void PositionBottomCenter()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Bottom - Height;
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
            _ => throw new ArgumentOutOfRangeException(nameof(s)),
        };
        _views[s] = v;
        return v;
    }

    void OnStateChanged(PillState old, PillState next)
    {
        if (next == PillState.Hidden)
        {
            HideAnimated();
            return;
        }

        Host.Content = ViewFor(next);
        if (PanelStates.Contains(next)) _panelShownTick = Environment.TickCount64;

        // Templates use a fixed large radius; only the tiny ambient pill is a capsule.
        Shell.CornerRadius = new CornerRadius(next == PillState.Collapsed ? 18 : 26);

        var (w, h) = Sizes[next];
        var dur = TimeSpan.FromMilliseconds(280);
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        Shell.BeginAnimation(WidthProperty, new DoubleAnimation(w, dur) { EasingFunction = ease });
        Shell.BeginAnimation(HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = ease });

        Host.Opacity = 0;
        Host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            BeginTime = TimeSpan.FromMilliseconds(old == PillState.Hidden ? 60 : 120),
        });

        // Toki peeks only while ambient.
        PeekBlob.BeginAnimation(OpacityProperty, new DoubleAnimation(
            next == PillState.Collapsed ? 1 : 0,
            TimeSpan.FromMilliseconds(next == PillState.Collapsed ? 350 : 100))
        {
            BeginTime = TimeSpan.FromMilliseconds(next == PillState.Collapsed ? 250 : 0),
        });

        if (old == PillState.Hidden || !IsVisible)
            ShowAnimated(w, h);
    }

    void ShowAnimated(double w, double h)
    {
        // Land at the target size instantly when arriving from hidden - the
        // slide-in is the entrance; morphing is for state-to-state changes.
        Shell.BeginAnimation(WidthProperty, null);
        Shell.BeginAnimation(HeightProperty, null);
        Shell.Width = w;
        Shell.Height = h;

        Show();
        App.Log($"ShowAnimated: shown at {Left},{Top} size {w}x{h}, IsVisible={IsVisible}");
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            App.Log($"  post-layout Shell actual = {Shell.ActualWidth}x{Shell.ActualHeight}"));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ShellSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        Shell.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    void HideAnimated()
    {
        PeekBlob.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(100)));
        var slide = new DoubleAnimation(0, 24, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        slide.Completed += (_, _) => Hide();
        ShellSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
    }

    void OnShellMouseEnter(object sender, MouseEventArgs e)
    {
        if (_state.Current != PillState.Collapsed) return;
        // The signature moment: Toki rises out of the pill on hover.
        _peekSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(PeekRisen, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 },
            });
    }

    void OnShellMouseLeave(object sender, MouseEventArgs e)
    {
        if (_state.Current != PillState.Collapsed) return;
        _peekSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(PeekRest, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            _state.ForceForDemo(PillState.Hidden);
    }
}

