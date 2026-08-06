# Ask Toki - the reasoning route, self-reference, and the new command set (P9)

*(2026-08-02. Companion to `docs/ask-toki-redesign.md` (P8) and `ARCHITECTURE.md`. Same shape as the
P8 doc: a plain-language summary first, then per-root-cause engineering detail, the research that
backs each decision, and the live-test script that only a human can run.)*

## The one-paragraph summary (plain language)

After P8, Toki answered *direct* questions well - "summarize my last meeting", "what are the action
items?" - but fell over on anything that needed **thinking**: "was anything assigned to *me*?",
"what should I focus on this week?", "what are the themes across my meetings?". Two real causes,
both in code, neither the model's fault: (1) Toki had **no concept of "me"** - P8 deliberately kept
the words "you/me" out of the person filter so they couldn't misfire, but never added the matching
*self* rule, so "assigned to me" listed other people's tasks (the screenshot bug); (2) every
thinking-style question was funneled down the **fact-lookup pipe**: top-3 search results, a
"answer-in-one-glance" prompt, and a token budget so small the model physically couldn't reason.
Both are now fixed: "me/my/I" resolves to the owner "You" from your notes with an honest "nothing
here is assigned to you - these belong to Lily and Mac" when that's the truth; and analytical
questions get their own route - Toki reads up to six related meetings (not three), uses a prompt
that asks for a conclusion plus evidence, and gets four times the room to answer. On top of that,
Toki now takes a whole new set of spoken/typed commands: start a recording, switch the notes engine
(on-device / API / no-AI), switch theme, ghost mode on/off, and "take me to the library" now lands
on the actual notes list (that was a real wrong-page bug). Everything is verified by 300 automated
tests (up from 228); the *quality* of the analytical answers is the one thing only Sid can judge -
script at the bottom.

## Root cause 1 - "assigned to me" didn't know who "me" is

**What the screenshot shows.** "Was anything assigned to me in this meeting?" → Toki listed
"Lily to work with Mac … (Lily, Mac)". Wrong twice: the item isn't the user's, and the answer
pretended it was.

**Why (verified in code).** `NotesQa.MatchPeople` resolves names against the owners that exist in
the database, and P8 added a `PersonGuard` that *excludes* "you/me/I/team" from ever matching - the
right call for by-name filtering ("what did *you* note down…" must not trigger a person filter),
but it left self-questions with **no slot at all**, so they fell through to "list everything in
scope". The notes themselves *do* know the answer: the note-writer stores the user's own items with
`Owner = "You"`.

**Fix - an explicit self slot (`NotesQa.SelfTaskRx` + `IsSelfOwner`).**
- A tight, unambiguous phrase list triggers it: "assigned/delegated to me", "on my plate", "my
  responsibility", "was I assigned", "mine", "for me to do", "do I own". Broad phrasings like "what
  are my open tasks" deliberately do **not** - in a meeting you recorded, *all* open items are your
  business, and P8's global list behaviour is preserved.
- When it fires, items are filtered to the self-owner values that exist in the data ("You", "Me",
  "myself") - the same closed-vocabulary discipline as the by-name slot.
- **Honest, helpful miss** (the calibrated-abstention shape): scoped - "Nothing in 'Simplify
  Community MR Rate Metric' is assigned to you. The items there are owned by Lily, Mac."; global -
  "Nothing's assigned specifically to you. The 3 open items belong to Lily, Mac, Raj."
- Bonus root-cause fix found on the way: owners are stored as combined strings ("Lily, Mac"), and
  `OwnerMatches`/`MatchPeople` compared them as one name - so "what are Mac's tasks" missed a
  "Lily, Mac" item entirely. Both now split multi-owner values ("Lily, Mac" / "Sid & Raj" /
  "Priya and Raj") and match per person.
- The LLM routes get the same knowledge in one prompt line: *"In the notes, 'You' is the user
  themself - 'I/me/my' in the question means them."* - so "was I mentioned…"-style lookups resolve
  self-reference too.

**Tests.** `AssignedToMe_WithNoSelfItems_IsHonest_NamingTheRealOwners` (the exact screenshot),
`AssignedToMe_ListsOnlyYourItems`, `GlobalSelfTasks_FilterToYou_AcrossMeetings`,
`GlobalSelfTasks_WithNoneOfYours_IsHonest_NamingOwners`, `MultiOwnerItem_MatchesEachNamedPerson`,
plus the P8 guard tests still green (`MatchPeople_IgnoresPseudoOwnersAndUnknowns`).

## Root cause 2 - every "thinking" question was forced through the fact-lookup pipe

**Why (verified in code).** `QueryUnderstanding.Classify` had five intents: Count, ListTasks,
ListMeetings, SummarizeOne - and a default of `LookupFact`. So "what should I focus on this week?",
"what's the team worried about?", "how is the MR-rate work progressing?" all became lookups:
**top-3** FTS hits, a context capped at 3,500 chars, a system prompt demanding a "one-glance"
answer, and a **85–170 token** budget. That pipeline is *structurally* incapable of analysis - no
prompt tuning can fix a route that never sees enough meetings and isn't allowed enough words.

**Research this maps to (fetched 2026-08-02).**
- **Adaptive / query-adaptive RAG** - the 2026 production consensus: classify query complexity
  first, keep simple factual queries on the fast narrow path, and give synthesis/comparison
  questions broader multi-source retrieval and a stronger reasoning treatment
  ([Adaptive-RAG-style routing overview](https://ragaboutit.com/query-adaptive-rag-routing-complex-questions-to-multi-hop-retrieval-while-keeping-simple-queries-fast/),
  [advanced-RAG survey](https://atlan.com/know/advanced-rag-techniques/)).
- **GraphRAG's local-vs-global finding** - "what are the main themes across this corpus?" questions
  *cannot* be answered by naive top-k retrieval; corpus-level questions need corpus-level reads
  ([From Local to Global](https://medium.com/@EleventhHourEnthusiast/paper-review-graph-rag-90257aa62464),
  [GraphRAG overview](https://www.datacamp.com/tutorial/graphrag)). At Toki's scale (tens of
  meetings, compact structured notes) "corpus-level" is achievable *directly*: feed several
  meetings' notes whole - no graphs, no embeddings (still blocked, see ARCHITECTURE), no new deps.
- **Calibrated honesty over binary abstention** - a grounded assistant should give the partial
  answer the sources *do* support and state what's missing, rather than a bare "not found"
  ([abstention survey](https://arxiv.org/pdf/2407.18418),
  [LLM honesty survey](https://github.com/SihengLi99/LLM-Honesty-Survey)).
- **Qwen3-4B-Instruct-2507 model card** - sampler already card-exact from P8 (T 0.7 / TopP 0.8 /
  TopK 20 / MinP 0); the Instruct variant is a **non-thinking** model, so structured reasoning must
  be *invited by the prompt shape* (conclusion → evidence) and *bounded by code*, exactly the
  belt-and-braces P8 established ([model card](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507),
  [Unsloth run guide](https://unsloth.ai/docs/models/qwen3-how-to-run-and-fine-tune/qwen3-2507)).

**Fix - a sixth intent: `QueryIntent.Analyze`, with its own breadth, prompt, and budget.**

```
             ┌ Count / ListTasks / ListMeetings  → SQL, no model      (unchanged)
understand → ┤ Analyze (NEW)  → up to 6 meetings → reasoning prompt → 220/420 tokens
             ├ SummarizeOne   → 1 meeting        → summary prompt   → 150/340 tokens  (unchanged)
             └ LookupFact     → top-3            → one-glance prompt →  85/170 tokens  (unchanged)
```

- **Classification** (`QueryUnderstanding.AnalyticalRx`): explicit reasoning-demand cues - "should
  I/we", "focus on", "prioritize", "risks/blockers/concerns/worried", "themes/patterns/trends/
  insights", "compare/versus/difference between", "progress(ing)", "unresolved/undecided/open
  questions", "across", "biggest/most important/overall", "advice/recommend/suggest", "what do you
  think / your take". Two guards keep the deterministic wins intact: cues are phrase-level where a
  bare word would over-trigger ("focus **on**", so "the focus group meeting" stays a summary), and
  a **who/when/where lead is always factual** ("who was worried about replication lag" stays a
  lookup - that's a name, not an analysis). Checked *after* Count/Tasks/ListMeetings and *before*
  Summarize (so "summarize the themes across my meetings" is treated as the multi-meeting synthesis
  it is).
- **Retrieval breadth** (`NotesQa.RetrieveBroad` + widened date-window path): topical anchor words
  = content terms minus the *analytical vocabulary* ("themes", "risks"… describe the analysis
  wanted, not text that appears in notes - a note never literally says "theme"). Anchored → up to
  **6** matching meetings; no anchor or no match → the **recent 5**. An analytical question never
  dead-ends deterministically: it's corpus-level by nature, so the *prompt* carries the honesty
  ("say what the notes don't cover") - partial grounded answer + explicit gap, not "found/not
  found". Factual lookups keep their P8 deterministic honest miss unchanged (regression-tested).
- **Prompt** (`BuildAnalyzePrompt`): "The user wants your analysis and judgement, not a lookup …
  Lead with your conclusion in one direct sentence. Then 2–4 short supporting points, each tied to
  a meeting by name. Use only what's in the notes; if they only partly cover the question, give
  what they DO show, then say in one honest sentence what's missing. Never invent." Context cap
  raised to 6,000 chars for this route only (~1.7k tokens prefill - fine on the 3050; notes are
  compact, and breadth is the point).
- **Budget**: 420 tokens in the Library, 220 on the notch. The notch's existing glance-cap
  machinery (`NotchOverBudget` → clean sentence cut → "See full answer in Library →") is exactly
  the right progressive disclosure for an analytical answer, so it's untouched.
- **Streaming, stops**: analyses (like summaries) may legitimately contain a blank line before
  their bullets, so the `"\n\n"` stop now applies to concise **lookups only** (same effective
  behaviour as before - tasks never reached the model).

**Testability.** New public seam `NotesQa.LlmOverride` (mirror of `StoreOverride`): tests inject a
fake `ILlm` that records the system/user prompt, token budget, and stops, then streams a canned
answer - so retrieval breadth, prompt shape, and budget are *proven*, end to end, without loading
the model or touching real settings. `AnalyticalQuestion_ReadsAcrossManyMeetings_WithAReasoningBudget`
(5 sources, ≥400 tokens, several meetings named in the prompt),
`AnalyticalQuestion_WithNoTopicAnchor_UsesRecentMeetings_NeverADeadEnd`,
`FactualLookup_KeepsTheNarrowPath_AndHonestMiss` (miss never reaches the model),
`AnalyticalQuestion_IsAnalyze` (10 phrasings), `AnalyzeRoute_DoesNotStealDeterministicIntents`
(9 phrasings covering every deterministic intent).

## Root cause 3 - the command vocabulary stopped at navigation

The owner asked for (verbatim list): start taking notes, switch to api/heuristic/on-device model,
switch to white/dark theme, ghost mode on/off, take me to the library (**the list, not the Ask
landing**), open templates, open assistant - "and all" (natural phrasings, same question-veto
discipline).

**Implementation (`CommandRouter` + real handlers in `App.ExecuteNotchCommand` and
`LibraryWindow.ExecuteLibraryCommand`).** Nine new `AppCommand`s, each anchored whole-utterance
with the existing fluff-stripping and unconditional question-shape veto:

| Say (any natural phrasing) | Does | Handler path |
|---|---|---|
| "start taking notes" / "start recording" / "take notes" / "record (this meeting)" | starts a manual recording - the same human-in-the-loop path as the Home button (`App.StartRecordingFromCommand` → `BeginRecording(null)`, which also finds the meeting app on the mic so "them" is captured); guards "already recording" | notch morphs into the recording bar; from the Library, the bar appears up top + a confirmation bubble |
| "switch to api model" / "use my api key" | `AppSettings.NotesMode = ApiKey` via the normal Settings path - **refuses honestly if no key is saved** ("add one in Settings, then I can switch") | `App.SwitchNotesMode` |
| "switch to on-device model" / "local model" | `NotesMode.Local` | 〃 |
| "switch to heuristic (model)" / "built-in notes" | `NotesMode.HeuristicOnly` (the no-AI floor) | 〃 |
| "switch to white theme" / "light mode" · "dark theme" / "go dark" | `ThemeManager.Apply` live + persisted; idempotent ("you're already in the light theme") | `App.SwitchTheme` |
| "ghost mode on / off" | capture-exclusion via the same `SetShowInRecordings` live path Settings uses, persisted (`on` = hidden from capture) | `App.SetGhostMode` |
| "take me to the library" | **the Library LIST page** - `ShowLibrary` gained a `page` parameter; `OpenHubPage(page)` now lands in one step (previously `ShowLibrary()` always landed on the Ask page: the wrong-page bug, fixed at the source; the tray/Home buttons keep their deliberate Ask landing) | `LibraryWindow.OpenHubPage("library")` |
| "open templates" | Templates page (now without the Ask-page flash) | `OpenHubPage("templates")` |
| "open assistant" / "open the chat" | the Ask page | `OpenHubPage("ask")` |

Also fixed on the way: `PillState.Ask` had **no entry in the state machine's transition table**, so
any `CanGo/TryGo` while the notch was open would have thrown `KeyNotFoundException` - it now has an
entry (→ Hidden/Home/Collapsed/RecordingBar), which is what lets "start taking notes" morph the
notch into the recording bar.

**Question-veto negatives, tested:** "what did we decide about the model switch?", "is ghost mode
on", "why is the theme white", "how do I start recording", "did we record the meeting yesterday",
"should we switch to the api model", "which model are we using", "what notes did we take in the
sync" - all fall through to Q&A. "start a note" still means the *typed* manual note (`NewNote`),
distinct from "start taking notes" (recording) - tested.

## What was *not* changed

- The P8 deterministic wins: routing rules, `ContentQuery` precision, derived screen grounding,
  the "About:" badge, persona/small-talk tiers, honest lookup misses - all untouched and
  regression-covered.
- No new native dependencies, no embeddings (still blocked - ARCHITECTURE "Why on-device
  embeddings are deferred"), no cloud calls, no telemetry. $0/month intact.
- Push-to-talk mic behaviour (mic only while the key is held). No wake word.
- The real `%APPDATA%\Toki\settings.json` and `toki.db` were never read or written by tests - all
  tests run on temp stores (`StoreOverride`) and the fake model (`LlmOverride`).
- Sampler: unchanged (already card-exact from P8). The next repetition knob, if Sid's live run
  shows rambling, is presence penalty toward 1.0–1.5 (the card's own advice) - a one-line change
  in `LlamaLlm`.

## Live-test script for Sid (final quality gate)

Automated tests prove **routing, self-ownership, breadth, budgets, and every command**. They cannot
prove the *quality and tone of analytical answers* - that's a human's call on the real machine.

**Commands (each should act instantly, no model):**
1. Push-to-talk: **"start taking notes"** → the notch turns into the recording bar; speak a bit,
   click stop → notes appear as usual. Then try it *again during the recording* → "Already
   recording - the bar's up top."
2. **"take me to the library"** → the Library window opens **on the notes list** (not the Ask page).
3. **"open templates"** → Templates page, directly. **"open assistant"** → Ask page.
4. **"switch to white theme"** → whole app recolours + "Light theme on." **"switch to dark theme"**
   → back. Say **"switch to dark theme"** again → "You're already in the dark theme."
5. **"ghost mode off"** → confirmation; start a screen recording (or screen-share) → Toki is
   visible in it. **"ghost mode on"** → Toki disappears from the capture. Check Settings - the
   toggle should reflect each change.
6. **"switch to heuristic model"** → confirmation; check Settings shows "Heuristic". **"switch to
   on device model"** → back to On-device. **"switch to api model"** with no key saved → the honest
   "there's no API key saved yet" reply (and the mode does NOT change).
7. Veto check, by voice: **"what did we decide about the model switch?"** → answered from notes
   (or an honest miss) - Settings must NOT open, nothing must switch.

**Smartness (the part only you can judge):**
8. On a meeting with items owned by others, push-to-talk **"was anything assigned to me in this
   meeting?"** → if nothing is yours: "Nothing in '…' is assigned to you. The items there are owned
   by …" - it must NOT present someone else's item as yours (the screenshot bug).
9. **"what's assigned to me?"** (global) → only items owned by "You", or the honest
   "nothing's assigned specifically to you…" naming the real owners.
10. **"What should I focus on this week?"** → expect a *conclusion first*, then a few supporting
    points naming real meetings. Judge: does it read like a sharp colleague's read, not a search
    result?
11. **"What are the main themes across my meetings?"** and **"how is the <your real topic> work
    progressing?"** → multi-meeting answers citing several meetings. On the notch, expect a clean
    teaser + "See full answer in Library →" for the full version.
12. **"What are my biggest risks or blockers?"** / **"what's still unresolved?"** → grounded
    synthesis; if the notes genuinely don't cover it, the answer should *say which part* is
    missing, not invent.
13. Regression spot-checks: **"summarize my most recent meeting"**, **"show me all my meetings"**,
    **"what are Mac's action items?"** (use a real name) - all exactly as good as before.

**Honesty note.** Steps 1–9 are backed by code-level tests and should behave exactly as written.
Steps 10–13's *routing and grounding* are proven (the right meetings reach the model with the right
prompt and budget), but the *phrasing quality* of the on-device model's analytical answers is
directional until this live run - if answers ramble or hedge, the tuning knobs are, in order: a
one-shot analytical exemplar in the prompt, presence penalty toward 1.0, and the notch token cap.
