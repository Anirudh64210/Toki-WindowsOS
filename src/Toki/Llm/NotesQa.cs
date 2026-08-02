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

    /// <summary>Answer a question, calling <paramref name="onSources"/> once the source meetings are known
    /// and <paramref name="onToken"/> for each piece of the answer as it streams. Returns the final answer.
    /// Callbacks fire on a background thread — the UI must marshal them to the dispatcher.</summary>
    public static async Task<Answer> AskStreamAsync(
        string question, Action<IReadOnlyList<Source>> onSources, Action<string> onToken, CancellationToken ct = default)
    {
        question = question?.Trim() ?? "";
        if (question.Length == 0) return new Answer("Ask me something about your meetings.", [], true);

        // Router: open tasks / commitments answered straight from the DB — instant, exact, no hallucination.
        var routed = TryTaskAnswer(question);
        if (routed is not null) { onSources(routed.Sources); onToken(routed.Text); return routed; }

        var llm = LlmService.Instance.Active();
        if (llm is null || !llm.Ready)
            return new Answer("Add the on-device model or your API key in Settings, and I can answer questions about your meetings.", [], false);

        var hits = Retrieve(question);
        if (hits.Count == 0) return new Answer("I couldn't find anything about that in your meetings yet.", [], true);

        var sources = hits.Select(h => new Source(h.Id, h.Title)).ToList();
        onSources(sources);   // show the citations up-front, before the answer streams in

        var prompt = BuildPrompt(question, hits);
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

    // ── task-query router ──
    static readonly string[] TaskCues =
    {
        "open task", "open tasks", "task", "tasks", "action item", "action items",
        "what do i need to do", "what do i have to do", "what did i commit", "what did i agree to",
        "outstanding", "to-do", "to-dos", "todo", "todos", "assigned", "still need to", "follow up", "follow-up",
    };

    // Words that are part of the task-question phrasing itself — stripped to see if the user ALSO named a
    // specific meeting/topic (e.g. "action items in the data engineering meeting" → residual "data engineering").
    static readonly HashSet<string> TaskWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "open","task","tasks","action","items","item","todo","todos","to-do","to-dos","outstanding","commit",
        "committed","commitments","commitment","assigned","agree","agreed","need","needs","follow","followup","up",
        "meeting","meetings","list","show","give","tell","pending","remaining","left","done","complete","my","all","the",
        "what","are","is","in","for","from","of","on","about","do","i","have","to","any","there","were","was",
    };

    static Answer? TryTaskAnswer(string question)
    {
        var q = question.ToLowerInvariant();
        if (!TaskCues.Any(q.Contains)) return null;

        // Did the user also name a specific meeting/topic? (content words beyond the task phrasing)
        var residual = question
            .Split(new[] { ' ', '?', '.', ',', '!', ';', ':', '\'', '"', '(', ')', '/', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !TaskWords.Contains(w) && !Stop.Contains(w))
            .ToList();

        // SCOPED: "action items in <meeting>" → list THAT meeting's items exactly (done + open), no global dump.
        if (residual.Count > 0)
        {
            var ids = App.Store.SearchRelevant(question, 1);
            if (ids.Count == 0) return null;   // couldn't pin a meeting → let the model try
            var m = App.Store.Get(ids[0]);
            if (m is null) return null;
            var sources = new List<Source> { new(m.Id, m.Title) };
            if (m.Notes.Actions.Count == 0)
                return new Answer($"No action items were recorded for “{m.Title}”.", sources, true);

            var open = m.Notes.Actions.Count(a => !a.Done);
            var sb = new StringBuilder($"“{m.Title}” has {m.Notes.Actions.Count} action item{(m.Notes.Actions.Count == 1 ? "" : "s")} ({open} open):\n");
            foreach (var a in m.Notes.Actions)
            {
                var meta = new[] { a.Owner, a.Due }.Where(x => !string.IsNullOrWhiteSpace(x));
                sb.Append(a.Done ? "• [x] " : "• [ ] ").Append(a.Text);
                if (meta.Any()) sb.Append(" (").Append(string.Join(", ", meta)).Append(')');
                sb.Append('\n');
            }
            return new Answer(sb.ToString().TrimEnd(), sources, true);
        }

        // GLOBAL: bare "what are my open tasks" → everything still open, across meetings.
        var acts = App.Store.OpenActions(25);
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
