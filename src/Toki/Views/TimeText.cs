using Toki.Storage;

namespace Toki.Views;

/// <summary>
/// Shared "when was this" formatting. LibraryWindow's card list keeps its own tighter
/// "3m ago" format on purpose (density choice for cards).
/// </summary>
public static class TimeText
{
    /// <summary>"just now" · "5 min ago" · "3h ago" · "yesterday" · "4 days ago".</summary>
    public static string Ago(DateTimeOffset when)
    {
        var d = DateTimeOffset.Now - when;
        if (d.TotalMinutes < 2) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 2) return "yesterday";
        return $"{(int)d.TotalDays} days ago";
    }

    /// <summary>Duration · relative time · app - the pill Notes screen's compact meta line.</summary>
    public static string CompactMeta(MeetingRecord m)
    {
        var parts = new List<string>();
        if (m.DurationMs > 0) parts.Add($"{Math.Max(1, m.DurationMs / 60000)} min");
        parts.Add(Ago(m.StartedAt));
        if (!string.IsNullOrWhiteSpace(m.App)) parts.Add(m.App!);
        return string.Join(" · ", parts);
    }

    /// <summary>When it happened, and how long it ran - the reading-pane meta line under a note title.</summary>
    public static string FullMeta(MeetingRecord m)
    {
        // TWO FACTS, NOT FOUR. This line used to read "24 min · Thursday, Sept 17 · 6:00 PM · Chrome",
        // sitting directly under the title of a note the reader opened deliberately from a list that
        // had already told them the app and the date. The reference notetaker sets the entire line
        // as "12:56 AM today".
        // The weekday and the app both go: the weekday is decoration once the date is present, and
        // which app hosted the call has never changed what anyone did with a note. Duration stays -
        // it is the one fact here that says something about the MEETING rather than about filing it.
        // Owner, 2026-09-27: "minimise the text". (Supersedes the four-part line of 2026-09-03.)
        var parts = new List<string> { m.StartedAt.ToString("MMM d · h:mm tt") };
        if (m.DurationMs > 0) parts.Add($"{Math.Max(1, m.DurationMs / 60000)} min");
        return string.Join("  ·  ", parts);
    }
}
