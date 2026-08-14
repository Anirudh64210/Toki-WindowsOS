using Toki.Storage;

namespace Toki.Llm;

/// <summary>
/// Carries a recurring meeting's unfinished business forward into the next one's note.
///
/// The prep card has computed this since M7.2 and it was the most useful thing on it - what was left
/// open last time is the first thing anyone wants when the same meeting comes round again. But the prep
/// card only exists BEFORE a meeting with a calendar event; the note is where people actually look
/// afterwards, and it had no memory of the previous occurrence at all.
///
/// So the note gets its own section, built the same deterministic way: no model pass, no extra wait, no
/// chance of an invented item, and it works whether or not the meeting came from a calendar.
///
/// THREE THINGS IT REFUSES TO DO, because each would make the section a liability:
///   • It never fires on a one-off. No prior meeting means no section, not an empty one.
///   • It never repeats a to-do THIS meeting already produced - the same task listed twice, once as
///     new and once as carried over, reads as two separate jobs.
///   • It never carries an item the user has ticked. Done is done, and re-surfacing it would make the
///     tick feel like it did not take.
/// </summary>
public static class OpenFromLastTime
{
    public const string Heading = "Still open from last time";

    /// <summary>Most a note will carry forward. Beyond a handful this stops being a reminder and starts
    /// being a backlog, which belongs in the Library rather than at the top of today's note.</summary>
    const int MaxItems = 4;

    /// <summary>
    /// The section, or null when there is nothing honest to say. <paramref name="current"/> must
    /// already be saved (it needs its id, so the search can exclude it).
    /// </summary>
    public static NoteSection? Build(MeetingRecord current, MeetingStore store)
    {
        try
        {
            if (current.Kind != MeetingKind.Meeting) return null;

            // Lowercased HERE, once. ContentQuery preserves the title's own casing, and comparing a
            // capitalised term against a lowercased title silently matches nothing - the section simply
            // never appears, which is indistinguishable from "there was nothing to carry".
            var terms = NotesQa.ContentQuery(current.Title)
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length >= 3)
                .ToArray();
            if (terms.Length == 0) return null;   // a title with no content words cannot match anything

            if (FindPrevious(current, terms, store) is not { } previous) return null;

            var alreadyHere = current.Notes.Actions.Select(a => Key(a.Text)).ToHashSet(StringComparer.Ordinal);
            var carried = previous.Notes.Actions
                .Where(a => !a.Done)
                .Where(a => !string.IsNullOrWhiteSpace(a.Text))
                .Where(a => !alreadyHere.Contains(Key(a.Text)))
                .Take(MaxItems)
                .Select(Line)
                .ToList();

            return carried.Count == 0 ? null : new NoteSection { Heading = Heading, Bullets = carried };
        }
        catch (Exception ex)
        {
            // A store hiccup must never cost the user their notes - the section is a bonus, not the note.
            App.Log($"open-from-last-time skipped (non-fatal): {ex.Message}");
            return null;
        }
    }

    /// <summary>The most recent EARLIER meeting whose title covers the same ground. Deliberately strict:
    /// a wrong match would attribute somebody else's unfinished work to this meeting.</summary>
    static MeetingRecord? FindPrevious(MeetingRecord current, string[] terms, MeetingStore store)
    {
        MeetingRecord? best = null;
        foreach (var id in store.TitleMatchIds(terms))
        {
            if (id == current.Id) continue;
            var m = store.Get(id);
            if (m is null || m.Kind != MeetingKind.Meeting) continue;
            if (m.StartedAt >= current.StartedAt) continue;              // must be a PRIOR occurrence
            if (!Shares(m.Title, terms)) continue;
            if (best is null || m.StartedAt > best.StartedAt) best = m;  // the most recent one
        }
        return best;
    }

    static bool Shares(string? title, string[] terms)
    {
        var t = (title ?? "").ToLowerInvariant();
        return terms.Any(term => t.Contains(term));
    }

    /// <summary>The carried line, keeping the owner when one is known - "who still owes this" is half
    /// the value of the reminder.</summary>
    static string Line(ActionItem a)
    {
        var text = a.Text.Trim();
        return string.IsNullOrWhiteSpace(a.Owner) ? text : $"{text} ({a.Owner.Trim()})";
    }

    /// <summary>Loose enough that a re-worded repeat of the same task counts as the same task.</summary>
    static string Key(string? text) =>
        new((text ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
