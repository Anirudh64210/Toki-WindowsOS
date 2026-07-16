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
using Toki.Storage;
using Toki.Transcription;

namespace Toki;

public partial class App : Application
{
    public static AppState State { get; } = new();

    /// <summary>The app that triggered the current prompt / is being recorded (for UI + capture).</summary>
    public static DetectedApp? CurrentApp { get; private set; }

    /// <summary>Set on Stop: true when nothing audible was captured (→ friendly empty state).</summary>
    public static bool LastRecordingSilent { get; private set; }

    /// <summary>The merged transcript of the most recent recording (null until transcribed).</summary>
    public static IReadOnlyList<TranscriptSegment>? LastTranscript { get; private set; }

    /// <summary>Local database of meetings and notes.</summary>
    static readonly Lazy<MeetingStore> LazyStore = new(() => new MeetingStore());
    public static MeetingStore Store => LazyStore.Value;

    /// <summary>The meeting the Notes screen should display (saved after transcription).</summary>
    public static MeetingRecord? CurrentMeeting { get; set; }

    /// <summary>The manual note currently being edited.</summary>
    public static MeetingRecord? CurrentManualNote { get; private set; }

    /// <summary>Create a fresh manual note and open the editor.</summary>
    public static void NewManualNote()
    {
        try
        {
            var id = Store.CreateManualNote("Untitled note", "");
            CurrentManualNote = Store.Get(id);
            State.TryGo(PillState.ManualNote);
        }
        catch (Exception ex) { Log($"new manual note failed: {ex.Message}"); }
    }

    /// <summary>Open an existing manual note for editing.</summary>
    public static void OpenManualNote(MeetingRecord note)
    {
        CurrentManualNote = note;
        State.TryGo(PillState.ManualNote);
    }

    /// <summary>Dev only: <c>--noghost</c> disables screen-capture exclusion so tools can grab the window.</summary>
    public static bool GhostDisabled { get; private set; }

    static System.Threading.Mutex? _singleInstance;
    static System.Threading.EventWaitHandle? _showSignal;
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
        GhostDisabled = e.Args.Contains("--noghost");

        // Headless self-tests (brief §10): attach a console, run, exit. No UI.
        var testFlag = e.Args.FirstOrDefault(a => a is "--mictest" or "--loopbacktest" or "--playtone" or "--detecttest" or "--recordtest" or "--asrtest" or "--transcribetest" or "--dbtest" or "--notestest" or "--seed");
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

        // One Toki only. A second launch signals the running instance to show
        // itself (so double-clicking a running Toki brings it up) and then exits.
        _singleInstance = new System.Threading.Mutex(initiallyOwned: true, "Toki.SingleInstance", out var isFirst);
        _showSignal = new System.Threading.EventWaitHandle(
            false, System.Threading.EventResetMode.AutoReset, "Toki.ShowSignal");
        if (!isFirst)
        {
            Log("another instance already running — signalling it to show, exiting");
            _showSignal.Set();
            Shutdown();
            return;
        }
        // First instance: listen for future launches asking us to surface.
        var signalThread = new System.Threading.Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() => State.ForceForDemo(PillState.Home)); // always surface
        }) { IsBackground = true, Name = "Toki.ShowSignalWaiter" };
        signalThread.Start();

        _pill = new PillWindow(State);
        _hotkey = new HotkeyManager(_pill, ToggleHome);
        _tray = BuildTray();

        // Dev/demo: `Toki.exe --state home` opens directly in a state.
        var i = Array.IndexOf(e.Args, "--state");
        if (i >= 0 && i + 1 < e.Args.Length
            && Enum.TryParse<PillState>(e.Args[i + 1], ignoreCase: true, out var s)
            && s != PillState.Hidden)
        {
            // Dev convenience: give data-driven screens something to show.
            try
            {
                if (s == PillState.Notes && CurrentMeeting is null)
                {
                    var latest = Store.List().FirstOrDefault(x => x.Kind == MeetingKind.Meeting);
                    if (latest is not null) CurrentMeeting = Store.Get(latest.Id);
                }
                else if (s == PillState.ManualNote && CurrentManualNote is null)
                {
                    var note = Store.List().FirstOrDefault(x => x.Kind == MeetingKind.Manual);
                    CurrentManualNote = note is not null ? Store.Get(note.Id) : null;
                }
            }
            catch { /* no store yet */ }
            State.ForceForDemo(s);
        }
        else
        {
            // Double-click launch: greet the user so it's obviously running,
            // then normal rules apply (hotkey toggles, click-away/Esc dismiss).
            State.TryGo(PillState.Home);
        }

        StartMeetingDetection();

        // Warm the ASR engine ahead of the first meeting so a stop never waits on
        // model load (lesson 2). Off the UI thread; no-op if the model isn't installed.
        System.Threading.Tasks.Task.Run(() => TranscriptionService.Instance.Warm());
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
        var session = _session;
        _session = null;
        _detector?.EndRecording();
        LastRecordingSilent = Math.Max(session?.YouPeak ?? 0, session?.ThemPeak ?? 0) < 0.01f;
        LastTranscript = null;
        CurrentMeeting = null;

        // Wall-clock duration = stop − start (lesson 10), captured at stop time.
        var startedAt = State.RecordingStartedAt ?? DateTimeOffset.UtcNow;
        var durationMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        var app = CurrentApp?.DisplayName;

        // Show the loader immediately; finalize capture + transcribe + save entirely
        // off the UI thread (never freeze the pill), then advance when done.
        State.TryGo(PillState.Summarizing);
        System.Threading.Tasks.Task.Run(() =>
        {
            var folder = "";
            try { folder = session?.Stop() ?? ""; }
            catch (Exception ex) { Log($"stop recording (bg): {ex.Message}"); }

            IReadOnlyList<TranscriptSegment>? transcript = null;
            var minShow = System.Threading.Tasks.Task.Delay(2200); // let the loader be seen
            try { if (folder.Length > 0) transcript = TranscriptionService.Instance.Process(folder); }
            catch (Exception ex) { Log($"transcription failed (non-fatal): {ex.Message}"); }

            MeetingRecord? meeting = null;
            if (transcript is { Count: > 0 })
            {
                try
                {
                    var notes = NoteExtractor.Extract(transcript);
                    meeting = new MeetingRecord
                    {
                        Kind = MeetingKind.Meeting,
                        App = app,
                        StartedAt = startedAt.ToLocalTime(),
                        DurationMs = durationMs,
                        Folder = folder,
                        Transcript = transcript.ToList(),
                        Notes = notes,
                        Title = NoteExtractor.Title(notes, app, startedAt.ToLocalTime()),
                    };
                    Store.Save(meeting);
                    MarkdownMirror.Write(meeting);
                    Log($"saved meeting '{meeting.Title}' (id {meeting.Id})");
                }
                catch (Exception ex) { Log($"save meeting failed (non-fatal): {ex.Message}"); }
            }
            minShow.Wait();

            Current.Dispatcher.BeginInvoke(() =>
            {
                LastTranscript = transcript;
                CurrentMeeting = meeting;
                if (transcript is { Count: 0 }) State.TryGo(PillState.NoSpeech);
                else State.TryGo(PillState.Notes);
            });
        });
    }

    static void BeginRecording(DetectedApp? app)
    {
        try
        {
            var session = new RecordingSession(app);
            _session = session;
            // Start capture off the UI thread too — device init can take ~100ms.
            System.Threading.Tasks.Task.Run(() =>
            {
                try { session.Start(); } catch (Exception ex) { Log($"begin recording (bg): {ex.Message}"); }
            });
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

    static void RestartApp()
    {
        try
        {
            // Release single-instance ownership first so the fresh process becomes
            // the owner instead of just signalling this dying one.
            _singleInstance?.Dispose();
            _singleInstance = null;
            var exe = Environment.ProcessPath;
            if (exe is not null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex) { Log($"restart failed: {ex.Message}"); }
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _detector?.Stop();
        try { _session?.Stop(); } catch { /* best effort on shutdown */ }
        _hotkey?.Dispose();
        _tray?.Dispose();
        _showSignal?.Dispose();
        _singleInstance?.Dispose();
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
        menu.Items.Add(Item("Restart Toki", RestartApp));
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
