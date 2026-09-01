using System.Collections.Generic;
using SherpaOnnx;

namespace Toki.Transcription;

/// <summary>
/// The LIVE-PREVIEW decoder: a streaming zipformer-transducer via sherpa-onnx that emits words as they
/// are spoken (~1s), so the recording view fills instantly instead of waiting ~15-21s for an offline
/// Parakeet window. PREVIEW ONLY and disposable - the SAVED transcript is still the accurate whole-file
/// pass. One recognizer, one persistent stream per speaker (You/Them). Same sherpa-onnx runtime as
/// Parakeet, so no new native library.
/// </summary>
public sealed class StreamingTranscriber : IDisposable
{
    const int Rate = AudioLoader.TargetRate;   // 16000

    readonly OnlineRecognizer _recognizer;
    readonly Dictionary<Speaker, OnlineStream> _streams = new();
    readonly object _gate = new();

    public StreamingTranscriber()
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = Rate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = ModelManager.PathTo(ModelManager.StreamingAsr, "encoder-epoch-99-avg-1.int8.onnx");
        config.ModelConfig.Transducer.Decoder = ModelManager.PathTo(ModelManager.StreamingAsr, "decoder-epoch-99-avg-1.int8.onnx");
        config.ModelConfig.Transducer.Joiner = ModelManager.PathTo(ModelManager.StreamingAsr, "joiner-epoch-99-avg-1.int8.onnx");
        config.ModelConfig.Tokens = ModelManager.PathTo(ModelManager.StreamingAsr, "tokens.txt");
        config.ModelConfig.NumThreads = ThreadCount();
        config.ModelConfig.Provider = System.Environment.GetEnvironmentVariable("TOKI_ASR_PROVIDER") ?? "cpu";
        config.DecodingMethod = "greedy_search";
        // Endpointing splits the live preview into lines: an utterance ends after a beat of trailing
        // silence (or a long run), which finalizes the current line and starts a fresh one.
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = 2.4f;   // end after 2.4s of silence following speech
        config.Rule2MinTrailingSilence = 1.2f;   // ...or 1.2s once a decode has produced text
        config.Rule3MinUtteranceLength = 20f;    // ...or force a break after 20s of continuous speech
        _recognizer = new OnlineRecognizer(config);
    }

    OnlineStream StreamFor(Speaker sp)
    {
        if (!_streams.TryGetValue(sp, out var s)) { s = _recognizer.CreateStream(); _streams[sp] = s; }
        return s;
    }

    /// <summary>Feed new 16kHz-mono samples for a speaker and pull the current state. Returns the
    /// growing PARTIAL line, and a non-null FINALIZED string when an utterance just ended (the line to
    /// commit). Serialized: a streaming recognizer is not safe to decode concurrently.</summary>
    public (string Partial, string? Finalized) Accept(Speaker sp, float[] samples, int count)
    {
        lock (_gate)
        {
            var st = StreamFor(sp);
            st.AcceptWaveform(Rate, count == samples.Length ? samples : samples[..count]);
            while (_recognizer.IsReady(st)) _recognizer.Decode(st);
            var text = (_recognizer.GetResult(st).Text ?? "").Trim();
            if (_recognizer.IsEndpoint(st))
            {
                _recognizer.Reset(st);
                return ("", string.IsNullOrWhiteSpace(text) ? null : text);
            }
            return (text, null);
        }
    }

    /// <summary>Flush a speaker's stream at stop: mark input finished, decode the tail, and return any
    /// last partial as a finalized line.</summary>
    public string? Flush(Speaker sp)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(sp, out var st)) return null;
            st.InputFinished();
            while (_recognizer.IsReady(st)) _recognizer.Decode(st);
            var text = (_recognizer.GetResult(st).Text ?? "").Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }

    static int ThreadCount()
    {
        var n = System.Environment.ProcessorCount - 2;
        return System.Math.Clamp(n, 1, 2);   // preview is light; leave cores for Parakeet + the app
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var s in _streams.Values) s.Dispose();
            _streams.Clear();
            _recognizer.Dispose();
        }
    }
}
