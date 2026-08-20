using SherpaOnnx;

namespace Toki.Transcription;

/// <summary>One region of a recording that actually contains speech, in 16kHz sample indices.</summary>
public readonly record struct SpeechRegion(int Start, int End)
{
    public int Length => End - Start;
    public double StartSeconds => Start / (double)AudioLoader.TargetRate;
    public double EndSeconds => End / (double)AudioLoader.TargetRate;
}

/// <summary>
/// Finds where somebody is ACTUALLY SPEAKING, so the recognizer is never asked to transcribe
/// silence (Parakeet fed near-silence returns confident nonsense, not nothing). Silero VAD
/// decides this properly rather than by loudness: <see cref="Transcriber.FindQuietCut"/> always
/// returns an answer and cannot say "there is nothing here" - a gate has to be able to say no.
/// Runs on the sherpa-onnx runtime already shipped (Microsoft.ML.OnnxRuntime conflicts with it).
/// </summary>
public sealed class SpeechGate : IDisposable
{
    // Silero's own frame size at 16kHz. Not a tuning knob: the model is exported for this window.
    const int WindowSize = 512;

    // A pause shorter than this is part of one utterance, not a boundary: a smaller value
    // fragments a quick exchange, and every region boundary is a chance to clip a word.
    const float MinSilenceSeconds = 0.50f;

    // The shortest run kept. Real conversation is full of one-word turns ("yep", "go ahead");
    // a higher floor measurably throws away genuine short speech.
    const float MinSpeechSeconds = 0.10f;

    // Cap on one region, so a continuous speaker still arrives in decodable pieces rather than one
    // twenty-minute block. Regions are cut at the model's own boundaries, never mid-word by a timer.
    const float MaxSpeechSeconds = 25f;

    // LOWER than Silero's 0.5 default, deliberately: a false negative is a word the user will
    // never see, a false positive is only a second of quiet decoded to nothing. The costs are
    // asymmetric, so the threshold errs toward keeping audio.
    const float Threshold = 0.30f;

    readonly VoiceActivityDetector _vad;

    public SpeechGate(string modelPath)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = modelPath;
        config.SileroVad.Threshold = Threshold;
        config.SileroVad.MinSilenceDuration = MinSilenceSeconds;
        config.SileroVad.MinSpeechDuration = MinSpeechSeconds;
        config.SileroVad.MaxSpeechDuration = MaxSpeechSeconds;
        config.SileroVad.WindowSize = WindowSize;
        config.SampleRate = AudioLoader.TargetRate;
        config.NumThreads = 1;                 // tiny model; a thread pool costs more than it saves
        // Sized for CONTINUOUS speech, not one region: the detector accumulates until it can
        // emit, which on an unbroken talker is far more than one region's worth. Two minutes of
        // float32 at 16kHz is under 8MB - the cheap side of the trade.
        _vad = new VoiceActivityDetector(config, bufferSizeInSeconds: 120);
    }

    /// <summary>
    /// Every speech region in <paramref name="samples"/> (16kHz mono), in order. Each region is
    /// padded by <paramref name="padSeconds"/> either side and merged, so the recognizer hears the
    /// attack of the first word and the tail of the last (an exact VAD cut clips plosives).
    /// </summary>
    public List<SpeechRegion> Find(float[] samples, double padSeconds = 0.35, float gain = 1f)
    {
        var regions = new List<SpeechRegion>();
        if (samples.Length == 0) return regions;

        _vad.Reset();

        // Fed in chunks: the detector holds only the buffer above, and a whole meeting in one
        // call would overrun it. Chunks are LEVELLED on the way in - Silero cannot read a
        // far-too-quiet stream either, and that is the stream this gate exists for.
        const int feed = WindowSize * 64;
        for (var pos = 0; pos < samples.Length; pos += feed)
        {
            var take = Math.Min(feed, samples.Length - pos);
            var chunk = samples[pos..(pos + take)];
            if (gain != 1f)
                for (var i = 0; i < chunk.Length; i++) chunk[i] *= gain;
            _vad.AcceptWaveform(chunk);
            Drain(regions);
        }
        _vad.Flush();
        Drain(regions);

        return Merge(regions, samples.Length, (int)(padSeconds * AudioLoader.TargetRate));
    }

    void Drain(List<SpeechRegion> into)
    {
        while (!_vad.IsEmpty())
        {
            var seg = _vad.Front();
            var start = seg.Start;
            var len = seg.Samples?.Length ?? 0;
            if (len > 0) into.Add(new SpeechRegion(start, start + len));
            _vad.Pop();
        }
    }

    /// <summary>Pad each region, clamp to the recording, and fuse any that now touch or overlap.</summary>
    static List<SpeechRegion> Merge(List<SpeechRegion> regions, int total, int pad)
    {
        var merged = new List<SpeechRegion>();
        foreach (var r in regions.OrderBy(r => r.Start))
        {
            var start = Math.Max(0, r.Start - pad);
            var end = Math.Min(total, r.End + pad);
            if (end <= start) continue;
            if (merged.Count > 0 && start <= merged[^1].End)
                merged[^1] = new SpeechRegion(merged[^1].Start, Math.Max(merged[^1].End, end));
            else
                merged.Add(new SpeechRegion(start, end));
        }
        return merged;
    }

    public void Dispose() => _vad.Dispose();
}
