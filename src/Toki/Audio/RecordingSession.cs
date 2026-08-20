using System.IO;
using Toki.Detection;
using Toki.Transcription;

namespace Toki.Audio;

/// <summary>
/// One recording: the "you" mic stream always, plus the "them" per-process
/// loopback stream when a meeting app is known. Writes both WAVs into a
/// per-meeting folder under %APPDATA%\Toki\Recordings\. Robust by design -
/// if the loopback tap fails, the mic keeps recording (mic-only note).
/// </summary>
public sealed class RecordingSession
{
    public string FolderPath { get; }
    public string YouWavPath { get; }
    public string? ThemWavPath { get; private set; }
    public DetectedApp? Target { get; }
    public bool HasThemStream { get; private set; }
    public float YouPeak => _mic.PeakLevel;
    public float ThemPeak => _loopback.PeakLevel;

    readonly MicCapture _mic = new();
    readonly ProcessLoopbackCapture _loopback = new();
    bool _loopbackRunning;

    LiveTranscriber? _live;
    /// <summary>The transcript built LIVE during the meeting (merged + echo-de-duped), or null if live
    /// transcription didn't run - in which case the caller does the whole-file pass. Set by <see cref="Stop"/>.</summary>
    public IReadOnlyList<TranscriptSegment>? LiveTranscript { get; private set; }

    /// <summary>Seconds of audio the live decoder failed on (see LiveTranscriber.LostSeconds).
    /// Above a few seconds the live transcript has a hole and the whole-file pass is the honest
    /// choice, whatever it costs in wait.</summary>
    public double LiveLostSeconds => _live?.LostSeconds ?? 0;
    /// <summary>The live transcriber (for wiring a live-preview handler), available after <see cref="Start"/>.</summary>
    public LiveTranscriber? Live => _live;

    // Escape hatch to disable live transcription (falls back to the whole-file pass at stop).
    static bool LiveEnabled => Environment.GetEnvironmentVariable("TOKI_NO_LIVE") is null;

    public RecordingSession(DetectedApp? target)
    {
        Target = target;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Toki", "Recordings", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        FolderPath = root;
        YouWavPath = Path.Combine(root, "you.wav");
    }

    public void Start()
    {
        // Live transcription: transcribe DURING the meeting so Stop is near-instant. Only when the
        // model is present (else it can't decode - the whole-file pass at stop covers that case).
        if (LiveEnabled && TranscriptionService.ModelReady)
            _live = new LiveTranscriber(TranscriptionService.Instance);

        // "You" - always. Prepare first, wire the live tap, THEN begin: wiring after the
        // capture was already running could miss the first words of the meeting.
        try
        {
            _mic.Prepare(YouWavPath);
            var fmt = _mic.Format;
            if (_live is not null && fmt is not null) _live.AddStream(Speaker.You, fmt);
            if (fmt is not null)
                _mic.OnBuffer = (buf, n) =>
                {
                    // Level measured on the SAME buffer that reaches the WAV: the bar shows
                    // what was actually captured.
                    LevelMeter.Recording.Report(buf, n, fmt);
                    _live?.Feed(Speaker.You, buf, n);
                };
            _mic.Begin();
        }
        catch (Exception ex) { App.Log($"mic start failed: {ex.Message}"); }

        // "Them" - only if we can resolve the meeting app to a PID.
        if (Target is not null)
        {
            var pid = ProcessResolver.FindPid(Target);
            if (pid is uint p)
            {
                try
                {
                    ThemWavPath = Path.Combine(FolderPath, "them.wav");
                    // Wire the tap BEFORE Start so no "them" audio is missed at the top of the call.
                    var themFmt = ProcessLoopbackCapture.CaptureFormat;
                    _live?.AddStream(Speaker.Them, themFmt);
                    _loopback.OnBuffer = (buf, n) =>
                    {
                        // Same meter as the mic: the bar shows max(you, them), so the other
                        // person talking moves it too.
                        LevelMeter.Recording.Report(buf, n, themFmt);
                        _live?.Feed(Speaker.Them, buf, n);
                    };
                    _loopback.Start(p, ThemWavPath);
                    _loopbackRunning = true;
                    HasThemStream = true;
                    App.Log($"recording: mic + loopback(pid {p}, {Target.DisplayName}) -> {FolderPath}");
                }
                catch (Exception ex) { App.Log($"loopback start failed, mic-only: {ex.Message}"); }
            }
            else
            {
                App.Log($"recording: could not resolve pid for '{Target.DisplayName}', mic-only");
            }
        }

        _live?.Start();
        if (_live is not null) App.Log("live transcription armed");
    }

    /// <summary>Stops capture and returns the folder holding the WAV(s).</summary>
    public string Stop()
    {
        try { _mic.Stop(); } catch (Exception ex) { App.Log($"mic stop: {ex.Message}"); }
        if (_loopbackRunning)
        {
            try { _loopback.Stop(); } catch (Exception ex) { App.Log($"loopback stop: {ex.Message}"); }
            _loopbackRunning = false;
        }
        App.Log($"recording stopped: you peak {_mic.PeakLevel:P0}, them peak {_loopback.PeakLevel:P0}");

        // Finalize live transcription (drains + decodes the last short remainder); almost
        // everything was already transcribed during the meeting, so Stop is quick.
        try { LiveTranscript = _live?.Stop(); }
        catch (Exception ex) { App.Log($"live transcription stop failed: {ex.Message}"); LiveTranscript = null; }

        return FolderPath;
    }
}
