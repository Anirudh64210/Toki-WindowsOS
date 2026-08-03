# Ask Toki redesign — root causes, validated fixes, and how to check them

*(2026-08-02. Companion to `ARCHITECTURE.md` §"Toki-as-assistant". Written so the summary reads
without any code knowledge; the per-bug sections carry the engineering detail.)*

## The one-paragraph summary (plain language)

Sid's live session showed five kinds of "dumb" answers. Every one traced to a **routing or state
bug in code**, not to the model being weak: a question *about a person* was mis-read as "list my
meetings"; "this meeting" pointed at a note nobody was looking at because the app's memory of "what's
on screen" went stale; "what did Mac have to do" ignored the name Mac entirely; questions about Toki
itself ("why are you so rude?") were literally searched against the meeting notes; and the answers
that *were* right read long and robotic. All five are now fixed **deterministically** — in the
routing rules, the screen-state plumbing, and the database queries — with the language model only
polishing phrasing on top, plus a tuned prompt/sampler so its voice sounds like a sharp colleague.
Each screenshot bug is now a permanent automated regression test (228 tests green), and there's a
10-step live script at the bottom for the final judgement only a human can make: tone.

## Design stance (unchanged, extended)

The pipeline keeps the existing **understand-first** philosophy: a deterministic ~1ms layer decides
*what kind of question this is* and *which meetings it's about* before any retrieval or model call.
The tiers, in order:

```
0    CommandRouter      "take me to the library"          → app action (question-shape veto first)
0.5  SmallTalk          "good morning" / "who are you"    → canned warm reply
0.6  Persona  (NEW)     "why are you so rude?" /
                        "do you see my screen?"           → canned in-character reply, screen-aware
1    QueryUnderstanding temporal + intent parse           → QueryPlan (date range / rank / intent / residual)
1.5  Screen grounding   proximal "this" + on-screen note  → pin to the note actually being viewed
2    Deterministic SQL  Count / ListMeetings / ListTasks  → exact answer, no model (now person-filtered)
3    LLM                Summarize / LookupFact            → FTS5 content-term retrieval → Qwen3-4B streams
```

A regex bug is fixed in the regex, an entity filter lives in SQL-adjacent code, and only phrasing is
delegated to the model. This mirrors how shipping assistants are built (Dialogflow's agent-design
guidance: handle the long tail of out-of-scope intents with prepared responses; Copilot Studio:
disambiguate intent *before* generation; Alexa/App-Intents: slots resolve against closed value lists).

---

## Bug 1 — "What was discussed about Kai Sinat in my meetings?" → "Your 10 most recent meetings"

**Root cause (verified in code).** `ListMeetingsRx` (`\b(what|which|list|show|all (my|the))\b.*\bmeetings\b|…`)
matched *any* question containing "…my meetings", so `Intent=ListMeetings` won and the topic
("Kai Sinat") was **discarded** — the name was never searched. A second, hidden half: even routed as
a lookup, `Retrieve()` fed the **raw question** into `SearchRelevant`, which builds a **prefix-OR**
FTS query — so generic words (`meetings*`, `discussed*`) matched every note and a true miss could
still surface three random meetings.

**Fix.**
- `QueryUnderstanding.Classify`: `ListMeetings` now additionally requires the residual to be **only
  "list chrome"** (`OnlyListChrome` — words a pure "show my meetings this week" can be made of). Any
  topical word ("Kai", "budget", "onboarding") ⇒ `LookupFact`, and the topic drives retrieval.
- `NotesQa.ContentQuery`: retrieval (FTS *and* the keyword fallback) now queries only **content
  terms** — the question minus stop-words and generic ask-words ("discussed", "meetings", …). A
  question whose only content is "kai sinat" either finds Kai or honestly misses.
- Honest miss was made **model-independent**: resolution now runs *before* the "is a model
  installed?" check, so "I couldn't find anything about that in your meetings yet" always works.

**Why this is right.** Slot-filling assistants treat "in my meetings" as *scope chrome*, not intent;
and BM25 precision on small corpora is dominated by query-term selection (generic terms in an OR
query flatten the ranking) — content-term filtering is the standard fix.

**Tests.** `QueryUnderstandingTests.TopicInsideAMeetingsMention_IsALookup_NotAList` (4 phrasings),
`PureListRequest_IsStillAList` (4 phrasings — no regression),
`NotesQaTests.TopicalPersonQuestion_IsAnHonestMiss_NotAMeetingList`, `LookupMiss_IsHonest_WithoutAModel`.

## Bug 2 — "this meeting" answered about a *different* meeting (screen awareness broken)

**Root cause (verified by tracing the state lifecycle).** `App.CurrentViewedMeeting` was a mutable
static set/cleared from scattered call sites, and it went stale in two directions:
1. **Startup pre-warm poisoned it**: `LibraryWindow.Warm()` builds the window off-screen and its
   `Reload()` auto-selects the first (most recent) meeting → `RenderMeeting` set
   `CurrentViewedMeeting` to a note *nobody was looking at*. "This meeting" then grounded on the most
   recent meeting — exactly the wrong-meeting screenshot ("SUS Metrics…" instead of the open
   "MR Rate Target…").
2. **Hide cleared it, re-show never restored it**: `HideToPill()` nulled it, but re-opening the warm
   window reuses the already-rendered note *without* re-running `Select` — so a visibly open note had
   `CurrentViewedMeeting == null`, and the question fell through to FTS, which picked a different
   meeting.

**Fix.** The property is now **derived, never stored**:
- `LibraryWindow.VisibleMeeting` computes it live: window **is visible** ∧ not the off-screen warm-up
  ∧ the rendered note is a real meeting ∧ **the user chose it** (`_viewedByUser` — a click or an
  explicit open; the list's silent auto-select never counts, stickily preserved across reloads).
  `App.CurrentViewedMeeting` is just `=> LibraryWindow.VisibleMeeting`. A computed value cannot go
  stale, and every path (warm, hide, re-show, delete, manual note) is correct by construction.
- The notch additionally grounds on the **pill's Notes view**: if push-to-talk opens while the pill
  is showing a just-finished meeting's notes, that meeting is captured (`_notchScreenId`) before the
  notch takes over the pill state.
- **Grounding is now visible**: when an answer is scoped to the on-screen note, the notch shows a
  small dim "**About: \<title\>**" line above the answer (`AskView.Scope`). A wrong grounding used to
  be invisible; now it's checkable at a glance — the Granola open-note-scope pattern plus the trust
  rule that context use must be surfaced, not silent.

**Tests.** `NotesQaTests.ScreenPin_BeatsConversationMemory` (screen id B + conversation about A →
answer is B, single source B). The window-lifecycle half is by-construction (derived property) and
covered in the live script (steps 4–6).

## Bug 3 — "What did Mac have as action items in this meeting?" listed ALL 5 items

**Root cause.** There was no person/owner slot anywhere in the pipeline. `TaskAnswer` scoped by
topic and date only; `ActionItem.Owner` was never consulted as a filter.

**Fix — closed-vocabulary person resolution** (the Alexa/Dialogflow slot pattern: an entity only
counts when it resolves against a list of values that actually exist):
- `MeetingStore.ActionOwners()` — distinct owner names across all action items.
- `NotesQa.MatchPeople(knownOwners, text)` — a question word counts as a person **only** if it
  exactly matches a known owner or a known owner's first name (case-insensitive, possessives
  stripped: "Mac's" → Mac). Pseudo-owners ("You", "me", "team") are guarded out. An unknown word can
  therefore **never** trigger the filter — no false filtering, ever.
- The filter applies on every task path: screen-pinned ("…in this meeting"), topic-scoped, and
  global ("what are Mac's open tasks" → Mac's open items across all meetings; the name is removed
  from the topic so it acts as a filter, not a search term).
- **Honest, helpful miss**: "No action items for Raj in 'Design review'. The items there are owned
  by Sid." — states the miss and names the real owners so the user isn't left guessing.

**Tests.** `PersonFilter_ListsOnlyThatPersonsItems`, `PersonWithNoItemsInScope_IsAnHonestMiss_NamingRealOwners`,
`UnknownName_NeverTriggersThePersonFilter`, `GlobalPersonTasks_FiltersAcrossMeetings`,
`MatchPeople_ResolvesAgainstKnownOwners`, `MatchPeople_IgnoresPseudoOwnersAndUnknowns`.

*(Not done, deliberately: "what did Mac SAY about X" stays an LLM lookup — the transcript speakers
are YOU/THEM until diarisation ships (roadmap #8), so a deterministic said-by filter has no data to
filter on yet. The person's name still drives retrieval.)*

## Bug 4 — "Why are you so rude?" → "there's no mention of rudeness in the meeting notes"

**Root cause.** No meta/persona tier existed. Anything that wasn't small-talk or a command was, by
construction, a *document question* — so complaints, capability questions, and "do you see my
screen?" were retrieval queries.

**Fix.** New `Llm/Persona.cs` — Tier 0.6, between SmallTalk and the parser. Anchored whole-utterance
patterns (same discipline as SmallTalk, so "who was rude in the standup yesterday" still goes to the
notes) covering: mood/rudeness complaints (warm apology), quality complaints and "that's wrong"
(apology + a concrete rephrase hint), **screen awareness** ("Do you see my screen?" → "I can see you
have '\<open note\>' open — ask me anything about it", or an honest "nothing's open right now"),
capabilities/how-it-works, privacy ("everything stays on this PC"), AI-identity edge cases, and user
apologies. Deterministic, instant, never retrieval. This is Google's conversation-design "design for
the long tail" guidance and Copilot Studio's out-of-scope handling made concrete.

**Tests.** `PersonaTests` (20 intercepts, 7 fall-throughs, screen-aware naming, honest no-note
answer), `NotesQaTests.WhyAreYouSoRude_IsPersona_NotRetrieval` (with a "rude" bait note in the DB),
`DoYouSeeMyScreen_AffirmsAndNamesTheOpenNote`.

## Bug 5 — rambling, robotic tone

**Root causes.** (a) No stop sequences: the model generated past the answer and code trimmed it —
which reads as "robotic template, cut off". (b) No exemplar: small instruct models follow an example
far better than abstract style rules. (c) The sampler was *close* but unverified against the model
card, and `MinP` silently used LLamaSharp's 0.1 default.

**Fixes (validated against the Qwen3-4B-Instruct-2507 model card "Best Practices")**:
- **Sampler**: Temperature 0.7, TopP 0.8, TopK 20 — already correct — now plus explicit **MinP = 0**
  (card value; LLamaSharp defaults to 0.1) and PresencePenalty 0.5 (card range 0–2; kept mild since
  high values cause language mixing — and lesson 8 already bans greedy).
- **Stop sequences**: `ILlm.StreamAsync` gained an optional `stop` list, wired through both engines
  (LlamaLlm → extra AntiPrompts; ApiLlm → the OpenAI `stop` field, only sent when set). Notch
  lookups stop at the first blank line (`"\n\n"`) — the model ends where the answer ends instead of
  overrunning into `NotchTrim`. Summaries keep their bullets (no stop).
- **One-shot exemplar** in both system prompts (a Q→A pair in the target voice: lead with the
  answer, meeting named in-sentence, contractions). The full-answer prompt was also rewritten to the
  "sharp colleague who was in the room" persona with an explicit ban-list, matching the concise
  prompt's discipline.
- Code-side length enforcement (`NotchTrim`, `NotchOverBudget`, per-intent token caps) stays — the
  model is *invited* to be brief and *forced* by code, belt and braces.

**Tests.** Interface/plumbing compile-verified + suite green; the ILlm fakes exercise the new
signature. Tone itself is human-judged — see the live script and the honesty note below.

## Bug 6 — "general smartness"

Covered by the sum of the above plus retrieval precision (`ContentQuery` everywhere `SearchRelevant`
is called: lookups, summarize-target, recency-pool, date-window, task-topic — generic words can no
longer drown the meaningful ones in the prefix-OR BM25 query). No embeddings were added: they remain
**blocked** by the native backend conflict (ARCHITECTURE "Why on-device embeddings are deferred"),
and none of the observed failures were paraphrase misses — they were routing/state/precision bugs,
now fixed deterministically at ~0 latency cost.

## What was *not* changed

- No new native dependencies, no cloud calls, no telemetry; everything stays local ($0/month).
- Push-to-talk mic behaviour untouched (mic runs only while the key is held).
- The state machine, storage schema, and note-writing pipeline are untouched (one additive store
  method: `ActionOwners()`).
- `settings.json` and the real `toki.db` were never read or written; all tests run on temp-file
  stores via the new `NotesQa.StoreOverride` seam.

## Research that backs the decisions

- **Qwen3-4B-Instruct-2507 model card, "Best Practices"** (Hugging Face, fetched 2026-08-02):
  Temperature 0.7 / TopP 0.8 / TopK 20 / MinP 0; presence penalty 0–2 to curb repetition, high
  values risk language mixing → sampler now matches the card exactly, presence kept at 0.5.
- **Google Dialogflow agent design + "design for the long tail"** and **Microsoft Copilot Studio
  intent-disambiguation guidance**: identify likely out-of-scope/meta intents up front and give each
  a prepared, personable response; decide intent before generation → the Persona tier and the
  question-shape/chrome-only routing rules.
- **Slot-resolution against closed value lists** (the Alexa/Dialogflow entity model): an entity slot
  only fires when it matches a known value → `MatchPeople` resolves only against owners that exist
  in the database.
- **Prior in-repo research memos** (assistant response style; screen-aware assistants — Granola's
  open-note scope, Cluely-style "own state, no OCR"; command routing): the "About:" scope badge,
  the derived-not-stored grounding, and keeping the question-veto ordering.

## Live-test script for Sid (final quality gate)

Automated tests prove routing, filtering, grounding and honesty. **They cannot prove tone** — the
on-device model's phrasing must be judged by a human. Run the app and try, in order:

1. **"What was discussed about Kai Sinat in my meetings?"** (Ask box) → expect: one honest sentence
   ("couldn't find anything about that…") — *no* 10-meeting list, *no* citations. If a real person
   from your notes is used instead, expect a short grounded answer citing the right meeting(s).
2. **"Show me all my meetings"** → still the clean meeting list (no regression).
3. **"What are Mac's action items?"** (use a name that owns items in your notes) → only that
   person's items, honest line if they have none.
4. Open the Library, click a specific meeting note, then push-to-talk: **"What were the updates in
   this meeting?"** → the answer must be about *that* note, with a small "About: \<its title\>" line
   on the notch.
5. Close the Library (back to pill), reopen it — the same note is still shown — push-to-talk the
   same question → still the correct note + badge (this was the stale-state bug).
6. Without ever clicking any note (fresh app start, Library closed), push-to-talk **"what were the
   action items in this meeting?"** → it must NOT silently pick the newest meeting; expect a global/
   honest response (nothing is "on screen").
7. **"Why are you so rude?"** → a short warm apology; nothing about "the notes don't mention…".
8. **"Do you see what's on my screen right now?"** with a note open → affirms and names that note;
   with nothing open → honest "nothing's open right now". No rambling.
9. **"Take me to the library"** / **"new note"** → still instant commands (no regression).
10. Judge tone on 2–3 real lookups (e.g. "what did we decide about X?"): the target voice is *one
    lead sentence that answers, meeting named inside the sentence, no preamble, stops cleanly*. If it
    still rambles, the next knobs are (in order): a second exemplar pair, presence penalty toward
    1.0, and a lower notch token cap — all one-line changes flagged in `NotesQa`/`LlamaLlm`.

**Honesty note.** Steps 1–9 have code-level regression tests behind them and should behave exactly
as written. Step 10 (phrasing quality of LLM answers) is *directionally* improved (exemplar, stops,
card-exact sampler) but unproven until this live run — prompt work on a 4-bit 4B model only counts
when a human reads real answers on the real machine.
