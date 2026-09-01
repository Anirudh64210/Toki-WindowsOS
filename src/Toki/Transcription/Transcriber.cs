using System.Text;
using SherpaOnnx;

namespace Toki.Transcription;

/// <summary>
/// Whole-file transcription with Parakeet TDT via sherpa-onnx. The recognizer is
/// heavy (~630MB of ONNX) - construct once and reuse. Each file gets a fresh
/// stream (offline streams are one-shot); the recognizer carries no cross-file
/// state, so there's no leaked state between meetings (lesson 5).
/// </summary>
public sealed class Transcriber : IDisposable
{
    // Split into a new segment when tokens are separated by more than this silence...
    const double SegmentGapSeconds = 0.8;
    // ...but ONLY once the line already has this many words, so slow/pausey speech (a presenter
    // pausing between words) can't shatter into one word per line. Sentence ends still break freely.
    const int MinWordsForPauseBreak = 8;

    // Whole-file decode is the rule (never hand-chunk), but the native recognizer can throw on
    // very long inputs; past SafeMaxSamples the file is split into windows cut at quiet points
    // so a word is never sliced. Env overrides (seconds) let tests trigger the windowed path.
    static readonly int SafeMaxSamples = SecondsEnv("TOKI_ASR_MAXSEC", 6 * 60) * AudioLoader.TargetRate;
    static readonly int WindowSamples  = SecondsEnv("TOKI_ASR_WINSEC", 3 * 60) * AudioLoader.TargetRate;
    const int SearchSamples  = 5 * AudioLoader.TargetRate;       // ±5s quiet-point search
    const int FrameSamples   = 400;                              // 25ms energy frame

    static int SecondsEnv(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var s) && s > 0 ? s : fallback;

    readonly OfflineRecognizer _recognizer;

    public Transcriber()
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = AudioLoader.TargetRate; // 16000
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = ModelManager.PathTo(ModelManager.Parakeet, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = ModelManager.PathTo(ModelManager.Parakeet, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = ModelManager.PathTo(ModelManager.Parakeet, "joiner.int8.onnx");
        config.ModelConfig.Tokens = ModelManager.PathTo(ModelManager.Parakeet, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer"; // required for Parakeet TDT
        config.ModelConfig.NumThreads = ThreadCount();
        config.ModelConfig.Provider = Environment.GetEnvironmentVariable("TOKI_ASR_PROVIDER") ?? "cpu";
        config.DecodingMethod = "greedy_search";
        _recognizer = new OfflineRecognizer(config);
    }

    /// <summary>
    /// Transcribe one stream's 16kHz-mono samples into timestamped segments.
    /// Empty result = nobody spoke; returns an empty list (silence is not failure).
    /// </summary>
    public IReadOnlyList<TranscriptSegment> Transcribe(float[] samples16kMono, Speaker speaker)
    {
        // Speech gate: decode only where somebody is speaking - Parakeet fed near-silence returns
        // confident nonsense, not nothing. Level FIRST, then gate: Silero cannot read a far-too-
        // quiet signal either, so both the gate and the recogniser see the lifted stream.
        var gain = SpeechGain(samples16kMono);
        if (SpeechRegions(samples16kMono, gain) is { } regions)
        {
            // Zero regions is an answer (nobody spoke), not a failure - never fall through to
            // the ungated decode, the input most likely to come back as confident nonsense.
            if (regions.Count == 0)
            {
                App.Log($"ASR gate: no speech in {samples16kMono.Length / (double)AudioLoader.TargetRate:F0}s stream, decoding nothing");
                return [];
            }
            var gated = new List<TranscriptSegment>();
            var speech = regions.Sum(r => r.Length);
            App.Log($"ASR gate: {regions.Count} speech region(s), {speech / (double)AudioLoader.TargetRate:F0}s of "
                  + $"{samples16kMono.Length / (double)AudioLoader.TargetRate:F0}s "
                  + $"({100.0 * speech / Math.Max(1, samples16kMono.Length):F0}% speech), gain x{gain:F1}");
            foreach (var r in regions)
            {
                try { gated.AddRange(TranscribeWindow(samples16kMono, r.Start, r.End, r.StartSeconds, speaker, gain)); }
                catch (Exception ex) { App.Log($"ASR region {r.StartSeconds:F0}s failed, skipping: {ex.GetType().Name}: {ex.Message}"); }
            }
            return gated;
        }

        if (samples16kMono.Length <= SafeMaxSamples)
            return TranscribeWindow(samples16kMono, 0, samples16kMono.Length, 0, speaker);

        // Long recording: walk it in windows, each cut at a quiet point so we never
        // slice a word in half. Timestamps stay meeting-relative via the offset.
        App.Log($"ASR long path: {samples16kMono.Length} samples ({samples16kMono.Length / (double)AudioLoader.TargetRate:F0}s), windowing");
        var all = new List<TranscriptSegment>();
        var pos = 0;
        while (pos < samples16kMono.Length)
        {
            var target = Math.Min(pos + WindowSamples, samples16kMono.Length);
            var cut = target >= samples16kMono.Length ? samples16kMono.Length : QuietCut(samples16kMono, target, SearchSamples);
            if (cut <= pos) cut = target; // guarantee forward progress
            var offsetSec = pos / (double)AudioLoader.TargetRate;
            App.Log($"ASR window @ {offsetSec:F0}s [{pos}..{cut}] ({(cut - pos) / (double)AudioLoader.TargetRate:F0}s)");
            try { all.AddRange(TranscribeWindow(samples16kMono, pos, cut, offsetSec, speaker)); }
            catch (Exception ex) { App.Log($"ASR window {offsetSec:F0}s failed, skipping: {ex.GetType().Name}: {ex.Message}"); }
            pos = cut;
        }
        App.Log($"ASR long path done: {all.Count} segments");
        return all;
    }

    /// <summary>
    /// Slice decode for LIVE incremental transcription: decode [start,end) at a meeting-time
    /// offset via the same one-shot-stream path as the whole-file decode. Gated and levelled like
    /// the whole-file path - the gate must sit on the LIVE path, the one meetings actually take.
    /// </summary>
    public IReadOnlyList<TranscriptSegment> TranscribeSlice(float[] samples, int start, int end, double offsetSec, Speaker speaker)
    {
        var len = end - start;
        if (len <= 0) return [];
        var slice = start == 0 && end == samples.Length ? samples : samples[start..end];

        // Level first, then gate - same order and reasons as the whole-file path. Gain is per
        // window; a pure-silence window has nothing worth lifting to, and nothing gets decoded.
        var gain = SpeechGain(slice);
        var regions = LiveRegions(slice, gain);
        if (regions is null)
        {
            // Gate unavailable: decode ungated, but the final flush after a stall can hand this a
            // backlog past what one decode call survives - chunk anything oversized at quiet points.
            if (len <= SafeMaxSamples)
                return TranscribeWindow(samples, start, end, offsetSec, speaker);
            var segs0 = new List<TranscriptSegment>();
            var pos = start;
            while (pos < end)
            {
                var target = Math.Min(pos + WindowSamples, end);
                var cut = target >= end ? end : QuietCut(samples, target, SearchSamples);
                if (cut <= pos) cut = target;
                try { segs0.AddRange(TranscribeWindow(samples, pos, cut, offsetSec + (pos - start) / (double)AudioLoader.TargetRate, speaker)); }
                catch (Exception ex) { App.Log($"live flush window failed, skipping: {ex.GetType().Name}: {ex.Message}"); }
                pos = cut;
            }
            return segs0;
        }

        var segs = new List<TranscriptSegment>();
        foreach (var r in regions)
        {
            try { segs.AddRange(TranscribeWindow(slice, r.Start, r.End, offsetSec + r.StartSeconds, speaker, gain)); }
            catch (Exception ex) { App.Log($"live region {offsetSec + r.StartSeconds:F0}s failed, skipping: {ex.GetType().Name}: {ex.Message}"); }
        }
        return segs;
    }

    // The live gate is PERSISTENT (one Silero session, reset per window) because it runs every ~15
    // seconds for the whole meeting - rebuilding the ONNX session each window is pure waste. All
    // live decode is serialized by TranscriptionService, so one instance is safe.
    SpeechGate? _liveGate;
    bool _liveGateBroken;

    List<SpeechRegion>? LiveRegions(float[] slice, float gain)
    {
        if (Environment.GetEnvironmentVariable("TOKI_ASR_NOGATE") == "1") return null;   // same A/B seam
        if (_liveGateBroken || !ModelManager.VadInstalled) return null;
        try
        {
            _liveGate ??= new SpeechGate(ModelManager.VadPath);
            return _liveGate.Find(slice, gain: gain);
        }
        catch (Exception ex)
        {
            // One failure disables the gate for the rest of this transcriber's life rather than
            // retrying (and re-logging) every 15 seconds of a long meeting.
            _liveGateBroken = true;
            App.Log($"live ASR gate failed, decoding ungated from here on: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Public quiet-point finder for the live windower - cut here to avoid slicing a word.</summary>
    public static int FindQuietCut(float[] samples, int center, int radius) => QuietCut(samples, center, radius);

    /// <summary>
    /// Where speech actually is, or null when the gate cannot run (missing model, or a failure
    /// inside it). Null means "fall back to the ungated path", never "no speech".
    /// </summary>
    static List<SpeechRegion>? SpeechRegions(float[] samples, float gain)
    {
        if (Environment.GetEnvironmentVariable("TOKI_ASR_NOGATE") == "1") return null;   // A/B seam
        if (!ModelManager.VadInstalled) { App.Log("ASR gate: silero_vad.onnx not installed - decoding ungated"); return null; }
        try
        {
            using var gate = new SpeechGate(ModelManager.VadPath);
            return gate.Find(samples, gain: gain);
        }
        catch (Exception ex)
        {
            App.Log($"ASR gate failed, decoding ungated: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Decode one slice [start,end) with a fresh stream; timestamps offset to meeting time.
    /// <paramref name="gain"/> lifts a quiet stream to a level the recogniser can read (see
    /// <see cref="SpeechGain"/>); 1 leaves the samples exactly as they are.</summary>
    List<TranscriptSegment> TranscribeWindow(float[] samples, int start, int end, double offsetSec, Speaker speaker, float gain = 1f)
    {
        var len = end - start;
        if (len <= 0) return [];
        // The no-copy fast path is only safe at unity gain - scaling in place would mutate the
        // caller's buffer, which diarisation and the evidence clips read afterwards.
        var slice = gain == 1f && start == 0 && end == samples.Length ? samples : samples[start..end];
        if (gain != 1f)
            for (var i = 0; i < slice.Length; i++) slice[i] *= gain;
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(AudioLoader.TargetRate, slice);
        _recognizer.Decode(stream);
        var r = stream.Result;
        if (string.IsNullOrWhiteSpace(r.Text) || r.Tokens is null || r.Tokens.Length == 0)
            return [];
        if (Environment.GetEnvironmentVariable("TOKI_ASR_DUMPTIMES") == "1")
            App.Log($"asr arrays: tokens={r.Tokens.Length} times={r.Timestamps?.Length ?? -1} durations={r.Durations?.Length ?? -1}");
        return GroupIntoSegments(r.Tokens, r.Timestamps, r.Durations, offsetSec, speaker);
    }

    // Per-second RMS of a well-levelled meeting stream, measured, rounded down.
    const double TargetSpeechRms = 0.06;
    // Ceiling on the lift: past this the source is broken (muted mic, dead input), and
    // amplifying its noise floor only manufactures hallucination.
    const float MaxGain = 20f;
    // Leave headroom so a lifted peak cannot clip; clipping costs more accuracy than the gain buys.
    const float PeakCeiling = 0.95f;

    /// <summary>
    /// Gain to lift a quiet stream to a level the recogniser was trained on. Level is the 95th
    /// percentile of 25ms frame energy (a mean over a mostly-silent track lies, and a percentile
    /// needs no VAD pass, so this runs BEFORE the gate); headroom comes from the 99.9th-percentile
    /// sample so one transient cannot veto the lift. Only ever lifts, never attenuates.
    /// </summary>
    static float SpeechGain(float[] samples)
    {
        if (samples.Length < FrameSamples) return 1f;

        var frames = samples.Length / FrameSamples;
        var energies = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            var at = f * FrameSamples;
            for (var i = at; i < at + FrameSamples; i++) sum += (double)samples[i] * samples[i];
            energies[f] = Math.Sqrt(sum / FrameSamples);
        }
        Array.Sort(energies);
        var level = energies[(int)(frames * 0.95)];
        // Below this floor there is no speech to lift, only a noise floor: lifting a silent live
        // window's hiss re-arms the hallucination path the gate closed. 2e-4 sits under the
        // quietest real speech measured and above electrical noise.
        if (level <= 2e-4) return 1f;
        var gain = TargetSpeechRms / level;
        if (gain <= 1.0) return 1f;                   // already at or above the target

        // Peak headroom from the 99.9th percentile magnitude, so isolated transients cannot veto it.
        var mags = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++) mags[i] = Math.Abs(samples[i]);
        Array.Sort(mags);
        var peak = mags[(int)((samples.Length - 1) * 0.999)];
        var headroom = peak > 1e-6 ? PeakCeiling / peak : MaxGain;

        return (float)Math.Min(Math.Min(gain, headroom), MaxGain);
    }

    /// <summary>Index of the quietest 25ms frame within ±radius of center - a safe place to cut.</summary>
    static int QuietCut(float[] samples, int center, int radius)
    {
        var lo = Math.Max(FrameSamples, center - radius);
        var hi = Math.Min(samples.Length - FrameSamples, center + radius);
        if (hi <= lo) return center;
        var bestIdx = center;
        var bestEnergy = double.MaxValue;
        for (var i = lo; i < hi; i += FrameSamples)
        {
            double sum = 0;
            for (var j = i; j < i + FrameSamples; j++) sum += samples[j] * samples[j];
            if (sum < bestEnergy) { bestEnergy = sum; bestIdx = i; }
        }
        return bestIdx;
    }

    static List<TranscriptSegment> GroupIntoSegments(string[] tokens, float[] times, float[] durations, double offsetSec, Speaker speaker)
    {
        var segments = new List<TranscriptSegment>();
        var sb = new StringBuilder();
        double segStart = times.Length > 0 ? times[0] : 0;
        double prevEnd = segStart;
        var words = 0;   // word-start tokens accumulated in the current line

        void Flush(double end)
        {
            var text = Detokenize(sb.ToString());
            if (text.Length > 0)
            {
                // NEVER cast an unchecked double to int. A NaN or an infinity from the recogniser
                // becomes int.MinValue silently, and that is exactly what shipped: stored lines
                // whose EndMs was int.MinValue, so the evidence clip collapsed to its 500ms floor
                // and played a fragment of the sentence it claimed. Measured with --evidencetest on
                // meeting 108. An end can also never precede its own start.
                var startMs = Ms(segStart + offsetSec);
                var endMs = Math.Max(startMs, Ms(end + offsetSec));
                segments.Add(new TranscriptSegment(speaker, startMs, endMs, text));
            }
            sb.Clear();
            words = 0;
        }

        static int Ms(double seconds) =>
            double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0
                ? 0
                : (int)Math.Min(seconds * 1000, int.MaxValue);

        for (var i = 0; i < tokens.Length; i++)
        {
            // A token time that is not a real number carries the whole line away with it, so treat
            // it as "no new information" and keep the clock where it was.
            var t = i < times.Length ? times[i] : prevEnd;
            var start = double.IsNaN(t) || double.IsInfinity(t) || t < 0 ? prevEnd : t;
            var d = i < durations.Length ? durations[i] : 0;
            if (double.IsNaN(d) || double.IsInfinity(d) || d < 0) d = 0;
            var end = start + d;
            // Break ONLY at a word boundary (this Parakeet build marks word starts with a leading
            // space; ▁ handled for other SentencePiece builds) - a time-gap split alone cuts
            // mid-word on pausey speech. At a boundary: sentence end always breaks; a real pause
            // (>0.8s) breaks only past MinWordsForPauseBreak words, so lines land per-utterance
            // (giant run-ons would break echo de-dup, one-word lines were the regression).
            var isWordStart = StartsWord(tokens[i]);
            if (sb.Length > 0 && isWordStart)
            {
                var longPause = start - prevEnd > SegmentGapSeconds && words >= MinWordsForPauseBreak;
                if (EndsSentence(sb) || longPause)
                {
                    Flush(prevEnd);
                    segStart = start;
                }
            }
            if (isWordStart) words++;
            sb.Append(tokens[i]);
            prevEnd = end;
        }
        if (sb.Length > 0) Flush(prevEnd);
        return segments;
    }

    /// <summary>True if this token begins a new word - marked by a leading space in this
    /// Parakeet build (" word"), or a ▁ marker in other SentencePiece builds.</summary>
    static bool StartsWord(string tok) =>
        tok.Length > 0 && (tok[0] == '▁' || char.IsWhiteSpace(tok[0]));

    /// <summary>True if the text so far ends a sentence (last real char is . ? or !),
    /// ignoring the ▁ word markers and whitespace. Used to break segments per-utterance.</summary>
    static bool EndsSentence(StringBuilder sb)
    {
        for (var i = sb.Length - 1; i >= 0; i--)
        {
            var c = sb[i];
            if (c == '▁' || char.IsWhiteSpace(c)) continue;
            return c is '.' or '?' or '!';
        }
        return false;
    }

    /// <summary>SentencePiece detokenize: the ▁ marker denotes a word boundary (space).</summary>
    static string Detokenize(string joined) =>
        joined.Replace("▁", " ").Replace("  ", " ").Trim();

    // ASR is CPU-bound; use about half the cores. Streams are decoded sequentially
    // (concurrent decode corrupts output), so this leaves headroom for the rest of
    // the PC to stay responsive. Env override for tuning.
    static int ThreadCount()
    {
        if (Environment.GetEnvironmentVariable("TOKI_ASR_THREADS") is { } s && int.TryParse(s, out var n) && n > 0)
            return n;
        return Math.Clamp(Environment.ProcessorCount / 2, 3, 8);
    }

    public void Dispose()
    {
        _liveGate?.Dispose();
        _recognizer.Dispose();
    }
}
