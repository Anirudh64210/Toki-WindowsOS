# Ask Toki gets a brain - the P10 dialogue-manager rebuild

*(2026-08-03. Companion to `docs/ask-toki-redesign.md` (P8), `docs/ask-toki-reasoning.md` (P9) and
`docs/ask-toki-refinements.md` (P9.1). Same shape: a plain-language summary first, then the
engineering detail, the research behind each decision, an honest section on what an on-device model
can and cannot be, and the live-test script only a human can run. All P8/P9/P9.1 deterministic wins
and their regression tests are untouched and still green.)*

## The one-paragraph summary (plain language)

After three rounds of fixes, Sid's verdict was still: *"it is just not smart - it looks like the
first version of ChatGPT."* And the verdict was right, because the previous rounds fixed **bugs**
while the real problem was the **architecture**: Toki treated every single thing you said as a
search query against your meeting notes. Ask "what is my name?" and it *searched the notes for your
name* and cited three meetings. Tell it "call me Anirudh" and it *searched the notes for Anirudh*.
Let the microphone mis-hear "switch to light theme" as "switch to like theme" and it force-fed five
random meetings to the model, which then **invented** a connection to an attendance-tracking
meeting. The model was never allowed to simply talk, think, or remember - it was a search box
wearing a chat skin. P10 rebuilds the layer that decides *what kind of turn this is*: commands,
chit-chat, questions about Toki, **facts you tell it about yourself** (now remembered, and used),
**questions about you** (answered from its memory, never a search), **questions about your
meetings** (the whole P8/P9 grounded pipeline, unchanged, as one branch), and - the big unlock -
**everything else**, where the model finally converses and reasons from its own knowledge with the
conversation in its context. Plus two safety rails: a garbled command now gets a friendly "did you
mean…?" instead of a fabricated notes answer, and an answer that admits "the notes don't cover
this" can no longer dress itself up with source citations. 428 automated tests green (was 389).
What only Sid can judge - how *natural* the model's own conversation feels - is flagged honestly in
the live script at the bottom.

---

## The architectural root cause (verified in code)

Every prior fix (P8/P9/P9.1) added a *narrow front-filter* - small-talk, persona, affection, more
commands - in front of the same funnel:

```
                          ┌─ matched a filter? → canned reply
utterance ──► filters ────┤
                          └─ everything else ──► FTS retrieval ──► "answer ONLY from the notes"
```

`NotesQa.AskStreamAsync` **assumed** any turn that survived the filters was a question to be
answered *from the meeting notes*. That assumption is the whole disease. Traced in code against the
two new screenshots:

- **"what is my name"** → no filter matches → `QueryIntent.LookupFact` → `ContentQuery` keeps
  `name` as a content term → the prefix-OR FTS query `name*` matches notes containing "named"/
  "names" → three meetings retrieved → the model is *ordered* to answer only from them → "I don't
  have your name in the meeting notes." **With three citation chips under it.** Then **"But you
  call me anirudh"** → same funnel → `anirudh*` search → "I don't see any mention of you being
  called Anirudh in the meeting notes." The user *taught the assistant a fact* and the architecture
  physically had nowhere to put it.
- **"switch to like theme"** (ASR for "switch to *light* theme") → no command matches ("like" ≠
  "light") → the word **"theme"** trips `AnalyticalRx` → `QueryIntent.Analyze` →
  `RetrieveBroad`'s anchor terms ("switch like") match nothing → the P9 never-dead-end fallback
  feeds the model **the 5 most recent meetings anyway** → the model, told to "connect what's
  related", *manufactures* a link: "You need to switch to a new theme because the audio
  troubleshooting meeting (Jul 27) assigns you to manage weekend attendance tracking…" Retrieval
  with zero shared content is fabrication fuel, handed to the model with instructions to connect
  things.

So the "dumbness" was structural, in three exact ways: **(a) no non-notes answer path existed**
(chat/reasoning/world knowledge were impossible by construction), **(b) no user memory existed**
(a stated fact had no store, an identity question had no source but the notes), and **(c) retrieval
was unconditional** (even for turns the corpus provably could not serve - which is precisely where
grounded models fabricate). None of that is fixable by prompt tuning; it needed a dialogue manager.

## What the research says (fetched 2026-08-03)

- **Route first, retrieve conditionally.** [Adaptive-RAG](https://arxiv.org/html/2403.14403)
  classifies each query and sends it down no-retrieval / single-step / multi-step paths - because
  for queries the model can answer parametrically, "introducing retrieval may inject misleading
  evidence and shift the generation distribution", making RAG *worse* than no RAG
  ([Retriever Portfolios](https://arxiv.org/html/2605.31176) reports the same). 
  [Self-RAG](https://arxiv.org/pdf/2310.11511) makes retrieval **on-demand**: retrieve only when
  the generation actually needs evidence, "avoiding unnecessary or misleading context"
  ([overview](https://www.emergentmind.com/topics/self-rag)). P10's `TurnRouter` is this decision,
  done deterministically: meeting-domain cues, then a retrieval *probe* (does the corpus hold any
  evidence for these terms at all?) - retrieve only when it does.
- **Irrelevant context actively harms small models.** The RAG-robustness literature
  ([trustworthy-RAG survey](https://arxiv.org/pdf/2502.06872),
  [Context Awareness Gate](https://arxiv.org/html/2411.16133v1),
  [negative-rejection review](https://link.springer.com/article/10.1007/s44443-025-00334-6)) finds
  the most misleading retrieval is context that *looks* related but can't answer - and that weaker
  models are the most easily distracted. That is the theme-command fabrication exactly, and why the
  P9.1-flagged **relevance floor** is now enforced: an anchored query that matches nothing returns
  *nothing*, never "5 recent meetings anyway".
- **Hybrid dialogue managers are the shipping pattern.** Task-oriented systems that users actually
  like route each utterance among task intents *and* chit-chat/social handling before any heavy
  machinery ([hybrid policy dialogue manager](https://patents.google.com/patent/US11669697B1/en),
  [TacoBot](https://arxiv.org/pdf/2307.16081), the classic NLU→DM→NLG stack in
  [task-oriented dialogue with ICL](https://arxiv.org/pdf/2402.12234)). P10 makes Toki exactly
  that: a turn-type router in front, with the grounded pipeline as *one* branch.
- **Assistants remember the user.** Modern assistant memory keeps a small durable set of
  user-stated facts outside the conversation window - ChatGPT's saved memories, 
  [MemGPT](https://arxiv.org/pdf/2310.08560)'s core-memory block, the survey of
  [long-term memory design patterns](https://serokell.io/blog/design-patterns-for-long-term-memory-in-llm-powered-architectures).
  Toki's version is deliberately tiny: a name + a capped facts list, persisted through the settings
  store it already had, plus a per-session transcript as working memory.
- **A garbled command degrades to a clarification, never an action or a guess.** VUI error-recovery
  practice: on a low-confidence command, ask a *targeted* clarifying question (a safe no-op) rather
  than acting or free-associating
  ([voice-AI error handling](https://stablekernel.com/blogs/blog-voice-ai-error-handling-resilience-guide),
  [ASR failure modes in production](https://hamming.ai/blog/7-voice-agent-asr-failure-modes-in-production),
  [VUI design principles](https://www.parallelhq.com/blog/voice-user-interface-vui-design-principles)).
  Also the P9.1 base: [sufficient context](https://arxiv.org/pdf/2411.06037) (RAG suppresses
  abstention → abstention must be designed in) still governs the miss behaviour.

## The new architecture - a dialogue brain, with meeting-QA as one branch

`NotesQa.AskStreamAsync` remains the single entry point (both UIs, all tests), but it is now an
orchestrator over typed turns:

```
                 ┌ 0    COMMAND            CommandRouter (UI tier, unchanged)      → app action
                 │ 0.5  SMALL_TALK/AFFECTION SmallTalk (unchanged)                 → canned warmth
                 │ 0.6  PERSONA/META       Persona (unchanged)                     → canned in-character
utterance ──────►│ 0.7  USER_FACT/IDENTITY UserFacts + UserProfile (NEW)           → capture/answer from profile
                 │ 0.8  MISHEARD COMMAND   CommandRouter.TryNearMiss (NEW)         → targeted clarification
                 │ 1-2  MEETING_QA (det.)  QueryUnderstanding → SQL routes (unchanged)
                 │ 2.5  RAG-vs-PARAMETRIC  TurnRouter (NEW): cues + evidence probe
                 │        ├ general turn → GeneralChat (NEW): model's own knowledge + conversation
                 │        └ meeting turn → P8/P9 grounded pipeline (retrieval → prompt → stream)
                 └      …with the relevance floor and no-citations-on-miss (NEW) on the way out
```

### 1. `Llm/UserProfile.cs` + `Llm/UserFacts.cs` - identity and durable memory

- The profile **is** the field Toki already had (`AppSettings.UserName`, used by the greeting) -
  no second source of truth. "Call me Anirudh" (including the live phrasing "*But you* call me
  anirudh") captures the name, persists it through the normal settings path, and acknowledges. A
  not-a-name guard ("call me *later*", "i'm *tired*") keeps capture precise. "I work at X" / "my
  role is X" append to a capped `AppSettings.UserFacts` list.
- "What is my name" / "who am I" / "what do you know about me" answer **from the profile** -
  deterministic, never a search. Unset name → the honest Windows-account fallback ("Going by this
  PC's account, you're Suptu - say 'call me …' if you'd rather I use something else").
- The grounded prompts also carry the name now ("The user's name is X"), so name-addressed meeting
  questions resolve naturally.
- Test seam: `UserProfile.SettingsOverride` - an in-memory `AppSettings`; tests never touch the
  real `settings.json` (which holds the DPAPI key).

### 2. `Llm/TurnRouter.cs` - the RAG-vs-parametric decision

An LLM-bound turn (LookupFact / Analyze) is **grounded** when *anything* ties it to the meetings:
a meeting-domain cue in the wording ("meeting/standup/notes/action items…", "did **we decide**",
"who **said**"…), a date/recency, a screen/follow-up pin - or, failing all of those, a **retrieval
probe**: do the turn's content terms hit *anything* in the store (title or FTS)? A hit means the
user's corpus holds evidence → ground it. No hit means retrieval could only mislead → the turn is
**GENERAL** and the model converses. (An anchor-less Analyze - "what should I focus on?" - stays
grounded: it is corpus-level about *their work* by nature; P9 behaviour preserved and tested.)
Deterministic and ~0ms. Where a shipping system might use a trained classifier (Adaptive-RAG
does), the cue-set + probe covers the same decision with zero latency and full testability - and
the design works unchanged if a fast LLM classifier is ever swapped in for the judgment calls.

### 3. `Llm/GeneralChat.cs` - the model finally gets to talk

General turns get a general-assistant system prompt (warm, brief, honest about being a small
on-device meeting assistant; the profile name and facts included; one exemplar in the target
voice) and a user prompt carrying **the recent conversation transcript** (`ConversationContext.
Turns`, capped) plus session-stated facts - so "I'm planning a trip to Japan" → "how long is the
flight?" works as an actual conversation. No retrieval, no citations, both backends (the prompt is
plain system+user text, so the local Qwen path and the BYO-API path behave identically). Budgets:
120 tokens on the notch (with the `\n\n` stop), 260 in the Library. Without any model, the floor
is an honest deterministic reply pointing at Settings - never retrieval.

### 4. Working memory everywhere

`ConversationContext` gained `Turns` (the transcript, appended on **every** answered path - canned,
deterministic, grounded, general) and `SessionFacts`. Both UIs already keep a context per thread;
nothing UI-side changed. Meeting-QA follow-ups keep using the P2.1 resolver (pins/ranges) - the
transcript is the *general* path's memory.

### 5. Zero-fabrication rails

- **Relevance floor** (`NotesQa.RetrieveBroad`): an anchored analytical query whose anchor matches
  nothing now returns empty → the honest miss. The "never dead-end" recent-5 fallback survives
  *only* for genuinely anchor-less corpus-level questions.
- **No citations on a non-answer** (`NotesQa.LooksLikeMiss`): a model answer whose lead declares a
  miss ("couldn't find…", "the notes don't mention…") ships with an empty source list, and the
  Library chip renderer now *retracts* chips on an empty final list. A real answer that adds an
  honest caveat later keeps its citations (lead-only check, tested).
- **Misheard commands** (`CommandRouter.TryNearMiss`): a short, imperative, command-shaped
  utterance with a command-domain word (theme/ghost/model) that matched no real command returns a
  targeted "did you mean…" clarification - checked before any parse or retrieval. The question veto
  and a meeting-content veto keep real questions and real content out of it.

## Honest section: the on-device ceiling, and the BYO escape hatch

A 4-bit, 4-billion-parameter model on a laptop **will not equal cloud ChatGPT** - not in knowledge
breadth, not in reasoning depth, not in conversational polish. That's physics, and this document
won't pretend otherwise. But the owner's "it looks like first-gen ChatGPT" verdict was **mostly not
the model's fault**: forced retrieval, no memory, no permission to converse - those were
architecture, and architecture is what P10 fixes. What remains model-bound after P10: nuance and
phrasing quality of general conversation, deep multi-step reasoning, and knowledge recency/breadth.
For users who want a frontier-model brain, the existing **BYO API key** path *is* that upgrade -
and because the dialogue brain routes turns and builds plain system+user prompts, the **same brain
drives either backend**: routing, memory, honesty rails and citations behave identically; only the
generator swaps. On-device stays the private, $0-forever default; the API key is the opt-in
horsepower.

## What was NOT changed

- Every P8/P9/P9.1 deterministic win and its tests: routing rules, `ContentQuery` precision,
  derived screen grounding, the "About:" badge, person/self slots, the Analyze route, meeting
  identification, the command set, honest misses. **428 tests green** (389 before).
- No new dependencies (native or NuGet), no embeddings (still blocked - ARCHITECTURE), no cloud
  calls, no telemetry. $0/month intact.
- Push-to-talk mic behaviour (mic only while held); no wake word; Hard Constraint #3 untouched.
- The real `%APPDATA%\Toki\settings.json` and `toki.db` were never read or written by tests - all
  tests run on temp stores (`StoreOverride`), an in-memory profile (`UserProfile.SettingsOverride`),
  and the fake model (`LlmOverride`).

## Research index

- [Adaptive-RAG (NAACL'24)](https://arxiv.org/html/2403.14403) · [Retriever Portfolios](https://arxiv.org/html/2605.31176) - classify the query first; skip retrieval when parametric knowledge suffices → `TurnRouter`.
- [Self-RAG](https://arxiv.org/pdf/2310.11511) · [overview](https://www.emergentmind.com/topics/self-rag) - retrieval on demand; indiscriminate retrieval injects misleading context → the evidence probe.
- [Trustworthy-RAG survey](https://arxiv.org/pdf/2502.06872) · [Context Awareness Gate](https://arxiv.org/html/2411.16133v1) · [negative-rejection review](https://link.springer.com/article/10.1007/s44443-025-00334-6) - related-looking-but-unanswerable context is the most misleading; small models most distractible → the relevance floor.
- [Hybrid policy dialogue manager (US11669697)](https://patents.google.com/patent/US11669697B1/en) · [TacoBot](https://arxiv.org/pdf/2307.16081) · [task-oriented dialogue with ICL](https://arxiv.org/pdf/2402.12234) - chit-chat and task turns routed by a dialogue manager, not one funnel → the turn-type tiers.
- [MemGPT](https://arxiv.org/pdf/2310.08560) · [long-term memory design patterns](https://serokell.io/blog/design-patterns-for-long-term-memory-in-llm-powered-architectures) - a small durable user-fact store + in-context working memory → `UserProfile` + `ConversationContext.Turns`.
- [Voice-AI error handling](https://stablekernel.com/blogs/blog-voice-ai-error-handling-resilience-guide) · [ASR failure modes](https://hamming.ai/blog/7-voice-agent-asr-failure-modes-in-production) · [VUI principles](https://www.parallelhq.com/blog/voice-user-interface-vui-design-principles) - low-confidence commands degrade to targeted clarification → `TryNearMiss`.
- [Sufficient context (Google)](https://arxiv.org/pdf/2411.06037) - carried from P9.1: abstention must be designed in; a miss must look like a miss → `LooksLikeMiss` + chip retraction.

## Live-test script for Sid (final quality gate)

Steps 1–9 are backed by code-level regression tests and should behave exactly as written; step 10
is the part only you can judge.

1. **"Good morning Toki"** → a short greeting (as before). Then **"what is my name?"** → your name,
   instantly, **no citations, no "meeting notes"**. (Fresh install: it'll use your Windows account
   name and invite "call me …".)
2. **"Call me Anirudh"** (or try the mumbled "*but you call me anirudh*") → a warm "Got it -
   Anirudh". Then **"what is my name?"** again → "You're Anirudh." Restart Toki → ask once more →
   still Anirudh (it's persisted), and the Library greeting now says it too.
3. **"I work at <somewhere>"** → noted. **"What do you know about me?"** → your name + that fact +
   "everything stays on this PC".
4. **"What's the capital of France?"**, **"tell me a joke"**, **"I'm stressed about tomorrow"** →
   real conversational answers from the model itself - *no* "I couldn't find that in your
   meetings", *no* source chips. Follow up ("and of Japan?") - it should hold the thread.
5. Push-to-talk and deliberately garble a command: **"switch to like theme"** → "Sounds like a
   theme switch - say 'switch to light theme' or 'switch to dark theme'." Nothing else happens -
   and absolutely no meeting content in the reply. Then **"switch to light theme"** → it switches.
6. **"What did we decide about <a real topic>?"** → the normal grounded answer with citations -
   the whole meeting-QA pipeline is unchanged.
7. Ask about something with a meeting word that isn't in your notes: **"what did we decide about
   kangaroos?"** → the honest one-liner, **zero citation chips** under it (watch for chips that
   appear then get removed - they must end up gone).
8. **"What are the themes across my meetings about zebras?"** (any topic your notes don't have) →
   an honest miss - NOT an invented synthesis over random meetings.
9. Regression spot-checks: "summarize my most recent meeting", "what are my open tasks", "was
   anything assigned to me?", "take me to the library", "why are you so rude?" - all exactly as
   good as P9.1.
10. **The feel.** Chat with it for a few minutes - small talk, a question about your world, a
    follow-up, then a meeting question mid-conversation. Judge: does it now feel like *someone*,
    not a search box? On-device, expect competent-but-modest general answers (a 4-bit 4B model);
    with your API key set, the same brain should feel dramatically sharper in the general turns.

**Honesty note.** The automated tests prove the routing (which path each turn takes), the memory
(capture → recall → persistence), the rails (no citations on a miss, no fabrication feed, the
clarification), and every prior deterministic win. They **cannot** prove the *quality* of the
model's own conversation - pacing, warmth, phrasing - which is exactly step 10, and on the on-device
model it will be honest-but-modest. If a general answer rambles, the knobs are `GeneralChat.
MaxTokens` and a second exemplar in `BuildSystem`; if it ever fabricates on a grounded turn, note
the utterance - the retrieval floor and miss detector are the levers, both in `NotesQa`.
