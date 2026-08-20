namespace Toki.Calendar;

/// <summary>
/// Which account an event came from, and which account a change should be sent to. Both connections
/// can be held at once with one merged agenda, so this travels on each event, not as a global mode.
/// </summary>
public enum CalendarProvider
{
    Google,
    Outlook,
}

/// <summary>Display names, in the one place that spells them.</summary>
public static class CalendarProviders
{
    public static string Name(this CalendarProvider p) => p switch
    {
        CalendarProvider.Outlook => "Outlook",
        _ => "Google Calendar",
    };

    /// <summary>The short form, for a chip or a connected-account row where the word "Calendar" is
    /// already implied by everything around it.</summary>
    public static string ShortName(this CalendarProvider p) => p switch
    {
        CalendarProvider.Outlook => "Outlook",
        _ => "Google",
    };

    /// <summary>What this provider calls the video link it attaches to a meeting.</summary>
    public static string MeetingLinkName(this CalendarProvider p) => p switch
    {
        CalendarProvider.Outlook => "Teams meeting",
        _ => "Google Meet",
    };
}
