# Toki — backlog & "what's next" (living doc)

*Plain-language capture of everything decided or discussed but **not yet built**, so any of it can be
picked up later without re-deriving it. Updated 2026-08-03. The canonical short roadmap lives at the
top of `CLAUDE.md`; this doc is the fuller version with the reasoning.*

---

## ⭐ Next up (decided 2026-08-03)

### Attribution / "Built with" screen — **ship-critical, small, do this first**
A real credits screen (in Settings/About) listing the on-device models and their licences. This is a
**legal obligation, not a nicety** — the app cannot be sold until it ships:

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

## 🚢 Ship path (M8) — the grind to actually distribute & sell

- **Signed installer** (.exe) — code-signing cert so Windows/SmartScreen doesn't scare users.
- **Velopack auto-update** from GitHub Releases — a meeting must never wait on a download; update in
  the background.
- **License-key gate** — Gumroad or Lemon-Squeezy one-time $20 key (no subscription; Hard Constraint #1).
- **Onboarding polish** — first-run model download UI with progress (models downloaded at onboarding,
  warmed every launch — a meeting never waits on a download).

## ✨ Features

- **M7 Calendar** — ✅ **foundation DONE (2026-08-03, live-verified):** Google OAuth (loopback + PKCE),
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
  Outlook is post-v1 (UI provider-neutral — `CalendarService.ProviderName` is the seam). Ship-side: Google
  sensitive-scope **verification** (privacy policy + demo video) before selling → M8.
- **Speaker diarisation** — real named speakers in transcripts instead of just YOU/THEM. (Task
  name-tags already done; transcript-level diarisation is the remaining, bigger piece — pairs well with
  the on-device-embeddings unblock.)

## 🎛 Polish / UX

- **Library filter/sort menu** — Newest / Oldest / Longest / Manual-notes-only. Only a drag-to-reorder
  toggle exists today.
- **Manual/custom notes** — owner decision still pending: a separate left-panel section vs. a "Manual
  notes" filter in the sort menu above.
- ✅ **Notch glassy/blur toggle — DONE (M9.0, 2026-08-03).** Settings → Appearance → "Glassy notch": a real
  DWM acrylic blur-behind (theme-aware frost, one size, flush top, rounded bottom, off by default). See the
  `redesign-and-glass` memory + ARCHITECTURE "glassy-notch blur" for the full interop.

- **⭐ Premium redesign — pass 2 (in flight).** Pass 1 shipped (type ramp, green `TokiCheck`, `TokiSwitch`,
  `ChipButton`, colour-coded note headers, flattened composer, "Calendar" rename, one-accent icons, glass
  notch). **Pass 2 to build:** the Wispr/Vercel **hover-expand "section pills"** (rows grow on hover →
  reveal Join/Prep/Edit inline; Toki already has the hover engine in `PillWindow`), the type/spacing sweep
  on Library + small states, and a motion-consistency pass. Full spec is in the Fable design audit;
  summary in the `redesign-and-glass` memory.

---

## 🧠 Discussed today (2026-08-03) — captured so we don't lose it

### 1. Graduated adaptive-resource scaling (RAM tiers) — **idea, not built**
Today's finding (verified in code): Toki's resource adaptation is **coarse**, not graduated.
- **RAM** drives a single binary switch: `App.LowMemoryMode = totalGB < 8.0` (one model resident at a
  time + no LLM prewarm below 8 GB; both warm above). A 12 GB and a 64 GB machine behave **identically** —
  RAM past 8 GB is unused.
- **VRAM** drives 3 GPU-offload tiers (`LlmRuntime.Resolve`): ≥7 GB full offload, ≥3.9 GB partial (20
  layers), else CPU. Context size follows the VRAM tier, **not** RAM. Model is always Qwen3-4B.

Proposed enhancement — a genuinely graduated policy:
- **RAM tiers** instead of one cutoff: e.g. **16 GB+** → larger context window / keep more meeting
  history in the prompt; **8–16** → today's normal; **6–8** → low-memory; **under ~6 GB** →
  heuristic-only floor (a 4 GB machine physically can't hold the 2.5 GB LLM + 630 MB ASR + Windows).
- Optionally a **smaller answer-model** (1–2B) on tiny machines so they still get AI notes, not just the
  heuristic floor.
- Bigger context / longer answers when there's headroom.

Honest caveats: 4 GB *total* RAM is the heuristic-only floor regardless; the biggest speed lever stays
**GPU/VRAM**, not RAM. (See the `dev-pc-specs` memory for the exact current thresholds. Dev box = 12 GB
RAM / RTX 3050 4 GB → "normal mode", partial GPU 20 layers.)

**⭐ TODO — automatic load-on-demand for the AI model (decided 2026-08-03).** Real finding on the owner's
12 GB machine: Toki **prewarms the ~2.4 GB Qwen model at launch and keeps it resident**, which on a 12 GB
box (≈11.3 GB usable, often only ~1–3 GB free with Chrome/Slack/etc.) leaves things tight and feels heavy
at startup. Measured clean (no leak — closing Toki returned the full ~2.4 GB). **Decision: make model
residency AUTOMATIC by RAM tier, not a manual toggle** — a modest-RAM machine (≈≤16 GB) uses **load-on-
demand** (model loads only when notes/Ask need it, then can unload after idle; costs ~10 s on the first
note per session), while a high-RAM machine (32 GB+) keeps today's **prewarm** for instant notes. Zero
config, fits Toki's "just works" ethos. Keep an optional Settings override — "Keep AI model loaded:
**Auto** / Always / On-demand" — defaulting to Auto, for power users only. This is the concrete first
slice of the RAM-tiers work above; the prewarm switch lives near `App.LowMemoryMode`.

### 2. BYO-API key as the "sharp brain" lever — **partially exists; not surfaced**
The P10 dialogue brain (`docs/ask-toki-brain.md`) routes turns and builds plain system+user prompts, so
the **same brain drives either backend** — local Qwen3-4B or the user's BYO API model — with identical
routing, memory, and honesty rails; only the generator swaps. Consequence discussed today: the honest
ceiling for *general conversation & reasoning* on a 4-bit 4B on-device model is "competent but modest";
the **API key is the real upgrade** for users who want frontier-model sharpness. Not-yet-built ideas:
- Surface/encourage the BYO-API path better in-product (esp. for the general/reasoning turns), while
  keeping on-device the private, $0-forever **default**.
- Consider a small local fallback model for tiny machines (ties into RAM tiers above).
- Keep Hard Constraint #1 intact: on-device is free forever; the API key is the user's own cost.

### 3. P10 dialogue brain — verification & tuning still open
- **Live-verify pending:** the owner still needs to judge the *conversational feel* (pacing, warmth,
  phrasing) of the on-device general-chat path — that's the one thing tests can't prove (live-script
  step 10 in `docs/ask-toki-brain.md`). 428/428 automated tests are green.
- **Tuning knobs left** (only if the live run shows problems): if general answers ramble →
  `GeneralChat.MaxTokens` + a second exemplar in `GeneralChat.BuildSystem`; if a grounded turn ever
  fabricates → the retrieval relevance floor + `LooksLikeMiss` detector, both in `NotesQa`.

---

## ⛔ Blocked

- **On-device meaning-search (embeddings / hybrid / reranker)** — blocked by a native backend conflict
  (sherpa-onnx vs. the embedder). Fix = an **out-of-process CPU embedder**. See ARCHITECTURE "Why
  on-device embeddings are deferred". None of the recent assistant failures were paraphrase misses —
  they were routing/state/precision bugs, now fixed deterministically — so this stays deferred.

## ⏸ Parked (owner-deprioritized)

- **Live transcription preview** (streaming Zipformer, preview-only, never saved).

---

## ✅ Done this session (2026-08-02→03) — for context

Voice (Ask-bar mic + push-to-talk notch), mascot redesign + animation vocabulary, and the four-part
**assistant intelligence** arc — all Fable-owned, all verified (build + tests + render + republished
`dist`), all documented:
- **P8** (`docs/ask-toki-redesign.md`) — routing/screen-grounding/persona/tone fixes.
- **P9** (`docs/ask-toki-reasoning.md`) — the Analyze route, self-reference, the command set.
- **P9.1** (`docs/ask-toki-refinements.md`) — wake-name fuzzing, honesty, identification, title recall,
  the clipped-notch + friendly-app-names.
- **P10** (`docs/ask-toki-brain.md`) — the **dialogue-brain rebuild**: turn-type routing, user
  profile/memory, RAG-vs-parametric, anti-fabrication rails. 428 tests green.
