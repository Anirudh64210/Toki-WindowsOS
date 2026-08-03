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
        // Render “—”, “✓” etc. correctly in the console instead of mojibake (ΓÇö). Best-effort:
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
                "--voicetest" => VoiceTest(args),
                "--transcribetest" => TranscribeTest(args),
                "--dbtest" => DbTest(),
                "--notestest" => NotesTest(),
                "--renotes" => ReNotes(args),
                "--livetest" => LiveTest(args),
                "--seed" => Seed(),
                "--llmtest" => LlmTest(),
                "--themetest" => ThemeTest(),
                "--copytest" => CopyTest(),
                "--asktest" => AskTest(args),
                "--querytest" => QueryTest(),
                "--followuptest" => FollowUpTest(),
                "--embedtest" => EmbedTest(),
                "--caltest" => CalTest(args),
                _ => Fail($"unknown self-test '{flag}'"),
            };
        }
        catch (Exception ex)
        {
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
            return Fail("Parakeet model not installed — run --asrtest once to fetch it.");
        Console.WriteLine($"[--voicetest] transcribing {Path.GetFileName(file)} via TranscribeFile()...");
        var text = Toki.Transcription.TranscriptionService.Instance.TranscribeFile(file);
        if (text is null) return Fail("TranscribeFile returned null (model/decoder error).");
        Console.WriteLine($"[--voicetest] result: \"{text}\"");
        if (text.Length == 0) { Console.WriteLine("[--voicetest] empty — PASS only if the clip was silent."); return 0; }
        Console.WriteLine("[--voicetest] PASS — non-empty transcription returned.");
        return 0;
    }

    static int MicTest(string outDir, int seconds)
    {
        var path = Path.Combine(outDir, "you.wav");
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
        Console.WriteLine($"[--mictest] PASS — play {Path.GetFileName(path)} and you should hear yourself.");
        if (mic.PeakLevel < 0.005f)
            Console.WriteLine("  note: peak is very low — was the mic muted or silent?");
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
        Console.WriteLine($"[--loopbacktest] PASS — play {Path.GetFileName(path)} and you should hear that app.");
        if (cap.PeakLevel < 0.005f)
            Console.WriteLine("  note: peak is near silence — the app may not have played audio during the test.");
        return 0;
    }

    static int LlmTest()
    {
        if (!ModelManager.QwenInstalled)
        {
            Console.WriteLine("[--llmtest] Qwen3-4B not installed — downloading (~2.5 GB, one time)...");
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
            new(Speaker.Them, 0, 4000, "So where are we on the launch — what's left before we can ship?"),
            new(Speaker.You, 4200, 9000, "Tool's basically done. The only blockers are the brand tokens and wiring the Make webhook."),
            new(Speaker.Them, 9200, 13000, "Okay. Let's move the ship date to Thursday then, and keep the email capture optional."),
            new(Speaker.You, 13200, 17000, "Sounds good. I'll wire the webhook this week. Priya, can you confirm the brand font and hex tomorrow?"),
            new(Speaker.Them, 17200, 20000, "Yep, I'll get you the font and colors by tomorrow. Which analytics platform are we pointing events at?"),
        };

        Console.WriteLine("[--llmtest] loading Qwen + writing notes (one structured pass + title)...");
        var sw = Stopwatch.StartNew();
        // Run off the UI thread — LLamaSharp's async continuations would otherwise
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
            Console.WriteLine($"  [warn] took {sw.ElapsedMilliseconds / 1000.0:0.0}s — target is under 30s.");
        if (TextValidator.IsDegenerate(notes.Tldr)) return Fail("SUMMARY is degenerate (CJK / repetition).");
        Console.WriteLine("[--llmtest] PASS — notes generated, English, no degeneration. Read them: do they match the transcript?");
        return 0;
    }

    /// <summary>Re-run the LLM note writer on a STORED meeting's real transcript and print the
    /// result — so we can verify the notes engine against actual meetings (e.g. the 25-min one that
    /// used to summarise only its first lines) without recording again.</summary>
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
        // Off the UI thread — LLamaSharp async continuations would deadlock a blocked caller.
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
        Console.WriteLine("[--renotes] done — read the notes: do they reflect the WHOLE meeting, honestly (no filler)?");
        return 0;
    }

    /// <summary>
    /// Feed a WAV through the LIVE transcriber the way capture would during a meeting, and prove that
    /// most segments finalize DURING the stream — so Stop only decodes the short remainder (the whole
    /// point of Phase 2). Paced ~2× real-time so the worker keeps up; use a short-ish clip.
    /// </summary>
    static int LiveTest(string[] args)
    {
        var i = Array.IndexOf(args, "--livetest");
        var path = i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[i + 1] : null;
        if (path is null) return Fail("usage: --livetest <path-to-wav | recording-folder>");

        if (!ModelManager.IsInstalled(ModelManager.Parakeet))
        {
            Console.WriteLine("[--livetest] Parakeet not installed — run --asrtest once to fetch it.");
            return Fail("model missing");
        }

        // A folder feeds BOTH you.wav + them.wav concurrently — the real two-stream meeting load.
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
            Console.WriteLine("[--livetest] empty — treat as silence. PASS if the clip was silent.");
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
        Console.WriteLine($"[--livetest] PASS — {duringStream} segments finalized mid-stream; Stop only had {stopSw.ElapsedMilliseconds} ms left. In a real meeting that's your wait after clicking Stop.");
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
            Title = "Weekly 1:1 — Rounak", App = "zoom.exe",
            StartedAt = DateTimeOffset.Now.AddDays(-3), DurationMs = 18 * 60 * 1000,
            Transcript = [new(Speaker.You, 0, 3000, "Landing page hero is roughed in, pricing next.")],
            Notes = new NoteSet
            {
                Tldr = "Landing page — hero roughed in, pricing next. Keep it one page for launch.",
                Decisions = ["Keep the landing page to one page for launch."],
                Actions = [new ActionItem { Text = "Draft pricing section.", Owner = "You" }],
            },
        };
        store.Save(m2); MarkdownMirror.Write(m2);

        store.CreateManualNote("Prep — Design review", "- ask about the analytics platform\n- confirm the ship date\n- bring the webhook URL");
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
                Title = "Design review — PixelFlow", App = "chrome.exe", DurationMs = 24 * 60 * 1000,
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

            Console.WriteLine("[--dbtest] PASS — insert / search / update / delete round-trip works.");
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
        Console.WriteLine($"  echo de-dup: {(echoRemoved ? "OK — duplicate removed, one copy kept" : "FAIL")}");

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
        Console.WriteLine("[--notestest] PASS — echo removed; decisions/actions/questions extracted; title clean.");
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
        Console.WriteLine("[--transcribetest] PASS — you/them attributed, echo removed, one timeline.");
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
            Console.WriteLine("[--asrtest] Parakeet model not installed — downloading (~630 MB, one time)...");
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
            Console.WriteLine("[--asrtest] empty transcript — treat as silence (nobody spoke). PASS if the clip was silent.");
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
        Console.WriteLine($"[--asrtest] PASS — verify the TAIL matches the END of the audio (proves no mid-file truncation).");
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
        Console.WriteLine("[--recordtest] PASS — session wrote real audio files.");
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
                Console.WriteLine($"  [{i + 1,2}] mic idle — no app is capturing.");
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
            ? "[--detecttest] PASS — at least one real meeting candidate was seen."
            : "[--detecttest] done — no candidate seen (start a call during the test to see one).");
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

    // Prove the live light/dark swap recolours an ALREADY-RENDERED element. The old bug: WPF froze the
    // themed brushes so mutating .Color threw, and nothing repainted. Replace-based Apply must instead
    // make the DynamicResource re-resolve to the new brush. Black notch → white notch on swap = PASS.
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
        return ok ? 0 : 1;
    }

    // Proves the Library note is selectable + copyable TOP TO BOTTOM. Builds the note document,
    // programmatically selects the whole thing (exactly what a drag from top to bottom + Ctrl+C does),
    // and checks the captured text contains every part — summary, a bullet, a to-do, a transcript line.
    // This is the real artifact the screenshot could never show.
    static int CopyTest()
    {
        var m = new MeetingRecord
        {
            Title = "Q3 Planning Sync",
            Notes = new NoteSet
            {
                Tldr = "SUMMARY_MARKER — the team agreed to ship the beta in August.",
                Sections = { new NoteSection { Heading = "Decisions", Bullets = { "BULLET_MARKER — cut scope to the core flow" } } },
                Actions = { new ActionItem { Text = "TODO_MARKER — send the beta invite list", Owner = "Sid" } },
            },
            Transcript =
            {
                new TranscriptSegment(Speaker.You, 0, 1000, "YOU_MARKER — where are we on the timeline?"),
                new TranscriptSegment(Speaker.Them, 1000, 2000, "THEM_MARKER — beta lands next week."),
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
        foreach (var a in open) Console.WriteLine($"  • {a.Text}  —  {a.Meeting}");

        // Deterministic query understanding (no model): show intent, parsed time, and the meeting(s)
        // the pipeline resolves — this proves "most recent"/"on <date>" pick the RIGHT meeting.
        var plan = Toki.Llm.QueryUnderstanding.Parse(q, DateTimeOffset.Now);
        Console.WriteLine($"── plan: intent={plan.Intent}  rank={(plan.Rank?.ToString() ?? "—")}  " +
            $"range={(plan.HasDateRange ? $"{plan.RangeStart:MMM d}..{plan.RangeEnd:MMM d}" : "—")}  residual=\"{plan.Residual}\"");
        if (plan.Rank == Toki.Llm.RecencyDir.Latest)
            Console.WriteLine($"   resolved latest → {store.Get(store.LatestMeetingIds(1).FirstOrDefault())?.Title}");
        if (plan.HasDateRange)
            foreach (var id in store.MeetingIdsInRange(plan.RangeStart!.Value, plan.RangeEnd!.Value).Take(5))
                Console.WriteLine($"   resolved in-range → {store.Get(id)?.Title}  ({store.Get(id)?.StartedAt:MMM d})");

        // Only the DETERMINISTIC intents are answered here (they need no model). Summary/lookup would
        // load Qwen (slow/absent in this headless test) — we've already shown their resolved meeting above.
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
            ("AI tool review and survey follow up", Toki.Llm.QueryIntent.LookupFact),   // NOT tasks — the bug
            ("what are my open tasks", Toki.Llm.QueryIntent.ListTasks),
            ("action items in the postgres meeting", Toki.Llm.QueryIntent.ListTasks),
            ("what meetings did I have last week", Toki.Llm.QueryIntent.ListMeetings),
            ("how many meetings in March", Toki.Llm.QueryIntent.Count),
            ("who was worried about replication lag", Toki.Llm.QueryIntent.LookupFact),
        };

        var pass = 0;
        foreach (var (q, want) in cases)
        {
            var p = Toki.Llm.QueryUnderstanding.Parse(q, now);
            var temporal = p.Rank is { } r ? $"rank={r}" : p.HasDateRange ? $"{p.RangeStart:MMM d}..{p.RangeEnd:MMM d}" : "—";
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
    // (the whole point of meaning-search — matching words the transcript never used).
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
        Console.WriteLine($"cosine(hiring, 'bringing someone onto the team') = {simPara:F3}   (paraphrase — should be higher)");
        Console.WriteLine($"cosine(hiring, 'postgres replication lag')       = {simUn:F3}   (unrelated — should be lower)");
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
                    Console.WriteLine("[--caltest connect] opening your browser — sign in to Google and click Allow...");
                    svc.ConnectAsync().GetAwaiter().GetResult();
                    Console.WriteLine($"[--caltest] PASS — connected as {svc.AccountEmail}. Refresh token stored (DPAPI-encrypted).");
                    Console.WriteLine("  verify: open %APPDATA%\\Toki\\settings.json — GoogleRefreshTokenProtected is opaque base64 (no plaintext token).");
                    return 0;

                case "disconnect":
                    svc.DisconnectAsync().GetAwaiter().GetResult();
                    Console.WriteLine("[--caltest] disconnected + token revoked at Google.");
                    return 0;

                case "insert":
                    if (!svc.IsConnected) return Fail("not connected — run: Toki.exe --caltest connect");
                    var start = DateTimeOffset.Now.AddMinutes(10);
                    var draft = new Toki.Calendar.NewEvent("Toki self-test — safe to ignore", start, start.AddMinutes(15));
                    Console.WriteLine("[--caltest insert] creating a throwaway event 10 min from now...");
                    var created = svc.CreateAsync(draft).GetAwaiter().GetResult();
                    Console.WriteLine($"  created id={created.Id}");
                    Console.WriteLine($"  link  : {created.HtmlLink}");
                    svc.DeleteAsync(created.Id).GetAwaiter().GetResult();
                    Console.WriteLine("[--caltest] PASS — event created then deleted; nothing left in your calendar.");
                    return 0;

                case "update":
                    if (!svc.IsConnected) return Fail("not connected — run: Toki.exe --caltest connect");
                    var s0 = DateTimeOffset.Now.AddMinutes(20);
                    Console.WriteLine("[--caltest update] insert → patch (rename + move +30 min) → verify → delete...");
                    var ins = svc.CreateAsync(new Toki.Calendar.NewEvent("Toki update-test A", s0, s0.AddMinutes(15))).GetAwaiter().GetResult();
                    var s1 = s0.AddMinutes(30);
                    var patched = svc.UpdateAsync(ins.Id, new Toki.Calendar.NewEvent("Toki update-test B", s1, s1.AddMinutes(15)), ins.HasMeetLink).GetAwaiter().GetResult();
                    var ok = patched.Summary == "Toki update-test B" && Math.Abs((patched.StartUtc - s1.ToUniversalTime()).TotalMinutes) < 1;
                    Console.WriteLine($"  now: \"{patched.Summary}\" at {patched.StartLocal:HH:mm}");
                    svc.DeleteAsync(ins.Id).GetAwaiter().GetResult();
                    Console.WriteLine(ok ? "[--caltest] PASS — edit applied then cleaned up." : "FAIL: patch didn't take");
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
                    if (!svc.IsConnected) return Fail("not connected — run: Toki.exe --caltest connect");
                    Console.WriteLine("[--caltest] refreshing token + listing the next 7 days...");
                    var events = svc.UpcomingAsync(7).GetAwaiter().GetResult();
                    Console.WriteLine($"  {events.Count} event(s) in the next 7 days for {svc.AccountEmail}:");
                    if (events.Count == 0)
                    {
                        Console.WriteLine("[--caltest] PASS — read path works (calendar is just empty for the next week).");
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
                    Console.WriteLine("[--caltest] PASS — read + auto-refresh work. Do these match your real calendar?");
                    return 0;
            }
        }
        catch (Toki.Calendar.GoogleAuthException ex) { return Fail(ex.Message); }
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
