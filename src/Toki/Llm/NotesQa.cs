using System.Text;
using System.Text.RegularExpressions;
using Toki.Storage;

namespace Toki.Llm;

/// <summary>
/// "Ask your notes": answers a natural-language question using ONLY the user's saved meetings.
/// A fully-local retrieval step (FTS5 BM25 over the meetings, with a keyword/recent fallback) gathers
/// the most relevant notes, then the active model (on-device Qwen or the BYO API key) answers from
/// that context and cites the meetings it used. Never invents — if the notes don't cover it, it says so.
///
/// Two fast paths: a task-query ROUTER answers "what are my open tasks / what did I commit to" straight
/// from the action_items table (instant, no model, no hallucination); everything else STREAMS tokens
/// from the model so words appear live.
///
/// Heavy (loads/runs the model): callers MUST invoke this off the UI thread (Task.Run).
/// </summary>
public static class NotesQa
{
    public sealed record Source(long Id, string Title);
    public sealed record Answer(string Text, IReadOnlyList<Source> Sources, bool Ok);

    /// <summary>Test seam: lets tests run the deterministic routes against a temp-file store instead of
    /// the user's real %APPDATA% database. Null in the running app.</summary>
    public static MeetingStore? StoreOverride;
    static MeetingStore Store => StoreOverride ?? App.Store;

    /// <summary>Test seam: forces the model the LLM routes use, so the full analyze/lookup pipeline
    /// (retrieval breadth, prompt shape, token budget) is testable with a fake — without loading the
    /// real model or touching the user's settings. Null in the running app.</summary>
    public static ILlm? LlmOverride;

    // Persona + a one-shot exemplar: small instruct models copy the SHAPE of an example far more
    // reliably than they follow abstract style rules, so both prompts carry one Q→A pair in the target
    // voice ("sharp colleague who was in the room": lead with the answer, name the meeting in-sentence,
    // contractions, no preamble).
    const string System =
        "You are Toki, the user's personal meeting assistant. Answer questions about their OWN meetings using ONLY "
        + "the meeting notes provided. Talk like a sharp, friendly colleague who was in the room: plain words, "
        + "contractions, direct. Lead with the answer — no preamble, never restate the question. Name the meeting "
        + "in the sentence when it helps. If the notes don't contain the answer, say so in one short sentence — "
        + "never guess or make anything up, and never stretch loosely related notes into an answer. If the "
        + "question isn't about their meetings at all, say that's outside what you can see — don't force one. "
        + "Never write \"Based on the notes\", \"Great question\", or \"As an AI\". "
        + "Respond in English only.\n"
        + "Example:\n"
        + "Q: What did we decide about pricing?\n"
        + "A: You settled on the one-time $20 price — no subscription. That was in Tuesday's pricing sync.";

    // The NOTCH voice: a one-glance answer, "sharp colleague who was in the room" (from the assistant-style
    // research). Numbered-checklist form (small Qwen models follow this best), shape not word-counts, with an
    // explicit ban-list — enforcement of length is ALSO done in code (NotchTrim), never trusted to the model.
    const string SystemConcise =
        "You are Toki, a personal meeting assistant. Answer from the user's meeting notes shown as CONTEXT. "
        + "Your answer appears in a tiny panel the user reads in one glance.\n"
        + "1. Answer in 1 to 3 short sentences. Never more than 3.\n"
        + "2. First sentence is the direct answer. No preamble, no restating the question.\n"
        + "3. Only use bullets if the question asks for multiple items; then at most 4, each under 8 words.\n"
        + "4. Talk like a helpful colleague: use \"you\", names from the meetings, and contractions. Plain text only — no headings, bold, or emojis.\n"
        + "5. Name the source meeting in a few words inside a sentence (like \"in Tuesday's standup\"), never as a citation list.\n"
        + "6. If the notes don't contain the answer, say so in one sentence and name the closest meeting if there is one. Never guess, and never stretch loosely related notes into an answer. If the question isn't about the meetings at all, say that's outside what you can see.\n"
        + "7. Never write \"Great question\", \"Based on the context\", \"As an AI\", or \"Let me know if\". Never ask a question back after answering. Never apologize.\n"
        + "8. If a real answer needs more than 3 sentences, give only the single most important sentence and end with [MORE].\n"
        + "9. Respond in English only.\n"
        + "Example:\n"
        + "Q: What did we decide about pricing?\n"
        + "A: You settled on the one-time $20 price — no subscription. That was in Tuesday's pricing sync.";

    // A question is "generic" (no specific meeting/topic named) if EVERY content word is an ask-word —
    // "what were the action items?", "who disagreed?", "summarize this". Such questions, asked while a
    // note is open, are about THAT note. A named topic ("the marketing meeting") has a non-generic word
    // and so goes to global search instead.
    static readonly HashSet<string> GenericAskWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "action","items","item","task","tasks","todo","todos","to-do","to-dos","decision","decisions","decided","decide",
        "summary","summarize","summarise","recap","overview","key","points","point","takeaway","takeaways","note","notes",
        "who","what","whats","when","said","say","says","agree","agreed","disagree","disagreed","discussed","discuss",
        "talked","happened","happen","going","on","the","a","an","my","our","were","was","are","is","did","do","does",
        "about","in","of","for","this","that","these","those","it","here","meeting","meetings","call","calls","chat","standup","sync",
        // Scope-chrome that carries no content ("the meeting REGARDING Amazon" → search "amazon",
        // not "regarding" — a generic word in the prefix-OR query drowns the real term in BM25 noise).
        "regarding","concerning","involving","mentioning","covering","around",
    };
    static bool OnlyGenericAsk(string residual)
    {
        var words = (residual ?? "").Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        foreach (var w in words)
            if (!GenericAskWords.Contains(w.Trim('?', '.', ',', '!', ';', ':', '\'', '"'))) return false;
        return true;
    }

    // PROXIMAL deixis = "the thing I'm looking at NOW" → the on-screen note wins (even over conversation
    // memory). NOTE the split from anaphora: "that/those/it/them" point at the PREVIOUS turn, not the
    // screen — those stay with the follow-up resolver. Only proximal words ground on the screen.
    static readonly Regex ProximalRx = new(
        @"\b(this|these|here|current(ly)?|on( my)? screen|the open|opened?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Trim an answer to a one-glance notch teaser. Returns (text, tooLong): if the model flagged
    /// [MORE], or the answer overflows the glance budget (~280 chars), cut to the last full sentence and flag
    /// it so the caller can offer "full answer in the Library". A complete short sentence reads intentional;
    /// a chopped one reads broken — so we cut on a sentence boundary, not mid-word.</summary>
    public static (string Text, bool TooLong) NotchTrim(string answer)
    {
        var t = (answer ?? "").Trim();
        var more = t.Contains("[MORE]");
        t = t.Replace("[MORE]", "").Trim();
        if (!more && t.Length <= 300) return (t, false);
        if (t.Length > 280)
        {
            var cut = t.LastIndexOfAny(new[] { '.', '!', '?' }, Math.Min(279, t.Length - 1));
            t = cut > 40 ? t[..(cut + 1)] : t[..280].TrimEnd() + "…";
        }
        return (t.Trim(), true);
    }

    /// <summary>Non-streaming convenience wrapper (accumulates the stream).</summary>
    public static Task<Answer> AskAsync(string question, CancellationToken ct = default)
        => AskStreamAsync(question, _ => { }, _ => { }, ct);

    /// <summary>Non-streaming, with conversation context (for follow-ups / tests).</summary>
    public static Task<Answer> AskAsync(string question, ConversationContext ctx, CancellationToken ct = default)
        => AskStreamAsync(question, _ => { }, _ => { }, ct, ctx);

    /// <summary>Answer a question, calling <paramref name="onSources"/> once the source meetings are known
    /// and <paramref name="onToken"/> for each piece of the answer as it streams. Returns the final answer.
    /// Callbacks fire on a background thread — the UI must marshal them to the dispatcher.</summary>
    public static async Task<Answer> AskStreamAsync(
        string question, Action<IReadOnlyList<Source>> onSources, Action<string> onToken,
        CancellationToken ct = default, ConversationContext? ctx = null,
        bool concise = false, long? screenMeetingId = null)
    {
        question = question?.Trim() ?? "";
        if (question.Length == 0) return new Answer("Ask me something about your meetings.", [], true);

        // Every answered turn is recorded into the conversation transcript (the brain's working
        // memory), so multi-turn chat works on every path.
        Answer Canned(string text)
        {
            onSources([]); onToken(text);
            ctx?.RecordTurn(question, text);
            return new Answer(text, [], true);
        }

        // 0.4) PENDING CALENDAR CREATE: if we just asked the user to confirm a create (or asked "when?"),
        //      THIS turn belongs to that — resolved before anything else so "yes"/"okay" can't be eaten by
        //      small-talk. Single-shot: the pending is cleared up front, so a stray later "yes" never
        //      creates, and a create never happens without an explicit confirmation.
        if (ctx?.PendingCreate is { } pending)
        {
            ctx.PendingCreate = null;
            if (pending.Start is null)
            {
                var t = Toki.Calendar.QuickEventParser.Parse(question, DateTimeOffset.Now);
                if (t.Start is not null)
                {
                    var filled = pending with { Start = t.Start, End = t.End };
                    ctx.PendingCreate = filled;
                    return Canned(CalendarCommand.ConfirmPrompt(filled));
                }
                // no time given — the draft dies; fall through and answer this turn normally
            }
            else if (CalendarCommand.IsYes(question))
                return Canned(await CalendarCommand.ExecuteAsync(pending, ct).ConfigureAwait(false));
            else if (CalendarCommand.IsNo(question))
                return Canned(CalendarCommand.Cancelled());
            // anything else → pending already cleared, fall through to normal routing
        }

        // 0.5) SMALL-TALK: greetings / thanks / "who are you" get a short canned reply — never a notes
        //      search (so "Good morning" can't return a rambling meeting summary).
        if (SmallTalk.TryReply(question) is string chit) return Canned(chit);

        // 0.6) PERSONA/META: questions about Toki ITSELF ("why are you so rude?", "do you see my
        //      screen?") get a warm in-character reply — never retrieval (searching the notes for
        //      "rudeness" was a real, screenshot-verified failure). Screen-aware: it can name the note
        //      that's open right now.
        var screenTitle = screenMeetingId is long stid ? Store.Get(stid)?.Title : null;
        if (Persona.TryReply(question, screenTitle) is string meta) return Canned(meta);

        // 0.7) USER FACTS + IDENTITY: "call me Anirudh" is a fact to remember and acknowledge (stored
        //      via the normal settings path, so the greeting uses it too); "what is my name" is a
        //      profile read. NEITHER is a notes search — the old pipeline searched the meeting notes
        //      for the user's name and cited three meetings (real screenshot).
        if (UserFacts.TryCapture(question, ctx) is string ack) return Canned(ack);
        if (UserFacts.TryIdentity(question) is string who) return Canned(who);

        // 0.75) VOICE CREATE: "schedule a call with Ana tomorrow at 3" is a command to CREATE an event.
        //       Detected deterministically, then CONFIRMED before anything is booked (a mishearing must
        //       never silently create an event). Before the misheard-command clarifier (so it owns its
        //       scheduling verbs) and before any retrieval (a create is never "answered" from the notes).
        if (ctx is not null && CalendarCommand.TryDetect(question, DateTimeOffset.Now) is { } draft)
        {
            if (!(CalendarCommand.ConnectedOverride ?? Toki.Calendar.CalendarService.Instance.IsConnected))
                return Canned(CalendarCommand.NotConnected());
            ctx.PendingCreate = draft;
            return Canned(draft.Start is null ? CalendarCommand.WhenPrompt(draft) : CalendarCommand.ConfirmPrompt(draft));
        }

        // 0.8) MISHEARD COMMAND: a command-shaped utterance that matched no real command ("switch to
        //      LIKE theme" — ASR for "light theme") degrades to a targeted clarification, never to
        //      retrieval (which fabricated an answer from an unrelated meeting — real screenshot).
        if (CommandRouter.TryNearMiss(question) is string clarify) return Canned(clarify);

        // 1) UNDERSTAND FIRST. Precedence (from how Apple/Cluely/Dia/Granola resolve "this"):
        //    named topic/date/recency  >  PROXIMAL deixis + on-screen note  >  conversation anaphora  >
        //    underspecified + on-screen note  >  global.
        var now = DateTimeOffset.Now;
        var parsed = QueryUnderstanding.Parse(question, now);
        var qaIntent = parsed.Intent is QueryIntent.SummarizeOne or QueryIntent.LookupFact or QueryIntent.ListTasks or QueryIntent.Analyze;

        QueryPlan plan;
        if (screenMeetingId is long psid && qaIntent && ProximalRx.IsMatch(question)
            && !parsed.HasDateRange && parsed.Rank is null)
        {
            // "what are the action items in THIS meeting?" while viewing note B → B, NOT the A you were
            // just discussing. Proximal deixis points at the screen — skip the follow-up resolver entirely.
            plan = parsed with { PinnedMeetingIds = new List<long> { psid } };
        }
        else
        {
            plan = (ctx is not null ? QueryUnderstanding.ResolveFollowUp(question, ctx, now) : null) ?? parsed;
            // Underspecified question ("what were the action items?") with a note open and no anaphor/pin →
            // ground on the on-screen note (Granola's open-note scope).
            if (screenMeetingId is long sid && plan.Intent is QueryIntent.SummarizeOne or QueryIntent.LookupFact or QueryIntent.ListTasks or QueryIntent.Analyze
                && !plan.HasDateRange && plan.Rank is null
                && (plan.PinnedMeetingIds is null || plan.PinnedMeetingIds.Count == 0)
                && (ThinResidual(plan.Residual) || OnlyGenericAsk(plan.Residual)))
            {
                plan = plan with { PinnedMeetingIds = new List<long> { sid } };
            }
        }

        // 2) Deterministic routes — answered straight from the DB, exact, no model, no hallucination.
        Answer? det = plan.Intent switch
        {
            QueryIntent.Count => CountAnswer(plan),
            QueryIntent.ListMeetings => ListMeetingsAnswer(plan),
            QueryIntent.ListTasks => TaskAnswer(plan),
            _ => null,
        };
        // "WHICH meeting was it?" questions are identification, not synthesis — answered from the DB:
        // oldest/most-recent (+title), an ambiguous date that hits SEVERAL meetings (never silently
        // pick one), and a topic that matches a meeting TITLE ("the meeting regarding Amazon").
        det ??= IdentifyMeetingAnswer(plan);
        if (det is not null)
        {
            Remember(ctx, plan, det.Sources); onSources(det.Sources); onToken(det.Text);
            ctx?.RecordTurn(question, det.Text);
            return det;
        }

        // 2.5) RAG-vs-PARAMETRIC routing: is this LLM-bound turn actually about the meetings?
        //      A turn with no meeting cue, no date, no pin, and no evidence in the store ("what's a
        //      mutex?", "I'm stressed about tomorrow") is GENERAL conversation — the model answers
        //      from its own knowledge, with the conversation as context and NO retrieval/citations.
        //      Forcing such turns through notes search is what made every answer a deny-or-fabricate.
        if (TurnRouter.IsGeneralTurn(plan, Store))
            return await GeneralAnswerAsync(question, ctx, onSources, onToken, concise, ct).ConfigureAwait(false);

        // 3) LLM routes (summary / factual lookup) — resolve the target meetings deterministically FIRST,
        //    so "most recent"/"on July 30" is decided by SQL and the model can't pick the wrong meeting.
        //    Resolution runs BEFORE the model check: an honest "couldn't find it" needs no model at all.
        var hits = ResolveForLlm(plan);
        if (hits.Count == 0)
        {
            var miss = plan.HasDateRange
                ? $"I couldn't find a meeting {DescribeRange(plan)}."
                : "I couldn't find anything about that in your meetings yet.";
            onSources([]); onToken(miss);
            ctx?.RecordTurn(question, miss);
            return new Answer(miss, [], true);
        }

        var llm = LlmOverride ?? LlmService.Instance.Active();
        if (llm is null || !llm.Ready)
            return new Answer("Add the on-device model or your API key in Settings, and I can answer questions about your meetings.", [], false);

        var sources = hits.Select(h => new Source(h.Id, h.Title)).ToList();
        Remember(ctx, plan, sources);
        onSources(sources);   // citations up-front, before the answer streams in

        var prompt = plan.Intent switch
        {
            QueryIntent.SummarizeOne => BuildSummarizePrompt(hits[0]),
            QueryIntent.Analyze => BuildAnalyzePrompt(question, hits),
            _ => BuildPrompt(question, hits),
        };
        // Notch = one-glance concise (short system prompt + tight token budget); Library = fuller answers.
        // The model knows WHO it's talking to (the profile name) — so "was I mentioned" and
        // name-addressed phrasing resolve naturally.
        var sys = concise ? SystemConcise : System;
        var uname = UserProfile.DisplayName;
        if (!string.IsNullOrEmpty(uname)) sys += $"\nThe user's name is {uname}.";
        // Per-intent budgets: a factual LOOKUP is 1-2 sentences; a SUMMARY earns more; ANALYSIS earns
        // the most (a conclusion + supporting points across meetings can't fit in a lookup budget —
        // that budget is exactly what made every analytical answer shallow). Code still caps length —
        // the model is never trusted with it (small models ramble past any prompt).
        var maxTok = plan.Intent switch
        {
            QueryIntent.SummarizeOne => concise ? 150 : 340,
            QueryIntent.Analyze => concise ? 220 : 420,
            _ => concise ? 85 : 170,
        };
        // A one-glance notch LOOKUP stops cleanly at the first blank line ("\n\n") instead of running
        // past the budget and being trimmed — the model ends where the answer ends. Summaries and
        // analyses may legitimately contain a blank line before their bullets, so they don't get the stop.
        var stops = concise && plan.Intent == QueryIntent.LookupFact ? new[] { "\n\n" } : null;
        var sb = new StringBuilder();
        try
        {
            await foreach (var tok in llm.StreamAsync(sys, prompt, maxTok, ct, stops).ConfigureAwait(false))
            {
                sb.Append(tok);
                onToken(tok);
            }
        }
        catch (OperationCanceledException) { return new Answer(sb.ToString().Trim(), sources, false); }
        catch (Exception ex)
        {
            App.Log($"notes QA failed: {ex.Message}");
            return new Answer(sb.Length > 0 ? sb.ToString().Trim() : "Something went wrong answering that — try again.", sources, false);
        }

        var raw = sb.ToString().Trim();
        if (raw.Length == 0 || TextValidator.IsDegenerate(raw))
            return new Answer("I couldn't produce a clean answer — try rephrasing the question.", sources, true);
        ctx?.RecordTurn(question, raw);
        // NO CITATIONS ON A NON-ANSWER: if the model concluded the notes don't cover it, the source
        // chips would decorate a miss with fake authority ("I don't have your name in the meeting
        // notes — From <3 meetings>", real screenshot). A miss ships with no sources.
        if (LooksLikeMiss(raw)) { onSources([]); return new Answer(raw, [], true); }
        return new Answer(raw, sources, true);
    }

    // A model answer that DECLINES ("couldn't find…", "the notes don't mention…") is a miss, not an
    // answer — detected so its citations can be dropped (calibrated abstention: an honest miss must
    // not look like a sourced answer).
    static readonly Regex MissRx = new(
        @"couldn'?t\s+find|could\s+not\s+find|didn'?t\s+find|don'?t\s+have|doesn'?t\s+(mention|contain|cover|include)|"
        + @"don'?t\s+(mention|contain|cover|include|see)|no\s+mention|not\s+(mentioned|covered|in\s+the\s+notes|in\s+your\s+(notes|meetings))|"
        + @"notes\s+don'?t|isn'?t\s+(mentioned|covered|in\s+the\s+notes)|outside\s+what\s+i\s+can\s+see|nothing\s+in\s+(the|your)\s+notes",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Does this LLM answer's LEAD declare a miss? (Checked on the first sentence-ish span —
    /// a real answer that later adds "the notes don't cover X" keeps its citations.)</summary>
    public static bool LooksLikeMiss(string answer)
    {
        var t = (answer ?? "").TrimStart();
        var head = t.Length > 160 ? t[..160] : t;
        var firstStop = head.IndexOfAny(new[] { '.', '!', '?' });
        if (firstStop > 0) head = head[..(firstStop + 1)];
        return MissRx.IsMatch(head);
    }

    /// <summary>The GENERAL conversation branch: no retrieval, no citations — the model converses
    /// and reasons from its own knowledge, with the recent turns + profile in context. Works over
    /// either backend (local Qwen / BYO API).</summary>
    static async Task<Answer> GeneralAnswerAsync(
        string question, ConversationContext? ctx,
        Action<IReadOnlyList<Source>> onSources, Action<string> onToken,
        bool concise, CancellationToken ct)
    {
        var llm = LlmOverride ?? LlmService.Instance.Active();
        if (llm is null || !llm.Ready)
        {
            // Honest, deterministic floor: without a model there is no general chat — but the notes
            // Q&A still works, so say exactly that.
            var floor = "I can't chat freely without a model — add the on-device model or your API key in Settings. I can still answer about your meetings.";
            onSources([]); onToken(floor);
            ctx?.RecordTurn(question, floor);
            return new Answer(floor, [], true);
        }

        onSources([]);   // never citations on a general turn
        var sys = GeneralChat.BuildSystem(concise);
        var user = GeneralChat.BuildUser(question, ctx);
        var stops = concise ? new[] { "\n\n" } : null;
        var sb = new StringBuilder();
        try
        {
            await foreach (var tok in llm.StreamAsync(sys, user, GeneralChat.MaxTokens(concise), ct, stops).ConfigureAwait(false))
            {
                sb.Append(tok);
                onToken(tok);
            }
        }
        catch (OperationCanceledException) { var part = sb.ToString().Trim(); ctx?.RecordTurn(question, part); return new Answer(part, [], false); }
        catch (Exception ex)
        {
            App.Log($"general chat failed: {ex.Message}");
            return new Answer(sb.Length > 0 ? sb.ToString().Trim() : "Something went wrong answering that — try again.", [], false);
        }
        var raw = sb.ToString().Trim();
        if (raw.Length == 0 || TextValidator.IsDegenerate(raw))
            return new Answer("I couldn't produce a clean answer — try rephrasing that.", [], true);
        ctx?.RecordTurn(question, raw);
        return new Answer(raw, [], true);
    }

    // Carry this turn's resolved state forward so the NEXT turn's follow-up ("before that", "its tasks") works.
    static void Remember(ConversationContext? ctx, QueryPlan plan, IReadOnlyList<Source> sources)
    {
        if (ctx is null) return;
        ctx.LastIntent = plan.Intent;
        if (sources.Count > 0) ctx.LastMeetingIds = sources.Select(s => s.Id).ToList();
        if (plan.HasDateRange) { ctx.LastRangeStart = plan.RangeStart; ctx.LastRangeEnd = plan.RangeEnd; }
        else if (plan.Rank is not null) { ctx.LastRangeStart = null; ctx.LastRangeEnd = null; }
    }

    // Resolve which meeting(s) the LLM should read, deterministically, from the plan. An ANALYZE plan
    // gets BREADTH (up to 6 meetings, not 3): synthesis questions ("themes", "progress", "compare")
    // structurally need to read across meetings — narrow top-k is exactly why they used to fail (the
    // GraphRAG local-vs-global finding: corpus-level questions can't be answered from a lookup's slice).
    static List<MeetingRecord> ResolveForLlm(QueryPlan plan)
    {
        var store = Store;
        var broad = plan.Intent == QueryIntent.Analyze;

        // Follow-up pinned the meeting(s) from the previous turn — use them directly.
        if (plan.PinnedMeetingIds is { Count: > 0 } pinned)
            return pinned.Select(id => store.Get(id)).OfType<MeetingRecord>().ToList();

        List<long> ids;

        if (plan.Rank == RecencyDir.Latest || plan.Rank == RecencyDir.Oldest)
        {
            // "most recent / oldest" (optionally about a topic) → pick by DATE, not relevance.
            // Title matches join the topical pool (title hits must never be lost — see Retrieve).
            var pool = ThinResidual(plan.Residual)
                ? null
                : store.TitleMatchIds(TitleTerms(plan.Residual))
                    .Concat(store.SearchRelevant(ContentQuery(plan.Residual), 12)).Distinct().ToList();
            var ranked = plan.Rank == RecencyDir.Latest ? store.LatestMeetingIds(200) : store.OldestMeetingIds(200);
            ids = (pool is null ? ranked : ranked.Where(pool.Contains)).Take(Math.Max(1, plan.RankN)).ToList();
            if (ids.Count == 0) ids = ranked.Take(1).ToList();   // topic had no match → fall back to pure recency
        }
        else if (plan.HasDateRange)
        {
            var take = broad ? 6 : 3;
            var inRange = store.MeetingIdsInRange(plan.RangeStart!.Value, plan.RangeEnd!.Value);
            if (ThinResidual(plan.Residual)) ids = inRange.Take(take).ToList();
            else
            {
                var matched = store.TitleMatchIds(TitleTerms(plan.Residual))
                    .Concat(store.SearchRelevant(ContentQuery(plan.Residual), 12))
                    .Distinct().Where(inRange.Contains).Take(take).ToList();
                ids = matched.Count > 0 ? matched : inRange.Take(take).ToList();   // topic within the date window, else the window
            }
            // An analytical question over a date window with nothing IN the window ("what should I
            // focus on this week?" on a quiet week) falls back to the recent meetings — the honest
            // "your notes don't cover this week" lives in the grounded answer, not a dead end.
            if (broad && ids.Count == 0) ids = store.LatestMeetingIds(5);
        }
        else if (plan.Intent == QueryIntent.SummarizeOne)
        {
            var src = string.IsNullOrWhiteSpace(plan.Residual) ? plan.Original : plan.Residual;
            // A summarize-by-name ("summarize the amazon meeting") resolves against TITLES first.
            ids = store.TitleMatchIds(TitleTerms(src))
                .Concat(store.SearchRelevant(ContentQuery(src), 1))
                .Distinct().Take(1).ToList();
        }
        else if (broad)
        {
            return RetrieveBroad(plan);      // ANALYZE with no temporal → multi-meeting pool
        }
        else
        {
            return Retrieve(plan.Original);   // LOOKUP_FACT with no temporal → existing FTS+fallback top-3
        }

        return ids.Select(id => store.Get(id)).OfType<MeetingRecord>().ToList();
    }

    // Words that describe the ANALYSIS the user wants, not text that appears in notes — a note almost
    // never literally contains "theme" or "blocker". Stripped before retrieval so "themes across my SLO
    // meetings" searches for "slo", not "themes".
    static readonly HashSet<string> AnalyticalVocab = new(StringComparer.OrdinalIgnoreCase)
    {
        "theme","themes","pattern","patterns","trend","trends","insight","insights","risk","risks",
        "blocker","blockers","concern","concerns","worried","worries","worry","worrying","focus","focused",
        "priority","priorities","prioritize","prioritise","prioritized","prioritised","progress","progressing",
        "progressed","compare","comparison","versus","difference","unresolved","undecided","biggest",
        "important","main","overall","advice","recommend","recommendation","recommendations","suggest",
        "suggestion","suggestions","analysis","analyze","analyse","think","thoughts","take","read",
        "opinion","missing","going","should","across","between",
    };

    /// <summary>Multi-meeting retrieval for the ANALYZE route: topical anchor words (content terms minus
    /// the analytical vocabulary) pick up to 6 relevant meetings; with no anchor — or no match — the
    /// recent 5 are analysed instead. An analytical question is corpus-level by nature, so it never
    /// dead-ends deterministically: the PROMPT carries the honesty ("say what the notes don't cover"),
    /// which is the calibrated-abstention shape — partial grounded answer plus an explicit gap, not a
    /// binary "found/not found".</summary>
    static List<MeetingRecord> RetrieveBroad(QueryPlan plan)
    {
        var store = Store;
        var terms = ProbeTerms(plan);
        if (terms.Count > 0)
        {
            // Anchored analysis ("how is the SLO work progressing?") → the matching meetings, title
            // hits first. RELEVANCE FLOOR: an anchor that matches NOTHING returns empty → the honest
            // miss — never "analyse 5 random recent meetings" (that fallback is what let a garbled
            // command be "answered" from an unrelated meeting: retrieval with zero shared content is
            // fabrication fuel, the P9.1-flagged floor).
            return store.TitleMatchIds(terms)
                .Concat(store.SearchRelevant(string.Join(" ", terms), 6))
                .Distinct().Take(6)
                .Select(store.Get).OfType<MeetingRecord>().ToList();
        }
        // No topical anchor at all ("what should I focus on?") → corpus-level by nature: the recent
        // 5, with the prompt carrying the honesty about what the notes don't cover.
        return store.LatestMeetingIds(5).Select(store.Get).OfType<MeetingRecord>().ToList();
    }

    /// <summary>The topical anchor terms of a plan: content words minus stop/ask words — and minus
    /// the analytical vocabulary for an Analyze turn ("themes"/"risks" describe the analysis wanted,
    /// not note text). Used by retrieval AND by the router's evidence probe.</summary>
    public static List<string> ProbeTerms(QueryPlan plan)
    {
        var src = string.IsNullOrWhiteSpace(plan.Residual) ? plan.Original : plan.Residual;
        var analytic = plan.Intent == QueryIntent.Analyze;
        return (src ?? "")
            .Split(new[] { ' ', '\t', '?', '.', ',', '!', ';', ':', '\'', '"', '(', ')', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !Stop.Contains(w) && !GenericAskWords.Contains(w)
                        && (!analytic || !AnalyticalVocab.Contains(w)))
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    static bool ThinResidual(string residual) =>
        residual.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(w => w.Length > 2 && !Stop.Contains(w)) == 0;

    // ── deterministic count / list-meetings answers ──
    static Answer CountAnswer(QueryPlan plan)
    {
        var ids = plan.HasDateRange ? Store.MeetingIdsInRange(plan.RangeStart!.Value, plan.RangeEnd!.Value) : Store.LatestMeetingIds(100000);
        var where = plan.HasDateRange ? $" {DescribeRange(plan)}" : "";
        return new Answer($"You have {ids.Count} meeting{(ids.Count == 1 ? "" : "s")}{where}.", [], true);
    }

    static Answer ListMeetingsAnswer(QueryPlan plan)
    {
        var ids = plan.HasDateRange
            ? Store.MeetingIdsInRange(plan.RangeStart!.Value, plan.RangeEnd!.Value)
            : Store.LatestMeetingIds(10);
        var meetings = ids.Select(id => Store.Get(id)).OfType<MeetingRecord>().ToList();
        if (meetings.Count == 0) return new Answer($"You had no meetings {DescribeRange(plan)}.", [], true);

        var sb = new StringBuilder(plan.HasDateRange
            ? $"You had {meetings.Count} meeting{(meetings.Count == 1 ? "" : "s")} {DescribeRange(plan)}:\n"
            : $"Your {meetings.Count} most recent meeting{(meetings.Count == 1 ? "" : "s")}:\n");
        foreach (var m in meetings)
            sb.Append("• ").Append(m.Title).Append("   —   ").Append(m.StartedAt.ToString("ddd, MMM d")).Append('\n');
        return new Answer(sb.ToString().TrimEnd(), meetings.Select(m => new Source(m.Id, m.Title)).ToList(), true);
    }

    static string DescribeRange(QueryPlan plan)
    {
        if (!plan.HasDateRange) return "";
        var s = plan.RangeStart!.Value; var e = plan.RangeEnd!.Value;
        if ((e - s).TotalDays <= 1) return $"on {s:MMM d, yyyy}";
        return $"between {s:MMM d} and {e.AddDays(-1):MMM d, yyyy}";
    }

    // ── deterministic "WHICH meeting?" identification ──
    // The live run showed three failures of the same class: "what was the OLDEST meeting I recorded?
    // what was the title?" (LLM answered "the notes don't mention recorded meeting dates" — the DB
    // knows this exactly), "what was the FIRST meeting I had?" (right meeting, but led with raw task
    // text and buried the name), and "what was the meeting on July 15th?" when FOUR meetings happened
    // that day (silently summarized one). Identification is a DB fact — answer it from the DB.

    // Words a pure "which/oldest/most recent meeting (what was its title?)" ask can be made of —
    // if the residual holds ONLY these, the user wants the meeting IDENTIFIED, not analysed.
    static readonly HashSet<string> IdentifyChrome = new(StringComparer.OrdinalIgnoreCase)
    {
        "what","was","were","is","the","a","an","that","this","it","its","i","we","my","our","me","one",
        "meeting","meetings","call","calls","note","notes","conversation","chat","and","had","have","did","do",
        "title","titles","name","names","named","called","titled","recorded","record","recording","recordings",
        "when","which","of","in","on","ever","you","there",
    };
    static bool IdentifyThin(string residual)
    {
        foreach (var w in (residual ?? "").Split(new[] { ' ', '?', '.', ',', '!', ';', ':', '\'', '"' }, StringSplitOptions.RemoveEmptyEntries))
            if (w.Length > 1 && !IdentifyChrome.Contains(w)) return false;
        return true;
    }

    // "what/which (was) the meeting … about/regarding <topic>" — an identification question. The
    // lead must BE "what/which … meeting" ("what did we SAY in the meeting about X" asks for content,
    // not identification, and must not fire this).
    static readonly Regex WhichMeetingRx = new(
        @"^\s*(what|which)('s|\s+was|\s+is|\s+were)?\s+(the\s+|my\s+|that\s+|a\s+)?(meetings?|calls?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static Answer? IdentifyMeetingAnswer(QueryPlan plan)
    {
        if (plan.Intent is not (QueryIntent.SummarizeOne or QueryIntent.LookupFact)) return null;
        if (plan.PinnedMeetingIds is { Count: > 0 }) return null;   // a follow-up/screen pin owns the scope
        var store = Store;

        // 1) Recency rank ("oldest / first / most recent meeting", optionally "+ its title") with no
        //    real topic → the meeting is a SQL fact. Same shape for Latest and Oldest — the bug was
        //    that only Latest happened to survive the LLM round-trip.
        if (plan.Rank is { } rank && IdentifyThin(plan.Residual))
        {
            var ids = (rank == RecencyDir.Latest ? store.LatestMeetingIds(plan.RankN) : store.OldestMeetingIds(plan.RankN));
            var m = ids.Count >= 1 ? store.Get(ids[^1]) : null;
            if (m is null) return new Answer("You don't have any recorded meetings yet.", [], true);
            var word = rank == RecencyDir.Latest
                ? "most recent"
                : plan.Original.Contains("first", StringComparison.OrdinalIgnoreCase) ? "first" : "oldest";
            var text = $"Your {word} meeting was “{m.Title}” on {m.StartedAt:MMM d, yyyy}.";
            var gist = Gist(m);
            if (gist is not null) text += $" {gist}";
            return new Answer(text, [new Source(m.Id, m.Title)], true);
        }

        // 2) A date that hits SEVERAL meetings ("the meeting I had on July 15th" × 4 that day) —
        //    never silently pick one; make the multiplicity explicit and let the user choose.
        if (plan.HasDateRange && plan.Rank is null && IdentifyThin(plan.Residual))
        {
            var ids = store.MeetingIdsInRange(plan.RangeStart!.Value, plan.RangeEnd!.Value);
            if (ids.Count > 1)
            {
                var meetings = ids.Select(store.Get).OfType<MeetingRecord>().ToList();
                var sb = new StringBuilder($"You had {meetings.Count} meetings {DescribeRange(plan)}:\n");
                foreach (var mm in meetings)
                    sb.Append("• ").Append(mm.Title).Append('\n');
                sb.Append("Ask me about any of them by name.");
                return new Answer(sb.ToString(), meetings.Select(mm => new Source(mm.Id, mm.Title)).ToList(), true);
            }
            return null;   // 0 → the honest date miss below; 1 → summarize that one as before
        }

        // 3) Identification by TOPIC ("what was the meeting I had regarding Amazon?") — if the topic
        //    matches meeting TITLE(s), that's the answer, from the DB. (The live run's Amazon miss:
        //    the term was in a title, and the lookup route still answered "no meeting regarding
        //    Amazon".) No title match → null, and the normal lookup (now title-aware) proceeds.
        if (!plan.HasTemporal && WhichMeetingRx.IsMatch(plan.Original))
        {
            var terms = TitleTerms(string.IsNullOrWhiteSpace(plan.Residual) ? plan.Original : plan.Residual);
            if (terms.Count == 0) return null;
            var ids = store.TitleMatchIds(terms);
            if (ids.Count == 1)
            {
                var m = store.Get(ids[0]);
                if (m is null) return null;
                var text = $"That was “{m.Title}” on {m.StartedAt:MMM d, yyyy}.";
                var gist = Gist(m);
                if (gist is not null) text += $" {gist}";
                return new Answer(text, [new Source(m.Id, m.Title)], true);
            }
            if (ids.Count is > 1 and <= 6)
            {
                var meetings = ids.Select(store.Get).OfType<MeetingRecord>().ToList();
                var sb = new StringBuilder($"You've got {meetings.Count} meetings on that:\n");
                foreach (var mm in meetings)
                    sb.Append("• ").Append(mm.Title).Append("   —   ").Append(mm.StartedAt.ToString("ddd, MMM d")).Append('\n');
                return new Answer(sb.ToString().TrimEnd(), meetings.Select(mm => new Source(mm.Id, mm.Title)).ToList(), true);
            }
        }
        return null;
    }

    /// <summary>A one-line gist for an identified meeting: the first sentence of its summary.</summary>
    static string? Gist(MeetingRecord m)
    {
        var tldr = (m.Notes.Tldr ?? "").Trim();
        if (tldr.Length == 0)
            tldr = m.Notes.EffectiveSections().SelectMany(s => s.Bullets).FirstOrDefault() ?? "";
        if (tldr.Length == 0) return null;
        var cut = tldr.IndexOfAny(new[] { '.', '!', '?' });
        var first = cut > 0 && cut < tldr.Length - 1 ? tldr[..(cut + 1)] : tldr;
        if (first.Length > 220) first = first[..220].TrimEnd() + "…";
        return first.EndsWith(".") || first.EndsWith("!") || first.EndsWith("?") ? first : first + ".";
    }

    /// <summary>Content terms suitable for a title match — the question minus stop/ask words.</summary>
    static List<string> TitleTerms(string text) =>
        (text ?? "")
            .Split(new[] { ' ', '\t', '?', '.', ',', '!', ';', ':', '\'', '"', '(', ')', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !Stop.Contains(w) && !GenericAskWords.Contains(w))
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();

    // A single meeting, to be condensed by the model (its stored notes are the context — already good).
    static string BuildSummarizePrompt(MeetingRecord m)
    {
        var sb = new StringBuilder();
        sb.Append("Today is ").Append(DateTimeOffset.Now.ToString("dddd, MMMM d, yyyy")).Append('.').AppendLine().AppendLine();
        sb.AppendLine("Summarize this meeting for the user in 2–4 sentences, then (if any) a short bullet list of the key points. Use ONLY these notes:").AppendLine();
        sb.Append(BuildContext(new List<MeetingRecord> { m }));
        return sb.ToString();
    }

    // ── task answers (intent already decided as ListTasks by QueryUnderstanding) ──
    // Words that are just task phrasing — stripped from the residual to see if the user ALSO named a
    // specific meeting/topic ("action items in the data engineering meeting" → topic "data engineering").
    static readonly HashSet<string> TaskWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "open","task","tasks","action","items","item","todo","todos","to-do","to-dos","outstanding","commit",
        "committed","commitments","commitment","assigned","agree","agreed","need","needs","follow","followup","up",
        "meeting","meetings","list","show","give","tell","pending","remaining","left","done","complete","my","all","the",
        "what","are","is","in","for","from","of","on","about","do","i","have","to","any","there","were","was",
        "anything","something","me","mine","own","get","got","given","did","they","mention","mentioned",
    };

    // SELF-reference ("was anything assigned to ME?", "what's on MY plate?") — the user is the
    // pseudo-owner "You"/"me" in the notes. P8 deliberately guarded those words OUT of the by-name
    // person filter (so "what did you note down" can't misfire); this is the matching, explicit
    // self-slot: it only fires on unambiguous self-assignment phrasings, and resolves against the
    // self-owner values that exist in the data.
    static readonly Regex SelfTaskRx = new(
        @"\b(assign(ed)?|delegat(ed)?)\s+(to\s+)?me\b|\bon\s+my\s+plate\b|\bmy\s+responsibilit(y|ies)\b"
        + @"|\b(am|was)\s+i\s+(responsible|assigned|mentioned|given|tasked|on\s+the\s+hook)\b|\bfor\s+me\s+to\s+do\b"
        + @"|\bdo\s+i\s+own\b|\bowned\s+by\s+me\b|\bi\s+(was|got)\s+assigned\b|\bmine\b"
        // The live-run misses ("did they mention me or give me any tasks?", "anything for me?",
        // "did I get anything?"): assignment-TO-me phrasings, still never the broad "what are my
        // open tasks" (that stays the global list — P9's deliberate guard).
        + @"|\bmention(ed)?\s+me\b|\b(they|anyone|someone|somebody|he|she|we)\s+(give|gave|given|assign(ed)?|hand(ed)?)\s+me\b"
        + @"|\b(anything|something|any\s+(tasks?|to-?dos?|items?|action\s+items?|work))\s+for\s+me\b"
        + @"|\btasks?\s+for\s+me\b|\bdid\s+i\s+get\s+(any(thing)?|some(thing)?|assigned)\b|\bwas\s+i\s+(given|tasked)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static Answer TaskAnswer(QueryPlan plan)
    {
        var store = Store;

        // SELF slot first ("assigned to me" = owner You), then the PERSON slot ("what did MAC have as
        // action items?") — resolved against the closed vocabulary of owner names that actually exist
        // in the data (the shipping-assistant slot-resolution pattern: an entity only counts when it
        // resolves against a known-value list, so an arbitrary word can never trigger the filter).
        var self = SelfTaskRx.IsMatch(plan.Original);
        var people = self ? new List<string>() : MatchPeople(store.ActionOwners(),
            string.IsNullOrWhiteSpace(plan.Residual) ? plan.Original : plan.Residual);

        // Follow-up / screen pin ("…in this meeting") pinned the meeting(s) → list theirs.
        if (plan.PinnedMeetingIds is { Count: > 0 } pinned)
        {
            var scoped = pinned.Select(store.Get).OfType<MeetingRecord>().ToList();
            if (scoped.Count > 0) return ScopedTaskAnswer(scoped, plan, people, self);
        }

        // Topic words = residual minus task phrasing AND minus the matched person (the person is a
        // filter, not a topic — "Mac's action items" is global-for-Mac, not a search for "Mac").
        var personTokens = people
            .SelectMany(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();
        var topic = plan.Residual
            .Split(new[] { ' ', '?', '.', ',', '!', ';', ':', '\'', '"', '(', ')', '/', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !TaskWords.Contains(w) && !Stop.Contains(w)
                        && !personTokens.Contains(NormToken(w)))
            .ToList();

        // Figure out the SCOPE of the task question, dynamically, from topic AND/OR date:
        //   topic only          → the meeting(s) matching that topic
        //   date only           → the meeting(s) on that date / in that range   (the "July 21" bug)
        //   topic + date        → topic matches within that date window
        //   neither             → global open tasks across all meetings
        List<MeetingRecord> scope = new();
        if (topic.Count > 0)
        {
            var ids = store.SearchRelevant(string.Join(" ", topic), plan.HasDateRange ? 15 : 3);
            if (plan.HasDateRange)
            {
                var inRange = store.MeetingIdsInRange(plan.RangeStart!.Value, plan.RangeEnd!.Value);
                ids = ids.Where(inRange.Contains).ToList();
            }
            // A named single meeting ("the postgres meeting") → the best match only; a topic within a
            // date window may legitimately span a few.
            scope = ids.Take(plan.HasDateRange ? 3 : 1).Select(store.Get).OfType<MeetingRecord>().ToList();
        }
        else if (plan.HasDateRange)
        {
            scope = store.MeetingIdsInRange(plan.RangeStart!.Value, plan.RangeEnd!.Value)
                .Select(store.Get).OfType<MeetingRecord>().ToList();
        }

        if (scope.Count > 0) return ScopedTaskAnswer(scope, plan, people, self);

        // GLOBAL: "what are my open tasks" → everything still open; "what are Mac's tasks" → Mac's only;
        // "is anything assigned to me?" → only items owned by You/me — and an honest, helpful miss that
        // names who the open items actually belong to (never someone else's item presented as yours —
        // the screenshot bug).
        var all = store.OpenActions(25);
        var acts = all;
        if (self) acts = all.Where(a => IsSelfOwner(a.Owner)).ToList();
        else if (people.Count > 0) acts = all.Where(a => OwnerMatches(a.Owner, people)).ToList();
        var who = people.Count > 0 ? Names(people) : null;
        if (acts.Count == 0)
        {
            if (self)
            {
                var others = all.Select(a => a.Owner).Where(o => !string.IsNullOrWhiteSpace(o)).Distinct().Take(4).ToList();
                return new Answer(all.Count == 0
                    ? "Nothing's assigned to you — no open tasks anywhere. 🎉"
                    : $"Nothing's assigned specifically to you. The {all.Count} open item{(all.Count == 1 ? "" : "s")} belong{(all.Count == 1 ? "s" : "")} to {string.Join(", ", others)}.", [], true);
            }
            return new Answer(who is null
                ? "You have no open tasks — everything's checked off. 🎉"
                : $"{who} has no open tasks in your meetings.", [], true);
        }
        var g = new StringBuilder(
            self ? $"You have {acts.Count} open task{(acts.Count == 1 ? "" : "s")} assigned to you:\n"
            : who is null
                ? $"You have {acts.Count} open task{(acts.Count == 1 ? "" : "s")}:\n"
                : $"{who} has {acts.Count} open task{(acts.Count == 1 ? "" : "s")}:\n");
        foreach (var a in acts)
        {
            var meta = new[] { a.Owner, a.Due }.Where(x => !string.IsNullOrWhiteSpace(x));
            g.Append("• ").Append(a.Text);
            if (meta.Any()) g.Append(" (").Append(string.Join(", ", meta)).Append(')');
            g.Append("   —   ").Append(a.Meeting).Append('\n');
        }
        return new Answer(g.ToString().TrimEnd(), [], true);
    }

    /// <summary>Which of the KNOWN owner names appear in the question text. A word only counts as a
    /// person when it exactly matches an existing owner (or an owner's first name), case-insensitive,
    /// possessives stripped — so the filter can never fire on a word that isn't a real name. A stored
    /// multi-owner value ("Lily, Mac") resolves per person, so "Mac's items" finds it.</summary>
    public static List<string> MatchPeople(IEnumerable<string> knownOwners, string text)
    {
        var tokens = (text ?? "")
            .Split(new[] { ' ', '\t', '?', '.', ',', '!', ';', ':', '"', '(', ')', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormToken)
            .Where(t => t.Length > 1)
            .ToHashSet();
        var found = new List<string>();
        foreach (var owner in knownOwners.Where(o => !string.IsNullOrWhiteSpace(o)).SelectMany(OwnerParts).Distinct())
        {
            var full = NormToken(owner);
            var first = NormToken(owner.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
            if (first.Length < 2 || PersonGuard.Contains(first)) continue;   // "you"/"me"-style pseudo-owners
            if (tokens.Contains(full) || tokens.Contains(first)) found.Add(owner);
        }
        return found.Distinct().ToList();
    }

    // Never treat these as person names even if they show up as an "owner" value.
    static readonly HashSet<string> PersonGuard = new(StringComparer.OrdinalIgnoreCase)
    { "you", "me", "them", "they", "us", "we", "i", "team", "everyone", "all" };

    // Owner values the notes use for the USER themself ("You", "me") — the self-slot's closed list.
    static readonly HashSet<string> SelfOwnerNames = new(StringComparer.OrdinalIgnoreCase)
    { "you", "me", "myself", "i", "self", "user" };

    /// <summary>Is this action owned by the user themself ("You"/"me" in the notes)? Multi-owner
    /// values ("You, Mac") count — the item is (also) yours.</summary>
    public static bool IsSelfOwner(string? owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return false;
        return OwnerParts(owner).Any(p =>
            NormToken(p.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]) is { Length: > 0 } t && SelfOwnerNames.Contains(t));
    }

    // Split a stored multi-owner value ("Lily, Mac" / "Sid & Mac" / "Priya and Raj") into people.
    static IEnumerable<string> OwnerParts(string owner) =>
        Regex.Split(owner ?? "", @"\s*(?:,|/|&|\+|\band\b)\s*", RegexOptions.IgnoreCase)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0);

    // Lowercase, strip possessives ("mac's" → "mac") and any leftover punctuation.
    static string NormToken(string w)
    {
        var t = w.Trim('\'', '’', '"', '.', ',', '?', '!', ';', ':').ToLowerInvariant();
        if (t.EndsWith("'s") || t.EndsWith("’s")) t = t[..^2];
        return t.Trim('\'', '’');
    }

    /// <summary>Does this action's owner match any of the asked-about people (full name or first name)?
    /// A multi-owner value ("Lily, Mac") matches if ANY of its people do.</summary>
    public static bool OwnerMatches(string? owner, IReadOnlyList<string> people)
    {
        if (string.IsNullOrWhiteSpace(owner)) return false;
        foreach (var part in OwnerParts(owner))
        {
            var full = NormToken(part);
            var first = NormToken(part.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
            foreach (var p in people)
            {
                var pf = NormToken(p);
                var pFirst = NormToken(p.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
                if (full == pf || first == pFirst) return true;
            }
        }
        return false;
    }

    static string Names(IReadOnlyList<string> people) => string.Join(" and ", people.Distinct());

    // List the action items of a specific set of meetings (grouped, done + open), exactly from the DB.
    // When a PERSON was named (or the user asked about THEMSELF — "assigned to me"), only the matching
    // items are listed — and a miss is honest ("nothing here is assigned to you"), naming who actually
    // owns the items so the user isn't left guessing.
    static Answer ScopedTaskAnswer(List<MeetingRecord> meetings, QueryPlan plan, IReadOnlyList<string>? people = null, bool self = false)
    {
        people ??= [];
        var sources = meetings.Select(m => new Source(m.Id, m.Title)).ToList();
        var withActions = meetings.Where(m => m.Notes.Actions.Count > 0).ToList();
        if (withActions.Count == 0)
        {
            var where = plan.HasDateRange ? DescribeRange(plan) : $"“{meetings[0].Title}”";
            return new Answer($"No action items were recorded {(plan.HasDateRange ? "for meetings " + where : "for " + where)}.", sources, true);
        }

        // Person/self filter: keep only the matching items per meeting.
        List<ActionItem> ItemsOf(MeetingRecord m) =>
            self ? m.Notes.Actions.Where(a => IsSelfOwner(a.Owner)).ToList()
            : people.Count == 0 ? m.Notes.Actions.ToList()
            : m.Notes.Actions.Where(a => OwnerMatches(a.Owner, people)).ToList();

        if ((self || people.Count > 0) && withActions.Sum(m => ItemsOf(m).Count) == 0)
        {
            var owners = withActions.SelectMany(m => m.Notes.Actions)
                .Select(a => a.Owner).Where(o => !string.IsNullOrWhiteSpace(o)).Distinct().ToList();
            var where = withActions.Count == 1 ? $"“{withActions[0].Title}”" : "those meetings";
            var hint = owners.Count > 0 ? $" The items there are owned by {string.Join(", ", owners)}." : "";
            return new Answer(self
                ? $"Nothing in {where} is assigned to you.{hint}"
                : $"No action items for {Names(people)} in {where}.{hint}", sources, true);
        }

        var sb = new StringBuilder();
        var withMatch = withActions.Where(m => ItemsOf(m).Count > 0).ToList();
        // Single meeting → a clean header; multiple (a date window) → group by meeting.
        if (withMatch.Count == 1)
        {
            var m = withMatch[0];
            var items = ItemsOf(m);
            var open = items.Count(a => !a.Done);
            sb.Append(self
                ? $"You have {items.Count} action item{(items.Count == 1 ? "" : "s")} in “{m.Title}” ({open} open):\n"
                : people.Count > 0
                    ? $"{Names(people)} has {items.Count} action item{(items.Count == 1 ? "" : "s")} in “{m.Title}” ({open} open):\n"
                    : $"“{m.Title}” has {items.Count} action item{(items.Count == 1 ? "" : "s")} ({open} open):\n");
            AppendActions(sb, items);
        }
        else
        {
            sb.Append(self
                ? "Your action items:\n\n"
                : people.Count > 0
                    ? $"{Names(people)}'s action items:\n\n"
                    : $"Action items {DescribeRange(plan)}:\n\n");
            foreach (var m in withMatch)
            {
                sb.Append("“").Append(m.Title).Append("”:\n");
                AppendActions(sb, ItemsOf(m));
                sb.Append('\n');
            }
        }
        return new Answer(sb.ToString().TrimEnd(), withMatch.Select(m => new Source(m.Id, m.Title)).ToList(), true);
    }

    static void AppendActions(StringBuilder sb, IReadOnlyList<ActionItem> actions, int max = int.MaxValue)
    {
        var shown = 0;
        foreach (var a in actions)
        {
            if (shown >= max) { sb.Append("• …and ").Append(actions.Count - shown).Append(" more\n"); break; }
            var meta = new[] { a.Owner, a.Due }.Where(x => !string.IsNullOrWhiteSpace(x));
            sb.Append(a.Done ? "✓ " : "• ").Append(a.Text);   // plain bullets — the ugly [ ]/[x] were pointless in text
            if (meta.Any()) sb.Append(" (").Append(string.Join(", ", meta)).Append(')');
            sb.Append('\n');
            shown++;
        }
    }

    // ── retrieval ──
    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","and","or","of","to","in","on","for","with","what","who","whom","when","where","which",
        "did","do","does","is","are","was","were","be","been","my","me","i","we","us","our","this","that","these",
        "those","about","any","some","have","has","had","how","should","could","would","will","can","from","at","as","it",
    };

    /// <summary>Reduce a question to its CONTENT terms for retrieval. SearchRelevant is a prefix-OR
    /// match, so feeding it the raw question ("what was discussed about Kai in my meetings?") lets
    /// generic words like "meetings"/"discussed" match every note and drown the name in BM25 noise —
    /// query only the words that carry meaning ("kai"), and a real miss stays a miss (honest answer)
    /// instead of surfacing three random meetings.</summary>
    public static string ContentQuery(string question)
    {
        var words = (question ?? "")
            .Split(new[] { ' ', '\t', '?', '.', ',', '!', ';', ':', '\'', '"', '(', ')', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !Stop.Contains(w) && !GenericAskWords.Contains(w))
            .ToList();
        return words.Count == 0 ? question ?? "" : string.Join(" ", words);
    }

    static List<MeetingRecord> Retrieve(string question)
    {
        var store = Store;

        // 1) FTS5 BM25 over the CONTENT terms — the good path (relevance-ranked, incl. transcript).
        //    A meeting whose TITLE carries a query term is prepended (BM25F-style field priority: a
        //    title hit on a small corpus is near-certain relevance and must never be lost to prefix-OR
        //    noise — the live "Amazon" recall miss).
        var ids = store.TitleMatchIds(TitleTerms(question))
            .Concat(store.SearchRelevant(ContentQuery(question), 3))
            .Distinct().Take(3).ToList();

        // 2) Fallback: keyword LIKE scan (if FTS is unavailable or found nothing) — over the same
        //    CONTENT terms, so a generic word like "discussed" can't resurrect a miss as a false hit.
        if (ids.Count == 0)
        {
            var words = ContentQuery(question)
                .Split(new[] { ' ', '?', '.', ',', '!', ';', ':', '\'', '"', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !Stop.Contains(w) && !GenericAskWords.Contains(w))
                .Select(w => w.ToLowerInvariant()).Distinct().ToList();
            var scores = new Dictionary<long, int>();
            foreach (var w in words)
                try
                {
                    foreach (var m in store.List(w))
                        if (m.Kind == MeetingKind.Meeting) scores[m.Id] = scores.GetValueOrDefault(m.Id) + 1;
                }
                catch { }
            ids = scores.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();
        }

        // If nothing matched a LOOKUP question, return EMPTY → an honest "I couldn't find that in your
        // meetings." Never fall back to random recent meetings (that's what made a miss look like a
        // confident wrong answer — "acts like it knows everything"). Vague summarize-style asks take the
        // date/recency paths above, not this one.
        return ids.Select(id => store.Get(id)).OfType<MeetingRecord>().ToList();
    }

    // Context FIRST, then the question LAST (small models lose the middle); today's date anchors "this
    // week/recently"; a tight format constraint keeps answers short and grounded.
    static string BuildPrompt(string question, List<MeetingRecord> hits)
    {
        var sb = new StringBuilder();
        sb.Append("Today is ").Append(DateTimeOffset.Now.ToString("dddd, MMMM d, yyyy")).Append('.').AppendLine().AppendLine();
        sb.AppendLine("Relevant meeting notes:").AppendLine();
        sb.Append(BuildContext(hits)).AppendLine();
        sb.AppendLine("In the notes, “You” is the user themself — “I/me/my” in the question means them.");
        sb.Append("Question: ").AppendLine(question);
        sb.AppendLine("Answer in 1–4 sentences (or a short bullet list) using ONLY the notes above; name the meeting when useful.");
        return sb.ToString();
    }

    // The REASONING prompt: an analytical question wants judgement across meetings, not a quote. The
    // shape is answer-first (one-line conclusion, then evidence tied to meetings by name) with the
    // honesty rule inverted from the lookup route: give the partial answer the notes DO support, and
    // say in one sentence what they don't cover — never a bare "not found", never invention.
    static string BuildAnalyzePrompt(string question, List<MeetingRecord> hits)
    {
        var sb = new StringBuilder();
        sb.Append("Today is ").Append(DateTimeOffset.Now.ToString("dddd, MMMM d, yyyy")).Append('.').AppendLine().AppendLine();
        sb.AppendLine("The user wants your analysis and judgement, not a lookup. Read ALL the meeting notes below, connect what's related across them, and form your best supported conclusions.").AppendLine();
        sb.Append(BuildContext(hits, cap: 6000)).AppendLine();
        sb.AppendLine("In the notes, “You” is the user themself — “I/me/my” in the question means them.");
        sb.Append("Question: ").AppendLine(question);
        sb.AppendLine("Give your read like a sharp colleague:");
        sb.AppendLine("- Lead with your conclusion in one direct sentence.");
        sb.AppendLine("- Then 2–4 short supporting points, each tied to a meeting by name.");
        sb.AppendLine("- Use only what's in the notes. If they only partly cover the question, give what they DO show, then say in one honest sentence what's missing. Never invent.");
        return sb.ToString();
    }

    static string BuildContext(List<MeetingRecord> meetings, int cap = 3500)
    {
        var sb = new StringBuilder();
        foreach (var m in meetings)
        {
            sb.AppendLine($"### {m.Title} — {m.StartedAt:MMM d, yyyy}");
            if (!string.IsNullOrWhiteSpace(m.Notes.Tldr)) sb.AppendLine(m.Notes.Tldr);
            foreach (var sec in m.Notes.EffectiveSections())
            {
                if (sec.Bullets.Count == 0) continue;
                sb.AppendLine($"{sec.Heading}:");
                foreach (var b in sec.Bullets) sb.AppendLine($"- {b}");
            }
            if (m.Notes.Actions.Count > 0)
            {
                sb.AppendLine("Tasks:");
                foreach (var a in m.Notes.Actions)
                {
                    var meta = new[] { a.Owner is { } o ? $"owner {o}" : null, a.Due is { } d ? $"due {d}" : null, a.Done ? "done" : null }
                        .Where(x => x is not null);
                    sb.AppendLine($"- {a.Text}" + (meta.Any() ? $" ({string.Join(", ", meta)})" : ""));
                }
            }
            sb.AppendLine();
        }
        // Keep the context small — prefill dominates time-to-first-token on the partial-GPU 3050.
        // (Analyze passes a bigger cap: breadth is the whole point there, and notes are compact.)
        var s = sb.ToString();
        return s.Length > cap ? s[..cap] : s;
    }
}
