using System.Text;
using SherpaOnnx;

namespace Toki.Transcription;

/// <summary>
/// Whole-file transcription with Parakeet TDT via sherpa-onnx. The recognizer is
/// heavy (~630MB of ONNX) — construct once and reuse. Each file gets a fresh
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

    // Whole-file decode is best (lesson 3) but the native recognizer THREW on a
    // real 35-minute clip ("External component has thrown an exception") and the
    // whole meeting was lost. So: normal-length meetings still take the single
    // whole-file path; anything longer is split into windows cut at quiet points
    // (avoids slicing mid-word) and decoded piece by piece. Never crash, never
    // lose a long meeting.
    // Env override (seconds) lets tests trigger the windowed path on a short clip.
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
    /// Public slice decode for LIVE incremental transcription: decode [start,end) of a buffer at a
    /// given meeting-time offset. Same one-shot-stream path the whole-file decode uses, so quality
    /// is identical — the live path just hands it ~15s windows during the meeting instead of the
    /// whole file at the end.
    /// </summary>
    public IReadOnlyList<TranscriptSegment> TranscribeSlice(float[] samples, int start, int end, double offsetSec, Speaker speaker) =>
        TranscribeWindow(samples, start, end, offsetSec, speaker);

    /// <summary>Public quiet-point finder for the live windower — cut here to avoid slicing a word.</summary>
    public static int FindQuietCut(float[] samples, int center, int radius) => QuietCut(samples, center, radius);

    /// <summary>Decode one slice [start,end) with a fresh stream; timestamps offset to meeting time.</summary>
    List<TranscriptSegment> TranscribeWindow(float[] samples, int start, int end, double offsetSec, Speaker speaker)
    {
        var len = end - start;
        if (len <= 0) return [];
        var slice = start == 0 && end == samples.Length ? samples : samples[start..end];
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(AudioLoader.TargetRate, slice);
        _recognizer.Decode(stream);
        var r = stream.Result;
        if (string.IsNullOrWhiteSpace(r.Text) || r.Tokens is null || r.Tokens.Length == 0)
            return [];
        return GroupIntoSegments(r.Tokens, r.Timestamps, r.Durations, offsetSec, speaker);
    }

    /// <summary>Index of the quietest 25ms frame within ±radius of center — a safe place to cut.</summary>
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
                segments.Add(new TranscriptSegment(speaker, (int)((segStart + offsetSec) * 1000), (int)((end + offsetSec) * 1000), text));
            sb.Clear();
            words = 0;
        }

        for (var i = 0; i < tokens.Length; i++)
        {
            var start = i < times.Length ? times[i] : prevEnd;
            var end = start + (i < durations.Length ? durations[i] : 0);
            // Break ONLY at a WORD boundary. This Parakeet build marks a new word with a
            // LEADING SPACE on the token (" Rashmi" = " R","as","h","m","i"); sub-word pieces
            // carry no leading space. Splitting on a time-gap alone would cut mid-word on
            // hesitant/pausey speech, shattering "Deepika" into "D / eep / ik / a".
            // At a word boundary we break on the end of a sentence (". ? !") — always — OR on a
            // real pause (>0.8s), but the pause break only fires once the line already has enough
            // words (MinWordsForPauseBreak). Otherwise a presenter pausing between words produces
            // one word per line (the real regression). So lines land per-utterance: not per-word,
            // and not one giant run-on per window (which would break echo de-dup).
            // (▁ is handled too, for other SentencePiece model builds.)
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

    /// <summary>True if this token begins a new word — marked by a leading space in this
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

    public void Dispose() => _recognizer.Dispose();
}
