using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;
using Toki.Storage;

namespace Toki.Llm;

/// <summary>
/// Due-date-aware scoping for task questions. "What tasks do I have this week / what's due Friday /
/// what's overdue" are DEADLINE questions, not "tasks from meetings held this week" - the mistake that
/// made a temporal task query dump every open task on a recent library. This resolves each task's
/// free-text due ("Friday", "next week", "by EOD") against ITS meeting's date (a due said in a meeting
/// means the date after that meeting), then buckets open tasks into Overdue / Due-in-window /
/// undated-from-window - the market-validated shape (task-managers scope by due date; meeting tools
/// disclose the undated fallback).
/// </summary>
public static class TaskScheduling
{
    /// <summary>Resolve a task's free-text due to a concrete date, using the meeting date as the
    /// reference point and preferring the reading on/after the meeting (a deadline is not in the
    /// past relative to when it was set). Null when the text carries no parseable date ("ASAP",
    /// "when ready", or no due at all).</summary>
    public static DateTimeOffset? ParseDue(string? dueText, DateTimeOffset meetingDate)
    {
        if (string.IsNullOrWhiteSpace(dueText)) return null;
        // Common non-date phrasings carry no window.
        var t = dueText.Trim();
        if (Regex.IsMatch(t, @"^\s*(asap|tbd|n/?a|when\s+ready|ongoing|no\s+date)\s*$", RegexOptions.IgnoreCase)) return null;

        List<ModelResult> results;
        try { results = DateTimeRecognizer.RecognizeDateTime(t, Culture.English, refTime: meetingDate.DateTime); }
        catch { return null; }

        var refDay = meetingDate.Date;
        var explicitPast = Regex.IsMatch(t, @"\b(last|yesterday|ago|previous|prior)\b", RegexOptions.IgnoreCase);
        // Bare weekday / "this <weekday>" resolves ambiguously (Recognizers often returns the PAST
        // occurrence); a due is set FOR the future, so those roll forward. An explicitly-dated due
        // ("August 22", "March 3") is taken as-is.
        var weekdayShaped = Regex.IsMatch(t,
            @"\b(mon|tue|wed|thu|fri|sat|sun)(day|sday|nesday|rsday|urday)?\b|\btomorrow\b|\bnext\b",
            RegexOptions.IgnoreCase);

        var candidates = new List<DateTimeOffset>();
        foreach (var r in results)
        {
            if (r.Resolution is null || !r.Resolution.TryGetValue("values", out var raw)) continue;
            if (raw is not IList<Dictionary<string, string>> values) continue;
            foreach (var v in values)
                if (ToDay(v) is { } day) candidates.Add(day);
        }
        if (candidates.Count == 0) return null;

        // Prefer the earliest reading on/after the meeting (the soonest real deadline).
        var future = candidates.Where(d => d.Date >= refDay).OrderBy(d => d).ToList();
        if (future.Count > 0) return future[0];

        // Only past readings. Roll a weekday-shaped due forward to its next occurrence after the
        // meeting (Recognizers gave last Friday; a "due Friday" set on Monday means this Friday).
        if (weekdayShaped && !explicitPast)
        {
            var d = candidates.Max();
            while (d.Date < refDay) d = d.AddDays(7);
            return d;
        }
        return candidates.Max();   // a genuine explicit past date (rare)
    }

    static DateTimeOffset? ToDay(Dictionary<string, string> v)
    {
        v.TryGetValue("type", out var type);
        string? raw = type switch
        {
            "date" or "datetime" => v.GetValueOrDefault("value"),
            "daterange" or "datetimerange" => v.GetValueOrDefault("start"),
            _ => null,
        };
        if (raw is not null && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return new DateTimeOffset(d.Date, DateTimeOffset.Now.Offset);
        return null;
    }

    /// <summary>Detects an explicit "overdue / past due / late" ask (no date window needed).</summary>
    public static readonly Regex OverdueRx = new(
        @"\b(over\s?due|past\s+due|late|behind\s+on|missed\s+deadline)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A DEADLINE cue ("due", "deadline") - makes a dated task query scope by due date rather
    /// than by which meetings happened in the window.</summary>
    public static readonly Regex DueCueRx = new(
        @"\bdue\b|\bdeadline\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Past-tense task recall ("what tasks DID I HAVE", "what WAS assigned", "what DID I commit to") -
    // signals PROVENANCE (tasks from that time's meetings), not a live deadline view.
    static readonly Regex PastTenseTaskRx = new(
        @"\b(did\s+i\s+have|did\s+we\s+have|were\s+there|was\s+there|did\s+i\s+get|did\s+i\s+commit|"
        + @"was\s+assigned|were\s+assigned|did\s+i\s+agree|came\s+out\s+of|did\s+we\s+decide|"
        + @"did\s+i\s+take\s+on|had\s+to\s+do)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>How a task query with a time reference should be scoped. The subtle distinction the
    /// taxonomy calls Rule T / the "August 2nd" fix.</summary>
    public enum TimeMode { None, DueDate, Provenance }

    /// <summary>
    /// Decide whether a dated task query means DEADLINE ("what's due this week / Friday / overdue") or
    /// PROVENANCE ("what tasks did I have on Aug 2 / last week"). Deadline wins on an explicit due/overdue
    /// cue OR a present/future window; provenance wins on past-tense recall OR a purely-past window with
    /// no deadline cue. Pure function for testing.
    /// </summary>
    public static TimeMode ModeFor(string original, DateTimeOffset? start, DateTimeOffset? end, DateTimeOffset now)
    {
        var overdue = OverdueRx.IsMatch(original ?? "");
        var deadlineCue = overdue || DueCueRx.IsMatch(original ?? "");
        var hasRange = start.HasValue && end.HasValue;
        // A deadline cue with no parsed window ("what's due", "what's due by end of week") is still a
        // deadline VIEW - DueWindowTaskAnswer shows overdue + upcoming.
        if (!hasRange) return deadlineCue ? TimeMode.DueDate : TimeMode.None;
        var pastRecall = PastTenseTaskRx.IsMatch(original ?? "");
        var windowPast = end!.Value <= now;
        if (!deadlineCue && (pastRecall || windowPast)) return TimeMode.Provenance;
        return TimeMode.DueDate;
    }

    public sealed record Buckets(
        List<OpenAction> Overdue, List<OpenAction> DueInWindow,
        List<OpenAction> UndatedFromWindow, int OtherOpenCount)
    {
        public int Total => Overdue.Count + DueInWindow.Count + UndatedFromWindow.Count;
    }

    /// <summary>Bucket open tasks for a due-date query. <paramref name="start"/>/<paramref name="end"/>
    /// is the window ([start,end)); null = an "overdue"/"what's due" query with no explicit window.</summary>
    public static Buckets Bucket(IReadOnlyList<OpenAction> open, DateTimeOffset? start, DateTimeOffset? end, DateTimeOffset now)
    {
        var today = now.Date;
        var overdue = new List<OpenAction>();
        var dueIn = new List<OpenAction>();
        var undated = new List<OpenAction>();
        var other = 0;

        // Include the overdue bucket when the window reaches back to today or has no lower bound
        // ("this week"/"today"/"overdue") - but NOT for a purely-future window ("next week").
        var wantOverdue = start is null || start.Value.Date <= today;
        // Include the undated-from-window bucket only when the window includes today (present tasks
        // the user is asking about now).
        var wantUndated = start is not null && end is not null && start.Value <= now && now < end.Value;

        foreach (var a in open)
        {
            var due = ParseDue(a.Due, a.MeetingAt);
            var inWindow = start is not null && end is not null && due is not null
                           && due.Value >= start.Value.Date && due.Value < end.Value.Date;
            if (inWindow) { dueIn.Add(a); continue; }
            if (wantOverdue && due is not null && due.Value < today) { overdue.Add(a); continue; }
            if (wantUndated && due is null && a.MeetingAt >= start!.Value && a.MeetingAt < end!.Value) { undated.Add(a); continue; }
            other++;
        }
        // Soonest deadlines first inside each dated bucket.
        overdue.Sort((x, y) => (ParseDue(x.Due, x.MeetingAt) ?? today).CompareTo(ParseDue(y.Due, y.MeetingAt) ?? today));
        dueIn.Sort((x, y) => (ParseDue(x.Due, x.MeetingAt) ?? today).CompareTo(ParseDue(y.Due, y.MeetingAt) ?? today));
        return new Buckets(overdue, dueIn, undated, other);
    }
}
