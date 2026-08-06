using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Toki.Calendar;

/// <summary>
/// The tiny slice of the Google Calendar REST API Toki actually uses: list upcoming events,
/// create an event, delete an event. Raw HttpClient + System.Text.Json in the <see cref="Toki.Llm.ApiLlm"/>
/// house style - no Google SDK (it would drag several MB of assemblies into the single-file publish for
/// three endpoints). The access token rides on each request; it is never logged.
/// </summary>
public static class CalendarClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    const string Base = "https://www.googleapis.com/calendar/v3/calendars/primary/events";

    /// <summary>The ONE send/read/error-map path for every Calendar call. The four public methods
    /// used to carry their own copies, which had already diverged (Delete lost the error body,
    /// Insert lost sendUpdates) - and the HttpClient 30s timeout leaked out as a raw
    /// TaskCanceledException past callers that (per the documented contract) catch only
    /// GoogleAuthException. All of that is mapped here, once.</summary>
    static async Task<(System.Net.HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method, string url, string accessToken, object? jsonBody, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (jsonBody is not null) req.Content = JsonContent.Create(jsonBody);
        req.Headers.Authorization = new("Bearer", accessToken);
        try
        {
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (resp.StatusCode, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new GoogleAuthException("Google Calendar timed out - try again.", needsReconnect: false); }
        catch (HttpRequestException) { throw new GoogleAuthException("Couldn't reach Google Calendar (network error).", needsReconnect: false); }
    }

    /// <summary>Upcoming events on the primary calendar for the next <paramref name="days"/> days,
    /// soonest first, recurring events expanded into instances. Cancelled instances are dropped.
    /// Follows nextPageToken to completion: a single daily standup expands to ~30 instances over
    /// the 31-day horizon, and rendering only the first page made weeks 2–4 silently look empty.</summary>
    public static async Task<IReadOnlyList<CalEvent>> ListUpcomingAsync(string accessToken, int days, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        // URL-encode the timestamps: RFC3339 ends in "+00:00", and a raw '+' in a query string means a
        // space - Google then sees a malformed date and returns HTTP 400. EscapeDataString → %2B.
        var timeMin = Uri.EscapeDataString(Rfc3339(now));
        var timeMax = Uri.EscapeDataString(Rfc3339(now.AddDays(Math.Max(1, days))));
        var baseUrl = $"{Base}?timeMin={timeMin}&timeMax={timeMax}" +
                      "&singleEvents=true&orderBy=startTime&maxResults=250";
        var all = new List<CalEvent>();
        string? pageToken = null;
        for (var page = 0; page < 8; page++)   // hard cap - 2000 events in a month is beyond real use
        {
            var url = pageToken is null ? baseUrl : $"{baseUrl}&pageToken={Uri.EscapeDataString(pageToken)}";
            var (status, raw) = await SendAsync(HttpMethod.Get, url, accessToken, null, ct).ConfigureAwait(false);
            if (status is not System.Net.HttpStatusCode.OK) throw ErrorFor(status, raw);
            var (events, next) = ParseEventsPage(raw);
            all.AddRange(events);
            if (next is null or "") return all;
            pageToken = next;
        }
        return all;
    }

    /// <summary>Create an event on the primary calendar; returns the created event (with its id +
    /// htmlLink + any auto-attached Meet link).</summary>
    public static async Task<CalEvent> InsertAsync(string accessToken, NewEvent ev, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["summary"] = ev.Summary,
            ["start"] = new { dateTime = Rfc3339(ev.Start) },
            ["end"] = new { dateTime = Rfc3339(ev.End) },
        };
        if (ev.Description is { Length: > 0 }) body["description"] = ev.Description;
        var query = new List<string>();
        if (ev.AttendeeEmails is { Count: > 0 })
        {
            body["attendees"] = ev.AttendeeEmails.Select(e => new { email = e }).ToArray();
            // Same as Patch: guests supplied at CREATE time must get their invitation email -
            // Google's insert default is sendUpdates=none, so without this the event just
            // silently appeared on their calendars.
            query.Add("sendUpdates=all");
        }
        if (ev.AddMeet)
        {
            body["conferenceData"] = new
            {
                createRequest = new
                {
                    requestId = Guid.NewGuid().ToString("N"),
                    conferenceSolutionKey = new { type = "hangoutsMeet" },
                },
            };
            query.Add("conferenceDataVersion=1");
        }
        var url = query.Count > 0 ? $"{Base}?{string.Join("&", query)}" : Base;

        var (status, raw) = await SendAsync(HttpMethod.Post, url, accessToken, body, ct).ConfigureAwait(false);
        if ((int)status is < 200 or >= 300) throw ErrorFor(status, raw);
        using var doc = JsonDocument.Parse(raw);
        return ParseEvent(doc.RootElement) ?? throw new GoogleAuthException("Google created the event but sent an odd reply.", needsReconnect: false);
    }

    /// <summary>Edit an existing event (PATCH - only the fields we send change). <c>sendUpdates=all</c>
    /// so guests are notified of a time change. Adds a Meet link only if the event doesn't already have one.</summary>
    public static async Task<CalEvent> PatchAsync(string accessToken, string eventId, NewEvent ev, bool hasMeetAlready, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["summary"] = ev.Summary,
            ["start"] = new { dateTime = Rfc3339(ev.Start) },
            ["end"] = new { dateTime = Rfc3339(ev.End) },
        };
        if (ev.Description is { Length: > 0 }) body["description"] = ev.Description;
        if (ev.AttendeeEmails is { Count: > 0 })
            body["attendees"] = ev.AttendeeEmails.Select(e => new { email = e }).ToArray();
        var url = $"{Base}/{Uri.EscapeDataString(eventId)}?sendUpdates=all";
        if (ev.AddMeet && !hasMeetAlready)
        {
            body["conferenceData"] = new
            {
                createRequest = new { requestId = Guid.NewGuid().ToString("N"), conferenceSolutionKey = new { type = "hangoutsMeet" } },
            };
            url += "&conferenceDataVersion=1";
        }

        var (status, raw) = await SendAsync(new HttpMethod("PATCH"), url, accessToken, body, ct).ConfigureAwait(false);
        if ((int)status is < 200 or >= 300) throw ErrorFor(status, raw);
        using var doc = JsonDocument.Parse(raw);
        return ParseEvent(doc.RootElement) ?? throw new GoogleAuthException("Google updated the event but sent an odd reply.", needsReconnect: false);
    }

    /// <summary>Delete an event by id (204 on success; a 404/410 "already gone" is treated as success).</summary>
    public static async Task DeleteAsync(string accessToken, string eventId, CancellationToken ct = default)
    {
        var (status, raw) = await SendAsync(HttpMethod.Delete, $"{Base}/{Uri.EscapeDataString(eventId)}", accessToken, null, ct).ConfigureAwait(false);
        if ((int)status is >= 200 and < 300) return;
        if (status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone) return;
        throw ErrorFor(status, raw);
    }

    // ── parsing (defensive: skip odd items rather than throwing on the whole list) ──

    /// <summary>Parse a Google <c>events.list</c> body into <see cref="CalEvent"/>s. Never throws on
    /// odd shapes - a malformed item is skipped. Exposed for unit tests against canned fixtures.</summary>
    public static IReadOnlyList<CalEvent> ParseEvents(string json) => ParseEventsPage(json).Events;

    /// <summary>Like <see cref="ParseEvents"/>, plus the page's <c>nextPageToken</c> (null when this
    /// is the last page) - for the pagination loop in <see cref="ListUpcomingAsync"/>.</summary>
    public static (IReadOnlyList<CalEvent> Events, string? NextPageToken) ParseEventsPage(string json)
    {
        var list = new List<CalEvent>();
        string? next = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            next = Str(doc.RootElement, "nextPageToken");
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return (list, next);
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("status", out var st) && st.GetString() == "cancelled") continue;
                if (ParseEvent(item) is { } ev) list.Add(ev);
            }
        }
        catch (JsonException) { /* garbage body → empty list */ }
        return (list, next);
    }

    static CalEvent? ParseEvent(JsonElement item)
    {
        try
        {
            var id = Str(item, "id") ?? "";
            var summary = Str(item, "summary") ?? "(no title)";
            var (start, allDay) = ParseWhen(item, "start");
            var (end, _) = ParseWhen(item, "end");
            if (start is null) return null;   // an event with no start is unusable
            var end2 = end ?? start.Value;

            var location = Str(item, "location");
            var htmlLink = Str(item, "htmlLink");
            var meet = ExtractMeetLink(item);
            var attendees = ParseAttendees(item);
            var description = Str(item, "description");

            return new CalEvent(id, summary, start.Value.ToUniversalTime(), end2.ToUniversalTime(),
                allDay, location, meet, htmlLink, attendees, description);
        }
        catch { return null; }
    }

    // Google gives either { "dateTime": "...+05:30" } (timed) or { "date": "2026-08-03" } (all-day).
    static (DateTimeOffset? when, bool allDay) ParseWhen(JsonElement item, string prop)
    {
        if (!item.TryGetProperty(prop, out var w) || w.ValueKind != JsonValueKind.Object) return (null, false);
        if (w.TryGetProperty("dateTime", out var dt) && dt.GetString() is { } s
            && DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var v))
            return (v, false);
        if (w.TryGetProperty("date", out var d) && d.GetString() is { } ds
            && DateTime.TryParse(ds, out var day))
            return (new DateTimeOffset(day.Date, TimeZoneInfo.Local.GetUtcOffset(day.Date)), true);
        return (null, false);
    }

    static string? ExtractMeetLink(JsonElement item)
    {
        if (Str(item, "hangoutLink") is { Length: > 0 } h) return h;
        if (item.TryGetProperty("conferenceData", out var cd) && cd.ValueKind == JsonValueKind.Object
            && cd.TryGetProperty("entryPoints", out var eps) && eps.ValueKind == JsonValueKind.Array)
        {
            foreach (var ep in eps.EnumerateArray())
                if (ep.TryGetProperty("entryPointType", out var t) && t.GetString() == "video"
                    && ep.TryGetProperty("uri", out var u) && u.GetString() is { Length: > 0 } uri)
                    return uri;
        }
        return null;
    }

    static IReadOnlyList<CalAttendee> ParseAttendees(JsonElement item)
    {
        var list = new List<CalAttendee>();
        if (item.TryGetProperty("attendees", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
            {
                if (a.TryGetProperty("resource", out var res) && res.ValueKind == JsonValueKind.True) continue; // rooms
                list.Add(new CalAttendee(
                    Str(a, "email"), Str(a, "displayName"), Str(a, "responseStatus"),
                    Bool(a, "self"), Bool(a, "organizer")));
            }
        return list;
    }

    static string? Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    static string Rfc3339(DateTimeOffset t) => t.ToString("yyyy-MM-ddTHH:mm:sszzz");

    static GoogleAuthException ErrorFor(System.Net.HttpStatusCode status, string body)
    {
        var reason = (int)status switch
        {
            401 => "Your Google sign-in has expired - please connect again.",
            403 => "Google denied the request (permissions or quota).",
            404 => "That calendar item wasn't found.",
            429 => "Google is rate-limiting Calendar requests - try again shortly.",
            >= 500 => "Google Calendar had a server error.",
            _ => $"Google Calendar refused the request (HTTP {(int)status}).",
        };
        return new GoogleAuthException(reason, needsReconnect: (int)status == 401);
    }
}
