using System;
using System.Linq;
using Toki.Calendar;

namespace Toki.Tests;

public class CalendarTests
{
    // ── PKCE ──

    [Fact]
    public void CodeVerifier_IsCorrectLengthAndCharset()
    {
        for (var i = 0; i < 50; i++)
        {
            var v = GoogleAuth.NewCodeVerifier();
            Assert.InRange(v.Length, 43, 128);                       // RFC 7636
            Assert.Matches("^[A-Za-z0-9._~-]+$", v);                 // unreserved set, base64url (no padding)
            Assert.DoesNotContain("=", v);
        }
    }

    [Fact]
    public void CodeVerifier_IsRandomEachTime()
        => Assert.True(Enumerable.Range(0, 20).Select(_ => GoogleAuth.NewCodeVerifier()).Distinct().Count() == 20);

    [Fact]
    public void State_IsRandomAndUrlSafe()
    {
        var states = Enumerable.Range(0, 20).Select(_ => GoogleAuth.NewState()).ToList();
        Assert.Equal(20, states.Distinct().Count());
        Assert.All(states, s => Assert.Matches("^[A-Za-z0-9._~-]+$", s));
    }

    // The canonical RFC 7636 Appendix B S256 vector — proves our challenge derivation is exactly right.
    [Fact]
    public void CodeChallenge_MatchesRfc7636Vector()
        => Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            GoogleAuth.CodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    // ── auth URL ──

    [Fact]
    public void BuildAuthUrl_HasEveryRequiredParam()
    {
        var url = GoogleAuth.BuildAuthUrl("cid.apps.googleusercontent.com",
            "http://127.0.0.1:5473", GoogleAuth.Scopes, "chal", "st");
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains("client_id=cid.apps.googleusercontent.com", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("code_challenge=chal", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=st", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A5473", url);   // exact-match, url-encoded
        Assert.Contains("calendar.events", url);                              // the write scope is requested
    }

    // ── id_token email ──

    [Fact]
    public void DecodeIdTokenEmail_ReadsTheEmailClaim()
    {
        // A JWT with header.payload.signature where payload = {"email":"sid@example.com"} (base64url, no pad).
        var payload = B64Url("{\"email\":\"sid@example.com\",\"email_verified\":true}");
        var jwt = $"{B64Url("{\"alg\":\"RS256\"}")}.{payload}.sig";
        Assert.Equal("sid@example.com", GoogleAuth.DecodeIdTokenEmail(jwt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]                                          // missing/garbage payload → null, never throws
    public void DecodeIdTokenEmail_IsNullOnJunk(string? token)
        => Assert.Null(GoogleAuth.DecodeIdTokenEmail(token));

    // ── events.list parsing ──

    [Fact]
    public void ParseEvents_ReadsTimedEventWithAttendeesAndMeet()
    {
        const string json = """
        { "items": [ {
            "id": "abc", "status": "confirmed", "summary": "Design sync",
            "location": "Room 4", "htmlLink": "https://cal/abc",
            "hangoutLink": "https://meet.google.com/xyz",
            "start": { "dateTime": "2026-08-05T15:00:00+05:30" },
            "end":   { "dateTime": "2026-08-05T15:30:00+05:30" },
            "attendees": [
              { "email": "sid@x.com", "self": true, "responseStatus": "accepted" },
              { "email": "ana@x.com", "displayName": "Ana", "organizer": true }
            ] } ] }
        """;
        var e = Assert.Single(CalendarClient.ParseEvents(json));
        Assert.Equal("abc", e.Id);
        Assert.Equal("Design sync", e.Summary);
        Assert.False(e.IsAllDay);
        Assert.Equal("Room 4", e.Location);
        Assert.Equal("https://meet.google.com/xyz", e.MeetLink);
        Assert.True(e.HasMeetLink);
        // 15:00 +05:30 == 09:30 UTC
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 9, 30, 0, TimeSpan.Zero), e.StartUtc);
        Assert.Equal(2, e.Attendees.Count);
        Assert.Contains(e.Attendees, a => a.IsSelf && a.Email == "sid@x.com");
        Assert.Contains(e.Attendees, a => a.IsOrganizer && a.Label == "Ana");
    }

    // The pagination fix: a page's nextPageToken must be surfaced so the client keeps fetching
    // (the 25-item cap silently emptied weeks 2–4 of the agenda).
    [Fact]
    public void ParseEventsPage_SurfacesNextPageToken()
    {
        const string withMore = """
        { "nextPageToken": "CONTINUE123", "items": [
            { "id": "a", "summary": "One", "start": { "dateTime": "2026-08-05T10:00:00Z" }, "end": { "dateTime": "2026-08-05T10:30:00Z" } } ] }
        """;
        var (events, next) = CalendarClient.ParseEventsPage(withMore);
        Assert.Single(events);
        Assert.Equal("CONTINUE123", next);

        const string lastPage = """
        { "items": [ { "id": "b", "summary": "Two", "start": { "dateTime": "2026-08-06T10:00:00Z" }, "end": { "dateTime": "2026-08-06T10:30:00Z" } } ] }
        """;
        var (_, none) = CalendarClient.ParseEventsPage(lastPage);
        Assert.Null(none);   // no token → the loop stops
    }

    [Fact]
    public void ParseEvents_HandlesAllDayEvent()
    {
        const string json = """
        { "items": [ { "id": "d1", "summary": "Holiday",
            "start": { "date": "2026-08-05" }, "end": { "date": "2026-08-06" } } ] }
        """;
        var e = Assert.Single(CalendarClient.ParseEvents(json));
        Assert.True(e.IsAllDay);
        Assert.Empty(e.Attendees);
        Assert.Null(e.MeetLink);
    }

    [Fact]
    public void ParseEvents_SkipsCancelledAndUsesConferenceEntryPoint()
    {
        const string json = """
        { "items": [
          { "id": "c1", "status": "cancelled", "start": { "dateTime": "2026-08-05T10:00:00Z" } },
          { "id": "c2", "summary": "Call", "start": { "dateTime": "2026-08-05T11:00:00Z" },
            "end": { "dateTime": "2026-08-05T11:30:00Z" },
            "conferenceData": { "entryPoints": [
              { "entryPointType": "phone", "uri": "tel:+100" },
              { "entryPointType": "video", "uri": "https://meet.google.com/vid" } ] } } ] }
        """;
        var e = Assert.Single(CalendarClient.ParseEvents(json));       // cancelled one dropped
        Assert.Equal("c2", e.Id);
        Assert.Equal("https://meet.google.com/vid", e.MeetLink);       // picked the video entry point, not phone
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]                                                 // no items
    [InlineData("{ \"items\": [] }")]
    [InlineData("{ \"items\": [ { \"id\": \"x\" } ] }")]               // item with no start → skipped
    public void ParseEvents_NeverThrowsOnOddInput(string json)
        => Assert.Empty(CalendarClient.ParseEvents(json));

    static string B64Url(string s)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── quick-add natural-language parsing ──

    static readonly DateTimeOffset Ref = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero); // Mon Aug 3, 09:00

    [Fact]
    public void QuickParse_ExtractsTitleTimeAndEmail()
    {
        var q = QuickEventParser.Parse("Design sync tomorrow 2pm with ana@work.com", Ref);
        Assert.Equal("Design sync", q.Summary);
        Assert.NotNull(q.Start);
        Assert.Equal(4, q.Start!.Value.Day);          // "tomorrow" = Aug 4
        Assert.Equal(14, q.Start!.Value.Hour);        // 2pm
        Assert.Contains("ana@work.com", q.Emails);
        Assert.True(q.HasTime);
    }

    [Fact]
    public void QuickParse_DefaultsThirtyMinutes()
    {
        var q = QuickEventParser.Parse("Coffee at 10am tomorrow", Ref);
        Assert.NotNull(q.Start);
        Assert.NotNull(q.End);
        Assert.Equal(30, (q.End!.Value - q.Start!.Value).TotalMinutes);
    }

    [Fact]
    public void QuickParse_NoTime_LeavesStartNull()
    {
        var q = QuickEventParser.Parse("Catch up with the team", Ref);
        Assert.Null(q.Start);
        Assert.False(q.HasTime);
        Assert.Equal("Catch up with the team", q.Summary);
    }

    [Fact]
    public void QuickParse_PastTimeRollsToTomorrow()
    {
        // Ref is 9am; "at 8am" already passed today → should land tomorrow, never in the past.
        var q = QuickEventParser.Parse("Standup at 8am", Ref);
        Assert.NotNull(q.Start);
        Assert.True(q.Start!.Value > Ref);
    }
}
