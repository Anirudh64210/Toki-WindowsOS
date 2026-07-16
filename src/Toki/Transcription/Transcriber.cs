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
    // Split into a new segment when tokens are separated by more than this silence.
    const double SegmentGapSeconds = 0.8;

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
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        _recognizer = new OfflineRecognizer(config);
    }

    /// <summary>
    /// Transcribe one stream's 16kHz-mono samples into timestamped segments.
    /// Empty result = nobody spoke; returns an empty list (silence is not failure).
    /// </summary>
    public IReadOnlyList<TranscriptSegment> Transcribe(float[] samples16kMono, Speaker speaker)
    {
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(AudioLoader.TargetRate, samples16kMono);
        _recognizer.Decode(stream);
        var r = stream.Result;
        if (string.IsNullOrWhiteSpace(r.Text) || r.Tokens is null || r.Tokens.Length == 0)
            return [];
        return GroupIntoSegments(r.Tokens, r.Timestamps, r.Durations, speaker);
    }

    static List<TranscriptSegment> GroupIntoSegments(string[] tokens, float[] times, float[] durations, Speaker speaker)
    {
        var segments = new List<TranscriptSegment>();
        var sb = new StringBuilder();
        double segStart = times.Length > 0 ? times[0] : 0;
        double prevEnd = segStart;

        void Flush(double end)
        {
            var text = Detokenize(sb.ToString());
            if (text.Length > 0)
                segments.Add(new TranscriptSegment(speaker, (int)(segStart * 1000), (int)(end * 1000), text));
            sb.Clear();
        }

        for (var i = 0; i < tokens.Length; i++)
        {
            var start = i < times.Length ? times[i] : prevEnd;
            var end = start + (i < durations.Length ? durations[i] : 0);
            if (sb.Length > 0 && start - prevEnd > SegmentGapSeconds)
            {
                Flush(prevEnd);
                segStart = start;
            }
            sb.Append(tokens[i]);
            prevEnd = end;
        }
        if (sb.Length > 0) Flush(prevEnd);
        return segments;
    }

    /// <summary>SentencePiece detokenize: the ▁ marker denotes a word boundary (space).</summary>
    static string Detokenize(string joined) =>
        joined.Replace("▁", " ").Replace("  ", " ").Trim();

    public void Dispose() => _recognizer.Dispose();
}
