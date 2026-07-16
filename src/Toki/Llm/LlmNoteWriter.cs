using Toki.Storage;
using Toki.Transcription;

namespace Toki.Llm;

/// <summary>
/// Writes meeting notes with a local LLM. To stay fast (target: well under the
/// old multi-pass minute), it does ONE structured generation for Key points +
/// Action items + Summary — with the Summary asked for LAST so the model writes
/// it after the details it's summarising (brief §5 / lesson 8) — then a tiny
/// title pass. Every output is validated; on garbage or error a section falls
/// back to the heuristic extractor, so the user always gets clean notes.
/// (Granola-style shape: a labelled summary, a few key points, and to-dos.
/// No "decisions" section — it just echoed the to-dos — and no open questions.)
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

        // One pass for everything the model is good at, in write-order KEY POINTS
        // → ACTION ITEMS → SUMMARY (summary last, from what it just wrote).
        var (keyPoints, actions, summary) = await CombinedPass(text, ct);

        var notes = new NoteSet
        {
            Tldr = string.IsNullOrWhiteSpace(summary) ? heuristic.Tldr : summary,
            Decisions = keyPoints ?? heuristic.Decisions,     // Decisions column repurposed as "Key points"
            Actions = actions ?? heuristic.Actions,
            Questions = [],                                    // open-questions section retired
        };

        // A short, specific title from the summary (concise — not a sentence).
        notes.Title = await TitlePass(notes.Tldr, ct);
        return notes;
    }

    /// <summary>
    /// Single generation returning (keyPoints, actions, summary). Any section the
    /// model mangles comes back null so the caller uses the heuristic for it.
    /// </summary>
    async Task<(List<string>? KeyPoints, List<ActionItem>? Actions, string? Summary)> CombinedPass(string text, CancellationToken ct)
    {
        var prompt =
            "Summarise this meeting in exactly three sections, using these headers on their own lines:\n\n"
            + "KEY POINTS\n- 3 to 4 SHORT bullets (max ~14 words each) on the TOPICS, context, and decisions "
            + "discussed — the substance, not a to-do list. Do NOT restate the action items here, and do not "
            + "start bullets with \"I will\".\n\n"
            + "ACTION ITEMS\n- each concrete task/follow-up on its own line as: task | owner: name | due: when. "
            + "Leave out 'owner:' or 'due:' entirely when unknown — never write a placeholder dash.\n\n"
            + "SUMMARY\nOne short paragraph (2-3 sentences) of plain-English takeaways.\n\n"
            + "If a section genuinely has nothing, put NONE under its header.\n\n"
            + $"Transcript:\n{text}";

        var raw = await TextPass(prompt, 480, ct);
        if (raw is null) return (null, null, null);

        var (kpLines, aiLines, summary) = SplitSections(raw);
        var keyPoints = kpLines.Count > 0 ? kpLines : null;
        var actions = aiLines.Count > 0 ? ParseActions(aiLines) : null;
        return (keyPoints, actions, string.IsNullOrWhiteSpace(summary) ? null : summary);
    }

    /// <summary>Split the model's output into the three labelled sections.</summary>
    static (List<string> KeyPoints, List<string> Actions, string Summary) SplitSections(string raw)
    {
        var keyPoints = new List<string>();
        var actions = new List<string>();
        var summary = new System.Text.StringBuilder();
        var section = 0; // 0 none, 1 key points, 2 actions, 3 summary

        foreach (var lineRaw in raw.Split('\n'))
        {
            var line = lineRaw.Trim();
            if (line.Length == 0) continue;
            var header = HeaderOf(line);
            if (header != 0) { section = header; continue; }
            if (line.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;

            var clean = line.TrimStart('-', '*', '•', ' ', '\t').Trim();
            switch (section)
            {
                case 1 when clean.Length > 1: keyPoints.Add(clean); break;
                case 2 when clean.Length > 1: actions.Add(clean); break;
                case 3: if (summary.Length > 0) summary.Append(' '); summary.Append(clean); break;
            }
        }
        return (keyPoints, actions, summary.ToString().Trim());
    }

    // Recognise a section header even if the model bolds it or adds a colon.
    static int HeaderOf(string line)
    {
        var h = line.Trim('*', '#', ':', ' ', '\t').ToLowerInvariant();
        if (h is "key points" or "keypoints" or "highlights" or "key point") return 1;
        if (h is "action items" or "action item" or "actions" or "to-dos" or "todos" or "to dos" or "next steps") return 2;
        if (h is "summary" or "tl;dr" or "tldr" or "overview") return 3;
        return 0;
    }

    static List<ActionItem> ParseActions(List<string> lines)
    {
        var actions = new List<ActionItem>();
        foreach (var line in lines)
        {
            var parts = line.Split('|');
            var item = new ActionItem { Text = parts[0].Trim().TrimEnd('.', ' ') };
            foreach (var p in parts.Skip(1))
            {
                var kv = p.Split(':', 2);
                if (kv.Length != 2) continue;
                var key = kv[0].Trim().ToLowerInvariant();
                var val = kv[1].Trim();
                if (IsEmptyMarker(val)) continue;
                if (key == "owner") item.Owner = val;
                else if (key == "due") item.Due = val;
            }
            if (item.Text.Length > 1) actions.Add(item);
        }
        return actions;
    }

    async Task<string?> TitlePass(string summary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summary)) return null;
        var raw = await TextPass(
            $"Write a title for this meeting: 3 to 6 words, specific and concise, like a calendar event. "
            + $"No quotes, no ending punctuation, no \"Meeting\" prefix.\n\nSummary: {summary}", 24, ct);
        if (raw is null) return null;
        // Take the first line, strip quotes/trailing punctuation, cap length.
        var t = raw.Split('\n')[0].Trim().Trim('"', '\'', '.', ':', '-', ' ');
        if (t.Length < 3) return null;
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 8) t = string.Join(' ', words.Take(8));
        return t;
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

    // The model likes to fill blanks with dashes/placeholders instead of omitting them.
    static bool IsEmptyMarker(string v)
    {
        var s = v.Trim().Trim('<', '>', '(', ')', '[', ']').Trim().ToLowerInvariant();
        return s is "" or "-" or "--" or "->" or "—" or "n/a" or "na" or "none"
            or "tbd" or "tba" or "unknown" or "unassigned" or "name" or "when"
            or "no owner" or "no due" or "no due date";
    }

    static string Flatten(IReadOnlyList<TranscriptSegment> t) =>
        string.Join("\n", t.Select(s => $"{(s.Speaker == Speaker.You ? "You" : "Them")}: {s.Text}"));
}
