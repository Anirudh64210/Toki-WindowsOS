using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Toki.Transcription;

/// <summary>
/// INCREMENTAL (live) transcription. Instead of one whole-file Parakeet pass after Stop - which
/// took ~10 minutes on a 25-minute meeting - this transcribes DURING the meeting: each capture
/// stream is tapped, resampled to 16 kHz mono, and a single background worker decodes it in
/// ~15-second windows (cut at quiet points, so never mid-word) as the audio arrives. By the time
/// you hit Stop, almost everything is already transcribed; only the final short remainder is left.
///
/// One recognizer, serialized through <see cref="TranscriptionService.DecodeLive"/> (concurrent
/// sherpa-onnx decode corrupts output). The worker runs BelowNormal so the live meeting never
/// stutters. Same slice-decode path as the whole-file decode, so transcript quality is identical.
///
/// Fail-safe: this never touches the WASAPI capture logic (it only observes the buffers already
/// being written to WAV), and if it produces nothing the caller falls back to the whole-file pass -
/// so a meeting is never lost.
/// </summary>
public sealed class LiveTranscriber
{
    const int Rate = AudioLoader.TargetRate;   // 16000
    static readonly int WinSec = Env("TOKI_LIVE_WINSEC", 15);   // decode a window once this much audio has piled up
    const int SearchSec = 3;                                    // ±search for a quiet cut near the window edge
    const int PollMs = 150;

    static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    readonly TranscriptionService _service;
    volatile StreamPipe[] _pipes = Array.Empty<StreamPipe>();   // copy-on-add: Feed iterates lock-free
    readonly List<TranscriptSegment> _you = new();
    readonly List<TranscriptSegment> _them = new();
    readonly object _lock = new();
    readonly object _decodeGate = new();   // serializes worker windows vs Stop's final flush
    Thread? _worker;
    volatile bool _stop;
    bool _started;

    /// <summary>Raised (on the worker thread) each time a window finalizes into segments - for live preview.</summary>
    public event Action<TranscriptSegment>? SegmentFinalized;

    public LiveTranscriber(TranscriptionService service) => _service = service;

    /// <summary>Register a stream to transcribe live, in its native capture format. Copy-on-add so a
    /// capture thread already inside <see cref="Feed"/> never sees a list mutating under it.</summary>
    public void AddStream(Speaker speaker, WaveFormat nativeFormat)
    {
        var next = new StreamPipe[_pipes.Length + 1];
        Array.Copy(_pipes, next, _pipes.Length);
        next[^1] = new StreamPipe(speaker, nativeFormat);
        _pipes = next;
    }

    /// <summary>Feed raw capture bytes (native format) for a stream. Safe to call from the capture thread.</summary>
    public void Feed(Speaker speaker, byte[] buffer, int bytes)
    {
        foreach (var p in _pipes) if (p.Speaker == speaker) { p.Feed(buffer, bytes); return; }
    }

    public void Start()
    {
        if (_started || _pipes.Length == 0) return;
        _started = true;
        _worker = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "toki-live-asr" };
        _worker.Start();
    }

    /// <summary>
    /// Stop capturing, drain and decode the final remainder of every stream, and return the merged,
    /// echo-de-duped transcript. Returns null if live transcription never ran (no streams / not
    /// started) so the caller falls back to the whole-file pass.
    /// </summary>
    public IReadOnlyList<TranscriptSegment>? Stop()
    {
        if (!_started) return null;
        _stop = true;
        // Let any in-flight window decode finish. If the worker is STILL busy after this, the
        // final flush below serializes behind it on the decode gate instead of mutating the
        // same pipes the worker is reading (which corrupted the transcript tail).
        if (_worker?.Join(TimeSpan.FromSeconds(30)) == false)
            App.Log("live stop: worker busy past 30s - flush will wait on the decode gate");
        // Final flush: whatever audio is still buffered gets one last decode per stream.
        foreach (var pipe in _pipes)
        {
            try
            {
                lock (_decodeGate)
                {
                    pipe.Drain();
                    if (pipe.Accum.Count > 0) DecodeWindow(pipe, finalFlush: true);
                }
            }
            catch (Exception ex) { App.Log($"live final flush {pipe.Speaker} failed: {ex.Message}"); }
        }
        lock (_lock)
        {
            App.Log($"live transcription: you={_you.Count} them={_them.Count} segments (decoded during the meeting)");
            return TranscriptMerger.Merge(_you, _them);
        }
    }

    void Work()
    {
        while (!_stop)
        {
            var did = false;
            // Guarded: an escaping exception on a raw background thread would take down the
            // whole PROCESS mid-meeting. Log, breathe, keep transcribing - the whole-file
            // pass at stop still covers anything a bad window missed.
            try
            {
                foreach (var pipe in _pipes)
                {
                    lock (_decodeGate)
                    {
                        pipe.Drain();
                        // Decode as many full windows as have piled up (with lookahead room for the quiet-cut search).
                        while (!_stop && pipe.Accum.Count >= (WinSec + 2 * SearchSec) * Rate)
                            did |= DecodeWindow(pipe, finalFlush: false);
                    }
                }
            }
            catch (Exception ex) { App.Log($"live worker error (recovered): {ex.Message}"); }
            if (!did) Thread.Sleep(PollMs);
        }
    }

    bool DecodeWindow(StreamPipe pipe, bool finalFlush)
    {
        var n = pipe.Accum.Count;
        if (n == 0) return false;
        var arr = pipe.Accum.ToArray();

        int cut;
        if (finalFlush) cut = n;
        else
        {
            var center = WinSec * Rate;
            cut = Transcriber.FindQuietCut(arr, center, SearchSec * Rate);
            if (cut <= 0 || cut > n) cut = Math.Min(center, n);
        }

        var segs = _service.DecodeLive(arr, 0, cut, pipe.DecodedSec, pipe.Speaker);
        pipe.DecodedSec += cut / (double)Rate;
        pipe.Accum.RemoveRange(0, cut);

        if (segs.Count > 0)
        {
            lock (_lock) (pipe.Speaker == Speaker.You ? _you : _them).AddRange(segs);
            foreach (var s in segs) { try { SegmentFinalized?.Invoke(s); } catch { } }
        }
        return true;
    }

    /// <summary>One capture stream: native bytes in → 16 kHz mono float out, accumulated for windowed decode.</summary>
    sealed class StreamPipe
    {
        public readonly Speaker Speaker;
        public readonly List<float> Accum = new();
        public double DecodedSec;

        readonly BufferedWaveProvider _buffer;
        readonly ISampleProvider _out;
        readonly float[] _pull = new float[Rate];   // ~1s pull chunk

        public StreamPipe(Speaker speaker, WaveFormat nativeFormat)
        {
            Speaker = speaker;
            var fmt = Normalize(nativeFormat);
            _buffer = new BufferedWaveProvider(fmt)
            {
                // Generous ceiling: the worker drains continuously (decode is faster than real-time), so
                // this stays near-empty in real use - it only matters as a safety cap if the worker
                // briefly stalls. Env-tunable for stress tests.
                BufferDuration = TimeSpan.FromSeconds(Env("TOKI_LIVE_BUFSEC", 120)),
                DiscardOnBufferOverflow = true,   // never throw into the capture thread
                ReadFully = false,                // Read returns only what's actually buffered
            };
            ISampleProvider sp = _buffer.ToSampleProvider();
            if (sp.WaveFormat.Channels > 1)
                sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
            if (sp.WaveFormat.SampleRate != Rate)
                sp = new WdlResamplingSampleProvider(sp, Rate);   // managed resampler, same as the file loader
            _out = sp;
        }

        public void Feed(byte[] buffer, int bytes)
        {
            try { _buffer.AddSamples(buffer, 0, bytes); }
            catch { /* overflow-discard or a format hiccup must NEVER crash the capture thread */ }
        }

        /// <summary>Pull all currently-available resampled 16 kHz mono samples into Accum.</summary>
        public void Drain()
        {
            int read;
            while ((read = _out.Read(_pull, 0, _pull.Length)) > 0)
            {
                for (var i = 0; i < read; i++) Accum.Add(_pull[i]);
                if (read < _pull.Length) break;   // nothing more available right now
            }
        }

        // WASAPI often reports WAVE_FORMAT_EXTENSIBLE, which ToSampleProvider can't read directly.
        // The raw bytes are plain float (32-bit) or PCM, so reinterpret to the matching simple format.
        static WaveFormat Normalize(WaveFormat f) =>
            f.Encoding == WaveFormatEncoding.Extensible
                ? (f.BitsPerSample == 32
                    ? WaveFormat.CreateIeeeFloatWaveFormat(f.SampleRate, f.Channels)
                    : new WaveFormat(f.SampleRate, f.BitsPerSample, f.Channels))
                : f;
    }
}
