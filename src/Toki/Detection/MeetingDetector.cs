using System.Windows.Threading;

namespace Toki.Detection;

/// <summary>
/// Watches the mic-in-use set and decides when a meeting is worth prompting for.
/// Denylist (not allowlist): any app can host a call, so treat every mic user as
/// a candidate and exclude the known non-meetings. Conservative on purpose - a
/// spurious prompt is dismissible; a missed meeting is lost forever.
///
/// Debounce 2 polls (~5s) before prompting; a dismissed app is suppressed 10 min;
/// while recording, if the target frees the mic for 3 polls (~7.5s) the call ended.
/// </summary>
public sealed class MeetingDetector
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.5);
    const int DebouncePolls = 2;
    const int AutoStopPolls = 3;
    static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    // Substring match, case-insensitive. Everything else is a meeting candidate.
    static readonly string[] Denylist =
    [
        "toki",                                             // ourselves
        "wispr", "superwhisper", "dragon", "voiceaccess",   // dictation
        "obs", "loom", "sharex", "snagit", "camtasia", "bandicam", "screenrec", // screen recorders
        "soundrecorder", "voicerecorder",                   // voice recorders
        "audacity", "flstudio", "ableton", "reaper", "cubase", // audio production
        "code", "cursor", "chatgpt", "claude", "copilot", "windsurf", // AI IDEs / assistants
    ];

    readonly DispatcherTimer _timer = new() { Interval = PollInterval };
    readonly Dictionary<string, int> _consecutive = new();
    readonly Dictionary<string, DateTime> _cooldownUntil = new();
    readonly HashSet<string> _prompted = new();   // apps already offered this mic-session
    readonly Func<DateTime> _now;
    readonly Func<IReadOnlyList<DetectedApp>> _readActive;

    DetectedApp? _recordingTarget;
    int _targetAbsentPolls;

    /// <summary>Raised when a debounced, non-cooldowned candidate should be offered. UI thread.</summary>
    public event Action<DetectedApp>? MeetingDetected;

    /// <summary>Raised when the recording target releases the mic (call ended). UI thread.</summary>
    public event Action? MeetingEnded;

    /// <summary>Set by the host: may we surface a prompt right now? (idle, not busy)</summary>
    public Func<bool> CanPrompt { get; set; } = () => true;

    public MeetingDetector(Func<DateTime>? now = null, Func<IReadOnlyList<DetectedApp>>? readActive = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _readActive = readActive ?? MicUsageReader.GetActiveMicApps;
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public void BeginRecording(DetectedApp target)
    {
        _recordingTarget = target;
        _targetAbsentPolls = 0;
    }

    public void EndRecording() => _recordingTarget = null;

    /// <summary>Suppress an app for the cooldown window (user dismissed its prompt). Also clears the
    /// "already offered" mark so that - if the call is STILL going when the cooldown expires - the
    /// ongoing-meeting prompt re-surfaces once more (the owner's "even while the meeting is happening"),
    /// rather than the app being silenced for the whole session after one dismiss.</summary>
    public void Snooze(DetectedApp app)
    {
        _cooldownUntil[app.Id] = _now() + Cooldown;
        _prompted.Remove(app.Id);
    }

    public static bool IsDenylisted(DetectedApp app)
    {
        // Match the app's own NAME only (exe filename, or packaged-app id) - never the full
        // install path. Matching the path made the user's folders part of the decision: a
        // Windows username like "Jacobs" (contains "obs") or a "C:\Code\" install dir would
        // denylist every per-user app and silently kill meeting detection.
        var name = app.DisplayName;
        var packagedId = app.Packaged ? app.Id : "";
        return Denylist.Any(d => name.Contains(d, StringComparison.OrdinalIgnoreCase)
                              || packagedId.Contains(d, StringComparison.OrdinalIgnoreCase));
    }

    void Poll()
    {
        IReadOnlyList<DetectedApp> active;
        try { active = _readActive(); }
        catch (Exception ex) { App.Log($"detect poll failed (non-fatal): {ex.Message}"); return; }
        PollOnce(active);
    }

    /// <summary>The decision logic for one poll - separated so it's unit-testable.</summary>
    public void PollOnce(IReadOnlyList<DetectedApp> active)
    {
        var candidates = active.Where(a => !IsDenylisted(a)).ToList();
        var liveIds = candidates.Select(c => c.Id).ToHashSet();

        // Debounce bookkeeping.
        foreach (var c in candidates)
            _consecutive[c.Id] = _consecutive.GetValueOrDefault(c.Id) + 1;
        foreach (var id in _consecutive.Keys.Where(k => !liveIds.Contains(k)).ToList())
            _consecutive.Remove(id);
        // Once an app leaves the mic, forget we prompted it - a fresh call later re-prompts.
        _prompted.RemoveWhere(id => !liveIds.Contains(id));

        // Auto-stop: is the recording target still on the mic?
        if (_recordingTarget is not null)
        {
            if (active.Any(a => a.Id == _recordingTarget.Id))
                _targetAbsentPolls = 0;
            else if (++_targetAbsentPolls >= AutoStopPolls)
            {
                var t = _recordingTarget;
                _recordingTarget = null;
                App.Log($"detect: target '{t?.DisplayName}' released mic - auto-stop");
                MeetingEnded?.Invoke();
            }
            return; // never prompt while recording
        }

        if (!CanPrompt()) return;

        foreach (var c in candidates)
        {
            if (_consecutive[c.Id] < DebouncePolls) continue;
            if (_prompted.Contains(c.Id)) continue; // already offered this session
            if (_cooldownUntil.TryGetValue(c.Id, out var until) && _now() < until) continue;
            _prompted.Add(c.Id);
            App.Log($"detect: prompting for '{c.DisplayName}'");
            MeetingDetected?.Invoke(c);
            break; // one prompt at a time
        }
    }
}
