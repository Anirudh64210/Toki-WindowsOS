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

    /// <summary>Raised (on the UI thread) when a transcript segment finalizes DURING recording — for the
    /// live-transcript preview in the recording view. Real text now, not the old fake placeholder lines.</summary>
    public static event Action<TranscriptSegment>? LivePreview;

    /// <summary>The app that triggered the current prompt / is being recorded (for UI + capture).</summary>
    public static DetectedApp? CurrentApp { get; private set; }

    /// <summary>Set on Stop: true when nothing audible was captured (→ friendly empty state).</summary>
    public static bool LastRecordingSilent { get; private set; }

    /// <summary>
    /// True on a memory-constrained PC (&lt;8GB RAM). In this mode Toki keeps only ONE
    /// heavy model resident at a time (release the LLM before transcribing, release
    /// the recognizer before writing notes) and never preloads the LLM — otherwise
    /// the 2.5GB LLM + 630MB recognizer starve transcription and page the whole PC.
    /// </summary>
    public static bool LowMemoryMode { get; private set; }

    /// <summary>True in the offscreen PNG render harness (--render). Views must not hit the
    /// network or persist settings in this mode — it runs against the user's real settings file.</summary>
    public static bool Headless { get; private set; }

    /// <summary>The merged transcript of the most recent recording (null until transcribed).</summary>
    public static IReadOnlyList<TranscriptSegment>? LastTranscript { get; private set; }

    /// <summary>Local database of meetings and notes.</summary>
    static readonly Lazy<MeetingStore> LazyStore = new(() => new MeetingStore());
    public static MeetingStore Store => LazyStore.Value;

    /// <summary>The meeting the Notes screen should display (saved after transcription).</summary>
    public static MeetingRecord? CurrentMeeting { get; set; }

    /// <summary>The meeting note the user is currently LOOKING AT in the Library (null if none / a manual
    /// note / the window is closed). Powers screen-aware Ask: "what were the action items?" resolves to
    /// THIS meeting when it's on screen. DERIVED live from the Library window's actual state (visible +
    /// user-opened note) — the old set/clear-from-scattered-callsites version went stale: it was cleared
    /// on hide but never restored when the warm window re-showed its old content, and the startup
    /// pre-warm set it to a note nobody was looking at, so "this meeting" grounded on the WRONG meeting
    /// (screenshot-verified). A computed property can't go stale.</summary>
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

    /// <summary>Discard a meeting entirely — remove it from the Library (DB), its Markdown mirror, and
    /// its recording folder. Used by the "Delete" choice on the Notes screen.</summary>
    public static void DiscardMeeting(MeetingRecord? m)
    {
        if (m is null) return;
        try
        {
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

    /// <summary>The pill's current on-screen rect in DIPs (for the Library morph to grow from the
    /// notch), or null if the pill isn't up. Read it BEFORE hiding the pill.</summary>
    public static System.Windows.Rect? PillRect() => (Current as App)?._pill?.ScreenDipRect();

    /// <summary>Re-write a saved meeting's notes in a SPECIFIC template (the manual override). Returns
    /// false when there's no transcript or no model available. Heavy — runs the LLM on the transcript.</summary>
    public static async Task<bool> RewriteNotesAsync(long meetingId, string templateName, System.Threading.CancellationToken ct = default)
    {
        var m = Store.Get(meetingId);
        if (m is null || m.Transcript.Count == 0) return false;
        var tpl = Llm.NoteTemplates.ByName(templateName);
        // Off the UI thread: WriteNotesAsync starts by loading the local model synchronously, which
        // would otherwise freeze the window the picker lives in.
        var notes = await System.Threading.Tasks.Task.Run(() => Llm.LlmService.Instance.WriteNotesAsync(m.Transcript, ct, tpl));
        if (notes is null) return false;
        notes.Template = tpl.Name;
        Store.ReplaceNotes(meetingId, notes);
        LibraryWindow.MarkDirty();
        return true;
    }

    static System.Threading.Mutex? _singleInstance;
    static System.Threading.EventWaitHandle? _showSignal;
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

        // How much RAM does this PC have? Toki's two heavy models (2.5GB LLM + 630MB
        // recognizer) don't both fit alongside a browser + meeting app on a small
        // machine — keeping both resident crashes transcription and pages the PC.
        var (totalGB, availGB) = Interop.SystemMemory.Physical();
        LowMemoryMode = totalGB > 0 && totalGB < 8.0;
        Log($"system RAM {totalGB:F1}GB (avail {availGB:F1}GB) → {(LowMemoryMode ? "LOW-MEMORY mode: one model at a time, no LLM prewarm" : "normal mode")}");

        // Decide how to run the model on THIS machine (GPU layers + context) BEFORE
        // any LLamaSharp call. Load the Vulkan backend ONLY when we actually intend to
        // offload — enabling it at 0 layers is the slowest possible path (see LlmRuntime).
        Llm.LlmRuntime.Resolve();
        Log($"llm runtime: {Llm.LlmRuntime.Reason}");
        try
        {
            var cfg = LLama.Native.NativeLibraryConfig.All;
            if (Llm.LlmRuntime.UseVulkan) cfg.WithVulkan(true).WithAutoFallback(true);
            else cfg.WithVulkan(false);
        }
        catch (Exception ex) { Log($"native lib config (non-fatal): {ex.Message}"); }

        // Headless self-tests (brief §10): attach a console, run, exit. No UI.
        var testFlag = e.Args.FirstOrDefault(a => a is "--mictest" or "--loopbacktest" or "--playtone" or "--detecttest" or "--recordtest" or "--asrtest" or "--voicetest" or "--transcribetest" or "--dbtest" or "--notestest" or "--renotes" or "--livetest" or "--seed" or "--llmtest" or "--themetest" or "--copytest" or "--asktest" or "--querytest" or "--followuptest" or "--embedtest" or "--caltest");
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
        // Ctrl+Alt+Space is PUSH-TO-TALK (hold to speak, release to answer) — see PushToTalkHook.
        _ptt = new Interop.PushToTalkHook(
            onStart: () => Dispatcher.BeginInvoke(new Action(PttStart)),
            onStop: () => Dispatcher.BeginInvoke(new Action(PttStop)));
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
            // Double-click launch: Toki peeks from the top edge (the character first) — click
            // it to open Home. It's never the welcome panel dropped on you.
            State.TryGo(PillState.Collapsed);
        }

        // Dev: open the Library window directly for inspection.
        if (e.Args.Contains("--library"))
            Dispatcher.BeginInvoke(() => LibraryWindow.ShowLibrary());
        if (e.Args.Contains("--newnote"))
            Dispatcher.BeginInvoke(() => LibraryWindow.ShowNewNote());

        // Pre-warm the Library window off-screen once the app has settled, so its first
        // open is instant (no visible build) — a clean, fast handoff from the pill.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { LibraryWindow.Warm(); } catch (Exception ex) { Log($"library warm (non-fatal): {ex.Message}"); }
        }), System.Windows.Threading.DispatcherPriority.Background);

        StartMeetingDetection();

        // Warm both engines ahead of the first meeting so a stop never waits on model
        // load (lesson 2) — but GENTLY. Toki must never make the rest of the PC feel
        // clunky. See WarmEnginesGently.
        WarmEnginesGently();
    }

    /// <summary>
    /// Load the ASR + LLM models ahead of the first meeting without disrupting the PC.
    /// The old path fired both heavy loads at once on normal-priority threads —
    /// reading ~3GB from disk, building graphs and uploading to the GPU all together —
    /// which made the mouse and desktop stutter on first launch of the day. Instead a
    /// single below-normal-priority background thread waits for the UI to settle, then
    /// loads the models one at a time. A finished meeting still never waits on a load,
    /// but starting Toki no longer competes with whatever you're doing.
    /// </summary>
    static void WarmEnginesGently()
    {
        var t = new System.Threading.Thread(() =>
        {
            try { System.Threading.Thread.Sleep(3000); } catch { } // let the app finish appearing
            try { TranscriptionService.Instance.Warm(); }
            catch (Exception ex) { Log($"ASR warm (non-fatal): {ex.Message}"); }
            // On a small-RAM PC we do NOT preload the 2.5GB LLM — that's what made the
            // machine clunky on first launch and starved transcription. It loads on
            // demand (one model at a time) when a meeting actually ends.
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
        // Auto-stop when the meeting app releases the mic — but only for a prompt we
        // detected. A manual start is stopped by the user, so it isn't armed here
        // (some apps drop the mic when you mute, which must NOT end a manual recording).
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

    /// <summary>True while a recording is running (for the "start taking notes" command's guard).</summary>
    public static bool IsRecording => State.Current is PillState.RecordingBar or PillState.RecordingOpen;

    /// <summary>"Start taking notes" as an assistant command (voice or typed). Same manual-start path
    /// as the Home button — the user explicitly asked, so this IS the human in the loop (constraint #3).
    /// Forced state (not TryGo): the command can arrive from the Ask notch or with the Library open,
    /// where the pill may be in a state with no direct edge to RecordingBar.</summary>
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
            return "There's no API key saved yet — add one in Settings, then I can switch.";
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
            Llm.NotesMode.HeuristicOnly => "Switched to the fast built-in notes — no AI model.",
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
            return on ? "Ghost mode is already on — Toki stays out of screen captures." : "Ghost mode is already off — Toki shows in recordings.";
        s.ShowInRecordings = wanted;
        s.Save();
        SetShowInRecordings(wanted);
        return on ? "Ghost mode on — Toki is hidden from screen recordings and shares."
                  : "Ghost mode off — Toki will show up in recordings.";
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
        // The FRIENDLY app name ("Slack", not "Com.tinyspeck.slackdesktop") — it labels the saved
        // meeting and seeds the fallback title "<app> — <date>", both user-facing.
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
            var minShow = System.Threading.Tasks.Task.Delay(1200); // let the loader be seen
            try
            {
                // Live transcription already did the work DURING the meeting → use it (the fast path,
                // no multi-minute wait). Fall back to the whole-file pass only when live produced
                // nothing — that pass is fast on true silence and correct if the live path glitched.
                if (live is { Count: > 0 })
                {
                    transcript = live;
                    Log($"using live transcript ({live.Count} segments) — no stop-time ASR");
                }
                else if (folder.Length > 0)
                {
                    transcript = TranscriptionService.Instance.Process(folder);
                }
            }
            catch (Exception ex) { Log($"transcription failed (non-fatal): {ex.Message}"); }

            // An accidental tap shouldn't become a hollow Library entry with a bogus summary.
            // A meeting has real content; a mis-click yields a handful of garbled words (e.g.
            // "Met the t I mean"). Drop anything with too few words OR a very short duration —
            // it's not a meeting. (Was: <3s AND <5 words — too weak; a 5-word tap slipped through.)
            var words = transcript?.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length) ?? 0;
            var tooTrivial = transcript is { Count: > 0 } && (words < 12 || durationMs < 4000);
            if (tooTrivial) Log($"discarding trivial recording ({durationMs}ms, {words} words) — accidental tap");

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
                        Notes = notes,
                        Title = NoteExtractor.Title(notes, app, startedAt.ToLocalTime()),
                    };
                    Store.Save(meeting);
                    MarkdownMirror.Write(meeting);
                    LibraryWindow.MarkDirty(); // a new meeting exists — refresh the Library on next open
                    Log($"saved meeting '{meeting.Title}' (id {meeting.Id})");
                }
                catch (Exception ex) { Log($"save meeting failed (non-fatal): {ex.Message}"); }
            }
            minShow.Wait();

            // Write the good AI notes BEFORE revealing the Notes screen, so the rough
            // heuristic draft (raw transcript lines) never flashes. The Summarizing
            // animation stays up while this runs — the user waits once and sees the
            // finished notes. Returns fast (heuristic kept) when there's no AI model;
            // a timeout guards a stuck generation so the pill can never hang. We're on
            // a background thread here, so blocking on the result is safe.
            if (meeting is not null && transcript is { Count: > 0 })
            {
                try
                {
                    // Map-reduce makes several short LLM calls for a long meeting; give it room.
                    // (Still bounded so a stuck generation can never hang the pill.)
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(300));
                    var llmNotes = LlmService.Instance.WriteNotesAsync(transcript, cts.Token).GetAwaiter().GetResult();
                    if (llmNotes is not null)
                    {
                        Store.ReplaceNotes(meeting.Id, llmNotes);
                        meeting.Notes = llmNotes;
                        meeting.Title = NoteExtractor.Title(llmNotes, meeting.App, meeting.StartedAt);
                        Store.UpdateTitleAndBody(meeting.Id, meeting.Title, meeting.Body);
                        MarkdownMirror.Write(meeting);
                        LibraryWindow.MarkDirty(); // notes/title upgraded — refresh on next open
                        Log($"notes written by LLM for meeting {meeting.Id}");
                    }
                }
                catch (Exception ex) { Log($"LLM notes failed (kept heuristic): {ex.Message}"); }

                // Low-memory: don't leave the 2.5GB model sitting resident while the user
                // reads their notes and goes back to work. Drop it and re-warm the small
                // recognizer so idle Toki is light and the next meeting is ready to record.
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
                if (meeting is null) State.TryGo(PillState.NoSpeech);
                else State.TryGo(PillState.Notes);
            });
        });
    }

    static void BeginRecording(DetectedApp? app)
    {
        try
        {
            // No explicit target (manual/hotkey/Home start) → find the meeting app
            // currently on the mic so we still capture "them". This is what makes
            // "mute yourself and hit record" actually save the other person; without
            // it, a manual start is mic-only and a muted meeting saves nothing.
            app ??= ResolveMeetingTarget();
            CurrentApp = app; // so the saved meeting is labelled with the right app
            var session = new RecordingSession(app);
            _session = session;
            // Start capture off the UI thread too — device init can take ~100ms.
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

    /// <summary>
    /// Best-effort meeting app when recording starts without an explicit target.
    /// Picks the (non-denylisted) app currently holding the mic open — how Toki
    /// still captures "them" when you mute yourself and hit record, or start a
    /// recording mid-call. Null = nobody else on the mic (a genuine solo note).
    /// </summary>
    static DetectedApp? ResolveMeetingTarget()
    {
        try
        {
            var app = MicUsageReader.GetActiveMicApps()
                .FirstOrDefault(a => !MeetingDetector.IsDenylisted(a));
            if (app is not null) Log($"manual start: resolved meeting target '{app.DisplayName}'");
            else Log("manual start: no meeting app on mic — recording mic only");
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
            _singleInstance?.Dispose();
            _singleInstance = null;
            var exe = Environment.ProcessPath;
            if (exe is not null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex) { Log($"restart failed: {ex.Message}"); }
        Exiting = true;
        Current.Shutdown();
    }

    /// <summary>True once the app is intentionally quitting — lets warm-and-hidden windows
    /// (e.g. the Library) actually close instead of cancelling-to-hide and blocking exit.</summary>
    public static bool Exiting { get; private set; }

    protected override void OnExit(ExitEventArgs e)
    {
        Exiting = true;
        _detector?.Stop();
        try { _session?.Stop(); } catch { /* best effort on shutdown */ }
        _ptt?.Dispose();
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
        // Ctrl+Alt+Space summons the small Toki peek (NOT the full welcome window) / tucks it back.
        // Toki-first, like the edge-summon: you get the character, then open a page from it.
        // Force-applied so the user can never get stuck.
        if (State.Current is PillState.Hidden) State.ForceForDemo(PillState.Collapsed);
        else State.ForceForDemo(PillState.Hidden);
    }

    // ── push-to-talk on the notch: hold Ctrl+Alt+Space → Toki drops down and listens; release →
    //    transcribe → answer, all on the notch. No always-on mic — the mic runs ONLY while held. ──
    bool _pttOn;
    Audio.MicCapture? _pttMic;
    string? _pttWav;
    Llm.ConversationContext? _notchCtx;   // the notch's session memory (multi-turn until it closes)
    long? _notchScreenId;                 // meeting on screen when the notch opened (pill Notes view)

    /// <summary>Re-fit the notch to its current Ask content (listening → thinking → answer).</summary>
    public static void GrowNotch() => (Current as App)?._pill?.GrowToContent();

    void PttStart()
    {
        if (_pttOn) return;
        _pttOn = true;
        // Fresh conversation each time the notch opens from closed; keep the thread while it stays open (multi-turn).
        var fresh = State.Current != PillState.Ask;
        if (fresh) _notchCtx = null;
        // Screen-grounding for the notch: if the pill is showing a just-finished meeting's NOTES,
        // "this meeting" means that one. Captured BEFORE the notch takes over the pill state.
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
            _pttMic = new Audio.MicCapture();
            _pttMic.Start(_pttWav);
            Views.AskView.Instance?.Listening();
        }
        catch (Exception ex)
        {
            Log($"ptt mic start failed: {ex.Message}");
            Views.AskView.Instance?.Failed("Couldn't reach the microphone — check Windows mic permissions.");
        }
    }

    async void PttStop()
    {
        if (!_pttOn) return;
        _pttOn = false;
        var mic = _pttMic; _pttMic = null;
        var wav = _pttWav; _pttWav = null;
        try { mic?.Stop(); } catch { }
        try { mic?.Dispose(); } catch { }
        if (wav is null || !File.Exists(wav)) return;   // e.g. model missing — the Failed hint already shows

        Views.AskView.Instance?.Thinking();
        var text = await Task.Run(() => Transcription.TranscriptionService.Instance.TranscribeFile(wav));
        try { File.Delete(wav); } catch { }
        if (string.IsNullOrWhiteSpace(text)) { Views.AskView.Instance?.Failed("Didn't catch that — hold and speak."); return; }
        await HandleNotchText(text!);
    }

    // Route a notch utterance: a little assistant COMMAND (open the library, new note, …) runs instantly;
    // otherwise it's a QUESTION answered from your notes. The command router (Tier 0) has a question-veto
    // so "what did we decide in the settings review?" is answered, never mis-fired as "open settings".
    async System.Threading.Tasks.Task HandleNotchText(string text)
    {
        var cmd = Llm.CommandRouter.TryMatch(text);
        if (cmd is Llm.AppCommand c && c != Llm.AppCommand.ShowTasks) { ExecuteNotchCommand(c); return; }

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
            // when the notch opened. Both derived from real view state — never a stale cached id.
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
                    // flagged [MORE]) — so the notch NEVER overshoots then visibly cuts back.
                    if (!capped && NotchOverBudget(s)) { capped = true; try { cts.Cancel(); } catch { } }
                },
                cts.Token, _notchCtx, concise: true, screenMeetingId: screenId));

            flusher.Stop();
            string finalRaw; lock (gate) finalRaw = acc.Length >= ans.Text.Length ? acc.ToString() : ans.Text;
            var (teaser, over) = Llm.NotesQa.NotchTrim(finalRaw);
            Views.AskView.Instance?.Final(teaser, capped || over, text);
        }
        catch (Exception ex) { flusher.Stop(); Log($"notch answer failed: {ex.Message}"); Views.AskView.Instance?.Final("Something went wrong — try again."); }
    }

    static string StripMore(string s) => s.Replace("[MORE]", "").TrimEnd();

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
            // "Take me to the library" means the LIBRARY LIST — not the hub's Ask landing (that was
            // the wrong-page bug: ShowLibrary() defaults to the Ask page).
            case Llm.AppCommand.OpenLibrary:   Views.AskView.Instance?.Command("Opening your Library."); LibraryWindow.OpenHubPage("library"); break;
            case Llm.AppCommand.OpenAsk:       Views.AskView.Instance?.Command("Opening the assistant."); LibraryWindow.OpenHubPage("ask"); break;
            case Llm.AppCommand.OpenSettings:  State.ForceForDemo(PillState.Settings); break;
            case Llm.AppCommand.GoHome:        State.ForceForDemo(PillState.Home); break;
            case Llm.AppCommand.OpenTemplates: Views.AskView.Instance?.Command("Opening Templates."); LibraryWindow.OpenHubPage("templates"); break;
            case Llm.AppCommand.NewNote:       Views.AskView.Instance?.Command("New note — go ahead."); LibraryWindow.ShowNewNote(); break;
            case Llm.AppCommand.NewChat:       _notchCtx = new Llm.ConversationContext(); Views.AskView.Instance?.Command("Fresh start — ask away."); break;
            case Llm.AppCommand.Close:         State.ForceForDemo(PillState.Hidden); break;
            case Llm.AppCommand.Help:          Views.AskView.Instance?.Help(); break;
            case Llm.AppCommand.StartRecording:
                if (IsRecording) { Views.AskView.Instance?.Command("Already recording — the bar's up top."); break; }
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
        menu.Items.Add(Item("Demo: meeting detected", () => State.ForceForDemo(PillState.MeetingPrompt)));
        menu.Items.Add(Item("Demo: summarizing", () => State.ForceForDemo(PillState.Summarizing)));
        menu.Items.Add(Item("Demo: notes ready", () => State.ForceForDemo(PillState.Notes)));
        menu.Items.Add(Item("Demo: nothing was said", () => State.ForceForDemo(PillState.NoSpeech)));
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
