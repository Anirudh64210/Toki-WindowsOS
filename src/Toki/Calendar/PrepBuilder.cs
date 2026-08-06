using System.Net;
using System.Text.RegularExpressions;
using Toki.Llm;
using Toki.Storage;

namespace Toki.Calendar;

/// <summary>The assembled content for a pre-meeting prep card. <see cref="LastTime"/> is null for a
/// first-time meeting (an honest empty state - we never invent a "last time").</summary>
public sealed record PrepContent(
    CalEvent Event,
    MeetingRecord? LastTime,
    IReadOnlyList<ActionItem> StillOpen,
    IReadOnlyList<string> Agenda)
{
    public bool HasRecap => LastTime is not null;
}

/// <summary>
/// Builds a prep card's content DETERMINISTICALLY - pure store reads + string parsing, no LLM, so it's
/// instant and costs nothing on a low-memory machine. Matches a calendar event to a prior meeting by
/// TITLE/topic similarity (attendee emails aren't stored in notes), and is honest when there's no
/// confident match. Provider-neutral (takes a <see cref="CalEvent"/>).
/// </summary>
public static class PrepBuilder
{
    static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);
    static readonly Regex BulletLead = new(@"^\s*([-*•·]|\d+[.)])\s*", RegexOptions.Compiled);
    static readonly Regex UrlOnly = new(@"^\s*https?://\S+\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static PrepContent Build(CalEvent e, MeetingStore store)
    {
        try
        {
            var terms = NotesQa.ContentQuery(e.Summary)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var last = terms.Length == 0 ? null : FindLastMeeting(e, terms, store);
            var open = last?.Notes.Actions.Where(a => !a.Done).Take(3).ToList() ?? new List<ActionItem>();
            var agenda = ParseAgenda(e.Description);
            return new PrepContent(e, last, open, agenda);
        }
        catch
        {
            // A store hiccup must never break the card - degrade to the first-time layout.
            return new PrepContent(e, null, Array.Empty<ActionItem>(), ParseAgenda(e.Description));
        }
    }

    // Title hits are confident (take the first prior meeting with a recap); FTS hits additionally
    // require a shared title term, so a noisy prefix-OR match can't invent an unrelated "last time".
    static MeetingRecord? FindLastMeeting(CalEvent e, string[] terms, MeetingStore store)
    {
        foreach (var id in store.TitleMatchIds(terms))
            if (Accept(store.Get(id), e, requireTitleShare: false, terms) is { } m) return m;

        foreach (var id in store.SearchRelevant(string.Join(" ", terms), 3))
            if (Accept(store.Get(id), e, requireTitleShare: true, terms) is { } m) return m;

        return null;
    }

    static MeetingRecord? Accept(MeetingRecord? m, CalEvent e, bool requireTitleShare, string[] terms)
    {
        if (m is null || m.Kind != MeetingKind.Meeting) return null;
        if (m.StartedAt >= e.StartLocal) return null;                 // must be a PRIOR meeting
        if (requireTitleShare && !TitleShares(m.Title, terms)) return null;
        if (string.IsNullOrWhiteSpace(Recap(m))) return null;          // a recap with nothing to say → not useful
        return m;
    }

    static bool TitleShares(string? title, string[] terms)
    {
        var t = (title ?? "").ToLowerInvariant();
        return terms.Any(term => term.Length >= 3 && t.Contains(term));
    }

    /// <summary>The one-line recap: the meeting's TL;DR, else the first key point.</summary>
    public static string Recap(MeetingRecord m)
    {
        if (!string.IsNullOrWhiteSpace(m.Notes.Tldr)) return m.Notes.Tldr.Trim();
        foreach (var sec in m.Notes.EffectiveSections())
            if (sec.Bullets.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b)) is { } b) return b.Trim();
        return "";
    }

    /// <summary>"TODAY" / "YESTERDAY" / "N DAYS AGO" / "N WEEKS AGO" - for the LAST TIME header.</summary>
    public static string AgoLabel(DateTimeOffset when)
    {
        var days = (int)(DateTimeOffset.Now.Date - when.Date).TotalDays;
        return days switch
        {
            <= 0 => "TODAY",
            1 => "YESTERDAY",
            < 7 => $"{days} DAYS AGO",
            < 14 => "LAST WEEK",
            < 60 => $"{days / 7} WEEKS AGO",
            _ => $"{when:MMM d}".ToUpperInvariant(),
        };
    }

    /// <summary>
    /// The prep card AS AN EDITABLE NOTE. Pressing Edit on the card doesn't open a blank sheet beside
    /// it - it hands you the card's own content as text you can carry on writing in (owner, 2026-08-05),
    /// so the still-open items arrive as real tick boxes and the agenda as real bullets.
    /// Deterministic, same as everything else on this card: no model, no network.
    /// </summary>
    public static string ToMarkdown(PrepContent prep)
    {
        var sb = new System.Text.StringBuilder();

        if (prep.LastTime is { } m && Recap(m) is { Length: > 0 } recap)
            sb.Append("## Last time\n").Append(recap).Append("\n\n");

        if (prep.StillOpen.Count > 0)
        {
            sb.Append("## Still open\n");
            foreach (var a in prep.StillOpen)
            {
                var owner = string.IsNullOrWhiteSpace(a.Owner) ? "" : a.Owner + " - ";
                sb.Append(a.Done ? "- [x] " : "- [ ] ").Append(owner).Append(a.Text).Append('\n');
            }
            sb.Append('\n');
        }

        if (prep.Agenda.Count > 0)
        {
            sb.Append("## Agenda\n");
            foreach (var line in prep.Agenda) sb.Append("- ").Append(line).Append('\n');
            sb.Append('\n');
        }

        // Always end with somewhere to type - an editor that opens with the caret in a heading you
        // didn't write is an editor you have to tidy before you can use it.
        sb.Append("## My notes\n");
        return sb.ToString();
    }

    /// <summary>Turn a calendar event's description into up to 4 agenda bullets. Strips HTML, decodes
    /// entities, removes list markers, drops empty / URL-only lines. Empty result → no agenda section.</summary>
    public static IReadOnlyList<string> ParseAgenda(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return Array.Empty<string>();
        var text = WebUtility.HtmlDecode(HtmlTag.Replace(description, "\n"));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => BulletLead.Replace(l, "").Trim())
            .Where(l => l.Length > 0 && !UrlOnly.IsMatch(l))
            .Take(4)
            .ToList();
        return lines;
    }
}
