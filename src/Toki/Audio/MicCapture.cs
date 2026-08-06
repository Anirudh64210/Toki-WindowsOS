using System.IO;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Toki.Audio;

/// <summary>
/// Captures the default microphone (WASAPI shared mode) to a WAV file - the
/// "you" stream. Reports the peak level so a self-test can prove real audio
/// (not silence) was recorded.
///
/// Mid-recording resilience: if the default communications mic CHANGES (headset
/// plugged in / unplugged, Bluetooth drop) the capture rebuilds itself on the new
/// default device and keeps writing to the SAME WAV - buffers are converted to the
/// original writer format when the new device's format differs. Without this, a
/// device switch silently killed the "you" stream for the rest of the meeting.
/// The WAV header is also re-finalized every few seconds so a crash or power loss
/// mid-meeting leaves a playable file instead of a zero-length RIFF header.
/// </summary>
public sealed class MicCapture : IDisposable
{
    readonly object _gate = new();
    WasapiCapture? _capture;
    WaveFileWriter? _writer;
    WaveFormat? _writerFormat;
    BufferConverter? _converter;      // non-null when the live device format ≠ writer format
    MMDeviceEnumerator? _enumerator;  // owned for the life of the capture (device-change watcher)
    DeviceWatcher? _watcher;
    string? _wavPath;
    long _lastHeaderFlush;
    float _peak;
    bool _began;

    public string DeviceName { get; private set; } = "(unknown)";
    public WaveFormat? Format { get; private set; }
    public float PeakLevel => _peak;

    /// <summary>Optional passive tap: capture bytes in the WRITER's format as they arrive, for live
    /// transcription. Purely an observer of the buffer already being written to WAV - must never
    /// affect capture. Wire it between <see cref="Prepare"/> and <see cref="Begin"/> so the very
    /// first buffers are tapped too.</summary>
    public Action<byte[], int>? OnBuffer;

    /// <summary>Open the default device and the WAV writer WITHOUT starting capture - so the
    /// caller can wire <see cref="OnBuffer"/>/live-transcription against <see cref="Format"/>
    /// before the first buffer arrives. Follow with <see cref="Begin"/>.</summary>
    public void Prepare(string wavPath)
    {
        lock (_gate)
        {
            _wavPath = wavPath;
            _enumerator = new MMDeviceEnumerator();
            var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            DeviceName = device.FriendlyName;

            _capture = new WasapiCapture(device);
            Format = _writerFormat = _capture.WaveFormat;
            _writer = new WaveFileWriter(wavPath, _capture.WaveFormat);
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;

            _watcher = new DeviceWatcher(this);
            try { _enumerator.RegisterEndpointNotificationCallback(_watcher); }
            catch (Exception ex) { App.Log($"mic device watcher (non-fatal): {ex.Message}"); }
        }
    }

    /// <summary>Start capturing (after <see cref="Prepare"/>).</summary>
    public void Begin()
    {
        lock (_gate)
        {
            _began = true;
            _capture?.StartRecording();
        }
    }

    public void Start(string wavPath)
    {
        Prepare(wavPath);
        Begin();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (_writer is null) return;   // stopped (or mid-rebuild) - drop the straggler buffer
            var buf = e.Buffer;
            var bytes = e.BytesRecorded;
            if (_converter is not null) (buf, bytes) = _converter.Convert(buf, bytes);
            if (bytes == 0) return;
            _writer.Write(buf, 0, bytes);
            var p = PeakOf(buf, bytes, _writerFormat!);
            if (p > _peak) _peak = p;
            try { OnBuffer?.Invoke(buf, bytes); } catch { /* a tap must never break capture */ }

            // Re-finalize the RIFF header every ~5s: a crash/power loss mid-meeting then
            // truncates the recording instead of destroying it (header sizes stay valid).
            var now = Environment.TickCount64;
            if (now - _lastHeaderFlush > 5000)
            {
                _lastHeaderFlush = now;
                try { _writer.Flush(); } catch { /* flush is best-effort */ }
            }
        }
    }

    /// <summary>The capture stopped on its own (device unplugged/disabled mid-recording) -
    /// try to carry on with whatever the new default mic is.</summary>
    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;               // deliberate Stop()
        App.Log($"mic capture stopped unexpectedly ({e.Exception.Message}) - rebuilding");
        System.Threading.Tasks.Task.Run(RebuildOnDefaultDevice);
    }

    /// <summary>Re-point the capture at the current default communications mic, converting
    /// buffers to the original writer format when the new device records differently.</summary>
    void RebuildOnDefaultDevice()
    {
        lock (_gate)
        {
            if (_writer is null || !_began || _enumerator is null) return; // stopped meanwhile
            try
            {
                try { if (_capture is not null) { _capture.DataAvailable -= OnData; _capture.RecordingStopped -= OnStopped; _capture.StopRecording(); _capture.Dispose(); } }
                catch { /* the old device may already be gone */ }
                _capture = null;

                var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                DeviceName = device.FriendlyName;
                _capture = new WasapiCapture(device);
                _converter = FormatsEqual(_capture.WaveFormat, _writerFormat!)
                    ? null
                    : new BufferConverter(_capture.WaveFormat, _writerFormat!);
                _capture.DataAvailable += OnData;
                _capture.RecordingStopped += OnStopped;
                _capture.StartRecording();
                App.Log($"mic capture rebuilt on '{DeviceName}'{(_converter is null ? "" : " (format-converted)")}");
            }
            catch (Exception ex)
            {
                // No usable mic right now - a later device arrival triggers another attempt.
                App.Log($"mic rebuild failed (will retry on next device change): {ex.Message}");
            }
        }
    }

    static bool FormatsEqual(WaveFormat a, WaveFormat b) =>
        a.Encoding == b.Encoding && a.SampleRate == b.SampleRate &&
        a.Channels == b.Channels && a.BitsPerSample == b.BitsPerSample;

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
        lock (_gate)
        {
            _began = false;
            if (_watcher is not null && _enumerator is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
                _watcher = null;
            }
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnData;
                _capture.RecordingStopped -= OnStopped;
                try { _capture.StopRecording(); } catch { }
                _capture.Dispose();
                _capture = null;
            }
            _writer?.Dispose();
            _writer = null;
            _converter = null;
            _enumerator?.Dispose();
            _enumerator = null;
        }
    }

    public void Dispose() => Stop();

    /// <summary>Default-device-change watcher. Windows fires these on a COM callback thread;
    /// the rebuild takes the capture lock, so it is safe from any thread. Only the
    /// communications-capture role matters - that's the device this capture opened.</summary>
    sealed class DeviceWatcher(MicCapture owner) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Capture && role == Role.Communications)
                System.Threading.Tasks.Task.Run(owner.RebuildOnDefaultDevice);
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
