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

    // A floor to-do has to be SHAPED like a task, not merely CONTAIN a task word. The old rule
    // accepted any sentence with an action word anywhere in it, so a rambling declarative
    // ("I optimized the rendering so typing feels smoother and ...") became a to-do and the brevity
    // pass then truncated it into a meaningless fragment. A statement of fact is not a task: a floor
    // to-do must be a first/second/third-person commitment, an explicit task marker, or an
    // imperative opener.
    static readonly Regex CommitmentShape = new(
        @"\b(?:i'?ll|we'?ll|you'?ll|he'?ll|she'?ll|they'?ll|will|need(?:s)?\s+to" +
        @"|have\s+to|has\s+to|going\s+to|make\s+sure|follow(?:[-\s])?up|action\s+item|to-?do)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex ImperativeOpener = new(
        @"^\s*(?:send|add|fix|review|create|schedule|confirm|set\s+up|ping|wire|reach\s+out|update" +
        @"|prepare|collect|check|draft|share|email|call|book|finalis[ez]e|write|build|test|deploy" +
        @"|assign|investigate|remove|migrate|document)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static bool LooksLikeAction(string sentence)
        => CommitmentShape.IsMatch(sentence) || ImperativeOpener.IsMatch(sentence);

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
            else if (LooksLikeAction(s))
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

        // NO raw-transcript summary. The floor used to concatenate the opening sentences into a
        // "TL;DR", which is just the conversation's first ~140 characters pasted in as if it were a
        // written summary - the leak the owner kept seeing whenever the model was unavailable. Empty
        // is honest: the renderer drops an empty summary, exactly as the model path already does when
        // it has no real summary to give. The floor's value is the extracted decisions/actions/
        // questions, which are checkable against the transcript; a fabricated summary is not.
        notes.Tldr = "";
        // The floor lifts its lines straight out of speech, so it needs the tightening pass even
        // more than the model does. One pass, both paths - see Llm/Brevity.cs.
        return Toki.Llm.Brevity.Tighten(notes);
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
