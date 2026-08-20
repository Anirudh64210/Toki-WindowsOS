using System.Text;
using System.Text.RegularExpressions;
using Toki.Transcription;

namespace Toki.Storage;

/// <summary>
/// Deterministic, instant, always-available note extraction - the "notes floor"
/// (brief §5). No model required. M6 layers LLM-written notes on top, but this
/// always works and is what a low-spec machine or a missing model falls back to.
/// </summary>
public static partial class NoteExtractor
{
    static readonly string[] DecisionCues =
        ["decided", "we agreed", "agreed to", "let's", "lets ", "we'll", "we will go",
         "going with", "go with", "final decision", "the decision", "moving to", "move to",
         "sign off", "signed off", "we should just", "plan is"];

    static readonly string[] ActionCues =
        ["will ", "i'll", "you'll", "he'll", "she'll", "they'll", "need to", "needs to",
         "have to", "has to", "action item", "to-do", "todo", "follow up", "followup",
         "send ", "wire ", "confirm", "set up", "create ", "add ", "fix ", "review ",
         "schedule", "ping ", "reach out", "make sure"];

    static readonly string[] DueCues =
        ["today", "tomorrow", "tonight", "this week", "next week", "this morning",
         "this afternoon", "monday", "tuesday", "wednesday", "thursday", "friday",
         "saturday", "sunday", "by end of day", "eod", "by friday", "by monday"];

    public static NoteSet Extract(IReadOnlyList<TranscriptSegment> transcript)
    {
        var sentences = Sentences(transcript);
        var notes = new NoteSet();

        foreach (var (speaker, s) in sentences)
        {
            var lower = s.ToLowerInvariant();
            if (s.TrimEnd().EndsWith('?'))
            {
                Add(notes.Questions, s);
            }
            else if (DecisionCues.Any(lower.Contains))
            {
                // Collective agreements are decisions; check before actions so a
                // decision that happens to mention an action verb isn't misfiled.
                Add(notes.Decisions, s);
            }
            else if (ActionCues.Any(lower.Contains))
            {
                notes.Actions.Add(new ActionItem { Text = s, Owner = Owner(s, speaker), Due = Due(lower) });
            }
        }
        DedupeActions(notes.Actions);

        // Uncapped extraction dumps dozens of raw questions/to-dos into a note. Keep the most
        // substantial few of each; the full transcript is one section away for anything trimmed.
        Trim(notes.Questions, MaxQuestions);
        Trim(notes.Decisions, MaxDecisions);
        if (notes.Actions.Count > MaxActions)
        {
            // An action with a named owner or a due date is worth more than a bare sentence.
            var keep = notes.Actions
                .OrderByDescending(a => (a.Owner is null ? 0 : 2) + (a.Due is null ? 0 : 1))
                .ThenByDescending(a => a.Text.Length)
                .Take(MaxActions).ToHashSet();
            notes.Actions.RemoveAll(a => !keep.Contains(a));
        }

        notes.Tldr = Tldr(sentences);
        return notes;
    }

    const int MaxQuestions = 10, MaxDecisions = 10, MaxActions = 12;

    /// <summary>Drop sub-4-word lines ("What?", "Is it good?" - connection checks and grunts),
    /// then keep the longest <paramref name="max"/> in their original order.</summary>
    static void Trim(List<string> list, int max)
    {
        list.RemoveAll(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4);
        if (list.Count <= max) return;
        var keep = new HashSet<string>(list.OrderByDescending(s => s.Length).Take(max));
        list.RemoveAll(s => !keep.Contains(s));
    }

    /// <summary>Title from the notes, heuristically (no separate LLM pass - lesson 9).</summary>
    public static string Title(NoteSet notes, string? app, DateTimeOffset when)
    {
        // Prefer an LLM-written title; else a decision (topic-y) over the TL;DR, which may open
        // with a question; else the TL;DR's first sentence.
        if (!string.IsNullOrWhiteSpace(notes.Title) && IsValidTitle(notes.Title!))
            return CapWords(notes.Title!, 8);

        // The canned low-substance summary must not become the title; for those meetings the
        // app-date fallback below is the honest name.
        var lowSubstance = notes.Tldr == Toki.Llm.LlmNoteWriter.LowSubstanceTldr;

        var candidate = (notes.Decisions.Count > 0 ? FirstMeaningful(notes.Decisions[0]) : null)
                        ?? (lowSubstance ? null : FirstSentence(notes.Tldr));
        if (candidate is not null && IsValidTitle(candidate))
            return CapWords(candidate, 7);

        var name = string.IsNullOrWhiteSpace(app) ? "Note" : Pretty(app!);
        return $"{name} - {when:MMM d}";
    }

    // ---- helpers ----

    static List<(Speaker Speaker, string Sentence)> Sentences(IReadOnlyList<TranscriptSegment> transcript)
    {
        var result = new List<(Speaker, string)>();
        foreach (var seg in transcript)
            foreach (var raw in SentenceSplit().Split(seg.Text))
            {
                var s = raw.Trim();
                if (s.Length >= 3) result.Add((seg.Speaker, s));
            }
        return result;
    }

    static string Tldr(List<(Speaker Speaker, string Sentence)> sentences)
    {
        var sb = new StringBuilder();
        foreach (var (_, s) in sentences)
        {
            if (sb.Length + s.Length > 240) break;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s);
            if (sb.Length > 140) break;
        }
        return sb.ToString();
    }

    /// <summary>Capitalised sentence-openers that are grammar, not a person ("This will take a
    /// while" is not owned by "This"). Mirrors the LLM path's ValidOwner rule.</summary>
    static readonly HashSet<string> NotAPerson = new(StringComparer.OrdinalIgnoreCase)
    {
        "This", "That", "There", "They", "Them", "These", "Those", "Then", "It", "He", "She",
        "We", "Someone", "Somebody", "Everyone", "Everybody", "Anyone", "Anybody", "Nobody",
        "Team", "People", "Management", "Everything", "Something", "Nothing", "What", "Who",
    };

    static string? Owner(string sentence, Speaker fallback)
    {
        // "Priya will confirm ..." / "Sid to wire ..." -> the leading proper noun.
        var m = OwnerPattern().Match(sentence);
        if (m.Success && !NotAPerson.Contains(m.Groups[1].Value)) return m.Groups[1].Value;
        if (m.Success) return null;   // pronoun-led ("They will handle it"): a task, but nobody named
        if (Regex.IsMatch(sentence, @"^\s*(I'?ll|I will|I need)", RegexOptions.IgnoreCase)) return "You";
        return fallback == Speaker.You ? "You" : null;
    }

    static string? Due(string lower)
    {
        foreach (var cue in DueCues)
            if (lower.Contains(cue))
                return char.ToUpperInvariant(cue[0]) + cue[1..];
        return null;
    }

    static void Add(List<string> list, string s)
    {
        var norm = TranscriptMerger.Normalize(s);
        if (!list.Any(x => TranscriptMerger.Normalize(x) == norm))
            list.Add(s);
    }

    static void DedupeActions(List<ActionItem> actions)
    {
        var seen = new HashSet<string>();
        actions.RemoveAll(a => !seen.Add(TranscriptMerger.Normalize(a.Text)));
    }

    static string? FirstMeaningful(string text)
    {
        var s = text.Trim();
        return s.Length >= 3 ? s : null;
    }

    static string? FirstSentence(string text)
    {
        var s = SentenceSplit().Split(text.Trim()).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    static bool IsValidTitle(string t)
    {
        var letters = t.Count(char.IsLetter);
        if (letters < 3) return false;                      // must have real words
        if (Regex.IsMatch(t, @"(.)\1{4,}")) return false;   // no "!!!!!"/"aaaaa" runs
        var ascii = t.Count(c => c < 128);
        return (double)ascii / t.Length > 0.8;              // mostly ASCII
    }

    /// <summary>Concise title: drop leading filler, keep the first N meaningful words.</summary>
    static string CapWords(string s, int maxWords)
    {
        var clean = s.Trim().TrimEnd('.', ',', ':', ';', ' ');
        // Drop a leading "We agreed to " / "The decision is " style lead-in.
        clean = Regex.Replace(clean, @"^(we (agreed|decided|will|should)|the (decision|plan|team)|let'?s|going to|it was decided)\s+(to\s+)?",
            "", RegexOptions.IgnoreCase);
        clean = clean.Length > 0 ? char.ToUpperInvariant(clean[0]) + clean[1..] : s;
        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= maxWords ? clean : string.Join(' ', words.Take(maxWords)).TrimEnd(',') + "…";
    }

    static string Pretty(string app)
    {
        var n = app.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        return n.Length == 0 ? "Note" : char.ToUpperInvariant(n[0]) + n[1..];
    }

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"^\s*([A-Z][a-z]+)\s+(?:will|to|should|needs? to|is going to)\b")]
    private static partial Regex OwnerPattern();
}
