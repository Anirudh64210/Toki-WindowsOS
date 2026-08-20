namespace Toki.State;

/// <summary>
/// Every state the pill can be in:
/// hidden → meetingPrompt → recordingBar ⇄ recordingOpen → summarizing → notes | noSpeech,
/// plus home, library, settings.
/// </summary>
public enum PillState
{
    Hidden,
    Collapsed,
    MeetingPrompt,
    RecordingBar,
    RecordingOpen,
    Summarizing,
    Notes,
    NoSpeech,
    Home,
    Library,
    Settings,
    ManualNote,
    Ask,   // push-to-talk voice ask on the notch: listening → thinking → answer
    Calendar, // Google Calendar week view: upcoming meetings + create/edit/delete
    PrepCard, // pre-meeting prep: last time + still-open items + agenda, auto-surfaced before a meeting
    About,    // "Built with" open-source attribution (licence obligation), reached from Settings
    Onboarding, // first-run: what should I call you → privacy pitch → all set → Home
}
