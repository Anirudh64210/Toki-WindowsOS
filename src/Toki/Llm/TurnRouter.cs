using System.Text.RegularExpressions;
using Toki.Storage;

namespace Toki.Llm;

/// <summary>
/// The RAG-vs-parametric decision of the dialogue brain: is this turn about the user's MEETINGS
/// (ground it in the notes) or a GENERAL question/statement the model should simply converse about
/// from its own knowledge?
///
/// The old pipeline had no such decision — every utterance that survived the small-talk/persona
/// filters was forced through notes retrieval, so "what is my name", chit-chat, world-knowledge
/// questions and misheard commands all became document searches that either denied ("not in your
/// notes") or, worse, fabricated a link to an unrelated meeting. That forced-retrieval design is a
/// known failure mode: retrieval on a query the corpus can't serve injects irrelevant context that
/// small models are the most easily misled by (Adaptive-RAG / Self-RAG: classify first, retrieve
/// only when the query needs the corpus; the Google "sufficient context" line: RAG makes models
/// answer instead of abstain).
///
/// The decision here is deterministic and ~0ms, two signals in order:
///   1. MEETING-DOMAIN CUES in the wording ("meeting", "standup", "action items", "did we decide",
///      "who said", …) → it's about the notes.
///   2. Otherwise a RETRIEVAL PROBE: do the question's content terms actually hit anything in the
///      store (title or FTS)? A hit → the user's own corpus has evidence → ground it. No hit → the
///      notes cannot answer it, so retrieval could only mislead → let the model converse.
/// (A judgment-call LLM classifier would also work here — Adaptive-RAG trains one — but at Toki's
/// scale the cue set + probe covers the same decision with zero latency and full testability.)
/// </summary>
public static class TurnRouter
{
    // The user's meeting world: meeting nouns, notes/task vocabulary, and "did we/they discuss…"
    // shapes. Deliberately NOT bare "we"/"they" (that would drag "can we write a poem?" into the
    // notes) — the pronoun only counts next to a discussion verb.
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

    /// <summary>
    /// True when an LLM-bound turn (LookupFact / Analyze) should be answered as GENERAL conversation
    /// — from the model's own knowledge, with NO retrieval and NO citations. Grounding stays the
    /// default whenever anything ties the turn to the meetings: a meeting cue, a date/recency, a
    /// pinned meeting (screen/follow-up), or a probe hit in the store. An Analyze turn with no
    /// topical anchor at all ("what should I focus on?") also stays grounded — it's corpus-level
    /// *about their work* by nature.
    /// </summary>
    public static bool IsGeneralTurn(QueryPlan plan, MeetingStore store)
    {
        if (plan.Intent is not (QueryIntent.LookupFact or QueryIntent.Analyze)) return false;
        if (plan.HasTemporal) return false;
        if (plan.PinnedMeetingIds is { Count: > 0 }) return false;
        if (HasMeetingCue(plan.Original)) return false;

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
