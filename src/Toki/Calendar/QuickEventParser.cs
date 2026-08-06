using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;

namespace Toki.Calendar;

/// <summary>Result of parsing a quick-add line like "Design sync tomorrow 2pm with ana@x.com".</summary>
public sealed record QuickEvent(string Summary, DateTimeOffset? Start, DateTimeOffset? End, IReadOnlyList<string> Emails)
{
    public bool HasTime => Start is not null;
}

/// <summary>
/// Natural-language event capture for the create composer. Wraps Microsoft.Recognizers (already shipped,
/// used by <see cref="Toki.Llm.QueryUnderstanding"/>) but **future-preferred** (a meeting is scheduled
/// ahead, the opposite of the notes query parser). Extracts the datetime, pulls out any emails, and the
/// remaining words are the title. Pure + deterministic → unit-testable.
/// </summary>
public static class QuickEventParser
{
    static readonly Regex EmailRx = new(@"\b[\w.+-]+@[\w.-]+\.\w{2,}\b", RegexOptions.Compiled);
    // Trailing connector words left dangling after the time/emails are stripped ("… with", "… at").
    static readonly Regex DanglingRx = new(@"[\s,]+(with|at|on|for|and|to)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static QuickEvent Parse(string text, DateTimeOffset now)
    {
        text ??= "";
        var emails = EmailRx.Matches(text).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var withoutEmails = EmailRx.Replace(text, " ");

        DateTimeOffset? start = null, end = null;
        var spans = new List<(int start, int len)>();

        List<ModelResult> results;
        try { results = DateTimeRecognizer.RecognizeDateTime(withoutEmails, Culture.English, refTime: now.DateTime); }
        catch { results = new(); }

        foreach (var r in results)
        {
            if (r.Resolution is null || !r.Resolution.TryGetValue("values", out var raw)) continue;
            if (raw is not IList<Dictionary<string, string>> values || values.Count == 0) continue;

            var picked = PickFuture(values, now);
            if (picked is null) continue;

            // Prefer the earliest FUTURE reading found (the soonest sensible slot).
            if (start is null || picked.Value.s < start)
            {
                start = picked.Value.s;
                end = picked.Value.e;
            }
            if (r.Start >= 0 && r.End >= r.Start) spans.Add((r.Start, r.End - r.Start + 1));
        }

        // A start with no explicit end → default 30-minute meeting.
        if (start is not null && (end is null || end <= start)) end = start.Value.AddMinutes(30);

        var summary = CleanSummary(StripSpans(withoutEmails, spans));
        return new QuickEvent(summary, start, end, emails);
    }

    // Choose the soonest reading that is in the future; if only past readings exist (e.g. "2pm" already
    // passed today), roll a time-only reading forward a day so a create always lands ahead.
    static (DateTimeOffset s, DateTimeOffset e)? PickFuture(IList<Dictionary<string, string>> values, DateTimeOffset now)
    {
        var futures = new List<(DateTimeOffset s, DateTimeOffset e)>();
        (DateTimeOffset s, DateTimeOffset e)? past = null;
        foreach (var v in values)
        {
            var r = ToRange(v);
            if (r is null) continue;
            if (r.Value.s >= now) futures.Add(r.Value);
            else if (past is null || r.Value.s > past.Value.s) past = r;
        }
        if (futures.Count > 0)
        {
            // Ambiguous bare hours ("at 3") resolve to BOTH 3 AM and 3 PM - prefer the business-hours
            // reading (7am–8pm), since a meeting "at 3" means the afternoon; else the soonest future.
            static bool Biz((DateTimeOffset s, DateTimeOffset e) x) => x.s.Hour is >= 7 and < 20;
            var biz = futures.Where(Biz).OrderBy(x => x.s).ToList();
            return biz.Count > 0 ? biz[0] : futures.OrderBy(x => x.s).First();
        }
        if (past is not null)
        {
            // Bump a same-day-but-past reading to tomorrow so "lunch at 1pm" (when it's 3pm) means
            // tomorrow. Rebuild from the local WALL CLOCK, not the instant: AddDays on the
            // DateTimeOffset keeps today's UTC offset, which lands one hour off across a DST change.
            var span = past.Value.e - past.Value.s;
            var localTomorrow = past.Value.s.DateTime.AddDays(1);
            var s = new DateTimeOffset(localTomorrow, TimeZoneInfo.Local.GetUtcOffset(localTomorrow));
            return (s, s + span);
        }
        return null;
    }

    static (DateTimeOffset s, DateTimeOffset e)? ToRange(Dictionary<string, string> v)
    {
        v.TryGetValue("type", out var type);
        switch (type)
        {
            case "datetime":
            case "time":
                if (v.TryGetValue("value", out var val) &&
                    DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    return (new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt)),
                            new DateTimeOffset(dt.AddMinutes(30), TimeZoneInfo.Local.GetUtcOffset(dt)));
                return null;
            case "datetimerange":
            case "timerange":
                if (v.TryGetValue("start", out var s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ds)
                    && v.TryGetValue("end", out var e) && DateTime.TryParse(e, CultureInfo.InvariantCulture, DateTimeStyles.None, out var de) && de > ds)
                    return (new DateTimeOffset(ds, TimeZoneInfo.Local.GetUtcOffset(ds)),
                            new DateTimeOffset(de, TimeZoneInfo.Local.GetUtcOffset(de)));
                return null;
            case "date":
                // Date with no time → 9:00 AM default slot on that day.
                if (v.TryGetValue("value", out var dv) && DateTime.TryParse(dv, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    var at = day.Date.AddHours(9);
                    return (new DateTimeOffset(at, TimeZoneInfo.Local.GetUtcOffset(at)),
                            new DateTimeOffset(at.AddMinutes(30), TimeZoneInfo.Local.GetUtcOffset(at)));
                }
                return null;
            default:
                return null;
        }
    }

    static string StripSpans(string text, List<(int start, int len)> spans)
    {
        foreach (var (s, len) in spans.OrderByDescending(x => x.start))
            if (s >= 0 && s + len <= text.Length) text = text.Remove(s, len);
        return text;
    }

    static string CleanSummary(string s)
    {
        s = Regex.Replace(s, @"\s{2,}", " ").Trim(' ', ',', '-', '·');
        s = DanglingRx.Replace(s, "");
        return s.Trim(' ', ',', '-', '·');
    }
}
