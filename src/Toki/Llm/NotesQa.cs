using System.Text;
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

    const string System =
        "You are Toki, a helpful assistant that answers questions about the user's OWN meetings, using ONLY the "
        + "meeting notes provided. Be concise, direct, and factual. Refer to meetings by their title when it helps. "
        + "If the notes do not contain the answer, say you couldn't find it in their meetings — never guess or make "
        + "anything up. Respond in English only.";

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
        CancellationToken ct = default, ConversationContext? ctx = null)
    {
        question = question?.Trim() ?? "";
        if (question.Length == 0) return new Answer("Ask me something about your meetings.", [], true);

        // 1) UNDERSTAND FIRST: resolve a follow-up against the conversation, else parse fresh (no model, ~1ms).
        var now = DateTimeOffset.Now;
        var plan = (ctx is not null ? QueryUnderstanding.ResolveFollowUp(question, ctx, now) : null)
                   ?? QueryUnderstanding.Parse(question, now);

        // 2) Deterministic routes — answered straight from the DB, exact, no model, no hallucination.
        Answer? det = plan.Intent switch
        {
            QueryIntent.Count => CountAnswer(plan),
            QueryIntent.ListMeetings => ListMeetingsAnswer(plan),
            QueryIntent.ListTasks => TaskAnswer(plan),
            _ => null,
        };
        if (det is not null) { Remember(ctx, plan, det.Sources); onSources(det.Sources); onToken(det.Text); return det; }

        // 3) LLM routes (summary / factual lookup) — resolve the target meetings deterministically FIRST,
        //    so "most recent"/"on July 30" is decided by SQL and the model can't pick the wrong meeting.
        var llm = LlmService.Instance.Active();
        if (llm is null || !llm.Ready)
            return new Answer("Add the on-device model or your API key in Settings, and I can answer questions about your meetings.", [], false);

        var hits = ResolveForLlm(plan);
        if (hits.Count == 0)
            return new Answer(plan.HasDateRange
                ? $"I couldn't find a meeting {DescribeRange(plan)}."
                : "I couldn't find anything about that in your meetings yet.", [], true);

        var sources = hits.Select(h => new Source(h.Id, h.Title)).ToList();
        Remember(ctx, plan, sources);
        onSources(sources);   // citations up-front, before the answer streams in

        var prompt = plan.Intent == QueryIntent.SummarizeOne ? BuildSummarizePrompt(hits[0]) : BuildPrompt(question, hits);
        var sb = new StringBuilder();
        try
        {
            await foreach (var tok in llm.StreamAsync(System, prompt, 340, ct).ConfigureAwait(false))
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
        return new Answer(raw, sources, true);
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

    // Resolve which meeting(s) the LLM should read, deterministically, from the plan.
    static List<MeetingRecord> ResolveForLlm(QueryPlan plan)
    {
        var store = App.Store;

        // Follow-up pinned the meeting(s) from the previous turn — use them directly.
        if (plan.PinnedMeetingIds is { Count: > 0 } pinned)
            return pinned.Select(id => store.Get(id)).OfType<MeetingRecord>().ToList();

        List<long> ids;

        if (plan.Rank == RecencyDir.Latest || plan.Rank == RecencyDir.Oldest)
        {
            // "most recent / oldest" (optionally about a topic) → pick by DATE, not relevance.
            var pool = ThinResidual(plan.Residual) ? null : store.SearchRelevant(plan.Residual, 12);
            var ranked = plan.Rank == RecencyDir.Latest ? store.LatestMeetingIds(200) : store.OldestMeetingIds(200);
            ids = (pool is null ? ranked : ranked.Where(pool.Contains)).Take(Math.Max(1, plan.RankN)).ToList();
            if (ids.Count == 0) ids = ranked.Take(1).ToList();   // topic had no match → fall back to pure recency
        }
        else if (plan.HasDateRange)
        {
            var inRange = store.MeetingIdsInRange(plan.RangeStart!.Value, plan.RangeEnd!.Value);
            if (ThinResidual(plan.Residual)) ids = inRange.Take(3).ToList();
            else
            {
                var matched = store.SearchRelevant(plan.Residual, 12).Where(inRange.Contains).Take(3).ToList();
                ids = matched.Count > 0 ? matched : inRange.Take(3).ToList();   // topic within the date window, else the window
            }
        }
        else if (plan.Intent == QueryIntent.SummarizeOne)
        {
            ids = store.SearchRelevant(string.IsNullOrWhiteSpace(plan.Residual) ? plan.Original : plan.Residual, 1);
        }
        else
        {
            return Retrieve(plan.Original);   // LOOKUP_FACT with no temporal → existing FTS+fallback top-3
        }

        return ids.Select(id => store.Get(id)).OfType<MeetingRecord>().ToList();
    }

    static bool ThinResidual(string residual) =>
        residual.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(w => w.Length > 2 && !Stop.Contains(w)) == 0;

    // ── deterministic count / list-meetings answers ──
    static Answer CountAnswer(QueryPlan plan)
    {
        var ids = plan.HasDateRange ? App.Store.MeetingIdsInRange(plan.RangeStart!.Value, plan.RangeEnd!.Value) : App.Store.LatestMeetingIds(100000);
        var where = plan.HasDateRange ? $" {DescribeRange(plan)}" : "";
        return new Answer($"You have {ids.Count} meeting{(ids.Count == 1 ? "" : "s")}{where}.", [], true);
    }

    static Answer ListMeetingsAnswer(QueryPlan plan)
    {
        var ids = plan.HasDateRange
            ? App.Store.MeetingIdsInRange(plan.RangeStart!.Value, plan.RangeEnd!.Value)
            : App.Store.LatestMeetingIds(10);
        var meetings = ids.Select(id => App.Store.Get(id)).OfType<MeetingRecord>().ToList();
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
    };

    static Answer TaskAnswer(QueryPlan plan)
    {
        var store = App.Store;

        // Follow-up ("what about the action items?") pinned the prior meeting(s) → list theirs.
        if (plan.PinnedMeetingIds is { Count: > 0 } pinned)
        {
            var scoped = pinned.Select(store.Get).OfType<MeetingRecord>().ToList();
            if (scoped.Count > 0) return ScopedTaskAnswer(scoped, plan);
        }

        // Topic words = residual minus task phrasing → did the user name a specific meeting/topic?
        var topic = plan.Residual
            .Split(new[] { ' ', '?', '.', ',', '!', ';', ':', '\'', '"', '(', ')', '/', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !TaskWords.Contains(w) && !Stop.Contains(w))
            .ToList();

        // Figure out the SCOPE of the task question, dynamically, from topic AND/OR date:
        //   topic only          → the meeting(s) matching that topic
        //   date only           → the meeting(s) on that date / in that range   (the "July 21" bug)
        //   topic + date        → topic matches within that date window
        //   neither             → global open tasks across all meetings
        List<MeetingRecord> scope = new();
        if (topic.Count > 0)
        {
            var ids = store.SearchRelevant(plan.Residual, plan.HasDateRange ? 15 : 3);
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

        if (scope.Count > 0) return ScopedTaskAnswer(scope, plan);

        // GLOBAL: "what are my open tasks" → everything still open, across meetings.
        var acts = store.OpenActions(25);
        if (acts.Count == 0) return new Answer("You have no open tasks — everything's checked off. 🎉", [], true);
        var g = new StringBuilder($"You have {acts.Count} open task{(acts.Count == 1 ? "" : "s")}:\n");
        foreach (var a in acts)
        {
            var meta = new[] { a.Owner, a.Due }.Where(x => !string.IsNullOrWhiteSpace(x));
            g.Append("• ").Append(a.Text);
            if (meta.Any()) g.Append(" (").Append(string.Join(", ", meta)).Append(')');
            g.Append("   —   ").Append(a.Meeting).Append('\n');
        }
        return new Answer(g.ToString().TrimEnd(), [], true);
    }

    // List the action items of a specific set of meetings (grouped, done + open), exactly from the DB.
    static Answer ScopedTaskAnswer(List<MeetingRecord> meetings, QueryPlan plan)
    {
        var sources = meetings.Select(m => new Source(m.Id, m.Title)).ToList();
        var withActions = meetings.Where(m => m.Notes.Actions.Count > 0).ToList();
        if (withActions.Count == 0)
        {
            var where = plan.HasDateRange ? DescribeRange(plan) : $"“{meetings[0].Title}”";
            return new Answer($"No action items were recorded {(plan.HasDateRange ? "for meetings " + where : "for " + where)}.", sources, true);
        }

        var sb = new StringBuilder();
        // Single meeting → a clean header; multiple (a date window) → group by meeting.
        if (withActions.Count == 1)
        {
            var m = withActions[0];
            var open = m.Notes.Actions.Count(a => !a.Done);
            sb.Append($"“{m.Title}” has {m.Notes.Actions.Count} action item{(m.Notes.Actions.Count == 1 ? "" : "s")} ({open} open):\n");
            AppendActions(sb, m.Notes.Actions);
        }
        else
        {
            sb.Append($"Action items {DescribeRange(plan)}:\n\n");
            foreach (var m in withActions)
            {
                sb.Append("“").Append(m.Title).Append("”:\n");
                AppendActions(sb, m.Notes.Actions);
                sb.Append('\n');
            }
        }
        return new Answer(sb.ToString().TrimEnd(), withActions.Select(m => new Source(m.Id, m.Title)).ToList(), true);
    }

    static void AppendActions(StringBuilder sb, IReadOnlyList<ActionItem> actions)
    {
        foreach (var a in actions)
        {
            var meta = new[] { a.Owner, a.Due }.Where(x => !string.IsNullOrWhiteSpace(x));
            sb.Append(a.Done ? "• [x] " : "• [ ] ").Append(a.Text);
            if (meta.Any()) sb.Append(" (").Append(string.Join(", ", meta)).Append(')');
            sb.Append('\n');
        }
    }

    // ── retrieval ──
    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","and","or","of","to","in","on","for","with","what","who","whom","when","where","which",
        "did","do","does","is","are","was","were","be","been","my","me","i","we","us","our","this","that","these",
        "those","about","any","some","have","has","had","how","should","could","would","will","can","from","at","as","it",
    };

    static List<MeetingRecord> Retrieve(string question)
    {
        var store = App.Store;

        // 1) FTS5 BM25 — the good path (relevance-ranked over every field incl. transcript).
        var ids = store.SearchRelevant(question, 3);

        // 2) Fallback: keyword LIKE scan (if FTS is unavailable or found nothing).
        if (ids.Count == 0)
        {
            var words = question
                .Split(new[] { ' ', '?', '.', ',', '!', ';', ':', '\'', '"', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !Stop.Contains(w))
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

        // 3) Nothing matched (vague question like "summarize my week") → the most recent meetings.
        if (ids.Count == 0)
            ids = store.List().Where(m => m.Kind == MeetingKind.Meeting).Take(3).Select(m => m.Id).ToList();

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
        sb.Append("Question: ").AppendLine(question);
        sb.AppendLine("Answer in 1–4 sentences (or a short bullet list) using ONLY the notes above; name the meeting when useful.");
        return sb.ToString();
    }

    static string BuildContext(List<MeetingRecord> meetings)
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
        var s = sb.ToString();
        const int cap = 3500;
        return s.Length > cap ? s[..cap] : s;
    }
}
