using System.Text;
using Toki.Storage;
using Toki.Transcription;

namespace Toki.Llm;

/// <summary>
/// Writes meeting notes with a local LLM — MAP-REDUCE and ADAPTIVE.
///
/// Why map-reduce: the local model's context is only a few thousand tokens. A single
/// pass over a 25-minute transcript overruns it, the call throws, and we fall back to
/// the raw-first-lines heuristic (the "summary is just the opening sentences" bug). So
/// instead we split the transcript at speaker/segment boundaries into context-safe
/// windows, "map" each window to a compact factual digest, then "reduce" the digests
/// into the final note. Summarising summaries never overflows — a 2-hour meeting works
/// as well as a 5-minute one.
///
/// Why adaptive + honest: not every meeting has decisions, tasks, or goals. The reduce
/// step judges the meeting's substance and emits ONLY the sections that genuinely apply
/// (empty sections are dropped by the renderer). A casual or inconclusive meeting gets a
/// one-line honest note — never corporate filler manufactured to look important. This is
/// the Granola/Fireflies principle: pick the shape that fits, don't force a template.
/// </summary>
public sealed class LlmNoteWriter
{
    const string System =
        "You are a meeting-notes assistant. Respond in English only. Be concise, factual, and honest. "
        + "Output ONLY what is asked — no preamble, no explanations, no markdown headings, no 'None'.";

    // A window that a chunk digest / final pass will report nothing for.
    const string NothingMarker = "NOTHING";

    readonly ILlm _llm;
    public LlmNoteWriter(ILlm llm) => _llm = llm;

    public async Task<NoteSet> WriteAsync(IReadOnlyList<TranscriptSegment> transcript, CancellationToken ct = default)
    {
        var heuristic = NoteExtractor.Extract(transcript);
        if (!_llm.Ready || transcript.Count == 0) return heuristic;

        var chunks = Chunk(transcript, MaxChunkChars());

        // Decide what the final pass reads. One window → read it raw (best fidelity for a short
        // meeting). Many windows → MAP each to a digest first, so the final pass fits in context.
        string source;
        bool sourceIsDigests;
        if (chunks.Count == 1)
        {
            source = chunks[0];
            sourceIsDigests = false;
        }
        else
        {
            var digests = new List<string>();
            for (var i = 0; i < chunks.Count; i++)
            {
                var d = await MapChunk(chunks[i], i + 1, chunks.Count, ct);
                if (!string.IsNullOrWhiteSpace(d) && !IsNothing(d)) digests.Add(d);
            }
            // Every window was greetings/logistics/silence → an honest, minimal note. No filler.
            if (digests.Count == 0) return LowSubstanceNote();
            source = string.Join("\n", digests);
            sourceIsDigests = true;
        }

        // REDUCE / FINAL: the adaptive, honest note.
        var note = await FinalNote(source, sourceIsDigests, ct);
        if (note is null) return heuristic;                       // total LLM failure → heuristic floor
        if (IsNothing(note.Summary) && note.KeyPoints.Count == 0 && note.Actions.Count == 0)
            return LowSubstanceNote();                            // model judged it low-substance

        var result = new NoteSet
        {
            Tldr = string.IsNullOrWhiteSpace(note.Summary) ? heuristic.Tldr : note.Summary,
            Decisions = note.KeyPoints,   // "Decisions" column is rendered as KEY POINTS; empty → section dropped
            Actions = note.Actions,       // empty → section dropped
            Questions = [],
        };
        result.Title = await TitlePass(result.Tldr, ct);
        return result;
    }

    // ── MAP: one window → a compact factual digest (or NOTHING) ──
    async Task<string?> MapChunk(string chunk, int part, int total, CancellationToken ct)
    {
        var prompt =
            $"This is part {part} of {total} of a meeting transcript. In 2 to 5 short dash bullets, list ONLY the "
            + "concrete things in THIS part: topics discussed, any decisions, any tasks/follow-ups (with who and "
            + "when if stated), and important facts. Capture real content even if the talk is informal, rambling, "
            + "or in fragments. Do not editorialise.\n"
            + $"Reply with exactly {NothingMarker} ONLY if this part is purely greetings, small talk, or "
            + "connection/audio checks with no real topic at all.\n\n"
            + $"Transcript part:\n{chunk}";
        return await TextPass(prompt, 220, ct);
    }

    // ── REDUCE / FINAL: adaptive, honest note ──
    async Task<FinalSections?> FinalNote(string content, bool contentIsDigests, CancellationToken ct)
    {
        var what = contentIsDigests ? "these notes from consecutive parts of one meeting" : "this meeting transcript";
        var prompt =
            $"Write the notes for {what}. First judge how much real substance the meeting had.\n\n"
            + "Output ONLY the sections that genuinely apply, each header on its own line:\n\n"
            + "SUMMARY\n"
            + "2 to 4 plain-English sentences on what the meeting was about and what came of it. Capture what was "
            + "genuinely discussed even if the talk was informal, wandering, or a working session — never inflate it, "
            + "but never dismiss real content either.\n\n"
            + "KEY POINTS\n"
            + "- up to 5 short bullets on the main topics, facts, or decisions. Include this section ONLY if there "
            + "are real points worth listing.\n\n"
            + "ACTION ITEMS\n"
            + "- concrete follow-up tasks only, one per line as: task | owner: name | due: when (drop owner or due "
            + "when not stated). Include this section ONLY if real tasks were actually agreed.\n\n"
            + "Hard rules: never invent decisions, tasks, or points that aren't clearly present. Omit any section "
            + $"that does not apply — a short, honest note is correct. Reply with exactly {NothingMarker} ONLY if the "
            + "ENTIRE thing is greetings, small talk, or connection checks with no real topic discussed at all.\n\n"
            + $"{(contentIsDigests ? "Notes" : "Transcript")}:\n{content}";

        var raw = await TextPass(prompt, 420, ct);
        if (raw is null) return null;
        if (IsNothing(raw)) return new FinalSections { Summary = NothingMarker };

        var (kp, ai, summary) = SplitSections(raw);
        return new FinalSections
        {
            Summary = summary,
            KeyPoints = kp,
            Actions = ai.Count > 0 ? ParseActions(ai) : [],
        };
    }

    sealed class FinalSections
    {
        public string Summary { get; set; } = "";
        public List<string> KeyPoints { get; set; } = [];
        public List<ActionItem> Actions { get; set; } = [];
    }

    // An honest, minimal note for a meeting where nothing substantive happened — the opposite of
    // padding nothing into a serious-looking paragraph.
    static NoteSet LowSubstanceNote() => new()
    {
        Tldr = "A brief exchange, mostly greetings and logistics. Nothing substantive was discussed, so there's little to note.",
        Decisions = [],
        Actions = [],
        Questions = [],
        // No Title → NoteExtractor.Title falls back to "<app> — <date>" (honest, not a fake headline).
    };

    // ── title ──
    async Task<string?> TitlePass(string summary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summary)) return null;
        var raw = await TextPass(
            "Write a title for this meeting: 3 to 6 words, specific and concise, like a calendar event. "
            + "No quotes, no ending punctuation, no \"Meeting\" prefix.\n\nSummary: " + summary, 24, ct);
        if (raw is null) return null;
        var t = raw.Split('\n')[0].Trim().Trim('"', '\'', '.', ':', '-', ' ');
        if (t.Length < 3) return null;
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 8) t = string.Join(' ', words.Take(8));
        return t;
    }

    // ── chunking (split at segment boundaries, never mid-utterance) ──

    /// <summary>Context budget for a chunk, in characters, sized from the model's context window.</summary>
    static int MaxChunkChars()
    {
        var ctx = (int)LlmRuntime.ContextSize;
        // Reserve ~800 tokens for the prompt scaffolding + generated digest; ~3 chars/token (conservative).
        return Math.Max(1800, (ctx - 800) * 3);
    }

    /// <summary>Group whole segments into windows under <paramref name="maxChars"/> each.</summary>
    static List<string> Chunk(IReadOnlyList<TranscriptSegment> transcript, int maxChars)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var seg in transcript)
        {
            var line = $"{(seg.Speaker == Speaker.You ? "You" : "Them")}: {seg.Text}";
            if (sb.Length > 0 && sb.Length + line.Length + 1 > maxChars)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }

    // ── section parsing (shared) ──

    static (List<string> KeyPoints, List<string> Actions, string Summary) SplitSections(string raw)
    {
        var keyPoints = new List<string>();
        var actions = new List<string>();
        var summary = new StringBuilder();
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

    // ── passes (validated, retried once, else null → caller falls back) ──

    async Task<string?> TextPass(string user, int maxTokens, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string outp;
            try { outp = (await _llm.CompleteAsync(System, user, maxTokens, ct)).Trim(); }
            catch (OperationCanceledException) { return null; }
            catch { return null; }
            if (!TextValidator.IsDegenerate(outp)) return outp;
        }
        return null;
    }

    // A window/note the model reported nothing for ("NOTHING", "none", "(nothing substantive)"...).
    static bool IsNothing(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;
        var t = s.Trim().TrimStart('-', '*', '•', '(', ' ').TrimEnd(')', '.', ' ').ToLowerInvariant();
        return t is "nothing" or "none" or "n/a" or "na"
            || t.StartsWith("nothing substantive") || t.StartsWith("nothing of substance");
    }

    static bool IsEmptyMarker(string v)
    {
        var s = v.Trim().Trim('<', '>', '(', ')', '[', ']').Trim().ToLowerInvariant();
        return s is "" or "-" or "--" or "->" or "—" or "n/a" or "na" or "none"
            or "tbd" or "tba" or "unknown" or "unassigned" or "name" or "when"
            or "no owner" or "no due" or "no due date"
            or "not specified" or "not stated" or "not mentioned";
    }
}
