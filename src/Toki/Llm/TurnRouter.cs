using System.Text.RegularExpressions;
using Toki.Storage;

namespace Toki.Llm;

/// <summary>
/// The RAG-vs-parametric decision: is this turn about the user's MEETINGS (ground it in the notes)
/// or GENERAL (converse from the model's own knowledge)? Retrieval on a query the corpus cannot
/// serve injects irrelevant context that misleads a small model, so retrieve only when the query
/// needs the corpus. Deterministic, ~0ms, two signals in order: meeting-domain cues in the
/// wording, else a retrieval probe (do the content terms hit the store's titles or FTS?).
/// </summary>
public static class TurnRouter
{
    // The user's meeting world: meeting nouns, notes/task vocabulary, and "did we/they discuss…"
    // shapes. Deliberately NOT bare "we"/"they" (that would drag "can we write a poem?" into the
    // notes) - the pronoun only counts next to a discussion verb.
    static readonly Regex MeetingCueRx = new(
        @"\b(meetings?|calls?|stand-?ups?|standups?|syncs?|1[:.]1s?|one-on-ones?|huddles?|retros?|retrospectives?|all-?hands|"
        + @"notes?|transcripts?|recordings?|recorded|agendas?|minutes|action\s+items?|to-?dos?|tasks?|"
        + @"decisions?|takeaways?|attendees?|deadlines?)\b"
        + @"|\b(we|they|everyone|anyone|somebody|someone|the\s+team)\s+(decide|decided|discuss|discussed|say|said|talk|talked|agree|agreed|"
        + @"mention|mentioned|cover|covered|commit|committed|assign|assigned|conclude|concluded|present|presented|raise|raised|review|reviewed)\b"
        + @"|\bwho\s+(said|mentioned|agreed|disagreed|asked|raised|owns?|committed|presented|attended)\b"
        + @"|\b(assigned|delegated)\s+to\b|\bon\s+my\s+plate\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Does the wording itself place this turn in the user's meeting world?</summary>
    public static bool HasMeetingCue(string question) => MeetingCueRx.IsMatch(question ?? "");

    // GENERAL-KNOWLEDGE SHAPE: definitional / how-to / creative / world-fact requests. Parametric by
    // nature; must NOT ground just because a content word prefix-matches a meeting title.
    // "what is A/AN x" is definitional; "what is THE x" stays grounded - the article is load-bearing.
    static readonly Regex GeneralKnowledgeRx = new(
        @"^\s*(what('s|\s+is|\s+are)\s+(a|an)\s+\w+"
        + @"|what\s+(does|do)\s+.+\bmean\b|define\s+\w+|what\s+(does|do)\s+\w+\s+stand\s+for"
        + @"|how\s+(do|can|would|should|to)\s+(i|you|one|we)\b"
        + @"|(write|draft|compose|create|make)\s+(me\s+)?(a|an|some)\s+\w+"
        + @"|give\s+me\s+(some\s+)?(tips|advice|ideas|examples|pointers)\b"
        + @"|explain\s+(how|what|the\s+(concept|idea|difference))\b"
        + @"|what('s|\s+is)\s+the\s+(capital|population|weather|time|date|meaning\s+of)\b"
        + @"|how\s+(many|much)\s+.+\b(in|per)\b)"
        + @"|\bdifference\s+between\b|\btips\s+for\s+(running|writing|doing|being|staying|getting|handling|managing|a\b|an\b)"
        + @"|\bbest\s+(way|practice)s?\s+(to|for)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Public for the router + tests: a clearly definitional/how-to/creative/world-fact ask.</summary>
    public static bool HasGeneralKnowledgeShape(string question) => GeneralKnowledgeRx.IsMatch(question ?? "");

    /// <summary>
    /// True when an LLM-bound turn (LookupFact / Analyze) should be answered as GENERAL conversation
    /// - from the model's own knowledge, with NO retrieval and NO citations. Grounding stays the
    /// default whenever anything ties the turn to the meetings: a meeting cue, a date/recency, a
    /// pinned meeting (screen/follow-up), or a probe hit in the store. An Analyze turn with no
    /// topical anchor at all ("what should I focus on?") also stays grounded - it's corpus-level
    /// *about their work* by nature.
    /// </summary>
    public static bool IsGeneralTurn(QueryPlan plan, MeetingStore store)
    {
        if (plan.Intent is not (QueryIntent.LookupFact or QueryIntent.Analyze)) return false;
        if (plan.HasTemporal) return false;
        if (plan.PinnedMeetingIds is { Count: > 0 }) return false;
        if (HasMeetingCue(plan.Original)) return false;

        // A general-knowledge SHAPE is parametric even when a content word prefix-matches a meeting
        // title - the probe below would otherwise wrongly ground it.
        if (HasGeneralKnowledgeShape(plan.Original)) return true;

        // Topical anchor terms (for Analyze: minus the analytical vocabulary, which describes the
        // analysis wanted, not note text).
        var terms = NotesQa.ProbeTerms(plan);
        if (terms.Count == 0)
            // No content at all: a lookup ("hmm, right?") is chat; a bare analytical ask
            // ("what should I focus on?") is about their work → grounded.
            return plan.Intent == QueryIntent.LookupFact;

        // Retrieval probe: does the user's corpus actually carry evidence for these terms?
        try
        {
            if (store.TitleMatchIds(terms).Count > 0) return false;
            if (store.SearchRelevant(string.Join(" ", terms), 1).Count > 0) return false;
        }
        catch { /* store unavailable → treat as no evidence */ }
        return true;   // nothing in the notes can answer this → converse, don't force retrieval
    }
}
