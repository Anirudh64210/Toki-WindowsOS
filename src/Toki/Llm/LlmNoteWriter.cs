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
    /// The transcript plus the meeting's own facts (date, length, who was there, what it was
    /// scheduled to be) are the input. The user's sticky note is deliberately never fed in:
    /// a summary that reshapes itself around private jottings can no longer be checked against
    /// what was actually said. The sticky note sits BESIDE the summary, untouched and untouching.
    /// </remarks>
    public async Task<NoteSet> WriteAsync(IReadOnlyList<TranscriptSegment> transcript,
        CancellationToken ct = default, NoteTemplate? force = null, string? about = null)
    {
        var heuristic = NoteExtractor.Extract(transcript);
        if (!_llm.Ready || transcript.Count == 0) return heuristic;

        var chunks = Chunk(transcript, MaxChunkChars());

        // Decide what the final pass reads. One window → read it raw (best fidelity for a short
        // meeting). Many windows → MAP each to a digest first, so the final pass fits in context.
        string source;
        bool sourceIsDigests;
        // Digest text paired with the transcript range it came from. Stays empty when the meeting
        // was written in one pass (nothing to attribute against) or when the digests had to be
        // mapped again, where the ranges stop being trustworthy and are dropped rather than guessed.
        var provenance = new List<Window>();

        if (chunks.Count == 1)
        {
            source = chunks[0].Text;
            sourceIsDigests = false;
        }
        else
        {
            // FAILURE and NOTHING are different answers and must stay different: a window whose
            // decode failed says nothing about the meeting; a window the model read and judged
            // empty does. Folding them together turns a mid-map timeout into an invented
            // "nothing was discussed" verdict.
            var digests = new List<Window>();
            var failed = 0;
            for (var i = 0; i < chunks.Count; i++)
            {
                var d = await MapChunk(chunks[i].Text, i + 1, chunks.Count, ct);
                if (d is null) { failed++; continue; }
                if (string.IsNullOrWhiteSpace(d) || IsNothing(d)) continue;

                // Every point in the digest cites a transcript line; anything whose citation does not
                // hold up is dropped here rather than carried into the note as a fact.
                // A VERIFICATION STEP MUST NEVER DELETE MORE THAN IT VERIFIES. When the model ignores
                // the citation format the window comes back entirely uncited, and dropping it threw
                // away a quarter of a 26-minute meeting - measured, on meeting 48, where two of four
                // windows vanished. Losing the content is far worse than losing the provenance, and
                // an uncited digest is exactly what this pipeline used before, so falling back to it
                // is a return to the old behaviour rather than a new failure.
                var checkedDigest = ValidateDigest(d, transcript, chunks[i].First, chunks[i].Last);
                if (checkedDigest is null)
                {
                    App.Log($"notes: window {i + 1}/{chunks.Count} came back uncited - keeping it, without provenance");
                    // Range -1 marks "text kept, provenance unknown". It stays in the SAME list, so a
                    // later re-reduction can still tell which digests it merged; a parallel list would
                    // drift out of step the moment one window is skipped.
                    digests.Add(new Window(d, -1, -1));
                    continue;
                }
                // The NARROWED range the surviving points actually cover, not the whole window - a
                // tighter provenance is a better evidence link.
                digests.Add(new Window(checkedDigest.Value.Text, checkedDigest.Value.First, checkedDigest.Value.Last));
            }
            if (digests.Count == 0)
            {
                // SAY WHICH ONE. These two outcomes look identical to the user (a poor note) and are
                // opposite faults: one is a broken model, the other is a quiet meeting. Without this
                // line the only symptom of a total map failure is the ABSENCE of other log lines.
                if (failed > 0)
                {
                    App.Log($"notes: ALL {chunks.Count} window(s) failed ({failed} model errors) - falling back to the "
                          + "heuristic floor. The note will be poor; the cause is above, not in the writer.");
                    return heuristic;
                }
                App.Log($"notes: {chunks.Count} window(s) read, none had substance - writing the honest minimal note.");
                return LowSubstanceNote();
            }
            source = string.Join("\n", digests.Select(w => w.Text));
            sourceIsDigests = true;

            // IF THE DIGESTS OVERFLOW, DROP THE WEAKEST LINES. DO NOT SUMMARISE THEM AGAIN.
            //
            // This used to re-map the digests, up to two extra rounds. That reintroduced exactly the
            // failure the map step exists to prevent: the writer ends up reading a paraphrase of a
            // paraphrase. MEASURED 2026-09-03 on meeting 48, whose seven windows mapped cleanly
            // (25/26, 19/21, 29/31, 22/23, 21/22 lines kept) and which then re-reduced once: the note
            // came back with ZERO bullets and its checkable facts fell from 25 to 2, while meeting 28,
            // which did not re-reduce, went from 15% to 62% recall in the same run.
            //
            // Selecting is lossy in a way a reader can reason about; re-summarising is lossy in a way
            // nobody can see. Every surviving line keeps the speaker's own words and its exact
            // provenance, instead of every line becoming a vaguer version of itself.
            if (source.Length > MaxChunkChars())
            {
                var before = source.Length;
                digests = TrimToFit(digests, MaxChunkChars());
                source = string.Join("\n", digests.Select(w => w.Text));
                App.Log($"notes: digests were {before} chars, over the {MaxChunkChars()} budget - "
                      + $"dropped the least informative lines down to {source.Length}. Nothing was re-summarised.");
            }
            provenance.AddRange(digests.Where(w => w.First >= 0));
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
        var note = await FinalNote(source, sourceIsDigests, template, spoken, about, ct, spoken.Length);
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
        // The prompt ASKS for short lines; this ENFORCES them. A model at temperature writes
        // reported speech and hedges however firmly it is told not to, and the same pass runs on
        // the heuristic floor, so both paths produce the same shape of bullet.
        // THE NAME GATE. Everything above this line is the model's account of the meeting; this is
        // the one check that holds it against what was actually said. It runs LAST, on the assembled
        // note, because a name can arrive from the map digest, the final pass or the retry, and
        // gating each of those separately would leave a hole in whichever one got added next.
        var pruned = Grounding.PruneUngroundedNames(result, transcript);
        if (pruned > 0) App.Log($"notes: dropped {pruned} line(s) naming someone absent from the transcript");

        // PROVENANCE, LAST. Attribution runs on the FINAL bullets because NoteSection.Sources is
        // index-aligned with Bullets, and both the name gate above and Brevity.Tighten can drop or
        // rewrite a line. Attributing earlier would leave the ranges pointing one bullet to the left.
        var tightened = Brevity.Tighten(result);
        AttachProvenance(tightened, provenance);
        return tightened;
    }

    /// <summary>A second, narrower ask for just the summary, used when the combined pass skipped it.</summary>
    async Task<string?> SummaryPass(string content, bool contentIsDigests, CancellationToken ct)
    {
        var raw = await TextPass(
            "In 1 to 2 short, plain-English sentences, say what this meeting was about and what came of "
            + "it. Lead with the outcome. Capture real content from informal or wandering talk, but cut ALL filler, "
            + "greetings and logistics. Reply with the sentences only - no heading, no bullets, no preamble.\n\n"
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
        // EXTRACT, DO NOT ABSTRACT. This step used to emit paraphrased dash bullets, and that was the
        // point where invention entered: by the time the writer ran, the transcript was gone and it
        // had nothing left to check itself against. A note for a 40-minute meeting was written from a
        // paraphrase of a paraphrase, so a name the map step misheard became a fact.
        //
        // Now each line must CITE the transcript line it came from and stay in the speaker's own
        // words. That turns the final pass from a recall task into a rewriting task over evidence,
        // and it makes the digest checkable - see ValidateDigest, which drops any line whose citation
        // does not hold up. The faithfulness literature is consistent that inserting an extraction
        // step before generation is the single highest-leverage change available here.
        // FORMAT FIRST. The rules used to come before the shape, and the model answered with 31
        // ordinary dash bullets and no citations at all - measured on meeting 48. A small model
        // follows the last simple pattern it was shown far better than a paragraph of conditions, so
        // the shape and its example lead, and the qualifications follow.
        var prompt =
            $"This is part {part} of {total} of a meeting transcript. Every line is numbered.\n\n"
            + $"Write {DigestLines(chunk.Length) / 2} to {DigestLines(chunk.Length)} lines. EVERY line must start with the number of the transcript line it "
            + "came from, in square brackets, like this:\n\n"
            + "[4] Craig found one issue with the invoice table\n"
            + "[9] migration finishes Friday, two days ahead of schedule\n\n"
            + "A line without [n] at the front is useless and will be thrown away.\n\n"
            + "Pull out the lines that carry real content: topics, decisions (with the stated reason, if one was "
            + "given), tasks and follow-ups (keeping the exact NAME of whoever it was given to and the due date, "
            + "when stated), and important facts. Skip greetings, filler, logistics and audio checks.\n\n"
            + "Rules:\n"
            + "- Use the WORDS THAT WERE SAID. Trim and tidy, but do not rewrite, and do not summarise "
            + "several lines into one - cite the single line the point is in.\n"
            + "- Keep every NAME, NUMBER, DATE and QUANTITY exactly as it appears. This is all the note "
            + "writer will ever see of this part, so anything you drop is lost for good.\n"
            + "- The text is auto-transcribed speech: read past obvious mis-recognitions using context, but never "
            + "invent or \"correct\" a name.\n"
            + "- Do not editorialise and do not add anything that is not in the numbered lines.\n"
            + $"Reply with exactly {NothingMarker} ONLY if this part is purely greetings, small talk or "
            + "connection checks with no real topic at all.\n\n"
            + $"Transcript part:\n{chunk}";
        // Room for the lines actually asked for. ~30 tokens a line covers a cited point comfortably,
        // and the ceiling stays under the 760 the final pass uses, which is what ReserveTokens is
        // sized against - a map pass that outgrew the reserve would bring back NoKvSlot.
        return await TextPass(prompt, Math.Min(700, DigestLines(chunk.Length) * 30 + 100), ct);
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
        string spoken, string? about, CancellationToken ct, int spokenChars = 0)
    {
        var what = contentIsDigests ? "these notes from consecutive parts of one meeting" : "this meeting transcript";

        // A cap that does not scale claims a 90-minute meeting holds no more than a 3-minute one.
        // Short meetings keep the tight 5; longer ones earn room.
        var bulletBudget = BulletBudget(spokenChars);
        var sectionSpec = string.Join("\n\n", template.Sections.Select(s =>
            $"{s.ToUpperInvariant()}\n- up to {bulletBudget} SHORT bullets for \"{s}\", aim for 6 to 12 words each, one idea per bullet. "
            + "Every bullet must carry information a busy person needs - drop anything vague, obvious, or logistical. "
            + "Write it as a note, not a sentence of reported speech (no \"the team discussed\", no \"X said that\"). "
            + "Include this section ONLY if real content for it exists. "
            + "If filling it would need you to infer, justify, or fill a gap, LEAVE THE WHOLE SECTION OUT - an "
            + "absent section is correct, an invented one is not. "
            + "A bullet that is a TASK someone has to do belongs in ACTION ITEMS and nowhere else - never write the same thing twice under two headings."));

        // WHAT THE MEETING WAS. The writer used to get the transcript and nothing else - no date,
        // no duration, no attendees, no calendar title - all of which Toki already knows. A model
        // that cannot tell a 4-minute catch-up from a 90-minute review writes both the same way.
        // Facts only, never a hint to go find something: a hint is a claim, and a claim the
        // transcript cannot confirm is how a section fills with invention.
        var facts = string.IsNullOrWhiteSpace(about) ? ""         : "What you know about this meeting:" + Environment.NewLine + about!.Trim() + Environment.NewLine + Environment.NewLine;

        var prompt =
            facts
            +
            $"Write the notes for {what}, in the \"{template.Name}\" format. First judge how much REAL substance the meeting had.\n\n"
            + "Output ONLY the sections that genuinely apply, each header on its own line:\n\n"
            + "TITLE\n3 to 6 words, specific and concise, like a calendar event - no quotes, no \"Meeting\" prefix.\n\n"
            + "SUMMARY\n1 to 2 short, plain-English sentences: what the meeting was about and what came of it. Lead with "
            + "the outcome. Capture real content from informal or wandering talk, but cut ALL filler. No preamble like "
            + "\"This meeting was about\".\n\n"
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
            var raw = await TextPass(ask, 760, ct);
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

    /// <summary>THE THIN-INPUT GATE. Below this many substantive words a meeting cannot support a
    /// structured note, and asking for one only buys invention.
    ///
    /// The worst note this engine ever wrote came from a transcript of one 90-word utterance in which
    /// somebody said "I propose we do a sort of two-month rotation". It asserted three Options
    /// considered, a firm Decision, and a three-bullet Rationale: roughly 80% invention. That is not
    /// really a hallucination, it is a MISSING NULL CASE - the format had no legal way to say "there
    /// was not enough here", so the model filled the shape it was handed. The fix is arithmetic,
    /// because a model asked to judge its own sufficiency says yes almost regardless.
    ///
    /// MEASURED against the real 112-note Library (2026-09-02): median meeting 210 substantive words,
    /// the rotation failure 56, the obvious junk ("Mm-hmm", "Hello hello hello") 1 to 14. 70 clears
    /// the known failure with margin and sits far below anything real.
    ///
    /// It lives at the CALL SITE, not in WriteAsync: the writer's job is to write notes from what it
    /// is handed, and deciding a meeting is too thin to write about is the caller's judgement.</summary>
    internal const int ThinInputWords = 70;

    /// <summary>Words that carry meeting content. Greetings, backchannels and audio checks are the
    /// bulk of a thin transcript and none of them are something to write a note about.</summary>
    static readonly HashSet<string> Chatter = new(StringComparer.OrdinalIgnoreCase)
    {
        "hi", "hello", "hey", "yeah", "yep", "yes", "no", "ok", "okay", "right", "sure", "thanks",
        "thank", "you", "bye", "goodbye", "sorry", "please", "mm", "mhm", "hmm", "uh", "um", "so",
        "well", "just", "like", "and", "the", "a", "an", "is", "are", "was", "were", "i", "we",
        "it", "that", "this", "to", "of", "in", "on", "for", "can", "hear", "me", "there", "here",
    };

    internal static int SubstantiveWords(IReadOnlyList<TranscriptSegment> transcript) =>
        transcript
            .SelectMany(s => s.Text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            .Select(w => w.Trim('.', ',', '?', '!', ';', ':', '"'))
            .Count(w => w.Length > 1 && !Chatter.Contains(w));

    internal static NoteSet LowSubstanceNote() => new()
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
    static int MaxChunkChars() => MaxChunkChars((int)LlmRuntime.ContextSize);

    /// <summary>
    /// HOW MANY BULLETS A SECTION MAY HOLD, FROM THE SIZE OF THE MEETING.
    ///
    /// This was `content.Length > 9000 ? 9 : > 4000 ? 7 : 5`, where `content` is the DIGEST handed to
    /// the final pass. That is the wrong quantity twice over. It is an implementation detail of the
    /// chunking rather than a fact about the meeting, and it is capped by `MaxChunkChars`, so the
    /// `> 9000` branch could never fire and the real ceiling was SEVEN.
    ///
    /// MEASURED 2026-09-03, ten meetings: the notes claimed 37 hard facts where the stored ones
    /// claimed 75. The `General` template has exactly one section ("Key points"), so seven bullets
    /// was the entire body of almost every note, whether the meeting ran three minutes or ninety.
    /// Roughly ninety verified extracted points were being compressed into seven lines.
    ///
    /// Measured against what was SAID, which is the only honest proxy for how much a meeting holds.
    /// The ladder is deliberately coarse: the number is a ceiling the model may leave unfilled, and
    /// the prompt still forbids inventing content to reach it. Precision is the check that this
    /// bought coverage rather than padding, and `--regentest` reports both.
    /// </summary>
    internal static int BulletBudget(int spokenChars) =>
        spokenChars >= 40_000 ? 14 :
        spokenChars >= 15_000 ? 10 :
        spokenChars >= 4_000 ? 7 : 5;

    /// <summary>Tokens a window must leave free: the system prompt and instructions (~500), the
    /// generated answer (420 on the map pass, 760 on the final), and margin. </summary>
    internal const int ReserveTokens = 1600;

    /// <summary>
    /// Characters per token for NUMBERED TRANSCRIPT LINES, which tokenise far worse than prose:
    /// "[147] Craig: " is punctuation and digits, and a mis-heard name splits into pieces.
    ///
    /// 2.6 was an estimate and one real window disproved it. Measured 2026-09-03 over a ten-meeting
    /// run: 1 window in 43 still threw, this time `ContextOverflowException` rather than NoKvSlot,
    /// with a 520-token generation inside a 4096 context. So some transcripts tokenise worse than
    /// 2.6, and the failure is ASYMMETRIC: a window slightly too small costs a little detail, a
    /// window slightly too large costs the ENTIRE window and everything said in it. Be conservative.
    ///
    /// At ctx 4096 this gives 5,491 chars a window, still well above the 4,347 the NoKvSlot fix left.
    /// </summary>
    internal const double ChunkCharsPerToken = 2.2;

    /// <summary>
    /// HOW MUCH TRANSCRIPT FITS IN ONE WINDOW.
    ///
    /// This was `(ctx - 800) * 3` and it OVERFLOWED. On the owner's machine a 4.0GB card takes the
    /// partial-offload branch, which sets ContextSize to 3072, so the old formula handed the model
    /// 6816 characters. At 2.6 chars per token that is ~2600 tokens of transcript, plus ~500 for the
    /// instructions, plus 420 generated: over 3072. Every window threw
    /// `LLamaDecodeError: llama_decode failed: 'NoKvSlot'`, every map call returned null, and the
    /// meeting silently fell to the heuristic floor - which writes raw transcript lines as to-dos.
    ///
    /// That is almost certainly a large part of "the notes are mediocre": on a 4GB card, LONG
    /// meetings were never being written by the model at all. Short ones fit and looked fine.
    ///
    /// The reserve is now honest about all three costs and the ratio is conservative. Smaller
    /// windows mean more map calls and a slower note; a slow correct note beats a fast floor note.
    /// </summary>
    internal static int MaxChunkChars(int ctx) =>
        Math.Max(1200, (int)((ctx - ReserveTokens) * ChunkCharsPerToken));

    /// <summary>
    /// Fit the digests inside the budget by DROPPING the least informative lines, keeping the rest
    /// verbatim with their provenance intact.
    ///
    /// Ranking, in order: a line carrying hard entities (names, numbers, dates, money) outranks one
    /// that carries none, because those are the facts a reader came for and the ones
    /// <see cref="Grounding"/> measures. Among equals, a line from a window whose citation VERIFIED
    /// outranks one from a window that came back uncited. Length breaks the remaining ties, shortest
    /// first, on the reasoning that a very long digest line is usually a run-on rather than a dense
    /// one.
    ///
    /// Original ORDER is restored after selection: the note reads the meeting in sequence, and a
    /// writer handed its evidence out of order invents transitions to explain the jumps.
    /// </summary>
    internal static List<Window> TrimToFit(IReadOnlyList<Window> digests, int maxChars)
    {
        // Flatten to lines, remembering which window each came from, so provenance stays exact.
        var lines = new List<(string Text, int Idx, int Order, int Score)>();
        var order = 0;
        for (var w = 0; w < digests.Count; w++)
            foreach (var raw in digests[w].Text.Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Trim().Length == 0) continue;
                var score = Grounding.Extract(line).Count * 10
                          + (digests[w].First >= 0 ? 3 : 0)
                          - Math.Min(2, line.Length / 200);
                lines.Add((line, w, order++, score));
            }

        var budget = 0;
        var keep = new List<(string Text, int Idx, int Order)>();
        foreach (var l in lines.OrderByDescending(l => l.Score).ThenBy(l => l.Order))
        {
            if (budget + l.Text.Length + 1 > maxChars) continue;   // skip it, but keep looking:
            budget += l.Text.Length + 1;                            // a shorter later line may fit
            keep.Add((l.Text, l.Idx, l.Order));
        }

        // Rebuild in reading order, one Window per original digest so each keeps its own range.
        var rebuilt = new List<Window>();
        foreach (var g in keep.OrderBy(k => k.Order).GroupBy(k => k.Idx).OrderBy(g => g.Key))
        {
            var text = string.Join("\n", g.OrderBy(k => k.Order).Select(k => k.Text));
            rebuilt.Add(new Window(text, digests[g.Key].First, digests[g.Key].Last));
        }
        return rebuilt;
    }

    /// <summary>
    /// Group already-made digests into re-reduction windows, carrying their transcript ranges.
    ///
    /// The point is the RANGE. A group's range is the union of the ranges of the digests it merges,
    /// which is something that was observed rather than guessed: those windows really are where this
    /// text came from. It is coarser than a first-round range and it is still a true statement, which
    /// is the line the provenance rules draw. Digests whose own range is unknown (a window that came
    /// back uncited, marked -1) contribute their TEXT to the group but nothing to the range, so an
    /// unverified window can never widen a range it did not earn.
    ///
    /// A group with no known range at all stays -1 and its bullets fall back to text matching.
    /// </summary>
    internal static List<Window> GroupWindows(IReadOnlyList<Window> parts, int maxChars)
    {
        var groups = new List<Window>();
        var sb = new StringBuilder();
        int lo = -1, hi = -1;

        void Flush()
        {
            if (sb.Length == 0) return;
            groups.Add(new Window(sb.ToString(), lo, hi));
            sb.Clear();
            lo = hi = -1;
        }

        foreach (var w in parts)
        {
            if (sb.Length > 0 && sb.Length + w.Text.Length + 1 > maxChars) Flush();
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(w.Text);
            if (w.First >= 0)
            {
                lo = lo < 0 ? w.First : Math.Min(lo, w.First);
                hi = hi < 0 ? w.Last : Math.Max(hi, w.Last);
            }
        }
        Flush();
        return groups;
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

    /// <summary>One window of transcript handed to the model, WITH the line range it covers. The
    /// range is the provenance: knowing which lines the model was reading when it wrote a bullet is
    /// strictly better than recovering it afterwards by matching words.</summary>
    internal sealed record Window(string Text, int First, int Last);

    /// <summary>How much of a written line's vocabulary must appear in a digest window before that
    /// window is accepted as its source. The final pass REWROTE the digest point, so it may compress
    /// and re-order, but it cannot introduce content words that were never in the window it read.</summary>
    const double ProvenanceOverlap = 0.5;

    /// <summary>How far ahead of the runner-up the winning window must score. Two windows that both
    /// plausibly explain a bullet are an ambiguous attribution, and CLAUDE.md's rule is that a wrong
    /// evidence link is worse than none: a reader sent confidently to the wrong minute of a meeting
    /// trusts the feature less than one offered no link at all.</summary>
    const double ProvenanceMargin = 0.15;

    /// <summary>
    /// RECORD WHERE EACH WRITTEN LINE CAME FROM.
    ///
    /// The map step already verified every digest point against the transcript line it cited, and
    /// ValidateDigest narrowed each window to the range its surviving points actually cover. Those
    /// ranges were computed and then thrown away: nothing assigned them, so every evidence link in
    /// the product was recovered afterwards by matching bullet text against the WHOLE transcript.
    ///
    /// This closes that gap. Matching a bullet against the digest it was rewritten from is a far
    /// easier problem than matching it against the whole meeting, because the digest is short, it is
    /// in the speaker's own words, and its line range is already verified. Evidence.BestRange then
    /// narrows within the recorded span instead of searching everything.
    ///
    /// Silent when unsure, by design. A bullet too generic to attribute, or one that two windows
    /// explain equally well, keeps a null source and falls back to text matching exactly as before.
    /// </summary>
    internal static void AttachProvenance(NoteSet note, IReadOnlyList<Window> windows)
    {
        if (note is null || windows.Count == 0) return;
        var windowWords = windows.Select(w => Evidence.ContentWords(w.Text)).ToList();

        foreach (var s in note.Sections)
        {
            var sources = new List<SourceSpan?>(s.Bullets.Count);
            var any = false;
            foreach (var b in s.Bullets)
            {
                var span = BestWindow(b, windows, windowWords);
                sources.Add(span);
                any |= span is not null;
            }
            // A list of nothing but nulls is noise in the stored JSON and says the same as no list.
            if (any) s.Sources = sources;
        }

        // Never overwrite a span the caller already recorded - a source set upstream was observed,
        // and this one is inferred.
        foreach (var a in note.Actions)
            a.Source ??= BestWindow(a.Text, windows, windowWords);
    }

    /// <summary>The one digest window that best explains a written line, or null when no window
    /// clears the overlap floor or two of them tie within the margin.</summary>
    internal static SourceSpan? BestWindow(string? line, IReadOnlyList<Window> windows,
        IReadOnlyList<HashSet<string>> windowWords)
    {
        var want = Evidence.ContentWords(line);
        // One shared word is a coincidence, not an attribution. Same floor the evidence matcher uses.
        if (want.Count < 2) return null;

        double best = 0, second = 0;
        var bestIdx = -1;
        for (var i = 0; i < windows.Count; i++)
        {
            var shared = want.Count(w => windowWords[i].Contains(w));
            var coverage = (double)shared / want.Count;
            if (coverage > best) { second = best; best = coverage; bestIdx = i; }
            else if (coverage > second) second = coverage;
        }

        if (bestIdx < 0 || best < ProvenanceOverlap) return null;
        if (best - second < ProvenanceMargin) return null;
        return new SourceSpan { First = windows[bestIdx].First, Last = windows[bestIdx].Last };
    }

    static readonly Regex CitedLineRx = new(@"^\s*[-*]?\s*\[(\d+)\]\s*(.+?)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// How many digest lines to ask for, SCALED TO THE WINDOW.
    ///
    /// This was a flat "5 to 12 lines" regardless of how much transcript the window held, which
    /// means the extraction budget was per-WINDOW when the thing being extracted is per-MINUTE.
    /// Raising ContextSize to 4096 made windows 49% bigger and immediately exposed it: meeting 28
    /// went from seven windows to four, kept the same line budget each, and its measured recall fell
    /// from 62% to 38% while running 100 seconds faster. Fewer, bigger windows were extracting less
    /// of the meeting.
    ///
    /// One line per ~450 characters of transcript, which is roughly one point per speaking turn at
    /// the rate these transcripts run. Clamped: below 6 a short window stops reporting anything
    /// useful, above 20 the model starts padding, and padding is what the citation gate then has to
    /// throw away.
    /// </summary>
    internal static int DigestLines(int chunkChars) => Math.Clamp(chunkChars / 450, 6, 20);

    /// <summary>How many seconds of speech either side of a cited line should count as supporting
    /// it. A spoken sentence runs roughly five to eight seconds, and a point drawn faithfully from
    /// one sentence can cite any line inside it.</summary>
    const double NeighbourSeconds = 6.0;

    /// <summary>
    /// HOW MANY LINES EITHER SIDE OF A CITATION COUNT AS SUPPORTING IT.
    ///
    /// This was a flat "one either side", which measures the neighbourhood in LINES. A line is not a
    /// unit of meaning: it is however much speech landed between two pauses, and that varies by a
    /// factor of three between meetings.
    ///
    /// MEASURED 2026-09-03 on two stored meetings, same pipeline, same day:
    ///
    ///   meeting 48   468 segments / 26 min = 3.3s per line   ->  +/-1 covers ~10s  ->  kept 25/26, 29/31, 22/23
    ///   meeting 28  1140 segments / 20 min = 1.05s per line  ->  +/-1 covers ~3s   ->  kept 4/10, 15/30, 3/9
    ///
    /// Meeting 28 is a heavily turn-merged call at about a second a line, so a sentence spans five or
    /// six lines and a faithful point citing the middle of it failed against a three-line check. The
    /// gate was dropping TRUE content, not catching invention: nearly all its losses were
    /// `unsupported` rather than `uncited` or `out-of-range`.
    ///
    /// So scale the neighbourhood to cover a comparable amount of SPEECH. The cap matters as much as
    /// the scaling: this widens a safety gate, and a very wide haystack would let a genuinely
    /// invented point find its words somewhere nearby. Five lines either side is the ceiling.
    ///
    /// DO NOT "fix" a poor keep rate by lowering CitationOverlap instead. That lowers the bar for
    /// every meeting including the clean ones, and this gate is the only thing standing between a
    /// digest and invention.
    /// </summary>
    internal static int NeighbourLines(IReadOnlyList<TranscriptSegment> transcript)
    {
        if (transcript.Count < 2) return 1;
        var first = transcript[0];
        var last = transcript[^1];
        var spanMs = last.EndMs - first.StartMs;
        // No usable timing (a transcript built without timestamps) keeps the old behaviour.
        if (spanMs <= 0) return 1;
        var secondsPerLine = spanMs / 1000.0 / transcript.Count;
        if (secondsPerLine <= 0) return 1;
        return Math.Clamp((int)Math.Round(NeighbourSeconds / secondsPerLine), 1, 5);
    }

    /// <summary>How much of a digest line's own vocabulary must appear in the transcript line it
    /// cites. Not 1.0: the map step is allowed to trim and tidy, only not to invent. Half the content
    /// words is enough to tell "tidied" from "made up".</summary>
    const double CitationOverlap = 0.5;

    /// <summary>
    /// CHECK THE DIGEST AGAINST THE TRANSCRIPT IT CLAIMS TO COME FROM.
    ///
    /// The map step now cites a line number for every point. That citation is only worth having if
    /// something verifies it, so this drops any line that cites a number outside the window, cites
    /// nothing at all, or whose words do not actually appear where it says they do. It is the one
    /// place in the pipeline where the model's output is measured against the transcript BEFORE it
    /// becomes the only thing the writer can see.
    ///
    /// Neighbours count, because a point genuinely can straddle two lines after turn-merging, and
    /// being strict there would throw away good content to enforce a formatting preference.
    ///
    /// Returns the cleaned digest with the citations stripped (the writer does not need them) and the
    /// narrowed line range the surviving points actually cover, which is the provenance a later
    /// evidence link can use instead of guessing.
    /// </summary>
    internal static (string Text, int First, int Last)? ValidateDigest(
        string digest, IReadOnlyList<TranscriptSegment> transcript, int windowFirst, int windowLast)
    {
        var kept = new List<string>();
        int lo = int.MaxValue, hi = int.MinValue;
        int seen = 0, uncited = 0, outOfRange = 0, unsupported = 0;

        foreach (var raw in digest.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            seen++;
            var m = CitedLineRx.Match(raw);
            if (!m.Success) { uncited++; continue; }                     // unverifiable, so dropped
            if (!int.TryParse(m.Groups[1].Value, out var id)) { uncited++; continue; }
            if (id < windowFirst || id > windowLast || id >= transcript.Count) { outOfRange++; continue; }

            var text = m.Groups[2].Value.Trim();
            if (text.Length == 0) { uncited++; continue; }

            // The cited line plus its NEIGHBOURS IN TIME, not a fixed number of lines. See
            // NeighbourLines: a line is not a unit of meaning, a few seconds of speech is.
            var span = NeighbourLines(transcript);
            var near = string.Join(" ",
                Enumerable.Range(Math.Max(windowFirst, id - span), span * 2 + 1)
                    .Where(i => i <= windowLast && i < transcript.Count)
                    .Select(i => transcript[i].Text));

            var claimed = Storage.Evidence.ContentWords(text);
            if (claimed.Count == 0) { unsupported++; continue; }   // nothing but stop words to check
            var supported = Storage.Evidence.ContentWords(near);
            var hits = claimed.Count(w => supported.Contains(w));
            if (hits / (double)claimed.Count < CitationOverlap) { unsupported++; continue; }

            kept.Add(text);
            lo = Math.Min(lo, id);
            hi = Math.Max(hi, id);
        }

        // Named counts, not a bare verdict: a window that lost everything to UNCITED lines is the
        // model ignoring the format, which is a prompt problem, while one that lost everything to
        // UNSUPPORTED is either real invention or an overlap threshold set too high. Those need
        // opposite fixes, and "dropped" alone cannot tell them apart.
        if (kept.Count < seen)
            App.Log($"notes: digest kept {kept.Count}/{seen} (uncited {uncited}, out-of-range {outOfRange}, unsupported {unsupported})");

        if (kept.Count == 0) return null;
        return (string.Join("\n", kept.Select(k => "- " + k)), lo, hi);
    }

    /// <summary>Group whole segments into windows under <paramref name="maxChars"/> each, keeping
    /// the transcript line range each window covers.</summary>
    static List<Window> Chunk(IReadOnlyList<TranscriptSegment> transcript, int maxChars)
    {
        var chunks = new List<Window>();
        var sb = new StringBuilder();
        var first = 0;
        for (var idx = 0; idx < transcript.Count; idx++)
        {
            var seg = transcript[idx];
            // Label, not the raw stream: once the speakers have been told apart the writer sees real
            // names, which is what lets it attribute a to-do instead of guessing. On a fresh meeting
            // diarisation has not run yet, so this is still "You"/"Them" - the names arrive on a rewrite.
            // NUMBERED. The map step is asked to CITE the line it took each point from, which is what
            // lets the digest be checked against the transcript instead of trusted. Without an id
            // there is nothing to check against and the digest is just the model's word for it.
            var line = $"[{idx}] {seg.Label}: {seg.Text}";
            if (sb.Length > 0 && sb.Length + line.Length + 1 > maxChars)
            {
                chunks.Add(new Window(sb.ToString(), first, idx - 1));
                sb.Clear();
                first = idx;
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        if (sb.Length > 0) chunks.Add(new Window(sb.ToString(), first, transcript.Count - 1));
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
            catch (Exception ex)
            {
                // NEVER SWALLOW THIS SILENTLY. A throw here returns null, the caller counts the
                // window as failed, and if every window fails the whole meeting drops to the
                // heuristic floor - which writes raw transcript lines as to-dos. That is the worst
                // note Toki can produce, and until 2026-09-03 it happened with NOTHING in the log to
                // say why. Measured on meeting 28: a 20-minute meeting fell to the floor and the only
                // clue was the absence of the "digest kept" lines that a working map pass emits.
                App.Log($"notes: model call FAILED ({maxTokens} tok, attempt {attempt + 1}): {ex.GetType().Name}: {ex.Message}");
                return null;
            }
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
