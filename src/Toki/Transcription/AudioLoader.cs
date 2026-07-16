using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Toki.Transcription;

/// <summary>
/// Loads a WAV of any rate/channel layout into the 16kHz mono float32 samples the
/// ASR expects. Our streams are 48kHz float ("you") and 44.1kHz 16-bit ("them"),
/// so both get downmixed to mono and resampled to 16kHz.
/// </summary>
public static class AudioLoader
{
    public const int TargetRate = 16000;

    public static float[] To16kMono(string wavPath)
    {
        using var reader = new AudioFileReader(wavPath); // exposes float samples
        ISampleProvider mono = reader.WaveFormat.Channels > 1
            ? new StereoToMonoSampleProvider(reader) { LeftVolume = 0.5f, RightVolume = 0.5f }
            : reader;

        ISampleProvider src = mono.WaveFormat.SampleRate == TargetRate
            ? mono
            : new WdlResamplingSampleProvider(mono, TargetRate); // managed resampler, no MediaFoundation

        var samples = new List<float>((int)(reader.TotalTime.TotalSeconds * TargetRate) + TargetRate);
        var buffer = new float[TargetRate]; // ~1s chunks
        int read;
        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(new ArraySegment<float>(buffer, 0, read));
        return samples.ToArray();
    }

    /// <summary>Duration of a WAV in milliseconds, without decoding all samples.</summary>
    public static int DurationMs(string wavPath)
    {
        using var reader = new AudioFileReader(wavPath);
        return (int)reader.TotalTime.TotalMilliseconds;
    }
}
