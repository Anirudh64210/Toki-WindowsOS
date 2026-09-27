using System.Windows.Threading;

namespace Toki.Detection;

/// <summary>
/// Watches the mic-in-use set and decides when a meeting is worth prompting for.
/// Denylist (not allowlist): any app can host a call, so treat every mic user as
/// a candidate and exclude the known non-meetings. Conservative on purpose - a
/// spurious prompt is dismissible; a missed meeting is lost forever.
///
/// Debounce 2 polls (~5s) before prompting. ONE "no" per call: a declined app is not offered
/// again until it has been off the mic long enough that the next time is a different meeting.
/// While recording, if the target frees the mic for 3 polls (~7.5s) the call ended.
/// </summary>
public sealed class MeetingDetector
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.5);
    const int DebouncePolls = 2;
    const int AutoStopPolls = 3;

    /// <summary>How long an app must be OFF the mic before its next appearance counts as a NEW call.
    ///
    /// This is the whole anti-nag mechanism, and it is deliberately NOT a clock. A 10-minute cooldown
    /// used to re-offer the same call over and over - the owner, 2026-09-23: "every 10 mins or so it
    /// re asks, it becomes redundant and pointless". Time does not make a refusal stale; a new
    /// meeting does. So the question is only ever asked once per call.
    ///
    /// 6 polls is ~15 seconds, comfortably longer than a mute toggle, a headset switch or a brief
    /// device glitch, any of which would otherwise read as "the call ended" and earn a fresh prompt.
    /// It is shorter than any real gap between two meetings.</summary>
    const int SessionEndPolls = 6;

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
    readonly HashSet<string> _prompted = new();   // apps already offered this call
    readonly HashSet<string> _declined = new();   // apps the user said no to, for this call
    readonly Dictionary<string, int> _absent = new();   // consecutive polls each tracked app has been off the mic
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

    /// <summary>The user said no. Toki does not ask about THIS call again - not in ten minutes, not
    /// at all. Starting a recording by hand is always available and costs one click; being asked the
    /// same question repeatedly during a meeting is not.</summary>
    public void Decline(DetectedApp app)
    {
        _declined.Add(app.Id);
        _prompted.Add(app.Id);
        _absent[app.Id] = 0;
        App.Log($"detect: '{app.DisplayName}' declined - not asking again until this call ends");
    }

    public static bool IsDenylisted(DetectedApp app)
    {
        // Match the app's own NAME only (exe filename, or packaged-app id), never the install
        // path - path matching lets the user's folder names silently denylist apps.
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

        // HAS THIS CALL ENDED? Count how long each tracked app has been off the mic. A SINGLE absent
        // poll is not the end of anything - muting, switching headset or a driver hiccup all drop the
        // mic for a moment, and treating that as a new meeting is how a declined prompt came straight
        // back. Only a sustained absence clears what we offered and what was refused.
        foreach (var id in _prompted.Concat(_declined).Distinct().ToList())
            _absent[id] = liveIds.Contains(id) ? 0 : _absent.GetValueOrDefault(id) + 1;
        foreach (var id in _absent.Where(kv => kv.Value >= SessionEndPolls).Select(kv => kv.Key).ToList())
        {
            if (_declined.Contains(id)) App.Log($"detect: '{id}' left the mic - a later call may ask again");
            _prompted.Remove(id);
            _declined.Remove(id);
            _absent.Remove(id);
        }

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
            if (_prompted.Contains(c.Id)) continue;  // already offered, or refused, for this call
            _prompted.Add(c.Id);
            App.Log($"detect: prompting for '{c.DisplayName}'");
            MeetingDetected?.Invoke(c);
            break; // one prompt at a time
        }
    }
}
