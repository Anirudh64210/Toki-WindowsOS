using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Toki.Calendar;

/// <summary>
/// The Microsoft Graph equivalent of <see cref="CalendarClient"/>: list, create, edit, delete on the
/// signed-in person's own calendar. Raw HttpClient + System.Text.Json, no Graph SDK - the SDK is many
/// megabytes of generated models for four endpoints, and this ships as a single file.
///
/// ── THE THREE PLACES GRAPH DIFFERS FROM GOOGLE, EACH OF WHICH BITES ──
///
///   1. LISTING A DATE RANGE. <c>/me/events</c> returns the SERIES master of a recurring meeting, once,
///      with a recurrence rule Toki would have to expand itself. <c>/me/calendarView</c> takes a start
///      and end and returns the expanded INSTANCES, which is what an agenda actually needs. Using the
///      wrong one makes a daily standup appear once, on the day it was created.
///
///   2. TIME ZONES. Graph returns <c>dateTime</c> with NO offset and a separate <c>timeZone</c> field,
///      so parsing it like an RFC3339 string silently reads it as local. The Prefer header below asks
///      for everything in UTC, and the parser then attaches UTC explicitly rather than assuming.
///
///   3. ATTENDEES AND SELF. Graph never marks which attendee is you, so "is this my meeting" has to be
///      answered by comparing against the connected account's own address, which the service passes in.
///
/// Everything else is deliberately the same shape as the Google client, so the two can sit behind one
/// facade without either one leaking its vocabulary upward.
/// </summary>
public static class OutlookClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    const string Base = "https://graph.microsoft.com/v1.0/me";

    /// <summary>The one send/read/error-map path, as on the Google side.</summary>
    static async Task<(System.Net.HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method, string url, string accessToken, object? jsonBody, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (jsonBody is not null) req.Content = JsonContent.Create(jsonBody);
        req.Headers.Authorization = new("Bearer", accessToken);
        // Ask for UTC rather than the mailbox's configured zone, so the parser never has to guess.
        req.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        try
        {
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (resp.StatusCode, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new GoogleAuthException("Outlook timed out - try again.", needsReconnect: false); }
        catch (HttpRequestException) { throw new GoogleAuthException("Couldn't reach Outlook (network error).", needsReconnect: false); }
    }

    /// <summary>Upcoming events for the next <paramref name="days"/> days, soonest first, recurring
    /// meetings expanded into instances. Follows @odata.nextLink to completion.</summary>
    public static async Task<IReadOnlyList<CalEvent>> ListUpcomingAsync(
        string accessToken, int days, string? selfEmail, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var start = Uri.EscapeDataString(Iso(now));
        var end = Uri.EscapeDataString(Iso(now.AddDays(Math.Max(1, days))));
        var url =
            $"{Base}/calendarView?startDateTime={start}&endDateTime={end}" +
            "&$orderby=start/dateTime&$top=250" +
            "&$select=id,subject,start,end,isAllDay,location,onlineMeeting,onlineMeetingUrl,webLink,attendees,bodyPreview,isCancelled";

        var all = new List<CalEvent>();
        for (var page = 0; page < 8; page++)   // the same hard cap as the Google path
        {
            var (status, raw) = await SendAsync(HttpMethod.Get, url, accessToken, null, ct).ConfigureAwait(false);
            if (status is not System.Net.HttpStatusCode.OK) throw ErrorFor(status, raw);
            var (events, next) = ParseEventsPage(raw, selfEmail);
            all.AddRange(events);
            if (string.IsNullOrEmpty(next)) return all;
            url = next!;
        }
        return all;
    }

    /// <summary>Create an event; returns it with its id, web link and any Teams link.</summary>
    public static async Task<CalEvent> InsertAsync(string accessToken, NewEvent ev, string? selfEmail, CancellationToken ct = default)
    {
        var (status, raw) = await SendAsync(HttpMethod.Post, $"{Base}/events", accessToken, BuildBody(ev, ev.AddMeet), ct)
            .ConfigureAwait(false);
        if ((int)status is < 200 or >= 300) throw ErrorFor(status, raw);
        using var doc = JsonDocument.Parse(raw);
        return ParseEvent(doc.RootElement, selfEmail)
            ?? throw new GoogleAuthException("Outlook created the event but sent an odd reply.", needsReconnect: false);
    }

    /// <summary>Edit an event. Graph PATCH is a merge, so only the fields sent here change - and a
    /// Teams link is only requested when the event does not already have one, because asking twice
    /// replaces the link people may already have in their diaries.</summary>
    public static async Task<CalEvent> PatchAsync(string accessToken, string eventId, NewEvent ev,
        bool hasMeetAlready, string? selfEmail, CancellationToken ct = default)
    {
        var url = $"{Base}/events/{Uri.EscapeDataString(eventId)}";
        var (status, raw) = await SendAsync(new HttpMethod("PATCH"), url, accessToken,
            BuildBody(ev, ev.AddMeet && !hasMeetAlready), ct).ConfigureAwait(false);
        if ((int)status is < 200 or >= 300) throw ErrorFor(status, raw);
        using var doc = JsonDocument.Parse(raw);
        return ParseEvent(doc.RootElement, selfEmail)
            ?? throw new GoogleAuthException("Outlook updated the event but sent an odd reply.", needsReconnect: false);
    }

    /// <summary>Delete by id. An already-gone event counts as success - the user wanted it gone.</summary>
    public static async Task DeleteAsync(string accessToken, string eventId, CancellationToken ct = default)
    {
        var (status, raw) = await SendAsync(HttpMethod.Delete,
            $"{Base}/events/{Uri.EscapeDataString(eventId)}", accessToken, null, ct).ConfigureAwait(false);
        if ((int)status is >= 200 and < 300) return;
        if (status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone) return;
        throw ErrorFor(status, raw);
    }

    static Dictionary<string, object?> BuildBody(NewEvent ev, bool wantOnlineMeeting)
    {
        var body = new Dictionary<string, object?>
        {
            ["subject"] = ev.Summary,
            ["start"] = new { dateTime = Graph(ev.Start), timeZone = "UTC" },
            ["end"] = new { dateTime = Graph(ev.End), timeZone = "UTC" },
        };
        if (ev.Description is { Length: > 0 })
            body["body"] = new { contentType = "text", content = ev.Description };
        if (ev.AttendeeEmails is { Count: > 0 })
            body["attendees"] = ev.AttendeeEmails
                .Select(e => new { emailAddress = new { address = e }, type = "required" }).ToArray();
        if (wantOnlineMeeting)
        {
            body["isOnlineMeeting"] = true;
            body["onlineMeetingProvider"] = "teamsForBusiness";
        }
        return body;
    }

    // ── parsing (defensive: a malformed item is skipped, never fatal to the whole list) ──

    /// <summary>Parse a Graph calendarView body. Exposed for unit tests against canned fixtures.</summary>
    public static IReadOnlyList<CalEvent> ParseEvents(string json, string? selfEmail = null)
        => ParseEventsPage(json, selfEmail).Events;

    public static (IReadOnlyList<CalEvent> Events, string? NextLink) ParseEventsPage(string json, string? selfEmail)
    {
        var list = new List<CalEvent>();
        string? next = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            next = Str(doc.RootElement, "@odata.nextLink");
            if (!doc.RootElement.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array)
                return (list, next);
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("isCancelled", out var c) && c.ValueKind == JsonValueKind.True) continue;
                if (ParseEvent(item, selfEmail) is { } ev) list.Add(ev);
            }
        }
        catch (JsonException) { /* garbage body -> empty list, same as the Google path */ }
        return (list, next);
    }

    static CalEvent? ParseEvent(JsonElement item, string? selfEmail)
    {
        try
        {
            var id = Str(item, "id") ?? "";
            var subject = Str(item, "subject");
            if (string.IsNullOrWhiteSpace(subject)) subject = "(no title)";
            var allDay = item.TryGetProperty("isAllDay", out var ad) && ad.ValueKind == JsonValueKind.True;

            var start = ParseWhen(item, "start");
            if (start is null) return null;                  // an event with no start is unusable
            var end = ParseWhen(item, "end") ?? start.Value;

            string? location = null;
            if (item.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
                location = Str(loc, "displayName");

            return new CalEvent(id, subject!, start.Value, end, allDay, location,
                MeetingLink(item), Str(item, "webLink"), Attendees(item, selfEmail), Str(item, "bodyPreview"))
            { Provider = CalendarProvider.Outlook };
        }
        catch { return null; }
    }

    /// <summary>
    /// Graph sends <c>{ "dateTime": "2026-08-13T14:00:00.0000000", "timeZone": "UTC" }</c> - note the
    /// missing offset. Parsed as-is it becomes a LOCAL time, which is how a 2pm meeting shows up at 7:30
    /// in another zone. The Prefer header asks for UTC, so the zone is attached explicitly here; a
    /// non-UTC reply (an older mailbox setting) is converted rather than trusted.
    /// </summary>
    static DateTimeOffset? ParseWhen(JsonElement item, string prop)
    {
        if (!item.TryGetProperty(prop, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (Str(w, "dateTime") is not { Length: > 0 } s) return null;
        if (!DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var naive)) return null;

        var zone = Str(w, "timeZone");
        if (string.IsNullOrWhiteSpace(zone) || zone.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Utc));

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(zone!);
            return new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Unspecified), tz.GetUtcOffset(naive))
                .ToUniversalTime();
        }
        catch
        {
            // An unrecognised zone id is better read as UTC (what we asked for) than as this machine's
            // local time, which would be wrong by a fixed and confusing amount.
            return new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Utc));
        }
    }

    static string? MeetingLink(JsonElement item)
    {
        if (item.TryGetProperty("onlineMeeting", out var om) && om.ValueKind == JsonValueKind.Object
            && Str(om, "joinUrl") is { Length: > 0 } join) return join;
        return Str(item, "onlineMeetingUrl") is { Length: > 0 } url ? url : null;
    }

    /// <summary>Graph does not flag which attendee is you, so the connected account's address is the
    /// only way to know. Without it every event would look like somebody else's.</summary>
    static IReadOnlyList<CalAttendee> Attendees(JsonElement item, string? selfEmail)
    {
        var list = new List<CalAttendee>();
        if (!item.TryGetProperty("attendees", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;

        foreach (var a in arr.EnumerateArray())
        {
            if (Str(a, "type") == "resource") continue;             // meeting rooms, not people
            string? email = null, name = null;
            if (a.TryGetProperty("emailAddress", out var ea) && ea.ValueKind == JsonValueKind.Object)
            {
                email = Str(ea, "address");
                name = Str(ea, "name");
            }
            string? response = null;
            if (a.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object)
                response = Str(st, "response");

            var isSelf = selfEmail is { Length: > 0 } && string.Equals(email, selfEmail, StringComparison.OrdinalIgnoreCase);
            list.Add(new CalAttendee(email, name, Response(response), isSelf, IsOrganizer: false));
        }
        return list;
    }

    /// <summary>Graph's response words mapped onto the ones the UI already speaks (Google's).</summary>
    static string? Response(string? graph) => graph?.ToLowerInvariant() switch
    {
        "accepted" or "organizer" => "accepted",
        "declined" => "declined",
        "tentativelyaccepted" => "tentative",
        "notresponded" or "none" => "needsAction",
        _ => graph,
    };

    static string? Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    /// <summary>Graph wants a local-looking string plus a separate timeZone field, never an offset.</summary>
    static string Graph(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss");

    static GoogleAuthException ErrorFor(System.Net.HttpStatusCode status, string body)
    {
        var reason = (int)status switch
        {
            401 => "Your Outlook sign-in has expired - please connect again.",
            403 => "Outlook denied the request (permissions).",
            404 => "That calendar item wasn't found.",
            429 => "Outlook is rate-limiting requests - try again shortly.",
            >= 500 => "Outlook had a server error.",
            _ => $"Outlook refused the request (HTTP {(int)status}).",
        };
        return new GoogleAuthException(reason, needsReconnect: (int)status == 401);
    }
}
