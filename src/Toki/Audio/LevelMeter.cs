using NAudio.Wave;

namespace Toki.Audio;

/// <summary>
/// Live room level for the waveform: every capture buffer reports its RMS here. Safe on an
/// audio thread (no allocation, lock held only for the bucket math). SELF-DECAYS via a sliding
/// two-bucket max: a meter unfed for two buckets reads 0, so the wave settles with no teardown call.
/// </summary>
public sealed class LevelMeter
{
    /// <summary>The recording bar's source. The mic AND the meeting stream both report here, so the
    /// wave shows max(you, them) and the other person talking moves it too.</summary>
    public static readonly LevelMeter Recording = new();

    /// <summary>Push-to-talk (Ask). Separate from <see cref="Recording"/> on purpose: asking a
    /// question mid-meeting must not make the Ask wave dance to the meeting's audio.</summary>
    public static readonly LevelMeter Voice = new();

    /// <summary>One bucket of the sliding max. Two of these with no report means the source stopped.</summary>
    internal const int BucketMs = 100;

    readonly object _gate = new();
    float _cur, _prev;
    long _bucketStart;

    public void Report(byte[] buffer, int bytes, WaveFormat format) => Report(RmsOf(buffer, bytes, format));

    public void Report(float rms) => ReportAt(rms, Environment.TickCount64);

    /// <summary>The loudest recent report, or 0 when nothing has reported for two buckets.</summary>
    public float Level => LevelAt(Environment.TickCount64);

    // Clock injected so the decay and the sliding max are testable without sleeping.
    internal void ReportAt(float rms, long now)
    {
        lock (_gate)
        {
            var age = now - _bucketStart;
            if (age > BucketMs)
            {
                _prev = age > 2 * BucketMs ? 0 : _cur;
                _cur = 0;
                _bucketStart = now;
            }
            if (rms > _cur) _cur = rms;
        }
    }

    // Read-only: rolling the bucket here instead would let a fast reader truncate the window a
    // writer is still filling.
    internal float LevelAt(long now)
    {
        lock (_gate)
        {
            var age = now - _bucketStart;
            if (age > 2 * BucketMs) return 0;
            return age > BucketMs ? _cur : Math.Max(_cur, _prev);
        }
    }

    /// <summary>RMS amplitude in 0..1 for IEEE-float or 16-bit PCM buffers (RMS, not peak:
    /// the wave shows a voice, not a click). Unreadable formats report 0 rather than guess.</summary>
    internal static float RmsOf(byte[] buffer, int bytes, WaveFormat format)
    {
        double sum = 0;
        var n = 0;
        if (MicCapture.IsFloat(format) && format.BitsPerSample == 32)
        {
            for (var i = 0; i + 4 <= bytes; i += 4)
            {
                double v = BitConverter.ToSingle(buffer, i);
                sum += v * v;
                n++;
            }
        }
        else if (MicCapture.IsPcmLike(format) && format.BitsPerSample == 16)
        {
            for (var i = 0; i + 2 <= bytes; i += 2)
            {
                var v = BitConverter.ToInt16(buffer, i) / 32768.0;
                sum += v * v;
                n++;
            }
        }
        return n == 0 ? 0 : (float)Math.Sqrt(sum / n);
    }
}
