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

    /// <summary>
    /// What the loader is actually doing, so it can say so.
    ///
    /// It used to read "Tidying up your notes" for the whole wait, which on a long meeting is
    /// transcription followed by several local-model calls and can run for minutes. A friendly phrase
    /// that never changes is indistinguishable from a hung app: "Specific beats friendly."
    ///
    /// The STAGE is named and no time is promised. An estimate would have to be measured, and a
    /// fabricated one is the same lie as a progress bar that is not tracking anything.
    /// </summary>
    public static string SummarizeStage
    {
        get => _summarizeStage;
        set
        {
            if (_summarizeStage == value) return;
            _summarizeStage = value;
            // Raised on the UI thread: the pipeline sets this from a background task.
            Current?.Dispatcher.BeginInvoke(new Action(() => SummarizeStageChanged?.Invoke(null, EventArgs.Empty)));
        }
    }

    static string _summarizeStage = "Tidying up your notes";

    /// <summary>The meeting's TITLE, the moment the model has written it - which is about two seconds
    /// into a final pass that runs for fourteen. The note itself cannot be shown until the whole
    /// generation is parsed, but the title is the first line the model emits, so the wait stops being
    /// blank long before it ends. Null until known; cleared at the start of every summarize so a new
    /// meeting never wears the last one's title.</summary>
    public static string? SummarizeTitle
    {
        get => _summarizeTitle;
        set
        {
            if (_summarizeTitle == value) return;
            _summarizeTitle = value;
            Current?.Dispatcher.BeginInvoke(new Action(() => SummarizeStageChanged?.Invoke(null, EventArgs.Empty)));
        }
    }

    static string? _summarizeTitle;
    public static event EventHandler? SummarizeStageChanged;

    /// <summary>Set by the pill: opens the Toki peek into the remembered page (click on the peek).</summary>
    public static Action? OpenPeek;

    /// <summary>Raised (on the UI thread) when a transcript segment finalizes DURING recording - for the
    /// live-transcript preview in the recording view.</summary>
    public static event Action<TranscriptSegment>? LivePreview;

    /// <summary>Raised (on the UI thread) with a speaker's GROWING in-progress line from the fast
    /// streaming preview - so words appear as they are spoken, not only when a line finalizes.</summary>
    public static event Action<Toki.Transcription.Speaker, string>? LivePreviewPartial;

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

    /// <summary>True only on a HIGH-RAM PC (&gt;=32GB): prewarm the notes LLM at launch and keep it
    /// resident for instant notes. Otherwise the LLM loads ON DEMAND (lighter startup) - see the
    /// RAM-tier note in OnStartup. Distinct from <see cref="LowMemoryMode"/>, which is the &lt;8GB
    /// one-model-at-a-time behaviour.</summary>
    public static bool PrewarmLlm { get; private set; }

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

    /// <summary>The outcome of a user-triggered "try again with the on-device model" on a note that
    /// fell back to the heuristic floor.</summary>
    public enum RegenNotesResult { Rewritten, NoModel, NeedsRestart }

    /// <summary>
    /// Re-attempt AI note-writing for a meeting whose notes fell back to the heuristic floor (the
    /// local model failed to load this session). Clears the stuck failure flag and tries the model
    /// once more; if it loads, the floor notes are replaced with the model's. A native poisoning
    /// cannot recover in-process, so that case reports <see cref="RegenNotesResult.NeedsRestart"/>
    /// rather than silently keeping the floor.
    /// </summary>
    public static async Task<RegenNotesResult> RegenerateNotesAsync(long meetingId, System.Threading.CancellationToken ct = default)
    {
        var m = Store.Get(meetingId);
        if (m is null || m.Transcript.Count == 0) return RegenNotesResult.NoModel;

        // Off the UI thread: both the model (re)load and the generation are heavy and block at the
        // native layer. RetryLocal clears the stuck failure; WriteNotesAsync then loads on demand.
        var notes = await System.Threading.Tasks.Task.Run(() =>
        {
            Llm.LlmService.Instance.RetryLocal();
            return Llm.LlmService.Instance.WriteNotesAsync(m.Transcript, ct).GetAwaiter().GetResult();
        });

        // Never replace a good note with the floor: only a note the MODEL actually wrote may land.
        if (notes is not { FromModel: true })
            return Llm.LlmService.Instance.LocalPoisoned ? RegenNotesResult.NeedsRestart : RegenNotesResult.NoModel;

        // The user's ticks win, exactly like the post-meeting path: carry Done across by task identity.
        var done = m.Notes.Actions.Where(a => a.Done).Select(a => TaskKey(a.Text)).ToHashSet();
        foreach (var a in notes.Actions)
            if (done.Contains(TaskKey(a.Text))) a.Done = true;

        m.Notes = notes;

        // The re-transcribe should RETITLE too: the floor placeholder (the meeting's first spoken
        // words) is usually exactly what the user is escaping. Replace it with the model's title, but
        // never clobber a title the USER typed - detect the placeholder by recomputing it.
        var placeholder = NoteExtractor.Title(NoteExtractor.Extract(m.Transcript), m.App, m.StartedAt);
        if (string.IsNullOrWhiteSpace(m.Title) || m.Title == placeholder)
        {
            m.Title = NoteExtractor.Title(notes, m.App, m.StartedAt);
            Store.UpdateTitleAndBody(meetingId, m.Title, m.Body);
        }

        if (Llm.OpenFromLastTime.Build(m, Store) is { } carried)
            notes.Sections = notes.Sections.Prepend(carried).ToList();

        Store.ReplaceNotes(meetingId, notes);
        Storage.MarkdownMirror.Write(m);    // keep the .md mirror telling the same story as the DB
        LibraryWindow.MarkDirty();
        Log($"regenerated notes for meeting {meetingId} with the on-device model");
        return RegenNotesResult.Rewritten;
    }

    /// <summary>
    /// Drop a section from a saved note, for good. Today only the carried-forward section offers
    /// this: matching two meetings by title alone can never be reliable, so the user gets a cheap
    /// correction instead of a confident guess. Removing it here also stops it coming back, because
    /// the section is PERSISTED in the note rather than recomputed on every render.
    /// </summary>
    public static bool DismissNoteSection(long meetingId, string heading)
    {
        try
        {
            if (Store.Get(meetingId) is not { } m) return false;
            var before = m.Notes.Sections.Count;
            m.Notes.Sections = m.Notes.Sections
                .Where(x => !string.Equals(x.Heading, heading, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (m.Notes.Sections.Count == before) return false;

            Store.ReplaceNotes(meetingId, m.Notes);
            Storage.MarkdownMirror.Write(m);
            LibraryWindow.MarkDirty();
            Log($"dismissed section \"{heading}\" on meeting {meetingId}");
            return true;
        }
        catch (Exception ex) { Log($"dismiss section failed: {ex.Message}"); return false; }
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
        // RAM-tier residency (the load-on-demand slice, owner-requested 2026-08-25): only a HIGH-RAM box
        // (>=32GB) prewarms + keeps the 2.5GB LLM resident for instant notes. Everything else - including
        // the common 8-16GB laptop the owner runs - LOADS ON DEMAND: the LLM loads when a meeting's notes
        // or an Ask actually needs it (~10s once per session, covered by the loader), so idle Toki stays
        // light and startup doesn't feel heavy. This is NOT low-memory mode (no aggressive release); it
        // only skips the prewarm. `EnsureLocal` already loads lazily, so skipping the prewarm IS the slice.
        PrewarmLlm = totalGB >= 32.0;
        Log($"system RAM {totalGB:F1}GB (avail {availGB:F1}GB) → "
            + (LowMemoryMode ? "LOW-MEMORY: one model at a time, no LLM prewarm"
               : PrewarmLlm ? "high-RAM: prewarm + keep the LLM resident"
               : "load-on-demand: LLM loads when first needed (lighter startup)"));

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
        var testFlag = e.Args.FirstOrDefault(a => a is "--mictest" or "--loopbacktest" or "--playtone" or "--detecttest" or "--recordtest" or "--asrtest" or "--evidencetest" or "--linktest" or "--wertest" or "--voicetest" or "--streamtest" or "--embedconflicttest" or "--transcribetest" or "--dbtest" or "--notestest" or "--renotes" or "--groundtest" or "--regentest" or "--livetest" or "--seed" or "--llmtest" or "--themetest" or "--copytest" or "--asktest" or "--querytest" or "--followuptest" or "--embedtest" or "--caltest" or "--preptest" or "--voicecreatetest" or "--measuretest" or "--notetest" or "--dumpnote" or "--uitest" or "--diartest" or "--speakertest" or "--hittest" or "--revealtest" or "--wavetest" or "--fpstest" or "--stickytest" or "--hovershot" or "--makeicon");
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
        // Filter on the PARSE, not on the value. The old form mapped an unparseable name to Hidden
        // and then dropped every Hidden - which did discard typos, but also made `Hidden` itself
        // unreachable, so the one transition this seam exists to drive (the notch retracting into
        // the top edge) was the one transition it could not drive. Found while trying to film the
        // close the owner reported as "it just cuts back up" (2026-09-27).
        var hops = i >= 0 && i + 1 < e.Args.Length
            ? e.Args[i + 1].Split(',')
                .Select(part => (ok: Enum.TryParse<PillState>(part, ignoreCase: true, out var st), st))
                .Where(p => p.ok).Select(p => p.st).ToArray()
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
            // WARM BEFORE THE FIRST SUMMON, not after it. This used to run at ApplicationIdle, which
            // is AFTER this line - so the very first drop-down the user ever sees, the one that sets
            // their impression of the whole app, was the only cold one: CollapsedView and TokiBlob
            // were constructed inside the transition, and a cold layout pass can outlast the 90ms
            // BeginTime on Toki's entrance spring. The subsequent WarmFirstOpen call is now a no-op
            // for these views and still catches the panels.
            _pill?.WarmFirstOpen();
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

            // Whisper base.en: the ASK-BY-VOICE decoder (robust on 1-2 word clips where Parakeet
            // collapses "Hi Toki" to "Yeah"). Fetched at launch, never mid-meeting, and only once
            // Parakeet is present. Failure is silent: the Ask path falls back to Parakeet until it lands.
            if (TranscriptionService.ModelReady && !ModelManager.WhisperInstalled)
            {
                try { ModelManager.EnsureWhisperAsync().GetAwaiter().GetResult(); Log("ask-by-voice: whisper base.en downloaded"); }
                catch (Exception ex) { Log($"whisper download (non-fatal, Ask falls back to Parakeet): {ex.Message}"); }
            }
            try { TranscriptionService.Instance.WarmWhisper(); }
            catch (Exception ex) { Log($"whisper warm (non-fatal): {ex.Message}"); }

            // Streaming zipformer: the LIVE-PREVIEW decoder (words as spoken, ~1s) so the recording view
            // fills instantly instead of the ~15-21s Parakeet window. Fetched at launch, never mid-meeting,
            // only once Parakeet is present. Failure is silent: the preview falls back to the Parakeet window.
            if (TranscriptionService.ModelReady && !ModelManager.StreamingAsrInstalled)
            {
                try { ModelManager.EnsureStreamingAsrAsync().GetAwaiter().GetResult(); Log("live preview: streaming zipformer downloaded"); }
                catch (Exception ex) { Log($"streaming model download (non-fatal, preview falls back to Parakeet window): {ex.Message}"); }
            }

            // Speaker diarisation models (pyannote segmentation + CampPlus embedding). Without these,
            // every remote voice collapses into "Them" and nobody can be named - they were only ever
            // fetched by the self-tests, so a fresh user never got diarisation. Fetch at launch, never
            // mid-meeting, only once Parakeet is present. Failure is silent (the meeting stays You/Them).
            if (TranscriptionService.ModelReady && !ModelManager.DiarisationInstalled)
            {
                try
                {
                    ModelManager.EnsureAsync(ModelManager.Segmentation).GetAwaiter().GetResult();
                    ModelManager.EnsureSpeakerEmbedAsync().GetAwaiter().GetResult();
                    Log("diarisation models downloaded (segmentation + speaker embedding)");
                }
                catch (Exception ex) { Log($"diarisation model download (non-fatal, stays You/Them): {ex.Message}"); }
            }
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
            // Prewarm the 2.5GB LLM only on a HIGH-RAM box; everyone else loads it ON DEMAND (the
            // load-on-demand slice) so idle Toki stays light and startup isn't heavy. It loads when a
            // meeting's notes or an Ask first needs it (~10s once, under the loader). Below 8GB this is
            // also true, plus the aggressive one-model-at-a-time release lives elsewhere.
            if (!PrewarmLlm) { Log("load-on-demand: skipping LLM prewarm (loads when first needed)"); return; }
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
        if (CurrentApp is not null) _detector?.Decline(CurrentApp);
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

    /// <summary>Meetings whose speakers are still being resolved. Resolution now runs BEFORE the notes
    /// (owner decision 2026-08-25) so the writer sees real names; the loader shows "Identifying speakers"
    /// during it. Nothing about anyone's voice is stored: clusters live and die inside
    /// <see cref="SpeakerResolver"/>; only renameable text lands in the database.</summary>
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

    /// <summary>Tell the speakers apart and TAG the transcript - now run BEFORE the notes (owner
    /// decision 2026-08-25): diarisation is ~50s/hour, and running it first lets the note writer see
    /// real names, attribute to-dos correctly, and NOT turn a roll-call into to-dos. Transient by
    /// design: nothing about anyone's voice is stored - clusters live and die inside SpeakerResolver,
    /// only renameable text lands in the DB. Returns true if it ran and tagged.</summary>
    static bool TagSpeakers(MeetingRecord meeting, List<string>? attendeeNames)
    {
        if (meeting.Transcript.Count == 0 || string.IsNullOrWhiteSpace(meeting.Folder)) return false;
        if (!SpeakerDiariser.Available) return false;

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

            if (!result.Ran) { Log($"speakers not resolved for meeting {meeting.Id}: {result.Skipped}"); return false; }
            // The meeting may have been deleted while this ran - never resurrect a discarded row.
            if (Store.Get(meeting.Id) is null) { Log($"meeting {meeting.Id} gone - dropping speaker tags"); return false; }

            Store.SetSpeakerTags(meeting.Id, result.Tags);
            for (var i = 0; i < meeting.Transcript.Count && i < result.Tags.Count; i++)
                meeting.Transcript[i] = meeting.Transcript[i] with { Tag = result.Tags[i] };

            var named = string.Join(", ", result.Named.Select(n => n.Name));
            Log($"speakers resolved for meeting {meeting.Id}: {result.Speakers} voices in {result.Took.TotalSeconds:F1}s"
                + (named.Length > 0 ? $" - named {named}" : " - none named"));
            return true;
        }
        catch (Exception ex) { Log($"speaker resolution failed (non-fatal): {ex.Message}"); return false; }
        finally
        {
            lock (Resolving) Resolving.Remove(meeting.Id);
            try { Current?.Dispatcher.BeginInvoke(() => SpeakersResolved?.Invoke(meeting.Id)); } catch { }
        }
    }

    /// <summary>Attribute to-do OWNERS from the (now speaker-tagged) transcript. Runs AFTER the notes,
    /// since it needs the written action items - no second model pass, just the existing evidence links.</summary>

    /// <summary>The meeting's own facts, for the note writer. Toki already knows the date, the
    /// length, who was invited and what the event was called; none of it used to reach the model,
    /// so a 4-minute catch-up and a 90-minute review were written identically. Facts only - each
    /// line is something Toki OBSERVED, never a guess and never an instruction.</summary>
    internal static string MeetingFacts(Storage.MeetingRecord m, Calendar.PrepContent? prep)
    {
        var lines = new System.Collections.Generic.List<string>();
        lines.Add("Date: " + m.StartedAt.ToLocalTime().ToString("dddd, MMMM d yyyy, h:mm tt"));
        var mins = (int)Math.Round(m.DurationMs / 60000.0);
        lines.Add("Length: " + (mins < 1 ? "under a minute" : mins + (mins == 1 ? " minute" : " minutes")));
        if (!string.IsNullOrWhiteSpace(m.App)) lines.Add("Held in: " + m.App);
        var title = prep?.Event.Summary;
        if (!string.IsNullOrWhiteSpace(title)) lines.Add("Calendar event: " + title);
        var names = prep?.Event.Attendees
            .Where(a => !string.IsNullOrWhiteSpace(a.DisplayName))
            .Select(a => a.DisplayName!).Take(12).ToList();
        if (names is { Count: > 0 }) lines.Add("Invited: " + string.Join(", ", names));
        // The speakers diarisation actually FOUND, which is the honest attendee list.
        var heard = m.Transcript.Select(t => t.Tag).Where(t => !string.IsNullOrWhiteSpace(t))
                     .Distinct().Take(12).ToList();
        if (heard.Count > 0) lines.Add("Voices heard: " + string.Join(", ", heard));
        return string.Join(Environment.NewLine, lines);
    }

    static void InferOwners(MeetingRecord meeting)
    {
        try
        {
            var owners = ActionOwners.Infer(meeting.Notes.Actions, meeting.Transcript);
            if (owners.Count == 0) return;
            Store.SetActionOwners(meeting.Id, owners);
            foreach (var (index, owner) in owners)
                if (index < meeting.Notes.Actions.Count) meeting.Notes.Actions[index].Owner = owner;
            Log($"attributed {owners.Count} to-do(s) from the transcript");
        }
        catch (Exception ex) { Log($"owner inference failed (non-fatal): {ex.Message}"); }
    }

    public static void StopRecording()
    {
        var session = _session;
        _session = null;
        _detector?.EndRecording();

        // The sticky note open during this meeting, if any. Captured HERE, before anything can
        // clear CurrentPrep; attached to the saved meeting further down.
        var stickyId = CurrentPrep is { } endedPrep ? Store.StickyForEvent(endedPrep.Event.Id) : null;
        // Held for the note writer's facts block, for the same reason: CurrentPrep gets cleared.
        var prepForNotes = CurrentPrep;

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
        SummarizeTitle = null;   // a new meeting must never wear the last one's title
        SummarizeStage = "Transcribing";
        System.Threading.Tasks.Task.Run(() =>
        {
            // THE EXIT IS UNCONDITIONAL. Nothing below moves the notch out of Summarizing except the
            // dispatcher call in the `finally`, so it must never be reachable-only-on-success. A path
            // that skips it - an early return, a throw from a step that is not itself wrapped - strands
            // the loader spinning forever over notes that were already written and saved. That shipped
            // once (2026-09-04: the thin-input `return` below) and cost the owner 15 minutes watching a
            // progress bar count up. transcript and meeting are hoisted so the finally can advance on
            // whatever the pipeline managed to produce before it gave up.
            IReadOnlyList<TranscriptSegment>? transcript = null;
            MeetingRecord? meeting = null;
            try
            {
                var folder = "";
                IReadOnlyList<TranscriptSegment>? live = null;
                try { folder = session?.Stop() ?? ""; live = session?.LiveTranscript; }
                catch (Exception ex) { Log($"stop recording (bg): {ex.Message}"); }

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

                // TELL THE SPEAKERS APART FIRST (owner decision 2026-08-25). Running diarisation before the
                // notes lets the writer see real names, attribute to-dos to the right person, and recognise a
                // roll-call as introductions rather than tasks. It tags meeting.Transcript in place, which is
                // what the note writer reads below. ~50s/hour; the loader names the stage.
                if (meeting is not null && transcript is { Count: > 0 })
                {
                    SummarizeStage = "Identifying speakers";
                    try { TagSpeakers(meeting, attendeeNames); }
                    catch (Exception ex) { Log($"speaker tagging (non-fatal, notes stay You/Them): {ex.Message}"); }
                }

                // Write the AI notes BEFORE revealing the Notes screen, so the heuristic draft never
                // flashes. A timeout guards a stuck generation so the pill can never hang; we're on a
                // background thread, so blocking on the result is safe.
                if (meeting is not null && transcript is { Count: > 0 })
                {
                    SummarizeStage = "Writing your notes";
                    try
                    {
                        // THE THIN-INPUT GATE. Some meetings are real enough to keep but too thin to
                        // write a structured note about, and handing the writer a shape it cannot fill
                        // is how a single sentence became three Options considered and a Decision.
                        // Deciding this arithmetically, before the model is consulted, is the whole
                        // point: a model asked to judge whether it has enough to say answers yes.
                        // See LlmNoteWriter.ThinInputWords for the calibration against the real Library.
                        var substantive = Llm.LlmNoteWriter.SubstantiveWords(meeting.Transcript);
                        if (substantive < Llm.LlmNoteWriter.ThinInputWords)
                        {
                            Log($"notes: thin input ({substantive} substantive words, floor is " +
                                $"{Llm.LlmNoteWriter.ThinInputWords}) - honest minimal note, model not consulted");
                            meeting.Notes = Llm.LlmNoteWriter.LowSubstanceNote();
                            Store.ReplaceNotes(meeting.Id, meeting.Notes);
                            Store.UpdateTitleAndBody(meeting.Id, meeting.Title, meeting.Body);
                            Storage.MarkdownMirror.Write(meeting);
                            LibraryWindow.MarkDirty();
                        }
                        // NEVER `return` from here. This lambda ends by advancing the notch out of
                        // Summarizing, and an early return strands the loader spinning forever over a
                        // note that was already written and saved (bug, 2026-09-04). Skipping the model
                        // is an `else`, not an exit.
                        else
                        {
                            // Map-reduce makes several short LLM calls for a long meeting; give it room,
                            // but stay bounded so a stuck generation can never hang the pill.
                            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(300));
                            // The TRANSCRIPT is the only input. The user's sticky note is deliberately not
                            // fed in (owner decision) - it sits beside the summary, it doesn't shape it.
                            // meeting.Transcript, not the local `transcript`: TagSpeakers tagged the former in
                            // place, so the writer sees the real names.
                            var llmNotes = LlmService.Instance
                                .WriteNotesAsync(meeting.Transcript, cts.Token, null, MeetingFacts(meeting, prepForNotes))
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
                    }
                    catch (Exception ex)
                    {
                        Log($"LLM notes failed (kept heuristic): {ex.Message}");
                        // Re-sync the in-memory record with the DB so later views/mirror writes cannot
                        // show a note the store never accepted.
                        if (meeting is not null && Store.Get(meeting.Id) is { } dbTruth) meeting = dbTruth;
                    }

                    // Speakers were resolved BEFORE the notes; now attribute the to-do OWNERS from the
                    // tagged transcript (needs the written action items, so it runs here). Guarded: the
                    // catch above can null `meeting` out, and this is the path that must never throw.
                    if (meeting is not null) InferOwners(meeting);

                    // Low-memory: drop the 2.5GB model and re-warm the small recognizer so idle Toki
                    // is light and the next meeting is ready to record.
                    if (LowMemoryMode)
                    {
                        LlmService.Instance.Release();
                        TranscriptionService.Instance.Warm();
                    }
                }
            }
            catch (Exception ex)
            {
                // Reaching here means a step outside every inner guard threw. The notes may already be
                // saved; what matters is that the finally still runs and the notch still advances.
                Log($"summarize pipeline failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
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
            }
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
                    // Live preview to the recording view (marshalled to the UI thread). When the fast
                    // streaming model is present it drives the preview (words as spoken + finalized lines);
                    // otherwise the Parakeet WINDOW's finalized segments do (the ~15-21s fallback).
                    if (session.Live is { } live)
                    {
                        if (live.StreamingPreview)
                        {
                            live.PreviewPartial += (sp, text) => Current?.Dispatcher.BeginInvoke(() => LivePreviewPartial?.Invoke(sp, text));
                            live.PreviewFinalized += s => Current?.Dispatcher.BeginInvoke(() => LivePreview?.Invoke(s));
                        }
                        else
                            live.SegmentFinalized += s => Current?.Dispatcher.BeginInvoke(() => LivePreview?.Invoke(s));
                    }
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
    int _pttMisses;   // consecutive empty captures - drives the escalating "didn't catch that" copy
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
            // becomes an endless loop. ESCALATE rather than repeat (research: never a 3rd identical
            // attempt): the first miss is a plain retry hint; a second in a row switches modes and
            // points at the likely cause (muted mic) + a type-it fallback. AskView also dedupes an
            // identical line, so even the same message never stacks.
            _pttMisses++;
            var msg = _pttMisses >= 2
                ? "Still not hearing you. Check your mic is unmuted and close to you, or open the Library to type your question."
                : "I didn't catch that - hold Ctrl + Alt + Space and try again.";
            Views.AskView.Instance?.Failed(msg);
            return;
        }
        _pttMisses = 0;
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
        // Text mutates IN PLACE every patch; the notch only RESIZES on a coarse throttle (~280ms, and
        // only ever grows while streaming) so the window does not flicker by re-morphing 14x a second
        // (research: hold the window, stream into a scrolling viewport, animate size on state changes).
        var flusher = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(70) };
        var sinceGrow = 0;
        flusher.Tick += (_, _) =>
        {
            string cur; lock (gate) cur = acc.ToString();
            Views.AskView.Instance?.SetAnswerText(StripMore(cur));
            if (++sinceGrow >= 4) { sinceGrow = 0; GrowNotch(); }
        };
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
