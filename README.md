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

## M4 — Transcription (next)

sherpa-onnx Parakeet whole-file per stream, streaming preview, whisper.cpp failure-only fallback, cross-stream echo de-dup, merge. `--asrtest`. (See ARCHITECTURE "Lessons from open-source notetakers" for the plan.)
