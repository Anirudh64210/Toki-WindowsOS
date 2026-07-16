# ARCHITECTURE.md — decisions and why

Running log of technical decisions. The stack itself was decided by the owner before this repo existed (see `WINDOWS-BUILD-PROMPT.md` §4) and is not re-litigated here; this file records how we apply it and every decision made along the way.

## Repo layout

```
Toki.sln
src/Toki/            WPF app (net8.0-windows) — the product
tests/Toki.Tests/    xUnit tests (net8.0-windows, UseWPF)
Toki-ProductDesign/  design reference PNGs (macOS notch design)
mansplain/           plain-language build story
```

**Why `src`/`tests` split:** keeps the app project clean of test-only dependencies and makes packaging (M8) simpler — the installer only ever looks at `src/Toki`.

**Decision (M0): test projects target `net8.0-windows` with `UseWPF`.** A plain `net8.0` test project cannot reference a WPF app; discovered immediately at scaffold time. Cost: tests only run on Windows. Acceptable — the whole product is Windows-only and audio/model paths need a real PC anyway.

## The overlay: one window that morphs

The macOS design hangs a panel from the notch, top-center, and the design's own rule is: *"The panel is one element… it only morphs its own width & height."* Windows has no notch, so the same element becomes a **pill at bottom-center that expands upward** (Wispr Flow position). Everything else transfers unchanged: one borderless window, animated width/height morphs, never a second overlay window.

- WPF window flags: `WindowStyle=None`, `AllowsTransparency=True`, `Topmost=True`, `ShowInTaskbar=False`.
- Anchoring: bottom edge fixed a small margin above the taskbar's work-area edge; the window grows upward and stays horizontally centered on the current monitor. Per-monitor DPI aware.
- The Library can also open as a normal resizable window (from Home / tray), because reading many meetings in a pill is cramped.

### State machine

```
hidden → meetingPrompt → recordingBar ⇄ recordingOpen → summarizing → notes | noSpeech
plus: home, library, settings
```

One `AppState` class owns the current state; views only read it. Ported exactly from the Mac app — it survived real usage there, and a single owner for state is what keeps a morphing window from tearing itself apart with competing animations.

### State sizes (logical px, from the design exports)

| State | Size | Notes |
|---|---|---|
| collapsed (pinned sliver) | 200×36 | character peeking |
| meetingPrompt | 300×74 | "Record this meeting?" — persistent, no auto-dismiss |
| recordingBar | 236×52 | timer + live waveform + stop |
| recordingOpen | 372×234 | live transcript + star/flag |
| summarizing | 340×164 | animated "Finding action items" |
| notes-ready pill | 244×52 | persistent — "save for later" |
| home (not connected) | 344×232 | Meeting / Take Notes / Connect Google |
| home (connected) | 344×320 | + "Coming up" calendar list |
| prep card | 356×306 | last notes + open items + agenda |
| notes | 424×438 | editable, scrolls |
| library / settings | 404×440 | also available as a resizable window |

### Visual language (from `Toki-ProductDesign/`)

Near-black surfaces, large corner radii (the pill is fully rounded), pink Toki character as the personality moment, coral/red record button, white pill-shaped primary buttons, amber accents for in-progress states. Mac uses SF Pro Rounded; Windows uses **Segoe UI Variable** (closest native equivalent, ships with Windows 11 — no font licensing). Exact colors will be sampled from the PNGs into a WPF resource dictionary during M1.

### How the morph is implemented (M1)

The window itself never resizes. It is a fixed 500×560 fully-transparent host pinned above the taskbar; only the inner `Shell` border animates width/height (240ms cubic ease-out) while content cross-fades. Transparent host pixels are click-through, so the invisible area doesn't block the desktop. This avoids WPF's janky live window-resize with `AllowsTransparency` and keeps positioning trivial.

**Decision (M1): target `net8.0-windows10.0.19041.0`, not bare `net8.0-windows`.** The tray package only ships modern assets for the 19041 TFM, and process-loopback audio (M2) requires build 19041 as a hard floor anyway.

**Decision (M1): every state change logs to `%TEMP%\toki-m1.log`** (startup args, old → new state, show position). Mirrors the brief's detection rule — "it didn't work" is only diagnosable if a log shows what actually happened. This grows into the app's proper log file under `%APPDATA%\Toki\` in later milestones.

**Verification gotcha (M1):** transparent WPF windows are *layered windows* — plain GDI screen captures (`CopyFromScreen`) do not see them. Automated screenshots of the pill silently show everything except the pill. Verify the overlay via the state log plus a human eye, or a DWM-aware capture.

## Data location

Everything under `%APPDATA%\Toki\`: `toki.db` (SQLite), `Notes\` (Markdown mirror), model files, logs. Local-only by hard constraint #2.

## Audio capture (M2)

Two independent WASAPI captures, each to its own WAV with meeting-relative timing:

- **"You"** — `MicCapture`, NAudio `WasapiCapture` on the default communications mic, shared mode. Device format (typically 32-bit float, 48kHz stereo here).
- **"Them"** — `ProcessLoopbackCapture`, raw COM interop around `ActivateAudioInterfaceAsync` with the `VAD\Process_Loopback` virtual device and `AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, INCLUDE_TARGET_PROCESS_TREE }`. Event-driven (`AUDCLNT_STREAMFLAGS_LOOPBACK | EVENTCALLBACK`), 44.1kHz 16-bit stereo PCM. NAudio has no per-process loopback, so this interface stack (`IActivateAudioInterfaceAsyncOperation`, `IActivateAudioInterfaceCompletionHandler`, `IAudioClient`, `IAudioCaptureClient`) is defined by hand, mirroring Microsoft's ApplicationLoopback sample. The COM work runs on a dedicated MTA thread.

**Why include the process *tree*:** browser meetings render audio in a child renderer process, not the main browser PID. Including the tree is what lets a single target PID capture Google Meet — the thing that took the Mac app weeks, solved natively by Windows.

**Verification technique that de-risked it without a live call:** `--playtone` makes one Toki process emit a 440Hz tone; another taps that PID and must read a peak matching the tone's gain. Tapping a *different, silent* process at the same time must read ~0 — proving per-process selectivity, not a system-wide tap.

Still open for later milestones: rebuild capture on process-tree changes and on default-device changes (`IMMNotificationClient`), and the cross-stream echo de-dup (M4).

## Meeting detection (M3)

The mic is the signal: a call is essentially the only everyday reason another app opens the microphone.

- `MicUsageReader` reads `...\CapabilityAccessManager\ConsentStore\microphone` under **both HKCU and HKLM** (most meeting apps are HKCU/NonPackaged; a few are per-machine), treating `LastUsedTimeStop == 0` as "in use now". No permission prompt; it also yields the app's identity.
- `MeetingDetector` is a **denylist, not allowlist** — any app can host a call, so every mic user is a candidate except known non-meetings. Debounce 2 polls, 10-min cooldown per dismissed app, prompt once per continuous mic session, auto-stop when the recorded app releases the mic. The poll decision is a pure method (`PollOnce`) with injectable clock + reader, so it's fully unit-tested.
- `ProcessResolver`/`ProcessTree` pick the **tree root** to tap, because a browser's meeting audio lives in a renderer child that `INCLUDE_TARGET_PROCESS_TREE` only reaches from the root.

**Decision (M3): detection prompts, never auto-records.** Confirmed by the research below — every mature local tool ships manual/confirmed start; silent auto-join is the risky, opt-in feature. For a privacy product, "we noticed a call — record it?" is the trustworthy default.

## Overlay dismissal & focus (M3 fix)

Click-away/Esc dismissal uses **global low-level mouse + keyboard hooks** (`InputMonitor`), not WPF window activation. Activation-based closing fired on unrelated focus changes and flickered the pill shut on launch. The hooks let Toki stay a true no-focus overlay (fits ghost mode) and only dismiss on a real click *outside* the shell rect (with a 500ms grace period after opening). Escape is handled the same way so it works without the window holding keyboard focus.

## Window architecture (revised M3.2) — non-layered, hardware-accelerated

**Decision:** the pill is a **non-layered, opaque, hardware-accelerated window** whose corners are rounded by DWM (`DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND`, plus a subtle `DWMWA_BORDER_COLOR`). The window *is* the pill — it resizes to each state's content and anchors bottom-center.

**Why not the original `AllowsTransparency=True` layered window:** that path (a) forced slow software rendering, and (b) is **fundamentally incompatible with `SetWindowDisplayAffinity`** (ghost mode) — the layered + capture-exclusion combination renders the window as a black rectangle (confirmed by Microsoft's own docs/issue tracker; the call returns `ERROR_NOT_ENOUGH_MEMORY` on layered windows). The owner hit exactly this black-rectangle glitch during screen recording. A non-layered window is GPU-composited (fast) and lets ghost mode work reliably.

**Rounded corners (M3.3):** DWM corner rounding (`DWMWA_WINDOW_CORNER_PREFERENCE`) does **not** apply to borderless `WindowStyle=None` windows — corners stayed square. The pill is instead clipped to a rounded region via `SetWindowRgn(CreateRoundRectRgn(...))` at radius 22 (DPI-scaled), re-applied on every resize and work-area change. This restores the larger pill radius the design wants. Region edges are 1-bit (mildly aliased) but fine at this radius/DPI; a `Border` inside draws the matching AA fill + subtle edge.

**Content sizing (M3.3):** the size-to-content window hosts the current view in a `Grid` (not a `ContentControl`) so the view is stretched to the exact window bounds — otherwise views render at their natural size and clip (this caused the owner's "cut off" report). Window height is clamped to the work area; taller panels scroll internally.

**Trade-off vs the old layered window:** no soft outer drop-shadow and no per-window opacity fade. Acceptable for the stability/ghost/perf win.

**Positioning (M3.4): physical pixels, not WPF logical.** The window is placed with Win32 `SetWindowPos` using the physical work area (`SystemParametersInfo(SPI_GETWORKAREA)`) and physical window size (`GetWindowRect`), because WPF's logical `Left`/`Top` mis-scale on a per-monitor-DPI-aware app (at 125% the pill floated mid-screen / tall panels ran off the bottom). This is fully resolution/DPI-adaptive. Window height is clamped to the physical work area so panels never exceed the screen (they scroll). No window-`Top` slide animation — it conflicted with `SetWindowPos`; the entrance is the content pop+fade.

**DPI caution for verification:** the dev machine runs at 125% scaling. Screen/window captures MUST be DPI-aware (`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`) or they grab only the top-left fraction of a scaled window and fake "cut off" content. The in-app layout log (`view == host` size) and the physical position log are the reliable checks.

## Ghost mode

`SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` — reliable now that the window is non-layered — makes the overlay visible to the user but omitted from DWM captures (screen recording and screen-share never show it). Plus `WS_EX_TOOLWINDOW` to stay out of alt-tab. Verified: a `PrintWindow` grab returns blank while the app is on screen. Dev-only `--noghost` disables it so capture tooling can verify the UI.

## Performance discipline (M3.2)

- **The mouse hook runs on its own thread** (`InputMonitor`), never the UI thread — a low-level hook is serviced on its installing thread, so putting it on the UI thread meant any render/GC stall there lagged all mouse input system-wide. Its callback does pure arithmetic against a cached window rect (via `GetWindowRect`, physical pixels) and marshals dismissal to the UI thread.
- **Animations pause when off-screen.** `TokiBlob`, `Waveform`, and the loader stop their loops on `IsVisibleChanged`/`Unloaded`; cached-but-hidden views were previously animating forever. The loader also stops non-active stage motifs. Hidden-state CPU ≈ 0.

## Never-hang discipline

A meeting assistant that freezes is worse than useless. Rules now enforced:

- **The UI thread never blocks.** Audio capture start and stop both run on background threads (`Task.Run`); the pill transitions immediately. The original hang was `StopRecording` joining the loopback worker (up to 3s) on the UI thread — which also stalled the low-level mouse hook, freezing all input.
- **The mouse hook does zero UI work.** A low-level hook runs on the UI thread's message pump, so any slow work there lags the whole system. The hook only compares the click point to a *cached* rect updated on layout — pure arithmetic. The global keyboard hook was removed entirely (invasive + another stall vector).
- **Single instance, but relaunch surfaces it.** A named `EventWaitHandle`: a second launch signals the running instance to show Home, then exits. Plus a tray "Restart Toki". So the app is always one process, always reachable, never a zombie that blocks new launches.

## Loading animation (M3.1)

The summarizing/loading view is a staged character scene, not a static spinner: Toki (breathing, blinking, bobbing, soft glow) beside a motif that crossfades through the stages of understanding a meeting — rings (listening) → document with sweeping highlight (reading) → checkboxes ticking (organizing) → pen drawing an underline (writing) — over a filling progress bar. Stages are placeholder-paced now; M6 will drive them off the real transcription/notes passes. Verified live via `--noghost` multi-frame capture.

## Distribution (test build)

`dist\Toki.exe` is a **self-contained single-file** publish (bundles the .NET runtime) so it double-clicks and runs on any Win10 2004+ PC with no "unable to run" prompt. Shortcuts on the Desktop and in the project folder. Real install (signed Velopack installer + launch-at-login, Wispr-Flow style) is M8. A single-instance mutex prevents duplicate pills/hotkey fights; global exception handlers keep the app alive through any UI/background fault.

## Lessons from open-source notetakers (research, informing M4/M6)

Surveyed Meetily, Hyprnote, Vibe, Buzz, Whishper, Amurex, and others (MIT ones are safe to learn from; **Whishper/Amurex are AGPL — do not copy code**). Key takeaways adopted into the plan:

- **Validated:** the ConsentStore mic-in-use approach and Parakeet-via-ONNX are both mainstream, correct choices (Meetily and Vibe both moved to Parakeet ONNX).
- **Windows advantage:** WASAPI per-process loopback avoids the macOS virtual-audio-device dance (BlackHole/aggregate devices) those tools need — we get clean per-app streams for free. Keep mic and each loopback as separate tracks = near-free "You vs Others" attribution.
- **Echo:** everyone hits it (Hyprnote built a custom ML AEC; Buzz separates speech pre-ASR). Since we have *isolated* streams, prefer **transcript-level de-dup** over signal AEC — but nudge "headphones recommended" for the speakers-bleed case.
- **M6 notes (biggest lesson):** small/fine-tuned beats big-and-general for meeting notes (Hyprnote's HyprLLM = fine-tuned Qwen3-1.7B). **Chunk the transcript** — map per-chunk (notes/decisions/actions) → reduce/merge → TL;DR over the *reduced* output, never the raw transcript. This is the #1 recurring pain (Meetily calls it unsolved). Our three-pass/TL;DR-last shape is right; make pass 1 a map over chunks.
- **Data model:** store transcript, intermediate, and final notes **separately** (Hyprnote keeps raw/enhanced/transcript as three types) so notes can be re-generated without re-transcribing. Fold into the M5 SQLite schema.
- **Don't over-promise diarization:** it's the most common gap/complaint. Ship honest "You vs Others" from the separate tracks; add a manual speaker-relabel UI rather than faking per-remote-speaker diarization.
- **Show liveness while recording** (meter/duration/partial text) — silence-until-done reads as broken.
- **Perf:** Windows on-device is the harder target (Hyprnote went Mac-first for this reason); ship a GPU path (Vulkan/DirectML) and validate CPU fallback timing on mid hardware.

## Transcription (M4)

On-device ASR pipeline: `you.wav`/`them.wav` → **16kHz mono float32** (`AudioLoader`, NAudio downmix + WDL resample) → **Parakeet TDT 0.6b v2** via sherpa-onnx (`Transcriber`, `OfflineRecognizer` with `ModelType="nemo_transducer"`, greedy) → timestamped `TranscriptSegment`s → `TranscriptMerger` (one timeline + echo de-dup) → `TranscriptionService` orchestrates and hands the UI a merged transcript.

- **Model**: `ModelManager` downloads the ~630MB int8 bundle to `%APPDATA%\Toki\Models\` (SharpCompress extracts the tar.bz2). Model files live outside the exe; only the sherpa native DLLs (~20MB: `sherpa-onnx-c-api.dll`, `onnxruntime.dll`) ship in the single-file exe (`IncludeNativeLibrariesForSelfExtract`). Verified transcription works from the *packaged* exe.
- **Recognizer lifecycle**: heavy (~630MB) — created once (`TranscriptionService`, lazy + `Warm()` at launch off the UI thread), reused; a fresh one-shot `OfflineStream` per file, so no state leaks between meetings (lesson 5).
- **Silence vs error**: an empty Parakeet result = nobody spoke → return no segments (silence is not failure, lesson 4/7). A thrown error is where the whisper.cpp fallback belongs (currently logged + skipped — a follow-up).
- **Echo de-dup** (`TranscriptMerger`): the mic hears the far end through the speakers, so a remote line can land on both streams. We keep the clean loopback ("them") copy and drop the "you" duplicate within ±4s; short interjections (<3 significant words) require an exact normalized match so real "yeah"/"okay" survive (lesson 6). Transcript-level (not signal-level AEC), matching the open-source-notetaker research.
- **Threading**: on Stop, capture-finalize + transcription run entirely off the UI thread; the loader animation shows meanwhile; the pill never blocks. The async model download uses `ConfigureAwait(false)` (a `.GetResult()` on the UI thread otherwise deadlocks).
- **Async deadlock gotcha**: blocking on an async download from the UI thread (`.GetAwaiter().GetResult()`) deadlocks unless every await is `ConfigureAwait(false)` — hit and fixed during M4.

Deferred: live streaming preview (streaming zipformer `OnlineRecognizer`) and the whisper.cpp error-only fallback (`Whisper.net` + `ggml-small.en`).

## Verification model

Two layers, both required:

1. **Unit tests** (`dotnet test`) for pure logic: state transitions, denylist filtering, echo de-dup, title validation, heuristic extraction.
2. **Self-test CLI flags** (`--mictest`, `--loopbacktest`, `--asrtest`, …) producing real artifacts (a WAV you can play, a transcript you can read) run on a real PC. CI cannot cover audio/GPU/model paths — this is permanent, not a temporary gap.

A milestone is done only when its real artifact has been seen/heard (brief, lessons 12–13).

## Deferred

- **CI pipeline** — needs a GitHub remote; added when the repo is pushed (M0 note in README).
- **Outlook calendar, teams features, "Ask Toki" chat** — explicitly out of v1 scope.

## Storage & notes (M5)

Local SQLite (`MeetingStore`, `%APPDATA%\Toki\toki.db`): `meetings` (with `kind` = meeting|manual), `transcript`, `decisions`, `questions`, `action_items` (all child rows cascade-delete). Manual notes reuse the `meetings` table (freeform `body`), so they're searchable alongside meetings — the brief's new manual-note feature with a `kind` column. `you:`/`them:` search scopes to transcript lines by speaker.

`NoteExtractor` is the deterministic **notes floor** (brief §5): cue-based classification of sentences into decisions / actions (with owner + due heuristics) / open questions, a bounded TL;DR, and a validated heuristic title (lesson 9: no separate title LLM pass; `<app> — <date>` fallback). Always available, instant, no model — this is what a missing/failed LLM degrades to. M6 layers LLM-written notes on top of the same `NoteSet` shape.

`MarkdownMirror` writes each meeting to `%APPDATA%\Toki\Notes\*.md` — the DB is source of truth; the Markdown is a user-inspectable, sync-friendly mirror/export (the open-source-notetaker research's "every meeting is a file you own").

Verification note: data-driven views populate on `Loaded`, which does NOT fire in the offscreen `--render` harness — verify them with live DPI-aware `PrintWindow` captures (`--state library/notes/manualnote --noghost`) instead.

## LLM notes (M6)

> **Superseded in parts by "Refinements (M6.1)" below** — the notes are now a single structured pass (not three), GPU offload is VRAM-auto (not CPU-by-default), and the model is warmed at launch. The M6 record is kept for history.

On-device notes with **Qwen3-4B-Instruct-2507 Q4_K_M** via **LLamaSharp** (llama.cpp), behind an `ILlm` interface so the source is swappable (local model / BYO OpenAI-compatible key).

- **Three passes, TL;DR last** (`LlmNoteWriter`): decisions → action items (owner/due) → TL;DR written from the *reduced* sections, not the raw transcript. Questions stay heuristic (reliable). Every model output goes through `TextValidator` (rejects CJK/kana/Hangul drift and 6+ char repetition — lesson 8), retried once, then falls back to `NoteExtractor` per section. So the user never sees degenerate text and always gets notes.
- **Sampler**: Qwen's official params via `DefaultSamplingPipeline` (temp 0.7 / topP 0.8 / topK 20 / presence 0.5). Never greedy.
- **CPU by default** (`GpuLayers = 0`), context 4096. This is deliberate: on a 4GB GPU, offloading all layers loads fine but then OOMs allocating the compute buffers, and llama.cpp crashes **natively** (an `AccessViolation` .NET can't catch after the fact). CPU can't OOM VRAM, so it can't crash. GPU offload is an opt-in setting for 6GB+ cards. `LLamaWeights` is loaded once (`LlmService`, lazy — not warmed at launch, to respect RAM); a fresh `StatelessExecutor` context per generation.
- **Async deadlock**: LLamaSharp's `InferAsync` continuations must not be forced back onto a blocked UI thread — inference runs on a background thread (the recording flow already does; the self-test wraps in `Task.Run`). Symptom was a hang with flat CPU and the model paged out.
- **UX**: heuristic notes render immediately after a recording; the LLM upgrade replaces them in place (`ReplaceNotes` + `NotesUpdated` event) ~a minute later on CPU. The DB/Markdown are updated regardless of whether the Notes screen is still open.
- **Settings**: `AppSettings` (%APPDATA%\Toki\settings.json) — notes mode (Local / HeuristicOnly / ApiKey), GPU layers, and the BYO API key **DPAPI-encrypted** (per-user, never plaintext — brief §4). `NativeLibraryConfig` is set once at startup (Vulkan preferred, CPU auto-fallback) before any LLamaSharp call.

Deferred: chunked map-reduce for very long transcripts (current single-context handles typical meetings within 4096 tokens); the "Built with" attribution screen (M8).

## Refinements (M6.1) — notes shape, speed, and polish

Driven by owner testing on real meetings. Three fronts: what the notes look like, how fast they're produced, and how the whole thing feels.

### Notes shape: Granola-style, one pass

**Decision: Summary → Key points → Action items** (dropped "Decisions" and "Open Questions"). Decisions just restated the to-dos right below them (the owner's exact complaint); open questions weren't wanted. Key points now capture *topics/context/decisions*, distinct from the action list. The `NoteSet.Decisions` field + `decisions` DB table are **repurposed** to store key points — no schema migration, older meetings still load.

**Decision: one structured generation, not three passes.** `LlmNoteWriter` asks for KEY POINTS + ACTION ITEMS + SUMMARY in a single call, with SUMMARY **last** so the model writes it from the details it just produced (preserves the "TL;DR last" property of lesson 8 within one generation). A tiny second pass writes the title from the summary. Each section still validates (`TextValidator`) and falls back to the heuristic on garbage. Halves the generations vs M6, and the output-token budget is trimmed (concise key points). Placeholder junk in owner/due (`->`, `-`, `tbd`) is stripped in the prompt, parser, **and** renderer (so pre-existing rows are clean).

### GPU offload: VRAM-auto, made coherent (`LlmRuntime`)

**Decision: decide GPU-vs-CPU once, up front, from real VRAM — and only load Vulkan if we'll actually offload.** The M6 "CPU by default" was safe but slow, and worse: the Vulkan backend was still loaded at `GpuLayers=0`, which is the **pathological path** — llama.cpp shuffles every layer CPU↔GPU (hundreds of graph splits). Measured on the owner's RTX 3050: that hybrid was **~65s**; clean CPU (no Vulkan) is **~46s**; a safe partial GPU offload is **~34s cold / ~13s warm**.

`LlmRuntime.Resolve()` runs before any LLamaSharp call. It reads true dedicated VRAM from the driver registry (`HKLM\...\Control\Class\{4d36e968-...}\NNNN\HardwareInformation.qwMemorySize`, max across adapters — no COM interop) and picks: ≥7 GB → full offload / ctx 4096; ≥3.9 GB → **20 layers / ctx 3072**; else CPU. `App` then loads the Vulkan backend **only** when `GpuLayers > 0`. Offload is deliberately conservative (20 of 36 layers ≈ 2.3 GB of 4 GB) to keep headroom — a live video call also holds VRAM, and a GPU OOM at model load is an **uncatchable native crash**. Tuning knobs: `AppSettings.GpuLayers` (0 = auto, -1 = force CPU, >0 = force N; Settings toggle maps to auto/CPU), env `TOKI_GPU_LAYERS` / `TOKI_CTX`.

**Both engines warmed at launch** (`LlmService.Instance.Warm()` alongside the ASR warm), so a meeting-stop pays no model-load time — the warm number (~13s) is what the user actually sees.

### No "bad first draft" flash

**Decision: stay on the summarizing animation until the good notes exist, then reveal once.** M6 showed the heuristic notes immediately (raw transcript lines — ugly) and swapped in the LLM version ~10s later; the owner found the flash jarring. Now `StopRecording` writes the AI notes on the background thread *before* switching to the Notes screen (a 150s timeout guards a stuck generation; heuristic is still saved first as the DB floor and shown if there's no model). The user waits once, on the animation, and sees the finished notes.

### Transcription speed: measured, left sequential on purpose

**Finding: transcription — not the AI notes — is the bottleneck, and it's a hard floor here.** Parakeet runs at **~4× real-time on CPU**, and the two tracks are transcribed **sequentially**, so a 1-min two-party meeting ≈ 30s of ASR. Investigated every lever:
- **Parallelising the two streams is unsafe** — two `Transcriber` instances decoding at once gave **non-deterministic, corrupt** output (same audio → 13 segments one run, 270 the next; sequential is stable). sherpa-onnx's offline recognizer has shared global state. Reverted; correctness wins.
- **Thread count barely matters** (flat past ~6 threads; oversubscription hurts).
- **GPU ASR isn't available** — the shipped sherpa-onnx native is built CPU-only (`Provider="cuda"/"directml"` both fall back: "compile with -DSHERPA_ONNX_ENABLE_GPU=ON").

So total summarize time is ~35s on a 1-min meeting, dominated by ASR. The genuine path to <30s is **live transcription during the meeting** (task #14, parked) — then only the ~13s of notes remain at stop. A GPU speech engine (e.g. whisper.cpp on the Vulkan backend we already use) is the alternative, but a heavier dependency.

### UI polish

- **Library window** (`LibraryWindow`) as a resizable master–detail view — the maximise target and the Home/tray Library button — with a **"‹ Back to Toki"** button that returns to the pill. Manual notes are editable here with an explicit **Save** (`UpdateTitleAndBody`).
- **Shared `NoteRenderer`** draws the notes for both the pill Notes view and the window, so spacing/sections stay consistent (scales with a `spacious` flag).
- **Custom slim rounded scrollbars** and **custom rounded checkboxes** (coral fill + tick) as app-wide styles in `Theme.xaml`; soft opacity cross-fade when switching notes. **Copy transcript** + **maximise** buttons on the pill; the dead "search this transcript" placeholder was removed.

**WPF gotcha (cost time):** setting the `Template` property inside a `ControlTemplate.Trigger` throws *"'Template' cannot be set in the current element's Template"* at layout, which the global handler swallowed and the window rendered **black**. Hit it building the custom scrollbar; fix is to handle orientation in `Style.Triggers` (Width/Height) with a `Track Orientation="{TemplateBinding Orientation}"`, never swapping the template in a trigger.
