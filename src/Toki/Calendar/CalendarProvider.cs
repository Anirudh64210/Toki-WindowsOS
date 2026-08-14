namespace Toki.Calendar;

/// <summary>
/// Which account an event came from, and which account a change should be sent to.
///
/// Toki can hold BOTH connections at once and shows one merged agenda, so this is not a global mode -
/// it travels on each event. Without it, editing an Outlook meeting would send a Google API call for an
/// id Google has never heard of, and the failure would look like a bug in the edit rather than in the
/// routing.
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
