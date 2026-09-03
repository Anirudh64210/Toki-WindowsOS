# Research findings: note quality and motion (2026-09-01 / 2026-09-02)

The record of two research rounds run after the owner's verdict that Toki "is not smooth and clean
application ready" and that note quality is "mediocre at best". Append-and-refine: add beside, never
overwrite. Sourced claims carry their source; inferences are marked [inference].

**Research limits, stated up front.** The user-voice corpus is roughly two dozen Hacker News comments
plus a Product Hunt review page. Reddit, DuckDuckGo and G2 were unreachable. HN over-indexes on
privacy, consent and local-first sentiment, which happens to flatter Toki and must not be read as
market-wide. Founder quotes came via podcast and interview writeups, not raw transcripts: accurate in
substance, near-verbatim in wording. NN/g's numbers were measured on web pages, not meeting notes.

---

## PART A - MOTION: what actually made Toki feel unfinished

### A1. The root cause was a gate, not a curve

`Theme/Motion.cs` was already correct. Toki's `ArriveStrong` (0.23, 1, 0.32, 1), `Move`
(0.77, 0, 0.175, 1) and `Drawer` (0.32, 0.72, 0, 1) are value-for-value the three curves Emil
Kowalski's standards prescribe, and the durations sit inside the published bands.

The defect was that `PillWindow.OnStateChanged` decided whether a transition animated AT ALL by asking
`IsHoverPair(old, next)`, an allowlist of five state pairs. Everything else fell through to an
in-place branch that called `SizeWindowNow` and teleported in one frame.

The three that snapped were the three the user clicks:

| Step | Transition | Old behaviour |
|---|---|---|
| Accept the meeting | MeetingPrompt to RecordingBar | 169x40 to 34x14 in one frame, window jumps 10px |
| Press Stop | RecordingOpen to Summarizing | 272x64 to 220x13 in one frame, mascot hard-cut to 0 |
| Open the notes | tucked to full page | content swapped at full opacity, then the box animated |

Meanwhile the drop-down and the 560ms notes-ready flight, which nobody asks for, are the most
polished motion in the app. **That inversion is the feeling of unfinished.**

### A2. The four supporting defects

1. **Union-width flash.** `PinContentForMorph` nulled `Root.Clip` at t=ms, but `AnimateSize`'s settle
   timer does not resize the window until t=ms+20, and Root stretches. Collapsing Home flashed back to
   344px, fully plated, for 1 to 2 frames, every time.
2. **Dead frames on close.** `ConcealUp` clipped at the WINDOW height while Root is `CardPadB`
   shorter, so about 36% of the peek's retract was invisible. Same bug in the handoff flight.
3. **Content pop.** `ShowNotesReady` and `RestoreTuckedView` swapped children at full opacity.
4. **Un-eased opacity.** Six opacity animations had no `EasingFunction`, i.e. WPF linear.

### A3. The rules this yields

**Animate by default; snapping is the exception.** An allowlist of animated transitions will always
drift behind the state machine, and the states it forgets are the new ones, which are the ones under
active development and therefore the ones the owner is looking at.

**A clip must be dropped only after the window has actually reached its target size.**

### A4. Apple / Kowalski numbers worth holding Toki against

- Springs are two parameters, not three: **damping ratio** (1.0 critically damped, no bounce) and
  **response** (seconds to target, not a duration). Apple ships: move/reposition 1.0 / 0.4, rotation
  0.8 / 0.4, drawer or sheet 0.8 / 0.3. Bounce only when the gesture itself carried momentum.
- **Interruptibility is the single most important principle.** Always animate from the PRESENTATION
  (live on-screen) value, never the target. Never lock out input during a transition. Toki's
  fixed-duration DoubleAnimation model restarts from zero, which is the structural gap remaining.
- Momentum projection, the function Apple actually ships (not the textbook one):
  `project(v) = (v / 1000) * d / (1 - d)`, d about 0.998, or 0.99 for snappier.
- Rubber-banding: `(overshoot * dim * 0.55) / (dim + 0.55 * abs(overshoot))`.
- Frequency gate: 100+ times a day means **no animation ever** (Raycast has no open/close animation).
  Tens of times a day means near-imperceptible. UI animations stay **under 300ms**.
- **Never ease-in on UI.** Ease-out at 200ms feels faster than ease-in at 200ms.
- Never scale-from-zero; start at 0.9 to 0.97 plus opacity. Press feedback scale 0.97 over 100-160ms.
- Stagger 30-80ms per item, total at most 300ms.
- Exit-only motion is a pure opacity fade, 150-220ms, no spring, no scale.
- Blur under 20px can mask an imperfect crossfade. This collides with Toki's standing rule of no
  Effect inside an animated subtree, and the house rule wins.

Vendored MIT under `.claude/skills/emil-*`. `motion-and-feel` remains the house style and wins.

---

## PART B - NOTES: what the pipeline audit found

### B1. The quality ceiling was arithmetic

`LlmRuntime` picks `ContextSize = 3072` on a 4GB card, so
`MaxChunkChars() = (3072 - 800) * 3 = 6816` characters, which is **about eight minutes of speech**.

| Meeting length | What the note was written from |
|---|---|
| under 8 min | the real transcript |
| 8 to 16 min | 2 digests, 10 dash bullets total |
| 40 min | 5 digests, at most 25 dash bullets |
| 55+ min | digests re-digested, provenance cleared |

The model was writing from a summary of a summary. No prompt wording fixes that.

### B2. The other confirmed defects

- **The writer was told nothing about the meeting.** No date, duration, attendees, calendar title or
  agenda. All of it existed in `CurrentPrep` and was used only for name repair.
- **Provenance is documented, tested, and was never implemented.** `ARCHITECTURE.md` describes it with
  measured gains (82.9 to 87.5 percent precision at 1 on hard cases). The map step built the line
  ranges. Nothing read them. `ProvenanceTests` tests only the consumer half.
- **Both quality instruments were blind.** `--llmtest` and `--renotes` printed `notes.Decisions`,
  which the model path never populates. They always printed `KEY POINTS (0)`.
- **`--seed` wrote into the real Library.** A default-constructed `MeetingStore` is the real
  `%APPDATA%` database. Two hand-written C# string literals (ids 1 and 2) sit in the owner's
  112-note store, and being hand-written they read better than real output, which silently biased
  every quality judgement made by browsing.
- **480 final tokens truncated action items.** The truncated-tail guard existed because ACTION ITEMS
  is written last and the cap landed inside it.
- **A fixed 5-bullets-per-section cap** claimed a 90-minute meeting holds no more than a 3-minute one.

### B3. The worst observed output

Aug 5. The complete transcript is one 90-word utterance in which one person says "I propose we do a
sort of two-month rotation". The note asserted three Options considered (two never said), a firm
Decision ("Rotation cycle begins immediately"), and a three-bullet Rationale. Roughly 80% invention.

This is not primarily a hallucination bug. **It is a missing null case:** the schema had no legal
"nothing was decided" output, so the model had to invent one.

---

## PART C - WHAT MAKES NOTES FEEL VALUABLE (user research)

### C1. The complaints, in heat order

1. **Misattributed and invented commitments.** "I see my name tasked with things I never committed
   to, or I see conclusions or action items grossly misrepresented" (HN 44861112). "frequently totally
   inaccurate... fabricate things that were never said" (HN 48095379).
2. **Flat salience.** The most actionable finding in the corpus, and a notes-design failure rather
   than an ASR failure: "removing all the unimportant side-items that were captured **in the same
   level of detail as the core decision**" (HN 46148354); "the weather is not important... action
   item to buy corn" (HN 40248213). The tool did the transcription and skipped the judgement.
3. **Nobody goes back to them.** "I have literally never seen anybody, in any situation, go back to
   them" (HN 49244682). Granola's own CEO agrees about his own product.
4. **The notes become social spam.** "These apps are cancer. Otter.ai... will scrape the call's
   contacts, and email every single one" (HN 44448338); "we all called it the Otter Infection until
   IT was able to purge it" (HN 44946245).
5. **Bad speaker attribution poisons everything downstream** (HN 49243813).
6. **No vocabulary customisation.** One engineer evaluated fourteen tools, built his own in a day, and
   found it "dramatically better... no contest", because internal codenames and acronyms matter
   (HN 39620459). Toki's `NameCorrector` is on the right side of this.

**Nobody complained that notes were too short.** Every complaint ran toward padding, noise, invention.

### C2. The praise clusters on exactly three things

It sounds like a person; it captures commitments; it is searchable later.

- "the notes genuinely sound like me, so I actually use them instead of **ignoring a block of robotic
  AI text**" (skillscouter Granola review).
- "Granola notes are a **1 Minute read**" (HN 48256570).
- The one genuine daily-use datapoint, from a thread asking what people use EVERY DAY: "Granola -
  transcription and meeting notes, **searching across notes, recalling action items**" (HN 44374609).
- "reminds me what promises I made" (HN 44157462).
- Teams paste the summary into chat and correct it below: the note is treated as **a first draft of
  shared memory**, not a record (HN 41132985).

### C3. Granola versus Circleback, structurally

**Granola wins on the INPUT side.** It enhances the user's own typed notes rather than summarising
from scratch. Pedregal: "It takes whatever notes you have written as a starting point, tidies them up,
and fleshes out the bits you missed." The rationale is psychological: people "value their thinking and
judgment", "**writing is thinking**", and the deal is you still write what matters to you and relax
about the details. In the rendered note **the user's own text is visually distinct from the AI's**.
Restraint is explicit doctrine: co-founder Sam Stephenson calls the product "surprisingly
unambitious"; anything added "has got to pass a really high bar"; templates are deliberately buried
because "**most users should not have to think about it**".

**Circleback wins on the OUTPUT side**, by being opinionated about prose and defending it with evals.
CEO Ali Haghani: "We really try to never have our notes say the word **'discussed'** cuz it's fully
pointless." A meeting is discussion by definition, so the word carries zero information. Their hard
action-item rule: **a task completed during the meeting is not an action item** - "I will send it now"
plus evidence they sent it "would just be noise". They run evals and treat note-quality regressions as
a first-class risk, specifically guarding against "making topics too long".

Neither wins by adding sections. Toki can take both: Granola's anchoring on typed notes, Circleback's
prose rules.

### C4. What earns the first five seconds (NN/g, measured)

- People read **20 to 28 percent** of the words. They read half the information only on pages of
  **111 words or less**. Time model: about 25 seconds fixed plus 4.4 seconds per extra 100 words.
- **79 percent always scan**; 16 percent read word by word. Measured usability lift: concise text
  +58%, scannable layout +47%, objective language +27%, all three +124%.
- First lines get more gazes than later lines; the first words of a line get more fixations.
- Scanning a list, users take in the **first two words, about 11 characters**. A well-front-loaded
  item was correctly predicted from those 11 characters by **85%** of users; one opening
  "Introducing..." by **15%**.
- Inverted pyramid and BLUF: the conclusion first, so a reader who stops anywhere still leaves with
  the point. BLUF is doctrine, not measured; the measured evidence is NN/g's.
- **No usability research exists specifically on meeting minutes.** That gap is real.

### C5. Dynamic versus fixed structure

For adaptive: Circleback states that relevance varies by meeting type. Granola derives headings from
topics and buries fixed templates. FRAME/SCOPE (arXiv 2509.15901) reports that meeting summarization
"remains error-prone, often producing outputs with hallucinations, omissions, and irrelevancies" and
fixes it architecturally: **extract and score salient facts, organise them thematically, enrich an
outline, then write**. Structure emerges from what the extractor found.

For fixed: NN/g's consistency heuristic. Predictable placement lets users build a mental model and
stop hunting. Some users explicitly build their own fixed frame (HN 48454349).

**Nobody publishes their section-selection policy.** [inference] The synthesis is a **fixed spine with
an adaptive middle**: the outcome line and the commitments block always in the same place, body
headings derived from content. Decisions and questions become earned marks inside the body, not
standing headings.

---

## PART D - MAKING A 4B LOCAL MODEL BEHAVE (technical research)

### D1. Do not let the model vote on whether a section applies

LLM judges show a true-positive rate around 96 percent but a **true-negative rate under 25 percent**
(arXiv 2510.11822). Asked "include this only if warranted", they say yes. **An inline conditional is a
permission request, and these models grant permission.** This is the mechanism behind the Aug 5 note.

The reliable shape: the model returns **evidence** (transcript line ids, or NONE), and **application
code decides** the section exists if the evidence count clears a per-type threshold. An absent section
becomes an absence of evidence, which is a fact, rather than a judgement of insufficiency, which is a
guess. Architecturally this is Attribute First then Generate (arXiv 2403.17104) and entity-chain
planning (arXiv 2104.07606), where hallucination is controlled by editing the PLAN rather than asking
the generator to restrain itself.

### D2. Extract before you abstract

The most consistent result in the faithfulness literature. Inserting an explicit extraction step
before generation cuts hallucination, and it works with weak generators because selection becomes
retrieval rather than generation.

- arXiv 2304.04193: extract-then-generate yields significant faithfulness gains over abstractive.
- arXiv 2312.10934: +30.56% faithfulness over a GPT-4 baseline.
- arXiv 2403.17104: content selection, then sentence planning, then generation.

**Applied to Toki:** the map step emitting paraphrased dash bullets IS the invention entry point. By
stage two the transcript is gone and the writer has nothing to check itself against. Emitting
**verbatim spans with line ids and speaker** turns the final pass from recall into rewriting.

### D3. Never ask the model to review its own note

arXiv 2310.01798 (ICLR 2024): without external feedback, models "struggle to self-correct... and at
times, their performance even degrades after self-correction." Combined with the agreeableness bias,
any "review your note" pass is theatre.

The usable form: **a new call, that sees the source, sees one claim, answers a closed question, and
has no memory of having written it.** That task is demonstrably in reach of very small models -
MiniCheck reaches GPT-4 accuracy at **770M** (arXiv 2404.10774), AlignScore at **355M** (2305.16739) -
so 4B is comfortable. SummaC's core finding (2111.09525) is that NLI fails at document level and
succeeds on **sentence pairs**, aggregated.

Qwen3 specifically: arXiv 2509.13332 tests 0.6B / 1.7B / **4B** as judges and finds thinking variants
give +10pp accuracy at under 2x cost, and +6% consistency under positional and bandwagon bias.

### D4. Constrained decoding: split the decision by pass

Against: arXiv 2408.02442 and 2604.03616 find format constraints degrade reasoning, and that
"decoupling reasoning from formatting substantially recovers lost accuracy". For small models
specifically, arXiv 2605.02363 measures **3.6x to 8.2x latency overhead**.

For: arXiv 2305.13971 finds grammar-constrained decoding "substantially outperforms" unconstrained on
**closed-vocabulary structured prediction**. The .txt rebuttal reproduces Let Me Speak Freely and finds
structured better (68% vs 65%, 77% vs 73%), attributing the original result to mismatched prompts and
to conflating JSON-mode with true constrained generation.

**Verdict for Toki:** do NOT grammar-force the prose pass. DO grammar-force every classification and
selection pass (section applicability, entailment yes/no, evidence line ids) - closed vocabulary, one
to ten tokens, so the latency multiplier is negligible in absolute terms, and it eliminates the class
of "the model answered a yes/no question with a paragraph".

Mechanics: llama.cpp GBNF supports alternation, grouping, repetition, char ranges; JSON Schema to GBNF
is built in. **The schema is NOT injected into the prompt** - you must still describe it or the model
generates blind into a mask, which is the biggest documented failure of the technique. Nested refs and
prefixItems are broken. In LLamaSharp 0.27 the grammar attaches to the sampler, not the inference
params: `new DefaultSamplingPipeline { Grammar = new(gbnf, "root") }`, with `Reset()` between runs.

### D5. Measuring quality offline

`Microsoft.ML.OnnxRuntime` is banned, so MiniCheck and AlignScore cannot run directly. But
`EmbeddingService` already runs bge-small via LLamaSharp, and Qwen3-4B is already loaded. Both
verifier substrates exist with zero new dependencies.

No single cheap metric is trustworthy alone (arXiv 2407.00747 found ROUGE-2, BERTScore AND SummaC all
failed to track human judgement on their domain). So: a small composite that fails in different
directions.

1. **Hard-entity precision, no model, exact.** Fraction of proper nouns, numbers, dates, times and
   amounts in the note that appear in the transcript. This is the failure the owner will actually
   notice - a wrong number, a person who was not there - and it is detectable with string matching.
   (arXiv 2102.09130, 2403.01373.)
2. **Bullet grounding via existing embeddings.** Max cosine of each bullet against transcript
   sentences. Weaker than NLI: embeddings cannot tell "we will ship Friday" from "we will NOT ship
   Friday", so use it to SELECT which bullets get check 3, never as the verdict.
3. **Entailment yes or no from Qwen3-4B**, fresh context, against that bullet's own evidence spans,
   grammar-constrained to YES or NO, thinking enabled.
4. **Entity recall counterweight.** Precision alone is gamed by writing less; without recall every
   measured "improvement" is the model getting terser. (arXiv 2401.17974.)

Verify bullets **as written, with their section heading prepended**, not decomposed into atoms:
decomposition scores are highly sensitive to how you decompose (2403.11903), gameable by repetition
(2407.03572), and misjudge a claim ripped from context (2412.13175).

### D6. The missing null case

The abstention literature (2407.18418, 2407.16221, 2605.25394) says give the model a real escape
hatch, and decide arithmetically what you can rather than delegating it. Toki's worst failure is a
thin transcript with no legal "nothing substantive here" output. A gate BEFORE the writer runs kills
most of that class without touching the model. This is CLAUDE.md lesson 7 ("a silent meeting saves
nothing") one notch up.

---

## PART F - TRUNKED AND LITTLEBIRD (2026-09-02, second round)

Researched at the owner's ask. Both confirmed real; both examined for anything Granola and Circleback
had not already taught us.

### F1. The headline: neither has solved grounding, and neither claims to

**Trunked (trunked.ai)** publishes no faithfulness claim, no grounding claim, no citations, no
confidence display and no eval, anywhere on its site or FAQ. Its privacy policy names the entire
stack: **AssemblyAI** for speech, **Google Gemini via Vercel AI Gateway** for "note generation and
question answering". Off-the-shelf ASR plus one hosted frontier model, almost certainly single-pass
[inference, from the absence of any extraction or verification stage]. That is exactly the
architecture that cannot show its work, which is presumably why it makes no claim.

**Littlebird (littlebird.ai)** answers hallucination in a docs FAQ line: *"Like any AI system,
Littlebird can produce an incomplete or incorrect response... ask it to link to the context it used,
and verify important details against the original source."* Verification is the user's manual chore
and citation is something you have to ask for in chat rather than something the note carries. It is a
disclaimer wearing a feature's clothes.

**Neither links a summary line back to a moment in the recording** (sourced negative: absent from
docs, marketing, comparison pages, FAQ and changelog). Toki's evidence clips and `--evidencetest`
already do automatically what Littlebird instructs its users to do by hand.

**The conclusion that matters: Toki's note problem is not a faithfulness problem, it is a SELECTION
problem.** What is worth saying, and where it sits on the page. Spend there.

### F2. Littlebird's note template, reconstructable from their own docs

Three peer views behind a toggle: **Note** (the user's own typed notes) | **Summary** | **Transcript**.
The Summary's sections, in order:

1. **Executive summary**
2. **For you** - a personalised action list of tasks assigned to the reader specifically
3. **Topics discussed** - bullets **with relevant quotes**
4. **Decisions** - "a clear, unambiguous list"
5. **Action items** - split explicitly into **You** and **Others**
6. **Risks / open questions**

Note the redundancy they deliberately accepted: "For you" duplicates the You half of Action items.
The personal slice is promoted above the topic body on purpose. Everything is editable; copy as
Markdown shipped April 2026.

Their workflow also includes a **microphone health check at the start of a meeting** and a named
"Verify and refine before sending" step in the post-meeting chain.

### F3. What to take

1. **"Risks / open questions"** - where the non-obvious value of a meeting lives, and the first thing
   a bland summary drops.
2. **"For you" above the body** - accepting the duplication. Cheapest change with the biggest felt
   effect: the reader sees their own name first.
3. **Action items split You / Others** at the LIST level. Note this does not conflict with the earlier
   Granola/Apple finding that owner should be inline muted prose rather than a chip: the split is
   structural, the styling stays prose.
4. **Quotes inside topic bullets.** Toki has the stronger version (playable clips) but not the visible
   one. A short verbatim line makes a note LOOK checkable, which is most of the perceived-value gap.
5. **"Decisions" as a constraint, not just a heading:** if a decision cannot be stated unambiguously
   it is not a decision, it is a risk or an open question.
6. **Microphone health check at meeting start.** Toki has measured this exact failure (mic 38dB below
   the meeting stream, half the transcript invented) and has better instruments to detect it.
7. **`NameCorrector` is an unmarketed shipped feature.** Trunked sells "improves accuracy with names
   and technical terms" as a paid tier. Toki has it, plus per-name recall in `--wertest`.
8. Trunked's free tier caps HISTORY at 30 days rather than crippling features, converting to a
   one-time $19.99 promo / $99.99 "Lifetime". Same economics as Toki's $20; a better free-tier lever.

### F4. What NOT to take

- **Trunked's stack** (hosted ASR + hosted frontier model). Breaks $0 upkeep and all-local, and is the
  reason they can make no grounding claim.
- **Trunked's indefinite cloud audio retention.** Their policy keeps audio as the original call record
  unless deleted. This is the thing Toki's positioning attacks.
- **Littlebird's always-on screen reading.** It drew Windows Recall comparisons on HN within hours and
  two commenters called the cloud a hard blocker. It also violates Toki's "activates only for
  meetings" rule. This is the strongest contrast asset Toki has; do not blunt it.
- **Littlebird's "verify against the original source" as an accuracy answer.** Toki should do the
  verification, not delegate it.
- **Littlebird's sprawl** (Routines, Hover, Glance, Projects, Artifacts, 150+ integrations, four
  consumer tiers). Coherent for an $11M cross-app OS; the opposite of restraint for a $20 offline app.
- **A fixed template.** Littlebird ships six sections with no per-meeting user template; Granola is the
  opposite extreme. Toki's position - a proposed structure the transcript must CONFIRM - is better
  than both poles. Do not drift toward either.

### F5. Confidence

Littlebird's six-section template is sourced directly from their docs and is reliable. **Trunked's
note structure is marketing-sourced only** - no screenshot, doc or review with a real example was
found, and its user and review numbers are self-reported with no independent trace of the product
anywhere (no HN, no Product Hunt page, no Trustpilot). Do not treat Trunked's template as
reconstructed.

---

## PART G - WHAT SHIPPED, AND WHAT IT TAUGHT (2026-09-02)

### G1. Stage 1, the instrument (`--groundtest`)

Scores every stored note against its own transcript for hard entities by exact string match, no model
involved. Precision plus recall, because precision alone is gamed by writing less.

**Its first reading was 67% and almost all of it was the instrument being wrong.** Four false-positive
classes, now mutation probes in `GroundingTests`: bullet-initial capitals read as names; spoken
numbers ("seventy percent") versus written ones ("70%"); the model's own list numbering; and the
carry-forward section, which is grounded in OTHER meetings' transcripts.

**TRUE BASELINE: 96.2% precision, 66.1% recall, 10 of 77 notes with an unsupported entity.**

**The lesson, and it is the expensive one: an instrument that cries wolf is worse than no instrument,
because it sends you off fixing a writer that was not broken.** Every number a new metric reports is a
claim about the metric first and the product second. Budget for that.

### G2. Stage 2, the thin-input gate

Below 70 substantive words the writer is never consulted. Calibrated against the real Library: median
meeting 210, the rotation failure 56, the junk 1 to 14. The first attempt guessed 120 and swallowed
every note test, which is how the calibration came to be measured rather than chosen.

It lives at the CALL SITE, not in `WriteAsync`. The writer's job is to write from what it is handed;
judging a meeting too thin to write about is the caller's.

### G3. The name gate

`Grounding.PruneUngroundedNames` drops any written line naming someone absent from the transcript or
its diarisation tags. Two deliberate limits, both learned the hard way:

- **Names only.** Numbers and dates get paraphrased legitimately ("about a hundred", "early next
  week"); pruning them deletes good content to satisfy a string match.
- **Nothing happens when the transcript names nobody.** Then an absent name is evidence the recogniser
  caught nothing, not evidence of invention. The existing
  `Real_tasks_are_never_dropped_by_the_name_filter` test caught the first version deleting real tasks.

Also: the confidence check must NOT use the same name-detection as the note side. Line-initial
suppression is right when reading a note (a capital after a bullet is grammar) and wrong when reading
a transcript ("Priya finished the migration" opens the line and Priya is still a person). Asking both
sides the same question with the same rule made the gate conclude a transcript full of names carried
none.

### G4. Stage 3, the extractive map

Transcript lines are numbered into the chunk; the map step must cite the line each point came from;
`ValidateDigest` drops any point whose citation does not hold up (out of window, uncited, or whose
content words do not appear at the cited line or its neighbours, threshold 0.5).

**Two things measured on meeting 48, a real 26-minute meeting:**

1. **First run: 2 of 4 windows produced no verifiable points and were DROPPED.** Diagnostics showed
   `uncited 31, out-of-range 0, unsupported 0` - the model was ignoring the citation format, not
   inventing. Two fixes followed.
2. **Format-first prompting took citation compliance from 0% to about 94%** (32/34 and 23/25). The
   rules used to precede the shape; a small model follows the last simple pattern it was shown far
   better than a paragraph of conditions. The required shape and a two-line example now lead, and the
   qualifications follow.

**THE RULE THIS YIELDED, and it generalises beyond this pipeline: a verification step must never be
able to delete more than it verifies.** An uncited window now falls back to the raw digest without
provenance, because losing the content is far worse than losing the citation, and an uncited digest is
exactly what the pipeline used before - a return to old behaviour, not a new failure.

Diagnostics name their counts (`uncited` / `out-of-range` / `unsupported`) rather than reporting a
bare verdict: a window that lost everything to UNCITED is a prompt problem, one that lost everything
to UNSUPPORTED is invention or a threshold set too high, and those need opposite fixes.

### G5. Next steps, in order

1. **Regenerate the Library and re-measure.** Every number above except the citation rates describes
   notes written by the OLD pipeline. Nothing shipped today retroactively improves the 77 stored
   notes, so the next real datapoint is `--groundtest` over notes written by the new one. Needs a
   persisting rewrite path (`--renotes` currently prints and discards).
2. **Wire provenance through to the note.** `ValidateDigest` now returns narrowed, verified line
   ranges and `provenance` carries them, but `NoteSection.Sources` and `ActionItem.Source` are still
   never assigned, and `MeetingStore.ReplaceNotes` does not persist `src_first`/`src_last`. The
   documented +4.6pp precision-at-1 gain is still not in the product.
   **DONE 2026-09-03.** Both joins closed, and the gain re-measured because +4.6pp no longer matched
   the library: 85.6% to 88.5% on the HARD condition (McNemar 4.17, p<0.05) and noise on the EASY
   one. See the ARCHITECTURE entry "Provenance is recorded at generation".
3. **Evidence-gated sections.** The model returns line ids per section type and CODE decides the
   section exists above a threshold. Needs one cheap grammar-constrained call per type, so it lands
   after the note format is settled.
4. **The note format** (Part C4, C5 and F2): outcome line first, commitments second and anchored,
   topic-derived headings, "For you" above the body, "Risks / open questions", decisions and questions
   as earned inline marks. Ban "discussed" as a lint rule the shape of `NoEmDashTests`.
5. **Grammar-constrain the classification passes only** (Part D4). The map citation format is the
   obvious first candidate now that its compliance is measurable.
6. **Microphone health check at meeting start** (Part F3) - Toki has measured this failure and has
   better instruments than the products that ship the check.
7. **The measure.** The note column still has no `MaxWidth`; body text runs about 140 characters in a
   wide window and 6 to 8 words in the Library pane. Owner decision 2026-08-24 stands; the number to
   come back to is 600.

---

## PART H - GLASS ON WINDOWS (2026-09-03, third round)

Run because the owner said "the glass is still fake glass... it should be very close" to Apple's.

### H1. The claim we had written down was wrong

`apple-glass-material-system.md` recorded blur-behind as permanently impossible on the notch because
of ghost mode (`WDA_EXCLUDEFROMCAPTURE`). **Withdrawn.** Microsoft's `SetWindowDisplayAffinity`
reference lists exactly one caveat - that DWM must be composing, which is a requirement rather than a
conflict - and no report of an incompatibility exists across the Electron, WPF or overlay corpus.
Stealth-overlay apps ship content protection and acrylic together routinely. At least one project
*requires* the exclusion for its blur: a window that captures the screen behind itself without
excluding itself samples its own output and greys out within frames.

**Ghost mode is a prerequisite for self-captured blur, not an obstacle to it.**

### H2. The blockers that are real

1. `AllowsTransparency=True` makes the notch a per-pixel layered window (`WS_EX_LAYERED`), which
   bypasses the frame surface DWM paints a system backdrop into.
2. The notch is never activated (`ShowActivated=False`), and Microsoft documents that acrylic and
   mica "replace translucency and texture with a solid color" when a window deactivates. A backdrop
   there would render its flat fallback essentially always.

Escape hatch, noted and not taken: the controller route (`DesktopAcrylicController` +
`SystemBackdropConfiguration.IsInputActive` pinned true) keeps a material live on an unfocused
window, but lives in the Windows App SDK, a runtime dependency that fights the self-contained dist.

### H3. The finding that reframes the whole question

Apple's own `SwiftUI.Material` documentation: **"A material blurs a background that's part of your
app, but not what appears behind your app on the screen."** On macOS the distinction is explicit API:
`NSVisualEffectView.BlendingMode` has `.behindWindow` and `.withinWindow`, and SwiftUI `Material` is
the second.

**The glass being admired is largely not sampling the desktop. The gap was a RECIPE gap.**

### H4. The recipe, as published

Microsoft's acrylic layer stack, in order: **background → blur → exclusion blend → tint → noise.**
Noise is a first-class named layer in a shipping OS material. Their four exposed knobs are
`TintColor`, `TintOpacity`, **`LuminosityOpacity`** and `FallbackColor` - luminosity being separate
from tint is the structural insight, and a single flat plate collapses both into one number.

Apple publishes no blur radii or saturation values anywhere; anyone quoting "blur(20px)
saturate(180%)" is quoting web practice. What Apple does publish: a thickness ladder (ultraThin →
thin → regular → thick → ultraThick), that the effect "isn't simple opacity", and that **vibrancy is a
FOREGROUND effect** - hierarchical text opacity that picks up the material, which is exactly Toki's
existing white-90/50/25 ladder. Setting a flat opaque colour on text over glass disables it.

Community practice, clearly labelled as such: `blur(16-24px) saturate(140-180%)`, with a brighter thin
edge layer at a smaller blur than the body, and the important structural note that a real blur must
sample pixels NEAR the surface, not only directly behind it - blurring a tight crop produces clamped
dark edges, which is the tell that reads as "a rectangle with a filter on it".

### H5. What shipped, and what was rejected

**Shipped:** the Library window gets real DWM acrylic (`DWMWA_SYSTEMBACKDROP_TYPE = 38`,
`DWMSBT_TRANSIENTWINDOW = 3`, Win11 22621+) - it is an ordinary activatable non-layered window, so it
has neither blocker. Requires BOTH `Background=Transparent` and
`CompositionTarget.BackgroundColor=Transparent`. Confirmed applied on the owner's machine. The notch
gets the recipe instead: 3% tiled grain at absolute viewport units, and an eased four-stop luminance
falloff. Both plain Brushes, so the no-Effect-in-an-animated-subtree rule holds.

**Rejected:** `SetWindowCompositionAttribute` acrylic (documented drag lag, unfixed since 2018,
unsupported API); WinUI 3 / XAML Islands (officially not embeddable in WPF); Windows Graphics Capture
live blur (coloured capture border unless borderless access is requested, which prompts the user and
needs a packaged manifest capability - three violations of hard constraint 4);
`CreateHostBackdropBrush` (reported not to update live). No NuGet dependency taken: every library in
this space is ~40 lines of P/Invoke plus a competing style system.

### H6. Open

A captured-and-blurred backdrop for the notch (GDI `BitBlt` a region padded by the blur radius,
downscale, blur off-thread, `ImageBrush`, refresh on show and lazily - never inside an animation).
Ghost mode makes it work. Only worth doing if the recipe proves insufficient beside the Library's real
glass. `BitBlt` can miss DirectComposition-only content on some drivers, so validate the captured
bitmap is not uniformly black and fall back to the recipe.

---

## PART E - THE DECISIONS THAT FOLLOW

1. **Animate by default, snap by exception.** Done 2026-09-02.
2. **A note is ranked, not merely structured.** Salience is the product; coverage is not.
3. **Fixed spine, adaptive middle.** Outcome line and commitments in fixed positions; body headings
   derived from content; decisions and questions as earned inline marks, promoted to a block only
   above a threshold of anchored items.
4. **Evidence decides sections, code decides existence.** The model never votes.
5. **The map step emits verbatim spans with line ids**, never paraphrase.
6. **Ban "discussed" and its family** as a lint rule, the same shape as `NoEmDashTests`.
7. **A task completed during the meeting is not an action item.**
8. **An unanchorable commitment is dropped, not shown.**
9. **Measure before improving.** `--groundtest` lands before any format change.
10. **Never build automatic sharing or emailing.** The corpus is uniformly hostile.
11. **Retention lives in search and cross-meeting open commitments**, not in re-reading a note.
