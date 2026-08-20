using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H.NotifyIcon;
using Toki.Audio;
using Toki.Detection;
using Toki.Interop;
using Toki.Llm;
using Toki.State;
using Toki.Storage;
using Toki.Transcription;

namespace Toki;

public partial class App : Application
{
    public static AppState State { get; } = new();

    /// <summary>Set by the pill: opens the Toki peek into the remembered page (click on the peek).</summary>
    public static Action? OpenPeek;

    /// <summary>Raised (on the UI thread) when a transcript segment finalizes DURING recording - for the
    /// live-transcript preview in the recording view.</summary>
    public static event Action<TranscriptSegment>? LivePreview;

    /// <summary>The app that triggered the current prompt / is being recorded (for UI + capture).</summary>
    public static DetectedApp? CurrentApp { get; private set; }

    /// <summary>Set on Stop: true when nothing audible was captured (→ friendly empty state).</summary>
    public static bool LastRecordingSilent { get; private set; }

    /// <summary>Raised when a finished summarize couldn't surface because the pill was forced
    /// elsewhere - the pill retargets its remembered context so the loader never re-appears dead.</summary>
    public static event Action<PillState>? ResultMissed;

    /// <summary>True on a memory-constrained PC (&lt;8GB RAM): keep only ONE heavy model resident
    /// at a time and never preload the LLM - both resident starves transcription and pages the PC.</summary>
    public static bool LowMemoryMode { get; private set; }

    /// <summary>True in the offscreen PNG render harness (--render). Views must not hit the
    /// network or persist settings in this mode - it runs against the user's real settings file.</summary>
    public static bool Headless { get; internal set; }

    /// <summary>The merged transcript of the most recent recording (null until transcribed).</summary>
    public static IReadOnlyList<TranscriptSegment>? LastTranscript { get; private set; }

    /// <summary>Local database of meetings and notes.</summary>
    static readonly Lazy<MeetingStore> LazyStore = new(() => new MeetingStore());
    public static MeetingStore Store => LazyStore.Value;

    /// <summary>The meeting the Notes screen should display (saved after transcription).</summary>
    public static MeetingRecord? CurrentMeeting { get; set; }

    /// <summary>The meeting note the user is currently LOOKING AT in the Library (null if none / a manual
    /// note / the window is closed). Powers screen-aware Ask. Must stay DERIVED from the Library window's
    /// actual state - a cached copy goes stale and grounds "this meeting" on the wrong meeting.</summary>
    public static MeetingRecord? CurrentViewedMeeting => LibraryWindow.VisibleMeeting;

    /// <summary>The manual note currently being edited.</summary>
    public static MeetingRecord? CurrentManualNote { get; private set; }

    /// <summary>Create a fresh manual note and open the editor.</summary>
    public static void NewManualNote()
    {
        try
        {
            var id = Store.CreateManualNote("Untitled note", "");
            CurrentManualNote = Store.Get(id);
            LibraryWindow.MarkDirty();
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

    /// <summary>Discard a meeting entirely - remove it from the Library (DB), its Markdown mirror, and
    /// its recording folder. Used by the "Delete" choice on the Notes screen.</summary>
    public static void DiscardMeeting(MeetingRecord? m)
    {
        if (m is null) return;
        try
        {
            // Tombstone the audio folder BEFORE the row disappears: if the folder delete fails or
            // the process dies, the recovery sweep would otherwise revive the deleted meeting.
            try
            {
                if (!string.IsNullOrWhiteSpace(m.Folder) && Directory.Exists(m.Folder))
                    File.WriteAllText(Path.Combine(m.Folder!, RecordingRecovery.DiscardMarker), "");
            }
            catch (Exception ex) { Log($"discard: tombstone: {ex.Message}"); }

            Store.Delete(m.Id);
            try
            {
                if (Directory.Exists(MarkdownMirror.NotesDir))
                    foreach (var f in Directory.GetFiles(MarkdownMirror.NotesDir, $"*-{m.Id}.md")) File.Delete(f);
            }
            catch (Exception ex) { Log($"discard: markdown cleanup: {ex.Message}"); }
            try { if (!string.IsNullOrWhiteSpace(m.Folder) && Directory.Exists(m.Folder)) Directory.Delete(m.Folder, true); }
            catch (Exception ex) { Log($"discard: folder cleanup: {ex.Message}"); }
            LibraryWindow.MarkDirty();
            if (CurrentMeeting?.Id == m.Id) CurrentMeeting = null;
            Log($"discarded meeting {m.Id}");
        }
        catch (Exception ex) { Log($"discard meeting failed: {ex.Message}"); }
    }

    /// <summary>When true the notch is NOT hidden from screen capture (ghost mode off). Set by the
    /// <c>--noghost</c> dev flag OR the persisted "Show Toki in screen recordings" setting, and
    /// flipped live by the Settings toggle via <see cref="SetShowInRecordings"/>.</summary>
    public static bool GhostDisabled { get; private set; }

    /// <summary>Flip screen-capture visibility live from the Settings toggle. On = show Toki in
    /// recordings/screen-share (ghost off); off = hidden. Re-applies the affinity on the live window
    /// so it takes effect immediately, no restart.</summary>
    public static void SetShowInRecordings(bool show)
    {
        GhostDisabled = show;   // showing in recordings == capture-exclusion OFF
        (Current as App)?._pill?.RefreshGhost();
    }

    /// <summary>Toggle the glassy (more see-through) notch surface, persisted + applied live.</summary>
    public static void SetGlassyNotch(bool on)
    {
        var s = AppSettings.Current; s.GlassyNotch = on; s.Save();
        (Current as App)?._pill?.RefreshSurface();
    }

    /// <summary>The pill's current on-screen rect in DIPs (for the Library morph to grow from the
    /// notch), or null if the pill isn't up. Read it BEFORE hiding the pill.</summary>
    public static System.Windows.Rect? PillRect() => (Current as App)?._pill?.ScreenDipRect();

    /// <summary>Re-write a saved meeting's notes in a SPECIFIC template (the manual override). Returns
    /// false when there's no transcript or no model available. Heavy - runs the LLM on the transcript.</summary>
    public static async Task<bool> RewriteNotesAsync(long meetingId, string templateName, System.Threading.CancellationToken ct = default)
    {
        var m = Store.Get(meetingId);
        if (m is null || m.Transcript.Count == 0) return false;
        var tpl = Llm.NoteTemplates.ByName(templateName);
        // Off the UI thread: WriteNotesAsync starts by loading the local model synchronously, which
        // would otherwise freeze the window the picker lives in.
        var notes = await System.Threading.Tasks.Task.Run(() => Llm.LlmService.Instance.WriteNotesAsync(m.Transcript, ct, tpl));
        if (notes is null) return false;
        // WriteAsync degrades to the heuristic floor rather than failing - right for a first note,
        // wrong here: never replace a good saved note with the heuristic fallback. Refuse instead.
        if (!notes.FromModel) { Log($"rewrite as {tpl.Name}: model unavailable, keeping the existing note"); return false; }
        notes.Template = tpl.Name;

        // A rewrite must not un-tick what the user ticked: carry Done across by task identity.
        var done = m.Notes.Actions.Where(a => a.Done).Select(a => TaskKey(a.Text)).ToHashSet();
        foreach (var a in notes.Actions)
            if (done.Contains(TaskKey(a.Text))) a.Done = true;

        // Rebuild the carried-forward section so switching template does not drop "Still open from last time".
        m.Notes = notes;
        if (Llm.OpenFromLastTime.Build(m, Store) is { } carried)
            notes.Sections = notes.Sections.Prepend(carried).ToList();

        Store.ReplaceNotes(meetingId, notes);
        Storage.MarkdownMirror.Write(m);    // keep the .md mirror telling the same story as the DB
        LibraryWindow.MarkDirty();
        return true;
    }

    /// <summary>Task identity for carrying tick-state across a notes rewrite: letters and digits
    /// only, case-folded - the same loose identity OpenFromLastTime uses.</summary>
    static string TaskKey(string? text) =>
        new((text ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    static Interop.SingleInstance? _singleInstance;
    static MeetingDetector? _detector;
    static RecordingSession? _session;

    PillWindow? _pill;
    TaskbarIcon? _tray;
    Interop.PushToTalkHook? _ptt;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Crash-proofing first: a meeting assistant must never die on the user.
        // Catch every unhandled-exception channel, log it, and keep running.
        InstallGlobalExceptionHandlers();

        Log($"startup args=[{string.Join(" ", e.Args)}]");
        GhostDisabled = e.Args.Contains("--noghost") || AppSettings.Current.ShowInRecordings;

        // Apply the saved light/dark theme before any window is built (a `--theme dark|light` arg
        // overrides it, so --render can capture both palettes).
        var ti = Array.IndexOf(e.Args, "--theme");
        var theme = ti >= 0 && ti + 1 < e.Args.Length
            ? Theme.ThemeManager.Parse(e.Args[ti + 1])
            : Theme.ThemeManager.Parse(AppSettings.Current.Theme);
        Theme.ThemeManager.Apply(theme);

        // The two heavy models (2.5GB LLM + 630MB recognizer) don't both fit on a small machine;
        // keeping both resident crashes transcription and pages the PC.
        var (totalGB, availGB) = Interop.SystemMemory.Physical();
        LowMemoryMode = totalGB > 0 && totalGB < 8.0;
        Log($"system RAM {totalGB:F1}GB (avail {availGB:F1}GB) → {(LowMemoryMode ? "LOW-MEMORY mode: one model at a time, no LLM prewarm" : "normal mode")}");

        // Resolve GPU layers + context BEFORE any LLamaSharp call. Load the Vulkan backend ONLY
        // when offloading - enabling it at 0 layers is the slowest possible path (see LlmRuntime).
        Llm.LlmRuntime.Resolve();
        Log($"llm runtime: {Llm.LlmRuntime.Reason}");
        try
        {
            var cfg = LLama.Native.NativeLibraryConfig.All;
            if (Llm.LlmRuntime.UseVulkan) cfg.WithVulkan(true).WithAutoFallback(true);
            else cfg.WithVulkan(false);
        }
        catch (Exception ex) { Log($"native lib config (non-fatal): {ex.Message}"); }

        // Headless self-tests: attach a console, run, exit. This list and the switch in SelfTests.Run
        // are TWO registrations of the same flag set - add every new flag in BOTH places, or the app
        // exits 0 with no output, which looks exactly like a passing test.
        var testFlag = e.Args.FirstOrDefault(a => a is "--mictest" or "--loopbacktest" or "--playtone" or "--detecttest" or "--recordtest" or "--asrtest" or "--wertest" or "--voicetest" or "--transcribetest" or "--dbtest" or "--notestest" or "--renotes" or "--livetest" or "--seed" or "--llmtest" or "--themetest" or "--copytest" or "--asktest" or "--querytest" or "--followuptest" or "--embedtest" or "--caltest" or "--preptest" or "--voicecreatetest" or "--measuretest" or "--notetest" or "--dumpnote" or "--uitest" or "--diartest" or "--speakertest" or "--hittest" or "--revealtest" or "--wavetest" or "--fpstest" or "--stickytest" or "--hovershot" or "--makeicon");
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
            Headless = true;                              // views must not touch the network or save settings
            Dev.RenderHarness.RenderAll(e.Args[r + 1]);
            Shutdown();
            return;
        }
        var ls = Array.IndexOf(e.Args, "--libshot");
        if (ls >= 0 && ls + 1 < e.Args.Length)
        {
            Headless = true;
            LibraryWindow.RenderShots(e.Args[ls + 1]);
            Shutdown();
            return;
        }
        var ra = Array.IndexOf(e.Args, "--render-anim");
        if (ra >= 0 && ra + 1 < e.Args.Length)
        {
            Headless = true;
            Dev.RenderHarness.RenderAnim(e.Args[ra + 1]); // shuts down itself after 2nd frame
            return;
        }

        // One Toki only. A second launch signals the running instance to show itself (so
        // double-clicking a running Toki brings it up) and then exits. Names are per-user and the
        // objects are ACL'd to this user - a local squatter can neither DoS startup nor drive the UI.
        _singleInstance = Interop.SingleInstance.Acquire();
        if (!_singleInstance.IsPrimary)
        {
            Log("another instance already running - signalled it to show, exiting");
            Shutdown();
            return;
        }
        _singleInstance.ListenForShow(() => Dispatcher.BeginInvoke(() => State.ForceForDemo(PillState.Home)));

        _pill = new PillWindow(State);
        // Ctrl+Alt+Space is PUSH-TO-TALK (hold to speak, release to answer) - see PushToTalkHook.
        _ptt = new Interop.PushToTalkHook(
            onStart: () => Dispatcher.BeginInvoke(new Action(PttStart)),
            onStop: () => Dispatcher.BeginInvoke(new Action(PttStop)));
        _tray = BuildTray();

        // Dev/demo: `--state home` opens directly in a state; `--state summarizing,notes` walks a PATH,
        // one hop every 3s - some behaviour only lives in a transition and needs a way to drive one.
        var i = Array.IndexOf(e.Args, "--state");
        var hops = i >= 0 && i + 1 < e.Args.Length
            ? e.Args[i + 1].Split(',')
                .Select(part => Enum.TryParse<PillState>(part, ignoreCase: true, out var st) ? st : PillState.Hidden)
                .Where(st => st != PillState.Hidden).ToArray()
            : Array.Empty<PillState>();
        if (hops.Length > 0)
        {
            // Dev convenience: give data-driven screens something to show.
            foreach (var s in hops)
            {
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
                    else if (s == PillState.PrepCard && CurrentPrep is null)
                    {
                        CurrentPrep = Dev.PrepSamples.Matched();   // demo a seeded prep card
                    }
                }
                catch { /* no store yet */ }
            }
            State.ForceForDemo(hops[0]);
            if (hops.Length > 1)
            {
                var walk = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                var at = 1;
                walk.Tick += (_, _) =>
                {
                    State.ForceForDemo(hops[at]);
                    if (++at >= hops.Length) walk.Stop();
                };
                walk.Start();
            }
        }
        else if (!AppSettings.Current.OnboardingComplete)
        {
            // First run: greet + capture the name once, land in Home. Gate on the flag, not on
            // whether a name exists, so everyone sees onboarding exactly once.
            State.ForceForDemo(PillState.Onboarding);
        }
        else
        {
            // Normal launch: Toki peeks from the top edge; click it to open Home.
            State.TryGo(PillState.Collapsed);
        }

        // Dev: open the Library window directly for inspection.
        if (e.Args.Contains("--library"))
            Dispatcher.BeginInvoke(() => LibraryWindow.ShowLibrary());
        if (e.Args.Contains("--newnote"))
            Dispatcher.BeginInvoke(() => LibraryWindow.ShowNewNote());

        // Pre-warm the Library window off-screen so its first open is instant.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { LibraryWindow.Warm(); } catch (Exception ex) { Log($"library warm (non-fatal): {ex.Message}"); }
        }), System.Windows.Threading.DispatcherPriority.Background);

        // Build the first peek/panel views and the mascot on an idle frame so the first expand
        // animates from its true beginning (see PillWindow.WarmFirstOpen). ApplicationIdle = after
        // startup layout and the first summon, never competing with them.
        Dispatcher.BeginInvoke(new Action(() => _pill?.WarmFirstOpen()),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        StartMeetingDetection();

        // Watch the calendar and surface a prep card ~5 min before a meeting (never headless - it reads
        // the network via UpcomingCache).
        if (!Headless) Calendar.PrepWatcher.Instance.Start();

        // Warm both engines ahead of the first meeting so a stop never waits on model load.
        WarmEnginesGently();
    }

    /// <summary>Load the ASR + LLM models ahead of the first meeting on one below-normal background
    /// thread, one model at a time - parallel normal-priority loads make the whole desktop stutter.</summary>
    static void WarmEnginesGently()
    {
        var t = new System.Threading.Thread(() =>
        {
            try { System.Threading.Thread.Sleep(3000); } catch { } // let the app finish appearing

            // The speech gate's model: fetched at launch, never mid-meeting, and only when the
            // recogniser it gates is installed. Failure is silent: the transcriber decodes ungated.
            if (TranscriptionService.ModelReady && !ModelManager.VadInstalled)
            {
                try
                {
                    ModelManager.EnsureVadAsync().GetAwaiter().GetResult();
                    Log("speech gate: silero_vad.onnx downloaded");
                }
                catch (Exception ex) { Log($"speech gate download (non-fatal, will decode ungated): {ex.Message}"); }
            }

            try { TranscriptionService.Instance.Warm(); }
            catch (Exception ex) { Log($"ASR warm (non-fatal): {ex.Message}"); }
            // Housekeeping: recover any recording a crash/quit orphaned, and clear stale
            // push-to-talk voice snippets left in %TEMP%.
            try { RecordingRecovery.Sweep(); } catch (Exception ex) { Log($"recovery sweep (non-fatal): {ex.Message}"); }
            // Carry any prep-card notes from the old JSON file into the sticky-note tables (once).
            try { StickyMigration.RunOnce(Store); } catch (Exception ex) { Log($"sticky migration (non-fatal): {ex.Message}"); }
            try
            {
                var voiceDir = Path.Combine(Path.GetTempPath(), "Toki", "voice");
                if (Directory.Exists(voiceDir))
                    foreach (var f in Directory.GetFiles(voiceDir, "*.wav")
                                 .Where(f => DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromMinutes(10)))
                        File.Delete(f);
            }
            catch (Exception ex) { Log($"voice temp cleanup (non-fatal): {ex.Message}"); }
            // On a small-RAM PC never preload the 2.5GB LLM - it starves transcription.
            // It loads on demand when a meeting actually ends.
            if (LowMemoryMode) { Log("low-memory: skipping LLM prewarm (loads on demand)"); return; }
            try { System.Threading.Thread.Sleep(750); } catch { }  // don't stack the two loads
            try { LlmService.Instance.WarmBlocking(); }
            catch (Exception ex) { Log($"LLM warm (non-fatal): {ex.Message}"); }
        })
        {
            IsBackground = true,
            Priority = System.Threading.ThreadPriority.BelowNormal,
            Name = "toki-warm",
        };
        t.Start();
    }

    void StartMeetingDetection()
    {
        _detector = new MeetingDetector
        {
            // Only interrupt when Toki is idle, and never over a fullscreen presentation/game
            // (the prompt is DELAYED, not dropped: the detector re-offers on the next poll).
            CanPrompt = () => State.Current is PillState.Hidden or PillState.Collapsed
                              && !PillWindow.QuietFullscreen(),
        };
        _detector.MeetingDetected += app => Dispatcher.Invoke(() =>
        {
            CurrentApp = app;
            State.TryGo(PillState.MeetingPrompt);
        });
        _detector.MeetingEnded += () => Dispatcher.Invoke(() =>
        {
            // Key off the SESSION, not the pill state: the auto-stop is one-shot, and a state
            // check can drop it - leaving the mic recording invisibly forever.
            if (_session is not null) StopRecording();
        });
        _detector.Start();
    }

    // ---- Recording flow (called by the pill views) ----

    /// <summary>User accepted the "record this meeting?" prompt.</summary>
    public static void AcceptPrompt()
    {
        BeginRecording(CurrentApp);
        // Auto-stop when the meeting app releases the mic - but only for a prompt we
        // detected. A manual start is stopped by the user, so it isn't armed here
        // (some apps drop the mic when you mute, which must NOT end a manual recording).
        if (CurrentApp is not null) _detector?.BeginRecording(CurrentApp);
        State.TryGo(PillState.RecordingBar);
    }

    /// <summary>User dismissed the prompt - snooze that app for 10 minutes.</summary>
    public static void DismissPrompt()
    {
        if (CurrentApp is not null) _detector?.Snooze(CurrentApp);
        CurrentApp = null;
        State.TryGo(PillState.Hidden);
    }

    /// <summary>A scheduled meeting with a Meet link happening RIGHT NOW (start ≤ now &lt; end), or null.
    /// Read from the cache only - never a fresh network hit on the UI thread.</summary>
    public static Toki.Calendar.CalEvent? LiveCalendarMeeting()
    {
        try
        {
            var snap = Toki.Calendar.UpcomingCache.Instance.Snapshot;
            if (snap is null) return null;
            var now = DateTimeOffset.UtcNow;
            return snap.FirstOrDefault(e => !e.IsAllDay && e.HasMeetLink && e.StartUtc <= now && e.EndUtc > now);
        }
        catch { return null; }
    }

    /// <summary>"Join call" on the prompt: open the live meeting's Meet link AND start recording.
    /// Falls back to a plain record if the link vanished since the prompt was shown.</summary>
    public static void JoinFromPrompt()
    {
        if (LiveCalendarMeeting()?.MeetLink is { Length: > 0 } link) Views.CalendarRows.OpenUrl(link);
        AcceptPrompt();   // arm auto-stop + start recording the detected call
    }

    /// <summary>Manual mic-only "quick note" from Home.</summary>
    public static void StartQuickNote()
    {
        if (IsRecording) { State.ForceForDemo(PillState.RecordingBar); return; }
        CurrentApp = null;
        BeginRecording(null);
        State.TryGo(PillState.RecordingBar);
    }

    // ---- Pre-meeting prep card ----

    /// <summary>The content of the prep card currently being shown (set by PrepWatcher / demo before the
    /// PrepCard state is entered). The analog of <see cref="CurrentApp"/> for MeetingPrompt.</summary>
    public static Calendar.PrepContent? CurrentPrep { get; set; }

    /// <summary>"Join and record": open the meeting link (if any) and start recording - the human-in-the-
    /// loop moment (Hard Constraint #3). Same manual-start path as <see cref="StartQuickNote"/>.</summary>
    public static void JoinAndRecord()
    {
        // KEEP the prep card armed through the meeting, so the recording page can still open it.
        var prep = CurrentPrep;
        if (prep?.Event.HasMeetLink == true) Views.CalendarRows.OpenUrl(prep.Event.MeetLink);
        if (IsRecording) { State.ForceForDemo(PillState.RecordingBar); return; }
        CurrentApp = null;
        BeginRecording(null);
        State.TryGo(PillState.RecordingBar);
    }

    /// <summary>"Later": dismiss the prep card and suppress it for this event this session.</summary>
    public static void DismissPrepCard()
    {
        if (CurrentPrep is { } p) Calendar.PrepWatcher.Instance.Snooze(p.Event.Id);
        CurrentPrep = null;
        State.TryGo(PillState.Hidden);
    }

    /// <summary>True while a recording is running. Derived from the SESSION, not the pill state -
    /// the pill can be forced elsewhere while the mic is still live.</summary>
    public static bool IsRecording => _session is not null;

    /// <summary>Close the notch from any generic dismiss path. A live recording never vanishes:
    /// with a session running the pill returns to the recording bar instead of hiding - an
    /// invisibly-running mic is the one thing this product must never do.</summary>
    public static void CloseOverlay()
    {
        var target = IsRecording ? PillState.RecordingBar : PillState.Hidden;
        if (State.Current != target) State.ForceForDemo(target);
    }

    /// <summary>"Start taking notes" as an assistant command - the user explicitly asked, so this IS
    /// the human in the loop (constraint #3). Forced state (not TryGo): the command can arrive from
    /// states with no direct edge to RecordingBar.</summary>
    public static void StartRecordingFromCommand()
    {
        if (IsRecording) return;
        CurrentApp = null;
        BeginRecording(null);
        State.ForceForDemo(PillState.RecordingBar);
    }

    /// <summary>Switch the notes/answers engine from an assistant command. Returns the reply to show.
    /// Persists through the normal settings path; refuses an API switch when no key is set (an honest
    /// "add the key first" beats silently degrading to heuristic notes).</summary>
    public static string SwitchNotesMode(Llm.NotesMode mode)
    {
        var s = Llm.AppSettings.Current;
        if (mode == Llm.NotesMode.ApiKey && string.IsNullOrWhiteSpace(s.ApiKey))
            return "There's no API key saved yet - add one in Settings, then I can switch.";
        if (s.NotesMode == mode)
            return mode switch
            {
                Llm.NotesMode.ApiKey => "Already using your API model.",
                Llm.NotesMode.HeuristicOnly => "Already on the fast built-in notes (no AI model).",
                _ => "Already on the on-device model.",
            };
        s.NotesMode = mode;
        s.Save();
        Log($"notes mode switched by command → {mode}");
        return mode switch
        {
            Llm.NotesMode.ApiKey => "Switched to your API model.",
            Llm.NotesMode.HeuristicOnly => "Switched to the fast built-in notes - no AI model.",
            _ => "Switched to the on-device model.",
        };
    }

    /// <summary>Switch the theme from an assistant command (applies live + persists). Returns the reply.</summary>
    public static string SwitchTheme(bool light)
    {
        var target = light ? Theme.AppTheme.Light : Theme.AppTheme.Dark;
        if (Theme.ThemeManager.Current == target)
            return light ? "You're already in the light theme." : "You're already in the dark theme.";
        var s = Llm.AppSettings.Current;
        s.Theme = light ? "light" : "dark";
        s.Save();
        Theme.ThemeManager.Apply(target);
        return light ? "Light theme on." : "Dark theme on.";
    }

    /// <summary>Ghost mode from an assistant command: on = hidden from screen capture (the default),
    /// off = visible in recordings. Applies live + persists. Returns the reply.</summary>
    public static string SetGhostMode(bool on)
    {
        var s = Llm.AppSettings.Current;
        var wanted = !on;   // ghost ON == ShowInRecordings OFF
        if (s.ShowInRecordings == wanted && GhostDisabled == wanted)
            return on ? "Ghost mode is already on - Toki stays out of screen captures." : "Ghost mode is already off - Toki shows in recordings.";
        s.ShowInRecordings = wanted;
        s.Save();
        SetShowInRecordings(wanted);
        return on ? "Ghost mode on - Toki is hidden from screen recordings and shares."
                  : "Ghost mode off - Toki will show up in recordings.";
    }

    /// <summary>Finish first-run onboarding: persist the chosen name (empty = keep the Windows-account
    /// fallback) and the completed flag so it never runs again, then land in Home - where the greeting
    /// immediately shows the name they just gave.</summary>
    public static void CompleteOnboarding(string name)
    {
        var s = Llm.AppSettings.Current;
        if (!string.IsNullOrWhiteSpace(name)) s.UserName = name.Trim();
        s.OnboardingComplete = true;
        s.Save();
        State.TryGo(PillState.Home);
    }

    /// <summary>Meetings whose speakers are still being resolved. Resolution runs AFTER the notes are
    /// written (nothing may delay the notes). Nothing about anyone's voice is stored: clusters live and
    /// die inside <see cref="SpeakerResolver"/>; only renameable text lands in the database.</summary>
    static readonly HashSet<long> Resolving = new();

    /// <summary>Is this meeting still having its speakers worked out? The note shows a loader for
    /// the parts about to change rather than letting them rewrite themselves under the reader.</summary>
    public static bool IsResolvingSpeakers(long meetingId)
    {
        lock (Resolving) return Resolving.Contains(meetingId);
    }

    /// <summary>Raised (on the UI thread) when a meeting's speakers are settled and its note should be
    /// re-read from the store.</summary>
    public static event Action<long>? SpeakersResolved;

    static void ResolveSpeakers(MeetingRecord meeting, List<string>? attendeeNames)
    {
        if (meeting.Transcript.Count == 0 || string.IsNullOrWhiteSpace(meeting.Folder)) return;
        if (!SpeakerDiariser.Available) return;

        lock (Resolving) Resolving.Add(meeting.Id);
        try
        {
            var themWav = Path.Combine(meeting.Folder, "them.wav");
            // The roster is everything a HUMAN asserted (invite attendees, pinned names, the user's
            // own). A name from here is trusted; a recogniser-only name must earn its place by repetition.
            var roster = NameLexicon.Trusted(attendeeNames);

            // The attendee count is the most useful prior: no clustering threshold serves both a
            // one-to-one and an eleven-person call.
            var expected = attendeeNames is { Count: > 1 } ? attendeeNames.Count - 1 : -1;

            var result = SpeakerResolver.Resolve(
                meeting.Transcript, themWav, roster, AppSettings.Current.UserName, expected);

            if (!result.Ran) { Log($"speakers not resolved for meeting {meeting.Id}: {result.Skipped}"); return; }

            // The meeting may have been deleted while this ran - never resurrect a discarded row.
            if (Store.Get(meeting.Id) is null) { Log($"meeting {meeting.Id} gone - dropping speaker tags"); return; }

            Store.SetSpeakerTags(meeting.Id, result.Tags);
            for (var i = 0; i < meeting.Transcript.Count && i < result.Tags.Count; i++)
                meeting.Transcript[i] = meeting.Transcript[i] with { Tag = result.Tags[i] };

            // With each line attributed, to-do owners come for free via the existing evidence links -
            // no second model pass.
            var owners = ActionOwners.Infer(meeting.Notes.Actions, meeting.Transcript);
            if (owners.Count > 0)
            {
                Store.SetActionOwners(meeting.Id, owners);
                foreach (var (index, owner) in owners)
                    if (index < meeting.Notes.Actions.Count) meeting.Notes.Actions[index].Owner = owner;
                Log($"attributed {owners.Count} to-do(s) from the transcript");
            }

            MarkdownMirror.Write(meeting);
            LibraryWindow.MarkDirty();

            var named = string.Join(", ", result.Named.Select(n => n.Name));
            Log($"speakers resolved for meeting {meeting.Id}: {result.Speakers} voices in {result.Took.TotalSeconds:F1}s"
                + (named.Length > 0 ? $" - named {named}" : " - none named"));
        }
        catch (Exception ex) { Log($"speaker resolution failed (non-fatal): {ex.Message}"); }
        finally
        {
            lock (Resolving) Resolving.Remove(meeting.Id);
            // Always raised, even on failure, so a loader can never be left spinning over work that stopped.
            try { Current?.Dispatcher.BeginInvoke(() => SpeakersResolved?.Invoke(meeting.Id)); } catch { }
        }
    }

    public static void StopRecording()
    {
        var session = _session;
        _session = null;
        _detector?.EndRecording();

        // The sticky note open during this meeting, if any. Captured HERE, before anything can
        // clear CurrentPrep; attached to the saved meeting further down.
        var stickyId = CurrentPrep is { } endedPrep ? Store.StickyForEvent(endedPrep.Event.Id) : null;

        // Attendee names are human-typed, so they outrank corpus counts when repairing a mis-heard
        // name. Grabbed before anything can clear CurrentPrep.
        var attendeeNames = CurrentPrep?.Event.Attendees
            .Where(a => !string.IsNullOrWhiteSpace(a.DisplayName))
            .Select(a => a.DisplayName!)
            .ToList();
        LastRecordingSilent = Math.Max(session?.YouPeak ?? 0, session?.ThemPeak ?? 0) < 0.01f;
        LastTranscript = null;
        CurrentMeeting = null;

        // Duration is wall-clock (stop - start), never UI ticks. Clear the clock AFTER reading it,
        // so the next recording starts fresh (see AppState.Apply).
        var startedAt = State.RecordingStartedAt ?? DateTimeOffset.UtcNow;
        var durationMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        State.RecordingEnded();
        // The FRIENDLY app name ("Slack", not "Com.tinyspeck.slackdesktop") - user-facing in the
        // saved meeting label and the fallback title.
        var rawApp = session?.Target?.DisplayName ?? CurrentApp?.DisplayName;
        var app = string.IsNullOrWhiteSpace(rawApp) ? rawApp : Detection.AppNames.Friendly(rawApp);

        // Show the loader immediately; finalize capture + transcribe + save entirely
        // off the UI thread (never freeze the pill), then advance when done.
        State.TryGo(PillState.Summarizing);
        System.Threading.Tasks.Task.Run(() =>
        {
            var folder = "";
            IReadOnlyList<TranscriptSegment>? live = null;
            try { folder = session?.Stop() ?? ""; live = session?.LiveTranscript; }
            catch (Exception ex) { Log($"stop recording (bg): {ex.Message}"); }

            IReadOnlyList<TranscriptSegment>? transcript = null;
            IReadOnlyList<Storage.TextFix> corrections = [];
            var minShow = System.Threading.Tasks.Task.Delay(1200); // let the loader be seen
            try
            {
                // Prefer the live transcript (the fast path). Fall back to the whole-file pass when
                // live produced nothing OR lost windows to decode errors - an unflagged multi-minute
                // hole is worse than making the user wait.
                var lostSec = session?.LiveLostSeconds ?? 0;
                if (live is { Count: > 0 } && lostSec > 8 && folder.Length > 0)
                {
                    Log($"live transcript lost {lostSec:F0}s to decode errors - running the whole-file pass instead");
                    live = null;
                }
                if (live is { Count: > 0 })
                {
                    // The live path merged turns inside LiveTranscriber.Stop but never went through
                    // Process, so it still needs its names repaired.
                    transcript = TranscriptionService.Instance.PolishNames(live, attendeeNames);
                    corrections = TranscriptionService.Instance.LastCorrections;
                    Log($"using live transcript ({live.Count} segments) - no stop-time ASR");
                }
                else if (folder.Length > 0)
                {
                    transcript = TranscriptionService.Instance.Process(folder, attendeeNames);
                    corrections = TranscriptionService.Instance.LastCorrections;
                }
            }
            catch (Exception ex) { Log($"transcription failed (non-fatal): {ex.Message}"); }

            // An accidental tap must not become a hollow Library entry with a bogus summary:
            // drop anything with too few words OR a very short duration.
            var words = transcript?.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length) ?? 0;
            var tooTrivial = transcript is { Count: > 0 } && (words < 12 || durationMs < 4000);
            if (tooTrivial) Log($"discarding trivial recording ({durationMs}ms, {words} words) - accidental tap");

            // A discarded recording must not leave orphaned audio behind - but only delete when the
            // transcript RAN and said "nothing here". A null transcript means the ASR was missing or
            // failed; then the audio is the only artifact and must be kept for the recovery sweep.
            if (transcript is not null && (transcript.Count == 0 || tooTrivial) && folder.Length > 0)
            {
                try { Directory.Delete(folder, true); Log($"deleted empty/trivial recording folder {folder}"); }
                catch (Exception ex) { Log($"folder cleanup: {ex.Message}"); }
                folder = "";
            }

            MeetingRecord? meeting = null;
            if (transcript is { Count: > 0 } && !tooTrivial)
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
                        Corrections = corrections.ToList(),
                        Notes = notes,
                        Title = NoteExtractor.Title(notes, app, startedAt.ToLocalTime()),
                    };
                    Store.Save(meeting);
                    MarkdownMirror.Write(meeting);
                    // Attach the sticky note that was open during it - only when something was
                    // actually written (an empty note is a button that opens nothing).
                    if (stickyId is { } sid && Store.GetSticky(sid) is { Body.Length: > 0 })
                    {
                        Store.LinkStickyToMeeting(meeting.Id, sid);
                        Log($"sticky note {sid} attached to meeting {meeting.Id}");
                    }
                    LibraryWindow.MarkDirty();
                    Log($"saved meeting '{meeting.Title}' (id {meeting.Id})");
                }
                catch (Exception ex)
                {
                    // The row does not exist - carrying on with Id 0 would make every later write a
                    // silent no-op. Null routes the pill to the empty state; the audio is KEPT so
                    // the recovery sweep can rebuild the meeting on next launch.
                    Log($"save meeting failed - audio kept for recovery: {ex.Message}");
                    meeting = null;
                }
            }
            minShow.Wait();

            // Write the AI notes BEFORE revealing the Notes screen, so the heuristic draft never
            // flashes. A timeout guards a stuck generation so the pill can never hang; we're on a
            // background thread, so blocking on the result is safe.
            if (meeting is not null && transcript is { Count: > 0 })
            {
                try
                {
                    // Map-reduce makes several short LLM calls for a long meeting; give it room,
                    // but stay bounded so a stuck generation can never hang the pill.
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(300));
                    // The TRANSCRIPT is the only input. The user's sticky note is deliberately not
                    // fed in (owner decision) - it sits beside the summary, it doesn't shape it.
                    var llmNotes = LlmService.Instance
                        .WriteNotesAsync(transcript, cts.Token)
                        .GetAwaiter().GetResult();
                    // Only a note the MODEL wrote may replace the saved one - the heuristic
                    // fallback is already in the row.
                    if (llmNotes is { FromModel: true })
                    {
                        // The REAL title must exist before the carry-forward runs: it matches
                        // previous occurrences BY TITLE, and the heuristic placeholder (the
                        // meeting's first words) can match an unrelated meeting.
                        meeting.Notes = llmNotes;
                        meeting.Title = NoteExtractor.Title(llmNotes, meeting.App, meeting.StartedAt);

                        // The Library was usable during the LLM wait, and the user's edits WIN:
                        // ticks carry onto matching new tasks, and a typed title is not overwritten.
                        var fresh = Store.Get(meeting.Id);
                        if (fresh is not null)
                        {
                            var done = fresh.Notes.Actions.Where(a => a.Done).Select(a => TaskKey(a.Text)).ToHashSet();
                            foreach (var a in llmNotes.Actions)
                                if (done.Contains(TaskKey(a.Text))) a.Done = true;
                            var placeholder = NoteExtractor.Title(NoteExtractor.Extract(transcript), meeting.App, meeting.StartedAt);
                            if (fresh.Title != placeholder && !string.IsNullOrWhiteSpace(fresh.Title))
                            {
                                Log($"user retitled meeting {meeting.Id} during the LLM wait - keeping their title");
                                meeting.Title = fresh.Title;
                            }
                        }

                        // Carry forward what the last occurrence left unfinished. Deterministic and
                        // instant - no second model pass. Silent for a one-off.
                        if (Llm.OpenFromLastTime.Build(meeting, Store) is { } carried)
                        {
                            llmNotes.Sections = llmNotes.Sections.Prepend(carried).ToList();
                            Log($"carried {carried.Bullets.Count} open item(s) forward into meeting {meeting.Id}");
                        }
                        Store.ReplaceNotes(meeting.Id, llmNotes);
                        Store.UpdateTitleAndBody(meeting.Id, meeting.Title, meeting.Body);
                        MarkdownMirror.Write(meeting);
                        LibraryWindow.MarkDirty();
                        Log($"notes written by LLM for meeting {meeting.Id}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"LLM notes failed (kept heuristic): {ex.Message}");
                    // Re-sync the in-memory record with the DB so later views/mirror writes cannot
                    // show a note the store never accepted.
                    if (meeting is not null && Store.Get(meeting.Id) is { } dbTruth) meeting = dbTruth;
                }

                // Speaker resolution runs AFTER the notes (owner decision): it must never add a
                // second to the wait for notes. It updates the transcript in place once it lands.
                ResolveSpeakers(meeting, attendeeNames);

                // Low-memory: drop the 2.5GB model and re-warm the small recognizer so idle Toki
                // is light and the next meeting is ready to record.
                if (LowMemoryMode)
                {
                    LlmService.Instance.Release();
                    TranscriptionService.Instance.Warm();
                }
            }

            Current.Dispatcher.BeginInvoke(() =>
            {
                LastTranscript = transcript;
                CurrentMeeting = meeting;
                // No saved meeting (silence, trivial tap, or no ASR model) → empty state.
                var target = meeting is null ? PillState.NoSpeech : PillState.Notes;
                if (!State.TryGo(target))
                {
                    // The user forced the pill away mid-summarize. Never leave a dead Summarizing
                    // loader behind: retarget the remembered context, and reveal now only from
                    // neutral states - a page the user deliberately navigated to is not stomped.
                    ResultMissed?.Invoke(target);
                    if (State.Current is PillState.Hidden or PillState.Summarizing or PillState.Collapsed)
                        State.ForceForDemo(target);
                    else Log($"summarize result ready but pill busy ({State.Current}) - saved to Library");
                }
            });
        });
    }

    static void BeginRecording(DetectedApp? app)
    {
        // No code path may ever overwrite a live session - the old reference would keep capturing
        // until process exit with no way to stop it. Re-surface the existing recording instead.
        if (_session is not null)
        {
            Log("begin recording refused - a session is already live");
            State.ForceForDemo(PillState.RecordingBar);
            return;
        }
        try
        {
            // No explicit target → find the meeting app currently on the mic so we still capture
            // "them"; otherwise a manual start is mic-only and a muted meeting saves nothing.
            app ??= ResolveMeetingTarget();
            CurrentApp = app; // so the saved meeting is labelled with the right app
            var session = new RecordingSession(app);
            _session = session;
            // Start capture off the UI thread too - device init can take ~100ms.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    session.Start();
                    // Stream live-finalized segments to the recording preview (marshalled to the UI thread).
                    if (session.Live is { } live)
                        live.SegmentFinalized += s => Current?.Dispatcher.BeginInvoke(() => LivePreview?.Invoke(s));
                }
                catch (Exception ex) { Log($"begin recording (bg): {ex.Message}"); }
            });
        }
        catch (Exception ex) { Log($"begin recording failed (non-fatal): {ex.Message}"); }
    }

    /// <summary>Best-effort meeting app when recording starts without an explicit target: the
    /// non-denylisted app currently holding the mic. Null = nobody else on the mic (a solo note).</summary>
    static DetectedApp? ResolveMeetingTarget()
    {
        try
        {
            var app = MicUsageReader.GetActiveMicApps()
                .FirstOrDefault(a => !MeetingDetector.IsDenylisted(a));
            if (app is not null) Log($"manual start: resolved meeting target '{app.DisplayName}'");
            else Log("manual start: no meeting app on mic - recording mic only");
            return app;
        }
        catch (Exception ex) { Log($"resolve meeting target failed (mic-only): {ex.Message}"); return null; }
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
            _singleInstance?.Release();
            _singleInstance = null;
            var exe = Environment.ProcessPath;
            if (exe is not null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex) { Log($"restart failed: {ex.Message}"); }
        Exiting = true;
        Current.Shutdown();
    }

    /// <summary>True once the app is intentionally quitting - lets warm-and-hidden windows
    /// (e.g. the Library) actually close instead of cancelling-to-hide and blocking exit.</summary>
    public static bool Exiting { get; private set; }

    protected override void OnExit(ExitEventArgs e)
    {
        Exiting = true;
        _detector?.Stop();
        Calendar.PrepWatcher.Instance.Stop();
        // Quit mid-recording: finalize the WAVs and LEAVE the folder so the startup recovery
        // sweep turns it into a saved meeting on next launch.
        if (_session is not null) Log($"quit during recording - audio kept at {_session.FolderPath}; recovered on next launch");
        try { _session?.Stop(); } catch { /* best effort on shutdown */ }
        _ptt?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Diagnostic log at %TEMP%\toki-m1.log - "it didn't show" is only diagnosable if we can see what happened.</summary>
    static bool _logSizeChecked;
    public static void Log(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "toki-m1.log");
            // Bounded, checked once per session: the log names apps/meetings, so an unbounded
            // file in %TEMP% is a privacy leak.
            if (!_logSizeChecked)
            {
                _logSizeChecked = true;
                try { var fi = new FileInfo(path); if (fi.Exists && fi.Length > 4_000_000) fi.Delete(); }
                catch { }
            }
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* logging must never take the app down */ }
    }

    static void ToggleHome()
    {
        // Ctrl+Alt+Space summons the small Toki peek / tucks it back. Force-applied so the user
        // can never get stuck; tucking never hides a live recording (see CloseOverlay).
        if (State.Current is PillState.Hidden) State.ForceForDemo(PillState.Collapsed);
        else CloseOverlay();
    }

    // ── push-to-talk on the notch: hold Ctrl+Alt+Space → listen; release → transcribe → answer.
    //    No always-on mic - the mic runs ONLY while held. ──
    bool _pttOn;
    Audio.MicCapture? _pttMic;
    string? _pttWav;
    Task? _pttMicStart;   // the off-thread mic start - PttStop awaits it before stopping
    Llm.ConversationContext? _notchCtx;   // the notch's session memory (multi-turn until it closes)
    long? _notchScreenId;                 // meeting on screen when the notch opened (pill Notes view)


    /// <summary>Re-fit the notch to its current Ask content (listening → thinking → answer).</summary>
    public static void GrowNotch() => (Current as App)?._pill?.GrowToContent();

    void PttStart()
    {
        if (_pttOn) return;
        _pttOn = true;
        // Fresh conversation each time the notch opens from closed; multi-turn while it stays open.
        var fresh = State.Current != PillState.Ask;
        if (fresh) _notchCtx = null;
        // Screen-grounding: if the pill is showing a meeting's NOTES, "this meeting" means that
        // one. Captured BEFORE the notch takes over the pill state.
        if (fresh) _notchScreenId = State.Current == PillState.Notes ? CurrentMeeting?.Id : null;
        State.ForceForDemo(PillState.Ask);   // notch drops down into the listening view
        if (fresh) Views.AskView.Instance?.NewSession();
        if (!Transcription.TranscriptionService.ModelReady)
        {
            Views.AskView.Instance?.Failed("Add the on-device speech model (in onboarding) to ask by voice.");
            return;   // _pttOn stays true; PttStop resets it (there's just no WAV to transcribe)
        }
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Toki", "voice");
            Directory.CreateDirectory(dir);
            _pttWav = Path.Combine(dir, $"ptt-{Guid.NewGuid():N}.wav");
            var mic = new Audio.MicCapture();
            _pttMic = mic;
            var wav = _pttWav;
            Views.AskView.Instance?.Listening();
            // WASAPI device init takes ~100ms - keep it off the UI thread. PttStop awaits this
            // task so Stop can never race the still-starting mic.
            _pttMicStart = Task.Run(() =>
            {
                try
                {
                    // Prepare, wire the level tap, THEN begin - so the wave answers the first syllable.
                    mic.Prepare(wav);
                    if (mic.Format is { } fmt)
                        mic.OnBuffer = (buf, n) => Audio.LevelMeter.Voice.Report(buf, n, fmt);
                    mic.Begin();
                }
                catch (Exception ex)
                {
                    Log($"ptt mic start failed: {ex.Message}");
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (ReferenceEquals(_pttMic, mic) || _pttMic is null)
                            Views.AskView.Instance?.Failed("Couldn't reach the microphone - check Windows mic permissions.");
                    });
                }
            });
        }
        catch (Exception ex)
        {
            Log($"ptt mic start failed: {ex.Message}");
            Views.AskView.Instance?.Failed("Couldn't reach the microphone - check Windows mic permissions.");
        }
    }

    async void PttStop()
    {
        if (!_pttOn) return;
        _pttOn = false;
        var mic = _pttMic; _pttMic = null;
        var wav = _pttWav; _pttWav = null;
        var starting = _pttMicStart; _pttMicStart = null;
        try { if (starting is not null) await starting; } catch { }   // never stop a mic mid-start
        try { mic?.Stop(); } catch { }
        try { mic?.Dispose(); } catch { }
        if (wav is null || !File.Exists(wav)) return;   // e.g. model missing - the Failed hint already shows

        Views.AskView.Instance?.Thinking();
        string? text;
        try { text = await Task.Run(() => Transcription.TranscriptionService.Instance.TranscribeFile(wav)); }
        catch (Exception ex)
        {
            // Fail visibly and always delete the WAV - an unhandled throw leaves the notch
            // stuck on "Thinking".
            Log($"ptt transcribe failed: {ex.Message}");
            Views.AskView.Instance?.Failed("Couldn't transcribe that - try again.");
            return;
        }
        finally { try { File.Delete(wav); } catch { } }
        if (string.IsNullOrWhiteSpace(text))
        {
            // Empty capture is COMMON - never auto-reopen the mic here, or a run of empty captures
            // becomes an endless "I didn't catch that" loop. Say it once; the user holds to retry.
            Views.AskView.Instance?.Failed("I didn't catch that - hold Ctrl + Alt + Space and try again.");
            return;
        }
        await HandleNotchText(text!);
    }

    // Route a notch utterance: an assistant COMMAND runs instantly; otherwise it's a QUESTION
    // answered from the notes. The command router has a question-veto so a question containing a
    // command word is answered, never mis-fired as the command.
    async System.Threading.Tasks.Task HandleNotchText(string text)
    {
        var cmd = Llm.CommandRouter.TryMatch(text);
        if (cmd is Llm.AppCommand c && c != Llm.AppCommand.ShowTasks) { ExecuteNotchCommand(c); return; }

        // A misheard COMMAND shouldn't get a bad Q&A answer: show what Toki heard and let the user
        // say it again. Never auto-reopen the mic - a second capture grabs the audio device and
        // starves the next push-to-talk.
        if (Llm.CommandRouter.TryNearMiss(text) is string hint)
        { Views.AskView.Instance?.Failed(hint); return; }

        Views.AskView.Instance?.Answer(text);

        var acc = new System.Text.StringBuilder();
        var gate = new object();
        var capped = false;
        using var cts = new System.Threading.CancellationTokenSource();

        // Flush the answer to the notch in PATCHES (~70ms) so words appear in chunks, not char-by-char.
        var flusher = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(70) };
        flusher.Tick += (_, _) => { string cur; lock (gate) cur = acc.ToString(); Views.AskView.Instance?.SetAnswerText(StripMore(cur)); };
        flusher.Start();

        try
        {
            _notchCtx ??= new Llm.ConversationContext();
            // Screen-aware: the note open in the Library wins; else the notes the pill was showing
            // when the notch opened. Both derived from real view state, never a stale cached id.
            var screenId = CurrentViewedMeeting?.Id ?? _notchScreenId;
            var ans = await Task.Run(() => Llm.NotesQa.AskStreamAsync(text,
                srcs =>
                {
                    // Grounding must be VISIBLE to be trusted: when the answer is scoped to the
                    // on-screen note, show a small "About: <title>" line above it.
                    if (screenId is long sid && srcs.Count == 1 && srcs[0].Id == sid)
                        Dispatcher.BeginInvoke(new Action(() => Views.AskView.Instance?.Scope(srcs[0].Title)));
                },
                tok =>
                {
                    string s;
                    lock (gate) { acc.Append(tok); s = acc.ToString(); }
                    // Stop generating once we have a full sentence past the glance budget (or the model
                    // flagged [MORE]) - so the notch NEVER overshoots then visibly cuts back.
                    if (!capped && NotchOverBudget(s)) { capped = true; try { cts.Cancel(); } catch { } }
                },
                cts.Token, _notchCtx, concise: true, screenMeetingId: screenId));

            flusher.Stop();
            string finalRaw; lock (gate) finalRaw = acc.Length >= ans.Text.Length ? acc.ToString() : ans.Text;
            var (teaser, over) = Llm.NotesQa.NotchTrim(finalRaw);
            Views.AskView.Instance?.Final(teaser, capped || over, text);
        }
        catch (Exception ex) { flusher.Stop(); Log($"notch answer failed: {ex.Message}"); Views.AskView.Instance?.Final("Something went wrong - try again."); }
    }

    static string StripMore(string s) => s.Replace("[MORE]", "").Trim();   // Trim (not just TrimEnd): a leading newline from the model must never show as a blank first line

    // A completed sentence past ~240 chars, or the model's [MORE] flag → stop the stream at a clean boundary.
    static bool NotchOverBudget(string acc)
    {
        if (acc.Contains("[MORE]")) return true;
        var s = acc.Replace("[MORE]", "").TrimEnd();
        return s.Length >= 240 && (s[^1] is '.' or '!' or '?');
    }

    void ExecuteNotchCommand(Llm.AppCommand c)
    {
        switch (c)
        {
            // "Take me to the library" means the LIBRARY LIST - ShowLibrary() defaults to the Ask page.
            case Llm.AppCommand.OpenLibrary:   Views.AskView.Instance?.Command("Opening your Library."); LibraryWindow.OpenHubPage("library"); break;
            case Llm.AppCommand.OpenAsk:       Views.AskView.Instance?.Command("Opening the assistant."); LibraryWindow.OpenHubPage("ask"); break;
            case Llm.AppCommand.OpenSettings:  State.ForceForDemo(PillState.Settings); break;
            case Llm.AppCommand.GoHome:        State.ForceForDemo(PillState.Home); break;
            case Llm.AppCommand.OpenTemplates: Views.AskView.Instance?.Command("Opening Templates."); LibraryWindow.OpenHubPage("templates"); break;
            case Llm.AppCommand.NewNote:       Views.AskView.Instance?.Command("New note - go ahead."); LibraryWindow.ShowNewNote(); break;
            case Llm.AppCommand.NewChat:       _notchCtx = new Llm.ConversationContext(); Views.AskView.Instance?.Command("Fresh start - ask away."); break;
            case Llm.AppCommand.Close:         CloseOverlay(); break;
            case Llm.AppCommand.Help:          Views.AskView.Instance?.Help(); break;
            case Llm.AppCommand.StartRecording:
                if (IsRecording) { Views.AskView.Instance?.Command("Already recording - the bar's up top."); break; }
                StartRecordingFromCommand();   // notch morphs straight into the recording bar
                break;
            case Llm.AppCommand.UseApiModel:      Views.AskView.Instance?.Command(SwitchNotesMode(Llm.NotesMode.ApiKey)); break;
            case Llm.AppCommand.UseLocalModel:    Views.AskView.Instance?.Command(SwitchNotesMode(Llm.NotesMode.Local)); break;
            case Llm.AppCommand.UseHeuristicMode: Views.AskView.Instance?.Command(SwitchNotesMode(Llm.NotesMode.HeuristicOnly)); break;
            case Llm.AppCommand.ThemeLight:       Views.AskView.Instance?.Command(SwitchTheme(light: true)); break;
            case Llm.AppCommand.ThemeDark:        Views.AskView.Instance?.Command(SwitchTheme(light: false)); break;
            case Llm.AppCommand.GhostOn:          Views.AskView.Instance?.Command(SetGhostMode(on: true)); break;
            case Llm.AppCommand.GhostOff:         Views.AskView.Instance?.Command(SetGhostMode(on: false)); break;
        }
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
        // Replays the first-run flow on demand. Non-destructive: finishing only re-saves the name
        // if the box isn't blank (see CompleteOnboarding).
        menu.Items.Add(Item("Replay first-run setup", () => State.ForceForDemo(PillState.Onboarding)));
        menu.Items.Add(Item("Demo: meeting detected", () => State.ForceForDemo(PillState.MeetingPrompt)));
        menu.Items.Add(Item("Demo: summarizing", () => State.ForceForDemo(PillState.Summarizing)));
        menu.Items.Add(Item("Demo: notes ready", () => State.ForceForDemo(PillState.Notes)));
        menu.Items.Add(Item("Demo: nothing was said", () => State.ForceForDemo(PillState.NoSpeech)));
        // Arms a sample prep card WITHOUT a calendar event, so the prep-card flows can be
        // exercised without a real meeting minutes away.
        menu.Items.Add(Item("Demo: arm a prep card", () =>
        {
            CurrentPrep = Dev.PrepSamples.Matched();
            State.ForceForDemo(PillState.Collapsed);   // land on the peek - hover it to open the card
            Log("prep: demo card armed - hover the peek, or start recording and use the Prep card button");
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Library", () => LibraryWindow.ShowLibrary()));
        menu.Items.Add(Item("New note", () => LibraryWindow.ShowNewNote()));
        menu.Items.Add(Item("Settings", () => State.ForceForDemo(PillState.Settings)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Restart Toki", RestartApp));
        menu.Items.Add(Item("Quit Toki", () =>
        {
            Exiting = true;
            _tray?.Dispose();
            Shutdown();
        }));

        var tray = new TaskbarIcon
        {
            ToolTipText = "Toki - Ctrl+Alt+Space",
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
