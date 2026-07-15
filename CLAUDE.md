# CLAUDE.md — Toki for Windows

Condensed from `WINDOWS-BUILD-PROMPT.md` (the full brief — read it when detail is needed). This file carries the same rules.

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
