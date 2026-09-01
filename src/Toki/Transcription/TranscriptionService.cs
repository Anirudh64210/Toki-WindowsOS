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
    WhisperTranscriber? _whisper;

    public static bool ModelReady => ModelManager.IsInstalled(ModelManager.Parakeet);

    /// <summary>Whisper is the ASK-BY-VOICE decoder, downloaded after Parakeet. When present, short
    /// push-to-talk clips go through it (robust on 1-2 word clips); when absent, they fall back to
    /// Parakeet, so the feature still works, just less reliably on the shortest utterances.</summary>
    public static bool WhisperReady => ModelManager.WhisperInstalled;

    TranscriptionService() { }

    /// <summary>
    /// Transcribe both streams of a recording folder and merge them. Sequential on purpose:
    /// a sherpa-onnx offline recognizer is NOT safe to run concurrently, even as two instances
    /// (concurrent decode corrupts output). Returns null if the model isn't available.
    /// </summary>
    /// <param name="attendees">Display names from the calendar event; typed by a human,
    /// so they outrank anything counted from past transcripts.</param>
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

            // Merge joins each person's consecutive lines into turns and lifts short backchannels
            // out of the middle of someone else's sentence.
            var merged = TranscriptMerger.Merge(you, them);

            // Text repairs run LAST so they see whole turns, not fragments - a name split across
            // two fragments is one word by the time it is judged.
            merged = Polish(merged, attendees);

            App.Log($"transcribed in {sw.ElapsedMilliseconds} ms: you={you.Count} them={them.Count} -> merged {merged.Count} segments");
            return merged;
        }
    }

    /// <summary>
    /// Every edit the last <see cref="Process"/> or <see cref="PolishNames"/> made. Read straight
    /// after the call on the same thread, so the caller can store the edits WITH the meeting.
    /// </summary>
    public IReadOnlyList<Toki.Storage.TextFix> LastCorrections { get; private set; } = [];

    /// <summary>
    /// Repair an ALREADY-merged transcript - the live path never goes through
    /// <see cref="Process"/>, and it is the path most meetings actually take.
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

        try
        {
            // Readability LAST: cosmetic by design, so it must never feed the earlier passes; a
            // name inside "august twenty first" has already been judged as spoken.
            var (tidied, tidies) = Readability.Clean(segments);
            record.AddRange(tidies.Select(td => new Toki.Storage.TextFix(td.From, td.To, "tidy")));
            segments = tidied;
        }
        catch (Exception ex) { App.Log($"readability skipped (non-fatal): {ex.Message}"); }

        LastCorrections = record
            .GroupBy(f => (f.From.ToLowerInvariant(), f.To.ToLowerInvariant()))
            .Select(g => g.First())
            .ToList();
        return segments;
    }

    /// <summary>
    /// Decode one LIVE window [start,end) using the SHARED recognizer under the SAME gate as the
    /// stop-time decode (concurrent decode corrupts sherpa-onnx output). Null means the decode
    /// FAILED - count the window as lost; an empty list means it genuinely contained no speech.
    /// </summary>
    public IReadOnlyList<TranscriptSegment>? DecodeLive(float[] samples, int start, int end, double offsetSec, Speaker speaker)
    {
        if (!ModelReady || end <= start) return [];
        lock (_gate)
        {
            try
            {
                _transcriber ??= new Transcriber();
                return _transcriber.TranscribeSlice(samples, start, end, offsetSec, speaker);
            }
            catch (Exception ex) { App.Log($"live decode {speaker} @ {offsetSec:F0}s failed: {ex.Message}"); return null; }
        }
    }

    /// <summary>
    /// Transcribe a SINGLE mic recording (the Ask-bar "ask by voice" flow) to plain text, using the
    /// same gated recognizer. Returns null if the model isn't installed or the decode errors; "" if the
    /// clip was silent; otherwise the joined text. Runs on a background thread (never the UI thread).
    /// </summary>
    public string? TranscribeFile(string wavPath)
    {
        // Whisper is the preferred Ask decoder; Parakeet is the fallback when it isn't downloaded yet.
        if (!WhisperReady && !ModelReady) { App.Log("voice transcribe skipped: no ASR model installed"); return null; }
        if (App.LowMemoryMode) Toki.Llm.LlmService.Instance.Release();
        lock (_gate)
        {
            try
            {
                var samples = AudioLoader.To16kMono(wavPath);
                if (IsSilent(samples)) return "";
                // Parakeet TDT returns an EMPTY decode when fed trailing silence (NVIDIA-NeMo #15757),
                // and a push-to-talk clip always ends in silence (the user releases the key a beat after
                // speaking). Trim the quiet tail - Ask path ONLY, never the shared meeting Transcribe -
                // keeping a small pad so no coda consonant is clipped.
                samples = TrimSilenceEnds(samples);

                // WHISPER first: it pads to its 30s window, so a 1-2 word clip decodes cleanly where
                // Parakeet collapses "Hi Toki" to "Yeah". Fall back to Parakeet only if whisper is not
                // installed yet (it downloads in the background after onboarding).
                if (WhisperReady)
                {
                    try
                    {
                        _whisper ??= new WhisperTranscriber();
                        return Readability.Clean(_whisper.Transcribe(samples)).Text;
                    }
                    catch (Exception ex)
                    {
                        // A whisper failure must not kill the feature - drop to Parakeet below.
                        App.Log($"whisper voice transcribe failed, falling back to Parakeet: {ex.Message}");
                    }
                }
                if (!ModelReady) return null;
                _transcriber ??= new Transcriber();
                var segs = _transcriber.Transcribe(samples, Speaker.You);
                return string.Join(" ", segs.Select(s => s.Text)).Trim();
            }
            catch (Exception ex) { App.Log($"voice transcribe failed: {ex.Message}"); return null; }
        }
    }

    /// <summary>Load the whisper recognizer ahead of the first Ask, off the meeting path.</summary>
    public void WarmWhisper()
    {
        if (!WhisperReady) return;
        lock (_gate)
        {
            try { _whisper ??= new WhisperTranscriber(); }
            catch (Exception ex) { App.Log($"whisper warm failed: {ex.Message}"); }
        }
    }

    IReadOnlyList<TranscriptSegment> TranscribeStream(string wavPath, Speaker speaker)
    {
        if (!File.Exists(wavPath)) return [];
        try
        {
            var samples = AudioLoader.To16kMono(wavPath);
            // Skip a near-silent stream (e.g. mic muted the whole call). Silence is not
            // failure: a muted stream just isn't transcribed.
            if (IsSilent(samples))
            {
                App.Log($"{speaker} stream near-silent, skipping ASR ({samples.Length / (double)AudioLoader.TargetRate:F0}s)");
                return [];
            }
            return _transcriber!.Transcribe(samples, speaker);
        }
        catch (Exception ex)
        {
            // Parakeet errored (not silence) - this is where the whisper.cpp fallback
            // will run; Whisper only on actual error, never on silence.
            App.Log($"Parakeet error on {speaker} stream: {ex.Message} (whisper fallback: TODO)");
            return [];
        }
    }

    // "Silent" = no sample crosses ~-46 dBFS: trips only on genuine digital silence
    // (muted mic / no loopback), never on quiet speech. Early-exits on the first loud sample.
    const float SilenceFloor = 0.005f;
    static bool IsSilent(float[] samples)
    {
        foreach (var s in samples)
            if (s > SilenceFloor || s < -SilenceFloor) return false;
        return true;
    }

    /// <summary>Trim only the quiet TAIL of a push-to-talk clip, keeping a ~125ms pad so no coda
    /// consonant is clipped. TRAILING only, by design: the Parakeet empty-decode bug (NeMo #15757) is
    /// specifically about trailing silence, and trimming the HEAD risks eating the first word (a soft
    /// "Hi" before a louder "Toki") - leading silence is harmless to the recogniser, so it stays. The
    /// gate is PEAK-RELATIVE (a quiet mic still trims) with an absolute floor (room noise can't 'find'
    /// speech). Returns the input unchanged when there is nothing clear to trim. Ask path only.</summary>
    internal static float[] TrimSilenceEnds(float[] s)
    {
        if (s.Length == 0) return s;
        float peak = 0f;
        foreach (var x in s) { var a = x < 0 ? -x : x; if (a > peak) peak = a; }
        var thresh = Math.Max(0.01f, peak * 0.08f);
        int last = s.Length - 1;
        while (last >= 0 && (s[last] < thresh && s[last] > -thresh)) last--;
        if (last < 0) return s;   // all quiet - leave it to the gate/decoder
        const int pad = AudioLoader.TargetRate / 8;   // 125ms at 16k
        last = Math.Min(s.Length - 1, last + pad);
        if (last == s.Length - 1) return s;   // no trailing silence past the pad
        var outp = new float[last + 1];
        Array.Copy(s, 0, outp, 0, outp.Length);   // keep the head intact - protect the first word
        return outp;
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
