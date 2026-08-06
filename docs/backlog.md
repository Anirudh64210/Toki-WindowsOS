# Toki - backlog & "what's next" (living doc)

*Plain-language capture of everything decided or discussed but **not yet built**, so any of it can be
picked up later without re-deriving it. Updated 2026-08-06. The canonical short roadmap lives at the
top of `CLAUDE.md`; this doc is the fuller version with the reasoning.*

---

## ✅ S8 "polish pass" - BUILT 2026-08-06

**1,774 tests green.** Em dashes gone project-wide (3,089 across 188 files; 3 functional exceptions
guarded by `NoEmDashTests`); one type ramp with **Settings as the reference** (`PageTitle` etc., SemiBold
everywhere, Bold only for micro-elements); Templates left-aligned like every other page; hit targets fixed
at the STYLE level (`PillButton`'s hover was an opacity change, invisible on transparent buttons, so those
had no hover at all); new shared `RowButton`; Copy is one split pill; nav rows light on hover.
Detail: README "S8", ARCHITECTURE "S8 decisions".

**And one real bug:** the "mosquito text" the owner reported was **losing text**, not just shrinking it -
a blank line drawn at `FontSize=6` stayed tagged Blank after being typed into, and Blank blocks are
written as nothing. Now covered by four checks in `--notetest`.

**Left open on purpose:**
1. **The Library window's "Built with" row** still uses the Border-on-negative-margins pattern that the
   notch's "All other settings" was just converted away from. Not flagged by the owner, so left alone;
   switch it to `RowButton` next time that page is touched.
2. **EN dashes remain** in numeric/time ranges ("9:41 - 10:11", "2-4 sentences"). Deliberate - that's what
   the character is for - but the owner may want them flattened to hyphens too.
3. The type sweep covered TITLES and section headers. Body/caption sizes were left as they are; if the
   window still reads inconsistent, that's the next layer.

---

## ✅ S1–S7 "sticky notes, done properly" - BUILT 2026-08-06 (awaiting real-meeting bug-testing)

**1,759 tests green**, published to `dist`. This batch came straight out of the owner's first test of
N1–N7. Detail: README "S1–S7", ARCHITECTURE "S1–S7 decisions".

**Closed by this batch:** #58 (the pre-meeting nudge is back, but only surfaces when the notch is idle
and nothing is fullscreen), #59 (sticky notes are real database rows now, not a JSON file that never
pruned), #61 (action items carry ↳ evidence links).

**What came out of it that ISN'T finished:**
1. **Sticky notes aren't in the Markdown mirror** - every other note is readable in Obsidian/Notepad from
   `%APPDATA%\Toki\Notes`; sticky notes live only in `toki.db`. That was a deliberate call (a second
   write path is a second thing to drift), but it does mean one class of hand-written text isn't
   plain-text-readable. Options: mirror them under `Notes/Sticky/`, or fold them into the meeting's own
   `.md` under a heading. Needs an owner decision about which.
2. **Sticky notes are unreachable except through their meeting.** If you write one for a meeting you
   never record, the only way back to it is the prep card's picker. There is no "all my sticky notes"
   view. Probably fine; worth watching whether notes get lost.
3. **The ↳ silence is still ambiguous** - a line with no arrow could mean "no confident match" or
   "nothing to match against". The floor is doing its job (a wrong link is worse than none), but the user
   asked about this directly, and a one-line explanation somewhere near the TRANSCRIPT header would
   answer it cheaply.
4. **Audio playback for evidence links** (task #60) - still text-jump only. A player (seek/scrub/waveform)
   is its own milestone.
5. **Re-writing a note with a different template** is now safe (the summary never depended on typed
   notes), so the old N6-era caveat is gone.
6. **`LibraryWindow.ShowMenu` and `Views/NotePicker` are two implementations of the same themed
   dropdown.** The notch pair was unified; the Library one wasn't, to keep this change's blast radius
   small. Worth merging next time either is touched.

---

## 🎯 THE BUILD AS SPECIFIED (owner regroup, 2026-08-05) - kept for the reasoning

The owner reviewed the whole pending list and picked. **Built in this order (N1→N7):**

| # | What | Why it's here |
|---|---|---|
| **N1** | **The note editor** - one reusable editable surface: text, 3 sizes, to-do checkboxes, lists, spacing, template insertion. **No font family, no colour** (owner: "nothing outside normal text… just size, templates, spacing, freedom"). Markdown-backed. | Everything below is this one component in a different frame. Build it once. |
| **N2** | **Manual notes in the Library** use N1 - templates, to-dos, action items, all insertable by hand. Plus a "Custom note" entry point. | Today a manual note is a bare `TextBox`. |
| **N3** | **The prep card becomes editable** - keeps its generated content, but the user can add to-dos and free text on top. "Like a blank Notion page, our version." | This *is* the "manual/custom notes as a separate section" answer - resolved, see below. |
| **N4** | **Prep card surfacing: auto-surface OFF → hover-only** + a **"Prep card" button in the recording state**: hover the peeking Toki → recording page drops → Prep card → stays open while hovered → Back returns to recording. | OWNER DECISION. Resolves the long-pending auto-vs-hover question. |
| **N5** | **Save-or-discard the scratchpad** into the Library after the meeting, classified as manually created, chosen **on the preview card in the note list itself**. | No hollow entries; the user decides. |
| **N6** | **Typed-notes augmentation** (was research item C#14) - the LLM merges what you typed during the meeting with the transcript, so your fragments steer the emphasis. | The highest-leverage note-quality item in the research. |
| **N7** | **Evidence-linked notes** (was C#15) - every bullet jumps to the transcript line that produced it, highlighted. **Text jump only**; the audio player is deliberately deferred to its own milestone. | Cheapest way to close the perceived-quality gap with cloud LLMs. |

**Owner decisions this settles (removing them from "pending"):**
- **Manual/custom notes - section vs filter: ANSWERED.** Neither, as originally framed. The editable
  prep card / sticky note covers the need, *plus* a dedicated custom-note button. The "Manual notes"
  filter already shipped in H2 and stays.
- **Prep card auto vs hover: ANSWERED - hover-only**, with the in-recording button as the second door.

**Everything else stays parked**, explicitly: the whole **M8 ship path** (revisit "once we get closer to
the actual complete and confident build"), the rest of section B's owner decisions, the remaining
research features, the #39 UI passes, the assistant-brain limits, and the infra work below.

---

## ⭐ Next up (updated 2026-08-05, after the v1-iter3 premium-UI pass)

**UI revamp #39 - passes ①, ⑤ and the premium redesigns are DONE.** ① motion tokens + ease-out morph +
message fade-up + notch jump-pill + 3-dot thinking indicator (v1-iter2); ⑤ the jump-pill in the **Library**
chat (v1-iter3). Also shipped in v1-iter3: settings consolidation (notch = UI/visibility only), the
compact recording page, the Luma-style Ask landing (**without** the example-question chips - owner removed
them), the in-house themed sort popup, the calendar/COMING-UP/PrepCard premium redesign, the new mascot +
7-size app icon, and ghost mode re-asserted on every notch state.

**Remaining #39 passes:** ② shared **empty-state** control (mascot + line + one action) across
Library/Ask/Calendar-disconnected/no-speech; ③ **layout-stability / text-trim sweep** (`TextTrimming` on
single-line, `MinHeight` reservations, `UseLayoutRounding`); ④ **hover-expand "section pills"** (rows grow
on hover → inline actions - the revamp's signature). None of these are blockers; they're the last coat of
polish.

**The real next milestone is the M8 SHIP PATH** (below): signed installer, Velopack auto-update, licence-
key gate, onboarding download polish, Google sensitive-scope verification. Everything else on this list is
optional; that list is what stands between Toki and being sold.

**Parked (owner, 2026-08-04) - ASR model quality** on short push-to-talk. Full resume plan lives in the
`asr-and-notch-reply-quality` memory: clipped-start fix, VAD/padding, phonetic post-correction for domain
words, a relevance floor so a garbled input can't retrieve a random meeting, or a stronger ASR model. The
"voice returns nothing" root cause this session was a MUTED Windows mic, not a bug (`--mictest` now
diagnoses device/peak in one command).

**Attribution screen - DONE 2026-08-04** (was "next" below; shipped as M8.0, see README). The M8 SHIP PATH
(installer / auto-update / key gate / onboarding download / Google scope verification) is still the road to
actually selling.

---

## Attribution / "Built with" screen - DONE (kept for the reasoning)

### The licence obligation that unblocked charging money
A real credits screen (in Settings/About) listing the on-device models and their licences. This is a
**legal obligation, not a nicety** - the app cannot be sold until it ships:

| Model | Role | Licence |
|---|---|---|
| Parakeet TDT 0.6b v2 | transcription (ASR) | **CC-BY-4.0** (requires attribution) |
| Qwen3-4B-Instruct-2507 | notes + Ask answers (LLM) | Apache-2.0 |
| Whisper small.en | ASR fallback | MIT |
| bge-small | embeddings (deferred/blocked, but shipped in-tree) | MIT |

Scope: a scrollable "Built with" section reachable from Settings/About, each entry with name, what it
does, licence, and link. CC-BY-4.0 (Parakeet) specifically **requires** visible credit. Keep it in the
product's design language (dividers-not-boxes, calm). Small, self-contained, unblocks charging money.

---

## 🚢 Ship path (M8) - the grind to actually distribute & sell

- **Signed installer** (.exe) - code-signing cert so Windows/SmartScreen doesn't scare users.
- **Velopack auto-update** from GitHub Releases - a meeting must never wait on a download; update in
  the background.
- **License-key gate** - Gumroad or Lemon-Squeezy one-time $20 key (no subscription; Hard Constraint #1).
- **Onboarding polish** - first-run model download UI with progress (models downloaded at onboarding,
  warmed every launch - a meeting never waits on a download).

## ✨ Features

- **M7 Calendar** - ✅ **foundation DONE (2026-08-03, live-verified):** Google OAuth (loopback + PKCE),
  DPAPI-encrypted refresh token, raw-HTTP `Calendar/` module (list/insert/delete), `CalendarService`
  facade, Home + Settings connect/disconnect, `--caltest`. See `docs/google-setup.md`, `ARCHITECTURE.md`
  "Google Calendar foundation", the `calendar-foundation` memory. **Surfaces still to build (sit on the
  facade). **UI DONE (M7.1, 2026-08-03, render + live verified):** connected Home "COMING UP" glance;
  Calendar page (`PillState.Calendar`) agenda-by-day week view + now-line + week nav + Join; **create**
  (one-box natural language via `QuickEventParser`), **edit** (`events.patch`), **delete** (undo for solo /
  confirm for guested). See `mansplain/25`, README M7.1, ARCHITECTURE "Google Calendar UI", the
  `calendar-foundation` memory.
  **Prep cards DONE (M7.2, 2026-08-03, render + logic verified):** ~5-min-before auto-surfaced prep card
  (LAST TIME recap + STILL OPEN items w/ owners + AGENDA), deterministic/no-LLM, honest first-time state,
  restrained `PrepWatcher`; `--preptest`, `--state PrepCard`, `TOKI_PREP_LEAD`. See README M7.2,
  ARCHITECTURE "Pre-meeting prep cards", `mansplain/26`.
  **Voice-create DONE (M7.3, 2026-08-03):** "schedule a call with Ana tomorrow at 3" on push-to-talk →
  confirm-gated create (`Llm/CalendarCommand.cs`, two tiers in `NotesQa.AskStreamAsync`,
  `ConversationContext.PendingCreate`); `--voicecreatetest`, 472 tests. Also fixed the shared parser so a
  bare "at 3" = 3 PM. See README M7.3, ARCHITECTURE, `mansplain/27`.
  **Still to build:** (1) **owner confirm** the prep-card **auto-surface vs hover-only** choice (currently
  auto; one-line switch); (2) optionally let an imminent COMING-UP row on Home open its prep card directly.
  Outlook is post-v1 (UI provider-neutral - `CalendarService.ProviderName` is the seam). Ship-side: Google
  sensitive-scope **verification** (privacy policy + demo video) before selling → M8.
- **Speaker diarisation** - real named speakers in transcripts instead of just YOU/THEM. (Task
  name-tags already done; transcript-level diarisation is the remaining, bigger piece - pairs well with
  the on-device-embeddings unblock.)

## 🎛 Polish / UX

- ✅ **Library filter/sort menu - DONE.** Newest / Oldest / Longest / Manual-notes-only shipped in H2;
  v1-iter3 replaced the native Windows `ContextMenu` with the **in-house themed popup** (Toki surface,
  hairlines, pink check on the active mode) so it stops looking like a system control.
- **Manual/custom notes** - owner decision still pending: a separate left-panel section vs. a "Manual
  notes" filter in the sort menu above.
- ✅ **Notch glassy/blur toggle - DONE (M9.0, 2026-08-03).** Settings → Appearance → "Glassy notch": a real
  DWM acrylic blur-behind (theme-aware frost, one size, flush top, rounded bottom, off by default). See the
  `redesign-and-glass` memory + ARCHITECTURE "glassy-notch blur" for the full interop.

- **⭐ Premium redesign - pass 2 (mostly done; one piece left).** Pass 1 shipped (type ramp, green
  `TokiCheck`, `TokiSwitch`, `ChipButton`, colour-coded note headers, flattened composer, "Calendar"
  rename, one-accent icons, glass notch). Pass 2 **shipped in v1-iter3**: the calendar/COMING-UP/PrepCard
  redesign (accent bars, derived attendee avatars, amber countdowns, Join), the compact recording page,
  the Luma-style Ask landing, the themed sort popup, the type/spacing sweep, and the motion-consistency
  pass (everything now draws from `Theme/Motion.cs`). **Still to build:** the Wispr/Vercel **hover-expand
  "section pills"** (rows grow on hover → reveal Join/Prep/Edit inline; Toki already has the hover engine
  in `PillWindow`). Full spec is in the Fable design audit; summary in the `redesign-and-glass` memory.

---

## 🧠 Discussed today (2026-08-03) - captured so we don't lose it

### 1. Graduated adaptive-resource scaling (RAM tiers) - **idea, not built**
Today's finding (verified in code): Toki's resource adaptation is **coarse**, not graduated.
- **RAM** drives a single binary switch: `App.LowMemoryMode = totalGB < 8.0` (one model resident at a
  time + no LLM prewarm below 8 GB; both warm above). A 12 GB and a 64 GB machine behave **identically** -
  RAM past 8 GB is unused.
- **VRAM** drives 3 GPU-offload tiers (`LlmRuntime.Resolve`): ≥7 GB full offload, ≥3.9 GB partial (20
  layers), else CPU. Context size follows the VRAM tier, **not** RAM. Model is always Qwen3-4B.

Proposed enhancement - a genuinely graduated policy:
- **RAM tiers** instead of one cutoff: e.g. **16 GB+** → larger context window / keep more meeting
  history in the prompt; **8–16** → today's normal; **6–8** → low-memory; **under ~6 GB** →
  heuristic-only floor (a 4 GB machine physically can't hold the 2.5 GB LLM + 630 MB ASR + Windows).
- Optionally a **smaller answer-model** (1–2B) on tiny machines so they still get AI notes, not just the
  heuristic floor.
- Bigger context / longer answers when there's headroom.

Honest caveats: 4 GB *total* RAM is the heuristic-only floor regardless; the biggest speed lever stays
**GPU/VRAM**, not RAM. (See the `dev-pc-specs` memory for the exact current thresholds. Dev box = 12 GB
RAM / RTX 3050 4 GB → "normal mode", partial GPU 20 layers.)

**⭐ TODO - automatic load-on-demand for the AI model (decided 2026-08-03).** Real finding on the owner's
12 GB machine: Toki **prewarms the ~2.4 GB Qwen model at launch and keeps it resident**, which on a 12 GB
box (≈11.3 GB usable, often only ~1–3 GB free with Chrome/Slack/etc.) leaves things tight and feels heavy
at startup. Measured clean (no leak - closing Toki returned the full ~2.4 GB). **Decision: make model
residency AUTOMATIC by RAM tier, not a manual toggle** - a modest-RAM machine (≈≤16 GB) uses **load-on-
demand** (model loads only when notes/Ask need it, then can unload after idle; costs ~10 s on the first
note per session), while a high-RAM machine (32 GB+) keeps today's **prewarm** for instant notes. Zero
config, fits Toki's "just works" ethos. Keep an optional Settings override - "Keep AI model loaded:
**Auto** / Always / On-demand" - defaulting to Auto, for power users only. This is the concrete first
slice of the RAM-tiers work above; the prewarm switch lives near `App.LowMemoryMode`.

### 2. BYO-API key as the "sharp brain" lever - **partially exists; not surfaced**
The P10 dialogue brain (`docs/ask-toki-brain.md`) routes turns and builds plain system+user prompts, so
the **same brain drives either backend** - local Qwen3-4B or the user's BYO API model - with identical
routing, memory, and honesty rails; only the generator swaps. Consequence discussed today: the honest
ceiling for *general conversation & reasoning* on a 4-bit 4B on-device model is "competent but modest";
the **API key is the real upgrade** for users who want frontier-model sharpness. Not-yet-built ideas:
- Surface/encourage the BYO-API path better in-product (esp. for the general/reasoning turns), while
  keeping on-device the private, $0-forever **default**.
- Consider a small local fallback model for tiny machines (ties into RAM tiers above).
- Keep Hard Constraint #1 intact: on-device is free forever; the API key is the user's own cost.

### 3. P10 dialogue brain - verification & tuning still open
- **Live-verify pending:** the owner still needs to judge the *conversational feel* (pacing, warmth,
  phrasing) of the on-device general-chat path - that's the one thing tests can't prove (live-script
  step 10 in `docs/ask-toki-brain.md`). 428/428 automated tests are green.
- **Tuning knobs left** (only if the live run shows problems): if general answers ramble →
  `GeneralChat.MaxTokens` + a second exemplar in `GeneralChat.BuildSystem`; if a grounded turn ever
  fabricates → the retrieval relevance floor + `LooksLikeMiss` detector, both in `NotesQa`.

---

## ⛔ Blocked

- **On-device meaning-search (embeddings / hybrid / reranker)** - blocked by a native backend conflict
  (sherpa-onnx vs. the embedder). Fix = an **out-of-process CPU embedder**. See ARCHITECTURE "Why
  on-device embeddings are deferred". None of the recent assistant failures were paraphrase misses -
  they were routing/state/precision bugs, now fixed deterministically - so this stays deferred.

## ⏸ Parked (owner-deprioritized)

- **Live transcription preview** (streaming Zipformer, preview-only, never saved).

---

## ✅ Done this session (2026-08-02→03) - for context

Voice (Ask-bar mic + push-to-talk notch), mascot redesign + animation vocabulary, and the four-part
**assistant intelligence** arc - all Fable-owned, all verified (build + tests + render + republished
`dist`), all documented:
- **P8** (`docs/ask-toki-redesign.md`) - routing/screen-grounding/persona/tone fixes.
- **P9** (`docs/ask-toki-reasoning.md`) - the Analyze route, self-reference, the command set.
- **P9.1** (`docs/ask-toki-refinements.md`) - wake-name fuzzing, honesty, identification, title recall,
  the clipped-notch + friendly-app-names.
- **P10** (`docs/ask-toki-brain.md`) - the **dialogue-brain rebuild**: turn-type routing, user
  profile/memory, RAG-vs-parametric, anti-fabrication rails. 428 tests green.

---

## 🛡️ Hardening pass - DONE (H1, 2026-08-04)

The full-codebase audit + fix + verify pass (see README H1 / ARCHITECTURE "The hardening pass"). All
14 high- and 21 medium-severity findings fixed, 472 tests green, dist republished & smoke-verified.

**Deliberately deferred from that audit (refactor-only, no user-visible effect, regression risk in
delicate code):**
- NotesQa's three near-identical tokenize pipelines (TitleTerms / ContentQuery / ProbeTerms) - consolidate
  only when next touching retrieval.
- Token-patch streaming plumbing duplicated between the notch (App.HandleNotchText) and the Library Ask.
- The state→view factory duplicated between PillWindow and RenderHarness.

## 🧭 Research-derived opportunities (2026-08-04, competitor + UX research - owner picks)

Full research in the session notes; ranked by leverage-per-effort, all within the hard constraints:

1. **Typed-notes augmentation (the Granola model)** - a scratchpad in the recording view; at meeting
   end the LLM merges your fragments (steering emphasis) with transcript-derived expansion. The single
   most-praised note UX in the market; Fathom just copied it. Manual-notes plumbing is a head start.
2. **Evidence-linked notes** - each note bullet carries a tap-to-transcript timestamp (and play-the-WAV).
   Research says users forgive a small model's phrasing when they can check the source in one click -
   the cheapest way to close the perceived-quality gap with cloud LLMs. Toki already keeps the WAVs
   (competitors' top complaint is no way to verify).
3. **"Open from last time" in notes for recurring meetings** - feed the prep card's STILL-OPEN query into
   note generation. Circleback's stickiest feature, deterministic here.
4. **Per-app "always record"** - surface the choice on the meeting prompt ("Always for Zoom"). Hard
   Constraint #3 explicitly allows it; closes the market's known "missed meeting is gone" failure mode.
   OWNER DECISION - it changes the consent posture per app.
5. **Speaker names v1 without diarisation** - map "them" to calendar attendee names for 1:1s (Notion's
   July-2026 approach). The #1 universal complaint category-wide; cheap first step before real diarisation.
6. **Suggested follow-up questions in Ask** (2–3 tappable, Fireflies pattern) + **Topic Trackers**
   (a saved FTS5 query with a badge) - both nearly free on the existing architecture.
7. **Follow-up email draft** from a note (one local-LLM pass + copy button) - reviewers use it constantly.
8. **Import/transcribe an audio file** - the whole-file ASR path exists; it's a UI affordance.
9. **Consent-responsibility notice** - botless recorders shift disclosure onto the user; a first-run
   note + optional "remind me to announce recording" line on the record prompt is the good-citizen
   pattern (Granola/Otter are in litigation over exactly this). Belongs in M8 onboarding.
10. **Tray recording indicator** - ghost mode hides the notch from capture, so the tray icon should
    reflect recording state too.
11. **Local(host)-only MCP server over the SQLite** - 2026's power-user tell (everyone shipped one);
    zero upkeep, data stays local, lets Claude/ChatGPT desktop query your meetings.
12. **Market the existing moats**: the Markdown mirror (Obsidian-ready files = loud unmet demand),
    ghost mode ("invisible in screen shares"), and offline-forever notes.

**Explicitly not worth copying** (research-confirmed): meeting bots, engagement/sentiment scoring,
viral sharing mechanics, video recording, CRM stacks, voice agents, 24/7 ambient capture, 100+
cloud languages, team sync.

---

## H2 - DONE (2026-08-04): task-scoping fix, Library sort, security pass

Assistant due-date scoping, Library sort menu, and a full security threat-model pass - see README H2 /
ARCHITECTURE. 520 tests green, dist republished.

**Deliberately deferred from the security audit (documented decisions, not oversights):**
- **RegisterHotKey instead of the WH_KEYBOARD_LL push-to-talk hook** - owner chose hold-to-talk, which
  needs key-UP that RegisterHotKey can't give. The hook callback already branches only on the chord and
  never stores other keys. Keep the hook; document it on the privacy/about screen when that copy is written.
- **DPAPI optionalEntropy parameter** - would fail to decrypt every EXISTING stored secret (wiping the
  user's API key + Google refresh token) for marginal gain (same-user malware can call our code path
  regardless). Skip unless we add a migration.
- **Move bulk data (DB/WAVs/Notes/log) from %APPDATA% (roaming) to %LOCALAPPDATA%** - stops meeting
  content silently syncing across an org's roaming profiles, but needs a one-time migration of existing
  users' data. Owner-gated.
- **WER/crash-dump scrubbing** - a full-heap .dmp could contain the in-memory access token / decrypted
  key. Low likelihood; revisit with the signed-installer/packaging work (M8).
- **Assistant answer citations "jump to transcript moment"** and the full `--braintest` fixture-DB golden
  harness - the targeted regression tests cover the fix; the big harness is a larger investment for when
  the brain is next reworked.

**Still open from H1's research list** (unchanged - owner picks): typed-notes augmentation, evidence-linked
notes, per-app always-record, speaker names from calendar attendees, follow-up email draft, audio import,
local MCP server, tray recording indicator, consent notice in onboarding.

---

## H3 - DONE (2026-08-04): assistant brain rework + 1,155-case prompt-test suite

Stress-test-driven rework of the Ask brain - see README H3 / ARCHITECTURE. 1,636 tests green, dist
republished. The golden set lives in `tests/Toki.Tests/Brain/` (fixture + frozen clock + RouteOf).

**Known assistant limitations left as future work (surfaced by the taxonomy, deliberately not fixed):**
- **General ASR/typo tolerance beyond the wake name.** Toki fuzzes "Toki"→Doki/Toky but does NOT
  fuzzy-match content words: "acton items" won't hit "action items", "meating" won't hit "meeting",
  "due"↔"do" isn't merged (too risky - "do" is ubiquitous). A phonetic/edit-distance normalization pass
  before parsing would close this; it's a real feature with real false-positive risk. Homophone table is
  in the research (scratchpad/taxonomy-spec.md §15.2).
- **Fuzzy PERSON-name matching.** "Anna" vs "Ana", "Sean/Shawn" - the person filter matches known owner
  names but doesn't yet do phonetic/edit-distance matching or a "did you mean?" clarify.
- **Nested attribution** ("what did Ana say Bob should do") - parsed as a single person scope, not two roles.
- **Multi-intent in one utterance** ("recap Monday and what's due Friday") - answers the first, doesn't split.
- **"tips for running a standup"-style ambiguity** - a meeting word inside a general how-to grounds on the
  corpus (defensible either way; left grounded).
- **Speaker-attribution honesty** (who said what) is coarse until named diarisation ships (YOU/THEM only).
- Deeper generation-side answer-quality (faithfulness scoring, citation-moment links) is a separate,
  model-in-the-loop eval - the deterministic layer is what this suite locks down.
