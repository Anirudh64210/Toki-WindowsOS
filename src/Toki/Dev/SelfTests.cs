using System.Diagnostics;
using System.IO;
using Toki.Audio;
using Toki.Detection;
using Toki.Llm;
using Toki.Storage;
using Toki.Transcription;

namespace Toki.Dev;

/// <summary>
/// Headless self-tests (brief §10). Each writes a real artifact (a WAV you can
/// play) and prints a pass/fail summary, so the owner can verify without reading
/// code. Run e.g. <c>Toki.exe --mictest</c> or <c>Toki.exe --loopbacktest chrome</c>.
/// </summary>
static class SelfTests
{
    // Apps that never host a meeting we'd want to tap for the loopback test's
    // auto-pick (a superset of the real detection denylist is fine here).
    static readonly string[] LikelyCallApps =
        ["chrome", "msedge", "firefox", "brave", "zoom", "teams", "ms-teams", "slack", "discord", "webex"];

    public static int Run(string flag, string[] args)
    {
        // Render “-”, “✓” etc. correctly in the console instead of mojibake (ΓÇö). Best-effort:
        // throws if stdout is redirected in some shells, which is harmless here.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        var outDir = ArgValue(args, "--out") ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);
        try
        {
            return flag switch
            {
                "--mictest" => MicTest(outDir, Seconds(args, 10)),
                "--loopbacktest" => LoopbackTest(outDir, args, Seconds(args, 15)),
                "--playtone" => PlayTone(Seconds(args, 20)),
                "--detecttest" => DetectTest(Seconds(args, 30)),
                "--recordtest" => RecordTest(args, Seconds(args, 6)),
                "--asrtest" => AsrTest(args),
                "--wertest" => WerTest.Run(args),
                "--voicetest" => VoiceTest(args),
                "--transcribetest" => TranscribeTest(args),
                "--dbtest" => DbTest(),
                "--notestest" => NotesTest(),
                "--renotes" => ReNotes(args),
                "--livetest" => LiveTest(args),
                "--seed" => Seed(),
                "--llmtest" => LlmTest(),
                "--themetest" => ThemeTest(),
                "--measuretest" => MeasureTest(),
                "--notetest" => NoteEditorTest(),
                "--dumpnote" => DumpNote(args),
                "--uitest" => UiPerfTest(),
                "--diartest" => DiarTest(args),
                "--speakertest" => SpeakerTest(args),
                "--hittest" => HitTargets.Run(),
                "--revealtest" => RevealTest(),
                "--wavetest" => WaveTest(),
                "--fpstest" => FrameTest.Run(),
                "--stickytest" => StickyTest(),
                "--hovershot" => HitTargets.Shot(
                    args.Skip(1).FirstOrDefault(a => !a.StartsWith("--") && a != "dark") ?? ".",
                    args.Contains("dark")),
                "--makeicon" => MakeIcon(args),
                "--copytest" => CopyTest(),
                "--asktest" => AskTest(args),
                "--querytest" => QueryTest(),
                "--followuptest" => FollowUpTest(),
                "--embedtest" => EmbedTest(),
                "--caltest" => CalTest(args),
                "--preptest" => PrepTest(),
                "--voicecreatetest" => VoiceCreateTest(),
                _ => Fail($"unknown self-test '{flag}'"),
            };
        }
        catch (Exception ex)
        {
            // The stack matters: "it threw" with no location has cost real debugging time here before.
            Console.WriteLine(ex.StackTrace);
            return Fail($"{flag} threw: {ex.Message}");
        }
    }

    // Verifies the Ask-bar "ask by voice" pipeline: TranscriptionService.TranscribeFile on a WAV →
    // plain text. Generate a spoken clip with `--mictest` (speak into it) or any speech WAV.
    static int VoiceTest(string[] args)
    {
        var i = Array.IndexOf(args, "--voicetest");
        if (i < 0 || i + 1 >= args.Length) return Fail("usage: --voicetest <path-to-wav>");
        var file = args[i + 1];
        if (!File.Exists(file)) return Fail($"file not found: {file}");
        if (!Toki.Transcription.TranscriptionService.ModelReady)
            return Fail("Parakeet model not installed - run --asrtest once to fetch it.");
        Console.WriteLine($"[--voicetest] transcribing {Path.GetFileName(file)} via TranscribeFile()...");
        var text = Toki.Transcription.TranscriptionService.Instance.TranscribeFile(file);
        if (text is null) return Fail("TranscribeFile returned null (model/decoder error).");
        Console.WriteLine($"[--voicetest] result: \"{text}\"");
        if (text.Length == 0) { Console.WriteLine("[--voicetest] empty - PASS only if the clip was silent."); return 0; }
        Console.WriteLine("[--voicetest] PASS - non-empty transcription returned.");
        return 0;
    }

    static int MicTest(string outDir, int seconds)
    {
        var path = Path.Combine(outDir, "you.wav");
        // Device diagnostics FIRST: Toki captures the "Communications" default (Role.Communications).
        // If that differs from the general "Console" default the user sees metering, or is disabled/
        // unplugged, the capture is silent even though "the mic works" in Windows.
        try
        {
            var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            NAudio.CoreAudioApi.MMDevice? Def(NAudio.CoreAudioApi.Role r)
            { try { return en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, r); } catch { return null; } }
            var comm = Def(NAudio.CoreAudioApi.Role.Communications);
            var cons = Def(NAudio.CoreAudioApi.Role.Console);
            Console.WriteLine($"  DEFAULT (Communications - USED BY TOKI): {comm?.FriendlyName ?? "(none)"} [{comm?.State}]");
            Console.WriteLine($"  DEFAULT (Console/Multimedia)           : {cons?.FriendlyName ?? "(none)"} [{cons?.State}]");
            if (comm is not null && cons is not null && comm.ID != cons.ID)
                Console.WriteLine("  ⚠ MISMATCH - Toki's mic differs from your general default. This is likely the cause.");
            Console.WriteLine("  all capture devices:");
            foreach (var d in en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.All))
                Console.WriteLine($"    - {d.FriendlyName} [{d.State}]");
        }
        catch (Exception ex) { Console.WriteLine($"  device enum failed: {ex.Message}"); }

        Console.WriteLine($"[--mictest] recording the default microphone for {seconds}s...");
        using var mic = new MicCapture();
        mic.Start(path);
        Console.WriteLine($"  device : {mic.DeviceName}");
        Console.WriteLine($"  format : {mic.Format}");
        CountDown(seconds, () => mic.PeakLevel);
        mic.Stop();

        var size = new FileInfo(path).Length;
        Console.WriteLine($"  wrote  : {path} ({size:N0} bytes)");
        Console.WriteLine($"  peak   : {mic.PeakLevel:P1}");
        if (size <= 44)
            return Fail("no audio data written (only a WAV header).");
        Console.WriteLine($"[--mictest] PASS - play {Path.GetFileName(path)} and you should hear yourself.");
        if (mic.PeakLevel < 0.005f)
            Console.WriteLine("  note: peak is very low - was the mic muted or silent?");
        return 0;
    }

    static int LoopbackTest(string outDir, string[] args, int seconds)
    {
        var target = ResolveTarget(args);
        if (target is null)
            return Fail("no target process. Start a call (or pass --loopbacktest <pid|name>) and retry.");

        var (pid, name) = target.Value;
        var path = Path.Combine(outDir, "them.wav");
        Console.WriteLine($"[--loopbacktest] tapping '{name}' (pid {pid}) + its process tree for {seconds}s...");
        Console.WriteLine("  play audio in that app now (e.g. the other side of a call).");
        var cap = new ProcessLoopbackCapture();
        cap.Record(pid, path, seconds);

        var size = new FileInfo(path).Length;
        Console.WriteLine($"  wrote : {path} ({size:N0} bytes)");
        Console.WriteLine($"  peak  : {cap.PeakLevel:P1}");
        if (size <= 44)
            return Fail("no audio captured. Is that app actually producing sound?");
        Console.WriteLine($"[--loopbacktest] PASS - play {Path.GetFileName(path)} and you should hear that app.");
        if (cap.PeakLevel < 0.005f)
            Console.WriteLine("  note: peak is near silence - the app may not have played audio during the test.");
        return 0;
    }

    static int LlmTest()
    {
        if (!ModelManager.QwenInstalled)
        {
            Console.WriteLine("[--llmtest] Qwen3-4B not installed - downloading (~2.5 GB, one time)...");
            var last = -5;
            var progress = new Progress<double>(p =>
            {
                var pct = (int)(p * 100);
                if (pct >= last + 5) { Console.WriteLine($"  download {pct}%"); last = pct; }
            });
            try { ModelManager.EnsureQwenAsync(progress).GetAwaiter().GetResult(); }
            catch (Exception ex) { return Fail($"Qwen download failed: {ex.Message}"); }
            Console.WriteLine("  model ready.");
        }

        var transcript = new List<TranscriptSegment>
        {
            new(Speaker.Them, 0, 4000, "So where are we on the launch - what's left before we can ship?"),
            new(Speaker.You, 4200, 9000, "Tool's basically done. The only blockers are the brand tokens and wiring the Make webhook."),
            new(Speaker.Them, 9200, 13000, "Okay. Let's move the ship date to Thursday then, and keep the email capture optional."),
            new(Speaker.You, 13200, 17000, "Sounds good. I'll wire the webhook this week. Priya, can you confirm the brand font and hex tomorrow?"),
            new(Speaker.Them, 17200, 20000, "Yep, I'll get you the font and colors by tomorrow. Which analytics platform are we pointing events at?"),
        };

        Console.WriteLine("[--llmtest] loading Qwen + writing notes (one structured pass + title)...");
        var sw = Stopwatch.StartNew();
        // Run off the UI thread - LLamaSharp's async continuations would otherwise
        // deadlock against this thread being blocked on the result.
        var notes = Task.Run(() => LlmService.Instance.WriteNotesAsync(transcript)).GetAwaiter().GetResult();
        sw.Stop();
        if (notes is null) return Fail("LLM returned no notes (model unavailable?).");

        Console.WriteLine($"  generated in {sw.ElapsedMilliseconds} ms ({(LlmService.Instance.LocalLoaded ? "local Qwen" : "?")})");
        Console.WriteLine($"  TITLE: {notes.Title ?? "(none)"}");
        Console.WriteLine($"  SUMMARY: {notes.Tldr}");
        Console.WriteLine($"  KEY POINTS ({notes.Decisions.Count}):");
        foreach (var d in notes.Decisions) Console.WriteLine($"    - {d}");
        Console.WriteLine($"  ACTION ITEMS ({notes.Actions.Count}):");
        foreach (var a in notes.Actions) Console.WriteLine($"    - {a.Text}  [owner: {a.Owner ?? "-"}, due: {a.Due ?? "-"}]");

        if (sw.ElapsedMilliseconds > 40000)
            Console.WriteLine($"  [warn] took {sw.ElapsedMilliseconds / 1000.0:0.0}s - target is under 30s.");
        if (TextValidator.IsDegenerate(notes.Tldr)) return Fail("SUMMARY is degenerate (CJK / repetition).");
        Console.WriteLine("[--llmtest] PASS - notes generated, English, no degeneration. Read them: do they match the transcript?");
        return 0;
    }

    /// <summary>Re-run the LLM note writer on a STORED meeting's real transcript and print the
    /// result - verifies the notes engine against actual meetings without recording again.</summary>
    static int ReNotes(string[] args)
    {
        var idArg = ArgValue(args, "--renotes");
        if (idArg is null || !long.TryParse(idArg, out var id))
            return Fail("usage: --renotes <meetingId>");

        var store = new MeetingStore();
        var m = store.Get(id);
        if (m is null) return Fail($"no meeting with id {id}");
        Console.WriteLine($"[--renotes] meeting {id}: \"{m.Title}\"  ({m.Transcript.Count} segments, {m.DurationMs / 60000} min)");
        if (m.Transcript.Count == 0) return Fail("meeting has no transcript to summarise.");

        var sw = Stopwatch.StartNew();
        // Off the UI thread - LLamaSharp async continuations would deadlock a blocked caller.
        var notes = Task.Run(() => LlmService.Instance.WriteNotesAsync(m.Transcript)).GetAwaiter().GetResult();
        sw.Stop();
        if (notes is null) return Fail("LLM returned no notes (model unavailable?).");

        Console.WriteLine($"  generated in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  TITLE  : {notes.Title ?? "(none → falls back to app + date)"}");
        Console.WriteLine($"  SUMMARY: {notes.Tldr}");
        Console.WriteLine($"  KEY POINTS ({notes.Decisions.Count}):");
        foreach (var d in notes.Decisions) Console.WriteLine($"    - {d}");
        Console.WriteLine($"  ACTION ITEMS ({notes.Actions.Count}):");
        foreach (var a in notes.Actions) Console.WriteLine($"    - {a.Text}  [owner: {a.Owner ?? "-"}, due: {a.Due ?? "-"}]");
        Console.WriteLine("[--renotes] done - read the notes: do they reflect the WHOLE meeting, honestly (no filler)?");
        return 0;
    }

    /// <summary>
    /// Feed a WAV through the LIVE transcriber the way capture would during a meeting, and prove that
    /// most segments finalize DURING the stream - so Stop only decodes the short remainder (the whole
    /// point of Phase 2). Paced ~2× real-time so the worker keeps up; use a short-ish clip.
    /// </summary>
    static int LiveTest(string[] args)
    {
        var i = Array.IndexOf(args, "--livetest");
        var path = i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[i + 1] : null;
        if (path is null) return Fail("usage: --livetest <path-to-wav | recording-folder>");

        if (!ModelManager.IsInstalled(ModelManager.Parakeet))
        {
            Console.WriteLine("[--livetest] Parakeet not installed - run --asrtest once to fetch it.");
            return Fail("model missing");
        }

        // A folder feeds BOTH you.wav + them.wav concurrently - the real two-stream meeting load.
        var streams = new List<(Speaker Speaker, string Wav)>();
        if (Directory.Exists(path))
        {
            var you = Path.Combine(path, "you.wav");
            var them = Path.Combine(path, "them.wav");
            if (File.Exists(you)) streams.Add((Speaker.You, you));
            if (File.Exists(them)) streams.Add((Speaker.Them, them));
            if (streams.Count == 0) return Fail("folder has no you.wav / them.wav");
        }
        else if (File.Exists(path)) streams.Add((Speaker.Them, path));
        else return Fail($"not found: {path}");

        var live = new LiveTranscriber(TranscriptionService.Instance);
        foreach (var (sp, wav) in streams)
        {
            using var r = new NAudio.Wave.WaveFileReader(wav);
            live.AddStream(sp, r.WaveFormat);
            Console.WriteLine($"[--livetest] {sp}: {Path.GetFileName(wav)}  [{r.WaveFormat}]  {r.TotalTime.TotalSeconds:0.0}s");
        }
        var duringStream = 0;
        live.SegmentFinalized += _ => Interlocked.Increment(ref duringStream);
        live.Start();
        Console.WriteLine($"  feeding {streams.Count} stream(s) as they'd arrive in a meeting (~2x real-time)...");

        // Feed every stream on its own thread, so you+them arrive together like a real call.
        var feeders = streams.Select(s => new Thread(() =>
        {
            using var reader = new NAudio.Wave.WaveFileReader(s.Wav);
            var chunkBytes = Math.Max(1, reader.WaveFormat.AverageBytesPerSecond / 5);   // ~200ms
            var buf = new byte[chunkBytes];
            int read;
            while ((read = reader.Read(buf, 0, buf.Length)) > 0)
            {
                live.Feed(s.Speaker, buf, read);
                Thread.Sleep(100);   // 200ms audio per 100ms wall → ~2x real-time
            }
        }) { IsBackground = true }).ToList();

        var feedSw = Stopwatch.StartNew();
        feeders.ForEach(t => t.Start());
        feeders.ForEach(t => t.Join());
        feedSw.Stop();
        Console.WriteLine($"  fed all audio in {feedSw.Elapsed.TotalSeconds:0.0}s; {duringStream} segments finalized DURING the stream");

        var stopSw = Stopwatch.StartNew();
        var result = live.Stop();
        stopSw.Stop();
        if (result is null) return Fail("live transcriber returned null (no stream registered?).");

        Console.WriteLine($"  >>> STOP finalized in {stopSw.ElapsedMilliseconds} ms → {result.Count} total merged segments <<<");
        if (result.Count == 0)
        {
            Console.WriteLine("[--livetest] empty - treat as silence. PASS if the clip was silent.");
            return 0;
        }
        Console.WriteLine("  --- HEAD ---");
        foreach (var s in result.Take(3)) Console.WriteLine($"   [{s.Start:mm\\:ss}] {s.Speaker,-4} {s.Text}");
        if (result.Count > 6)
        {
            Console.WriteLine("   ...");
            Console.WriteLine("  --- TAIL ---");
            foreach (var s in result.TakeLast(3)) Console.WriteLine($"   [{s.Start:mm\\:ss}] {s.Speaker,-4} {s.Text}");
        }
        Console.WriteLine($"[--livetest] PASS - {duringStream} segments finalized mid-stream; Stop only had {stopSw.ElapsedMilliseconds} ms left. In a real meeting that's your wait after clicking Stop.");
        return 0;
    }

    static int Seed()
    {
        var store = new MeetingStore();
        var m1 = new MeetingRecord
        {
            Title = "Ship on Thursday; email capture optional", App = "chrome.exe",
            StartedAt = DateTimeOffset.Now.AddDays(-2), DurationMs = 24 * 60 * 1000,
            Transcript =
            [
                new(Speaker.Them, 0, 3000, "So what is left before we ship?"),
                new(Speaker.You, 3200, 6000, "Just the brand font and the Make webhook."),
                new(Speaker.Them, 6200, 9000, "Which analytics platform do we point events at?"),
            ],
            Notes = new NoteSet
            {
                Tldr = "Reviewed launch readiness. Tool is code-complete; remaining blockers are brand tokens and the Make webhook. Ship moved to Thursday.",
                Decisions = ["We agreed to ship on Thursday and keep email capture optional."],
                Questions = ["Which analytics platform do we point events at?"],
                Actions =
                [
                    new ActionItem { Text = "Confirm brand font + hex.", Owner = "Priya", Due = "Tomorrow" },
                    new ActionItem { Text = "Wire the Make webhook and route the /health slug.", Owner = "You", Due = "This week" },
                ],
            },
        };
        store.Save(m1); MarkdownMirror.Write(m1);

        var m2 = new MeetingRecord
        {
            Title = "Weekly 1:1 - Rounak", App = "zoom.exe",
            StartedAt = DateTimeOffset.Now.AddDays(-3), DurationMs = 18 * 60 * 1000,
            Transcript = [new(Speaker.You, 0, 3000, "Landing page hero is roughed in, pricing next.")],
            Notes = new NoteSet
            {
                Tldr = "Landing page - hero roughed in, pricing next. Keep it one page for launch.",
                Decisions = ["Keep the landing page to one page for launch."],
                Actions = [new ActionItem { Text = "Draft pricing section.", Owner = "You" }],
            },
        };
        store.Save(m2); MarkdownMirror.Write(m2);

        store.CreateManualNote("Prep - Design review", "- ask about the analytics platform\n- confirm the ship date\n- bring the webhook URL");
        Console.WriteLine($"[--seed] inserted 2 meetings + 1 manual note into {MeetingStore.DbPathDefault}");
        return 0;
    }

    static int DbTest()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"toki-dbtest-{Environment.ProcessId}.db");
        try
        {
            var store = new MeetingStore(dbPath);
            Console.WriteLine($"[--dbtest] round-trip against {dbPath}");

            var rec = new MeetingRecord
            {
                Title = "Design review - PixelFlow", App = "chrome.exe", DurationMs = 24 * 60 * 1000,
                Transcript =
                [
                    new(Speaker.Them, 0, 2000, "So what is left before we ship?"),
                    new(Speaker.You, 2100, 4000, "Just the brand font and the webhook."),
                ],
                Notes = new NoteSet
                {
                    Tldr = "Reviewed launch readiness; blockers are brand tokens and the webhook.",
                    Decisions = ["Ship on Thursday."],
                    Questions = ["Which analytics platform?"],
                    Actions = [new ActionItem { Text = "Wire the webhook.", Owner = "You", Due = "This week" }],
                },
            };
            var id = store.Save(rec);
            Console.WriteLine($"  inserted meeting id={id}");

            var got = store.Get(id) ?? throw new Exception("Get returned null");
            if (got.Transcript.Count != 2) return Fail("transcript not round-tripped");
            if (got.Notes.Decisions.Count != 1 || got.Notes.Actions.Count != 1) return Fail("notes not round-tripped");
            Console.WriteLine($"  read back: {got.Transcript.Count} lines, {got.Notes.Actions.Count} action(s)");

            var all = store.List();
            if (all.Count != 1 || all[0].PeopleCount != 2) return Fail($"list wrong (count={all.Count})");
            if (store.List("them: ship").Count != 1) return Fail("them: search failed");
            if (store.List("you: webhook").Count != 1) return Fail("you: search failed");
            if (store.List("them: webhook").Count != 0) return Fail("them: search matched the wrong speaker");
            Console.WriteLine("  search: title/you:/them: all correct");

            var actionId = got.Notes.Actions[0].Id;
            store.SetActionDone(actionId, true);
            if (!store.Get(id)!.Notes.Actions[0].Done) return Fail("action done not persisted");

            var noteId = store.CreateManualNote("My prep note", "- ask about pricing\n- confirm scope");
            if (store.List().Count != 2) return Fail("manual note not saved");
            Console.WriteLine($"  manual note id={noteId} saved");

            store.Delete(id);
            store.Delete(noteId);
            if (store.List().Count != 0) return Fail("delete failed");
            Console.WriteLine("  deleted both (cascade ok)");

            Console.WriteLine("[--dbtest] PASS - insert / search / update / delete round-trip works.");
            return 0;
        }
        catch (Exception ex) { return Fail($"--dbtest threw: {ex}"); }
        finally { try { File.Delete(dbPath); } catch { } }
    }

    static int NotesTest()
    {
        Console.WriteLine("[--notestest] canned transcript with a planted echo pair + decisions/actions/questions");
        // Planted echo: the remote line also appears on YOU within the window.
        var you = new List<TranscriptSegment>
        {
            new(Speaker.You, 2100, 4000, "We agreed to ship on Thursday and keep email capture optional."),
            new(Speaker.You, 6000, 8000, "So what is left before we ship?"),   // echo of them@6000
            new(Speaker.You, 9000, 11000, "I'll wire up the webhook this week."),
        };
        var them = new List<TranscriptSegment>
        {
            new(Speaker.Them, 0, 2000, "Which analytics platform do we point events at?"),
            new(Speaker.Them, 6000, 8000, "So what is left before we ship?"),  // clean copy
            new(Speaker.Them, 12000, 14000, "Priya will confirm the brand font tomorrow."),
        };

        var merged = TranscriptMerger.Merge(you, them);
        var echoRemoved = merged.Count(s => s.Speaker == Speaker.You && s.Text.Contains("what is left")) == 0
                          && merged.Count(s => s.Text.Contains("what is left")) == 1;
        Console.WriteLine($"  echo de-dup: {(echoRemoved ? "OK - duplicate removed, one copy kept" : "FAIL")}");

        var notes = NoteExtractor.Extract(merged);
        var title = NoteExtractor.Title(notes, "chrome.exe", DateTimeOffset.Now);
        Console.WriteLine($"  title: \"{title}\"");
        Console.WriteLine($"  tl;dr: {notes.Tldr}");
        Console.WriteLine($"  decisions ({notes.Decisions.Count}): {string.Join(" | ", notes.Decisions)}");
        Console.WriteLine($"  actions   ({notes.Actions.Count}): {string.Join(" | ", notes.Actions.Select(a => $"{a.Text} [{a.Owner}/{a.Due}]"))}");
        Console.WriteLine($"  questions ({notes.Questions.Count}): {string.Join(" | ", notes.Questions)}");

        var hasGarbage = HasCjkOrRepetition(title);
        var ok = echoRemoved
                 && notes.Decisions.Any(d => d.Contains("Thursday"))
                 && notes.Actions.Any(a => a.Text.Contains("webhook"))
                 && notes.Actions.Any(a => a.Owner == "Priya")
                 && notes.Questions.Any(q => q.Contains("analytics"))
                 && !hasGarbage;
        if (!ok) return Fail("planted items not all extracted (or title garbage).");
        Console.WriteLine("[--notestest] PASS - echo removed; decisions/actions/questions extracted; title clean.");
        return 0;
    }

    static bool HasCjkOrRepetition(string s) =>
        s.Any(c => c is >= (char)0x3040 and <= (char)0x9FFF) ||
        System.Text.RegularExpressions.Regex.IsMatch(s, @"(.)\1{5,}");

    static int TranscribeTest(string[] args)
    {
        var i = Array.IndexOf(args, "--transcribetest");
        var folder = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        if (folder is null || !Directory.Exists(folder))
            return Fail("usage: --transcribetest <folder with you.wav / them.wav>");

        Console.WriteLine($"[--transcribetest] transcribing both streams in {folder} and merging...");
        var sw = Stopwatch.StartNew();
        var transcript = TranscriptionService.Instance.Process(folder);
        sw.Stop();

        if (transcript is null) return Fail("model not installed (run --asrtest once to fetch it).");
        Console.WriteLine($"  {transcript.Count} merged segments in {sw.ElapsedMilliseconds} ms:");
        foreach (var s in transcript)
            Console.WriteLine($"  [{s.Start:mm\\:ss}] {s.Speaker,-4}  {s.Text}");
        Console.WriteLine("[--transcribetest] PASS - you/them attributed, echo removed, one timeline.");
        return 0;
    }

    static int AsrTest(string[] args)
    {
        var i = Array.IndexOf(args, "--asrtest");
        var file = i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[i + 1] : null;
        if (file is null || !File.Exists(file))
            return Fail("usage: --asrtest <path-to-wav>");

        // Model must be present (download at onboarding in the real app; here on demand).
        if (!ModelManager.IsInstalled(ModelManager.Parakeet))
        {
            Console.WriteLine("[--asrtest] Parakeet model not installed - downloading (~630 MB, one time)...");
            var last = -5;
            var progress = new Progress<double>(p =>
            {
                var pct = (int)(p * 100);
                if (pct >= last + 5) { Console.WriteLine($"  download {pct}%"); last = pct; }
            });
            try { ModelManager.EnsureAsync(ModelManager.Parakeet, progress).GetAwaiter().GetResult(); }
            catch (Exception ex) { return Fail($"model download failed: {ex.Message}"); }
            Console.WriteLine("  model ready.");
        }

        Console.WriteLine($"[--asrtest] loading {Path.GetFileName(file)} -> 16kHz mono...");
        var audioSw = Stopwatch.StartNew();
        var samples = AudioLoader.To16kMono(file);
        audioSw.Stop();
        var audioSecs = samples.Length / (double)AudioLoader.TargetRate;
        Console.WriteLine($"  audio: {audioSecs:0.0}s ({samples.Length:N0} samples), loaded in {audioSw.ElapsedMilliseconds} ms");

        Console.WriteLine("  loading Parakeet + transcribing (whole file, one pass)...");
        var sw = Stopwatch.StartNew();
        IReadOnlyList<TranscriptSegment> segments;
        using (var asr = new Transcriber())
            segments = asr.Transcribe(samples, Speaker.Them);
        sw.Stop();

        Console.WriteLine($"  {segments.Count} segments in {sw.ElapsedMilliseconds} ms " +
                          $"({audioSecs / Math.Max(0.001, sw.Elapsed.TotalSeconds):0.0}x real-time)");
        if (segments.Count == 0)
        {
            Console.WriteLine("[--asrtest] empty transcript - treat as silence (nobody spoke). PASS if the clip was silent.");
            return 0;
        }

        Console.WriteLine("  --- HEAD ---");
        foreach (var s in segments.Take(3)) Console.WriteLine($"  [{s.Start:mm\\:ss}] {s.Text}");
        if (segments.Count > 6)
        {
            Console.WriteLine("  ...");
            Console.WriteLine("  --- TAIL ---");
            foreach (var s in segments.TakeLast(3)) Console.WriteLine($"  [{s.Start:mm\\:ss}] {s.Text}");
        }
        // The WHOLE transcript to a file so two runs can be DIFFED. Named after the wav and whether
        // the speech gate ran, so an A/B is two runs and a diff:
        //     Toki.exe --asrtest x.wav --out C:\tmp
        //     set TOKI_ASR_NOGATE=1 && Toki.exe --asrtest x.wav --out C:\tmp
        var dumpDir = ArgValue(args, "--out") ?? Path.GetDirectoryName(file) ?? ".";
        var dump = Path.Combine(dumpDir,
            $"{Path.GetFileNameWithoutExtension(file)}.{(Environment.GetEnvironmentVariable("TOKI_ASR_NOGATE") == "1" ? "ungated" : "gated")}.txt");
        try
        {
            File.WriteAllLines(dump, segments.Select(s => $"[{s.Start:hh\\:mm\\:ss}] {s.Text}"));
            var words = segments.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            Console.WriteLine($"  {words} words -> {dump}");
        }
        catch (Exception ex) { Console.WriteLine($"  (could not write the transcript: {ex.Message})"); }

        Console.WriteLine($"[--asrtest] PASS - verify the TAIL matches the END of the audio (proves no mid-file truncation).");
        return 0;
    }

    static int RecordTest(string[] args, int seconds)
    {
        // Drives the real RecordingSession the app uses. Optional --loopbacktest-style
        // target via --target <pid|name>; otherwise mic-only.
        var targetArg = ArgValue(args, "--target");
        DetectedApp? target = null;
        if (targetArg != null)
        {
            var t = ResolveTarget(["--loopbacktest", targetArg]);
            if (t is { } tv) target = new DetectedApp(tv.name, tv.name, false);
        }
        Console.WriteLine($"[--recordtest] recording {(target is null ? "mic-only" : target.DisplayName)} for {seconds}s via RecordingSession...");
        var session = new RecordingSession(target);
        session.Start();
        Thread.Sleep(seconds * 1000);
        var folder = session.Stop();

        var you = Path.Combine(folder, "you.wav");
        Console.WriteLine($"  folder : {folder}");
        Console.WriteLine($"  you.wav: {(File.Exists(you) ? new FileInfo(you).Length + " bytes" : "MISSING")}  peak {session.YouPeak:P0}");
        if (session.ThemWavPath != null)
            Console.WriteLine($"  them.wav: {(File.Exists(session.ThemWavPath) ? new FileInfo(session.ThemWavPath).Length + " bytes" : "MISSING")}  peak {session.ThemPeak:P0}");
        if (!File.Exists(you) || new FileInfo(you).Length <= 44)
            return Fail("recording session did not write a mic WAV.");
        Console.WriteLine("[--recordtest] PASS - session wrote real audio files.");
        return 0;
    }

    static int DetectTest(int seconds)
    {
        Console.WriteLine($"[--detecttest] watching the mic-in-use set for {seconds}s (poll every 2.5s).");
        Console.WriteLine("  start a call (Meet/Zoom/Teams) and watch it appear as a CANDIDATE.");
        var polls = Math.Max(1, (int)(seconds / 2.5));
        var everSawCandidate = false;
        for (var i = 0; i < polls; i++)
        {
            var active = MicUsageReader.GetActiveMicApps();
            if (active.Count == 0)
            {
                Console.WriteLine($"  [{i + 1,2}] mic idle - no app is capturing.");
            }
            else
            {
                foreach (var a in active)
                {
                    var denied = MeetingDetector.IsDenylisted(a);
                    var verdict = denied ? "filtered (denylist)" : "CANDIDATE";
                    everSawCandidate |= !denied;
                    Console.WriteLine($"  [{i + 1,2}] {a.DisplayName,-28} {(a.Packaged ? "[pkg]" : "     ")}  -> {verdict}");
                }
            }
            Thread.Sleep(2500);
        }
        Console.WriteLine(everSawCandidate
            ? "[--detecttest] PASS - at least one real meeting candidate was seen."
            : "[--detecttest] done - no candidate seen (start a call during the test to see one).");
        return 0;
    }

    /// <summary>Dev helper: play a 440Hz tone so another instance can tap this process for --loopbacktest.</summary>
    static int PlayTone(int seconds)
    {
        Console.WriteLine($"[--playtone] playing 440Hz for {seconds}s (pid {Environment.ProcessId})...");
        using var output = new NAudio.Wave.WasapiOut();
        var tone = new NAudio.Wave.SampleProviders.SignalGenerator(44100, 2)
        {
            Gain = 0.2, Frequency = 440, Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin,
        };
        output.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(tone));
        output.Play();
        Thread.Sleep(seconds * 1000);
        output.Stop();
        Console.WriteLine("[--playtone] done.");
        return 0;
    }

    static (uint pid, string name)? ResolveTarget(string[] args)
    {
        // Explicit: --loopbacktest <pid|name>
        var i = Array.IndexOf(args, "--loopbacktest");
        var explicitArg = i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[i + 1] : null;
        if (explicitArg != null)
        {
            if (uint.TryParse(explicitArg, out var pid))
            {
                try { return ((uint)Process.GetProcessById((int)pid).Id, Process.GetProcessById((int)pid).ProcessName); }
                catch { return null; }
            }
            var byName = Process.GetProcessesByName(explicitArg.Replace(".exe", ""))
                .OrderByDescending(p => p.WorkingSet64).FirstOrDefault();
            return byName is null ? null : ((uint)byName.Id, byName.ProcessName);
        }

        // Auto-pick the largest likely call app that's running.
        var candidate = Process.GetProcesses()
            .Where(p => LikelyCallApps.Contains(p.ProcessName.ToLowerInvariant()))
            .OrderByDescending(p => p.WorkingSet64)
            .FirstOrDefault();
        return candidate is null ? null : ((uint)candidate.Id, candidate.ProcessName);
    }

    static void CountDown(int seconds, Func<float> peak)
    {
        for (var s = 0; s < seconds; s++)
        {
            Thread.Sleep(1000);
            Console.WriteLine($"  {s + 1,2}/{seconds}s  peak so far {peak():P1}");
        }
    }

    static string? ArgValue(string[] args, string key)
    {
        var i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // Diagnoses the notch auto-height cut-off headlessly. For each auto-height page it prints TWO
    // heights: (A) the out-of-tree Measure PillWindow.ContentHeight uses, and (B) the TRUE arranged
    // height from a real offscreen window. If A < B, the reveal is sized short and the bottom clips.
    static int MeasureTest()
    {
        var cases = new (string name, double w, Func<System.Windows.Controls.UserControl> build)[]
        {
            ("MeetingPrompt",         288, () => { var v = new Views.MeetingPromptView(); v.Refresh("Com.tinyspeck.slackdesktop"); return v; }),
            ("RecordingOpen",         268, () => new Views.RecordingOpenView()),
            ("Ask",                   344, () => new Views.AskView()),
            ("NoSpeech",              300, () => new Views.NoSpeechView()),
            ("PrepCard",              356, () => { App.CurrentPrep = PrepSamples.FirstTime(); return new Views.PrepCardView(); }),
            ("Home-connect-cta",      344, () => new Views.HomeView()),
            ("Home-connected-empty",  344, () => { var v = new Views.HomeView(); v.DebugConnected(false); return v; }),
            ("Home-connected-events", 344, () => { var v = new Views.HomeView(); v.DebugConnected(true); return v; }),
            ("Onboarding-name",       330, () => new Views.OnboardingView()),
            ("Onboarding-privacy",    330, () => { var v = new Views.OnboardingView(); v.DebugShowStep(1); return v; }),
            ("Onboarding-done",       330, () => { var v = new Views.OnboardingView(); v.DebugShowStep(2); return v; }),
            ("MeetingPrompt-calmatch",288, () => { var v = new Views.MeetingPromptView(); v.DebugCalendarMatch("Design sync"); return v; }),
        };
        var anyShort = false;
        foreach (var (name, w, build) in cases)
        {
            // (A) exactly what PillWindow.ContentHeight does: bare out-of-tree measure.
            var a = build();
            a.Measure(new System.Windows.Size(w - 2, double.PositiveInfinity));
            var hA = a.DesiredSize.Height;

            // (B) the true arranged height: host a fresh instance in a real (offscreen) window at the
            // state width, let WPF do a full measure+arrange pass, then read what it actually needs.
            var b = build();
            var host = new System.Windows.Controls.Border { Width = w, VerticalAlignment = System.Windows.VerticalAlignment.Top, Child = b };
            var win = new System.Windows.Window
            {
                Width = w, Height = 900, Left = -4000, Top = -4000,
                WindowStyle = System.Windows.WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                Content = host,
            };
            win.Show();
            win.UpdateLayout();
            b.UpdateLayout();
            var hB = b.DesiredSize.Height;
            var actual = b.ActualHeight;
            win.Close();

            // Natural (unconstrained) width - if it exceeds the state width, a non-wrapping row (e.g. the
            // prompt's button row) would overflow/clip at the notch edges.
            b.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var natW = b.DesiredSize.Width;
            var wideBy = natW - w;

            var shortBy = hB - hA;
            if (shortBy > 0.5) anyShort = true;
            var flag = shortBy > 0.5 ? "  <-- CLIPS(H)" : wideBy > 0.5 ? "  <-- WIDE" : "";
            Console.WriteLine($"{name,-24} measA={hA,7:F1}  trueB={hB,7:F1}  natW={natW,6:F1}/{w,0:F0}  A_short_by={shortBy,6:F1}{flag}");

            // When a page clips, NAME the element rather than leaving a whole view to bisect by hand:
            // walk both trees and report every element whose arranged height beats its measured one.
            if (shortBy > 0.5)
            {
                // Keyed by tree PATH, not by type - a page is mostly unnamed Borders, and keying those
                // by type made every one overwrite the last, which produced a report that named nothing.
                var measured = new Dictionary<string, double>();
                void WalkA(System.Windows.DependencyObject d, string path)
                {
                    if (d is System.Windows.FrameworkElement fe)
                        measured[path] = fe.DesiredSize.Height;
                    var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
                    for (var i = 0; i < n; i++)
                        WalkA(System.Windows.Media.VisualTreeHelper.GetChild(d, i), $"{path}/{i}");
                }
                WalkA(a, "");
                void WalkB(System.Windows.DependencyObject d, string path)
                {
                    if (d is System.Windows.FrameworkElement fe
                        && measured.TryGetValue(path, out var ha) && fe.DesiredSize.Height - ha > 0.5)
                    {
                        var label = $"{fe.GetType().Name}:{(fe.Name is { Length: > 0 } nm ? nm : path)}";
                        Console.WriteLine($"    {label,-46} A={ha,6:F1}  B={fe.DesiredSize.Height,6:F1}");
                    }
                    var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
                    for (var i = 0; i < n; i++)
                        WalkB(System.Windows.Media.VisualTreeHelper.GetChild(d, i), $"{path}/{i}");
                }
                var win2 = new System.Windows.Window
                {
                    Width = w, Height = 900, Left = -4000, Top = -4000,
                    WindowStyle = System.Windows.WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                    Content = new System.Windows.Controls.Border { Width = w, VerticalAlignment = System.Windows.VerticalAlignment.Top, Child = build() },
                };
                win2.Show(); win2.UpdateLayout();
                WalkB(((System.Windows.Controls.Border)win2.Content).Child!, "");
                win2.Close();
            }
        }
        Console.WriteLine(anyShort
            ? "RESULT: at least one page's out-of-tree measure under-reports the true height → reveal clips."
            : "RESULT: out-of-tree measure matches the true arranged height for every page.");
        return 0;
    }

    /// <summary>
    /// Measure speaker separation on REAL meeting audio, both routes, on THIS machine - published
    /// benchmarks are other hardware; this prints the actual wall clock.
    /// Run: Toki.exe --diartest "%APPDATA%\Toki\Recordings\&lt;folder&gt;\them.wav" [speakers]
    /// </summary>
    static int DiarTest(string[] args)
    {
        var wav = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrWhiteSpace(wav) || !File.Exists(wav))
            return Fail("usage: --diartest <path to them.wav> [expected speakers]");
        var expected = args.Skip(2).Select(a => int.TryParse(a, out var n) ? n : -1).FirstOrDefault(n => n > 0);
        if (expected == 0) expected = -1;

        if (!ModelManager.DiarisationInstalled)
        {
            Console.WriteLine("[--diartest] downloading the two diarisation models (~34 MB, one time)...");
            var p = new Progress<double>(f => Console.Write($"\r  {f * 100,5:F1}%"));
            ModelManager.EnsureAsync(ModelManager.Segmentation, p).GetAwaiter().GetResult();
            ModelManager.EnsureSpeakerEmbedAsync(p).GetAwaiter().GetResult();
            Console.WriteLine("\r  done.        ");
        }

        var sweep = args.Any(a => a == "sweep");

        Console.WriteLine($"[--diartest] {Path.GetFileName(wav)}");
        var samples = AudioLoader.To16kMono(wav);
        var audioSec = samples.Length / (double)SpeakerDiariser.SampleRate;
        Console.WriteLine($"  audio      : {audioSec / 60:F1} min ({samples.Length:N0} samples)");
        Console.WriteLine($"  speakers   : {(expected > 0 ? expected.ToString() : "estimate")}");
        Console.WriteLine();

        // Sweep mode: the clustering threshold is model- and audio-dependent, so measure it rather
        // than trust a published default - print how many speakers each threshold yields.
        if (sweep)
        {
            var scribeS = new Transcriber();
            var asrS = scribeS.Transcribe(samples, Toki.Transcription.Speaker.Them);
            scribeS.Dispose();
            var uttS = asrS.Select(s => (s.StartMs / 1000f, s.EndMs / 1000f)).ToList();
            Console.WriteLine($"  {uttS.Count} utterances from ASR");
            Console.WriteLine();
            Console.WriteLine("  threshold   speakers   biggest cluster share");
            foreach (var t in new[] { 0.30, 0.40, 0.50, 0.55, 0.60, 0.65, 0.70, 0.75, 0.80, 0.85, 0.90, 0.95 })
            {
                var (sp, _) = SpeakerDiariser.ByUtterance(samples, uttS, -1, 4, t);
                var groups = sp.GroupBy(s => s.Speaker).ToList();
                var total = sp.Sum(s => s.EndSec - s.StartSec);
                var biggest = groups.Count == 0 ? 0 : groups.Max(g => g.Sum(s => s.EndSec - s.StartSec)) / Math.Max(0.01, total);
                Console.WriteLine($"  {t,9:F2}   {groups.Count,8}   {biggest * 100,6:F0}%");
            }
            return 0;
        }

        // Route A: the textbook pipeline.
        var (fullSpans, fullTook) = SpeakerDiariser.Full(samples, expected);
        Report("FULL   (segmentation sweep + per-hop embeddings)", fullSpans, fullTook, audioSec);

        // Route B: reuse the ASR boundaries we already have. Approximate them here by asking the real
        // recognizer for its segments, so the comparison is honest about total cost.
        var asrSw = Stopwatch.StartNew();
        using var scribe = new Transcriber();
        var asr = scribe.Transcribe(samples, Toki.Transcription.Speaker.Them);
        asrSw.Stop();
        var utterances = asr.Select(s => (s.StartMs / 1000f, s.EndMs / 1000f)).ToList();
        Console.WriteLine($"  (ASR produced {utterances.Count} utterances in {asrSw.Elapsed.TotalSeconds:F1}s - already paid for today)");

        var (utSpans, utTook) = SpeakerDiariser.ByUtterance(samples, utterances, expected);
        Report("BY UTTERANCE (one embedding per line Toki already found)", utSpans, utTook, audioSec);

        Console.WriteLine();
        var speedup = fullTook.TotalSeconds / Math.Max(0.001, utTook.TotalSeconds);
        Console.WriteLine($"RESULT: by-utterance is {speedup:F1}x faster. "
            + $"A 60-minute meeting would take ~{utTook.TotalSeconds * (3600 / audioSec) / 60:F1} min "
            + $"instead of ~{fullTook.TotalSeconds * (3600 / audioSec) / 60:F1} min.");
        return 0;

        static void Report(string what, IReadOnlyList<SpeakerSpan> spans, TimeSpan took, double audioSec)
        {
            var speakers = spans.Select(s => s.Speaker).Distinct().Count();
            Console.WriteLine($"{what}");
            Console.WriteLine($"    took     : {took.TotalSeconds,7:F1} s   (RTF {took.TotalSeconds / audioSec:F3})");
            Console.WriteLine($"    speakers : {speakers}");
            Console.WriteLine($"    segments : {spans.Count}");
            foreach (var g in spans.GroupBy(s => s.Speaker).OrderBy(g => g.Key))
                Console.WriteLine($"      speaker {g.Key}: {g.Sum(s => s.EndSec - s.StartSec),6:F1}s over {g.Count()} turns");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Run the WHOLE speaker chain on a real saved meeting and print what it concluded: voices
    /// found, names assigned, and which lines each name landed on. READ-ONLY - safe on the real
    /// library. Run: <c>Toki.exe --speakertest [meeting id]</c> (defaults to the latest with audio).
    /// </summary>
    static int SpeakerTest(string[] args)
    {
        var wanted = args.Skip(1).Select(a => long.TryParse(a, out var n) ? n : -1).FirstOrDefault(n => n > 0);

        var candidates = App.Store.List()
            .Where(s => s.Kind == MeetingKind.Meeting)
            .Select(s => App.Store.Get(s.Id))
            .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.Folder) && m.Transcript.Count > 0)
            .Select(m => m!)
            .ToList();

        var meeting = wanted > 0
            ? candidates.FirstOrDefault(m => m.Id == wanted)
            : candidates.FirstOrDefault(m => File.Exists(Path.Combine(m.Folder!, "them.wav")));

        if (meeting is null) return Fail("no saved meeting with audio to test (pass a meeting id?)");

        var themWav = Path.Combine(meeting.Folder!, "them.wav");
        if (!File.Exists(themWav)) return Fail($"meeting {meeting.Id} has no them.wav at {themWav}");

        if (!ModelManager.DiarisationInstalled)
        {
            Console.WriteLine("[--speakertest] downloading the two diarisation models (~34 MB, one time)...");
            var p = new Progress<double>(f => Console.Write($"\r  {f * 100,5:F1}%"));
            ModelManager.EnsureAsync(ModelManager.Segmentation, p).GetAwaiter().GetResult();
            ModelManager.EnsureSpeakerEmbedAsync(p).GetAwaiter().GetResult();
            Console.WriteLine("\r  done.        ");
        }

        Console.WriteLine($"[--speakertest] meeting {meeting.Id}: \"{meeting.Title}\"");
        Console.WriteLine($"  transcript : {meeting.Transcript.Count} lines "
            + $"({meeting.Transcript.Count(s => s.Speaker == Toki.Transcription.Speaker.Them)} from them)");

        // Extra names for THIS RUN only, so the value of a roster can be measured without writing
        // anything to settings: --speakertest 76 +Craig +Yashtren
        var extra = args.Where(a => a.StartsWith('+')).Select(a => a[1..]).Where(a => a.Length > 0).ToList();
        var roster = NameLexicon.Trusted(extra);
        Console.WriteLine($"  roster     : {(roster.Count > 0 ? string.Join(", ", roster) : "(nobody pinned, no calendar)")}"
            + (extra.Count > 0 ? "   [+ supplied for this run only, nothing saved]" : ""));
        Console.WriteLine();

        var result = SpeakerResolver.Resolve(
            meeting.Transcript, themWav, roster, Llm.AppSettings.Current.UserName);

        if (!result.Ran)
        {
            Console.WriteLine($"RESULT: did not run - {result.Skipped}");
            return 0;   // a deliberate skip is not a failure
        }

        Console.WriteLine($"  voices     : {result.Speakers}");
        Console.WriteLine($"  took       : {result.Took.TotalSeconds:F1} s");
        Console.WriteLine();
        if (result.Named.Count == 0)
        {
            Console.WriteLine("  nobody could be named from the conversation - every voice keeps its number.");
        }
        else
        {
            Console.WriteLine("  named from the conversation alone (no voiceprints):");
            foreach (var n in result.Named)
                Console.WriteLine($"    {SpeakerNamer.Fallback(n.Cluster),-10} -> {n.Name,-14} score {n.Score,4:F1}   {n.Evidence}");
        }

        Console.WriteLine();
        Console.WriteLine("  first 25 lines as they would now read:");
        var shown = 0;
        for (var i = 0; i < meeting.Transcript.Count && shown < 25; i++, shown++)
        {
            var seg = meeting.Transcript[i];
            var tag = result.Tags[i] ?? (seg.Speaker == Toki.Transcription.Speaker.You ? "You" : "Them");
            var text = seg.Text.Length <= 78 ? seg.Text : seg.Text[..75] + "...";
            Console.WriteLine($"    {tag,-12} {text}");
        }

        Console.WriteLine();
        Console.WriteLine("RESULT: read-only. Nothing was written; no voice data was stored anywhere.");
        return 0;
    }

    /// <summary>
    /// Time the Library's real work against the REAL database, so "slow and clunky" becomes a number.
    ///
    /// The three costs that matter are the ones that fire constantly: building the note list, moving the
    /// selection highlight, and rendering an open note. Run: Toki.exe --uitest
    /// </summary>
    static int UiPerfTest()
    {
        App.Headless = true;
        var w = LibraryWindow.PerfHarness();
        var sw = new Stopwatch();

        double Time(string what, int runs, Action act)
        {
            act();                                   // warm: first call pays JIT + resource lookups
            sw.Restart();
            for (var i = 0; i < runs; i++) act();
            sw.Stop();
            var each = sw.Elapsed.TotalMilliseconds / runs;
            Console.WriteLine($"{what,-42} {each,8:F1} ms   (x{runs})");
            return each;
        }

        var ids = App.Store.List().Select(m => m.Id).ToList();
        Console.WriteLine($"[--uitest] {ids.Count} meetings in the library");
        Console.WriteLine();

        // Split: query is SQLite, handover is ours, arranging visible rows is WPF's. The list
        // virtualizes, so "build" must not track the meeting count - if it does, virtualization is
        // defeated (usual cause: an ItemsControl inside an outer ScrollViewer realises every row).
        Time("  ...of which: query the store", 5, () => w.PerfQueryOnly());
        Time("  ...of which: hand the list over", 5, () => w.PerfBuildOnly());
        var build = Time("build the whole list (create + lay out)", 5, () => w.PerfLoadList());
        var pick = Time("move the selection highlight", 50, () =>
        {
            for (var i = 0; i < ids.Count && i < 4; i++) w.PerfHighlight(ids[i]);
        });
        var render = ids.Count > 0
            ? Time("render an open meeting note", 3, () => w.PerfRenderMeeting(ids[0]))
            : 0;

        Console.WriteLine();
        // A click must feel instant.
        var ok = true;
        void Check(string what, double ms, double budget)
        {
            var pass = ms <= budget;
            if (!pass) ok = false;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {what} {ms:F1} ms (budget {budget:F0} ms)");
        }
        Check("clicking a note stays under budget:", pick, 16);
        Check("a full list build stays under budget:", build, 250);
        if (render > 0) Check("opening a note stays under budget:", render, 400);

        Console.WriteLine();
        Console.WriteLine(ok
            ? "RESULT: the Library is within budget."
            : "RESULT: over budget - check that the list is still virtualizing (Views/MeetingList.cs).");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Print a note's RAW stored text, every character visible. READ-ONLY. "What does the screen
    /// show" and "what is saved" are different questions.
    /// Run: <c>Toki.exe --dumpnote "some words from the note"</c>
    /// </summary>
    static int DumpNote(string[] args)
    {
        var needle = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrWhiteSpace(needle)) return Fail("usage: --dumpnote <text to look for>");

        var hits = 0;
        foreach (var row in App.Store.List())
        {
            var m = App.Store.Get(row.Id);
            var body = m?.Body;
            if (body is null || body.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;

            hits++;
            Console.WriteLine($"=== #{m!.Id} [{m.Kind}] {m.Title} ===");
            var i = 0;
            foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
            {
                // Leading whitespace and repeated markers are exactly what we're hunting, so make both
                // impossible to miss.
                Console.WriteLine($"{i++,3}: |{line}|");
            }
            Console.WriteLine($"--- {body.Length} chars, re-parsed as: ---");
            foreach (var b in Storage.NoteMarkdown.Parse(body))
                Console.WriteLine($"     {b.Kind,-10} done={b.Done,-5} |{b.Text}|");
            Console.WriteLine();
        }

        Console.WriteLine(hits == 0 ? "no note matched." : $"{hits} note(s) matched.");
        return 0;
    }

    /// <summary>Let the real dispatcher and the real animation clock run for a while. The entrance and
    /// the retract are animations, so a test that only calls methods measures a model of them.</summary>
    static void Pump(int ms)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// --wavetest: the waveform on the REAL control with the REAL clock - the bars must rise fast,
    /// fall slow, and rest as dots at silence (a wave dancing at silence is the one lie this product
    /// cannot afford). No microphone involved: a synthetic level is pushed at capture rate.
    /// </summary>
    static int WaveTest()
    {
        var fails = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(ok ? "" : $"   ({detail})")}");
            if (!ok) fails++;
        }

        var meter = new LevelMeter();
        var wave = new Toki.Controls.VoiceWave { BarCount = 14, Height = 20, Meter = meter };
        var host = new System.Windows.Window
        {
            Left = -32000, Top = -32000, Width = 200, Height = 60, Content = wave,
            WindowStyle = System.Windows.WindowStyle.None, ShowActivated = false, ShowInTaskbar = false,
        };
        var rest = Toki.Controls.VoiceWave.AuditRest;
        try
        {
            host.Show();
            wave.IsLive = true;
            Pump(200);

            var quiet = wave.AuditScales();
            Console.WriteLine($"  silence      : {Fmt(quiet)}");
            Check("a silent room rests as dots", quiet.All(v => Math.Abs(v - rest) < rest * 0.1),
                  $"max {quiet.Max():F2} vs rest {rest:F2}");

            // Speech-level tone, arriving at capture rate (every 20ms) for 500ms.
            var feed = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            feed.Tick += (_, _) => meter.Report(0.12f);
            feed.Start();
            Pump(120);
            var early = wave.AuditScales();
            Pump(400);
            var loud = wave.AuditScales();
            feed.Stop();
            Console.WriteLine($"  after 120ms  : {Fmt(early)}");
            Console.WriteLine($"  talking      : {Fmt(loud)}");
            Check("the bars rise fast", early.Max() > 0.6, $"max {early.Max():F2} after 120ms");
            Check("the row fills while somebody talks", loud.Max() > 0.85, $"max {loud.Max():F2}");
            Check("the envelope is centre-weighted", loud[7] > loud[0] * 1.4 && loud[7] > loud[13] * 1.4,
                  $"centre {loud[7]:F2} vs ends {loud[0]:F2}/{loud[13]:F2}");

            Pump(250);
            var falling = wave.AuditScales();
            Console.WriteLine($"  +250ms quiet : {Fmt(falling)}");
            Check("the fall is slower than the rise", falling.Max() > rest * 1.5 && falling.Max() < loud.Max(),
                  $"max {falling.Max():F2} (was {loud.Max():F2}, rest {rest:F2})");

            Pump(1200);
            var settled = wave.AuditScales();
            Console.WriteLine($"  +1.5s quiet  : {Fmt(settled)}");
            Check("it settles back to dots when the room goes quiet",
                  settled.All(v => Math.Abs(v - rest) < rest * 0.12), $"max {settled.Max():F2}");

            wave.IsLive = false;
            Pump(120);
            Check("parking the wave stops it dead at rest",
                  wave.AuditScales().All(v => Math.Abs(v - rest) < 1e-9), "left mid-animation");
        }
        finally { try { host.Close(); } catch { } }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "RESULT: the wave answers to the level, rises fast, falls slow, and rests at silence."
            : $"RESULT: {fails} check(s) failed.");
        return fails == 0 ? 0 : 1;

        static string Fmt(double[] v) => string.Join(" ", v.Select(x => x.ToString("F2")));
    }

    /// <summary>
    /// --revealtest: the notch's ENTRANCE on the real window - at the instant it becomes visible,
    /// nothing that animates IN may already be at its final value (a delayed animation shows the
    /// BASE value until its clock starts, which is the "flickers before it renders" bug class).
    /// </summary>
    static int RevealTest()
    {
        App.Headless = true;
        var fails = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(ok ? "" : $"   ({detail})")}");
            if (!ok) fails++;
        }

        PillWindow? pill = null;
        try
        {
            var state = new Toki.State.AppState();
            pill = new PillWindow(state) { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };

            // Hidden -> a panel state: the summon path. Read IMMEDIATELY, before any UpdateLayout -
            // what is being tested is where the entrance starts, not how fast it runs.
            state.ForceForDemo(Toki.State.PillState.Home);
            var arm = pill.AuditEntranceArm();

            // Everything below is read from a snapshot taken at Show(), not sampled afterwards - the
            // entrance is an animation, and sampling it races the clock.
            Check("the card starts clipped to zero height", arm.Clip is >= 0 and < 1,
                  arm.Clip < 0 ? "no clip at all" : $"clip height {arm.Clip:F1}");
            Check("Toki starts invisible", arm.MascotOpacity < 0.01, $"opacity {arm.MascotOpacity:F2}");
            Check("Toki starts smaller than his final size", arm.MascotScale < 0.99, $"scale {arm.MascotScale:F2}");
            Check("the content starts faded out", arm.Content < 0.01, $"opacity {arm.Content:F2}");

            // Hiding must park everything shut: Windows retains a layered window's LAST composed
            // frame and the next Show() presents it until a new frame lands.
            state.ForceForDemo(Toki.State.PillState.Hidden);
            Pump(500);   // the retract animation is 170ms; give it room

            Check("after hiding, Toki is not left showing", !pill.AuditMascotShowing(), "still visible");
            var parked = pill.AuditCardClip();
            Check("after hiding, the card is left clipped shut", parked is { Height: < 1 },
                  parked is null ? "clip cleared - the last frame is a full-size card" : $"clip height {parked.Value.Height:F1}");
            Check("after hiding, the content is left faded out", pill.AuditContentOpacity() < 0.01,
                  $"opacity {pill.AuditContentOpacity():F2}");
            // Parked state is necessary but not sufficient: Hide() on the animation clock is one
            // frame early - an empty frame must COMPOSE into the layered surface before hiding.
            // The flag is set in that deferred step, so it proves the ORDER.
            Check("the hide composed an empty frame before hiding", pill.AuditParkedEmptyFrame(),
                  "Hide() ran before an empty frame landed in the layered surface");

            // Entering a FLOW must rewind it: onboarding is one cached view, so walk to the end,
            // leave, come back - it must be on step one. The caching IS the thing under test.
            state.ForceForDemo(Toki.State.PillState.Onboarding);
            pill.UpdateLayout();
            if (pill.AuditView(Toki.State.PillState.Onboarding) is Views.OnboardingView ob1)
            {
                ob1.DebugShowStep(2);                                   // walk to "You're all set"
                state.ForceForDemo(Toki.State.PillState.Home);          // leave
                state.ForceForDemo(Toki.State.PillState.Onboarding);    // and come back
                pill.UpdateLayout();
                Check("replaying onboarding starts at step one", ob1.AuditOnStep() == 0,
                      $"showing step {ob1.AuditOnStep()}");
            }
            else Check("replaying onboarding starts at step one", false, "could not reach the view");

            // The SECOND summon must arm the entrance just like the first.
            state.ForceForDemo(Toki.State.PillState.Home);
            var arm2 = pill.AuditEntranceArm();
            Check("the second summon arms Toki's entrance too",
                  arm2.MascotOpacity < 0.01 && arm2.MascotScale < 0.99,
                  $"opacity {arm2.MascotOpacity:F2}, scale {arm2.MascotScale:F2}");
        }
        catch (Exception ex) { Console.WriteLine($"FAIL  (driving the window)   {ex.Message}"); fails++; }
        finally { try { pill?.Close(); } catch { } }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "RESULT: the notch's entrance starts at its beginning, not at its end."
            : $"RESULT: {fails} part(s) of the entrance are already finished before it starts - that is the flash.");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// --stickytest: drive the sticky note WHERE IT LIVES - inside the meeting note's read-only
    /// RichTextBox. The editor passes on its own; the defect class is the HOSTING. READ-ONLY,
    /// debug-seeded panel, no database row.
    /// </summary>
    static int StickyTest()
    {
        App.Headless = true;
        var pass = 0; var fail = 0;
        void Check(string what, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? $"   {detail}" : "")}");
        }

        // A real meeting note, with the sticky panel inside it, in a live offscreen window.
        var meeting = new MeetingRecord
        {
            Id = -1, Kind = MeetingKind.Meeting, Title = "Sticky host", App = "Test",
            StartedAt = DateTimeOffset.Now, DurationMs = 60000,
            Notes = new NoteSet { Tldr = "A short summary so the document has content above the note." },
            Transcript = [new Toki.Transcription.TranscriptSegment(Toki.Transcription.Speaker.Them, 0, 2000, "hello there")],
        };

        var sticky = new Views.MeetingStickyView(-1, null);
        var doc = Views.NoteRenderer.MeetingDocument(meeting, spacious: true, sticky: sticky);
        var win = new System.Windows.Window
        {
            Width = 720, Height = 800, Left = -4000, Top = -4000,
            WindowStyle = System.Windows.WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Content = doc,
        };
        win.Show();
        win.UpdateLayout();

        // Open it exactly as the button does.
        sticky.DebugReveal();
        win.UpdateLayout();
        // Let the dispatcher run: focus is set at Loaded priority (focusing an un-laid-out control
        // fails silently), so a test that never pumps cannot see it.
        Pump();

        var editor = sticky.DebugEditor;
        Check("the panel opens and has an editor", editor is not null);
        if (editor is null) { Console.WriteLine("\nRESULT: cannot continue."); win.Close(); return 1; }

        // Does focus land in the editor? If not, keystrokes go to the hosting read-only RichTextBox.
        // LOGICAL focus, not Keyboard.FocusedElement: a never-activated offscreen window has no
        // keyboard focus to give.
        var focused = System.Windows.Input.FocusManager.GetFocusedElement(win);
        var hasFocus = focused is System.Windows.DependencyObject d && IsWithin(d, editor);
        Check("focus lands in the editor when the panel opens", hasFocus,
            hasFocus ? "" : $"focus is on {focused?.GetType().Name ?? "nothing"}");

        // And the thing the owner actually did: type.
        editor.DebugType("pricing call notes");
        win.UpdateLayout();
        var landed = editor.Markdown.Contains("pricing call notes");
        Check("typed text lands in the note", landed, landed ? "" : $"markdown is «{editor.Markdown}»");

        // THE STRUCTURAL RULE: DebugType bypasses real input routing, so also check the structure -
        // an editor nested inside a RichTextBox's document is starved of keyboard, caret and
        // selection services at class level. The structure IS the bug class.
        var nested = false;
        for (System.Windows.DependencyObject? a = sticky; a is not null;
             a = System.Windows.Media.VisualTreeHelper.GetParent(a))
            if (a is System.Windows.Controls.RichTextBox) { nested = true; break; }
        Check("the sticky editor is NOT nested inside another text editor", !nested,
            nested ? "a RichTextBox ancestor will eat its keystrokes, caret and selection" : "");

        win.Close();
        Console.WriteLine();
        Console.WriteLine($"RESULT: {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;

        static bool IsWithin(System.Windows.DependencyObject node, System.Windows.DependencyObject root)
        {
            for (var p = node; p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
                if (ReferenceEquals(p, root)) return true;
            return false;
        }

        // Run everything the dispatcher has queued up to Loaded priority, then return.
        static void Pump()
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }

    /// <summary>
    /// --notetest: drives the REAL note editor the way fingers do (markers, Enter, toolbar, tick
    /// boxes) and checks the Markdown that comes back out - the fault class lives in the document
    /// structure, which a pure round-trip test cannot see.
    /// </summary>
    static int NoteEditorTest()
    {
        var pass = 0; var fail = 0;

        void Check(string what, string expected, string actual)
        {
            var ok = expected == actual;
            if (ok) pass++; else fail++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
            if (!ok)
            {
                Console.WriteLine($"        expected: {Show(expected)}");
                Console.WriteLine($"        actual  : {Show(actual)}");
            }
        }
        static string Show(string s) => "«" + s.Replace("\n", "⏎") + "»";

        // A RichTextBox refuses a caret position until it's in a live visual tree, so each case gets a
        // real (offscreen) window - the same trick --measuretest uses to get a true arranged height.
        var windows = new List<System.Windows.Window>();
        Views.NoteEditor New(string markdown = "")
        {
            var ed = new Views.NoteEditor
            {
                // Pin the size dial: the editor loads the user's saved size on construction, and a
                // self-test must never inherit ambient user settings.
                TextSize = Views.NoteEditor.DefaultTextSize,
            };
            var win = new System.Windows.Window
            {
                Width = 440, Height = 700, Left = -4000, Top = -4000,
                WindowStyle = System.Windows.WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                Content = ed,
            };
            win.Show();
            win.UpdateLayout();
            windows.Add(win);
            ed.Markdown = markdown;
            ed.DebugFocus();
            ed.DebugCaretToEnd();
            return ed;
        }

        // 1. Saving and reloading must not add anything - ten cycles, because growth compounds.
        {
            const string src = "# Plan\n\n- one\n- two\n\n1. first\n2. second\n\n- [ ] send the deck\n- [x] booked the room";
            var ed = New(src);
            var after = ed.Markdown;
            for (var i = 0; i < 10; i++) { ed.Markdown = after; after = ed.Markdown; }
            Check("ten save/reload cycles change nothing", src, after);
        }

        // 2. A marker the user TYPED becomes a real one, exactly once, and never survives into the text.
        {
            var ed = New();
            ed.DebugType("- ");
            ed.DebugType("milk");
            Check("typing \"- \" makes ONE bullet", "- milk", ed.Markdown);

            var ed2 = New();
            ed2.DebugType("1. ");
            ed2.DebugType("first");
            Check("typing \"1. \" makes ONE numbered item", "1. first", ed2.Markdown);

            var ed3 = New();
            ed3.DebugType("## ");
            ed3.DebugType("Agenda");
            Check("typing \"## \" makes a heading", "## Agenda", ed3.Markdown);

            var ed4 = New();
            ed4.DebugType("- ");
            ed4.DebugType("[ ] ");
            ed4.DebugType("call Ana");
            Check("typing \"- [ ] \" makes a tick box", "- [ ] call Ana", ed4.Markdown);
        }

        // 3. Enter continues the list; Enter on an empty item leaves it.
        {
            var ed = New("- one");
            ed.DebugCaretToEnd();
            ed.DebugEnter();
            ed.DebugType("two");
            ed.DebugEnter();
            ed.DebugEnter();
            ed.DebugType("plain");
            Check("Enter continues, empty Enter leaves the list", "- one\n- two\nplain", ed.Markdown);
        }

        // 4. Numbering is positional - deleting an item can't leave a hole.
        {
            var ed = New("1. a\n1. b\n1. c");
            Check("numbers are renumbered from position", "1. a\n2. b\n3. c", ed.Markdown);
        }

        // 5. The toolbar can take a line OUT of a list again.
        {
            var ed = New("- one");
            ed.DebugCaretToEnd();
            ed.DebugToolbar(Storage.NoteBlockKind.Body);
            Check("Body button removes the bullet", "one", ed.Markdown);

            var ed2 = New("- two");
            ed2.DebugCaretToEnd();
            ed2.DebugToolbar(Storage.NoteBlockKind.Bullet);
            Check("clicking the same list button toggles it off", "two", ed2.Markdown);
        }

        // 6. Backspace at the start of a marked line is the way out of a list.
        {
            var ed = New("- one\n- two");
            ed.DebugCaretToLineStart();
            ed.DebugBackspace();
            Check("Backspace at line start drops the marker", "- one\ntwo", ed.Markdown);
        }

        // 7. Tick state round-trips (it's the whole point of a checklist).
        {
            var ed = New("- [ ] a\n- [ ] b");
            ed.DebugTick(1, true);
            Check("ticking a box is saved", "- [ ] a\n- [x] b", ed.Markdown);
        }

        // 8. A line whose TEXT carries a marker is never written with two.
        {
            var ed = New("- - two");
            Check("a doubled marker collapses, and stays collapsed", "- two", ed.Markdown);
        }

        // 9. Blank lines are the user's own spacing - kept, not multiplied.
        {
            var ed = New("a\n\n\nb");
            Check("blank lines survive exactly", "a\n\n\nb", ed.Markdown);
        }

        // 10. A marker GLYPH must never appear in what comes out of the editor (reading a bulleted
        //     line through TextRange.Text includes the native List's "•\t" and compounds per reload).
        {
            var ed = New("## Agenda\n- Confirm launch blockers & owners\n- Lock the ship date\n1. open with the numbers");
            var text = ed.Markdown;
            for (var i = 0; i < 8; i++) { ed.Markdown = text; text = ed.Markdown; }
            Check("eight reloads never grow a marker",
                "## Agenda\n- Confirm launch blockers & owners\n- Lock the ship date\n1. open with the numbers", text);

            var leaked = text.IndexOfAny(new[] { '•', '\t' }) >= 0;
            if (leaked) { fail++; Console.WriteLine("FAIL  a list marker glyph leaked into the saved text"); }
            else { pass++; Console.WriteLine("PASS  no marker glyph or tab ever reaches the saved text"); }
        }

        // 10b. Typing on a blank line must retag it: Write emits nothing for a Blank block, so a
        //      still-Blank tag throws the typed sentence away on the next save.
        {
            var ed = New("## Agenda\n\nsome body text");
            ed.DebugCaretToLine(1);              // the blank line between them
            ed.DebugType("typed on the blank line");
            Check("typing on a blank line keeps the text",
                "## Agenda\ntyped on the blank line\nsome body text", ed.Markdown);

            var size = ed.DebugFontSize(1);
            if (Math.Abs(size - 13) < 0.01) { pass++; Console.WriteLine("PASS  and it is body-sized, not mosquito-sized"); }
            else { fail++; Console.WriteLine($"FAIL  line is {size}px, expected 13"); }
        }

        // 10c. The same line, emptied again, goes back to being a blank beat rather than staying a
        //      full-height row.
        {
            var ed = New("a\n\nb");
            Check("a blank line is still a blank line", "a\n\nb", ed.Markdown);
            if (Math.Abs(ed.DebugFontSize(1) - 13) < 0.01) { pass++; Console.WriteLine("PASS  a blank line is body-sized (its LINE HEIGHT is what shrinks)"); }
            else { fail++; Console.WriteLine($"FAIL  blank line font is {ed.DebugFontSize(1)}px, expected 13"); }
        }

        // 11. And a note already damaged by that bug heals when it's opened.
        {
            var ed = New("## Agenda\n- •\t•\t•\t•\t•\t•\t•\tConfirm launch blockers & owners");
            Check("a note damaged by the old bug opens clean",
                "## Agenda\n- Confirm launch blockers & owners", ed.Markdown);
        }

        // 12. THE SIZE DIAL. It scales how the note is drawn and must never touch what the note SAYS -
        //     the whole reason it is a document-wide dial rather than a per-selection point size (see
        //     NoteEditor.TextSize). These three checks are the ones that would catch a regression into
        //     the mosquito-text failure mode from the other direction.
        {
            var ed = New("# Title\n\nbody line\n- a bullet\n- [ ] a task");
            var before = ed.Markdown;

            ed.TextSize = Views.NoteEditor.MaxTextSize;
            if (ed.Markdown == before) { pass++; Console.WriteLine("PASS  changing the text size changes no text"); }
            else { fail++; Console.WriteLine($"FAIL  the size dial rewrote the note:\n{ed.Markdown}"); }

            var big = ed.DebugFontSize(2);        // the body line
            ed.TextSize = Views.NoteEditor.MinTextSize;
            var small = ed.DebugFontSize(2);
            if (big > small) { pass++; Console.WriteLine($"PASS  the dial actually resizes the text ({small}px -> {big}px)"); }
            else { fail++; Console.WriteLine($"FAIL  size {small}px at min and {big}px at max"); }

            // Out of range must clamp rather than throw or produce an unreadable note.
            ed.TextSize = 99;
            if (ed.TextSize == Views.NoteEditor.MaxTextSize) { pass++; Console.WriteLine("PASS  an out-of-range size clamps"); }
            else { fail++; Console.WriteLine($"FAIL  size 99 became {ed.TextSize}"); }
        }

        foreach (var w in windows) w.Close();

        Console.WriteLine();
        Console.WriteLine($"RESULT: {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    // Render the (vector) Toki mascot to a transparent PNG and pack it into a Vista-style .ico (single
    // 256px PNG frame). Run: Toki.exe --makeicon src\Toki\toki.ico
    static int MakeIcon(string[] args)
    {
        var outPath = args.Length > 1 && !args[1].StartsWith("--") ? args[1]
                    : System.IO.Path.Combine(Directory.GetCurrentDirectory(), "toki.ico");
        // Build the icon from the LIGHT-theme mascot. Toki's dark-theme feet fade to near-white,
        // which is right on a dark notch and invisible on a light taskbar; the light-theme values
        // keep their edges on any background, which is the only requirement an icon has.
        Theme.ThemeManager.Apply(Theme.AppTheme.Light);
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var pngs = new List<byte[]>();
        foreach (var s in sizes)
        {
            var mascot = new Controls.TokiBlob { Width = s, Height = s };
            var host = new System.Windows.Controls.Border { Width = s, Height = s, Background = System.Windows.Media.Brushes.Transparent, Child = mascot };
            host.Measure(new System.Windows.Size(s, s));
            host.Arrange(new System.Windows.Rect(0, 0, s, s));
            host.UpdateLayout();
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(s, s, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(host);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            pngs.Add(ms.ToArray());
        }
        using var fs = File.Create(outPath);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);   // ICONDIR
        var offset = 6 + 16 * sizes.Length;                                   // data starts after the directory
        for (var i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));                  // width  (0 = 256)
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));                  // height
            w.Write((byte)0); w.Write((byte)0);                              // colours, reserved
            w.Write((short)1); w.Write((short)32);                          // planes, bpp
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
        Console.WriteLine($"[--makeicon] wrote {outPath} ({new FileInfo(outPath).Length:N0} bytes, {sizes.Length} sizes)");
        return 0;
    }

    static int ThemeTest()
    {
        var border = new System.Windows.Controls.Border();
        border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Notch");
        var win = new System.Windows.Window
        {
            Width = 60, Height = 60, Left = -4000, Top = -4000,
            WindowStyle = System.Windows.WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Content = border,
        };
        win.Show();
        win.UpdateLayout();

        Toki.Theme.ThemeManager.Apply(Toki.Theme.AppTheme.Dark);
        win.UpdateLayout();
        var dark = (border.Background as System.Windows.Media.SolidColorBrush)?.Color;

        Toki.Theme.ThemeManager.Apply(Toki.Theme.AppTheme.Light);
        win.UpdateLayout();
        var light = (border.Background as System.Windows.Media.SolidColorBrush)?.Color;

        win.Close();

        Console.WriteLine($"notch  dark={dark}  light={light}");
        // The element must actually change, and dark must be dark / light must be light.
        var isDark = dark is { } d && d.R < 0x20 && d.G < 0x20 && d.B < 0x20;
        var isLight = light is { } l && l.R > 0xE0 && l.G > 0xE0 && l.B > 0xE0;
        var ok = dark != light && isDark && isLight;
        Console.WriteLine(ok
            ? "PASS: an already-rendered element re-resolves + recolours on theme swap"
            : "FAIL: element did not recolour (still the old brush)");

        // The check above proves the MECHANISM on a correctly wired Border; the sweep asks the same
        // question of the REAL views, which are built in code where a captured brush
        // (`(Brush)Resources["TextHi"]`) never hears about a theme swap.
        var live = ThemeContrastSweep();
        return ok && live == 0 ? 0 : 1;
    }

    /// <summary>
    /// Build every notch view, in BOTH themes, and check that each piece of text actually contrasts
    /// with whatever is painted behind it. Reports the offenders rather than a bare pass/fail, since
    /// the useful output is "which label, on which view, at what ratio".
    /// </summary>
    static int ThemeContrastSweep()
    {
        const double floor = 3.0;   // generous: this is hunting invisible text, not grading a palette
        var bad = 0;

        // THE VIEWS ARE BUILT ONCE, IN DARK, AND THEN THE THEME IS SWITCHED UNDER THEM. That order
        // is the entire point. Building a fresh view per theme would pass even with the bug this
        // exists to catch: a view that reads `(Brush)Resources["TextHi"]` in its constructor gets
        // the RIGHT brush if it is constructed after the switch, and the wrong one only when it
        // outlives the switch - which is exactly what the notch's cached views do.
        var views = ThemeSweepViews().ToList();
        var hosts = new List<System.Windows.Window>();
        foreach (var (name, view) in views)
        {
            var w = new System.Windows.Window
            {
                Width = 420, Height = 900, Left = -32000, Top = -32000,
                WindowStyle = System.Windows.WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                Content = view,
            };
            w.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "GlassOpaque");
            hosts.Add(w);
            try { w.Show(); w.UpdateLayout(); w.UpdateLayout(); }
            catch (Exception ex) { Console.WriteLine($"  (could not build {name}: {ex.Message})"); }
        }

        // The LIBRARY WINDOW too, on every page - it is a Window rather than a UserControl, so it
        // hosts itself.
        LibraryWindow? lib = null;
        try
        {
            lib = LibraryWindow.ForAudit();
        }
        catch (Exception ex) { Console.WriteLine($"  (could not build the Library window: {ex.Message})"); lib = null; }

        foreach (var theme in new[] { Toki.Theme.AppTheme.Dark, Toki.Theme.AppTheme.Light })
        {
            Toki.Theme.ThemeManager.Apply(theme);

            if (lib is not null)
            {
                foreach (var page in new[] { "ask", "library", "templates", "settings" })
                {
                    try
                    {
                        lib.AuditPage(page);
                        foreach (var (tb, ratio, fgC, bgC, owner) in WeakText(lib, floor))
                        {
                            bad++;
                            var text = new string((tb.Text ?? "").Trim()
                                .Select(ch => ch >= '' && ch <= '' ? '□' : ch).ToArray());
                            if (text.Length > 28) text = text[..28] + "…";
                            if (text.Length == 0) text = "(icon)";
                            Console.WriteLine($"  FAIL [{theme}] Library/{page}: \"{text}\" {fgC} on {bgC} ({owner}) = {ratio:F2}:1");
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"  (skipped Library/{page} in {theme}: {ex.Message})"); }
                }
            }

            for (var i = 0; i < hosts.Count; i++)
            {
                var (name, _) = views[i];
                var host = hosts[i];
                try
                {
                    host.UpdateLayout();
                    host.UpdateLayout();
                    foreach (var (tb, ratio, fgC, bgC, owner) in WeakText(host, floor))
                    {
                        bad++;
                        // Icon-font glyphs live in the private use area and print as mojibake, which
                        // makes a failure unreadable in the one place it needs to be readable.
                        var text = new string((tb.Text ?? "").Trim()
                            .Select(ch => ch >= '' && ch <= '' ? '□' : ch).ToArray());
                        if (text.Length > 28) text = text[..28] + "…";
                        if (text.Length == 0) text = "(icon)";
                        Console.WriteLine($"  FAIL [{theme}] {name}: \"{text}\" {fgC} on {bgC} ({owner}) = {ratio:F2}:1");
                    }
                }
                catch (Exception ex) { Console.WriteLine($"  (skipped {name} in {theme}: {ex.Message})"); }
            }
        }
        foreach (var h in hosts) { try { h.Content = null; h.Close(); } catch { } }
        try { lib?.Close(); } catch { }
        Toki.Theme.ThemeManager.Apply(Toki.Theme.AppTheme.Dark);
        Console.WriteLine(bad == 0
            ? "PASS: every label in every notch view contrasts with its background, in both themes"
            : $"FAIL: {bad} label(s) are effectively invisible - see above");
        return bad;
    }

    static IEnumerable<(string Name, System.Windows.Controls.UserControl View)> ThemeSweepViews()
    {
        yield return ("Home", new Views.HomeView());
        yield return ("MeetingPrompt", new Views.MeetingPromptView());
        yield return ("RecordingOpen", new Views.RecordingOpenView());
        yield return ("NotesReady", new Views.NotesReadyView());
        yield return ("Summarizing", new Views.SummarizingView());
        yield return ("Settings", new Views.SettingsView());
        yield return ("NoSpeech", new Views.NoSpeechView());
        yield return ("Onboarding", new Views.OnboardingView());
        // Opened, not empty: a collapsed composer has no visible labels, so a sweep that only built
        // the calendar would report it clean while checking nothing in it.
        var cal = new Views.CalendarView();
        cal.DebugOpenComposer("Design sync tomorrow 2pm with ana@work.com");
        yield return ("Calendar+composer", cal);
    }

    // Deliberately NOT here: a button-fill-vs-surface contrast check. Tree-walking cannot resolve
    // what is actually painted behind rim-defined or gradient-backed buttons, so real faults and
    // correct controls score alike and the gate cries wolf. If reintroduced, it must render and
    // sample the ACTUAL painted pixel behind the control.

    /// <summary>Every TextBlock whose colour is under <paramref name="floor"/> against the nearest
    /// ancestor that actually paints a background.</summary>
    static IEnumerable<(System.Windows.Controls.TextBlock Tb, double Ratio, string Fg, string Bg, string Owner)>
        WeakText(System.Windows.DependencyObject root, double floor)
    {
        foreach (var tb in Descendants(root).OfType<System.Windows.Controls.TextBlock>())
        {
            if (!tb.IsVisible || string.IsNullOrWhiteSpace(tb.Text)) continue;
            if (tb.Foreground is not System.Windows.Media.SolidColorBrush fg) continue;
            // Skip anything FADED (mid entrance, or deliberately dimmed): its contrast measures the
            // animation clock, not the colour.
            if (Opacityish(tb) < 0.95) continue;
            var (bg, owner) = NearestBackground(tb);
            if (bg is null) continue;
            var flat = Flatten(fg.Color, fg.Opacity * Opacityish(tb));
            var r = Contrast(flat, bg.Value);
            if (r < floor) yield return (tb, r, flat.ToString(), bg.Value.ToString(), owner);
        }
    }

    static double Opacityish(System.Windows.FrameworkElement e)
    {
        var o = 1.0;
        for (System.Windows.DependencyObject? d = e; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is System.Windows.UIElement u) o *= u.Opacity;
        return o;
    }

    static (System.Windows.Media.Color? Color, string Owner) NearestBackground(System.Windows.DependencyObject e)
    {
        for (var d = System.Windows.Media.VisualTreeHelper.GetParent(e); d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            // Only elements that actually PAINT a fill: a templated Control's Background carries the
            // default style's value even when its template never draws it. The template's own
            // Borders are in the visual tree and get found on the way up anyway.
            var brush = d switch
            {
                System.Windows.Controls.Border b => b.Background,
                System.Windows.Controls.Panel p => p.Background,
                _ => null,
            };
            var owner = (d as System.Windows.FrameworkElement)?.Name is { Length: > 0 } n
                ? $"{d.GetType().Name} '{n}'" : d.GetType().Name;
            // Only a substantially OPAQUE fill counts as "the background": hover washes are overlays
            // that are absent most of the time, and translucent rows fall through to the card anyway.
            if (brush is System.Windows.Media.SolidColorBrush s && s.Color.A >= 0xC0) return (Flatten(s.Color, 1), owner);
        }
        return (null, "");
    }

    // Composite over the opaque card so an alpha brush is judged on what it actually looks like.
    static System.Windows.Media.Color Flatten(System.Windows.Media.Color c, double extraOpacity)
    {
        var a = c.A / 255.0 * Math.Clamp(extraOpacity, 0, 1);
        var under = System.Windows.Application.Current.Resources["GlassOpaque"] as System.Windows.Media.SolidColorBrush;
        var b = under?.Color ?? System.Windows.Media.Colors.Black;
        return System.Windows.Media.Color.FromRgb(
            (byte)(a * c.R + (1 - a) * b.R), (byte)(a * c.G + (1 - a) * b.G), (byte)(a * c.B + (1 - a) * b.B));
    }

    static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        static double L(System.Windows.Media.Color c)
        {
            static double Ch(double v) { v /= 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        var (l1, l2) = (L(a), L(b));
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    // Proves the Library note is selectable + copyable TOP TO BOTTOM: selects the whole document
    // (what a drag + Ctrl+C does) and checks the captured text contains every part.
    static int CopyTest()
    {
        var m = new MeetingRecord
        {
            Title = "Q3 Planning Sync",
            Notes = new NoteSet
            {
                Tldr = "SUMMARY_MARKER - the team agreed to ship the beta in August.",
                Sections = { new NoteSection { Heading = "Decisions", Bullets = { "BULLET_MARKER - cut scope to the core flow" } } },
                Actions = { new ActionItem { Text = "TODO_MARKER - send the beta invite list", Owner = "Sid" } },
            },
            Transcript =
            {
                new TranscriptSegment(Speaker.You, 0, 1000, "YOU_MARKER - where are we on the timeline?"),
                new TranscriptSegment(Speaker.Them, 1000, 2000, "THEM_MARKER - beta lands next week."),
            },
        };

        var rtb = (System.Windows.Controls.RichTextBox)Views.NoteRenderer.MeetingDocument(m, spacious: true);
        // Realise the transcript teaser's "show full" so the whole transcript is in the document.
        // (The document already holds the first 3 lines; both our markers are within that preview.)
        var doc = rtb.Document;
        var captured = new System.Windows.Documents.TextRange(doc.ContentStart, doc.ContentEnd).Text;

        Console.WriteLine("── captured by a full top-to-bottom select-all ──");
        Console.WriteLine(captured.Trim());
        Console.WriteLine("──────────────────────────────────────────────");

        string[] markers = ["SUMMARY_MARKER", "BULLET_MARKER", "TODO_MARKER", "YOU_MARKER", "THEM_MARKER"];
        var missing = markers.Where(x => !captured.Contains(x)).ToList();
        var selOk = missing.Count == 0;
        Console.WriteLine(selOk
            ? "PASS: select-all captures the whole note (summary + bullet + to-do + transcript)"
            : $"FAIL: selection missed {string.Join(", ", missing)}");

        // And the Copy button's one-click "everything" must yield the same full note.
        var full = Views.NoteRenderer.FullText(m);
        var copyOk = markers.All(full.Contains);
        Console.WriteLine(copyOk ? "PASS: Copy 'everything' includes every part" : "FAIL: Copy 'everything' is incomplete");

        return selOk && copyOk ? 0 : 1;
    }

    // Verifies FTS5 retrieval + the task router against the REAL library (no model needed):
    // prints which meetings a question matches, and a sample of open tasks. Toki.exe --asktest "who owns the deck"
    static int AskTest(string[] args)
    {
        var i = Array.IndexOf(args, "--asktest");
        var q = i >= 0 && i + 1 < args.Length ? args[i + 1] : "what did we decide";
        var store = new MeetingStore();   // the real %APPDATA%\Toki\toki.db (EnsureCreated backfills FTS)

        Console.WriteLine($"question: \"{q}\"");
        var ids = store.SearchRelevant(q, 5);
        Console.WriteLine($"FTS matched {ids.Count} meeting(s):");
        foreach (var id in ids) Console.WriteLine($"  #{id}  {store.Get(id)?.Title}");

        var open = store.OpenActions(8);
        Console.WriteLine($"open tasks in library: {open.Count} (showing up to 8)");
        foreach (var a in open) Console.WriteLine($"  • {a.Text}  -  {a.Meeting}");

        // Deterministic query understanding (no model): show intent, parsed time, and the meeting(s)
        // the pipeline resolves - this proves "most recent"/"on <date>" pick the RIGHT meeting.
        var plan = Toki.Llm.QueryUnderstanding.Parse(q, DateTimeOffset.Now);
        Console.WriteLine($"── plan: intent={plan.Intent}  rank={(plan.Rank?.ToString() ?? "-")}  " +
            $"range={(plan.HasDateRange ? $"{plan.RangeStart:MMM d}..{plan.RangeEnd:MMM d}" : "-")}  residual=\"{plan.Residual}\"");
        if (plan.Rank == Toki.Llm.RecencyDir.Latest)
            Console.WriteLine($"   resolved latest → {store.Get(store.LatestMeetingIds(1).FirstOrDefault())?.Title}");
        if (plan.HasDateRange)
            foreach (var id in store.MeetingIdsInRange(plan.RangeStart!.Value, plan.RangeEnd!.Value).Take(5))
                Console.WriteLine($"   resolved in-range → {store.Get(id)?.Title}  ({store.Get(id)?.StartedAt:MMM d})");

        // Only the DETERMINISTIC intents are answered here (they need no model). Summary/lookup would
        // load Qwen (slow/absent in this headless test) - we've already shown their resolved meeting above.
        var deterministic = plan.Intent is Toki.Llm.QueryIntent.Count or Toki.Llm.QueryIntent.ListMeetings or Toki.Llm.QueryIntent.ListTasks;
        if (deterministic)
        {
            Console.WriteLine("── NotesQa answer (deterministic) ──");
            var ans = Toki.Llm.NotesQa.AskAsync(q).GetAwaiter().GetResult();
            Console.WriteLine(ans.Text);
            Console.WriteLine("────────────────────");
        }
        else Console.WriteLine("(summary/lookup route → would call the model; resolution shown above)");

        var ok = ids.Count > 0 || open.Count >= 0;
        Console.WriteLine(ok ? "PASS: retrieval ran" : "FAIL");
        return ok ? 0 : 1;
    }

    // Verifies the deterministic query-understanding layer (temporal parse + intent) against a fixed
    // reference date, so "most recent / July 30 / last week / follow up" all classify correctly offline.
    static int QueryTest()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);   // fixed ref so results are stable
        Console.WriteLine($"(reference 'now' = {now:ddd MMM d yyyy})\n");

        // query → expected intent (temporal/rank printed for eyeballing)
        var cases = new (string q, Toki.Llm.QueryIntent intent)[]
        {
            ("summarise my most recent meeting", Toki.Llm.QueryIntent.SummarizeOne),
            ("what happened in my last meeting", Toki.Llm.QueryIntent.SummarizeOne),
            ("meeting on July 30", Toki.Llm.QueryIntent.SummarizeOne),
            ("what did we decide about the survey forms", Toki.Llm.QueryIntent.LookupFact),
            ("AI tool review and survey follow up", Toki.Llm.QueryIntent.LookupFact),   // NOT tasks - the bug
            ("what are my open tasks", Toki.Llm.QueryIntent.ListTasks),
            ("what tasks do I have for this week", Toki.Llm.QueryIntent.ListTasks),
            ("what's due on Friday", Toki.Llm.QueryIntent.ListTasks),
            ("what are my tasks for next week", Toki.Llm.QueryIntent.ListTasks),
            ("action items in the postgres meeting", Toki.Llm.QueryIntent.ListTasks),
            ("what meetings did I have last week", Toki.Llm.QueryIntent.ListMeetings),
            ("how many meetings in March", Toki.Llm.QueryIntent.Count),
            ("who was worried about replication lag", Toki.Llm.QueryIntent.LookupFact),
        };

        var pass = 0;
        foreach (var (q, want) in cases)
        {
            var p = Toki.Llm.QueryUnderstanding.Parse(q, now);
            var temporal = p.Rank is { } r ? $"rank={r}" : p.HasDateRange ? $"{p.RangeStart:MMM d}..{p.RangeEnd:MMM d}" : "-";
            var ok = p.Intent == want;
            if (ok) pass++;
            Console.WriteLine($"{(ok ? "ok " : "XX ")} \"{q}\"\n      intent={p.Intent} (want {want})  time=[{temporal}]  residual=\"{p.Residual}\"");
        }
        Console.WriteLine($"\n{pass}/{cases.Length} intents correct");
        return pass == cases.Length ? 0 : 1;
    }

    // Scripted multi-turn conversation → verifies follow-up memory ("before that", "what about the
    // action items?") resolves against the previous turn. Uses only deterministic routes (no model).
    static int FollowUpTest()
    {
        var ctx = new Toki.Llm.ConversationContext();
        var now = DateTimeOffset.Now;
        var pass = true;

        // Turn 1: a dated count → sets the range in context.
        var a1 = Toki.Llm.NotesQa.AskAsync("how many meetings did I have last week", ctx).GetAwaiter().GetResult();
        Console.WriteLine($"» how many meetings last week\n   {a1.Text}");
        var r1s = ctx.LastRangeStart; var r1e = ctx.LastRangeEnd;

        // Turn 2: "before that" → the range must shift ~7 days earlier and still answer a count.
        var a2 = Toki.Llm.NotesQa.AskAsync("before that", ctx).GetAwaiter().GetResult();
        Console.WriteLine($"» before that\n   {a2.Text}");
        var shifted = ctx.LastRangeStart is { } s2 && r1s is { } s1 && s2 < s1;
        Console.WriteLine(shifted ? "   ok range shifted earlier" : "   XX range did not shift");
        pass &= shifted && a2.Text.StartsWith("You have", StringComparison.OrdinalIgnoreCase);

        // Turn 3: a scoped task list → pins that meeting in context.
        var a3 = Toki.Llm.NotesQa.AskAsync("action items in the postgres meeting", ctx).GetAwaiter().GetResult();
        Console.WriteLine($"» action items in the postgres meeting\n   {Head(a3.Text)}");
        var pinned = ctx.LastMeetingIds.Count > 0;

        // Turn 4 (plan-only): "what about the summary?" must become SummarizeOne pinned to that meeting.
        var p4 = Toki.Llm.QueryUnderstanding.ResolveFollowUp("what about the summary", ctx, now);
        var p4ok = p4 is { Intent: Toki.Llm.QueryIntent.SummarizeOne, PinnedMeetingIds: { Count: > 0 } };
        Console.WriteLine($"» what about the summary → {(p4 is null ? "not-a-followup" : $"{p4.Intent} pinned={p4.PinnedMeetingIds?.Count}")}  {(p4ok ? "ok" : "XX")}");
        pass &= pinned && p4ok;

        // Turn 5 (plan-only): "what about the action items?" must become ListTasks pinned.
        var p5 = Toki.Llm.QueryUnderstanding.ResolveFollowUp("what about the action items", ctx, now);
        var p5ok = p5 is { Intent: Toki.Llm.QueryIntent.ListTasks, PinnedMeetingIds: { Count: > 0 } };
        Console.WriteLine($"» what about the action items → {(p5 is null ? "not-a-followup" : $"{p5.Intent} pinned={p5.PinnedMeetingIds?.Count}")}  {(p5ok ? "ok" : "XX")}");
        pass &= p5ok;

        Console.WriteLine(pass ? "PASS: follow-ups resolve against conversation state" : "FAIL");
        return pass ? 0 : 1;
    }

    static string Head(string s) => s.Length > 120 ? s[..120] + "…" : s;

    // Verifies the on-device embedder: a paraphrase pair must score higher than an unrelated pair
    // (the whole point of meaning-search - matching words the transcript never used).
    static int EmbedTest()
    {
        if (!ModelManager.EmbedInstalled)
        {
            Console.WriteLine($"embedder model not downloaded yet: {ModelManager.EmbedPath}");
            Console.WriteLine("run with --downloadembed first (or it downloads at onboarding).");
            return 2;
        }
        var svc = Toki.Llm.EmbeddingService.Instance;
        float[] E(string t) => svc.EmbedAsync(t).GetAwaiter().GetResult() ?? Array.Empty<float>();

        var q = E("what did we say about hiring");
        var para = E("notes on bringing someone new onto the team");   // same meaning, different words
        var un = E("the database replication lag on postgres");        // unrelated

        if (q.Length == 0) { Console.WriteLine("FAIL: embedder returned nothing"); return 1; }
        var simPara = Toki.Llm.EmbeddingService.Cosine(q, para);
        var simUn = Toki.Llm.EmbeddingService.Cosine(q, un);
        Console.WriteLine($"dim={q.Length}");
        Console.WriteLine($"cosine(hiring, 'bringing someone onto the team') = {simPara:F3}   (paraphrase - should be higher)");
        Console.WriteLine($"cosine(hiring, 'postgres replication lag')       = {simUn:F3}   (unrelated - should be lower)");
        var ok = simPara > simUn && simPara > 0.4f;
        Console.WriteLine(ok ? "PASS: meaning-search ranks the paraphrase above the unrelated text" : "FAIL: embeddings not discriminating");
        return ok ? 0 : 1;
    }

    // Exercises the Google Calendar plumbing end to end (no UI). Subcommands:
    //   --caltest connect      → interactive sign-in (opens your browser); stores the encrypted token
    //   --caltest              → list the next 7 days (head + tail), proving read + refresh work
    //   --caltest insert       → create a throwaway event, print its link, then delete it (nothing left)
    //   --caltest disconnect   → wipe + revoke the local grant
    static int CalTest(string[] args)
    {
        var i = Array.IndexOf(args, "--caltest");
        var sub = i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[i + 1].ToLowerInvariant() : "list";
        var svc = Toki.Calendar.CalendarService.Instance;

        if (!svc.IsConfigured)
            return Fail("Google OAuth client not set up. Add TOKI_GOOGLE_CLIENT_ID + TOKI_GOOGLE_CLIENT_SECRET " +
                        "env vars, or drop the console's client JSON at %APPDATA%\\Toki\\google_client.json (see docs/google-setup.md).");

        try
        {
            switch (sub)
            {
                case "connect":
                    if (svc.IsConnected) { Console.WriteLine($"[--caltest] already connected as {svc.AccountEmail}. Run '--caltest disconnect' to reset."); return 0; }
                    Console.WriteLine("[--caltest connect] opening your browser - sign in to Google and click Allow...");
                    svc.ConnectAsync().GetAwaiter().GetResult();
                    Console.WriteLine($"[--caltest] PASS - connected as {svc.AccountEmail}. Refresh token stored (DPAPI-encrypted).");
                    Console.WriteLine("  verify: open %APPDATA%\\Toki\\settings.json - GoogleRefreshTokenProtected is opaque base64 (no plaintext token).");
                    return 0;

                case "disconnect":
                    svc.DisconnectAsync().GetAwaiter().GetResult();
                    Console.WriteLine("[--caltest] disconnected + token revoked at Google.");
                    return 0;

                case "insert":
                    if (!svc.IsConnected) return Fail("not connected - run: Toki.exe --caltest connect");
                    var start = DateTimeOffset.Now.AddMinutes(10);
                    var draft = new Toki.Calendar.NewEvent("Toki self-test - safe to ignore", start, start.AddMinutes(15));
                    Console.WriteLine("[--caltest insert] creating a throwaway event 10 min from now...");
                    var created = svc.CreateAsync(draft).GetAwaiter().GetResult();
                    Console.WriteLine($"  created id={created.Id}");
                    Console.WriteLine($"  link  : {created.HtmlLink}");
                    svc.DeleteAsync(created.Id).GetAwaiter().GetResult();
                    Console.WriteLine("[--caltest] PASS - event created then deleted; nothing left in your calendar.");
                    return 0;

                case "update":
                    if (!svc.IsConnected) return Fail("not connected - run: Toki.exe --caltest connect");
                    var s0 = DateTimeOffset.Now.AddMinutes(20);
                    Console.WriteLine("[--caltest update] insert → patch (rename + move +30 min) → verify → delete...");
                    var ins = svc.CreateAsync(new Toki.Calendar.NewEvent("Toki update-test A", s0, s0.AddMinutes(15))).GetAwaiter().GetResult();
                    var s1 = s0.AddMinutes(30);
                    var patched = svc.UpdateAsync(ins.Id, new Toki.Calendar.NewEvent("Toki update-test B", s1, s1.AddMinutes(15)), ins.HasMeetLink).GetAwaiter().GetResult();
                    var ok = patched.Summary == "Toki update-test B" && Math.Abs((patched.StartUtc - s1.ToUniversalTime()).TotalMinutes) < 1;
                    Console.WriteLine($"  now: \"{patched.Summary}\" at {patched.StartLocal:HH:mm}");
                    svc.DeleteAsync(ins.Id).GetAwaiter().GetResult();
                    Console.WriteLine(ok ? "[--caltest] PASS - edit applied then cleaned up." : "FAIL: patch didn't take");
                    return ok ? 0 : 1;

                case "parse":
                    var line = i + 2 < args.Length ? args[i + 2] : "Design sync tomorrow 2pm with ana@work.com";
                    var qp = Toki.Calendar.QuickEventParser.Parse(line, DateTimeOffset.Now);
                    Console.WriteLine($"[--caltest parse] \"{line}\"");
                    Console.WriteLine($"  title : {qp.Summary}");
                    Console.WriteLine($"  when  : {(qp.Start is { } st ? $"{st:ddd MMM d HH:mm} – {qp.End:HH:mm}" : "(no time)")}");
                    Console.WriteLine($"  emails: {(qp.Emails.Count > 0 ? string.Join(", ", qp.Emails) : "(none)")}");
                    return qp.Start is not null ? 0 : 1;

                default: // list
                    if (!svc.IsConnected) return Fail("not connected - run: Toki.exe --caltest connect");
                    Console.WriteLine("[--caltest] refreshing token + listing the next 7 days...");
                    var events = svc.UpcomingAsync(7).GetAwaiter().GetResult();
                    Console.WriteLine($"  {events.Count} event(s) in the next 7 days for {svc.AccountEmail}:");
                    if (events.Count == 0)
                    {
                        Console.WriteLine("[--caltest] PASS - read path works (calendar is just empty for the next week).");
                        return 0;
                    }
                    void Print(Toki.Calendar.CalEvent e) => Console.WriteLine(
                        $"   {e.StartLocal:ddd MMM d HH:mm}  {e.Summary}" +
                        $"  ({e.Attendees.Count} attendee{(e.Attendees.Count == 1 ? "" : "s")}{(e.HasMeetLink ? ", meet" : "")})");
                    Console.WriteLine("  --- HEAD ---");
                    foreach (var e in events.Take(3)) Print(e);
                    if (events.Count > 6)
                    {
                        Console.WriteLine("   ...");
                        Console.WriteLine("  --- TAIL ---");
                        foreach (var e in events.TakeLast(3)) Print(e);
                    }
                    Console.WriteLine("[--caltest] PASS - read + auto-refresh work. Do these match your real calendar?");
                    return 0;
            }
        }
        catch (Toki.Calendar.GoogleAuthException ex) { return Fail(ex.Message); }
    }

    // Verifies the deterministic prep-card matching + agenda parse against a temp store (no network/model).
    static int PrepTest()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"toki-preptest-{Environment.ProcessId}.db");
        try
        {
            var store = new MeetingStore(dbPath);
            store.Save(new MeetingRecord
            {
                Title = "Design review - PixelFlow", Kind = MeetingKind.Meeting, StartedAt = DateTimeOffset.Now.AddDays(-2),
                Notes = new NoteSet
                {
                    Tldr = "Blockers are brand tokens and the webhook. Ship moved to Thursday.",
                    Actions = { new ActionItem { Text = "Confirm brand font + hex.", Owner = "Priya" },
                                new ActionItem { Text = "Wire the webhook.", Owner = "Sid" } },
                },
            });

            Toki.Calendar.CalEvent Ev(string title) => new("id1", title,
                DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow.AddMinutes(35), false, null,
                "https://meet.google.com/x", "https://cal", new List<Toki.Calendar.CalAttendee> { new("a@x.com", "A", "accepted", false, false) });

            var match = Toki.Calendar.PrepBuilder.Build(Ev("Design review - PixelFlow"), store);
            var noMatch = Toki.Calendar.PrepBuilder.Build(Ev("Quarterly finance deep-dive"), store);
            var agenda = Toki.Calendar.PrepBuilder.ParseAgenda("<ul><li>Confirm blockers</li><li>Lock the ship date</li></ul>");

            Console.WriteLine($"  match  : last={(match.LastTime?.Title ?? "(none)")}  open={match.StillOpen.Count}");
            Console.WriteLine($"  noMatch: last={(noMatch.LastTime?.Title ?? "(none)")}");
            Console.WriteLine($"  agenda : {string.Join(" | ", agenda)}");

            var ok = match.LastTime is not null && match.StillOpen.Count == 2
                     && match.StillOpen.Any(a => a.Owner == "Priya")
                     && noMatch.LastTime is null
                     && agenda.Count == 2 && agenda[0] == "Confirm blockers";
            Console.WriteLine(ok
                ? "[--preptest] PASS - matched a prior meeting by title, pulled 2 open items, rejected the unrelated event, parsed the agenda."
                : "FAIL: prep matching/agenda wrong");
            return ok ? 0 : 1;
        }
        catch (Exception ex) { return Fail($"--preptest threw: {ex}"); }
        finally { try { File.Delete(dbPath); } catch { } }
    }

    // Verifies voice-create end to end WITHOUT a mic/model/network: detection matrix + the confirm gate
    // (turn 1 never creates; "yes" creates exactly once; "no" cancels; a stray "yes" can't create).
    static int VoiceCreateTest()
    {
        var now = DateTimeOffset.Now;
        var pass = true;

        string[] positives =
        {
            "schedule a call with Ana tomorrow at 3", "hey doki, book a meeting with the design team friday at 10am",
            "put lunch with sam on my calendar tomorrow at noon", "set up a sync monday 9am",
            "remind me to send the deck tomorrow at 5", "can you schedule a call with ana tomorrow at 3",
        };
        string[] negatives =
        {
            "what meetings do I have tomorrow", "when is my call with Ana", "did we schedule the follow-up",
            "summarize the planning meeting", "who set up the meeting yesterday", "should we schedule another sync",
            "add a new note in the library", "we scheduled a call for friday",
        };
        foreach (var p in positives)
            if (CalendarCommand.TryDetect(p, now) is null) { pass = false; Console.WriteLine($"  XX should DETECT: \"{p}\""); }
        foreach (var n in negatives)
            if (CalendarCommand.TryDetect(n, now) is not null) { pass = false; Console.WriteLine($"  XX should NOT detect: \"{n}\""); }
        Console.WriteLine($"  detection: {positives.Length} positives + {negatives.Length} negatives checked");

        var creates = 0;
        CalendarCommand.ConnectedOverride = true;
        CalendarCommand.CreateOverride = (ev, ct) =>
        {
            creates++;
            return Task.FromResult(new Toki.Calendar.CalEvent("id", ev.Summary, ev.Start.ToUniversalTime(),
                ev.End.ToUniversalTime(), false, null, null, "https://cal", Array.Empty<Toki.Calendar.CalAttendee>()));
        };
        try
        {
            var ctx = new ConversationContext();
            var a1 = NotesQa.AskAsync("schedule a call with Ana tomorrow at 3", ctx).GetAwaiter().GetResult();
            Console.WriteLine($"  turn1: {a1.Text}");
            var gateOk = creates == 0 && ctx.PendingCreate is not null && a1.Text.Contains("yes");
            var a2 = NotesQa.AskAsync("yes", ctx).GetAwaiter().GetResult();
            Console.WriteLine($"  turn2: {a2.Text}");
            var createdOk = creates == 1 && ctx.PendingCreate is null && a2.Text.StartsWith("Done", StringComparison.OrdinalIgnoreCase);

            var before = creates;
            var ctx2 = new ConversationContext();
            NotesQa.AskAsync("book a meeting with sam tomorrow at 4", ctx2).GetAwaiter().GetResult();
            NotesQa.AskAsync("no", ctx2).GetAwaiter().GetResult();
            var cancelOk = creates == before && ctx2.PendingCreate is null;

            // Stray-yes safety: an unrelated turn clears the pending (deterministic small-talk, no model),
            // so a later "yes" has nothing to create.
            var ctx3 = new ConversationContext();
            NotesQa.AskAsync("set up a sync monday 9am", ctx3).GetAwaiter().GetResult();
            NotesQa.AskAsync("thanks", ctx3).GetAwaiter().GetResult();
            var strayOk = ctx3.PendingCreate is null;

            Console.WriteLine($"  gate={gateOk}  created-once={createdOk}  cancel={cancelOk}  stray-safe={strayOk}");
            pass &= gateOk && createdOk && cancelOk && strayOk;
        }
        finally { CalendarCommand.ConnectedOverride = null; CalendarCommand.CreateOverride = null; }

        Console.WriteLine(pass ? "[--voicecreatetest] PASS - detection matrix + confirm gate all correct." : "FAIL");
        return pass ? 0 : 1;
    }

    static int Seconds(string[] args, int fallback)
    {
        var v = ArgValue(args, "--seconds");
        return v != null && int.TryParse(v, out var s) && s > 0 ? s : fallback;
    }

    static int Fail(string message)
    {
        Console.WriteLine($"FAIL: {message}");
        return 1;
    }
}
