using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H.NotifyIcon;
using Toki.Audio;
using Toki.Detection;
using Toki.Interop;
using Toki.State;

namespace Toki;

public partial class App : Application
{
    public static AppState State { get; } = new();

    /// <summary>The app that triggered the current prompt / is being recorded (for UI + capture).</summary>
    public static DetectedApp? CurrentApp { get; private set; }

    /// <summary>Set on Stop: true when nothing audible was captured (→ friendly empty state).</summary>
    public static bool LastRecordingSilent { get; private set; }

    static System.Threading.Mutex? _singleInstance;
    static MeetingDetector? _detector;
    static RecordingSession? _session;

    PillWindow? _pill;
    TaskbarIcon? _tray;
    HotkeyManager? _hotkey;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Crash-proofing first: a meeting assistant must never die on the user.
        // Catch every unhandled-exception channel, log it, and keep running.
        InstallGlobalExceptionHandlers();

        Log($"startup args=[{string.Join(" ", e.Args)}]");

        // Headless self-tests (brief §10): attach a console, run, exit. No UI.
        var testFlag = e.Args.FirstOrDefault(a => a is "--mictest" or "--loopbacktest" or "--playtone" or "--detecttest" or "--recordtest");
        if (testFlag != null)
        {
            Dev.ConsoleHelper.Attach();
            var code = Dev.SelfTests.Run(testFlag, e.Args);
            Environment.Exit(code);
            return;
        }

        State.StateChanged += (o, n) => Log($"state {o} -> {n}");

        // Dev: render every view to PNGs and exit (verifies the visual tree
        // without the transparent-window screenshot problem).
        var r = Array.IndexOf(e.Args, "--render");
        if (r >= 0 && r + 1 < e.Args.Length)
        {
            Dev.RenderHarness.RenderAll(e.Args[r + 1]);
            Shutdown();
            return;
        }
        var ra = Array.IndexOf(e.Args, "--render-anim");
        if (ra >= 0 && ra + 1 < e.Args.Length)
        {
            Dev.RenderHarness.RenderAnim(e.Args[ra + 1]); // shuts down itself after 2nd frame
            return;
        }

        // One Toki only — a second launch would fight over the hotkey and stack
        // duplicate pills. If we're not first, just exit quietly.
        _singleInstance = new System.Threading.Mutex(initiallyOwned: true, "Toki.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Log("another instance already running — exiting");
            Shutdown();
            return;
        }

        _pill = new PillWindow(State);
        _hotkey = new HotkeyManager(_pill, ToggleHome);
        _tray = BuildTray();

        // Dev/demo: `Toki.exe --state home` opens directly in a state.
        var i = Array.IndexOf(e.Args, "--state");
        if (i >= 0 && i + 1 < e.Args.Length
            && Enum.TryParse<PillState>(e.Args[i + 1], ignoreCase: true, out var s)
            && s != PillState.Hidden)
        {
            State.ForceForDemo(s);
        }
        else
        {
            // Double-click launch: greet the user so it's obviously running,
            // then normal rules apply (hotkey toggles, click-away/Esc dismiss).
            State.TryGo(PillState.Home);
        }

        StartMeetingDetection();
    }

    void StartMeetingDetection()
    {
        _detector = new MeetingDetector
        {
            // Only interrupt when Toki is idle — never over notes or a recording.
            CanPrompt = () => State.Current is PillState.Hidden or PillState.Collapsed,
        };
        _detector.MeetingDetected += app => Dispatcher.Invoke(() =>
        {
            CurrentApp = app;
            State.TryGo(PillState.MeetingPrompt);
        });
        _detector.MeetingEnded += () => Dispatcher.Invoke(() =>
        {
            if (State.Current is PillState.RecordingBar or PillState.RecordingOpen)
                StopRecording();
        });
        _detector.Start();
    }

    // ---- Recording flow (called by the pill views) ----

    /// <summary>User accepted the "record this meeting?" prompt.</summary>
    public static void AcceptPrompt()
    {
        BeginRecording(CurrentApp);
        if (CurrentApp is not null) _detector?.BeginRecording(CurrentApp);
        State.TryGo(PillState.RecordingBar);
    }

    /// <summary>User dismissed the prompt — snooze that app for 10 minutes.</summary>
    public static void DismissPrompt()
    {
        if (CurrentApp is not null) _detector?.Snooze(CurrentApp);
        CurrentApp = null;
        State.TryGo(PillState.Hidden);
    }

    /// <summary>Manual mic-only "quick note" from Home.</summary>
    public static void StartQuickNote()
    {
        CurrentApp = null;
        BeginRecording(null);
        State.TryGo(PillState.RecordingBar);
    }

    public static void StopRecording()
    {
        var youPeak = _session?.YouPeak ?? 0;
        var themPeak = _session?.ThemPeak ?? 0;
        try { _session?.Stop(); } catch (Exception ex) { Log($"stop recording: {ex.Message}"); }
        _session = null;
        _detector?.EndRecording();
        LastRecordingSilent = Math.Max(youPeak, themPeak) < 0.01f;
        State.TryGo(PillState.Summarizing);
    }

    static void BeginRecording(DetectedApp? app)
    {
        try
        {
            _session = new RecordingSession(app);
            _session.Start();
        }
        catch (Exception ex) { Log($"begin recording failed (non-fatal): {ex.Message}"); }
    }

    void InstallGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            Log($"UI EXCEPTION (recovered): {ex.Exception}");
            ex.Handled = true; // swallow so the app keeps running
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log($"FATAL (AppDomain): {ex.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log($"TASK EXCEPTION (observed): {ex.Exception}");
            ex.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _detector?.Stop();
        try { _session?.Stop(); } catch { /* best effort on shutdown */ }
        _hotkey?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Diagnostic log at %TEMP%\toki-m1.log — "it didn't show" is only diagnosable if we can see what happened.</summary>
    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "toki-m1.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* logging must never take the app down */ }
    }

    static void ToggleHome()
    {
        // Ctrl+Alt+Space: open Home when hidden, close to Hidden when open.
        // Force-applied so the user can never get stuck.
        if (State.Current is PillState.Hidden or PillState.Collapsed) State.ForceForDemo(PillState.Home);
        else State.ForceForDemo(PillState.Hidden);
    }

    TaskbarIcon BuildTray()
    {
        static MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        var menu = new ContextMenu();
        menu.Items.Add(Item("Open Toki", () => State.ForceForDemo(PillState.Home)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Demo: meeting detected", () => State.ForceForDemo(PillState.MeetingPrompt)));
        menu.Items.Add(Item("Demo: summarizing", () => State.ForceForDemo(PillState.Summarizing)));
        menu.Items.Add(Item("Demo: notes ready", () => State.ForceForDemo(PillState.Notes)));
        menu.Items.Add(Item("Demo: nothing was said", () => State.ForceForDemo(PillState.NoSpeech)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Library", () => State.ForceForDemo(PillState.Library)));
        menu.Items.Add(Item("Settings", () => State.ForceForDemo(PillState.Settings)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit Toki", () =>
        {
            _tray?.Dispose();
            Shutdown();
        }));

        var tray = new TaskbarIcon
        {
            ToolTipText = "Toki — Ctrl+Alt+Space",
            ContextMenu = menu,
        };
        try { tray.IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/toki.ico")); }
        catch (Exception ex) { Log($"tray icon load failed (non-fatal): {ex.Message}"); }
        tray.TrayLeftMouseUp += (_, _) => ToggleHome();
        try { tray.ForceCreate(); }
        catch (Exception ex) { Log($"tray create failed (non-fatal): {ex.Message}"); }
        return tray;
    }
}
