# Telling the speakers apart, without keeping anyone's voice

*T8, built 2026-08-12. Owner decisions: **no persistent voiceprints**, **diarisation runs after the
notes**, **drag-reorder removed**.*

---

## The problem

Toki records two streams: `you.wav` (your microphone) and `them.wav` (the meeting's own audio). That
means the person using Toki is already perfectly separated - better than any model achieves - but
**everybody else collapses into one label, "Them"**. In the 11-person call recorded on 2026-08-11 that
was eleven people sharing one name, and it is what let a roll call be read as a list of to-dos: with no
speaker boundaries, a register read aloud looks exactly like one person listing commitments.

---

## The decision that shaped everything: no voiceprints

The obvious way to name a voice is **enrollment**: store a fingerprint per person, recognise them next
time. Circleback does this; you rename a speaker once and it remembers. It is genuinely useful.

Toki does not do it, and this was the owner's call after looking at what the category actually does.

**Wispr Flow shipped a bot-free meeting notetaker on 5 August 2026** and states, on the product page and
in the FAQ:

> "Notetaker does not create voiceprints or biometric profiles of you or anyone on your calls."

They name speakers anyway, from the calendar invite, a personal dictionary, and conversational context -
"thanks, Priya". A company with servers, a paid tier and an enterprise pitch chose not to store
biometrics and then made "no voiceprints" a marketing line. That is the category telling you which side
of this trade is worth being on.

The legal picture agrees. Storing a voice fingerprint for a colleague who never agreed to it is the fact
pattern in *In re Otter.AI* (N.D. Cal. 5:25-cv-06911) and *Cruz v. Fireflies.AI* (N.D. Ill.). Toki's
mitigation would have been that nothing leaves the laptop - a real defence, and the deciding factor in
most relevant US cases - but "we have a good defence" is a worse position than "there is nothing to sue
over".

**So: cluster voices within a meeting, name the clusters from the conversation, forget everything.**
Reopen the same meeting tomorrow and Toki has no idea whose voice it heard. What persists is text, which
you can rename or delete like any other text.

The one thing given up: a recurring participant who is not on the invite has to be renamed each time.
`AppSettings.PinnedNames` already covers the people who recur.

---

## How a voice gets a name

`Transcription/SpeakerNamer.cs`. No audio, no model, no stored state - which is why it is testable
against a real transcript and why it cannot remember anyone.

| Signal | Example | Who it names | Score |
|---|---|---|---|
| **Self-introduction** | "I'm Priya", "Priya here", "Priya, present" | the speaker | 6 |
| **Handover** | "over to you, Priya" (end of turn) | the person who speaks **next** | 3 |
| **Thanks** | "thanks, Priya" (start of turn) | the person who spoke **before** | 3 |
| **Vocative** | "Priya, can you take this?" | the person who speaks **next** | 3 |

Below a total of **3.0**, the voice keeps its number. One name maps to one cluster in both directions:
if two clusters both look like Priya, one of them is wrong, and the weaker one stays "Speaker 2".

### The four refusals

A wrong name is worse than no name - the same rule the evidence links follow.

1. **The user's own name is never given to a remote voice.** In a one-to-one the other person says your
   name constantly, and every one of those is about *you*.
2. **A name nobody vouched for must be said at least twice.** One capitalised word is likelier a
   mishearing than a colleague. Self-introductions are exempt: the speaker is telling you.
3. **A word said in lowercase anywhere in the same meeting is not a name.** See below - this is the
   important one.
4. **The vocative signal only counts for a name a human supplied** (calendar attendee or pinned).

---

## What the first real run did, and what it cost

The rule is verify by running. The very first `--speakertest` against a real recording produced
participants called **"Please"**, **"Otherwise"** and **"Claudia"**, from lines like:

```
Please, please break out right away, everyone.
Otherwise, folks can break out.
```

Both are perfect vocative shapes. Neither is a person. **A hand-written list of words-that-are-not-names
is never finished** - extending it would have fixed those two and missed the next two.

The fix that generalises: **collect every word that appears in lowercase anywhere in this meeting, and
refuse to treat any of them as a name.** "please", "otherwise", "great", "sorry" all appear lowercase
constantly. A real name essentially never does. One pass over the text, and it catches words no list
would have anticipated.

That killed "Please" and "Claudia". **"Otherwise" survived** - it happened never to be said in lowercase
in that particular transcript. Hence refusal 4: the vocative is the weakest and most ambiguous shape,
and it lives exactly where discourse markers live, so it now requires a human-supplied name. The
sentence-adverbs went into the shared stopword list as well, as a second layer.

*Worth keeping: the general rule got 2 of 3, and the last one still needed a specific guard. "Find the
principled fix" and "handle the residue" are both required; the principled fix alone was not enough.*

---

## The threshold, and which way to be wrong

Measured on two real meetings, average linkage:

| Threshold | a 1:1 (truth **1**) | an 11-person call (truth **~10**) |
|---|---|---|
| 0.55 | 5 | 7 |
| 0.65 | 4 | 5 |
| **0.80** | **1** correct | 2 |

**No single value serves both**, so the only question is which way to be wrong.

- **Under-splitting** merges several quiet people into one label. That is today's behaviour, and it
  reads as coarse.
- **Over-splitting invents colleagues who were never in the room** and puts words in their mouths.

0.55 did exactly that on first contact: one person reading announcements came back as **six speakers**.
The default is now **0.80**, and the real answer comes from the attendee count when a calendar supplies
one - passing the true count works perfectly.

*When estimation is genuinely unreliable, make CORRECTION cheap instead of making estimation confident.*
Re-clustering takes about five seconds, which is what makes a user-settable count affordable.

---

## Measured on the owner's PC, real recordings

| Meeting | Truth | Voices found | Named | Time |
|---|---|---|---|---|
| 75 - a 1:1, 8.4 min | 2 (you + 1) | **2** correct | **"Devi"**, from *"Thank you, Devi."* | 6.5 s |
| 76 - 11-person call, 6 min | ~10 | 3 (under-split, no calendar) | none | 8.2 s |
| 77 - announcements, 5 min | 1-2 | 2 | none | 8.8 s |

Meeting 75 is the shape this is built for: two voices correctly separated, and the remote person named
from a single thank-you, with no calendar connected and no voice stored.

Meeting 76 is the honest limit. Ten people, no calendar, conservative threshold: three voices. Better
than one, well short of ten, and the fix is the attendee count rather than a bolder threshold.

---

## Where it runs, and why the notes never wait

**After the notes**, on the same background thread in `App.ResolveSpeakers`. The notes are what the user
is standing there waiting for and nothing may delay them - the owner was explicit: *"I don't like the
idea of increasing the wait time, it is already taking a while."* Diarisation then costs ~7s on an
8-minute recording, about 50s for an hour.

That creates an obvious problem: the note writer never sees the names, so a to-do cannot say who owns it.
The tempting fix - run diarisation first - is exactly the one that was rejected.

**The note arrives in two waves instead.**

1. Summary, key points and to-dos land as soon as the model has written them. Complete and readable.
2. A few seconds later the speakers resolve, and the transcript gains names and the to-dos gain owners,
   **in place**.

While wave 2 is pending, the two sections that are about to change show `ThinkingDots` - the same
three-dot pulse the notch already uses for "thinking" - captioned *"telling the speakers apart"* and
*"working out who owns these"*. This is not decoration. **A note that silently rewrites itself while you
are reading it is unsettling in a way that a slower note is not**; showing the work makes the wait
legible and the change expected. Both the notch and the Library window subscribe to
`App.SpeakersResolved` and re-read the meeting when it fires.

### The owners come free - no second model pass

The part that makes this cheap: **every to-do already has an evidence link** (N7) to the transcript line
it came from. The moment that line knows who spoke it, the owner of a volunteered to-do is sitting right
there. `Llm/ActionOwners.cs`, deterministic, no LLM call, no added wait.

The rule that keeps it honest - **only a first-person commitment names its speaker**:

```
"I'll send the deck by Friday"    said by Devi  ->  owner: Devi     (volunteered)
"Craig will send the deck"        said by Devi  ->  owner: nobody   (reporting)
"You will need to update it"      said by Devi  ->  owner: nobody   (assigning)
```

Getting that backwards would attribute other people's work to whoever happened to mention it. An unowned
to-do is obviously unfinished; a wrongly-owned one looks finished and is quietly false. An owner the
model already found is never overwritten, and neither is one the user typed - the `UPDATE` is guarded on
`owner IS NULL OR owner=''`.

This closes **T4** as a side effect of T8, which is the second time this round that one structural fix
paid for a symptom listed somewhere else.

*General shape worth keeping: when a slow step would delay a fast one, look for what the fast result
already knows. The evidence links had been sitting there since N7 with the answer in them.*

---

## Correcting it

`Views/SpeakerChips.cs` puts a row of people above the transcript, each a click from being renamed. One
rename updates every line that voice said - "assign once, applies across the transcript", the same
affordance every notetaker that ships automatic naming provides. A feature that is usually right and
impossible to fix is worse than one that never guesses.

A rename is a plain `UPDATE` on a text column. There is no model to retrain and nothing remembered for
next time, which is exactly why it is instant.

---

## Structure

| File | What it does |
|---|---|
| `Transcription/SpeakerDiariser.cs` | embeddings + agglomerative clustering (average linkage) |
| `Transcription/SpeakerNamer.cs` | **names the clusters from the conversation.** Pure text |
| `Transcription/SpeakerResolver.cs` | the whole chain for one meeting: audio to tags |
| `Llm/ActionOwners.cs` | **to-do owners from the evidence link.** No model call |
| `Views/SpeakerChips.cs` | the rename row |
| `Views/ThinkingDots.cs` | the shared "still working" pulse, used while wave 2 is pending |
| `TranscriptSegment.Tag` / `.Label` | who said it, and what to display |
| `transcript.speaker_tag` | the column. NULL = never diarised |

**`Speaker` stays a two-value enum on purpose.** It is which *stream* the bytes came from - a fact - and
it is what the `you:`/`them:` search prefixes and `COUNT(DISTINCT speaker)` rely on. Who *spoke* is an
inference and lives in `Tag`, which can be absent, wrong, or renamed.

**`TranscriptMerger.SameVoice` is the highest-risk line in this change.** It must not be
`a.Speaker == b.Speaker`: every remote participant answers to "Them", so two different people talking
back to back with a short gap - the most ordinary thing in a group call - would be welded into one turn
attributed to whoever spoke first.

---

## Verify

```
Toki.exe --speakertest [meeting id]     # the whole chain on a real meeting. READ-ONLY
Toki.exe --diartest <them.wav> [n|sweep]
```

`--speakertest` writes nothing and stores no voice data, so it is safe to run against the real library.

## Does the pinned-names box still earn its place, now that naming is passive?

Asked by the owner 2026-08-13. Measured rather than argued, by letting `--speakertest` take a roster for
one run (`--speakertest 76 +Craig +Yashtren`) so nothing has to be saved to find out.

**The first run found a bug rather than an answer.** With "Yashtren" on the roster, meeting 76 named the
CHAIR Yashtren - from the chair's own line *"Yashtren, are you there?"*. Addressing somebody names the
person who **answers**; the code named "the next turn", and the chair simply carried on talking, so the
next turn was still the chair. Fixed: handover, vocative and thanks now look for the next (or previous)
**different** speaker, within 4 turns - beyond that it is a different part of the conversation, not a
reply. Two tests pin it, both built from this transcript.

**After the fix, the roster changed nothing for meeting 76** - and that is correct. Nobody ever answers
"Yashtren, are you there?" in that recording, so there is no evidence to name anyone, and the honest
result is three numbered voices.

### So where the roster actually earns its place

| | overlaps with passive naming? |
|---|---|
| **Fixing a mis-heard SPELLING** (`Yashtren` -> `Yashina`) | **No overlap at all.** Diarisation names a voice; `NameCorrector` fixes the letters in the text. A pinned name gets a wider match reach (raw 5 / key 2 vs 3 / 1) and is the *only* way to settle a name the library's own frequency cannot |
| **Unlocking the vocative signal** | Yes, and it is decisive: a bare vocative counts ONLY for a rostered name, because "Otherwise, folks can break out" is the same shape |
| **Names that are also words** (Grace, Hope, Sunny) | Yes: the lowercase-elsewhere rule kills them unless a human vouched |
| **A name said only once** | Yes: the say-it-twice rule |

**But the calendar does most of this automatically.** `NameLexicon.Trusted(attendeeNames)` folds in
attendee display names, so any meeting with an invite gets a roster for free. The typed box is the
fallback for ad-hoc calls with no invite, for people who join without being on it, and - its one job that
nothing else can do - for a name the recogniser consistently mangles.

*Verdict: keep it, but it is narrower than it looks. Connecting the calendar is the higher-leverage move
for naming; the box's unique value is spelling.*
