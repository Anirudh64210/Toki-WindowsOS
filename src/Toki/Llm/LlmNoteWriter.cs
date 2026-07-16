using Toki.Storage;
using Toki.Transcription;

namespace Toki.Llm;

/// <summary>
/// Writes meeting notes with a local LLM in focused passes, with the TL;DR
/// written LAST from the other sections (brief §5). Every model output is
/// validated (lesson 8); on garbage or error a section falls back to the
/// heuristic extractor, so the user never sees degenerate text and always gets
/// notes. Questions come from the heuristic (reliable: they end in '?').
/// </summary>
public sealed class LlmNoteWriter
{
    const string System =
        "You are a meeting-notes assistant. Respond in English only. Be concise and factual. "
        + "Output ONLY what is asked — no preamble, no explanations, no markdown headings.";

    readonly ILlm _llm;
    public LlmNoteWriter(ILlm llm) => _llm = llm;

    public async Task<NoteSet> WriteAsync(IReadOnlyList<TranscriptSegment> transcript, CancellationToken ct = default)
    {
        var heuristic = NoteExtractor.Extract(transcript);
        if (!_llm.Ready || transcript.Count == 0) return heuristic;

        var text = Flatten(transcript);

        var decisions = await ListPass(
            $"List the concrete decisions the group made. One per line, each starting with \"- \". "
            + $"If there were none, output exactly: NONE.\n\nTranscript:\n{text}", ct)
            ?? heuristic.Decisions;

        var actions = await ActionPass(
            $"List the action items / to-dos. One per line in the format: "
            + $"\"- <task> | owner: <name or -> | due: <when or ->\". If none, output exactly: NONE.\n\nTranscript:\n{text}", ct)
            ?? heuristic.Actions;

        // TL;DR written LAST, from the reduced sections (not the raw transcript).
        var tldr = await TextPass(
            $"Write a 2-3 sentence plain-English summary of this meeting using these notes.\n\n"
            + $"Decisions:\n{Bullets(decisions)}\n\nTo-dos:\n{Bullets(actions.Select(a => a.Text))}", 160, ct)
            ?? heuristic.Tldr;

        return new NoteSet
        {
            Tldr = tldr,
            Decisions = decisions,
            Actions = actions,
            Questions = heuristic.Questions, // heuristic is reliable for questions
        };
    }

    // ---- passes (each validated, retried once, else null → caller falls back) ----

    async Task<string?> TextPass(string user, int maxTokens, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string outp;
            try { outp = (await _llm.CompleteAsync(System, user, maxTokens, ct)).Trim(); }
            catch { return null; }
            if (!TextValidator.IsDegenerate(outp)) return outp;
        }
        return null;
    }

    async Task<List<string>?> ListPass(string user, CancellationToken ct)
    {
        var raw = await TextPass(user, 400, ct);
        if (raw is null) return null;
        if (raw.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase)) return [];
        var items = ParseBullets(raw);
        return items.Count > 0 ? items : null;
    }

    async Task<List<ActionItem>?> ActionPass(string user, CancellationToken ct)
    {
        var raw = await TextPass(user, 500, ct);
        if (raw is null) return null;
        if (raw.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase)) return [];
        var actions = new List<ActionItem>();
        foreach (var line in ParseBullets(raw))
        {
            var parts = line.Split('|');
            var item = new ActionItem { Text = parts[0].Trim() };
            foreach (var p in parts.Skip(1))
            {
                var kv = p.Split(':', 2);
                if (kv.Length != 2) continue;
                var key = kv[0].Trim().ToLowerInvariant();
                var val = kv[1].Trim();
                if (val is "-" or "") continue;
                if (key == "owner") item.Owner = val;
                else if (key == "due") item.Due = val;
            }
            if (item.Text.Length > 1) actions.Add(item);
        }
        return actions.Count > 0 ? actions : null;
    }

    static List<string> ParseBullets(string raw) => raw
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.TrimStart('-', '*', '•', ' ', '\t').Trim())
        .Where(l => l.Length > 1 && !l.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        .ToList();

    static string Flatten(IReadOnlyList<TranscriptSegment> t) =>
        string.Join("\n", t.Select(s => $"{(s.Speaker == Speaker.You ? "You" : "Them")}: {s.Text}"));

    static string Bullets(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "(none)" : string.Join("\n", list.Select(x => $"- {x}"));
    }
}
