using System.Text;
using Toki.Storage;

namespace Toki.Llm;

/// <summary>
/// "Ask your notes": answers a natural-language question using ONLY the user's saved meetings.
/// A lightweight, fully-local retrieval step (keyword match over the stored meetings) gathers the
/// most relevant notes, then the active model (on-device Qwen or the BYO API key) answers from that
/// context and cites the meetings it used. Never invents — if the notes don't cover it, it says so.
///
/// Heavy (loads/runs the model): callers MUST invoke this off the UI thread (Task.Run).
/// </summary>
public static class NotesQa
{
    public sealed record Source(long Id, string Title);
    public sealed record Answer(string Text, IReadOnlyList<Source> Sources, bool Ok);

    const string System =
        "You are Toki, a helpful assistant that answers questions about the user's OWN meetings, using ONLY the "
        + "meeting notes provided below. Be concise, direct, and factual. Refer to meetings by their title when it "
        + "helps. If the provided notes do not contain the answer, say you couldn't find it in their meetings — "
        + "never guess or make anything up. Respond in English only.";

    public static async Task<Answer> AskAsync(string question, CancellationToken ct = default)
    {
        question = question?.Trim() ?? "";
        if (question.Length == 0) return new Answer("Ask me something about your meetings.", [], true);

        var llm = LlmService.Instance.Active();
        if (llm is null || !llm.Ready)
            return new Answer("Add the on-device model or your API key in Settings, and I can answer questions about your meetings.", [], false);

        var hits = Retrieve(question);
        if (hits.Count == 0)
            return new Answer("I couldn't find anything about that in your meetings yet.", [], true);

        var sources = hits.Select(h => new Source(h.Id, h.Title)).ToList();
        var prompt = $"The user asks: \"{question}\"\n\nAnswer using ONLY these meeting notes:\n\n{BuildContext(hits)}\n\nAnswer:";

        string raw;
        try { raw = (await llm.CompleteAsync(System, prompt, 440, ct)).Trim(); }
        catch (OperationCanceledException) { return new Answer("", sources, false); }
        catch (Exception ex) { App.Log($"notes QA failed: {ex.Message}"); return new Answer("Something went wrong answering that — try again.", sources, false); }

        if (TextValidator.IsDegenerate(raw))
            return new Answer("I couldn't produce a clean answer — try rephrasing the question.", sources, true);
        return new Answer(raw, sources, true);
    }

    // Words that carry no retrieval signal.
    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","and","or","of","to","in","on","for","with","what","who","whom","when","where","which",
        "did","do","does","is","are","was","were","be","been","my","me","i","we","us","our","this","that","these",
        "those","about","any","some","have","has","had","how","should","could","would","will","can","from","at","as","it",
    };

    static List<MeetingRecord> Retrieve(string question)
    {
        var store = App.Store;
        var words = question
            .Split(new[] { ' ', '?', '.', ',', '!', ';', ':', '\'', '"', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !Stop.Contains(w))
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();

        // Score meetings by how many of the question's keywords hit their title/transcript.
        var scores = new Dictionary<long, int>();
        foreach (var w in words)
        {
            try
            {
                foreach (var m in store.List(w))
                    if (m.Kind == MeetingKind.Meeting) scores[m.Id] = scores.GetValueOrDefault(m.Id) + 1;
            }
            catch { /* one bad term shouldn't sink the whole query */ }
        }

        List<long> ids = scores.Count > 0
            ? scores.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Key).ToList()
            // No keyword hits (or a vague question like "summarize my week") → the most recent meetings.
            : store.List().Where(m => m.Kind == MeetingKind.Meeting).Take(3).Select(m => m.Id).ToList();

        return ids.Select(id => store.Get(id)).OfType<MeetingRecord>().ToList();
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
        // Keep the context inside the local model's window.
        var s = sb.ToString();
        const int cap = 6000;
        return s.Length > cap ? s[..cap] : s;
    }
}
