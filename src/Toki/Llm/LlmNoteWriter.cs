using System.Text;
using Toki.Storage;
using Toki.Transcription;

namespace Toki.Llm;

/// <summary>
/// Writes meeting notes with a local LLM - MAP-REDUCE and ADAPTIVE.
///
/// Why map-reduce: the local model's context is only a few thousand tokens. A single
/// pass over a 25-minute transcript overruns it, the call throws, and we fall back to
/// the raw-first-lines heuristic (the "summary is just the opening sentences" bug). So
/// instead we split the transcript at speaker/segment boundaries into context-safe
/// windows, "map" each window to a compact factual digest, then "reduce" the digests
/// into the final note. Summarising summaries never overflows - a 2-hour meeting works
/// as well as a 5-minute one.
///
/// Why adaptive + honest: not every meeting has decisions, tasks, or goals. The reduce
/// step judges the meeting's substance and emits ONLY the sections that genuinely apply
/// (empty sections are dropped by the renderer). A casual or inconclusive meeting gets a
/// one-line honest note - never corporate filler manufactured to look important. This is
/// the Granola/Fireflies principle: pick the shape that fits, don't force a template.
/// </summary>
public sealed class LlmNoteWriter
{
    const string System =
        "You are a meeting-notes assistant. Respond in English only. Be concise, factual, and honest. "
        + "Output ONLY what is asked - no preamble, no explanations, no markdown headings, no 'None'. "
        + "Never use em dashes; write a plain hyphen, a comma, or a new sentence instead.";

    // A window that a chunk digest / final pass will report nothing for.
    const string NothingMarker = "NOTHING";

    readonly ILlm _llm;
    public LlmNoteWriter(ILlm llm) => _llm = llm;

    /// <remarks>
    /// The transcript is the ONLY input. An earlier version fed the user's own sticky note in as a
    /// steering constraint; the owner removed it (2026-08-06) and the reasoning is worth keeping: the
    /// summary's job is to say what was said in the meeting, and a summary that reshapes itself around
    /// your private jottings is one you can't check the room against any more. The sticky note now sits
    /// BESIDE the summary in the same note, untouched and untouching.
    /// </remarks>
    public async Task<NoteSet> WriteAsync(IReadOnlyList<TranscriptSegment> transcript,
        CancellationToken ct = default, NoteTemplate? force = null)
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

        // Use the caller's forced template (manual override), else pick the one that fits this meeting.
        var template = force ?? await ClassifyTemplate(source, sourceIsDigests, ct);

        // REDUCE / FINAL: the adaptive, honest note in the chosen template.
        var note = await FinalNote(source, sourceIsDigests, template, ct);
        if (note is null) return heuristic;                  // total LLM failure → heuristic floor
        if (IsNothing(note.Summary) && note.Sections.Count == 0 && note.Actions.Count == 0)
            return LowSubstanceNote();                       // model judged it low-substance

        var result = new NoteSet
        {
            Template = template.Name,
            Tldr = string.IsNullOrWhiteSpace(note.Summary) ? heuristic.Tldr : note.Summary,
            Sections = note.Sections,     // template middle sections (empty ones already dropped)
            Actions = note.Actions,       // empty → section dropped
        };
        // The combined pass already produced the title in the SAME call - only fall back to a
        // separate title generation (an extra ~round-trip) if it somehow didn't.
        result.Title = !string.IsNullOrWhiteSpace(note.Title) ? note.Title : await TitlePass(result.Tldr, ct);
        return result;
    }

    // ── MAP: one window → a compact factual digest (or NOTHING) ──
    async Task<string?> MapChunk(string chunk, int part, int total, CancellationToken ct)
    {
        var prompt =
            $"This is part {part} of {total} of a meeting transcript. In 2 to 5 short dash bullets, list ONLY the "
            + "concrete things in THIS part: topics discussed, any decisions (with the stated reason, if one was given), "
            + "any tasks/follow-ups (keeping the exact NAME of whoever the task was given to, and the due date, when "
            + "stated), and important facts. The text is auto-transcribed speech - read past obvious mis-recognitions "
            + "using context, but never invent or \"correct\" a name: keep names exactly as transcribed. Capture "
            + "real content even if the talk is informal, rambling, or in fragments. Cut filler, greetings, and "
            + "logistics. Do not editorialise.\n"
            + $"Reply with exactly {NothingMarker} ONLY if this part is purely greetings, small talk, or "
            + "connection/audio checks with no real topic at all.\n\n"
            + $"Transcript part:\n{chunk}";
        return await TextPass(prompt, 220, ct);
    }

    // ── template classification: which note shape fits this meeting ──
    async Task<NoteTemplate> ClassifyTemplate(string source, bool isDigests, CancellationToken ct)
    {
        var prompt =
            "Pick the single best note template for this meeting from the list below. "
            + "Reply with ONLY the template name, exactly as written - nothing else.\n\n"
            + NoteTemplates.Menu() + "\n\n"
            + (isDigests ? "Notes:\n" : "Transcript:\n") + source;
        return Pick(await TextPass(prompt, 12, ct));
    }

    static NoteTemplate Pick(string? raw)
    {
        var s = (raw ?? "").ToLowerInvariant();
        // Longest name first, so "Decision log" wins over an incidental "1:1" substring, etc.
        foreach (var t in NoteTemplates.All.OrderByDescending(t => t.Name.Length))
            if (s.Contains(t.Name.ToLowerInvariant())) return t;
        return NoteTemplates.General;
    }

    // ── REDUCE / FINAL: adaptive, honest note in the chosen template ──
    async Task<FinalNoteResult?> FinalNote(string content, bool contentIsDigests, NoteTemplate template,
        CancellationToken ct)
    {
        var what = contentIsDigests ? "these notes from consecutive parts of one meeting" : "this meeting transcript";

        var sectionSpec = string.Join("\n\n", template.Sections.Select(s =>
            $"{s.ToUpperInvariant()}\n- up to 5 tight bullets for \"{s}\". Every bullet must carry information a busy "
            + "person needs - drop anything vague, obvious, or logistical. Include this section ONLY if real content for it exists."));

        var prompt =
            $"Write the notes for {what}, in the \"{template.Name}\" format. First judge how much REAL substance the meeting had.\n\n"
            + "Output ONLY the sections that genuinely apply, each header on its own line:\n\n"
            + "TITLE\n3 to 6 words, specific and concise, like a calendar event - no quotes, no \"Meeting\" prefix.\n\n"
            + "SUMMARY\n2 to 4 dense, plain-English sentences: what the meeting was about and what came of it. Capture "
            + "real content from informal or wandering talk, but cut ALL filler.\n\n"
            + sectionSpec + "\n\n"
            + "ACTION ITEMS\n"
            + "- concrete follow-up tasks only, one per line as: task | owner: name | due: when\n"
            + "  - owner = the EXACT name of the person the task was given to, as spoken (e.g. \"Priya\"). If the "
            + "speaker took it on themselves, use owner: You. If no person is clearly named, DROP the owner field - never guess.\n"
            + "  - drop the due field when no time was stated.\n"
            + "  Include this section ONLY if real tasks were actually agreed.\n\n"
            + "Hard rules: never invent points, tasks, or names that aren't clearly present. When you record a decision, "
            + "pair it with the reason that was given for it (skip the reason if none was stated - never invent one). "
            + "The text is auto-transcribed speech: read past obvious mis-recognitions using context, but keep names "
            + "exactly as transcribed. A short, honest note beats "
            + $"a padded one - omit any section that doesn't apply. Reply with exactly {NothingMarker} ONLY if the ENTIRE "
            + "thing is greetings, small talk, or connection checks with no real topic at all.\n\n"
            + $"{(contentIsDigests ? "Notes" : "Transcript")}:\n{content}";

        var raw = await TextPass(prompt, 480, ct);
        if (raw is null) return null;
        if (IsNothing(raw)) return new FinalNoteResult { Summary = NothingMarker };

        var (title, summary, sections, actionLines) = ParseNote(raw, template);
        return new FinalNoteResult
        {
            Title = CleanTitle(title),
            Summary = summary,
            Sections = sections,
            Actions = actionLines.Count > 0 ? ParseActions(actionLines) : [],
        };
    }

    sealed class FinalNoteResult
    {
        public string? Title { get; set; }
        public string Summary { get; set; } = "";
        public List<NoteSection> Sections { get; set; } = [];
        public List<ActionItem> Actions { get; set; } = [];
    }

    /// <summary>The ONE title sanitizer (first line, quote-strip, 8-word cap) - CleanTitle and
    /// TitlePass both funnel here so a future validation tweak can never land in one and not
    /// the other (the combined pass is primary and the fallback is rarely exercised, so a
    /// divergence would look fixed in review while never running in production).</summary>
    static string? SanitizeTitle(string raw)
    {
        var t = raw.Split('\n')[0].Trim().Trim('"', '\'', '.', ':', '-', ' ');
        if (t.Length < 3) return null;
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 8 ? string.Join(' ', words.Take(8)) : t;
    }

    static string? CleanTitle(string raw) => SanitizeTitle(raw);

    // An honest, minimal note for a meeting where nothing substantive happened - the opposite of
    // padding nothing into a serious-looking paragraph.
    static NoteSet LowSubstanceNote() => new()
    {
        Tldr = "A brief exchange, mostly greetings and logistics. Nothing substantive was discussed, so there's little to note.",
        Decisions = [],
        Actions = [],
        Questions = [],
        // No Title → NoteExtractor.Title falls back to "<app> - <date>" (honest, not a fake headline).
    };

    // ── title ──
    async Task<string?> TitlePass(string summary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summary)) return null;
        var raw = await TextPass(
            "Write a title for this meeting: 3 to 6 words, specific and concise, like a calendar event. "
            + "No quotes, no ending punctuation, no \"Meeting\" prefix.\n\nSummary: " + summary, 24, ct);
        if (raw is null) return null;
        return SanitizeTitle(raw);
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

    // ── section parsing (template-aware) ──
    // Recognises TITLE / SUMMARY / ACTION ITEMS plus the chosen template's section headings; every
    // other non-empty line is a bullet under the current section. Empty sections are dropped.
    static (string Title, string Summary, List<NoteSection> Sections, List<string> ActionLines)
        ParseNote(string raw, NoteTemplate template)
    {
        var buckets = template.Sections.ToDictionary(s => s, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var title = new StringBuilder();
        var summary = new StringBuilder();
        var actions = new List<string>();
        string cur = "";   // "title" | "summary" | "actions" | a template section heading | ""

        foreach (var lineRaw in raw.Split('\n'))
        {
            var line = lineRaw.Trim();
            if (line.Length == 0) continue;

            var head = line.Trim('*', '#', ':', '-', ' ', '\t');
            // Template sections take precedence (so a heading the template defines is never mistaken
            // for an action-items synonym), then the three fixed headers.
            var sec = template.Sections.FirstOrDefault(s => string.Equals(s, head, StringComparison.OrdinalIgnoreCase));
            if (sec is not null) { cur = sec; continue; }
            var special = SpecialHeader(head);
            if (special is not null) { cur = special; continue; }
            if (line.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;

            var clean = line.TrimStart('-', '*', '•', ' ', '\t').Trim();
            switch (cur)
            {
                case "title": if (title.Length == 0) title.Append(clean); break;   // first line only
                case "summary": if (summary.Length > 0) summary.Append(' '); summary.Append(clean); break;
                case "actions": if (clean.Length > 1) actions.Add(clean); break;
                default: if (clean.Length > 1 && buckets.TryGetValue(cur, out var b)) b.Add(clean); break;
            }
        }

        var sections = template.Sections
            .Where(s => buckets[s].Count > 0)
            .Select(s => new NoteSection { Heading = s, Bullets = buckets[s] })
            .ToList();
        return (title.ToString().Trim(), summary.ToString().Trim(), sections, actions);
    }

    static string? SpecialHeader(string head)
    {
        var h = head.ToLowerInvariant();
        if (h is "title") return "title";
        if (h is "summary" or "tl;dr" or "tldr" or "overview") return "summary";
        if (h is "action items" or "action item" or "actions" or "to-dos" or "todos" or "to dos" or "next steps") return "actions";
        return null;
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
            // Every line of every written note funnels through here, so this is the one place that has
            // to turn the model's em dashes into plain hyphens (owner, 2026-08-06). Asking in the prompt
            // is not enough on its own - a 4B model still slips one in, and the one it slips in is the
            // one the user reads. IsEmptyMarker still recognises a bare "-", so nothing downstream breaks.
            outp = GeneralChat.PlainDashes(outp);
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
