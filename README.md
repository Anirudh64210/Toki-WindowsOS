# Toki for Windows

A meeting assistant that lives quietly at the edge of your screen. It notices when you're in a meeting, offers to record it, transcribes it, writes clean structured notes, and remembers every meeting in a searchable local Library — **entirely on your machine**. One-time $20, no subscription, nothing ever leaves the laptop.

- **Full project brief:** [`WINDOWS-BUILD-PROMPT.md`](WINDOWS-BUILD-PROMPT.md)
- **Decisions & rationale:** [`ARCHITECTURE.md`](ARCHITECTURE.md)
- **Plain-language story of the build:** [`mansplain/`](mansplain/)
- **Design reference:** [`Toki-ProductDesign/`](Toki-ProductDesign/) (macOS notch design — Windows adapts it to a bottom-center pill)

## Building

Requires the .NET 8 SDK on Windows 10 2004+ (build 19041).

```
dotnet build Toki.sln     # build everything
dotnet test Toki.sln      # run unit tests
dotnet run --project src/Toki   # run the app
```

Note: unit tests/CI can never verify audio, GPU, or model paths — those are verified by the self-test flags (`--mictest`, `--asrtest`, …) run on a real PC, milestone by milestone.

---

# Build log

One entry per milestone: what was built, how it was verified, what broke.

## M0 — Scaffold (2026-07-15)

**Built**
- Installed the .NET 8 SDK (8.0.423) — the machine had no .NET tooling.
- Solution `Toki.sln` with two projects:
  - `src/Toki` — the WPF app (`net8.0-windows`), currently the default template window (placeholder until M1).
  - `tests/Toki.Tests` — xUnit test project referencing the app.
- `CLAUDE.md` (condensed from the brief), `ARCHITECTURE.md`, `mansplain/`, `.gitignore`.
- Received `Toki-ProductDesign/` (PNG exports of the macOS design) — the UI reference for M1's pill.

**Verified**
- `dotnet build Toki.sln` — build succeeded, 0 warnings, 0 errors.
- `dotnet test Toki.sln` — 1 placeholder test discovered and passed, proving the test harness runs.

**What broke**
- The xUnit template targets `net8.0`, but a WPF app targets `net8.0-windows`, so the test project couldn't reference the app. Fixed by retargeting the test project to `net8.0-windows` with `UseWPF` enabled. Every future test project must do the same.

**Not done in M0**
- CI (build + test on push) needs a GitHub remote; will be added when the repo is pushed. Local `dotnet build` + `dotnet test` are the gate until then.

## M1 — Pill shell (2026-07-15)

**Built**
- `AppState` state machine (`src/Toki/State/`) — single owner of the pill's state, explicit transition table, wall-clock recording start. 12 unit tests.
- `PillWindow` — one borderless, topmost, transparent window fixed at bottom-center; an inner shell morphs width/height between states with eased animations (transparent areas are click-through). Sizes taken from `Toki-ProductDesign/`.
- Placeholder views for all 9 visible states (meeting prompt, recording bar, expanded recording, summarizing, notes, no-speech, home, library, settings) styled to the design: dark surfaces, pill buttons, coral record button, pink Toki blob, animated waveform.
- Tray icon (pink blob, generated `Assets/toki.ico`) with menu: open, demo states, library, settings, quit. Left-click toggles the pill.
- Global hotkey **Ctrl+Alt+Space** toggles the pill.
- Dev aids: `Toki.exe --state <name>` opens directly in a state; every state change is logged to `%TEMP%\toki-m1.log`.

**Verified**
- 12/12 state-machine unit tests pass.
- Sid drove the real app: hotkey toggles, home ⇄ library ⇄ settings, meeting prompt → dismiss, Take Notes → recording → summarizing → notes → close. The state log recorded every transition matching the clicks.

**What broke**
- The tray-icon package restored against .NET Framework under bare `net8.0-windows`; retargeted both projects to `net8.0-windows10.0.19041.0` (needed for M2 loopback audio anyway). A restore warning remains but the modern build is used and works.
- Verification detour: GDI screenshots cannot capture transparent (layered) windows, so my captures showed no pill while the app was on screen and fine. Lesson recorded — verify via the state log + a human looking at the screen.

## M1.1 — UI revision to match templates (2026-07-15)

Feedback from Sid: the pill was too big, buttons were full ovals, and Toki wasn't alive.

- **Ambient state added** — Toki is always quietly present as a small pill (112×30) at bottom-center with the pink character peeking over the top; hover lifts it out (the signature moment). No more "only appears on hotkey."
- **Toki is animated** — the character now breathes (squash/stretch) and blinks at natural irregular intervals, everywhere it appears. Summarizing shows a spinning sparkle, cycling status with animated dots, a paper stack, and Toki bobbing.
- **Buttons fixed** — rounded rectangles (13px), not capsules/ovals.
- **Per-monitor DPI manifest** — the app was being bitmap-stretched (big + blurry) on scaled monitors; now it renders crisply at each monitor's real DPI. This was the main cause of "huge."
- **Self-verification** — added `Toki.exe --render <dir>`, which renders every view to a PNG via `RenderTargetBitmap`. This captures the visual tree directly, so I can finally see the UI despite the transparent window that defeats normal screenshots.

Verified by rendering all nine views and comparing against `Toki-ProductDesign/`.

## M1.2 — Interaction + alive loading (2026-07-15)

Owner decisions (asked directly): idle shows **nothing**; Ctrl+Alt+Space **toggles** open/closed; closing also via **Esc, click-outside, or the X button**.

- **Idle is Hidden** — nothing on screen until a meeting, the hotkey, or the tray icon. (The always-on ambient pill was dropped per the owner.)
- **Ctrl+Alt+Space toggles** Home ↔ Hidden, force-applied so the user can never get stuck.
- **Click-outside / Esc dismiss** panels (Home/Library/Settings/Notes/NoSpeech) back to Hidden; recording/summarizing/prompt are deliberately immune to a stray click.
- **Loading state is now clearly alive** — spinning sparkle, cycling status with animated dots, a soft pulsing glow behind Toki, Toki bobbing, and a sheet lifting off the paper stack, on top of the character's own breathing + blinking.

**Verified**
- 13/13 unit tests pass.
- Live animation proven by capturing the running window twice ~450ms apart with `PrintWindow(PW_RENDERFULLCONTENT)` (which *can* grab the transparent window): the two frames differ — sparkle angle and dots changed.
- Shell size confirmed 340×164 via a post-layout `ActualWidth/Height` log (the PrintWindow grab renders transparent pixels as black, so size can't be eyeballed from it).

## M2 — Audio spike (2026-07-15) — the de-risk milestone

**Built**
- `MicCapture` — WASAPI shared-mode capture of the default mic ("you" stream) to a WAV, with peak metering.
- `ProcessLoopbackCapture` — the hard part: per-process-tree loopback via `ActivateAudioInterfaceAsync` + `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK` with `INCLUDE_TARGET_PROCESS_TREE` ("them" stream). Hand-written COM interop (no NAudio support for this), event-driven capture to a WAV. This is what will tap a browser meeting, where Meet's audio lives in a renderer child process.
- Self-test harness: a WinExe console attach (`ConsoleHelper`) + `--mictest`, `--loopbacktest <pid|name>`, and a `--playtone` helper. `--out <dir>` and `--seconds <n>` options.

**Verified (on this PC, via self-tests)**
- `--mictest` → recorded a real 48kHz WAV from the Realtek mic array, peak 24.6% (heard the room). Playable.
- `--loopbacktest` → one Toki instance played a 440Hz tone; another tapped **that process** and captured peak **20.5%** — matching the tone's 0.2 gain exactly (proves it's the real tone, not noise).
- **Selectivity proof:** with the tone playing, tapping the tone process gave 20.5% while simultaneously tapping a *silent* app (Slack) gave **0.0%**. Toki captures only the target's audio, never system-wide.

**Still to verify with Sid (by ear, brief's bar):** record a real 2-person browser Google Meet and listen to `you.wav` + `them.wav`. Commands:
```
Toki.exe --mictest --out <folder>                 # 10s of your mic
Toki.exe --loopbacktest chrome --out <folder>     # 15s of the meeting app (while it plays)
```

## M2.1 — Robustness, double-click app, ghost mode (2026-07-15)

Owner asks: it must never crash, it should be a double-click app for testing, deployment should feel like Wispr Flow, and it needs a Cluely-style undetectable recording overlay.

- **Never crashes** — global handlers for UI, background-task, and AppDomain exceptions; UI exceptions are logged and swallowed so the app keeps running. Tray-icon and ghost-mode setup are individually guarded.
- **Single instance** — a named mutex; a second launch exits quietly instead of fighting over the hotkey and stacking pills.
- **Opens on launch** — double-click now shows Home immediately (was: nothing until hotkey), then normal rules apply.
- **Ghost mode** — `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` + tool-window style: the overlay is visible to the user but **invisible to screen recording and screen-share**, so Toki never leaks onto the call it's taking notes on. Also kept out of alt-tab.
- **Double-click app** — self-contained single-file `dist\Toki.exe` (75 MB, bundles the .NET runtime → no "unable to run" prompt, runs on any Win10 2004+ PC). Shortcuts placed on the Desktop and in the project folder.

**Verified**
- 13/13 tests pass. Stress test launched all 9 states — 0 crashes.
- Ghost mode: a `PrintWindow(PW_RENDERFULLCONTENT)` capture (how recorders grab windows) comes back fully black while the app is visibly on screen.
- Double-click: launching the shortcut opens Home; a second launch detects the running instance and exits (stays at 1 process).

**Deployment note (future):** real install is a signed Velopack installer + launch-at-login (Wispr-Flow style), landing in M8. `dist\Toki.exe` is the test build.

## M3 — Meeting detection + real recording (2026-07-15)

**Built**
- `MicUsageReader` — reads which apps hold the mic *now* from the capability-access registry (HKCU **and** HKLM; `LastUsedTimeStop == 0`), decoding the `#`-encoded exe paths.
- `MeetingDetector` — denylist (dictation/recorders/audio tools/AI IDEs/Toki itself), debounce (2 polls ≈ 5s), 10-min per-app cooldown on dismiss, prompt-once-per-mic-session, and auto-stop when the recorded app frees the mic for 3 polls (≈ 7.5s). Poll logic is pure and unit-tested.
- `ProcessResolver` + `ProcessTree` — map a detected app to the **root** of its process tree (Toolhelp snapshot), so `INCLUDE_TARGET_PROCESS_TREE` reaches a browser's audio-rendering child.
- `RecordingSession` — starts the mic ("you") always and the per-process loopback ("them") when the app resolves, writing WAVs to `%APPDATA%\Toki\Recordings\<timestamp>\`. Falls back to mic-only if the tap fails — never crashes a meeting.
- Wired into the pill: detection → "Chrome is in a call — record it?" prompt → Record starts real capture → Stop/auto-stop → Summarizing → Notes (or the empty state if silent). `--detecttest`, `--recordtest`.

**Verified**
- 21/21 unit tests pass (8 new detector tests: denylist, debounce, reset, cooldown, can-prompt gate, auto-stop, presence-reset, no-prompt-while-recording). The debounce/cooldown tests caught a real re-prompt-spam bug, now fixed.
- `--detecttest` reads the live mic set and applies the denylist correctly.
- `--recordtest` drives the real `RecordingSession`: mic-only and mic+loopback both write playable WAVs to `%APPDATA%\Toki\Recordings\`.

**Robustness/UX fixes this round**
- Replaced focus-based click-away (which flickered the pill shut) with global low-level mouse/Esc hooks + a 500ms grace period — dismiss only on a real click outside the pill, no focus-stealing (keeps ghost feel).

**Still to verify with Sid (by ear):** join a real Google Meet, accept the prompt, talk, hang up → watch auto-stop, then play `you.wav`/`them.wav` from the recordings folder.

## M3.1 — Never-hang + animation redo (2026-07-15)

Owner feedback: Toki must **never hang** (always alive, responds only when summoned), and the animations should be buttery/cute like the Mac app — the loading screen especially should feel alive, not just glowing papers.

**Never hang**
- **Root cause fixed:** `StopRecording` used to block the UI thread up to 3s joining the capture worker (which also stalled the input hook → whole app frozen). Capture now starts *and* stops on background threads; the UI advances instantly.
- **Non-invasive input:** dropped the global keyboard hook (it saw every keystroke); the remaining mouse hook does zero UI work — it reads a **cached** shell rectangle, so it can never lag system input.
- **Relaunch surfaces the running instance** (named event) instead of silently exiting, so double-clicking a running Toki brings it up. Tray **Restart Toki** added.

**Animation redo**
- The loader is now a **cycling character scene**: Toki (breathing/blinking/bobbing, soft glow) beside a motif that changes through the stages — pulsing **rings** (Listening back) → a **document with a sweeping highlight** (Reading it over) → **checkboxes ticking** one by one (Finding to-dos) → a **pen drawing an underline** (Writing it up) — with a filling progress bar. Clean, rounded, spaced.
- Morph tuned buttery (gentle 340ms ease with a hint of spring).

**Verified**
- 21/21 tests, 0 crashes across all states.
- Live-captured the loader at 4 moments (via a dev `--noghost` flag) — the four stages visibly cycle with distinct animated motifs.
- Published app launches, single instance, relaunch re-surfaces it.

## M3.2 — Black-window + slowness fix (2026-07-15)

Owner reported: ghost mode works (recording doesn't capture Toki 👍), but the PC gets slow/buggy and a **black rectangle** pops up. Screenshot analysed.

**Root cause (confirmed via Microsoft docs/issue tracker):** `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` is **incompatible with `AllowsTransparency=True`** (layered) WPF windows — the combination produces the black rectangle, and the transparent window also forced slow **software** rendering. Separately, the click-away mouse hook ran on the **UI thread**, so when rendering stalled, *all* mouse input across Windows lagged.

**Fixes**
- **Pill is now a non-layered, hardware-accelerated window** with native **DWM rounded corners** + subtle border. Opaque, GPU-composited, sized to the current state's content and anchored bottom-center. Ghost mode (`WDA_EXCLUDEFROMCAPTURE`) works *reliably* on non-layered windows — no more black rectangle.
- **Mouse hook moved to its own dedicated thread** with its own message loop, so UI load can never lag system-wide input. Callback does pure arithmetic against a cached window rect.
- **Animations pause when hidden** — `TokiBlob`, `Waveform`, and the loader stop when off-screen (cached views were animating forever in the background). Fixed a blink-timer handler leak too.
- **Loader only animates the active stage** (previous stages' motifs were still animating invisibly).

**Verified**
- Ghost still works: a `PrintWindow` capture of the running window comes back blank (excluded).
- CPU: idle Home 3.8% → 1.8%; summarizing 6.5% → 5.3% (now GPU-composited). Hidden ≈ 0 (animations paused).
- 21/21 tests, 0 crashes. Home stays open with no interaction (click-away only fires on a real outside click).
- New window renders as a clean rounded opaque pill.

**Trade-off:** dropped the layered window's soft outer shadow and very-large corner radius; DWM gives clean anti-aliased ~8px corners. Stability + ghost reliability were the priority.

## M3.3 — UI fixes: rounded corners + content fit (2026-07-15)

Owner reported: sharp corners (not rounded), Library/Notes content cut off, record button pushed off-screen.

- **Sharp corners fixed** — DWM corner rounding doesn't apply to a borderless (`WindowStyle=None`) window, so the pill had square corners. Replaced with a `SetWindowRgn(CreateRoundRectRgn)` region at radius 22 (re-applied on every resize), which also brings back the larger pill radius the design wants.
- **Content cut-off fixed** — when the window became size-to-content, the content host stopped stretching the view to fill it, so views rendered at their natural (larger) size and clipped (Stop/Star buttons, right-edge text, bottom items). Switched the host to a `Grid` that stretches the view to the exact window size; text now wraps and tall panels (Notes) scroll internally. Also clamp window height to the work area so it never runs under the taskbar.
- **Verified** with DPI-aware window captures (the machine is at 125% scaling — my earlier non-DPI-aware captures were grabbing only the top-left corner, which faked "cut off"). Home, RecordingOpen, and Notes all render fully with rounded corners; the layout log confirmed `view == host` size. 21/21 tests, 0 crashes.

## M3.4 — DPI-proof positioning (2026-07-15)

Owner (with screenshots): the recording bar floated in the **middle of the screen** and Notes ran off the **bottom** — on their 125%-scaled display.

- **Root cause:** the window was positioned with WPF's logical `Left`/`Top`, which mis-scale on a per-monitor-DPI-aware app at 125%+ — so nothing landed where intended.
- **Fix:** position in **physical pixels via Win32** — read the real work area (`SystemParametersInfo(SPI_GETWORKAREA)`), the window's physical size (`GetWindowRect`), and place it with `SetWindowPos`. Fully dynamic/self-adjusting for any resolution or DPI. Window height is clamped to the physical work area so tall panels never run under the taskbar (they scroll internally).
- Removed the window-`Top` slide animation (it fought the physical placement); entrance is now the content pop+fade.

**Verified** (position logs + DPI-aware captures, screen 1920×1080, work-area bottom 1020): recording bar bottom-edge at 1002, Notes bottom-edge at 1002 — both sit just above the taskbar, centered. Recording bar and summarizing render correctly with rounded corners. 21/21 tests, 0 crashes.

## M4 — Transcription (2026-07-15)

On-device speech-to-text with **Parakeet TDT 0.6b v2** via **sherpa-onnx**.

**Built**
- `AudioLoader` — any WAV → 16kHz mono float32 (NAudio downmix + WDL resample), for the 48kHz-float "you" and 44.1kHz-16bit "them" streams.
- `ModelManager` — downloads + extracts the ~630MB Parakeet int8 bundle to `%APPDATA%\Toki\Models\` with progress (onboarding-time, never mid-meeting — lesson 2).
- `Transcriber` — sherpa-onnx `OfflineRecognizer` (`nemo_transducer`, greedy), whole-file per stream, one fresh stream per file (no leaked state — lesson 5), token timestamps grouped into segments. Empty result = silence, returns nothing (lesson 4/7).
- `TranscriptMerger` — merges you/them into one timeline and removes echo (drop a "you" line that duplicates a "them" line within ±4s; exact-match only for short interjections — lesson 6). 8 unit tests.
- `TranscriptionService` — orchestrates both streams → merge; owns the heavy recognizer (created once, warmed at launch); degrades gracefully if the model isn't installed.
- Wired into the flow: Stop → loader animation while transcription runs **off the UI thread** → Notes shows the real you/them transcript (or the empty state if silent). `--asrtest`, `--transcribetest`.

**Verified (real audio)**
- `--asrtest` on a 22.8s speech clip: transcribed near-perfectly, **tail matched the end** (no mid-file truncation), **2.3× real-time on CPU**.
- `--transcribetest` on two separate speech files: both transcribed accurately and merged with correct **You/Them** attribution.
- 29/29 unit tests pass.

**Deferred:** live streaming preview (streaming zipformer) and the whisper.cpp error-only fallback are stubbed (fallback logs + skips for now) — both are follow-ups; the core accurate transcription works.

## M5 — Storage + heuristic notes + Library (2026-07-15)

**Built**
- `MeetingStore` — SQLite at `%APPDATA%\Toki\toki.db` (meetings, transcript, decisions, questions, action_items; manual notes share the `meetings` table via `kind`). Save/Get/List/Search/SetActionDone/UpdateTitleAndBody/Delete (cascade). `you:`/`them:` search filters.
- `NoteExtractor` — deterministic "notes floor" (no model): TL;DR, decisions, action items (with owner + due heuristics), open questions; heuristic title with validation + `<app> — <date>` fallback (lesson 9). 7 unit tests.
- `MarkdownMirror` — every meeting also written to `%APPDATA%\Toki\Notes\*.md` (user-inspectable, export-ready).
- Wired: on transcription, notes are extracted and the meeting saved to DB + Markdown, then the Notes screen shows the structured notes (TL;DR / Decisions / checkable Action items / Open questions / Transcript).
- **Library** with real data: searchable meeting cards (date, tldr, open-actions/people tags), open → notes, delete-with-confirm, **+ New note**.
- **Manual notes**: editable title + freeform body, auto-saved, searchable alongside meetings (brief's NEW feature). `--dbtest`, `--notestest`, `--seed`.

**Verified**
- `--dbtest`: insert/search(you:/them:)/update/delete round-trip + cascade.
- `--notestest`: planted transcript → echo removed, decisions/actions(+owner/due)/questions extracted, clean title.
- Live DPI-aware captures of Library (3 seeded items), Notes (full structured notes), and the manual-note editor — all render correctly. 36/36 tests.

## M6 — LLM notes (2026-07-15)

On-device AI-written notes with **Qwen3-4B-Instruct-2507** (Q4_K_M GGUF) via **LLamaSharp**.

**Built**
- `LlamaLlm` (ILlm) — loads the ~2.5GB GGUF once, Qwen3 chat template, `DefaultSamplingPipeline` with Qwen's official params (temp 0.7 / topP 0.8 / topK 20 / presence 0.5; never greedy — lesson 8).
- `LlmNoteWriter` — three focused passes (decisions → to-dos → **TL;DR last**, from the reduced sections); every output validated (`TextValidator`: rejects CJK-drift / repetition), retried once, else falls back to the heuristic per section. 12 unit tests.
- `LlmService` + `AppSettings` — model selection (On-device / Heuristic-only / BYO API key, DPAPI-encrypted), **GPU off by default** (CPU is crash-proof; GPU is opt-in for 6GB+ cards). `OpenAiLlm` for the BYO path.
- Wired: after a recording, heuristic notes show instantly, then the LLM notes **upgrade in place** in the background. `--llmtest`. Functional Settings screen.

**Verified**
- `--llmtest`: Qwen wrote a correct, coherent, English summary + decisions + action items (with owners & due dates) from a test transcript — **62s on CPU**, no degeneration.
- Caught + fixed two real bugs: a GPU **VRAM OOM crash** on a 4GB card (→ default CPU, context 4096, GPU opt-in) and an **async deadlock** when blocking on inference from the UI thread (→ run off-thread). 48/48 tests. App launches clean with the native LLM backend configured.

## M6.1 — Refinements: notes shape, speed & polish (2026-07-16)

Owner testing on real meetings drove a batch of refinements to the notes, the engine speed, and the UI.

**Notes — Granola-style, one pass**
- Restructured the notes to **Summary → Key points → Action items** (labelled). Dropped the old "Decisions" section (it just echoed the to-dos) and "Open Questions". The `decisions` DB column is repurposed to store Key points (no migration).
- `LlmNoteWriter` now does **one structured generation** (Key points + Action items + Summary, summary written last so it's drawn from the details) plus a tiny title pass — down from four passes. Output token budget trimmed.
- **Concise, context-aware titles** via a dedicated title pass from the summary (e.g. "Finalize Branding and Webhook Integration"), with a validated heuristic fallback.
- Owner/due placeholder junk (`->`, `-`, `TBD`) is now stripped in the prompt, the parser, **and** the renderer, so old rows are clean too.

**Speed — 65s → ~13s for the AI notes**
- **VRAM-auto GPU offload** (`LlmRuntime`): reads the true GPU memory from the driver registry and offloads a *safe* number of layers (20 on a 4 GB card, ctx 3072), or full offload on a big card, or clean CPU otherwise. The Vulkan backend is loaded **only** when we actually offload — enabling it at 0 layers was the pathological slow path (was ~65s of CPU/GPU thrash; clean CPU is ~46s).
- Both engines are now **warmed at launch**, so a meeting-stop never waits on model load.
- Settings GPU toggle is now **automatic** (on = auto/safe, off = force CPU) instead of forcing an unsafe full offload.

**No more "bad first draft" flash**
- Toki now stays on the summarizing animation until the good AI notes are ready, then reveals them once — instead of flashing the rough heuristic draft (raw transcript lines) and swapping it out ~10s later. A 150s timeout guards against a stuck generation.

**UI polish (buttery pass)**
- New resizable **Library window** as the maximise target and the Home/tray Library button: master–detail, every meeting + manual note, with a **"‹ Back to Toki"** button.
- **Custom slim rounded scrollbars** and **custom rounded checkboxes** (coral fill + tick) app-wide; soft cross-fade when switching notes.
- **Copy transcript** and **maximise** buttons on the pill Notes view; removed a dead "search this transcript" box.
- **Manual notes** are fully editable in the window with an explicit **Save**.

**Transcription — findings (kept sequential on purpose)**
- Measured Parakeet at **~4× real-time on CPU**; the two tracks run **sequentially**. Parallelising them corrupted the transcript (sherpa-onnx isn't safe to decode two at once — non-deterministic output), so it was reverted: correctness over ~15% wall-clock. More CPU threads don't help (flat past 6). The shipped engine is **CPU-only** (no GPU execution provider in the package).
- Net: total summarize time is ~35s on a 1-min two-party meeting, dominated by transcription. The clean path to <30s is **live transcription during the meeting** (parked, task #14).

**Verified**
- `--llmtest`: new structure + concise title, **~27s cold / ~13s warm**, no degeneration. 49/49 tests.
- Live DPI-aware captures of the Library window, manual-note editor, and pill Notes view — all render correctly after fixing a WPF crash (a `Template` set inside a `ControlTemplate` trigger blanked the window black).

## M6.2 — Reliability on a small PC (2026-07-16)

Triggered by a real 30-min meeting that saved **"nothing was said."** Root-caused from the logs + the actual 784 MB recording to a **6 GB RAM** machine trying to hold both heavy models (2.5 GB LLM + 630 MB ASR) at once.

- **Manual start now captures the other side.** Recording from Home/hotkey was mic-only, so a muted user saved silence. `BeginRecording` now resolves the meeting app on the mic (`ResolveMeetingTarget`) and taps it. Auto-stop stays armed only for prompt-accepted meetings.
- **Long meetings can't crash ASR.** `Transcriber` windows long audio into ~3-min slices cut at quiet points (whole-file kept for normal meetings). Verified end-to-end on the exact 35-min file that used to throw.
- **Loader OOM fixed.** `AudioLoader` preallocates one exact array instead of a doubling `List` + `ToArray`.
- **Low-memory mode (`App.LowMemoryMode`, <8 GB).** Only one heavy model resident at a time (release LLM before transcribe, release ASR before notes, no LLM prewarm, release LLM after notes). This is the fix for the crash *and* the first-launch clunkiness.
- **Gentle warm** on a below-normal-priority thread; **junk-tap guard** (sub-3s, <5 words → discarded, no hollow Library entry).

Docs: `mansplain/11-fits-your-pc.md`.

## M6.3 — Calmer recording, sane dismissal, honest "glass" (2026-07-16)

- **Recording chip** — removed the fake looping waveform; the recording pill is now a small calm chip (breathing dot + time + stop, 138×46) instead of a wide animated bar.
- **Click-outside dismissal** — `DismissOnClickOutside` reduced to `{Home, NoSpeech}`; Notes/Library/Settings (and recording/summarizing) no longer vanish when you click into your real work.
- **"Glass" pill** — first attempt kept it opaque (mistakenly thought translucency broke ghost mode). Corrected in M6.4.

Still open: further speed (live transcription remains the real answer).

## M6.4 — Real translucent glass, crisp icons, design-matched Library (2026-07-17)

Owner pointed out (correctly) that Cluely / "cheating daddy" have transparent windows *and* stay hidden from screen capture — so the M6.3 "opaque only" conclusion was wrong.

- **Genuinely translucent pill** — `PillWindow` is now `AllowsTransparency=True` with a translucent Border fill + top sheen. Verified: `--noghost` shows a see-through pill; **ghost-on shows it absent from capture** (ghost still works, via `WDA_EXCLUDEFROMCAPTURE` re-applied on every show). No DWM accent acrylic (it caused sharp black corners); corners are clean because outside the Border the window is truly transparent.
- **Crisp icons** — all buttons use **Segoe Fluent Icons**; copy / open-window / close are now distinct (were ambiguous unicode glyphs). `IconButton` restyled to a clean floating glyph with a subtle hover highlight.
- **Library cards** — date badge + meta + snippet + "N open / N people" chips, matching `Toki-ProductDesign/⑥`.
- **Buttery Library open** — the window scales up from its bottom-centre (toward the pill) with a fade, so it reads as the pill extending into a window instead of a hard pop.
- **Drag-to-reorder notes** — pick up a card and the others slide to make space (iPhone-home-screen style); the order persists via a new `sort_order` column (safe migration, seeded to the current newest-first order). New notes land at the top.

## M6.5 — Monotone notes + morph/drag polish (2026-07-17)

Owner feedback on the first pass: coloured section headers + green tick were "ugly, not clean," the Library "force opened," and a dropped card could sit awkwardly between two rows.

- **Notes go monotone** — SUMMARY / KEY POINTS / ACTION ITEMS / TRANSCRIPT are quiet dim-grey labels (no coral/green/blue on text, no tick icon); chips are neutral grey (YOU a shade brighter than THEM). Apple-clean, generous consistent spacing. Removed the "ON-DEVICE" badge.
- **Morph grows from the pill** — the Library window's content now scales up out of the pill's *exact* on-screen rectangle (not the screen centre), so it reads as the pill extending into a window.
- **Drag settles cleanly** — on release the card animates into its target slot before committing (it can no longer sit between rows), and an off-by-one in the drop target was fixed.

## M6.6 — Transcript integrity + calmer recording + UI fixes (2026-07-27)

Owner tested real 20-min meetings and hit two real bugs plus a batch of UI issues.

**Transcript was shattered per word (the big one).** Notes came out garbled ("a udi o", "min ute", "D eep ik a"); the transcript broke every 1–2 sub-word pieces. Root-caused meticulously from the actual recordings + logs: `GroupIntoSegments` splits on a time-gap (>0.8s), and the split was allowed to fall **mid-word**. Parakeet emits words as sub-word pieces, and on hesitant/pausey real-meeting speech the gap landed inside words. Verified the tokenizer directly (`--asrtest` debug dump): this Parakeet build marks a new word with a **leading space** on the token (`" R","as","h","m","i"` = "Rashmi"), *not* the `▁` marker the old `Detokenize` assumed (its `▁`→space replace was a silent no-op — the spaces were already literal). Fix: only break at a **word boundary** (token with a leading space / `▁`), on either a real pause **or** end-of-sentence punctuation. Verified on the exact meeting that shattered: **912 fragments → 82 clean per-sentence segments**, whole words throughout, tail matches audio end.

**A muted-mic side is skipped now.** Logs showed meetings where the user's mic was muted the whole call (`you peak 0%`) yet we still spent ~half the total time transcribing pure silence. `TranscriptionService` now skips a near-silent stream (linear amplitude scan, early-exit) — roughly halves processing whenever one side is muted. (The remaining ~4×-real-time floor on a *fully-active* meeting is why live transcription is the next real speed win — parked below.)

**UI**
- **Recording is out of the way (WisprFlow-style).** While recording, the pill collapses to just the **alive Toki character** (breathing/blinking) with a small pulsing dot — no timer or button sitting on screen. Click it to reveal a compact **timer + Stop**. The expanded view's **fake placeholder transcript** (shown during real recordings — a lie) was removed.
- **Softer record button.** The harsh fire red-orange (`#F94A26`) → a truer, calmer coral (`#FF6F61`) across record/stop/accents.
- **Adaptive greeting.** "Welcome back, Sid!" (hardcoded) → the actual user (settings name, else Windows account name).
- **No em dash** in the "X is in a call. Record it?" prompt.
- **Library opens instantly.** It felt like a ~2s freeze. Profiled it: the cost was rebuilding content on every open — above all **rendering a 1140-segment transcript synchronously** into a non-virtualizing panel. Fixes: stream the transcript in on background priority (render ~30ms), stop rebuilding twice per open, and keep the window warm + skip the rebuild unless data changed (like WisprFlow keeping its window alive). Measured **~940ms → ~80ms**.

Verified: `--asrtest` on the real 20-min recording (82 clean segments), `--render` of every view, 49/49 unit tests.

## M6.7 — UI/UX overhaul: hover-reveal pill, smooth morphs, cleaner Library (2026-07-27)

A focused pass on how Toki *feels*, driven by live owner testing. Everything below was verified by running the app (view renders, mouse-driven hover cycle in the log, maximise capture) — not just described.

**Toki is now an always-present, hover-reveal pill (WisprFlow-style)**
- **Launch = the tiny alive Toki pill** (56×44, breathing/blinking), not a static "Welcome back" panel. **Hover it → Home reveals** ("welcome back" + the page); **move away → it collapses back**. Ctrl+Alt+Space toggles the pill on/off.
- **While recording**, same idea: the tiny Toki sits out of the way; **hover → it grows to the timer + a `Stop` button**; leave → shrinks back. No click needed.
- **The morph is smooth, not a burst:** the pill *animates its size* (grows over ~300ms, shrinks faster ~190ms so closing feels snappy), staying anchored bottom-centre, content fading in.
- **Hover detection is cursor-position polling, not WPF `MouseEnter`/`Leave`** — those fire phantom leave/enter while the window animates+repositions under the pointer, which caused an open/close flicker loop. Polling the real cursor vs the pill's real rect gives clean hysteresis (on it → open; off it → closed) with a dwell (~260ms, so a graze won't trigger it) and a quick close (~120ms).
- **Toki blinks + pops** the moment you hover the pill, and when Home/recording controls appear — alive and glad to see you.
- Fixed: the expanded recording pill sometimes clipped the `Stop` button (height was exactly tight) — gave it headroom so it always shows. Fixed: Home wouldn't collapse back (the state machine disallowed Home→Collapsed) — added the transition.

**Library window**
- **Opens instantly** (~80ms): created once and kept warm (pre-built off-screen at launch), hidden not destroyed between opens, and rebuilt only when data changed. The transcript is streamed in on background priority instead of built all at once. See ARCHITECTURE.
- **Maximise is clean now:** pinned to the monitor work area (`WM_GETMINMAXINFO`) so it snaps correctly with no jump; the content is a **fixed-width readable column** so widening/maximising doesn't re-wrap every transcript line (that reflow was the lag that made the content "sit then snap"); dark window border, not white.
- **Monotone selection** (soft white hairline, not the coral/red box). The maximise-from-transcript button now hides the pill first, then opens the warm window — consistent with Home→Library.

**Notes & Home**
- **Manual notes auto-save** in the Library window (debounced) — the static red **Save**/**Cancel** are gone; a quiet "Saved" flashes and fades; the editor left-aligns instead of floating centre.
- **One `Take Notes` button** on Home (the duplicate "Meeting" is gone — both just recorded).

## M7 (UI) — The virtual notch: Toki hangs from the top edge (2026-07-28)

A design pivot (owner-driven): instead of a bottom pill (which shadowed WisprFlow), Toki is a **virtual notch** at the top-centre edge — closer to the original macOS Toki, which "hangs from the notch". Built in three phases; the whole state machine, hover-morph, blink and ghost mode transferred unchanged, just re-anchored to the top.

**The notch**
- Anchored **flush to the top-centre edge**, square top corners, **rounded only on the bottom** — reads as carved into the top, not a floating pill. Grows **downward** (blooms out of the edge) and **tucks back up** into it on hide.
- **Idle is nothing.** It appears only when needed.

**How it comes and goes**
- **Reach the top-centre edge → it summons** (Phase 2). Tuned against accidental triggers (top-centre is crowded with browser tabs / window controls): a **narrow centre band**, the **absolute top pixel row**, a short **dwell**, and a **velocity gate** (a fast reach for a tab won't trip it). Suppressed while **dragging a window** (don't fight Snap Layouts) and in **fullscreen** (`SHQueryUserNotificationState`).
- **Peek ≠ open.** Reaching the edge shows only **Toki peeking** (the character alone — no dark box). Opening the welcome window is deliberate: **click Toki**, or **dwell noticeably** ("waiting for it"). A brief hover just shows the peek and tucks away.
- **Hotkey / launch** also summon it; **Home stays until Esc/click-away** (an explicit summon never vanishes).
- **Recording** shows a **minimal eyes-peek** (tiny, clear of browser tabs) — hover reveals timer + Stop.
- **Results** (notes / "nothing was said") **drop for a glance (~2s) then tuck themselves away** so they never sit on your work — unless you hover to keep reading.
- **Multi-monitor:** appears on the screen your cursor is on.

Env `TOKI_NO_EDGE` disables edge-summon (a Settings toggle will follow). Settings notch trimmed (no dead space); the Settings page content is still placeholder wiring — flagged for the ship pass.

## M7.1 (UI) — Context-aware snap-bar: Toki first, tuck-not-dismiss (2026-07-28)

Refined the notch into a proper snap-bar that remembers where you were and never squats on your work.

**Toki first, then the page**
- **Launch / reach-the-edge → Toki peeks** (the character alone, no panel) and greets for ~3s. **Click Toki** (or rest on it) → it **blooms open into your remembered page** — Home by default, or whatever you were last on.
- **Context-aware.** Toki remembers the current page (`_context`). Clicking into your real work **tucks the page to a thin sliver (keeps context)** instead of resetting to Home; re-summoning restores that page. A tucked sliver fully hides after ~9s, still remembering the page.

**Persistent tucked sliver (no rebuild)**
- Any page (Home / Notes / loader / "nothing was said") **tucks to a sliver ~2.5s after you stop hovering** — not gone, a little hangs from the edge. **Hover the sliver → the SAME page slides back down** (context preserved, no reload).
- **Loader** shows ~2.8s then tucks regardless of hover (it's a transient "working" flash). **Results arrive as a quiet sliver** (a peek), never a panel dropped in your face — hover to open.

**Recording = upside-down Toki**
- Toki **flips and falls** to hang **upside-down**, only its **eyes peeking** over the edge, blinking — minimal, alive, clear of browser tabs.

**Two real bugs fixed**
- **Timers were starved during model warm-up.** The tuck timers ran at `DispatcherTimer` default *Background* priority, so they got starved while the ASR/LLM models load at launch — exactly when Home / the loader show. That's why pages during warm-up never tucked but Notes (later) did. Bumped to **Input priority**. (This was the "works in the demo, not in real use" bug.)
- **Accidental-tap guard strengthened.** A ~5-word mis-click ("Met the t I mean") was saved as a meeting because the guard required <3s **and** <5 words. Now drops anything with **<12 words or <4s** — a mis-click is never a meeting.

Sizes trimmed smaller across the board (Notes no longer half-screen). Timer/animation knobs are all constants at the top of `PillWindow`.

## M7.2 (UI) — Pitch-black notch, close-off that *sticks*, tiny recording peek (2026-07-28)

Owner testing in a real Chrome session exposed that M7.1's "tuck to a sliver" left Toki squatting on the screen, and — worse — it kept **re-summoning itself**. Rebuilt the resting behaviour around one principle: **one element at the top, and once it's gone it stays gone until you deliberately ask for it back.**

- **Pitch-black notch.** The frosted-translucent surface read as a washed-out grey pill and slivers were nearly invisible. The notch is now a **fully-opaque pitch-black slab** (no white sheen, no translucency) — a real "hole carved out of the top edge," like the Mac notch / Dynamic Island.
- **Close-off, not a lingering sliver.** A page now **closes off completely** (→ `Hidden`, context remembered) when you move on, instead of leaving a strip hanging in your way. The **loader is the one exception** — it keeps a clean sliver as its "I'm working" heartbeat.
- **Summon that sticks (the "context-aware close-off doesn't work" bug).** The log showed `Home → Hidden → Collapsed → Home` looping every ~2s: the top-centre summon zone sits exactly on **Chrome's tab strip**, so the instant Toki closed off, the cursor (still up there) re-triggered it. Fix: the edge-summon **re-arms only after the cursor drops ≥150 px into the page body**, the arm is consumed on each summon and reset on every close-off, and the dwell went 220 → 300 ms. Now you can close Toki and keep working up top — it stays gone; you bring it back with a deliberate reach up (or `Ctrl+Alt+Space` / tray).
- **"Nothing was said" no longer loops.** Clicking *Okay* hid it, but the remembered page was still `NoSpeech`, so hovering re-opened it forever. A **result is a one-shot announcement** — dismissing it now resets the remembered page to Home.
- **Loader is a clean sliver.** It used to show a clipped slice of Toki's head + text when tucked (ugly). The loader view's top row is now exactly the sliver height, carrying only a **soft coral shimmer heartbeat** — Toki's body never peeks through. Redesigned smaller and cuter.
- **Recording peek shrunk** to ~34 px (was 52) so the upside-down eyes never clash with browser tabs or the snipping-tool toolbar.
- **`Ctrl+Alt+Space` summons the small Toki**, not the full welcome window (Toki-first, like the edge-summon).

## M8 (Notes) — Map-reduce, adaptive, honest notes (2026-07-28)

Two real meetings exposed that the notes were fundamentally broken, and the logs told the true story:

| Meeting | Transcription | Notes |
|---|---|---|
| 25-min | **602 s (10 min!)** whole-file at stop | LLM overran its ~3 k-token context → threw → silent heuristic fallback (TL;DR = the first three sentences, "to-do" = 19 keyword-matched lines) |
| 5-min "nothing" | 38 s | LLM ran and **manufactured corporate filler** out of a screen-share logistics exchange |

**Findings:** the 6–7-minute wait is *transcription*, not notes (see M8.1 plan). The bad notes are two separate failures — **context overflow on long meetings** (→ garbage fallback) and a **rigid template that forces KEY POINTS / ACTION ITEMS on every meeting** (→ invented filler).

**What changed — `LlmNoteWriter` is now map-reduce and adaptive:**
- **Map-reduce.** The transcript is split at speaker/segment boundaries into context-safe windows (`MaxChunkChars` sized from `LlmRuntime.ContextSize`). Each window is **mapped** to a compact factual digest; the digests are **reduced** into the final note. Summarising summaries never overflows — a 2-hour meeting works as well as a 5-minute one. (One-window meetings skip the map and read raw for best fidelity.)
- **Adaptive + honest.** The reduce step judges the meeting's substance and emits **only the sections that genuinely apply** (empty sections are already dropped by both renderers, so this is adaptive-by-omission — no schema change). A meeting with no real content gets a one-line honest note, never filler. The `NOTHING` escape is deliberately narrow (**greetings / small-talk / connection-checks only**) so a real-but-informal working session isn't wrongly dismissed.
- **Title from the whole-meeting summary**, not the opening line.
- **Verification:** new **`--renotes <meetingId>`** self-test re-runs the writer on a stored meeting's real transcript and prints the result. Confirmed against the two failing meetings — the 25-min one now yields *"Refine Weekend Attendance Tracking"* + a real whole-meeting summary/points/actions (80 s); the 5-min one yields an honest *"nothing substantive"* line (7 s). App notes timeout raised 150 → 300 s (map-reduce makes several short calls).

## M8.1 (ASR) — Pausey speech no longer shatters the transcript (2026-07-28)

A presenter reading slowly ("And… professors… Brown… University.") produced **one word per line** in the transcript. The M6.6 fix stopped mid-*word* shatter, but a >0.8 s pause *between* words still forced a new segment. Fix: a pause break now only fires once the line already has **≥ 8 words** (`MinWordsForPauseBreak`); sentence ends (`. ? !`) still break freely. Verified by re-transcribing the real clip → clean full-sentence lines. (This also fixed a knock-on notes false-negative: a shattered transcript looked like noise and got a wrongly-empty note.)

## Live transcription — the next milestone (Phase 2, planned)

The 25-min meeting spent **10 minutes transcribing after Stop** because Toki does one whole-file Parakeet pass at the end (and the sherpa-onnx build is CPU-only — no GPU ASR). Owner-approved plan: **incremental Parakeet during the meeting** — a voice-activity detector splits the live audio at natural pauses and each utterance is transcribed in the background, so **Stop → transcript is near-instant**. One recognizer feeds both a live preview and the saved transcript (memory-friendly for low-RAM PCs). The notes' "map" passes can then run *during* the meeting on capable machines for near-instant notes. This is the real fix for the multi-minute wait; it touches the audio pipeline so it needs a real meeting to verify by ear.

## M9 (BYO API key) — Optional cloud-speed notes, done securely (2026-07-29)

On-device notes keep every meeting private but are slow on a modest PC (see `mansplain/16`). This milestone adds an **optional** path: bring your own API key from a cloud provider you already pay for, and notes come back in seconds. It stays true to the $0-upkeep rule — **it's your key and your cost; Toki never pays and never takes a cut.**

**What shipped:**
- **Works with any provider, not just one.** Toki speaks the common chat-completions format, so the same setting works for **Claude, OpenAI, Groq, OpenRouter**, or a **local model** — and any OpenAI-compatible router. Claude works through Anthropic's compatibility endpoint with no special build.
- **Dead-simple, provider-first setup.** In **Settings → My API key** you: pick your **provider** from a dropdown (the web address is filled in for you), paste your **key**, then pick a **model** from a list Toki fetches live from that provider. Switching provider reloads the right models — nothing gets stuck. A searchable dropdown handles providers with hundreds of models.
- **Your key is protected, hard.** It's **encrypted on your PC** (Windows DPAPI, tied to your user account), stored only under `%APPDATA%\Toki\` — **never** in the project, never in plaintext, never in a log, and scrubbed out of any error message. It travels only over an encrypted (https) connection.
- **Honest connection status.** One clean line with a status dot: it only turns **green (with a gentle pulse)** after **Test connection** actually reaches your provider and gets a reply — it never claims to be working just because a key is present. One-tap **Remove key** wipes it and returns you to the free on-device model.

**Findings/fixes along the way:** an early version quietly built everything around Claude (auto-targeting it, defaulting the model to a Claude one) — corrected to be fully provider-neutral. A first attempt used an iOS-style scroll-wheel picker — replaced with cleaner dropdowns. And a self-inflicted one worth recording: the screenshot/render tool was reading the *live* settings and firing the model-fetch, which corrupted the settings file (and cost a saved key); the render harness is now fully offline/read-only (`App.Headless`), and the rule "never mutate real settings to take a screenshot" is written into project memory. Verified: 82/82 tests pass, no key material anywhere in the repo, clean 130 MB packaged build.

## M9.1 (Recording) — Record-a-demo toggle; Library un-ghosted (2026-07-31)

Toki's notch runs **ghost mode** — it's excluded from **every** screen recorder (Snipping Tool, Xbox Game Bar, **OBS**) and from screen-share. That's the privacy win (Toki never shows up in a meeting you're sharing), but it also means you can't record a demo of Toki. Two changes came out of the owner trying to record a walkthrough:

- **New "Show Toki in screen recordings & sharing" toggle** — Settings → **Recording & sharing**. **Off by default**, so Toki stays hidden and private; turn it on to record a demo, then turn it back off. Takes effect **immediately, no restart**. (The `--noghost` dev flag still works.) Under the hood it flips `App.GhostDisabled`, which swaps the window between `WDA_EXCLUDEFROMCAPTURE` and `WDA_NONE` live.
- **The Library is no longer ghosted.** Testing revealed the Library window was *also* excluded from capture — invisible in a recording **even after** turning the toggle on. It had been silently copied from the pill "for consistency," which the owner never asked for. Per owner decision it was **removed entirely**: the Library now records and screen-shares like any ordinary window. Trade-off (owner-accepted): if you screen-share with the Library open, its meeting list is visible to others. The pill's ghost is deliberately kept pill-only.
- **Still open (parked):** with the toggle on the notch *is* capturable, but Snipping Tool pins its own control bar to the top-centre of the recorded screen — right where the notch sits — so they overlap regardless of how many monitors you have. The clean fix for a top-centre notch is a recorder whose controls live in their own window (e.g. OBS on a second screen, capturing the notch's screen). Tracked for a later pass.

Verified: build clean, **82/82 tests pass**, new build launches cleanly (ghost affinity applied with no error in the log).

## M10 (Look, feel & templated notes) — 2026-08-02

A broad refinement pass across appearance, motion, and note quality.

- **Live light/dark theme.** A sun/moon toggle in Settings flips the *whole* app — notch and Library — between the signature dark and a clean, warm Apple-white, instantly, no restart. `ThemeManager` swaps a small palette of brushes that every screen references dynamically. (Two dead ends recorded in ARCHITECTURE — poking Color resources, and mutating brushes, which WPF freezes; the fix is to *replace* the brush.)
- **The notch is a tinted mirror.** The black and white notch are now semi-transparent, so the desktop colours behind them bleed through faintly — see-through, not blurred. Kept fairly opaque so text stays readable and it never washes out to grey.
- **Templated notes (8 shapes).** Toki auto-detects the meeting type — Standup, 1:1, Interview, Brainstorm, Lecture, Sales call, Decision log, or General — and writes the note in that shape (a standup gets Updates/Blockers; an interview gets Strengths/Concerns/Recommendation). Tasks are name-tagged to whoever they were actually given to (or "You"), never guessed; summaries are tuned to cut filler. Notes store flexible sections; older notes still render.
- **Smoothness.** Notch open/close is GPU-composited (an animated clip, no per-frame relayout); the Library grows out of the notch and retracts back to it; every scroller glides; maximise eases in.
- **Cloud mascot.** Toki is now the pink cloud from the product design (three humps + eyes), not a single oval.
- **Cleaner UI.** Theme + screen-recording controls are single header icons (shown once, not repeated under each notes-mode page); the recording icon is a camera crossed out when Toki is hidden. Deleting a note uses Toki's own rounded dialog, not the Windows box.
- **Housekeeping.** Removed the dead `Waveform` control and a never-firing notes-update event chain. The dev PC was found to be 11.4 GB RAM + RTX 3050 (not the old 6 GB) — noted for on-device headroom.

Verified: **83/83 tests pass** (incl. new template-detection and live-theme-swap self-tests), clean 130 MB build.

## P1 (Library → hub) — 2026-08-02

The Library window became Toki's **home** — a Claude-desktop-style hub. (Phase 1 of a larger plan; the chatbot is Phase 2.)

- **Sidebar** — the Toki cloud mascot up top (click it → a cute reaction that varies each poke), nav for **Ask · Library · Templates**, and **‹ Back to Toki** at the bottom.
- **Ask** (the landing) — a greeting that types itself in and rotates each open, a centered search bar with a **mic**, and quick suggestion chips. *(Phase 1: a query routes into the Library search; conversational answering over your notes + voice input arrive with the Phase 2 chatbot.)*
- **Library** — the meetings list + note detail, with a **drag-reorder toggle** (off by default, so cards never move by accident) and a **per-note template picker** (change the shape → Toki re-writes the note in it, off the UI thread).
- **Templates** — all 8 shapes with the sections each produces.
- Smooth page fades; theme-reactive throughout.

Verified: **83/83 tests pass**; hub pages render clean via a new `--libshot` dev flag. Fixed at the pre-commit review: the template-rewrite ran the model on the UI thread (would freeze the window) — now offloaded to `Task.Run`; `LlmService.CanWrite` no longer force-loads the model.

## P1.1 (Library note polish) — 2026-08-02

Copy/selection and layout fixes for the open note.

- **Whole note is selectable + copyable.** The note is now one rich-text document, so a single drag from top to bottom selects everything (summary → bullets → to-dos → transcript) and Ctrl+C copies it all — not one bullet at a time. **Copy** is a split button: the button copies the *entire* note; the **▾** offers everything / summary+to-dos / transcript / reveal the note's file. Proven by a headless `--copytest` (a mouse drag can't be screenshot-verified, so the test selects the whole doc and checks every part is captured).
- **Clean note layout.** Content is left-aligned and fills the pane at any window size (no centered column with a big left gap); the scrollbar sits at the window's right edge. Top toolbar: **Template** on the left, **Copy · Delete** on the right, above the title. **Delete** is a clear red, not a greyed-out look. Action-item name tags are bright (blue/amber) and readable on both themes.
- **Buttons truly centered.** Segoe UI Variable sat labels ~1px low; corrected in the button template (measured against a true-center line via the `--libshot` `buttons.png` probe). The "‹ Back to Toki" chevron uses a properly-centered icon glyph.
- **Resizable panels.** VS Code-style seams — invisible until hover, then a thin pink line — between the sidebar/list/note; drag to resize. Clicking **Library** while already on Library **collapses the note list** (animated); click again to bring it back.
- **Outlined inputs.** The Library search box and the Settings API-key / Address fields get a hairline outline so they no longer blend into the surface behind them.

Note: `NotesQa.cs` (retrieval for the Phase 2 chatbot) is new and untracked — add it when committing.

## P2 (Ask chatbot — fast + smart) — 2026-08-02

"Ask Toki" became a real assistant over your notes.

- **Answers stream in.** Words appear as the model writes them (instead of waiting for the whole reply) — the big perceived-speed win. Works for both the on-device model and a BYO API key.
- **Better, grounded retrieval.** A local **FTS5 full-text index** (BM25-ranked over title, summary, sections, transcript, and tasks) finds the most relevant meetings; the answer cites them as chips you can click to open. Falls back to keyword/recent if needed. Verified against the real library with a `--asktest` dev flag.
- **Instant task answers.** "What are my open tasks" is answered **straight from the database** (exact, no model, no hallucination). It's **scope-aware**: naming a meeting ("action items in the Postgres meeting") lists just that meeting's items; a bare "my open tasks" lists everything still open.
- **New chat + history.** A **+ New chat** button starts fresh; a **History** list reopens past conversations (auto-titled from the first question, deletable) — no more being stuck in one endless thread. Conversations persist locally in `chats.json`.
- A tighter prompt (context first, question last, today's date, short-answer constraint) keeps replies concise and on-topic.

Verified: **87/87 tests pass** (incl. new FTS retrieval + open-tasks tests); `--llmtest` confirms the model still generates cleanly through the new streaming path. *(Voice input in the Ask bar is the next slice.)*

## P2.1 (Ask — understand the question first) — 2026-08-02

The chatbot was giving confidently wrong answers to questions about **time** — because it searched by keyword and let the AI guess dates. Fixed by understanding the question *before* answering (like Granola/Hyprnote/Notion do):

- **Dates & recency are now handled by the database, not the AI.** "My most recent meeting" is a date sort; "meeting on July 30" / "last week" / "in March" are date filters (parsed offline by Microsoft's date recogniser). The AI is *told* the right meeting and only writes the answer — it can no longer pick the wrong one.
- **Instant, exact answers** for "how many meetings…", "what meetings did I have this week", and task lists — straight from the database.
- **Smarter task scoping** — "action items in the July 21 meeting" now lists *that* meeting's items (by date), not all 25.
- **Intent is judged on your question, not note titles** — so a meeting named "…Follow-Up" no longer triggers the task list.
- **Conversation memory** — follow-ups work: "before that" shifts the week, "what about the action items?" / "summarize it" stay on the meeting you were just discussing.

Verified: **107/107 tests pass**; new `--querytest` / `--followuptest` / `--asktest` dev flags prove the routing and follow-ups on the real library.

**Deferred — on-device meaning-search (embeddings):** planned as the next quality layer, but both ways to run a second small model on-device conflict with an engine Toki already ships (a native `onnxruntime.dll` version clash with the transcription engine; and the embedder *hangs* on the GPU/Vulkan backend that the notes AI needs). It was **not** forced in — that would risk transcription. The fix (run the embedder in an isolated CPU-only helper process) is scoped for the next iteration; the code is present but dormant and unused. Keyword search stays active meanwhile — the bugs above were about time/intent, which are now fixed. See ARCHITECTURE.md for the technical detail.

## P3 (UI polish — notch behaviour + Library refinements) — 2026-08-02

A pass focused entirely on how it *feels*, driven by a short set of design rules (dividers-not-boxes, selection-isn't-an-outline, motion is brief, professional colour, one clean scroll from the top). No new features.

**Notch behaviour**
- **Dropdowns no longer shut the notch.** When the model/provider list drops *below* the notch, it now counts as part of the notch — you can scroll and click an option without it retracting mid-pick. Clicking the dropdown again closes it (fixed a Windows toggle quirk).
- **Cleaner open.** The reveal is clipped to zero height *before* the window shows, removing a one-frame flash on hover-to-open.

**Library**
- **Airy note list** — rows are open (no boxed tiles); only the selected note carries a soft fill, hover shows a subtle one. The bright divider line is gone (a faint grey seam appears only while you drag to resize).
- **Normal top-to-bottom scroll** — the whole note (actions, title, body) is one scroll filling the pane, so the scrollbar runs the full height from the very top.
- **Small "＋ New note" and a book "My notes" filter** by the Library title; the filter shows only your hand-written notes.
- **Title & date are selectable/copyable**, and a freshly opened note starts at the top.

**Settings**
- **Connection status redesigned** — dropped the pulsing dot + status card; one quiet line under *Test connection* (green "Connected" when verified), and the button itself confirms.

**Colour** — Delete is a cleaner crimson shown as red *text* on the neutral button (no loud red pill); the red fill is reserved for the confirm dialog.

Verified by rendering the Library (`--libshot`) and **107/107 tests pass**. Known-open: a small transient glitch to the left of the mascot during hover-to-open — being tracked; needs a live capture to pin down.

## P4 (Ask by voice — on-device) — 2026-08-02

The Ask bar's mic is live. Click it, speak, click again — Toki transcribes your question **on-device** (the same Parakeet speech model already used for meetings, warmed at launch) and drops the text into the box for you to glance over and send. Nothing leaves the machine; no extra download. It's user-initiated and mic-only — not always-listening (that's the later "Hey Toki" carve-out).

- **Auto-stops when you finish** (Google-/Safari-style): once it's heard you speak, it ends on a generous ~1.8 s silence, transcribes, and fills the box — no need to tap stop (you still can). Gives up gracefully if nothing's said, and hard-caps a single utterance.
- **Live feedback**: the input box shows a pulsing coral **"Listening…"**, then a pink **"Transcribing…"** loader while it thinks — the mic glows coral throughout.
- Fills the box for review rather than auto-sending, so a mis-hear never fires the wrong question.
- Degrades kindly: a clear hint if the speech model isn't installed or the mic can't be reached.

Verified end-to-end with a new `--voicetest <wav>` dev flag (synth a clip → `TranscribeFile` → text); the live mic capture, auto-stop, and loader are confirmed by ear/eye. 107/107 tests still pass.

Also this pass: the Library note list renders **edge-to-edge on one background** (no inset box / padding strip), the note toolbar's **Template** picker works inside the scroll (rebuilt as a real button + themed menu), and the VS Code pane resizer was removed.

## P5 (Mascot redesign + press-to-talk hotkey) — 2026-08-02

**Toki looks like Toki now.** The flat, sharp cloud was redrawn as the soft, dimensional character from the product design — a glossy light crown fading to a deep pink base, cheek blush, glossy eyes, a gentle lift-shadow. It's one control, so it upgraded everywhere at once (the notch peek, Home, the Library sidebar).

**Toki feels alive.** The corner mascot plays a *different* reaction every time you poke it — squish, hop, wobble, wink, spin, big-eyes, nod — and there's a springy "drop-in" hello. Idle breathing and blinking continue underneath.

**Hotkey = press-to-talk.** `Ctrl+Alt+Space` now opens Ask and starts listening straight away — a silent, always-reliable way to ask by voice. There is **no always-on mic and no wake word**: the owner chose press-to-talk only, so Toki still only listens when you ask it to (Hard Constraint #3 stays fully intact). The "Hey Toki" wake word was researched (sherpa-onnx keyword spotting) and deliberately dropped.

107/107 tests pass; mascot verified by render, voice/hotkey by ear.

## P6 (Push-to-talk on the notch) — 2026-08-02

`Ctrl+Alt+Space` is now **hold-to-talk**, Wispr-Flow style: **hold** it and Toki drops down from the top into a listening notch (Toki + a pink waveform); **speak** while holding; **release** and it thinks, then the answer streams onto the notch. The mic runs **only while you hold the key** — there's no always-on listening and no wake word, so Toki still only listens when you ask it to.

- New `PushToTalkHook` (a low-level keyboard hook) sees the key *release* that `RegisterHotKey` can't, so hold/release works.
- New `AskView` notch flow: listening (waveform) → thinking (loader) → answer (selectable, scrolls); the notch grows to fit each phase. Esc / × / a 30 s timeout dismiss it.
- Reuses the on-device Parakeet transcription and the Ask engine — nothing new leaves the machine.
- Full polished Q&A-on-the-notch is a future refinement; this is the working first cut.

107/107 tests pass; the listening notch verified by render, the live hold→speak→answer flow by ear.

## P7 (Toki becomes an assistant) — 2026-08-02

The push-to-talk notch is now a proper quick-access assistant, built on deep research into how the best assistants (Claude, ChatGPT voice, Perplexity, Raycast, Granola, Apple App Intents, Copilot/Dia) behave.

- **Quick-view answers.** The notch answers in 1–3 short, personal sentences — "a sharp colleague who was in the room," not a report. Brevity is enforced in code (≤280 chars, cut on a sentence boundary); when an answer is genuinely long, Toki gives the one-line takeaway and a **"See full answer in Library →"** button that jumps there. The Library keeps giving fuller answers.
- **Assistant commands.** "Take me to the library", "open settings", "new note", "go home", "close", "help" — Toki just does them. A deterministic router runs first, with a question-veto so "what did we decide in the *settings* review?" is still *answered*, never mis-fired as a command. (35 adversarial tests.)
- **Screen-aware.** Ask "what were the action items?" while looking at a meeting's notes and Toki answers about *that* meeting — because it reads its *own* window state (which note is open), never your screen. No screenshots, no OCR, nothing leaves the machine — a privacy story Copilot/Recall can't tell.
- **Session memory.** The notch remembers the thread while it's open (follow-ups like "and who owns them?"), and starts fresh when you close it.

142/142 tests pass.

## P7.1 (Assistant polish) — 2026-08-02

Refinements to the notch assistant from real use:

- **Words render in patches**, not letter-by-letter — both the notch and the Library Ask buffer tokens and flush in ~70 ms chunks.
- **No ugly cut-back.** A long answer used to stream out in full then snap down to a short one. Now the notch stops generating at a clean sentence boundary once it's past the glance budget, so what you see is what you get — with the "See full answer in Library" button for the rest.
- **Adjustable notch.** The answer panel starts as small as its content and grows to fit as words arrive, then scrolls past a cap.
- **Conversation thread.** The notch now keeps the whole session as a scrollable thread of questions and answers (until you close it), with follow-up memory.
- **Natural commands.** "Pull up my library", "jump to settings", "let me see my notes" — not just one hard-coded phrase. (Questions still never mis-fire — 155 tests.)
- **Screen-aware actually works** — "what were the action items?" grounds on the meeting you're viewing.
- **The Library always pops up** — from closed, minimized, or behind — and a "full answer" hand-off opens a fresh conversation there.

## P8 (Ask Toki redesign — the "it's dumb" fixes) — 2026-08-02

Five verified live-session failures were root-caused in the code and fixed deterministically — the full story (root causes, research, and the owner live-test script) is in [`docs/ask-toki-redesign.md`](docs/ask-toki-redesign.md).

- **A person question isn't a meeting list.** "What was discussed about Kai Sinat in my meetings?" now *searches for Kai Sinat* (and says honestly when nothing's there) instead of dumping "your 10 most recent meetings". Retrieval also queries only the words that carry meaning, so filler like "discussed"/"meetings" can't drag in random notes.
- **"This meeting" means the note on your screen — reliably.** The "what's open right now" state is now *derived* from the real window state instead of a cached value that went stale (that's what answered about the wrong meeting). The notch also shows a small "About: <note title>" line whenever it answers about the open note, so you can *see* what it grounded on.
- **"What did Mac have to do?" filters by Mac.** Names are recognised against the owners that actually exist in your action items (first names and "Mac's" included), on every task question — with an honest "no items for Mac here; those are owned by Sid" when that's the truth.
- **Toki answers for itself.** "Why are you so rude?", "do you see my screen?", "is my data private?" get short, warm, in-character replies — never a search of your notes ("there's no mention of rudeness in the meeting notes" is gone). The screen one names the open note honestly.
- **Tighter voice.** The sampler now matches the Qwen3 model card exactly, prompts carry an example answer in the target "sharp colleague" voice, and quick answers stop cleanly at the end of the answer instead of overrunning and being trimmed.

228/228 tests pass — every screenshot bug is now a permanent regression test. Final tone check is the owner's live run (script in the doc).

## P9 (Ask Toki — the reasoning route, "me", and the full command set) — 2026-08-02

Full story in [`docs/ask-toki-reasoning.md`](docs/ask-toki-reasoning.md).

- **"Assigned to me" now knows who "me" is.** Self-questions filter to the items owned by *you* ("You" in the notes), with an honest "nothing here is yours — these belong to Lily and Mac" when that's the truth. Multi-owner items ("Lily, Mac") now match each named person too.
- **Thinking questions get a thinking pipeline.** "What should I focus on?", "themes across my meetings", "how is X progressing?" now read up to six related meetings (not three), with a conclusion-first reasoning prompt and four times the answer budget — instead of being forced through the one-glance lookup pipe.
- **A whole command vocabulary.** "Start taking notes", "switch to the api/on-device/heuristic model", "switch to white/dark theme", "ghost mode on/off", "open templates", "open the assistant" — and "take me to the library" now lands on the actual notes list. All question-vetoed, all instant.

300/300 tests pass.

## P9.1 (Ask Toki refinements — the wake-name cluster) — 2026-08-03

Full story in [`docs/ask-toki-refinements.md`](docs/ask-toki-refinements.md).

- **Toki recognises its own name however the mic mangles it** (Doki/Toky/Docie/Toku/Tokyo…) — so spoken commands and "I love you, Toky" stop falling into notes retrieval (where the model *invented* answers).
- **"Which meeting was it?" is answered from the database** — oldest/first/most-recent (+ title) exactly; a date with several meetings lists them all instead of silently picking one; a topic that matches a meeting *title* ("the meeting regarding Amazon") is found deterministically.
- **Friendly app names + no more clipped notch** — "Slack is in a call. Record it?", measured *after* the text is set so the prompt always fits.

389/389 tests pass.

## P10 (The dialogue brain — Toki learns to talk) — 2026-08-03

The owner's verdict after live use: still "not smart — like the first version of ChatGPT". The root cause was architectural — **every** utterance was force-fit into meeting-notes retrieval ("what is my name" was *searched in the notes* and answered with citations; a misheard "switch to like theme" was "answered" with a fabricated link to an unrelated meeting). P10 rebuilds the orchestration into a real dialogue manager — full design, research and live-test script in [`docs/ask-toki-brain.md`](docs/ask-toki-brain.md).

- **Toki knows who you are.** "Call me Anirudh" is remembered (persisted — the greeting uses it too); "what's my name / who am I / what do you know about me" answer from that memory, never a search.
- **Toki can just talk.** Turns that aren't about your meetings — chit-chat, world knowledge, "I'm stressed about tomorrow" — go to the model's *own* knowledge with the conversation as context. No retrieval, no citations, no "not in your notes".
- **Meetings stay grounded.** Everything P8/P9/P9.1 built is preserved as the meeting branch, entered when the turn is actually about the meetings (wording cues, dates, screen, or real evidence in your notes).
- **Zero fabrication.** A retrieval with no matching content is an honest miss (never "analyse 5 random meetings"); an answer that admits a miss carries **no** source chips; a garbled command gets "did you mean the light theme?" instead of an invented notes answer.

428/428 tests pass. What tests can't judge — how *natural* the conversation feels — is the owner's live run (script in the doc).

## M7.0 (Google Calendar — the foundation) — 2026-08-03

The plumbing that connects Toki to Google Calendar, built to carry the calendar features that come next
(notch preview, prep cards, meeting creator). Owner setup guide: [`docs/google-setup.md`](docs/google-setup.md); decisions in `ARCHITECTURE.md`; plain-language story in [`mansplain/24-connecting-your-calendar.md`](mansplain/24-connecting-your-calendar.md).

- **Sign in, direct to Google.** Standard installed-app OAuth: **loopback redirect + PKCE**, the browser
  talks straight to Google, tokens land on the user's PC. No Toki server — hard constraint #1 intact.
- **Least privilege.** One scope pair — `openid email` + `calendar.events` (read **and** create, so the
  meeting-creator needs no second consent). No Gmail/Drive/contacts.
- **Secure at rest.** Only the long-lived refresh token is stored, **DPAPI-encrypted** (per-Windows-user,
  reusing the API-key pattern); access tokens live in memory and refresh lazily. Tokens are never logged.
  Verified: `settings.json` holds an opaque DPAPI blob, zero plaintext token.
- **Lean by choice.** No Google SDK — three REST endpoints hand-rolled over the shared `HttpClient` in the
  `ApiLlm` house style, so the single-file publish stays small.
- **New `Calendar/` module** — `GoogleAuth` (PKCE/loopback/exchange/refresh/revoke), `CalendarClient`
  (events list/insert/delete), `CalendarModels`, `CalendarService` (the facade every UI surface uses).
  Wired into the Home "Connect Google" button and a Settings "Google Calendar" connect/disconnect section.
- **Proven live**, not just compiled — `--caltest connect | list | insert | disconnect` ran against a real
  Google account: signed in, read the next 7 days, created+deleted an event, auto-refreshed the token. One
  real bug caught and fixed (an un-encoded `+` in the `timeMin`/`timeMax` timestamps → HTTP 400 → now
  `EscapeDataString`d).

446/446 tests pass (+18 calendar: PKCE correctness against the RFC 7636 vector, event-JSON parsing across
timed/all-day/cancelled/Meet-link shapes, id_token email decode). Ship-side follow-up: Google's free
**sensitive-scope verification** (privacy-policy page + demo video) before selling, to drop the
"unverified app" warning — queued with the M8 attribution/licensing work.

## M7.1 (Calendar UI — the surfaces) — 2026-08-03

The visible calendar, built on the foundation's `CalendarService` facade. Design matches
`Toki-ProductDesign/③ Home` exactly; plain-language story in
[`mansplain/25-calendar-in-the-notch.md`](mansplain/25-calendar-in-the-notch.md); decisions in `ARCHITECTURE.md`.

- **Home redesign (connected):** a two-up **Meeting** + **Take Notes** row, a **"COMING UP"** list (date
  badge · title · time · **Join**, with an amber "in N min" treatment for imminent meetings), and an
  account footer with a live green dot. Not-connected keeps the Connect-Google CTA.
- **Calendar page** (`PillState.Calendar`, 392×472): an **agenda-by-day** week view (the right form for a
  narrow surface — not a cramped 7-col grid), today picked out in amber with a live **"now" line**, week
  nav (this week + 3, no fake past), Join, and a per-event **…** menu (Join · Edit · Open in Google · Delete).
- **Create — natural language, one box:** type "Design sync tomorrow 2pm with ana@work.com"; a live green
  preview shows the parsed time; chips for duration / Google Meet (default on) / people. Backed by the
  already-shipped `Microsoft.Recognizers` (future-preferred) in `QuickEventParser`.
- **Edit** reopens the same composer prefilled (Save mode) via a new `events.patch` (`sendUpdates=all`).
- **Delete — safe but fast:** instant + an **Undo** bar for solo events; a one-tap **confirm** when guests
  would get a cancellation email (that one isn't reversible).
- **Provider-neutral by construction** — views touch only `CalendarService` + the models, so Outlook
  drops in later with no UI rewrite (`CalendarService.ProviderName` is the one naming seam).
- New: `Calendar/UpcomingCache` (stale-while-revalidate, shared by Home + Calendar), `Views/CalendarRows`
  (shared row builders), `Views/CalendarView`, `Views/NotchMenu`, `Calendar/QuickEventParser`,
  `Dev/CalendarSamples`. Self-tests: `--caltest update`, `--caltest parse`.
- **Verified:** all screens render-checked (connected Home, week view, create composer with live preview);
  create / **edit** / delete / parse run live against a real Google account (edit = insert→patch→verify→delete PASS).

450/450 tests pass (+4 natural-language parser: title/time/email extraction, 30-min default, no-time, past-rolls-forward).

## M7.2 (Pre-meeting prep cards) — 2026-08-03

The calendar earns its keep: ~5 min before a real meeting, Toki auto-surfaces a prep card fusing the
calendar (what's next) with the notes store (what happened last time + what's still open). Design matches
`Toki-ProductDesign/④ Assistant — prep card`; story in [`mansplain/26-prep-cards.md`](mansplain/26-prep-cards.md).

- **Content (100% deterministic — no LLM, instant, zero extra RAM):** title + amber "in N min", **LAST
  TIME (N days ago)** recap (the matched meeting's TL;DR), **STILL OPEN** action items with amber owner
  tags (tickable — writes through to the store), **AGENDA** parsed from the calendar invite's description.
- **Matching is honest:** a `CalEvent` links to a prior meeting by **title/topic** (`TitleMatchIds` →
  `SearchRelevant` + a shared-title confidence bar) — never by attendee (notes don't store emails). No
  confident match → an honest **first-time card** ("First time with this one — I'll take notes if you
  record"), never an invented recap.
- **Trigger (`PrepWatcher`):** a 30 s calendar poll raises the card in a 5-min lead window
  (`TOKI_PREP_LEAD` override), **restrained** — real meetings only (link or other guests), **once per
  meeting-instance ever**, only while the notch is idle (Hidden/Collapsed), never during a fullscreen
  app/presentation, "Later" suppresses it, and it auto-retires if you walk away. Never records on its own
  — "Join and record" is the human click (Hard Constraint #3).
- New: `Calendar/PrepBuilder` (deterministic assembly + matching + agenda parse), `Calendar/PrepWatcher`
  (the trigger), `Views/PrepCardView`, `Dev/PrepSamples`; `CalEvent` gained `Description`;
  `PillState.PrepCard` (auto-height 356×306). Self-test `--preptest`; demo `--state PrepCard`.
- **Verified:** matched + first-time cards render-checked in light **and** dark (near-exact to design ④);
  `--preptest` proves title-match / open-item pull / unrelated-event rejection / agenda parse; live
  `--state PrepCard` runs without crashing (the ctor-grow recursion guard from the Home fix applied here too).

450/450 tests pass. Owner to confirm live: the auto-surface-vs-hover choice, and the real T-5 pop-up +
"Join and record" against a live meeting.

## M7.3 (Voice-create a meeting) — 2026-08-03

Say "schedule a call with Ana tomorrow at 3" on push-to-talk → Toki confirms, then creates it. Story in
[`mansplain/27-voice-create.md`](mansplain/27-voice-create.md); routing detail in ARCHITECTURE.

- **A new command-tier intent in the dialogue brain** (`Llm/CalendarCommand.cs`), slotted into
  `NotesQa.AskStreamAsync` **before** retrieval/general-chat (a create is never answered from the notes)
  and after the safety/identity tiers. Deterministic detection (no LLM): a tight scheduling-verb + event-
  noun / "put … on my calendar" / "remind me to" shape, with the **question-shape veto reused from
  `CommandRouter`** so "when is my call with Ana" / "what meetings do I have" stay READ turns.
- **Confirm before booking (the point):** turn 1 never creates — it reads back "Create 'Call with Ana' —
  tomorrow, 3:00–3:30 PM …?" and holds a single-shot `ConversationContext.PendingCreate`; "yes" creates
  once, "no" cancels, and a **stray "yes" later can never create** (pending is cleared up front). No time
  → asks "when?". Not connected → honest, books nothing. A mishearing costs one "no".
- **Honest about people:** a spoken "with Ana" is a name, not an email — it goes in the title (+ a
  description note), never a fabricated invite. Reuses the shared `QuickEventParser` + `CalendarService`.
- **Parser fix (helps the typed composer too):** a bare hour like "at 3" now resolves to the **business-
  hours reading (3 PM)**, not 3 AM; explicit "9am"/"8pm" are respected.
- **No UI changes** — the confirm/result flow rides the existing Ask streaming path on both the notch and
  the Library. Self-test `--voicecreatetest` (detection matrix + the confirm gate, no mic/model/network).

472/472 tests pass (+22: detection positives/negatives, title/time/person extraction, and the full
confirm-gate matrix incl. stray-yes and no-time). Owner to confirm live: hold-to-talk → speak → "yes" →
event on the real calendar.

## M9.0 (Premium redesign — pass 1 + glassy notch) — 2026-08-03

First pass of a taste-driven redesign (owner: the UI read "AI-slop"), researched via Mobbin + premium
references (Wispr Flow, Linear, Vercel, Cron…). Story in `mansplain/`, decisions in ARCHITECTURE.

- **Design tokens** (`Theme.xaml`): a restrained type ramp (was 18 ad-hoc sizes → named `Title/BodyStrong/
  Secondary/Caption/Micro`; section headers SemiBold not heavy Bold). Reused across the touched screens.
- **Redesigned checkbox** (`TokiCheck`): 18px, correct 12px tick (no more stretch-distortion), **green** fill
  when done (frees coral for record only), spring-pop on check. Plus **`TokiSwitch`** — a real toggle
  replacing the bare Win32 checkbox in Settings.
- **Notes**: colour-coded section headers (decisions green / actions blue / questions purple, per design ⑤)
  and left-aligned text (justify made rivers in a narrow column).
- **Calendar composer**: flattened (was box-in-box-in-box → a section under a hairline), clean outlined
  **chip toggles** (`ChipButton`) — the "Google Meet ✓" text-slop is now a green-filling toggle.
- **Home**: "Meeting" → **"Calendar"** (it opens the calendar, not starts a meeting); one-accent icons
  (coral mic on Take Notes, monochrome calendar).
- **Fixes**: typing in the create composer no longer auto-tucks the notch (idle-tuck now respects focused
  input); a bare "at 3" resolves to 3 PM (business-hours reading) in the shared parser.
- **Glassy notch toggle** (Settings → Appearance): a **real DWM acrylic blur-behind** that frosts whatever's
  behind the notch (not a tint), theme-aware (dark/white frost, re-tints on live theme swap), one consistent
  size (glass = opaque size, no growth), flush square top (window extends above the screen edge so DWM's
  top-corner rounding hides off-screen), rounded bottom, no rim. Off by default. See ARCHITECTURE for the
  full interop (why true blur can't coexist with a 22px corner, and every constraint solved).

472/472 tests pass. **Still to build (redesign pass 2):** the Wispr/Vercel hover-expand "section pills"
(rows growing on hover to reveal actions), the remaining type/spacing sweep (Library + small states), and
the motion-consistency pass.

## M9.1 (Calendar page revamp) — 2026-08-04

The Calendar page read too big and busy (three arrows, week paging) and flickered on refresh.

- **One continuous agenda** replaces week paging — the upcoming cache already holds ~31 days, so the
  two week arrows were artificial. Now a single scroll with **week bands** ("This week" / "Next week" /
  date range) separating it; only the **back** arrow remains.
- **Scroll-synced subtitle** (header updates to the week you're viewing + its count) and a **Today pill**
  that fades in only once you scroll away.
- **Smooth**: a render-key skips the rebuild when nothing changed (the 60s tick + multi-source refresh
  no longer re-stagger identical rows), and the entrance animation runs **once**. Fixed-height page with a
  real internal scroller (an earlier auto-height attempt fought the ScrollViewer → no scroll); smaller at
  360×430. Coral **now-line dot**, mascot empty state ("＋ Plan something") that reacts on hover.
- **Theme fix**: the render-key carries the theme and the page listens for theme swaps, so the code-built
  rows recolour on a light↔dark toggle (they were staying black-on-dark before).

## M8.0 (Attribution — the "Built with" screen) — 2026-08-04

The licence-obligation screen — Toki cannot be sold without crediting the open models it ships (Parakeet is
**CC-BY-4.0**). First real step onto the ship runway.

- **`AboutView`** (`PillState.About`), reached from **Settings → About → "Built with — open source &
  licences"**. Scrollable credits list, design-system native (type ramp, dividers, amber/neutral chips).
- **MODELS**: Parakeet TDT 0.6b v2 (NVIDIA · **CC-BY-4.0**, with an "int8 quantised" note to satisfy the
  modification clause), Qwen3-4B-Instruct-2507 (Alibaba · **Apache-2.0**), bge-small-en-v1.5 (BAAI · MIT).
- **ENGINES & LIBRARIES**: sherpa-onnx (Apache-2.0), ONNX Runtime, LLamaSharp · llama.cpp, NAudio, SQLite,
  H.NotifyIcon, SharpCompress, Recognizers-Text (all MIT). Each row opens its upstream source.
- Attribution-required licences (CC-BY, Apache) get **amber chips**; MIT stays neutral. **Whisper is
  deliberately omitted** — it's a stubbed error-fallback, not shipped, so crediting it would be false.
- Repo **`THIRD-PARTY-NOTICES.md`** holds the full, authoritative attributions + licence links.

472/472 tests pass. **Still to build (M8):** signed installer + auto-update, licence-key gate, onboarding
download polish.

## H1 (The hardening pass — audit, fix, verify) — 2026-08-04

With the feature set settled, the whole app went through a production-readiness pass: a deep
multi-agent audit of every subsystem (each finding adversarially re-verified against the code before
it counted), plus market research across the top consumer notetakers to calibrate quality. 67 real
issues confirmed; every high- and medium-severity one fixed, and all but three deliberate deferrals
of the lows.

**Built / fixed (highlights)**
- **A recording can never run invisibly.** Recording truth now lives in the audio session itself, and
  every way of dismissing the notch keeps a live recording's bar on screen. Push-to-talk or Esc during
  a meeting previously could leave the mic running with no indicator and lose the meeting.
- **Delete really deletes.** Removing a meeting from the Library now also removes its audio files and
  Markdown mirror — which the confirm dialog had promised all along.
- **Crash recovery.** WAVs stay playable through a crash (headers finalized every few seconds), and on
  the next launch Toki finds any recording that never became a meeting and turns it into one. Verified
  live: it recovered a real meeting from a 3-week-old orphaned folder on this PC.
- **Unplugging your headset mid-meeting no longer kills the recording** — capture rebuilds on the new
  default mic and keeps writing the same file, converting formats on the fly.
- **Detection fixes:** the denylist no longer matches install paths (a Windows username containing
  "obs" used to disable detection entirely), and new Teams now resolves to its real process, so its
  meetings capture both sides.
- **Stability:** the low-memory model-release race that could crash the app mid-answer; API streaming
  no longer dies at 60 seconds on slow local models; calendar requests can't silently truncate the
  agenda (real pagination) or vanish on timeouts; settings/chat files are written atomically (a crash
  mid-write used to wipe the API key and Google connection).
- **Security/privacy:** calendar "Join" links only open real web URLs; the OAuth listener shrugs off
  bogus local requests; the SharpCompress CVE was patched; the diagnostic log is size-capped; orphaned
  voice snippets are swept.
- **Quality:** meeting prompts wait until you leave fullscreen/presentation mode; the notes prompts now
  read past speech-recognition errors without inventing names and pair every decision with its stated
  reason; if the API key is missing, notes fall back to the on-device model instead of plain heuristics.

**Verified**
- 472/472 unit tests green. Self-tests all pass on this PC: `--dbtest`, `--notestest`, `--querytest`
  (10/10), `--themetest`, `--copytest`, `--caltest parse`, `--preptest`, `--voicecreatetest`,
  `--detecttest`, and `--llmtest` — real notes from the real Qwen model through the new fallback path.
- All 17 views rendered clean in both themes (`--render` / `--theme light`).
- `dist` republished (132 MB, healthy), smoke-launched, warm-up + state machine verified in the log —
  including watching the recovery sweep clean 4 dead folders and save 1 recovered meeting, live.

## H2 (Assistant scoping fix, Library sort, security hardening) — 2026-08-04

Owner-reported: asking "what tasks do I have for this week" listed every open task. Root-caused, fixed,
and hardened alongside a Library sort menu and a security/testing pass — all research-validated.

**Assistant answer quality**
- **Task time-queries now scope by DUE DATE, not meeting date.** "What's due this week / Friday /
  overdue" was scoping to *meetings held* in the window — which on a recent library is almost every
  meeting, so it dumped everything. Now it resolves each task's free-text due ("Friday", "next week")
  against its meeting's date and buckets open tasks into **Overdue / Due-this-window / undated-from-this-
  window**, with the window echoed in the answer ("2 tasks this week:"). This is the market-validated
  behavior (task managers scope by due date; the undated fallback is disclosed).
- "What's due Friday", "what's overdue", "anything due tomorrow" now route to the task list (they used
  to fall through to a generic lookup).
- Bare weekday deadlines are future-biased ("due Friday" set on Monday means *this* Friday, not last).
- 15 new tests (unit + end-to-end through the store seam) lock the behavior in.

**Library sort/filter** — a menu on the list: Newest / Oldest / Longest / Shortest / Most open tasks /
Manual (drag order). Persisted; drag-reorder auto-selects Manual so it never renumbers a sorted view.

**Security & testing** (from a full threat-model audit)
- **Single-instance guard hardened** against local named-object squatting: the Mutex/EventWaitHandle now
  use per-user (SID-salted) names with a current-user-only ACL, and "already exists" is verified against
  a real running Toki before deferring — a squatter can no longer DoS startup or drive the UI.
- **One shell choke point** (`SafeShell`): every "open link / reveal file" goes through it; only http/https
  URLs (and validated in-app paths) ever reach Windows — a poisoned calendar join-link can't launch
  `file:`/UNC/`ms-msdt:`.
- **One FTS query builder** (`ToFtsQuery`) so no search text can ever reach a MATCH clause un-sanitized.
- **Path-containment guard** on the Markdown mirror (a title can never escape the Notes folder).
- **Prompt-injection delimiters** around meeting notes fed to the model ("treat as data, not instructions").
- OAuth loopback socket set `ExclusiveAddressUse`; confirmed no TLS-validation bypass and no
  `BinaryFormatter` anywhere.
- **NuGet vulnerability auditing turned on — and it immediately earned its keep:** it surfaced a
  previously-invisible **high-severity advisory in the bundled native SQLite** (`SQLitePCLRaw` 2.1.6),
  now patched to 2.1.12 (verified: the DB round-trip still passes). A critical advisory in
  `NuGet.CommandLine` (a build tool pulled in by the date parser, never shipped in the app) is suppressed
  with a documented reason so real runtime advisories still surface.
- 33 new security/pagination tests.

**Verified** — 520/520 unit tests; self-tests all pass (`--dbtest`, `--notestest`, `--querytest` 13/13,
`--voicecreatetest`); single-instance guard runtime-verified (2 launches → 1 process); Library sort
render-verified; `dist` republished (132 MB) and smoke-launched.
