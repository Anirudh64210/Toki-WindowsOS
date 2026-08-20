using System.Text;
using System.Text.RegularExpressions;
using Toki.Storage;
using Toki.Transcription;

namespace Toki.Llm;

/// <summary>
/// Writes meeting notes with a local LLM - MAP-REDUCE and ADAPTIVE. The local context is only a
/// few thousand tokens, so the transcript is split at segment boundaries into context-safe
/// windows, each mapped to a compact digest, then the digests are reduced into the final note -
/// summarising summaries never overflows. Adaptive and honest: the reduce step emits ONLY the
/// sections that genuinely apply; a meeting with no substance gets a one-line honest note,
/// never filler manufactured to look important.
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
    /// The transcript is the ONLY input. The user's sticky note is deliberately never fed in:
    /// a summary that reshapes itself around private jottings can no longer be checked against
    /// what was actually said. The sticky note sits BESIDE the summary, untouched and untouching.
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
            // FAILURE and NOTHING are different answers and must stay different: a window whose
            // decode failed says nothing about the meeting; a window the model read and judged
            // empty does. Folding them together turns a mid-map timeout into an invented
            // "nothing was discussed" verdict.
            var digests = new List<string>();
            var failed = 0;
            for (var i = 0; i < chunks.Count; i++)
            {
                var d = await MapChunk(chunks[i], i + 1, chunks.Count, ct);
                if (d is null) { failed++; continue; }
                if (!string.IsNullOrWhiteSpace(d) && !IsNothing(d)) digests.Add(d);
            }
            if (digests.Count == 0)
                return failed > 0 ? heuristic          // could not read the meeting → the floor
                                  : LowSubstanceNote(); // read it all and found nothing → honest minimal note
            source = string.Join("\n", digests);
            sourceIsDigests = true;

            // If even the DIGESTS overflow the final pass's context, map again over the digest
            // text (up to two extra rounds) - otherwise a very long meeting throws in the final
            // pass and falls back to raw-first-lines.
            for (var round = 0; round < 2 && source.Length > MaxChunkChars(); round++)
            {
                App.Log($"notes: digests still {source.Length} chars, reducing again (round {round + 1})");
                var rechunks = ChunkText(source, MaxChunkChars());
                var redigests = new List<string>();
                for (var i = 0; i < rechunks.Count; i++)
                {
                    var d = await MapChunk(rechunks[i], i + 1, rechunks.Count, ct);
                    if (!string.IsNullOrWhiteSpace(d) && !IsNothing(d)) redigests.Add(d!);
                }
                if (redigests.Count == 0) break;   // keep the level that worked
                source = string.Join("\n", redigests);
            }
        }

        // Use the caller's forced template (a human assertion, never second-guessed), else pick the one
        // that fits - and then make the TRANSCRIPT confirm it. A template is a claim about the meeting,
        // and an unearned one manufactures content to fill its sections. See TemplateGate.
        var template = force ?? TemplateGate.Verify(
            await ClassifyTemplate(source, sourceIsDigests, ct), transcript);

        // What was actually SAID, for validating owners. In map-reduce mode `source` is the model's
        // own digests, and a name the map pass invented would be "found" there by construction -
        // the owner gate has to check against the one text the model cannot have written.
        var spoken = string.Join("\n", transcript.Select(s => s.Text));

        // REDUCE / FINAL: the adaptive, honest note in the chosen template.
        var note = await FinalNote(source, sourceIsDigests, template, spoken, ct);
        if (note is null) return heuristic;                  // total LLM failure → heuristic floor
        if (IsNothing(note.Summary) && note.Sections.Count == 0 && note.Actions.Count == 0)
            return LowSubstanceNote();                       // model judged it low-substance

        // The model sometimes returns good sections and simply skips SUMMARY. The heuristic
        // summary (raw first lines) is not a patch for a model that answered: ask again, just for
        // the summary. If that fails too, leave it EMPTY - the renderer drops the section, and a
        // note with no summary is honest where raw transcript is not.
        var summary = note.Summary;
        if (string.IsNullOrWhiteSpace(summary) || IsNothing(summary))
            summary = await SummaryPass(source, sourceIsDigests, ct) ?? "";

        var result = new NoteSet
        {
            FromModel = true,             // the model actually answered - callers may replace notes with this
            Template = template.Name,
            Tldr = summary,
            Sections = note.Sections,     // template middle sections (empty ones already dropped)
            Actions = note.Actions,       // empty → section dropped
        };
        // The combined pass already produced the title in the SAME call - only fall back to a
        // separate title pass if it somehow didn't. Titling from the SUMMARY (never the raw
        // transcript opening) keeps the title on the subject; with no summary, title from the sections.
        result.Title = !string.IsNullOrWhiteSpace(note.Title) ? note.Title
            : await TitlePass(summary.Length > 0 ? summary : SectionDigest(result), ct);
        return result;
    }

    /// <summary>A second, narrower ask for just the summary, used when the combined pass skipped it.</summary>
    async Task<string?> SummaryPass(string content, bool contentIsDigests, CancellationToken ct)
    {
        var raw = await TextPass(
            "In 2 to 4 dense, plain-English sentences, say what this meeting was about and what came of "
            + "it. Capture real content from informal or wandering talk, but cut ALL filler, greetings "
            + "and logistics. Reply with the sentences only - no heading, no bullets, no preamble.\n\n"
            + (contentIsDigests ? "Notes:\n" : "Transcript:\n") + content, 200, ct);
        return raw is null || IsNothing(raw) ? null : raw.Trim();
    }

    /// <summary>What the note managed to say, for titling when there is no summary to title from.</summary>
    static string SectionDigest(NoteSet note)
    {
        var bits = note.Sections.SelectMany(s => s.Bullets)
            .Concat(note.Actions.Select(a => a.Text))
            .Take(8);
        return string.Join(". ", bits);
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
            + "A specific template only fits when that IS the meeting's main purpose and the text shows "
            + "it plainly. Discussing a choice is not a decision; mentioning a task is not a standup. "
            + "When in doubt, answer General. "
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
        string spoken, CancellationToken ct)
    {
        var what = contentIsDigests ? "these notes from consecutive parts of one meeting" : "this meeting transcript";

        var sectionSpec = string.Join("\n\n", template.Sections.Select(s =>
            $"{s.ToUpperInvariant()}\n- up to 5 tight bullets for \"{s}\". Every bullet must carry information a busy "
            + "person needs - drop anything vague, obvious, or logistical. Include this section ONLY if real content for it exists. "
            + "If filling it would need you to infer, justify, or fill a gap, LEAVE THE WHOLE SECTION OUT - an "
            + "absent section is correct, an invented one is not."));

        var prompt =
            $"Write the notes for {what}, in the \"{template.Name}\" format. First judge how much REAL substance the meeting had.\n\n"
            + "Output ONLY the sections that genuinely apply, each header on its own line:\n\n"
            + "TITLE\n3 to 6 words, specific and concise, like a calendar event - no quotes, no \"Meeting\" prefix.\n\n"
            + "SUMMARY\n2 to 4 dense, plain-English sentences: what the meeting was about and what came of it. Capture "
            + "real content from informal or wandering talk, but cut ALL filler.\n\n"
            + sectionSpec + "\n\n"
            + "ACTION ITEMS\n"
            + "- concrete follow-up tasks only, one per line as: task | owner: name | due: date\n"
            + "  - owner = the EXACT name of the person the task was given to, as spoken (e.g. \"Priya\"). If the "
            + "speaker took it on themselves, use owner: You. If no person is clearly named, OMIT the whole \"| owner:\" part - never guess.\n"
            + "  - due = the stated deadline (e.g. \"Friday\", \"Aug 21\"). If no time was stated, OMIT the whole \"| due:\" part.\n"
            + "  Include this section ONLY if real tasks were actually agreed.\n\n"
            + "Hard rules: never invent points, tasks, or names that aren't clearly present. When you record a decision, "
            + "pair it with the reason that was given for it (skip the reason if none was stated - never invent one). "
            + "The text is auto-transcribed speech: read past obvious mis-recognitions using context, but keep names "
            + "exactly as transcribed. A short, honest note beats "
            + $"a padded one - omit any section that doesn't apply. Reply with exactly {NothingMarker} ONLY if the ENTIRE "
            + "thing is greetings, small talk, or connection checks with no real topic at all.\n\n"
            + $"{(contentIsDigests ? "Notes" : "Transcript")}:\n{content}";

        // Validate by PARSING, and retry once on a miss: a generation can be perfectly fluent and
        // still land in no recognised section - a formatting miss, not a verdict about the
        // meeting - and at sampling temperature the second ask is a genuinely different answer.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var ask = attempt == 0 ? prompt : prompt
                + "\n\nIMPORTANT: use the EXACT section headers named above (TITLE, SUMMARY, "
                + string.Join(", ", template.Sections.Select(s => s.ToUpperInvariant()))
                + ", ACTION ITEMS), each alone on its own line, never as a bullet.";
            var raw = await TextPass(ask, 480, ct);
            if (raw is null) return null;
            if (IsNothing(raw)) return new FinalNoteResult { Summary = NothingMarker };

            var (title, summary, sections, actionLines) = ParseNote(raw, template);
            bool Empty() => title.Length == 0 && summary.Length == 0
                && sections.Count == 0 && actionLines.Count == 0;

            // Some runs write the whole note as one bullet list, headers included - give the
            // strict parser a second look with those header-bullets un-bulleted.
            if (Empty() && raw.Trim().Length > 80)
                (title, summary, sections, actionLines) = ParseNote(PromoteBulletHeaders(raw, template), template);

            // The model answered with SUBSTANCE but none of it landed in a recognised header even
            // after the rescue. An empty result would read upstream as "low substance" and stamp
            // a real meeting with the canned nothing-happened note; retry, then null sends the
            // caller to the heuristic floor. Log WHAT it wrote, or the failure is undiagnosable.
            if (Empty() && raw.Trim().Length > 80)
            {
                App.Log($"notes: {raw.Trim().Length} chars of model output matched no header"
                    + (attempt == 0 ? " - retrying with a format reminder" : " - falling back")
                    + $" | raw: \"{Excerpt(raw)}\"");
                continue;
            }

            return new FinalNoteResult
            {
                Title = CleanTitle(title),
                Summary = summary,
                Sections = sections,
                // Owners are validated against what was SAID, never against model-written digests.
                Actions = actionLines.Count > 0 ? ParseActions(actionLines, spoken) : [],
            };
        }
        return null;
    }

    /// <summary>A one-line, bounded slice of model output for the log - enough to diagnose a
    /// parse failure without dumping the whole generation.</summary>
    static string Excerpt(string s)
    {
        var t = Regex.Replace(s.Trim(), @"\s*\r?\n\s*", " \\n ");
        return t.Length <= 240 ? t : t[..240] + "...";
    }

    /// <summary>
    /// Rescue for output the strict parser matched NOTHING in: the model sometimes formats the
    /// whole note as one bullet list, headers included ("- SUMMARY: ...", "- ACTION ITEMS:"), so
    /// every line reads as content with no section open and the entire answer is dropped. The
    /// strict parser must keep refusing bullets as headers (a bullet whose text happens to open
    /// "Next steps:" would hijack the actions parser) - but when strictness has already yielded
    /// nothing there is nothing left to protect. Un-bullet exactly the lines that match a known
    /// header and let the same strict parser try again.
    /// </summary>
    internal static string PromoteBulletHeaders(string raw, NoteTemplate template)
    {
        var lines = raw.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length < 2 || line[0] is not ('-' or '*' or '•') || line[1] == line[0]) continue;
            var body = line.TrimStart('-', '*', '•', ' ', '\t');
            if (MatchHeader(HeadOf(body).Head, template) is not null) lines[i] = body;
        }
        return string.Join("\n", lines);
    }

    sealed class FinalNoteResult
    {
        public string? Title { get; set; }
        public string Summary { get; set; } = "";
        public List<NoteSection> Sections { get; set; } = [];
        public List<ActionItem> Actions { get; set; } = [];
    }

    /// <summary>The ONE title sanitizer (first line, quote-strip, 8-word cap). CleanTitle and
    /// TitlePass both funnel here so a validation tweak can never land in one and not the other.</summary>
    static string? SanitizeTitle(string raw)
    {
        var t = raw.Split('\n')[0].Trim().Trim('"', '\'', '.', ':', '-', ' ');
        if (t.Length < 3) return null;
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 8 ? string.Join(' ', words.Take(8)) : t;
    }

    static string? CleanTitle(string raw) => SanitizeTitle(raw);

    /// <summary>The canned low-substance summary. Referenced by NoteExtractor.Title so the title
    /// logic can recognise it and fall back to "app - date" instead of titling every trivial
    /// meeting "A brief exchange, mostly greetings and logistics".</summary>
    internal const string LowSubstanceTldr =
        "A brief exchange, mostly greetings and logistics. Nothing substantive was discussed, so there's little to note.";

    static NoteSet LowSubstanceNote() => new()
    {
        FromModel = true,   // this IS the model's verdict, not a failure
        Tldr = LowSubstanceTldr,
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

    /// <summary>Split plain TEXT (already-made digests) into windows at line boundaries, for the
    /// second reduction round of a very long meeting.</summary>
    static List<string> ChunkText(string text, int maxChars)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
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

    /// <summary>Group whole segments into windows under <paramref name="maxChars"/> each.</summary>
    static List<string> Chunk(IReadOnlyList<TranscriptSegment> transcript, int maxChars)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var seg in transcript)
        {
            // Label, not the raw stream: once the speakers have been told apart the writer sees real
            // names, which is what lets it attribute a to-do instead of guessing. On a fresh meeting
            // diarisation has not run yet, so this is still "You"/"Them" - the names arrive on a rewrite.
            var line = $"{seg.Label}: {seg.Text}";
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

            // A line that BEGINS as a bullet is content, never a header - a bullet whose text
            // happens to read "Next steps" must not flip every bullet after it into the actions
            // parser. (Markdown bold and "#"-style headers are still stripped below.)
            var isBullet = line[0] is '-' or '*' or '•' && !(line.Length > 1 && line[1] == line[0]);
            var (head, inline) = HeadOf(line);

            if (!isBullet)
            {
                var sec = MatchHeader(head, template);
                if (sec is not null)
                {
                    cur = sec;
                    if (inline.Length > 1)
                        switch (cur)
                        {
                            case "title": if (title.Length == 0) title.Append(inline); break;
                            case "summary": summary.Append(inline); break;
                            case "actions": actions.Add(inline); break;
                            default: if (buckets.TryGetValue(cur, out var ib)) ib.Add(inline); break;
                        }
                    continue;
                }
            }
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

    /// <summary>Header-shape of a line: markdown/number decoration stripped, an inline
    /// "TITLE: Weekly sync" pair split at the colon (the old parser saw one un-matched line,
    /// stayed in no section, and dropped the whole thing). ONE definition, shared by the strict
    /// parser and the bullet-header rescue, so the two can never disagree about what a header
    /// looks like.</summary>
    static (string Head, string Inline) HeadOf(string line)
    {
        var head = line.Trim('*', '#', ':', '-', ' ', '\t');
        // Models number their headers ("1. ACTION ITEMS") - strip a leading list number.
        head = Regex.Replace(head, @"^\d{1,2}[.)]\s*", "");
        var inline = "";
        var colon = head.IndexOf(':');
        if (colon > 0 && colon < 24) { inline = head[(colon + 1)..].Trim(); head = head[..colon].Trim(); }
        return (head, inline);
    }

    /// <summary>The canonical section a header-shaped line opens, or null. Template sections take
    /// precedence (so a heading the template defines is never mistaken for an action-items
    /// synonym), then the three fixed headers.</summary>
    static string? MatchHeader(string head, NoteTemplate template)
        => template.Sections.FirstOrDefault(s => string.Equals(s, head, StringComparison.OrdinalIgnoreCase))
           ?? SpecialHeader(head);

    static string? SpecialHeader(string head)
    {
        var h = head.ToLowerInvariant();
        if (h is "title") return "title";
        if (h is "summary" or "tl;dr" or "tldr" or "overview") return "summary";
        // Every synonym a small model has actually used for the actions section. A missed synonym
        // is not cosmetic: the raw "task | owner: x" lines land in whatever section is open -
        // usually the summary - unparsed and unvalidated.
        if (h is "action items" or "action item" or "actions" or "to-dos" or "todos" or "to dos"
            or "next steps" or "follow-ups" or "follow ups" or "followups" or "follow-up"
            or "tasks" or "task list" or "open items" or "to-do list" or "to do list") return "actions";
        return null;
    }

    static List<ActionItem> ParseActions(List<string> lines, string source)
    {
        // ACTION ITEMS is the last thing the model writes, so the token cap lands here. A line the
        // cap cut mid-field ("... | owner: Pri") would otherwise ship as a real task with a
        // truncated name or a one-digit "due". If the FINAL line ends in a dangling field, drop
        // that line - a missing task is recoverable from the transcript, a garbled one is noise.
        if (lines.Count > 0)
        {
            var last = lines[^1].TrimEnd();
            // A 1-2 char field value is a truncation UNLESS it is the model's deliberate
            // empty-marker ("owner: -", "due: ->"), which the field parser below handles.
            var frag = Regex.Match(last, @"\|\s*(owner|due)\s*:\s*(?<v>\S{0,2})$", RegexOptions.IgnoreCase);
            var cutField = last.EndsWith("|")
                || Regex.IsMatch(last, @"\|\s*(owner|due)?\s*$", RegexOptions.IgnoreCase)
                || (frag.Success && !IsEmptyMarker(frag.Groups["v"].Value));
            if (cutField) lines = lines.Take(lines.Count - 1).ToList();
        }

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
                if (key == "owner") item.Owner = ValidOwner(val, source);
                else if (key == "due") item.Due = LooksLikeWhen(val) ? val : null;
            }
            if (!IsRealTask(item.Text)) continue;
            if (actions.Any(a => SameTask(a.Text, item.Text))) continue;   // said twice, not two tasks
            actions.Add(item);
        }
        return actions;
    }

    /// <summary>
    /// A due value the note may show. The model sometimes echoes the prompt's instruction instead
    /// of obeying it ("due: when no time stated") - a prompt is a request; this is the enforcement.
    /// A due has to LOOK like a time (a digit, day, month, or relative word) and be date-short.
    /// </summary>
    internal static bool LooksLikeWhen(string due)
    {
        var d = due.Trim();
        if (d.Length is < 2 or > 40) return false;
        var low = " " + d.ToLowerInvariant() + " ";
        // Instruction echoes and non-answers, whatever else they contain.
        string[] never = ["no time", "not stated", "unstated", "unspecified", "not mentioned",
            "no date", "unknown", "unclear", "if stated", "when stated", "when no", "no deadline"];
        if (never.Any(low.Contains)) return false;

        // Time WORDS match on word boundaries: "maybe" is not May, "marching orders" is not March.
        string[] when = ["today", "tomorrow", "tonight", "monday", "tuesday", "wednesday", "thursday",
            "friday", "saturday", "sunday", "january", "february", "march", "april", "may", "june",
            "july", "august", "september", "october", "november", "december",
            "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
            "mon", "tue", "tues", "wed", "thu", "thurs", "fri", "sat", "sun",
            "next", "day", "week", "month", "quarter", "end", "eod", "eow", "eom", "noon",
            "midnight", "morning", "afternoon", "evening", "night", "weekend", "asap", "soon"];
        if (when.Any(w => Regex.IsMatch(low, $@"\b{w}\b"))) return true;

        // Digits qualify only in a DATE or TIME shape - "version 2" and "item 3" are not deadlines.
        return Regex.IsMatch(d, @"\b\d{1,2}(:\d{2})?\s*(am|pm)\b", RegexOptions.IgnoreCase)   // 5pm, 10:30 am
            || Regex.IsMatch(d, @"\b\d{1,2}(st|nd|rd|th)\b", RegexOptions.IgnoreCase)         // the 21st
            || Regex.IsMatch(d, @"\b\d{4}-\d{1,2}-\d{1,2}\b")                                 // 2026-08-21
            || Regex.IsMatch(d, @"\b\d{1,2}[/.]\d{1,2}([/.]\d{2,4})?\b");                     // 21/8, 8/21/26
    }

    /// <summary>
    /// An owner the note may show, or null. A mis-attributed task compounds, so an owner has to
    /// be "You" or a name that actually appears in what was SAID - a name the source never
    /// contains was invented, and an invented owner is worse than no owner.
    /// </summary>
    internal static string? ValidOwner(string owner, string source)
    {
        var o = owner.Trim().TrimEnd('.', ',');
        if (o.Length is < 2 or > 40) return null;
        if (o.Equals("you", StringComparison.OrdinalIgnoreCase)) return "You";
        // Generic role words and pronouns are not a person, however plausible they look in the
        // slot. "Them" matters doubly: every transcript line is labelled "Them:", so a pronoun
        // owner would validate against the labels themselves.
        var first = o.Split(' ')[0].ToLowerInvariant();
        if (first is "name" or "person" or "people" or "team" or "everyone" or "someone" or "somebody"
            or "anyone" or "anybody" or "nobody" or "speaker" or "user" or "attendee" or "participant"
            or "owner" or "the" or "they" or "them" or "he" or "she" or "we" or "us" or "it"
            or "all" or "both" or "i" or "me" or "myself" or "everybody")
            return null;
        // The owner has to appear in the source AS A PHRASE, words adjacent. Checking each word
        // independently let the model weld a real name to a real noun ("Craig Portal") and pass -
        // a person who does not exist, assembled from two words that do.
        var words = o.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
        return Regex.IsMatch(source, $@"\b{string.Join(@"\s+", words)}\b", RegexOptions.IgnoreCase)
            ? o : null;
    }

    /// <summary>
    /// Is this actually something to DO? A task needs an action in it; a roll-call of names
    /// ("Anirud, Bindu.") is people, not jobs.
    /// </summary>
    static bool IsRealTask(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length < 4) return false;

        var words = t.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;

        // One or two words, every one of them capitalised -> a name, or a pair of names.
        if (words.Length <= 2 && words.All(w => char.IsUpper(w[0]))) return false;

        // Nothing but names, however many: still a register, not a task.
        return !words.All(w => char.IsUpper(w[0]) && w.All(char.IsLetter));
    }

    /// <summary>The same job written twice ("connect Abhishek with the team member" / "connect Abhishek
    /// with the person responsible") should be one line, not two.</summary>
    static bool SameTask(string a, string b)
    {
        var wa = Words(a);
        var wb = Words(b);
        if (wa.Count == 0 || wb.Count == 0) return false;
        var shared = wa.Count(wb.Contains);
        return (double)shared / Math.Min(wa.Count, wb.Count) >= 0.75;
    }

    static HashSet<string> Words(string s) =>
        new(s.ToLowerInvariant().Split(new[] { ' ', ',', '.', ';', ':' }, StringSplitOptions.RemoveEmptyEntries)
             .Where(w => w.Length > 2), StringComparer.OrdinalIgnoreCase);

    // ── passes (validated, retried once, else null → caller falls back) ──

    async Task<string?> TextPass(string user, int maxTokens, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string outp;
            try { outp = (await _llm.CompleteAsync(System, user, maxTokens, ct)).Trim(); }
            catch (OperationCanceledException) { return null; }
            catch { return null; }
            // Every line of every written note funnels through here - the one place that turns the
            // model's em dashes into plain hyphens (the prompt alone is not enough; a 4B model
            // still slips one in). IsEmptyMarker still recognises a bare "-".
            outp = GeneralChat.PlainDashes(outp);
            if (!TextValidator.IsDegenerate(outp)) return outp;
        }
        return null;
    }

    // A window/note the model reported nothing for ("NOTHING", "none", "(nothing substantive)"...).
    // The prefix checks are LENGTH-BOUNDED: "Nothing substantive was decided yet, but the team
    // compared three vendors and..." is a digest full of content that happens to open with the
    // marker words, and throwing it away deletes that window from the note.
    static bool IsNothing(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;
        var t = s.Trim().TrimStart('-', '*', '•', '(', ' ').TrimEnd(')', '.', ' ').ToLowerInvariant();
        return t is "nothing" or "none" or "n/a" or "na"
            || (t.Length <= 48 && (t.StartsWith("nothing substantive") || t.StartsWith("nothing of substance")));
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
