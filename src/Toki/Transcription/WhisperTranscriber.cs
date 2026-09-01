using System.Linq;
using SherpaOnnx;

namespace Toki.Transcription;

/// <summary>
/// Whisper base.en via sherpa-onnx - the ASK-BY-VOICE decoder. Whisper pads every clip to its 30s
/// mel window internally, so a 1-2 word push-to-talk clip transcribes cleanly where Parakeet (the
/// meeting engine) collapses it to "Yeah". Same sherpa-onnx runtime as Parakeet, so no second native
/// library. Construct once and reuse; each clip gets a fresh one-shot stream (no cross-clip state).
/// </summary>
public sealed class WhisperTranscriber : IDisposable
{
    readonly OfflineRecognizer _recognizer;

    public WhisperTranscriber()
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = AudioLoader.TargetRate;   // 16000; whisper builds its own log-mel
        config.ModelConfig.Whisper.Encoder = ModelManager.PathTo(ModelManager.Whisper, "base.en-encoder.int8.onnx");
        config.ModelConfig.Whisper.Decoder = ModelManager.PathTo(ModelManager.Whisper, "base.en-decoder.int8.onnx");
        config.ModelConfig.Whisper.Language = "en";
        config.ModelConfig.Whisper.Task = "transcribe";
        config.ModelConfig.Tokens = ModelManager.PathTo(ModelManager.Whisper, "base.en-tokens.txt");
        config.ModelConfig.ModelType = "whisper";
        config.ModelConfig.NumThreads = ThreadCount();
        config.ModelConfig.Provider = System.Environment.GetEnvironmentVariable("TOKI_ASR_PROVIDER") ?? "cpu";
        config.DecodingMethod = "greedy_search";
        _recognizer = new OfflineRecognizer(config);
    }

    /// <summary>Decode one 16kHz-mono clip to plain text. Empty string = nothing decoded (or the model
    /// hallucinated a stock phrase on near-silence, which we reject as if nothing was said).</summary>
    public string Transcribe(float[] samples16kMono)
    {
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(AudioLoader.TargetRate, samples16kMono);
        _recognizer.Decode(stream);
        var text = (stream.Result.Text ?? "").Trim();
        return IsNoiseHallucination(text) ? "" : text;
    }

    // Whisper was trained on YouTube captions, so on near-silence/noise it emits a handful of stock
    // caption phrases ("you", "Thanks for watching", music notes). Reject ONLY these whole-output
    // hallucinations - never real greetings like "thank you"/"bye", which the Ask small-talk layer
    // handles and which a user genuinely says. Match on the depunctuated, lowercased whole string.
    static readonly System.Collections.Generic.HashSet<string> Noise = new(System.StringComparer.Ordinal)
    {
        "you", "thanks for watching", "thank you for watching", "thanks for watching!",
        "please subscribe", "subscribe", "like and subscribe", "see you next time",
        "blank audio", "silence", "music", "applause",
    };

    internal static bool IsNoiseHallucination(string text)
    {
        if (text.Length == 0) return true;
        // Underscores/dashes (as in "[BLANK_AUDIO]") become spaces; everything else non-alphanumeric is
        // dropped, then runs of spaces collapse - so glyph-only output ("♪♪") normalises to empty.
        var chars = text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : (c is '_' or '-' or ' ') ? ' ' : '\0')
                        .Where(c => c != '\0').ToArray();
        var bare = string.Join(' ', new string(chars).Split(' ', System.StringSplitOptions.RemoveEmptyEntries));
        if (bare.Length == 0) return true;   // only punctuation / music glyphs
        return Noise.Contains(bare);
    }

    // Leave the machine responsive: two cores free, at least one thread, never more than four (whisper
    // base is small and the Ask path is interactive, not a batch decode).
    static int ThreadCount()
    {
        var n = System.Environment.ProcessorCount - 2;
        return System.Math.Clamp(n, 1, 4);
    }

    public void Dispose() => _recognizer.Dispose();
}
