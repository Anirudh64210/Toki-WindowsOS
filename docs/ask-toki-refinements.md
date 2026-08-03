# Ask Toki refinements — the P9.1 live-run bug cluster (wake-name, honesty, identification, recall, the clipped notch)

*(2026-08-02/03. Companion to `docs/ask-toki-redesign.md` (P8) and `docs/ask-toki-reasoning.md` (P9),
same shape: plain-language summary first, then per-root-cause engineering detail with the research
that backs each decision, and the live-test script only a human can run. All P8/P9 deterministic wins
and their regression tests are untouched and still green.)*

## The one-paragraph summary (plain language)

Sid's live test of the P9 build surfaced a new cluster, and almost all of it traced to one theme:
**Toki didn't recognise its own name, or a fact it already had in the database, and then let the
model improvise.** Push-to-talk speech goes through the ASR, and the ASR hears "Toki" as *Doki,
Toky, Docie, Toku, Tokyo* — the routers only knew the literal "toki", so commands and even "I love
you, Toky" fell through to the notes engine, which then **invented** a grounded-sounding answer (the
worst possible failure — it destroys trust). Separately, questions whose answers are plain database
facts — "what was my *oldest* meeting?", "which meeting was on July 15th?" (there were four),
"which meeting was about Amazon?" (it's a meeting *title*) — were being handed to the language model,
which shrugged or silently picked one. And the meeting prompt showed the raw process identity
("Com.tinyspeck.slackdesktop is in a call") whose length broke the notch's height calculation, so it
rendered cut off. Every fix is deterministic: one shared fuzzy wake-name matcher used by every
router; broader command/affection/self-reference vocabularies (still question-vetoed); a new
deterministic "which meeting?" identification layer (recency, ambiguous dates, title matches)
answered straight from SQLite; a title-match backstop in retrieval; friendly app names; and the
prompt text now updates *before* the notch measures its height. 389 automated tests green (up from
300). What tests can't prove — the on-device model's phrasing when a question genuinely does need
it — is flagged honestly in the live script at the bottom.

---

## Root cause A — commands & affection fell through to the notes engine, which then hallucinated

**What the screenshots show.** "Hey Doki, can you open a library for me?" → "The meeting notes don't
mention opening a library or any action related to Docie." · "switch to api key settings" → "There
is no mention of switching to API key settings in the provided meeting notes." · "add a new note in
the library" → "No meeting notes mention adding a new note in the library." · **"I love you, Toky."
→ an invented ramble about "UI and transcription improvements in today's meeting notes" — none of it
grounded.**

**Why (verified in code).** Four independent gaps compounding:
1. **The wake-name was literal.** `CommandRouter.Fluff` stripped only `toki`; SmallTalk/Persona
   stripped only `tok(i|y|ie)`. The ASR's actual renderings (Doki/Toky/Docie/Toku/Tokyo/Doky) blocked
   the strip, so the leading address made the utterance unmatchable and it fell to retrieval —
   *with the mis-heard name still in the query*, which is where "related to Docie" came from.
2. **Article/phrasing rigidity.** "open **a** library" (the command table only knew "the/my"),
   "add a new note **in the library**" (no destination tail allowed), "switch to api **key
   settings**" (no such tail), and the 6-word cap killed "add a new note in the library" (7 words).
3. **No affection tier.** "I love you" matched nothing in SmallTalk/Persona, so it became a
   retrieval query; "love" matched a note; the model then flattered its way to a fabricated answer.
4. **The prompt permitted stretching.** The system prompts said "if the notes don't contain the
   answer, say so" but nothing about *loosely related* notes — exactly the gap the Google
   sufficient-context work identifies: models **answer instead of abstaining** when context is
   merely adjacent.

**Fixes.**
- **`Llm/WakeName.cs` (new)** — the single closed alias list for what "Toki" sounds like to the ASR
  (`[td]oa?[kc]k?(i|y|ie|ee|ey|e|u)` + `tokyo`), with `StripLeading`/`StripTrailing`. Used by
  `CommandRouter` (front fluff + trailing politeness), `SmallTalk`, and `Persona`. Scope discipline:
  it only ever strips a leading/trailing **address** — the question sent to retrieval is never
  rewritten, so a real question about Tokyo is untouched.
- **`CommandRouter`**: articles `a/an` accepted where natural ("open a library", "switch to an api
  model"); API-switch tails now include `key`/`settings`; NewNote accepts "(add) a (new) note (in
  the/my library)"; word cap 6 → 8 (the anchored whole-utterance patterns are what actually hold
  precision, not the cap). The **question-shape veto is unchanged and unconditional** — "hey toki,
  what did we decide about the library redesign?" still falls through to Q&A (tested).
- **`SmallTalk`**: a new affection tier ("I love you / miss you / you're the best / marry me…") with
  warm canned replies that redirect to the job. Anchored whole-utterance, so "i love the new
  dashboard design" still reaches the notes.
- **Anti-hallucination in the prompts**: both system prompts now add *"never stretch loosely related
  notes into an answer; if the question isn't about the meetings at all, say that's outside what you
  can see."* This is belt-and-braces — the primary fix is that these utterances no longer reach the
  model at all.

**Research.** Fuzzy alias/synonym lists + phonetic tolerance are the standard treatment for
domain entities the ASR mangles ([MindMeld/Webex robust NLP for voice assistants](https://medium.com/mindmeld/robust-nlp-for-voice-assistants-bad1550fef3b),
[Webex engineering version](https://blog.webex.com/engineering/robust-nlp-for-voice-assistants/));
wake-word systems are hardened against *fuzzy variants* explicitly ([FakeWake, CCS'21](https://dl.acm.org/doi/pdf/10.1145/3460120.3485365)).
On the honesty side: RAG **increases** a model's propensity to answer over abstaining, so abstention
must be explicit ([Google Research, "sufficient context"](https://research.google/blog/deeper-insights-into-retrieval-augmented-generation-the-role-of-sufficient-context/),
[Sufficient Context paper](https://arxiv.org/pdf/2411.06037)), and small models are the worst at
it ([Honest AI: fine-tuning small LLMs to say IDK](https://arxiv.org/pdf/2410.09699)) — which is
why the *deterministic* interception (route affection/commands away; answer identification from the
DB) is the real fix and the prompt line is only the backstop.

**Tests.** `CommandRouterTests.Fuzzy_wake_name_and_articles_still_command` (11 wake/article
variants incl. the exact screenshot utterances), `Live_run_missed_commands_now_match` (9 model/note
phrasings), `Wake_addressed_questions_still_fall_through` (6 veto negatives),
`SmallTalkTests.Intercepts_affection` (11), `Fuzzy_wake_name_small_talk`,
`Affection_words_about_content_fall_through` (incl. "tell me about tokyo"),
`NotesQaTests.ILoveYouToky_IsAffection_NeverRetrieval` (bait note in the DB; the model is proven
never called).

## Root cause B — self-reference still leaked ("did they… give me any tasks?" dumped all 5 items)

**Why (verified in code).** P9's `SelfTaskRx` was deliberately tight and didn't know "mention me",
"give me any tasks", "any tasks for me", "did I get anything", "was I mentioned/given/tasked". With
`self=false` and no named person, the scoped task answer lists everything — the screenshot.
A second gap: phrasings *without a task noun* ("did I get anything?") never even classified as task
queries (`QueryUnderstanding.TasksRx` required one), so they went to the LLM.

**Fix.** Both regexes broadened with **assignment-to-me shapes only**: `TasksRx` gains
`did i get any(thing)/some(thing)/assigned`, `was i assigned/given/tasked/mentioned`,
`anything/something for me`, `they/anyone… gave me`, `mention(ed) me`; `SelfTaskRx` gains the same
family. Deliberately **not** included: bare "give me any…" (imperative = the global list) and broad
"what are my open tasks" (P9's guard — in a meeting you recorded, all open items are your business —
kept and now explicitly regression-tested). The honest miss still names the real owners.

**Tests.** `MentionMeOrGiveMeTasks_IsSelfScoped_HonestMiss` (the exact screenshot, 5 planted items),
`SelfAssignment_Phrasings_FilterToYou` (5 phrasings), `BroadMyOpenTasks_IsStillTheGlobalList`,
`QueryUnderstandingTests.SelfAssignmentShapes_AreTaskQueries` + the over-trigger negatives
`GetGive_ContentQuestions_StayLookups` ("did i get the budget approved" stays a lookup).

## Root cause C — meeting identification was left to the model (oldest/first, ambiguous dates)

**What the screenshots show.** "What was the oldest meeting that I recorded? What was the title?" →
"The notes do not mention any recorded meeting dates or titles" (the DB knows both, exactly).
"What was the first meeting I had?" → right meeting, but led with raw task bullets and buried the
name. "What was the meeting I had on July 15th?" → four meetings that day; one silently summarized
as "the Jul 15 meeting".

**Why (verified in code).** The recency rank *parsed correctly* (`OldestRx` handled
"oldest/first meeting"), and resolution picked the right record — but then handed it to the
LLM as a *lookup/summary* with a question containing "recorded"/"title", words that appear nowhere
in the notes text, so the model answered "not mentioned". Identification is not a retrieval
problem — it's a `SELECT`. Similarly, a date resolving to N meetings fed `hits[0]` to the summary
prompt: the multiplicity was silently dropped.

**Fix — a deterministic identification layer (`NotesQa.IdentifyMeetingAnswer`)**, checked after the
Count/List/Tasks routes and before any model call:
1. **Recency-identify**: rank (Latest *or* Oldest) + a residual made only of identify-chrome
   ("title/name/called/recorded/had/…", the `IdentifyChrome` set) → *"Your oldest/first/most recent
   meeting was "«title»" on «date»."* + a one-line gist (first sentence of the stored summary). No
   model. "Summarize my most recent meeting" has a non-chrome word ("summarize") and still takes the
   LLM summary path — no regression.
2. **Ambiguous-date multiplicity**: a thin date question resolving to **>1 meeting** lists them all
   ("You had 3 meetings on Jul 28: …  Ask me about any of them by name.") — never a silent pick.
   Exactly 1 → summarized as before; 0 → the P8 honest date miss.
3. **Title identification** (see D).

**Tests.** `OldestMeeting_WithTitleAsk_IsDeterministic` (asserts the model is never called),
`FirstMeeting_LeadsWithTitleAndDate_ThenGist` (asserts the answer *starts* with the identification),
`MostRecentMeetingTitle_IsDeterministicToo`, `AmbiguousDate_WithSeveralMeetings_ListsThemAll`,
`QueryUnderstandingTests.RecencyRank_ParsesThroughChrome` (4 phrasings).

## Root cause D — a clear title term ("Amazon") missed its meeting

**What the screenshot shows.** "What was the meeting I had regarding Amazon?" → "There was no
meeting regarding Amazon in the notes" — while "Amazon's Resilience and Innovation" sits in the
Library.

**Why (verified in code).** Two compounding weaknesses: "regarding" was not in the generic-word
lists, so the FTS query was `regarding* OR amazon*` — a low-IDF word ORed in flattens BM25 on a tiny
corpus and can push the real hit out of top-3; and nothing guaranteed that a **title** hit survives
ranking at all. The single `bm25(…, 5.0, 1.0)` title weight is a soft preference, not a guarantee.

**Fix.**
- `"regarding/concerning/involving/mentioning/covering/around"` added to `GenericAskWords` (scope
  chrome, not content).
- **`MeetingStore.TitleMatchIds(terms)` (new)** — case-insensitive substring match over meeting
  titles, content-filtered terms only (≥3 chars, no stop/ask words), LIKE-escaped, newest first.
- **Recall union**: every retrieval site (`Retrieve`, the recency topic pool, the date-window match,
  the summarize-one target) now **prepends** title matches before FTS results — the BM25F insight
  that a title field hit deserves priority, applied as a hard guarantee at this corpus size
  ([BM25F field weighting](https://www.emergentmind.com/topics/bm25-ranking),
  [BM25-FIC on small collections](https://ceur-ws.org/Vol-2741/paper-11.pdf),
  [Elastic on recall tuning](https://www.elastic.co/search-labs/blog/elasticsearch-relevance-tuning-improve-recall)).
- **Deterministic answer for the identification shape**: "what/which (was) the meeting …" + a unique
  title match → *"That was "«title»" on «date»." + gist*, from the DB; several matches → a short
  list. Not the identification shape ("what did we discuss regarding amazon?") → normal LLM lookup,
  but with the title meeting guaranteed present in its context.
- **Precision guard kept**: terms are content-filtered, so P8's "generic words match everything" fix
  is intact — "regarding kangaroos" still misses honestly (tested).

**Tests.** `TitleTermQuestion_IdentifiesTheMeeting_Deterministically` (the Amazon screenshot),
`TitleTermRecall_ReachesTheLlmContext_OnContentQuestions` (title meeting proven inside the LLM
prompt), `UnknownTopic_StillAnHonestMiss_WithTitleBackstop`, plus the P8 miss/precision suite still
green.

## Root cause E — the raw process name + the clipped meeting prompt (one bug, two halves)

**What the screenshot shows.** "Com.tinyspeck.slackdesktop is in a call. Record it?" — and the notch
rendered **cut off** halfway through the buttons.

**Why (verified in code).** `MeetingPromptView` set its prompt text in the `Loaded` event — but
`PillWindow.OnStateChanged` measures the view's content height (`ContentHeight` → `Measure`)
**before** `Loaded` fires (Loaded runs after the layout pass). So the reveal height was computed on
the *stale/default one-line text*; the real text — the raw reverse-DNS identity, which is long —
then wrapped to two lines and overflowed past the composited clip. Two causes, both fixed:
1. **Friendly names (`Detection/AppNames.cs`, new)**: a known-app map (Slack/Teams/Zoom/Chrome/
   Discord/Webex/…, matched token-wise so "com.tinyspeck.slackdesktop" → Slack) plus a cleanup for
   unknown identities (strip reverse-DNS chrome, ".exe", "desktop/client", title-case) — the raw
   identifier can never reach the UI. Also applied where the app name labels the *saved meeting*
   (`App.StopRecording`), so fallback titles read "Slack — Jul 15", not the identifier.
2. **Measure-before-reveal**: `MeetingPromptView.Refresh()` sets the text eagerly (constructor +
   an explicit call from `PillWindow` right before the height measurement + `Loaded` re-fit via
   `GrowToContent` as belt-and-braces), and the prompt TextBlock now wraps
   (`TextWrapping="Wrap"`), so even a genuinely long name yields a correctly *taller* notch, never a
   clipped one.

**Proof.** `Toki.exe --render` gained two regression shots: `MeetingPrompt-slack.png`
("Com.tinyspeck.slackdesktop" → "**Slack** is in a call. Record it?") and
`MeetingPrompt-longname.png` (an unknown reverse-DNS identity → "Videoconferencing Collaboration is
in a call. Record it?", wrapping to **two lines with the buttons fully visible** — the exact
would-have-clipped case). Plus `AppNamesTests` (18 mappings/cleanups).

## What was *not* changed

- Every P8/P9 deterministic win and its tests: routing rules, `ContentQuery` precision, derived
  screen grounding, the "About:" badge, person/self slots' closed vocabularies, the Analyze route,
  the command set and its handlers, honest misses. 389 tests green (300 before).
- No new dependencies, native or otherwise; no embeddings (still blocked — ARCHITECTURE), no cloud,
  no telemetry. $0/month intact. Push-to-talk mic behaviour untouched (mic only while held); no wake
  word, no always-listening — `WakeName` is *text-side alias matching* on what the user already
  chose to say, not detection.
- The real `%APPDATA%\Toki\settings.json` and `toki.db` were never read or written; all tests run on
  temp stores (`StoreOverride`) and the fake model (`LlmOverride`).

## Research index

- [MindMeld — Robust NLP for voice assistants](https://medium.com/mindmeld/robust-nlp-for-voice-assistants-bad1550fef3b) · [Webex version](https://blog.webex.com/engineering/robust-nlp-for-voice-assistants/) — fuzzy/alias/phonetic entity matching under ASR noise → `WakeName`.
- [FakeWake (ACM CCS'21)](https://dl.acm.org/doi/pdf/10.1145/3460120.3485365) — wake words have a systematic fuzzy-variant neighbourhood → treat the alias list as closed and explicit.
- [Google Research — the role of sufficient context in RAG](https://research.google/blog/deeper-insights-into-retrieval-augmented-generation-the-role-of-sufficient-context/) · [paper](https://arxiv.org/pdf/2411.06037) — RAG raises answer-confidence and *reduces* abstention; abstention must be designed in → deterministic interception + explicit no-stretch prompt rule.
- [Honest AI — fine-tuning small LLMs to say "I don't know"](https://arxiv.org/pdf/2410.09699) — small models are the least able to abstain → don't send them questions the DB can answer.
- [BM25F / field weighting](https://www.emergentmind.com/topics/bm25-ranking) · [BM25-FIC (small collections)](https://ceur-ws.org/Vol-2741/paper-11.pdf) · [Elastic — relevance tuning for recall](https://www.elastic.co/search-labs/blog/elasticsearch-relevance-tuning-improve-recall) — title-field priority; on a tens-of-documents corpus, made a hard guarantee (title-match union) instead of a soft weight.

## Live-test script for Sid (final quality gate)

Everything numbered 1–12 is backed by code-level tests and should behave exactly as written; the
honesty note below says what only you can judge.

**Wake-name + commands (each acts instantly, no model):**
1. Push-to-talk, deliberately mispronounce it: **"Hey Doki, can you open a library for me?"** →
   the Library opens on the notes list. Try **"Hey Toky, open the library"** too.
2. **"Add a new note in the library"** → the manual-note editor opens.
3. **"Switch to api key settings"** (with no key saved) → the honest "no API key saved yet" reply,
   nothing switches. If you have a key saved: it switches and confirms.
4. Veto check: **"What did we decide about the library redesign?"** → answered from notes (or an
   honest miss) — the Library must NOT open.
5. **"I love you, Toki"** (and try "…Toky") → a short warm reply, *zero* meeting content in it.

**Self-reference:**
6. On a meeting whose action items belong to others, push-to-talk **"Did they mention me or give me
   any tasks in this meeting?"** → "Nothing in '…' is assigned to you. The items there are owned by
   …" — it must NOT dump the full list.
7. **"What are my open tasks?"** → still the full global list (all owners) — deliberately unchanged.

**Identification (deterministic — should be instant and exact):**
8. **"What was the oldest meeting that I recorded? What was the title?"** → "Your oldest meeting
   was '«real title»' on «real date»." plus a one-line gist. Same for **"What was the first meeting
   I had?"** — it must LEAD with the title and date.
9. **"What was the meeting I had on July 15th?"** (a date you had several meetings) → a list of all
   of them, not a silent pick. With exactly one meeting that day → a normal summary.
10. **"What was the meeting I had regarding Amazon?"** (any word from a real title) → "That was
    '«title»' on «date»." + gist. And **"what did we discuss regarding amazon?"** → a grounded
    answer that cites that meeting.

**The notch clip:**
11. Take a Slack (or Teams) call → the prompt must read "**Slack** is in a call. Record it?" —
    friendly name, notch fully visible, both buttons shown, however long the app name is.
12. Regression spot-checks: "summarize my most recent meeting", "show me all my meetings", "what
    are «name»'s action items?", "why are you so rude?" — all exactly as good as P9.

**Honesty note.** Steps 1–12 exercise deterministic paths with regression tests behind them. What
the tests **cannot** prove: (a) the *phrasing/tone* of answers that legitimately reach the on-device
model (steps 10b, 12); (b) that the model will *never* fabricate on a content question whose
retrieval returns loosely-related notes — the prompts now forbid stretching and the worst known
triggers are intercepted before the model, but a 4-bit 4B model's discipline is only provable by
reading real answers on the real machine. If you still catch a fabricated answer, note the exact
utterance: the next lever is a deterministic relevance floor (drop hits that share no content term
with the question) — scoped and ready to add in `NotesQa.Retrieve`.
