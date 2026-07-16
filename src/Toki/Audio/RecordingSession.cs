using System.IO;
using Toki.Detection;

namespace Toki.Audio;

/// <summary>
/// One recording: the "you" mic stream always, plus the "them" per-process
/// loopback stream when a meeting app is known. Writes both WAVs into a
/// per-meeting folder under %APPDATA%\Toki\Recordings\. Robust by design —
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
        // "You" — always.
        try { _mic.Start(YouWavPath); }
        catch (Exception ex) { App.Log($"mic start failed: {ex.Message}"); }

        // "Them" — only if we can resolve the meeting app to a PID.
        if (Target is not null)
        {
            var pid = ProcessResolver.FindPid(Target);
            if (pid is uint p)
            {
                try
                {
                    ThemWavPath = Path.Combine(FolderPath, "them.wav");
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
        return FolderPath;
    }
}
