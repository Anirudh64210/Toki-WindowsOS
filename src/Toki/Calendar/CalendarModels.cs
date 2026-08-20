namespace Toki.Calendar;

/// <summary>One person on an event's invite list.</summary>
public sealed record CalAttendee(string? Email, string? DisplayName, string? ResponseStatus, bool IsSelf, bool IsOrganizer)
{
    /// <summary>Best label for the UI: display name, else the email, else "Guest".</summary>
    public string Label => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName!
                         : !string.IsNullOrWhiteSpace(Email) ? Email! : "Guest";
}

/// <summary>A calendar event, normalized from Google's or Microsoft's JSON into just what Toki's
/// surfaces need. Times are stored in UTC; use <see cref="StartLocal"/>/<see cref="EndLocal"/> for
/// display.</summary>
public sealed record CalEvent(
    string Id,
    string Summary,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool IsAllDay,
    string? Location,
    string? MeetLink,
    string? HtmlLink,
    IReadOnlyList<CalAttendee> Attendees,
    string? Description = null)
{
    /// <summary>
    /// Which account this came from: edits/deletes must route to the owning account (an id from one
    /// service means nothing to the other). Defaults to Google for existing construction sites.
    /// </summary>
    public CalendarProvider Provider { get; init; } = CalendarProvider.Google;

    public DateTimeOffset StartLocal => StartUtc.ToLocalTime();
    public DateTimeOffset EndLocal => EndUtc.ToLocalTime();
    public TimeSpan Until => StartUtc - DateTimeOffset.UtcNow;
    public bool HasMeetLink => !string.IsNullOrWhiteSpace(MeetLink);
}

/// <summary>The minimal shape for creating an event (the meeting-creator). Times carry their own
/// offset (DateTimeOffset), so no IANA/Windows time-zone juggling is needed. <see cref="AddMeet"/>
/// asks Google to auto-attach a Google Meet link.</summary>
public sealed record NewEvent(
    string Summary,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<string>? AttendeeEmails = null,
    bool AddMeet = false,
    string? Description = null);
