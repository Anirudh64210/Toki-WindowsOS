# CLAUDE.md — Toki for Windows

Condensed from `WINDOWS-BUILD-PROMPT.md` (the full brief — read it when detail is needed). This file carries the same rules.

## Current state & roadmap (updated 2026-08-04)

**H3 assistant-brain rework DONE (2026-08-04):** stress-test-driven rework of the Ask brain — 1,155-case
deterministic prompt-test golden set (`tests/Toki.Tests/Brain/`, fixture + frozen clock + RouteOf), total
1,636 tests green. Fixed: task provenance-vs-due-date (`TaskScheduling.ModeFor` — "what tasks did I have
on Aug 2" no longer dumps overdue), accurate Persona privacy responder (was falling through to retrieval),
general-knowledge routing override, wider phrasing coverage. Brain-only. See README H3, ARCHITECTURE
"The assistant brain", `mansplain/31`, backlog H3 (deferred: content-word ASR/typo tolerance, fuzzy names).

**H1 hardening pass DONE (2026-08-04):** full multi-agent audit (67 verified findings — all 14 high +
21 medium fixed), recording-lifecycle integrity (a live mic can never be hidden; session is the truth),
delete-removes-audio, crash/quit recovery (`RecordingRecovery` startup sweep + periodic WAV-header
flush), mic device-change rebuild (`IMMNotificationClient` + `BufferConverter`), denylist/new-Teams
detection fixes, LLM dispose race, ApiLlm streaming timeouts, calendar pagination/timeout contracts +
OAuth listener resilience, atomic settings writes, fullscreen prompt suppression, notes-prompt quality
rules, API→local notes fallback, SharpCompress CVE bump. See README H1, ARCHITECTURE "The hardening
pass", `mansplain/29`, and `docs/backlog.md` (research-derived opportunities + deliberate deferrals).

**Shipped:** M0–M6 (pill shell, audio, detection, transcription+echo-dedup, storage+heuristic notes+Library, LLM notes). Plus: theme toggle, 8 note templates, cloud mascot, tinted-mirror notch, the Library **hub** (Ask / Library / Templates), whole-note copy/select, BYO API key, and the **Ask chatbot** — streaming answers, FTS5 retrieval, a deterministic **understand-first** layer (temporal + intent parse → SQL resolution) with **conversation memory** (follow-ups), and (P10) a **dialogue brain**: turn-type routing (command / small-talk / persona / **user-fact & identity** / misheard-command clarification / meeting-QA / **general conversation**), a persisted user profile ("call me Anirudh" → remembered, used by the greeting and prompts), RAG-vs-parametric routing (general turns answered from the model's own knowledge — no forced retrieval), and anti-fabrication rails (relevance floor; no citations on a miss). Detail: `ARCHITECTURE.md`, `docs/ask-toki-brain.md`, and the `notes-and-chatbot-plan` / `build-and-run` memories.

**Not yet done (roadmap) — see the fuller list below in this file's history or ask):**
1. **On-device meaning-search (embeddings/hybrid/reranker)** — BLOCKED by a native backend conflict; fix = out-of-process CPU embedder (see ARCHITECTURE "Why on-device embeddings are deferred").
2. ✅ **DONE — voice.** Ask-bar mic (click → speak → fills the box; auto-stop + loader) AND **push-to-talk on the notch**: `Ctrl+Alt+Space` = hold-to-talk (Wispr-Flow style) → notch drops down, listens (waveform), release → thinks → answer streams on the notch (`PushToTalkHook` + `AskView`). Mic runs ONLY while held.
3. ~~"Hey Toki" wake word~~ — **DROPPED by owner (2026-08-02).** Owner chose **push-to-talk only, no always-listening**: the hotkey is hold-to-talk; there is **NO always-on mic and NO wake word**. Keeps Hard Constraint #3 fully intact. Do NOT rebuild the wake word unless the owner reverses this. (Polished conversational Q&A-on-the-notch is a future refinement of the P6 push-to-talk.)
4. **Toki animation in the Library/hub** — ✅ mascot redesigned (soft gradient cloud, matches the product design) + a click-reaction vocabulary (squish/hop/wobble/wink/spin/big-eyes/nod) in `TokiBlob`. (Idle breathing/blink already existed.)
5. **Library filter/sort menu** (Newest/Oldest/Longest/Manual-notes-only) — only a drag-reorder toggle exists today.
6. **Manual/custom notes**: separate left-panel section vs a "Manual notes" filter — owner decision pending.
7. **Notch acrylic on/off toggle** + blurrier white notch (background text unreadable). Acrylic exists but isn't user-toggleable.
8. **Speaker diarisation** (named speakers, not just YOU/THEM) — task name-tags done; transcript diarisation not.
9. **M7 Calendar** — ✅ **foundation + UI DONE (2026-08-03, live-verified):** OAuth (loopback+PKCE), DPAPI token, raw-HTTP `Calendar/` module, `CalendarService` facade; connected Home "COMING UP", `PillState.Calendar` agenda week view, natural-language create, edit (`events.patch`), delete (undo/confirm); **prep cards** (M7.2 — `PrepWatcher` auto-surfaces a deterministic before-meeting card: LAST TIME recap + STILL OPEN + AGENDA + Join-and-record). `--caltest …|parse`, `--preptest`, `--state PrepCard`, `docs/google-setup.md`, design = `Toki-ProductDesign/③ Home` + `④ prep card`. **voice-create** (M7.3 — "schedule a call with Ana tomorrow at 3" on push-to-talk, confirm-gated, `Llm/CalendarCommand.cs`, `--voicecreatetest`). **Still to build:** owner-confirm prep auto-vs-hover. Outlook post-v1 (UI provider-neutral); Google scope verification → M8. See `calendar-foundation` memory.
10. **M8 Ship shape** — ✅ **Attribution/"Built with" screen DONE (2026-08-04):** `AboutView` (`PillState.About`), reached from **Settings → About → "Built with — open source & licences"**; scrollable credits list (MODELS + ENGINES & LIBRARIES), each row opens its upstream source; attribution-required licences (Parakeet **CC-BY-4.0** with an "int8 quantised" modification note, Qwen **Apache-2.0**, sherpa-onnx Apache-2.0) get amber chips, MIT stays neutral; Whisper deliberately **omitted** (stubbed fallback, not shipped); repo `THIRD-PARTY-NOTICES.md` holds the full attributions. **Still to build:** signed installer + Velopack auto-update, Gumroad/Lemon-Squeezy key gate, onboarding download polish.
11. **M9** character & polish. (Live transcription preview is parked — owner deprioritized.)
12. **Graduated adaptive-resource scaling (RAM tiers)** — TODAY'S IDEA, not built. Resource adaptation is coarse: RAM = a single binary switch (`App.LowMemoryMode`, <8GB), VRAM = 3 GPU tiers (`LlmRuntime`), context follows VRAM not RAM, model always Qwen3-4B. Proposed: RAM tiers (16GB+ → bigger context/history; 6–8 → low-memory; <~6 → heuristic-only floor), maybe a 1–2B fallback model on tiny machines. See `docs/backlog.md`.
13. **BYO-API "sharp brain" lever** — the P10 dialogue brain drives EITHER backend identically; on-device general chat is competent-but-modest (4-bit 4B ceiling), so the API key is the real sharpness upgrade. Not-yet-built: surface/encourage it better while keeping on-device the $0 default. See `docs/backlog.md`.

**Full backlog with reasoning: `docs/backlog.md`.**


## What Toki is

A meeting assistant that lives quietly at the edge of the screen. Detects when you're in a meeting, offers to record, transcribes and summarizes **entirely on-device**, and keeps every meeting in a searchable local Library. One-time $20, no subscription, nothing leaves the laptop.

## Hard constraints — every decision filters through these

1. **$0 monthly upkeep, forever.** No servers, no cloud costs. On-device ASR + LLM. Optional BYO API key = user's cost.
2. **All data local.** SQLite + files under `%APPDATA%\Toki\`. No account, backend, telemetry, or sync.
3. **Toki only activates for meetings.** Never taps system audio globally. Detects a call, asks the user, taps only that app's process tree. Human in the loop unless the user chose "always record". Owner decision — do not relax.
4. **Minimal permissions.** Loopback needs no prompt on Windows; keep it that way. Never add anything that triggers a scary system prompt.
5. **Restraint is the design.** Appear only with something worth showing.

## Working with the owner

The owner (Sid, they/them) is **non-technical**.

- Explain plans in plain language before big changes. Recommend, don't assume; ask before product decisions.
- **Verify by running the app and watching real output appear — never by describing what code should do.** We build on the target OS; run everything, every time.
- Never run `git` commands unless asked.
- Small milestones, each verified before the next.
- After every milestone update: `README.md` (build log), `ARCHITECTURE.md` (decisions + rationale), `mansplain/` (plain-language story).

## Tech stack (owner-confirmed — do not re-litigate)

| Concern | Decision |
|---|---|
| Framework | C# / .NET 8 + WPF |
| UI | Single morphing pill overlay, bottom-center (see Design) |
| "Them" audio | WASAPI Application Loopback, per-process-tree (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, include-tree), Win 10 2004+ |
| "You" audio | WASAPI mic capture via NAudio |
| Detection | Registry mic-in-use poll (`CapabilityAccessManager\ConsentStore\microphone`), 2.5s |
| Final ASR | Parakeet TDT 0.6b v2 (ONNX int8) via sherpa-onnx, whole-file per stream |
| Live preview | sherpa-onnx streaming Zipformer (preview only, never saved) |
| ASR fallback | whisper.cpp `small.en` — **only on error, never on silence** |
| Notes LLM | Qwen3-4B-Instruct-2507 Q4_K_M via LLamaSharp, Vulkan backend, CPU fallback |
| Notes floor | Deterministic heuristic extraction, always available |
| Storage | SQLite (`Microsoft.Data.Sqlite`) at `%APPDATA%\Toki\toki.db` + Markdown mirror in `%APPDATA%\Toki\Notes\` |
| Calendar | Google OAuth loopback redirect, DPAPI-encrypted tokens; Outlook is post-v1 |
| Hotkey | `RegisterHotKey`, default Ctrl+Alt+Space, configurable |
| Tray | `H.NotifyIcon.Wpf` |
| Distribution | Signed installer + Velopack updates from GitHub Releases; Gumroad/Lemon Squeezy key |
| Models | Downloaded during onboarding with progress UI, re-warmed every launch — a meeting never waits on a download |

## Design — notch → pill

Reference designs: `Toki-ProductDesign/` (PNG exports of the macOS design). The Mac version hangs from the notch, top-center; **Windows uses a pill at bottom-center that expands upward.** Same design language: near-black surfaces, large corner radii, pink Toki character, coral record button, white pill buttons. SF Pro Rounded on Mac → Segoe UI Variable on Windows.

One window that morphs width/height between states — never multiple overlay windows. State machine (port exactly):
`hidden → meetingPrompt → recordingBar ⇄ recordingOpen → summarizing → notes | noSpeech`, plus `home`, `library`, `settings`. One `AppState` class owns it; views only read it.

State sizes from the design (logical px): collapsed 200×36 · record prompt 300×74 · recording 236×52 · recording expanded 372×234 · summarizing 340×164 · ready pill 244×52 · home 344×232 (344×320 connected) · prep card 356×306 · notes 424×438 · library/settings 404×440. Library also opens as a separate resizable window.

WPF: `WindowStyle=None`, `AllowsTransparency=True`, `Topmost=True`, `ShowInTaskbar=False`, per-monitor-DPI aware.

## Meeting detection

- Poll the mic consent-store registry key every 2.5s; `LastUsedTimeStop == 0` = app using mic now. Classic apps under `NonPackaged` with `#`-encoded paths.
- **Denylist, not allowlist** (dictation, screen recorders, audio tools, AI IDEs, Toki itself). Conservative: a spurious prompt is dismissible; a missed meeting is lost forever.
- Debounce 2 polls (~5s) before prompting. Dismiss = 10-min cooldown per app. Auto-stop when target releases mic ~3 polls (~7.5s).
- Log every change in the mic-using-app set. Never prompt while busy.

## Audio & echo

- Each stream to its own WAV, meeting-relative timestamps. Rebuild capture on process-tree changes and default-device changes (`IMMNotificationClient`).
- Echo fix is **cross-stream text de-dup post-ASR**: ±4s window, drop `you` line matching a `them` line; lines under 3 significant words require exact normalized match. Windows AEC is an opt-in toggle, off by default (fragile on macOS; assume the same).

## Lessons paid for on macOS — do NOT relearn

1. **Garbage in, garbage out** — check what the LLM is fed before tuning prompts. Hence Parakeet + `--asrtest`.
2. **A meeting never waits on a download.** Download at onboarding, warm engines at launch, degrade to heuristic notes.
3. **Never hand-chunk audio.** One whole-file ASR call per stream; use the toolkit's long-audio path if needed.
4. **Silence is not failure.** Empty Parakeet result = nobody spoke. Whisper fallback only on actual error.
5. **Reset recognizer state between meetings.** Fresh state per call, always.
6. **OS echo-cancel is fragile; opt-in only.** Never re-bind devices on a voice-processing node.
7. **A silent meeting saves nothing.** Null + friendly empty state; no hollow Library entries.
8. **Sampler params over prompts.** Qwen3: temp 0.7, topP 0.8, topK 20, presence ~0.5, never greedy. "Respond in English only." Validate output (no CJK/kana/Hangul, no 6+ char repeats); retry once; fall back to heuristic.
9. **Titles from the summary, heuristically** — validate (≥3 letters, mostly ASCII, no `!!!` runs); fallback "`<app>` — `<date>`".
10. **Duration is wall-clock** (end − start), not UI ticks.
11. **Attribution is a licence obligation.** Parakeet is CC-BY-4.0; Qwen3 Apache-2.0; Whisper MIT. Ship the "Built with" screen or we're in breach.
12. **A self-test that never produced its real artifact is not a test.** Verify by watching the WAV/transcript/note appear.
13. **Test the packaged artifact, not just `dotnet run`.** The installed app is part of verification.

## Self-test CLI flags (build as you go; headless, exit pass/fail)

`--detecttest` · `--loopbacktest` · `--mictest` · `--asrtest <file>` (print head **and tail**) · `--notestest` (planted echo/decisions/actions) · `--llmtest` (degeneration validator) · `--dbtest` (round-trip incl. manual note)

## Milestones

M0 scaffold ✓ · M1 pill shell + state machine + tray + hotkey · M2 **audio spike (de-risk; nothing proceeds until verified by ear)** · M3 detection · M4 transcription + echo de-dup · M5 storage + heuristic notes + Library + manual notes (`kind` column) · M6 LLM notes (three passes, TL;DR last) · M7 calendar · M8 ship shape (onboarding, packaging, attribution, license) · M9 character & polish

## Build & test

```
dotnet build Toki.sln
dotnet test Toki.sln
```

Solution: `src/Toki` (WPF app, `net8.0-windows`) + `tests/Toki.Tests` (xUnit, must also target `net8.0-windows`). CI can never verify GPU/model/audio paths — those are run-on-a-real-PC jobs forever.
