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

    /// <summary>Duration · full date/time · app - the reading-pane meta line under a note title.</summary>
    public static string FullMeta(MeetingRecord m)
    {
        var parts = new List<string>();
        if (m.DurationMs > 0) parts.Add($"{Math.Max(1, m.DurationMs / 60000)} min");
        parts.Add(m.StartedAt.ToString("dddd, MMM d · h:mm tt"));
        if (!string.IsNullOrWhiteSpace(m.App)) parts.Add(m.App!);
        return string.Join("  ·  ", parts);
    }
}
