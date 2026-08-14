using System.IO;

namespace Toki.Transcription;

/// <summary>
/// App-level transcription: turns a finished recording folder (you.wav / them.wav)
/// into one merged, echo-de-duped transcript. Owns the heavy Parakeet recognizer
/// and creates it once, lazily. Degrades gracefully (returns null) if the model
/// isn't installed yet - a recording is never lost, it just isn't transcribed
/// until the model is present (lesson 2).
/// </summary>
public sealed class TranscriptionService
{
    static readonly Lazy<TranscriptionService> Lazy = new(() => new TranscriptionService());
    public static TranscriptionService Instance => Lazy.Value;

    readonly object _gate = new();
    Transcriber? _transcriber;

    public static bool ModelReady => ModelManager.IsInstalled(ModelManager.Parakeet);

    TranscriptionService() { }

    /// <summary>
    /// Transcribe both streams of a recording folder (sequentially - a sherpa-onnx
    /// offline recognizer is NOT safe to run concurrently, even as two instances:
    /// concurrent decode produced non-deterministic, corrupt transcripts in testing,
    /// so correctness wins over the ~15% wall-clock a parallel run would save) and
    /// merge them. Returns null if the model isn't available. Background-thread safe.
    /// </summary>
    /// <param name="attendees">Display names from the calendar event, when this meeting has one. These
    /// are typed by a human, so they outrank anything counted from past transcripts.</param>
    public IReadOnlyList<TranscriptSegment>? Process(string sessionFolder, IEnumerable<string>? attendees = null)
    {
        if (!ModelReady)
        {
            App.Log("transcription skipped: Parakeet model not installed");
            return null;
        }

        // On a small-RAM PC the 2.5GB notes LLM cannot be resident while the recognizer
        // decodes a long meeting - that's the native OOM crash. Free it first so the
        // recognizer has room; the LLM reloads on demand after transcription.
        if (App.LowMemoryMode) Toki.Llm.LlmService.Instance.Release();

        lock (_gate)
        {
            try { _transcriber ??= new Transcriber(); }
            catch (Exception ex) { App.Log($"transcriber init failed: {ex.Message}"); return null; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var you = TranscribeStream(Path.Combine(sessionFolder, "you.wav"), Speaker.You);
            var them = TranscribeStream(Path.Combine(sessionFolder, "them.wav"), Speaker.Them);

            // Merge joins each person's consecutive lines into turns and lifts short backchannels out
            // of the middle of someone else's sentence. Before this, a real 8-minute meeting produced
            // 182 fragments (see docs/real-meeting-findings.md).
            var merged = TranscriptMerger.Merge(you, them);

            // Then repair mis-heard names against the user's own history, and drop the discarded half
            // of spoken self-corrections. Runs LAST so it sees whole turns rather than fragments, and
            // so a name split across two fragments is one word by the time it is judged.
            merged = Polish(merged, attendees);

            App.Log($"transcribed in {sw.ElapsedMilliseconds} ms: you={you.Count} them={them.Count} -> merged {merged.Count} segments");
            return merged;
        }
    }

    /// <summary>
    /// Every edit the last <see cref="Process"/> or <see cref="PolishNames"/> made to the transcript.
    /// Read straight after the call, on the same thread that made it - the recorder finishes one
    /// meeting at a time, and this exists so the caller can store the edits WITH the meeting instead
    /// of the transcript quietly disagreeing with what was said.
    /// </summary>
    public IReadOnlyList<Toki.Storage.TextFix> LastCorrections { get; private set; } = [];

    /// <summary>
    /// Repair an ALREADY-merged transcript - the live path, which decodes during the meeting and so
    /// never goes through <see cref="Process"/>. Both routes must get this: the live one is the fast
    /// path and therefore the one most meetings actually take.
    /// </summary>
    public IReadOnlyList<TranscriptSegment> PolishNames(
        IReadOnlyList<TranscriptSegment> segments, IEnumerable<string>? attendees = null) =>
        Polish(segments, attendees);

    /// <summary>
    /// The two text repairs, in the order that makes each one see its best input: names first (whole
    /// turns, so a name split across fragments is one word by the time it is judged), then spoken
    /// self-corrections. Never fatal - a transcript with a mis-heard name beats no transcript.
    /// </summary>
    IReadOnlyList<TranscriptSegment> Polish(
        IReadOnlyList<TranscriptSegment> segments, IEnumerable<string>? attendees)
    {
        var record = new List<Toki.Storage.TextFix>();
        try
        {
            var trusted = NameLexicon.Trusted(attendees);
            var corpus = NameLexicon.Corpus(App.Store);
            var (named, fixes) = NameCorrector.Correct(segments, trusted, corpus);
            if (fixes.Count > 0)
                App.Log("names corrected: " + string.Join(", ", fixes.Select(f => $"{f.From}->{f.To}")));
            record.AddRange(fixes.Select(f => new Toki.Storage.TextFix(f.From, f.To, "name")));
            segments = named;
        }
        catch (Exception ex) { App.Log($"name correction skipped (non-fatal): {ex.Message}"); }

        try
        {
            var (repaired, repairs) = SelfCorrection.Clean(segments);
            if (repairs.Count > 0)
                App.Log("self-corrections dropped: " + string.Join(", ", repairs.Select(r => $"{r.From}->{r.To}")));
            record.AddRange(repairs.Select(r => new Toki.Storage.TextFix(r.From, r.To, "repair")));
            segments = repaired;
        }
        catch (Exception ex) { App.Log($"self-correction skipped (non-fatal): {ex.Message}"); }

        LastCorrections = record
            .GroupBy(f => (f.From.ToLowerInvariant(), f.To.ToLowerInvariant()))
            .Select(g => g.First())
            .ToList();
        return segments;
    }

    /// <summary>
    /// Decode one LIVE window [start,end) of a stream's 16k-mono buffer at a meeting-time offset,
    /// using the SHARED recognizer under the SAME gate as the stop-time decode - so live windows
    /// and any stop-time pass never run concurrently (concurrent decode corrupts sherpa-onnx output).
    /// Returns [] if the model isn't ready or the decode throws (never crashes the recording).
    /// </summary>
    public IReadOnlyList<TranscriptSegment> DecodeLive(float[] samples, int start, int end, double offsetSec, Speaker speaker)
    {
        if (!ModelReady || end <= start) return [];
        lock (_gate)
        {
            try
            {
                _transcriber ??= new Transcriber();
                return _transcriber.TranscribeSlice(samples, start, end, offsetSec, speaker);
            }
            catch (Exception ex) { App.Log($"live decode {speaker} @ {offsetSec:F0}s failed: {ex.Message}"); return []; }
        }
    }

    /// <summary>
    /// Transcribe a SINGLE mic recording (the Ask-bar "ask by voice" flow) to plain text, using the
    /// same gated recognizer. Returns null if the model isn't installed or the decode errors; "" if the
    /// clip was silent; otherwise the joined text. Runs on a background thread (never the UI thread).
    /// </summary>
    public string? TranscribeFile(string wavPath)
    {
        if (!ModelReady) { App.Log("voice transcribe skipped: Parakeet not installed"); return null; }
        if (App.LowMemoryMode) Toki.Llm.LlmService.Instance.Release();
        lock (_gate)
        {
            try { _transcriber ??= new Transcriber(); }
            catch (Exception ex) { App.Log($"transcriber init failed: {ex.Message}"); return null; }
            try
            {
                var samples = AudioLoader.To16kMono(wavPath);
                if (IsSilent(samples)) return "";
                var segs = _transcriber!.Transcribe(samples, Speaker.You);
                return string.Join(" ", segs.Select(s => s.Text)).Trim();
            }
            catch (Exception ex) { App.Log($"voice transcribe failed: {ex.Message}"); return null; }
        }
    }

    IReadOnlyList<TranscriptSegment> TranscribeStream(string wavPath, Speaker speaker)
    {
        if (!File.Exists(wavPath)) return [];
        try
        {
            var samples = AudioLoader.To16kMono(wavPath);
            // Skip a near-silent stream (e.g. the user's mic muted the whole call).
            // Decoding it costs the full ~4x-real-time grind yet yields nothing - on
            // a muted-mic meeting that was ~half the total processing time (real logs).
            // Silence is not failure (lesson 4): a muted stream just isn't transcribed.
            if (IsSilent(samples))
            {
                App.Log($"{speaker} stream near-silent, skipping ASR ({samples.Length / (double)AudioLoader.TargetRate:F0}s)");
                return [];
            }
            return _transcriber!.Transcribe(samples, speaker);
        }
        catch (Exception ex)
        {
            // Parakeet errored (not silence) - this is where the whisper.cpp
            // fallback will run (lesson 4). For now, log and skip this stream.
            App.Log($"Parakeet error on {speaker} stream: {ex.Message} (whisper fallback: TODO)");
            return [];
        }
    }

    // A stream is "silent" if no sample ever crosses a low amplitude floor (~-46 dBFS).
    // Real speech peaks near 0.1–1.0, so this only ever trips on genuine digital silence
    // (muted mic / no loopback), never on quiet speech. Early-exits on the first loud
    // sample, so it's a cheap linear scan.
    const float SilenceFloor = 0.005f;
    static bool IsSilent(float[] samples)
    {
        foreach (var s in samples)
            if (s > SilenceFloor || s < -SilenceFloor) return false;
        return true;
    }

    /// <summary>Load the recognizer ahead of the first meeting (lesson 2: warm at launch).</summary>
    public void Warm()
    {
        if (!ModelReady) return;
        lock (_gate)
        {
            try { _transcriber ??= new Transcriber(); }
            catch (Exception ex) { App.Log($"transcriber warm failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Free the recognizer (~630MB). Used in low-memory mode so the notes LLM can load
    /// without the two models fighting over RAM. The next transcription reloads it.
    /// </summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_transcriber is null) return;
            try { _transcriber.Dispose(); } catch { }
            _transcriber = null;
            App.Log("ASR recognizer released (low-memory)");
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
