using Toki.Calendar;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Parsing Microsoft Graph into the same <see cref="CalEvent"/> the Google path produces.
///
/// The time-zone cases carry most of the weight. Graph sends <c>dateTime</c> with NO offset and a
/// separate <c>timeZone</c> field, so the obvious parse reads a UTC timestamp as local - a 2pm meeting
/// then appears at 7:30pm in India and nothing in the UI looks broken, it is just wrong.
/// </summary>
public class OutlookClientTests
{
    const string Page = """
    {
      "value": [
        {
          "id": "AAMkAD_1",
          "subject": "Pricing sync",
          "isAllDay": false,
          "isCancelled": false,
          "start": { "dateTime": "2026-08-20T14:00:00.0000000", "timeZone": "UTC" },
          "end":   { "dateTime": "2026-08-20T14:30:00.0000000", "timeZone": "UTC" },
          "location": { "displayName": "Room 2" },
          "webLink": "https://outlook.office365.com/calendar/item/1",
          "onlineMeeting": { "joinUrl": "https://teams.microsoft.com/l/meetup-join/abc" },
          "bodyPreview": "Agenda: the new page",
          "attendees": [
            { "type": "required", "emailAddress": { "address": "me@work.com", "name": "Me" },
              "status": { "response": "organizer" } },
            { "type": "required", "emailAddress": { "address": "ana@work.com", "name": "Ana" },
              "status": { "response": "tentativelyAccepted" } },
            { "type": "resource", "emailAddress": { "address": "room2@work.com", "name": "Room 2" },
              "status": { "response": "accepted" } }
          ]
        },
        {
          "id": "AAMkAD_2",
          "subject": "Cancelled thing",
          "isCancelled": true,
          "start": { "dateTime": "2026-08-21T09:00:00.0000000", "timeZone": "UTC" },
          "end":   { "dateTime": "2026-08-21T09:30:00.0000000", "timeZone": "UTC" }
        },
        {
          "id": "AAMkAD_3",
          "subject": "All day off",
          "isAllDay": true,
          "start": { "dateTime": "2026-08-22T00:00:00.0000000", "timeZone": "UTC" },
          "end":   { "dateTime": "2026-08-23T00:00:00.0000000", "timeZone": "UTC" }
        }
      ],
      "@odata.nextLink": "https://graph.microsoft.com/v1.0/me/calendarView?$skip=250"
    }
    """;

    [Fact]
    public void A_page_parses_into_events()
    {
        var events = OutlookClient.ParseEvents(Page, "me@work.com");
        Assert.Equal(2, events.Count);                       // the cancelled one is dropped
        Assert.Equal("Pricing sync", events[0].Summary);
        Assert.Equal("Room 2", events[0].Location);
        Assert.Equal("Agenda: the new page", events[0].Description);
    }

    [Fact]
    public void Every_event_is_tagged_as_Outlook()
    {
        // Without this, an edit would be sent to Google with an id Google has never seen.
        Assert.All(OutlookClient.ParseEvents(Page, "me@work.com"),
            e => Assert.Equal(CalendarProvider.Outlook, e.Provider));
    }

    [Fact]
    public void A_time_with_no_offset_is_read_as_UTC_not_local()
    {
        var e = OutlookClient.ParseEvents(Page, "me@work.com")[0];
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 14, 0, 0, TimeSpan.Zero), e.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 14, 30, 0, TimeSpan.Zero), e.EndUtc);
    }

    [Fact]
    public void A_named_zone_is_converted_rather_than_assumed()
    {
        const string json = """
        { "value": [ { "id": "x", "subject": "s",
          "start": { "dateTime": "2026-08-20T14:00:00.0000000", "timeZone": "Pacific Standard Time" },
          "end":   { "dateTime": "2026-08-20T15:00:00.0000000", "timeZone": "Pacific Standard Time" } } ] }
        """;
        var e = OutlookClient.ParseEvents(json)[0];
        // August is PDT, UTC-7, so 14:00 local is 21:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 21, 0, 0, TimeSpan.Zero), e.StartUtc);
    }

    [Fact]
    public void The_Teams_link_becomes_the_meeting_link()
    {
        var e = OutlookClient.ParseEvents(Page, "me@work.com")[0];
        Assert.True(e.HasMeetLink);
        Assert.Contains("teams.microsoft.com", e.MeetLink);
    }

    [Fact]
    public void Rooms_are_not_people()
    {
        var e = OutlookClient.ParseEvents(Page, "me@work.com")[0];
        Assert.Equal(2, e.Attendees.Count);
        Assert.DoesNotContain(e.Attendees, a => a.Email == "room2@work.com");
    }

    [Fact]
    public void The_connected_account_is_recognised_as_self()
    {
        // Graph never flags it, so the only way to know is the address we signed in with.
        var e = OutlookClient.ParseEvents(Page, "ME@WORK.COM")[0];
        Assert.Single(e.Attendees, a => a.IsSelf);
        Assert.Equal("me@work.com", e.Attendees.First(a => a.IsSelf).Email);
    }

    [Fact]
    public void Without_the_account_address_nobody_is_self()
    {
        var e = OutlookClient.ParseEvents(Page, null)[0];
        Assert.DoesNotContain(e.Attendees, a => a.IsSelf);
    }

    [Fact]
    public void Response_words_are_mapped_to_the_ones_the_UI_speaks()
    {
        var e = OutlookClient.ParseEvents(Page, "me@work.com")[0];
        Assert.Equal("tentative", e.Attendees.First(a => a.Email == "ana@work.com").ResponseStatus);
    }

    [Fact]
    public void An_all_day_event_is_marked_as_one()
    {
        Assert.True(OutlookClient.ParseEvents(Page, "me@work.com")[1].IsAllDay);
    }

    [Fact]
    public void The_next_page_link_is_returned_so_a_long_month_is_not_truncated()
    {
        var (_, next) = OutlookClient.ParseEventsPage(Page, null);
        Assert.Contains("skip=250", next);
    }

    [Fact]
    public void A_garbage_body_yields_an_empty_list_rather_than_throwing()
    {
        Assert.Empty(OutlookClient.ParseEvents("not json at all"));
        Assert.Empty(OutlookClient.ParseEvents("{}"));
    }

    [Fact]
    public void An_event_with_no_start_is_skipped_not_fatal()
    {
        const string json = """
        { "value": [ { "id": "broken", "subject": "no start" },
                     { "id": "fine", "subject": "ok",
                       "start": { "dateTime": "2026-08-20T09:00:00", "timeZone": "UTC" },
                       "end":   { "dateTime": "2026-08-20T09:30:00", "timeZone": "UTC" } } ] }
        """;
        var events = OutlookClient.ParseEvents(json);
        Assert.Single(events);
        Assert.Equal("ok", events[0].Summary);
    }

    [Fact]
    public void A_google_event_still_says_it_is_google()
    {
        // The default on CalEvent - so every existing construction site keeps meaning what it meant.
        var e = new CalEvent("id", "s", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false,
            null, null, null, Array.Empty<CalAttendee>());
        Assert.Equal(CalendarProvider.Google, e.Provider);
    }
}

/// <summary>The Microsoft sign-in URL, which cannot be exercised end to end without a real account.</summary>
public class MicrosoftAuthTests
{
    [Fact]
    public void The_auth_url_carries_PKCE_and_no_secret()
    {
        var url = MicrosoftAuth.BuildAuthUrl("client-123", "http://localhost:5050", "challenge-abc", "state-xyz");

        Assert.StartsWith("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?", url);
        Assert.Contains("client_id=client-123", url);
        Assert.Contains("code_challenge=challenge-abc", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=state-xyz", url);
        Assert.Contains("response_type=code", url);
        Assert.DoesNotContain("client_secret", url);   // public client: PKCE is the whole proof
    }

    [Fact]
    public void Offline_access_is_requested_or_the_connection_dies_in_an_hour()
    {
        Assert.Contains("offline_access", MicrosoftAuth.Scopes);
        Assert.Contains("Calendars.ReadWrite", MicrosoftAuth.Scopes);
    }

    [Fact]
    public void The_redirect_uri_is_sent_and_escaped()
    {
        var url = MicrosoftAuth.BuildAuthUrl("c", "http://localhost:5050", "ch", "st");
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A5050", url);
    }
}
