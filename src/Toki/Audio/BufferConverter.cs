using NAudio.Wave;

namespace Toki.Audio;

/// <summary>
/// Streaming PCM converter (channel remix + linear-interpolation resample), stateful across
/// buffers so there are no seams between callbacks. Used when the default mic changes
/// mid-recording: the one-WAV-per-stream transcription contract forbids a new file per device.
/// </summary>
public sealed class BufferConverter
{
    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT / _PCM - how a WaveFormatExtensible says what it holds.
    static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    internal static Guid IeeeFloatSubFormatId => IeeeFloatSubFormat;
    internal static readonly Guid PcmSubFormatId = new("00000001-0000-0010-8000-00aa00389b71");

    readonly WaveFormat _from, _to;
    readonly double _step;          // source frames consumed per output frame
    float[]? _tailFrame;            // last source frame of the previous buffer
    double _pos;                    // fractional read position into (tail + current) frames

    public BufferConverter(WaveFormat from, WaveFormat to)
    {
        if (!Supported(from) || !Supported(to))
            throw new NotSupportedException($"unsupported conversion {from} -> {to}");
        _from = from;
        _to = to;
        _step = (double)from.SampleRate / to.SampleRate;
        _pos = 0;
    }

    static bool Supported(WaveFormat f) =>
        (f.Encoding == WaveFormatEncoding.IeeeFloat && f.BitsPerSample == 32) ||
        (f.Encoding == WaveFormatEncoding.Pcm && f.BitsPerSample is 16 or 24 or 32) ||
        (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample is 16 or 24 or 32);

    /// <summary>Convert one capture buffer; returns bytes in the target format.</summary>
    public (byte[] Buffer, int Bytes) Convert(byte[] src, int bytes)
    {
        var srcFrames = DecodeFrames(src, bytes);
        var frames = _tailFrame is null ? srcFrames : Prepend(_tailFrame, srcFrames);

        // Linear-interp resample over the frame sequence; _pos carries across calls.
        var outFrames = new List<float[]>((int)(frames.Length / _step) + 2);
        while (_pos + 1 < frames.Length)
        {
            var i = (int)_pos;
            var frac = (float)(_pos - i);
            var a = frames[i];
            var b = frames[i + 1];
            var mixed = new float[a.Length];
            for (var c = 0; c < a.Length; c++) mixed[c] = a[c] + (b[c] - a[c]) * frac;
            outFrames.Add(mixed);
            _pos += _step;
        }

        // Keep the final frame for continuity and rebase the position onto it.
        if (frames.Length > 0)
        {
            _tailFrame = frames[^1];
            _pos -= frames.Length - 1;
            if (_pos < 0) _pos = 0;
        }

        return EncodeFrames(outFrames);
    }

    static float[][] Prepend(float[] tail, float[][] frames)
    {
        var all = new float[frames.Length + 1][];
        all[0] = tail;
        Array.Copy(frames, 0, all, 1, frames.Length);
        return all;
    }

    float[][] DecodeFrames(byte[] src, int bytes)
    {
        var bytesPerSample = _from.BitsPerSample / 8;
        var frameBytes = bytesPerSample * _from.Channels;
        var count = bytes / frameBytes;
        var frames = new float[count][];
        var isFloat = _from.Encoding == WaveFormatEncoding.IeeeFloat
                   || (_from is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);
        for (var f = 0; f < count; f++)
        {
            var frame = new float[_from.Channels];
            for (var c = 0; c < _from.Channels; c++)
            {
                var o = f * frameBytes + c * bytesPerSample;
                frame[c] = bytesPerSample switch
                {
                    4 when isFloat => BitConverter.ToSingle(src, o),
                    4 => BitConverter.ToInt32(src, o) / 2147483648f,
                    3 => (((src[o + 2] << 24) | (src[o + 1] << 16) | (src[o] << 8)) >> 8) / 8388608f,
                    _ => BitConverter.ToInt16(src, o) / 32768f,
                };
            }
            frames[f] = Remix(frame);
        }
        return frames;
    }

    /// <summary>Channel remix to the target layout: average down to mono, replicate up.</summary>
    float[] Remix(float[] frame)
    {
        if (frame.Length == _to.Channels) return frame;
        float mono = 0;
        for (var c = 0; c < frame.Length; c++) mono += frame[c];
        mono /= frame.Length;
        var target = new float[_to.Channels];
        for (var c = 0; c < target.Length; c++) target[c] = mono;
        return target;
    }

    (byte[], int) EncodeFrames(List<float[]> frames)
    {
        var bytesPerSample = _to.BitsPerSample / 8;
        var frameBytes = bytesPerSample * _to.Channels;
        var buf = new byte[frames.Count * frameBytes];
        // Must mirror the decode-side float test: Encoding==IeeeFloat alone misses
        // Extensible-wrapped float and would write int PCM bytes under a float header.
        var isFloat = _to.Encoding == WaveFormatEncoding.IeeeFloat
                   || (_to is WaveFormatExtensible toExt && toExt.SubFormat == IeeeFloatSubFormat);
        var o = 0;
        foreach (var frame in frames)
        {
            for (var c = 0; c < _to.Channels; c++)
            {
                var v = Math.Clamp(frame[c], -1f, 1f);
                switch (bytesPerSample)
                {
                    case 4 when isFloat:
                        BitConverter.TryWriteBytes(buf.AsSpan(o), v);
                        break;
                    case 4:
                        BitConverter.TryWriteBytes(buf.AsSpan(o), (int)(v * 2147483647f));
                        break;
                    case 3:
                        var s24 = (int)(v * 8388607f);
                        buf[o] = (byte)s24; buf[o + 1] = (byte)(s24 >> 8); buf[o + 2] = (byte)(s24 >> 16);
                        break;
                    default:
                        BitConverter.TryWriteBytes(buf.AsSpan(o), (short)(v * 32767f));
                        break;
                }
                o += bytesPerSample;
            }
        }
        return (buf, buf.Length);
    }
}
