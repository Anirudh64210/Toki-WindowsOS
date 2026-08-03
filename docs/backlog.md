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
  facade):** (1) **notch calendar preview** — next meeting quietly at the edge; (2) **prep cards** — before
  a meeting: last decisions, open action items, who's attending (356×306 design state, new `PillState`);
  (3) **meeting creator** — the write path is built + tested; needs UI/voice (natural-language time via the
  already-referenced `Microsoft.Recognizers.Text.DateTime`). Outlook is post-v1. Ship-side: Google
  sensitive-scope **verification** (privacy policy + demo video) before selling → queue with M8.
- **Speaker diarisation** — real named speakers in transcripts instead of just YOU/THEM. (Task
  name-tags already done; transcript-level diarisation is the remaining, bigger piece — pairs well with
  the on-device-embeddings unblock.)

## 🎛 Polish / UX

- **Library filter/sort menu** — Newest / Oldest / Longest / Manual-notes-only. Only a drag-to-reorder
  toggle exists today.
- **Manual/custom notes** — owner decision still pending: a separate left-panel section vs. a "Manual
  notes" filter in the sort menu above.
- **Notch acrylic on/off toggle** + a blurrier white notch (white-theme background text can be hard to
  read through the current acrylic). Acrylic exists but isn't user-toggleable.

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
