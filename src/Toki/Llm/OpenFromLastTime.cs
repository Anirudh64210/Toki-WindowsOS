using Toki.Storage;

namespace Toki.Llm;

/// <summary>
/// Carries a recurring meeting's unfinished business forward into the next one's note.
/// Deterministic, no model pass, calendar not required. It refuses to: fire on a one-off (no
/// prior meeting = no section, not an empty one), repeat a to-do this meeting already produced,
/// or carry an item the user has ticked.
/// </summary>
public static class OpenFromLastTime
{
    public const string Heading = "Still open from last time";

    /// <summary>Most a note will carry forward. Beyond a handful this stops being a reminder and starts
    /// being a backlog, which belongs in the Library rather than at the top of today's note.</summary>
    const int MaxItems = 4;

    /// <summary>How far back a "previous occurrence" may be. Recurring meetings run weekly to
    /// monthly; 45 days covers both. Without the cap, a stale record hands its to-dos to every
    /// later meeting whose title shares one word with it.</summary>
    const int MaxAgeDays = 45;

    /// <summary>
    /// The section, or null when there is nothing honest to say. <paramref name="current"/> must
    /// already be saved (it needs its id, so the search can exclude it).
    /// </summary>
    public static NoteSection? Build(MeetingRecord current, MeetingStore store)
    {
        try
        {
            if (current.Kind != MeetingKind.Meeting) return null;

            // Lowercased HERE, once: a capitalised term against a lowercased title silently matches
            // nothing, which is indistinguishable from "there was nothing to carry".
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
            if (m.StartedAt < current.StartedAt.AddDays(-MaxAgeDays)) continue;   // too old to be "last time"
            if (!Shares(m.Title, terms)) continue;
            if (best is null || m.StartedAt > best.StartedAt) best = m;  // the most recent one
        }
        return best;
    }

    /// <summary>Words that appear in half the meeting titles ever written. Shared, they say nothing:
    /// "Design review" and "Budget review" are not the same meeting.</summary>
    static readonly System.Collections.Generic.HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "meeting", "meet", "sync", "call", "chat", "review", "session", "discussion", "discuss",
        "weekly", "daily", "monthly", "biweekly", "standup", "stand", "catch", "catchup", "check",
        "checkin", "update", "updates", "touch", "base", "quick", "team", "general", "misc",
        "morning", "afternoon", "evening", "recurring", "regular", "followup", "follow",
    };

    /// <summary>
    /// Does this title cover the same ground? Whole-WORD overlap, never substring ("just" inside
    /// "adJUST" connects unrelated meetings), and the shared words have to MEAN something.
    ///
    /// ONE shared word used to be enough, which is what made this feature wrong so often: every
    /// title carrying "sync" or "review" inherited the unfinished work of every other one. Generic
    /// words are dropped first, and when both titles have more than one distinctive word, a single
    /// coincidence is not a match. The cost is asymmetric - a missed carry-forward is a note that
    /// says nothing, a wrong one attributes somebody else's unfinished work to this meeting - so
    /// this stays strict, and the user can dismiss whatever survives.
    /// </summary>
    static bool Shares(string? title, string[] terms)
    {
        var words = (title ?? "").ToLowerInvariant()
            .Split(new[] { ' ', '-', ',', ':', ';', '(', ')', '/', '.', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        var mine = terms.Where(t => !Generic.Contains(t)).ToArray();
        var theirs = words.Where(w => w.Length >= 3 && !Generic.Contains(w)).ToArray();
        if (mine.Length == 0 || theirs.Length == 0) return false;   // nothing distinctive to agree on

        var shared = mine.Count(theirs.Contains);
        if (shared == 0) return false;
        // A single shared word carries a match only when one side has nothing else to offer: a
        // one-word project title ("PixelFlow") is a strong signal on its own.
        return shared >= 2 || mine.Length == 1 || theirs.Length == 1;
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
