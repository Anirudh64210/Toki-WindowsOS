using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Toki.Audio;

/// <summary>
/// Captures the default microphone (WASAPI shared mode) to a WAV file — the
/// "you" stream. Reports the peak level so a self-test can prove real audio
/// (not silence) was recorded.
/// </summary>
public sealed class MicCapture : IDisposable
{
    WasapiCapture? _capture;
    WaveFileWriter? _writer;
    float _peak;

    public string DeviceName { get; private set; } = "(unknown)";
    public WaveFormat? Format { get; private set; }
    public float PeakLevel => _peak;

    /// <summary>Optional passive tap: raw capture bytes (native format) as they arrive, for live transcription.
    /// Purely an observer of the buffer already being written to WAV — must never affect capture.</summary>
    public Action<byte[], int>? OnBuffer;

    public void Start(string wavPath)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        DeviceName = device.FriendlyName;

        _capture = new WasapiCapture(device);
        Format = _capture.WaveFormat;
        _writer = new WaveFileWriter(wavPath, _capture.WaveFormat);
        _capture.DataAvailable += OnData;
        _capture.StartRecording();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        _writer!.Write(e.Buffer, 0, e.BytesRecorded);
        var p = PeakOf(e.Buffer, e.BytesRecorded, Format!);
        if (p > _peak) _peak = p;
        try { OnBuffer?.Invoke(e.Buffer, e.BytesRecorded); } catch { /* a tap must never break capture */ }
    }

    /// <summary>Peak absolute sample amplitude in 0..1, for IEEE-float or 16-bit PCM buffers.</summary>
    internal static float PeakOf(byte[] buffer, int bytes, WaveFormat format)
    {
        float peak = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (var i = 0; i + 4 <= bytes; i += 4)
            {
                var v = Math.Abs(BitConverter.ToSingle(buffer, i));
                if (v > peak) peak = v;
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            for (var i = 0; i + 2 <= bytes; i += 2)
            {
                var v = Math.Abs(BitConverter.ToInt16(buffer, i)) / 32768f;
                if (v > peak) peak = v;
            }
        }
        return peak;
    }

    public void Stop()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;
        _writer?.Dispose();
        _writer = null;
    }

    public void Dispose() => Stop();
}
