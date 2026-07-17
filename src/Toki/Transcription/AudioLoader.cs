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

        // Preallocate the exact array and fill it in place. The old List<float> +
        // ToArray path transiently held 3-4x the final size (doubling growth + a full
        // copy), which OOM'd on a 35-min recording once the LLM was warmed. This holds
        // just the one array.
        var estimate = (int)(reader.TotalTime.TotalSeconds * TargetRate) + TargetRate;
        var samples = new float[estimate];
        var count = 0;
        var buffer = new float[TargetRate]; // ~1s chunks
        int read;
        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (count + read > samples.Length)
                Array.Resize(ref samples, Math.Max(samples.Length + TargetRate, count + read));
            Array.Copy(buffer, 0, samples, count, read);
            count += read;
        }
        if (count != samples.Length) Array.Resize(ref samples, count);
        return samples;
    }

    /// <summary>Duration of a WAV in milliseconds, without decoding all samples.</summary>
    public static int DurationMs(string wavPath)
    {
        using var reader = new AudioFileReader(wavPath);
        return (int)reader.TotalTime.TotalMilliseconds;
    }
}
