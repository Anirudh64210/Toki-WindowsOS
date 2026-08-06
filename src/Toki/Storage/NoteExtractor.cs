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

        notes.Tldr = Tldr(sentences);
        return notes;
    }

    /// <summary>Title from the notes, heuristically (no separate LLM pass - lesson 9).</summary>
    public static string Title(NoteSet notes, string? app, DateTimeOffset when)
    {
        // Prefer a decision (a topic-y statement) over the TL;DR, which may open
        // with a question; fall back to the TL;DR's first sentence.
        // Prefer an LLM-written title when present (concise + context-aware).
        if (!string.IsNullOrWhiteSpace(notes.Title) && IsValidTitle(notes.Title!))
            return CapWords(notes.Title!, 8);

        var candidate = (notes.Decisions.Count > 0 ? FirstMeaningful(notes.Decisions[0]) : null)
                        ?? FirstSentence(notes.Tldr);
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

    static string? Owner(string sentence, Speaker fallback)
    {
        // "Priya will confirm ..." / "Sid to wire ..." -> the leading proper noun.
        var m = OwnerPattern().Match(sentence);
        if (m.Success) return m.Groups[1].Value;
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
