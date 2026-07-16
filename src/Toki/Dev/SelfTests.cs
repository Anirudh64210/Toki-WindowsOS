using System.Diagnostics;
using System.IO;
using Toki.Audio;
using Toki.Detection;
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
                "--transcribetest" => TranscribeTest(args),
                _ => Fail($"unknown self-test '{flag}'"),
            };
        }
        catch (Exception ex)
        {
            return Fail($"{flag} threw: {ex.Message}");
        }
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
