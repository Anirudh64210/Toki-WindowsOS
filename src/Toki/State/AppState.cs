namespace Toki.State;

/// <summary>
/// Single owner of the pill's state machine. Views read it and request
/// transitions through <see cref="TryGo"/>; nothing else mutates state.
/// </summary>
public sealed class AppState
{
    static readonly Dictionary<PillState, PillState[]> Allowed = new()
    {
        // Idle is Hidden (nothing on screen). Collapsed remains valid for a
        // future "pin the pill" setting but is not used in the default flow.
        [PillState.Hidden]        = [PillState.Home, PillState.MeetingPrompt, PillState.Collapsed],
        [PillState.Collapsed]     = [PillState.Home, PillState.MeetingPrompt, PillState.Hidden],
        [PillState.MeetingPrompt] = [PillState.RecordingBar, PillState.Hidden],
        [PillState.RecordingBar]  = [PillState.RecordingOpen, PillState.Summarizing],
        [PillState.RecordingOpen] = [PillState.RecordingBar, PillState.Summarizing],
        [PillState.Summarizing]   = [PillState.Notes, PillState.NoSpeech],
        [PillState.Notes]         = [PillState.Hidden, PillState.Home, PillState.Library],
        [PillState.NoSpeech]      = [PillState.Hidden, PillState.Home],
        [PillState.Home]          = [PillState.Hidden, PillState.MeetingPrompt, PillState.RecordingBar, PillState.Library, PillState.Settings],
        [PillState.Library]       = [PillState.Hidden, PillState.Home, PillState.Settings],
        [PillState.Settings]      = [PillState.Hidden, PillState.Home, PillState.Library],
    };

    public PillState Current { get; private set; } = PillState.Hidden;

    /// <summary>Wall-clock start of the current recording (lesson 10: duration is end − start, never UI ticks).</summary>
    public DateTimeOffset? RecordingStartedAt { get; private set; }

    /// <summary>Raised after every applied transition with (old, new).</summary>
    public event Action<PillState, PillState>? StateChanged;

    public bool CanGo(PillState next) => Allowed[Current].Contains(next);

    public bool TryGo(PillState next)
    {
        if (!CanGo(next)) return false;
        Apply(next);
        return true;
    }

    /// <summary>Demo/dev only (tray demo items, --state flag): jumps without transition rules.</summary>
    public void ForceForDemo(PillState next) => Apply(next);

    void Apply(PillState next)
    {
        var old = Current;
        var wasRecording = old is PillState.RecordingBar or PillState.RecordingOpen;
        var isRecording = next is PillState.RecordingBar or PillState.RecordingOpen;
        if (isRecording && !wasRecording)
            RecordingStartedAt = DateTimeOffset.UtcNow;
        Current = next;
        StateChanged?.Invoke(old, next);
    }
}
