# What two real meetings found that 1,800 tests did not

*2026-08-11. The first time Toki was pointed at real meetings with real people in them.*

Two recordings, back to back:

| | Meeting 75 | Meeting 76 |
|---|---|---|
| Title Toki gave it | "Supervisor update with Abhishek" | "Evacuate Immediate Action Required" |
| Length | 8 min | 6 min |
| People | 2 | ~11 |
| Transcript segments | 182 | ~180 |
| Note size | 10 KB | 12 KB |

Everything below was found by reading the notes Toki produced, not by running tests. The test suite was
green throughout. That is the single most important fact in this document.

---

## The failures, in the order they hurt

### 1. The Summary was raw transcript. Twice.

Meeting 75 opened with:

> **Summary:** Hello. Uh hi Sai, you are actually on mute. Hello. Hey Davy. Yeah. Hi, how are you okay?
> I'm good, I'm good. So actually Abhishek is one of the supervisor

Meeting 76 opened with the first four lines of its transcript too.

**Root cause.** The AI pass *worked* both times - the sections, actions and title were all model-written.
But the model returned no `SUMMARY`, and `LlmNoteWriter` had:

```csharp
Tldr = string.IsNullOrWhiteSpace(note.Summary) ? heuristic.Tldr : note.Summary
```

`NoteExtractor.Tldr` is the first ~140 characters of raw transcript sentences. It exists as the floor for
users with no model at all. It was being used as a patch for a model that *had* answered, and the result
was a note that looked broken under a set of perfectly good sections.

**Fixed** (T3): ask again with a summary-only prompt; if that fails, leave the summary EMPTY and let the
renderer drop the section. A missing section is honest. Raw transcript labelled "Summary" is not.

### 2. The bad summary poisoned the title

"Evacuate Immediate Action Required" was a meeting about centralising team reports. The title is generated
*from* the summary, and the summary was the opening words of the call - people who were not team leads
being asked to drop off. Fixing (1) fixes this; with no summary, the title is now derived from the
sections instead.

### 3. The transcript was shredded, and speakers interleaved mid-sentence

182 segments in 8 minutes is one every 2.6 seconds. A single continuous explanation became twelve
paragraphs, each with its own speaker label. Worse, short utterances from the other side were spliced
into the middle of sentences:

```
You:  "And did did did yeah, did Craig mention ask"
Them: "idea."
You:  "you to like do this?"
```

**Root cause.** `TranscriptMerger.Merge` did echo-dedup and then `OrderBy(StartMs)`. **No same-speaker
merging existed at all.** Two parallel streams sorted by start time will always interleave like this.

**Fixed** (T1): consecutive lines from one person join into a turn (1.5s gap, capped at 700 chars so a
monologue still breaks into readable paragraphs); short backchannels are moved out of the middle of
someone else's sentence. Timestamps are preserved, so evidence links still resolve.

This one fix cascades: fewer paragraphs in the note document (a UI win), better chunk boundaries for the
note writer, and a transcript a person can actually read.

### 4. Names

| Heard | Actually | Where |
|---|---|---|
| Greg | Craig | m75 line 106, and in the note's Decision |
| Abashik | Abhishek | m76 line 196 |
| Yashtren | Yashina | m76 line 50 |
| Panita | Punita | m76 line 442 |
| Davey / Dave | one person | m76 |
| Proteleka / Protocol / Prayer Lika | one person, three ways | m76 |
| Anirud | Anirudh (the owner's own name) | m76 line 212 |
| educative | executive | m75 line 70 - and the speaker corrects it out loud on line 74 |
| infraction | a system name | m75 line 52 |
| 15-20 teens | teams | m75 line 98 |

**The decisive observation:** meeting 75 contains BOTH "Craig" (from the mic stream) and "Greg" (from the
loopback stream), once each. Within one meeting it is a dead tie. Across the library it is not close -
meeting 76 says "Craig" four times and "Greg" never.

**Fixed** (T2): see "The name decision" below.

### 5. A roll-call became eleven to-dos

```
## To do
- [ ] Priyanka   - [ ] Abashik   - [ ] Yashina   - [ ] Davey
- [ ] Dakshita   - [ ] Ganesh    - [ ] Anirud    - [ ] Bindu
- [ ] Punita     - [ ] Proteleka - [ ] Serata
```

The chair was reading a register ("Anirud, Bindu.", "Dakshita, Ganesh."). Every name became a task.

**Fixed** (T7): a task needs an action in it. A bare name, or a pair of names, is a person. Near-duplicate
tasks also collapse (m75 had "connect Abhishek with..." twice).

### 6. Action items lost every owner

All four of m75's to-dos were things the OWNER agreed to do, and not one said so. The prompt asks for
`task | owner: name`; the owners never arrived. **Still open (T4).**

### 7. Eleven people, one label

Meeting 76 was a large team call. Toki's YOU/THEM model flattens every remote participant into "Them".
This is what let the model read a register as tasks - it had no way to know a name being called is not a
name being assigned work.

**In progress (T8, diarisation).** See below.

### 8. Template misfit

Both meetings were classified "Decision log" (Options considered / Decision / Rationale). Neither made a
decision; one was information-gathering, the other a status round-up. The Rationale section then reads as
invented justification: "Automation improves transparency and reduces errors" - a fair paraphrase, but
nobody said it. **Not yet addressed.**

---

## The name decision, and why

Three options were on the table.

**A. Contextual biasing at decode time (hotwords).** The principled fix: feed expected names into the
recogniser so it prefers them. sherpa-onnx supports it, and
[PR #3077](https://github.com/k2-fsa/sherpa-onnx/pull/3077) (merged Feb 2026) added it specifically for
NeMo TDT models like Parakeet. The .NET binding exposes `HotwordsFile`, `HotwordsScore`, `ModelingUnit`
and `BpeVocab`.

**Rejected, for two concrete reasons.** Hotwords on a BPE model require a `bpe.vocab`, and
[the Parakeet TDT int8 archive Toki ships](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/tree/main)
contains only `encoder`, `decoder`, `joiner` and `tokens.txt` - no BPE vocabulary. It would also force
`modified_beam_search` over `greedy_search`, which costs real time on a CPU for a long meeting.

**B. Post-ASR correction from the user's own history.** Chosen. Deterministic, unit-testable, free at
runtime, needs no extra download - and, decisively, **it repairs the 50 meetings already saved**.

This is also what the category does. A patent on
[meeting-adapted language models](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/11636854)
describes building a custom vocabulary from the participant list, and commercial tools ship it as
["custom vocabulary" / "custom terms"](https://equaltime.io/2025/09/23/custom-vocabulary-with-equal-time-ai-meeting-notes-that-capture-your-unique-names-acronyms-and-terms/).

**C. Ask the user to type names.** Kept, but only as the escape hatch for what (B) cannot decide.

### How it decides

Names that sound alike are clustered (compact Metaphone key + edit distance), and each cluster collapses
to one spelling, in strict order:

1. **A trusted spelling** - the user's own name from Settings, a calendar attendee, or one they pinned.
   Typed by a human, therefore correct.
2. **Otherwise, the most frequent spelling across the WHOLE library.** One meeting saw Craig once and
   Greg once; the library has seen Craig many times and Greg once.

A cluster that cannot be decided is **left exactly as it was**.

### The guards, and why each exists

- **Ordinary words are excluded before matching.** "Great" and "Craig" are phonetically identical.
  Rewriting the first would be the worst thing this code could do.
- **Sentence-initial words are excluded.** Capitalised for grammar, not because they are names.
- **Two trusted spellings in one cluster means two real people** who happen to sound alike. Nothing is
  touched.
- **A dead tie is not broken.** A coin flip is not an answer.
- **A trusted name may reach further** (edit distance 5 vs 3, key distance 2 vs 1) because the user
  asserted it. The risk stays bounded: a loose match can only ever rewrite *towards* a human-supplied
  spelling.

### What it deliberately does NOT fix

"Yashtren" -> "Yashina" is four edits apart with different phonetic keys, and stays uncorrected. Widening
the thresholds far enough to catch it also starts merging names that are genuinely different people. The
two mistakes are not equal in cost: one is a missed fix, the other rewrites what somebody said into
somebody else's name. Pinning the name in Settings fixes it. There is a test asserting both halves of
this.

---

## Diarisation (T8) - BUILT 2026-08-12

> **Done. Full write-up: `docs/speaker-naming.md`.** Owner decisions: **no persistent voiceprints**,
> **diarisation runs after the notes**, **drag-reorder removed** (which is what made the Library
> virtualization safe - drag was the one feature it silently breaks; 424ms → 46ms).
>
> The deciding evidence on voiceprints: **Wispr Flow shipped a bot-free notetaker on 5 August 2026 that
> states "does not create voiceprints or biometric profiles of you or anyone on your calls"** and names
> speakers from the calendar invite, a personal dictionary and conversational context instead. A company
> with servers and an enterprise pitch chose not to store biometrics, and made that a selling point.
>
> Measured on the real recordings: **meeting 75 gets 2 voices correctly separated and names the remote
> person "Devi" from a single "Thank you, Devi."** Meeting 76 under-splits (3 voices, truth ~10) with no
> calendar connected - the conservative side, deliberately.
>
> **The first real run invented participants called "Please" and "Otherwise."** Fixed with a general
> rule - *a word said in lowercase anywhere in the meeting is not a name* - plus a specific one, because
> the general rule caught 2 of the 3 and "Otherwise" simply never appeared lowercase in that transcript.
> Threshold moved **0.55 → 0.80** after 0.55 turned one announcer into six people.

The two-stream design turns out to help a lot. `you.wav` is the microphone, so **the local user is
already perfectly separated** - better than general diarisation gets. Only `them.wav` needs splitting,
and it comes with a free labelled sample of the owner's voice for comparison.

sherpa-onnx 1.13.4, already shipped, already exposes what is needed in its .NET binding:

- `OfflineSpeakerDiarization` + `OfflineSpeakerDiarizationConfig`
- `OfflineSpeakerSegmentationPyannoteModelConfig` (segmentation)
- `SpeakerEmbeddingExtractorConfig` + `FastClusteringConfig`
- `SpeakerEmbeddingManager` with `Add` / `Search` / `Verify` / `GetAllSpeakers` - the primitive for
  **voice enrollment**, i.e. name a voice once and recognise that person in future meetings

So no new dependency; two small model downloads. Licence, model sizes, CPU cost and the naming UX are
under research and are exactly the things not to guess at - a non-commercial or gated model would force a
different choice.

---

## Learnings worth keeping

**Real use found what 1,800 tests could not.** Every failure above was invisible to the suite because the
suite tested the layers, not the artifact. The tests written since are all built from these two
transcripts.

**A fallback is a product decision, not an implementation detail.** `heuristic.Tldr` was a perfectly good
floor for a user with no model. Used as a patch for a model that answered, it silently printed raw
transcript into the most-read part of the note. Any fallback that can be reached in a state its author
did not intend needs a guard, not a comment.

**When two sources disagree, count across the corpus, not the sample.** Craig/Greg is undecidable inside
one meeting and obvious across a library. The same shape appears in the sticky-note linking decision:
local evidence is often a tie that global evidence settles.

**Asymmetric costs set the thresholds.** Missing a name fix costs a slightly wrong transcript. A wrong
name fix rewrites what somebody said. When the costs are that lopsided, tune conservative and give the
user an override - do not chase recall.

**One structural fix can pay for several symptoms.** Merging same-speaker segments fixed readability, cut
the note document from 182 paragraphs to roughly 40 (a UI win), and gave the note writer better chunk
boundaries. Before reaching for three fixes, check whether one cause explains all three.
