# ARCHITECTURE.md - decisions and why

Running log of technical decisions. The stack itself was decided by the owner before this repo existed (see `WINDOWS-BUILD-PROMPT.md` §4) and is not re-litigated here; this file records how we apply it and every decision made along the way.

## Repo layout

```
Toki.sln
src/Toki/            WPF app (net8.0-windows) - the product
tests/Toki.Tests/    xUnit tests (net8.0-windows, UseWPF)
Toki-ProductDesign/  design reference PNGs (macOS notch design)
mansplain/           plain-language build story
```

**Why `src`/`tests` split:** keeps the app project clean of test-only dependencies and makes packaging (M8) simpler - the installer only ever looks at `src/Toki`.

**Decision (M0): test projects target `net8.0-windows` with `UseWPF`.** A plain `net8.0` test project cannot reference a WPF app; discovered immediately at scaffold time. Cost: tests only run on Windows. Acceptable - the whole product is Windows-only and audio/model paths need a real PC anyway.

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

One `AppState` class owns the current state; views only read it. Ported exactly from the Mac app - it survived real usage there, and a single owner for state is what keeps a morphing window from tearing itself apart with competing animations.

### State sizes (logical px, from the design exports)

| State | Size | Notes |
|---|---|---|
| collapsed (pinned sliver) | 200×36 | character peeking |
| meetingPrompt | 300×74 | "Record this meeting?" - persistent, no auto-dismiss |
| recordingBar | 236×52 | timer + live waveform + stop |
| recordingOpen | 372×234 | live transcript + star/flag |
| summarizing | 340×164 | animated "Finding action items" |
| notes-ready pill | 244×52 | persistent - "save for later" |
| home (not connected) | 344×232 | Meeting / Take Notes / Connect Google |
| home (connected) | 344×320 | + "Coming up" calendar list |
| prep card | 356×306 | last notes + open items + agenda |
| notes | 424×438 | editable, scrolls |
| library / settings | 404×440 | also available as a resizable window |

### Visual language (from `Toki-ProductDesign/`)

Near-black surfaces, large corner radii (the pill is fully rounded), pink Toki character as the personality moment, coral/red record button, white pill-shaped primary buttons, amber accents for in-progress states. Mac uses SF Pro Rounded; Windows uses **Segoe UI Variable** (closest native equivalent, ships with Windows 11 - no font licensing). Exact colors will be sampled from the PNGs into a WPF resource dictionary during M1.

### How the morph is implemented (M1)

The window itself never resizes. It is a fixed 500×560 fully-transparent host pinned above the taskbar; only the inner `Shell` border animates width/height (240ms cubic ease-out) while content cross-fades. Transparent host pixels are click-through, so the invisible area doesn't block the desktop. This avoids WPF's janky live window-resize with `AllowsTransparency` and keeps positioning trivial.

**Decision (M1): target `net8.0-windows10.0.19041.0`, not bare `net8.0-windows`.** The tray package only ships modern assets for the 19041 TFM, and process-loopback audio (M2) requires build 19041 as a hard floor anyway.

**Decision (M1): every state change logs to `%TEMP%\toki-m1.log`** (startup args, old → new state, show position). Mirrors the brief's detection rule - "it didn't work" is only diagnosable if a log shows what actually happened. This grows into the app's proper log file under `%APPDATA%\Toki\` in later milestones.

**Verification gotcha (M1):** transparent WPF windows are *layered windows* - plain GDI screen captures (`CopyFromScreen`) do not see them. Automated screenshots of the pill silently show everything except the pill. Verify the overlay via the state log plus a human eye, or a DWM-aware capture.

## Data location

Everything under `%APPDATA%\Toki\`: `toki.db` (SQLite), `Notes\` (Markdown mirror), model files, logs. Local-only by hard constraint #2.

## Audio capture (M2)

Two independent WASAPI captures, each to its own WAV with meeting-relative timing:

- **"You"** - `MicCapture`, NAudio `WasapiCapture` on the default communications mic, shared mode. Device format (typically 32-bit float, 48kHz stereo here).
- **"Them"** - `ProcessLoopbackCapture`, raw COM interop around `ActivateAudioInterfaceAsync` with the `VAD\Process_Loopback` virtual device and `AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, INCLUDE_TARGET_PROCESS_TREE }`. Event-driven (`AUDCLNT_STREAMFLAGS_LOOPBACK | EVENTCALLBACK`), 44.1kHz 16-bit stereo PCM. NAudio has no per-process loopback, so this interface stack (`IActivateAudioInterfaceAsyncOperation`, `IActivateAudioInterfaceCompletionHandler`, `IAudioClient`, `IAudioCaptureClient`) is defined by hand, mirroring Microsoft's ApplicationLoopback sample. The COM work runs on a dedicated MTA thread.

**Why include the process *tree*:** browser meetings render audio in a child renderer process, not the main browser PID. Including the tree is what lets a single target PID capture Google Meet - the thing that took the Mac app weeks, solved natively by Windows.

**Verification technique that de-risked it without a live call:** `--playtone` makes one Toki process emit a 440Hz tone; another taps that PID and must read a peak matching the tone's gain. Tapping a *different, silent* process at the same time must read ~0 - proving per-process selectivity, not a system-wide tap.

Still open for later milestones: rebuild capture on process-tree changes and on default-device changes (`IMMNotificationClient`), and the cross-stream echo de-dup (M4).

## Meeting detection (M3)

The mic is the signal: a call is essentially the only everyday reason another app opens the microphone.

- `MicUsageReader` reads `...\CapabilityAccessManager\ConsentStore\microphone` under **both HKCU and HKLM** (most meeting apps are HKCU/NonPackaged; a few are per-machine), treating `LastUsedTimeStop == 0` as "in use now". No permission prompt; it also yields the app's identity.
- `MeetingDetector` is a **denylist, not allowlist** - any app can host a call, so every mic user is a candidate except known non-meetings. Debounce 2 polls, 10-min cooldown per dismissed app, prompt once per continuous mic session, auto-stop when the recorded app releases the mic. The poll decision is a pure method (`PollOnce`) with injectable clock + reader, so it's fully unit-tested.
- `ProcessResolver`/`ProcessTree` pick the **tree root** to tap, because a browser's meeting audio lives in a renderer child that `INCLUDE_TARGET_PROCESS_TREE` only reaches from the root.

**Decision (M3): detection prompts, never auto-records.** Confirmed by the research below - every mature local tool ships manual/confirmed start; silent auto-join is the risky, opt-in feature. For a privacy product, "we noticed a call - record it?" is the trustworthy default.

**Decision: a *manual* start still captures the other side.** Starting a recording from Home/hotkey used to be mic-only (`BeginRecording(null)`), so a user who muted themselves and hit record saved nothing but silence - a real lost meeting. Now `BeginRecording` resolves a target when none is given (`ResolveMeetingTarget` → the non-denylisted app currently holding the mic, via `MicUsageReader`) and taps its process tree too. Auto-stop (target releases the mic) stays armed **only** for prompt-accepted meetings - a manual start is stopped by the user, because some apps drop the mic on mute and that must not end a manual recording. A sub-3s recording with <5 words is treated as an accidental tap and discarded (no hollow Library entry).

## Overlay dismissal & focus (M3 fix)

Click-away/Esc dismissal uses **global low-level mouse + keyboard hooks** (`InputMonitor`), not WPF window activation. Activation-based closing fired on unrelated focus changes and flickered the pill shut on launch. The hooks let Toki stay a true no-focus overlay (fits ghost mode) and only dismiss on a real click *outside* the shell rect (with a 500ms grace period after opening). Escape is handled the same way so it works without the window holding keyboard focus.

## Window architecture (revised M3.2) - non-layered, hardware-accelerated

**Decision:** the pill is a **non-layered, opaque, hardware-accelerated window** whose corners are rounded by DWM (`DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND`, plus a subtle `DWMWA_BORDER_COLOR`). The window *is* the pill - it resizes to each state's content and anchors bottom-center.

> **Superseded (2026-07-17):** the "non-layered/opaque is required for ghost mode" claim below was a **misdiagnosis**. The pill is now `AllowsTransparency=True` (a genuinely translucent glass window) AND ghost mode works - the old black rectangle was the pre-2004 `WDA_MONITOR` fallback / a hide→show affinity revert, not a transparency conflict. See "The pill IS a translucent glass window" further down.

**Original reasoning (kept for history):** that path (a) forced slow software rendering, and (b) was thought **incompatible with `SetWindowDisplayAffinity`** (ghost mode) - the layered + capture-exclusion combination appeared to render a black rectangle. This turned out to be fixable (use `WDA_EXCLUDEFROMCAPTURE` 0x11, not `WDA_MONITOR`, and re-apply on show).

**Rounded corners (M3.3):** DWM corner rounding (`DWMWA_WINDOW_CORNER_PREFERENCE`) does **not** apply to borderless `WindowStyle=None` windows - corners stayed square. The pill is instead clipped to a rounded region via `SetWindowRgn(CreateRoundRectRgn(...))` at radius 22 (DPI-scaled), re-applied on every resize and work-area change. This restores the larger pill radius the design wants. Region edges are 1-bit (mildly aliased) but fine at this radius/DPI; a `Border` inside draws the matching AA fill + subtle edge.

**Content sizing (M3.3):** the size-to-content window hosts the current view in a `Grid` (not a `ContentControl`) so the view is stretched to the exact window bounds - otherwise views render at their natural size and clip (this caused the owner's "cut off" report). Window height is clamped to the work area; taller panels scroll internally.

**Trade-off vs the old layered window:** no soft outer drop-shadow and no per-window opacity fade. Acceptable for the stability/ghost/perf win.

**Positioning (M3.4): physical pixels, not WPF logical.** The window is placed with Win32 `SetWindowPos` using the physical work area (`SystemParametersInfo(SPI_GETWORKAREA)`) and physical window size (`GetWindowRect`), because WPF's logical `Left`/`Top` mis-scale on a per-monitor-DPI-aware app (at 125% the pill floated mid-screen / tall panels ran off the bottom). This is fully resolution/DPI-adaptive. Window height is clamped to the physical work area so panels never exceed the screen (they scroll). No window-`Top` slide animation - it conflicted with `SetWindowPos`; the entrance is the content pop+fade.

**DPI caution for verification:** the dev machine runs at 125% scaling. Screen/window captures MUST be DPI-aware (`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`) or they grab only the top-left fraction of a scaled window and fake "cut off" content. The in-app layout log (`view == host` size) and the physical position log are the reliable checks.

## Ghost mode

`SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` - reliable now that the window is non-layered - makes the overlay visible to the user but omitted from DWM captures (screen recording and screen-share never show it). Plus `WS_EX_TOOLWINDOW` to stay out of alt-tab. Verified: a `PrintWindow` grab returns blank while the app is on screen. Dev-only `--noghost` disables it so capture tooling can verify the UI.

## Performance discipline (M3.2)

- **The mouse hook runs on its own thread** (`InputMonitor`), never the UI thread - a low-level hook is serviced on its installing thread, so putting it on the UI thread meant any render/GC stall there lagged all mouse input system-wide. Its callback does pure arithmetic against a cached window rect (via `GetWindowRect`, physical pixels) and marshals dismissal to the UI thread.
- **Animations pause when off-screen.** `TokiBlob`, `Waveform`, and the loader stop their loops on `IsVisibleChanged`/`Unloaded`; cached-but-hidden views were previously animating forever. The loader also stops non-active stage motifs. Hidden-state CPU ≈ 0.

## Never-hang discipline

A meeting assistant that freezes is worse than useless. Rules now enforced:

- **The UI thread never blocks.** Audio capture start and stop both run on background threads (`Task.Run`); the pill transitions immediately. The original hang was `StopRecording` joining the loopback worker (up to 3s) on the UI thread - which also stalled the low-level mouse hook, freezing all input.
- **The mouse hook does zero UI work.** A low-level hook runs on the UI thread's message pump, so any slow work there lags the whole system. The hook only compares the click point to a *cached* rect updated on layout - pure arithmetic. The global keyboard hook was removed entirely (invasive + another stall vector).
- **Single instance, but relaunch surfaces it.** A named `EventWaitHandle`: a second launch signals the running instance to show Home, then exits. Plus a tray "Restart Toki". So the app is always one process, always reachable, never a zombie that blocks new launches.

## Loading animation (M3.1)

The summarizing/loading view is a staged character scene, not a static spinner: Toki (breathing, blinking, bobbing, soft glow) beside a motif that crossfades through the stages of understanding a meeting - rings (listening) → document with sweeping highlight (reading) → checkboxes ticking (organizing) → pen drawing an underline (writing) - over a filling progress bar. Stages are placeholder-paced now; M6 will drive them off the real transcription/notes passes. Verified live via `--noghost` multi-frame capture.

## Distribution (test build)

`dist\Toki.exe` is a **self-contained single-file** publish (bundles the .NET runtime) so it double-clicks and runs on any Win10 2004+ PC with no "unable to run" prompt. Shortcuts on the Desktop and in the project folder. Real install (signed Velopack installer + launch-at-login, Wispr-Flow style) is M8. A single-instance mutex prevents duplicate pills/hotkey fights; global exception handlers keep the app alive through any UI/background fault.

## Lessons from open-source notetakers (research, informing M4/M6)

Surveyed Meetily, Hyprnote, Vibe, Buzz, Whishper, Amurex, and others (MIT ones are safe to learn from; **Whishper/Amurex are AGPL - do not copy code**). Key takeaways adopted into the plan:

- **Validated:** the ConsentStore mic-in-use approach and Parakeet-via-ONNX are both mainstream, correct choices (Meetily and Vibe both moved to Parakeet ONNX).
- **Windows advantage:** WASAPI per-process loopback avoids the macOS virtual-audio-device dance (BlackHole/aggregate devices) those tools need - we get clean per-app streams for free. Keep mic and each loopback as separate tracks = near-free "You vs Others" attribution.
- **Echo:** everyone hits it (Hyprnote built a custom ML AEC; Buzz separates speech pre-ASR). Since we have *isolated* streams, prefer **transcript-level de-dup** over signal AEC - but nudge "headphones recommended" for the speakers-bleed case.
- **M6 notes (biggest lesson):** small/fine-tuned beats big-and-general for meeting notes (Hyprnote's HyprLLM = fine-tuned Qwen3-1.7B). **Chunk the transcript** - map per-chunk (notes/decisions/actions) → reduce/merge → TL;DR over the *reduced* output, never the raw transcript. This is the #1 recurring pain (Meetily calls it unsolved). Our three-pass/TL;DR-last shape is right; make pass 1 a map over chunks.
- **Data model:** store transcript, intermediate, and final notes **separately** (Hyprnote keeps raw/enhanced/transcript as three types) so notes can be re-generated without re-transcribing. Fold into the M5 SQLite schema.
- **Don't over-promise diarization:** it's the most common gap/complaint. Ship honest "You vs Others" from the separate tracks; add a manual speaker-relabel UI rather than faking per-remote-speaker diarization.
- **Show liveness while recording** (meter/duration/partial text) - silence-until-done reads as broken.
- **Perf:** Windows on-device is the harder target (Hyprnote went Mac-first for this reason); ship a GPU path (Vulkan/DirectML) and validate CPU fallback timing on mid hardware.

## Transcription (M4)

On-device ASR pipeline: `you.wav`/`them.wav` → **16kHz mono float32** (`AudioLoader`, NAudio downmix + WDL resample) → **Parakeet TDT 0.6b v2** via sherpa-onnx (`Transcriber`, `OfflineRecognizer` with `ModelType="nemo_transducer"`, greedy) → timestamped `TranscriptSegment`s → `TranscriptMerger` (one timeline + echo de-dup) → `TranscriptionService` orchestrates and hands the UI a merged transcript.

- **Model**: `ModelManager` downloads the ~630MB int8 bundle to `%APPDATA%\Toki\Models\` (SharpCompress extracts the tar.bz2). Model files live outside the exe; only the sherpa native DLLs (~20MB: `sherpa-onnx-c-api.dll`, `onnxruntime.dll`) ship in the single-file exe (`IncludeNativeLibrariesForSelfExtract`). Verified transcription works from the *packaged* exe.
- **Recognizer lifecycle**: heavy (~630MB) - created once (`TranscriptionService`, lazy + `Warm()` at launch off the UI thread), reused; a fresh one-shot `OfflineStream` per file, so no state leaks between meetings (lesson 5).
- **Silence vs error**: an empty Parakeet result = nobody spoke → return no segments (silence is not failure, lesson 4/7). A thrown error is where the whisper.cpp fallback belongs (currently logged + skipped - a follow-up).
- **Echo de-dup** (`TranscriptMerger`): the mic hears the far end through the speakers, so a remote line can land on both streams. We keep the clean loopback ("them") copy and drop the "you" duplicate within ±4s; short interjections (<3 significant words) require an exact normalized match so real "yeah"/"okay" survive (lesson 6). Transcript-level (not signal-level AEC), matching the open-source-notetaker research.
- **Threading**: on Stop, capture-finalize + transcription run entirely off the UI thread; the loader animation shows meanwhile; the pill never blocks. The async model download uses `ConfigureAwait(false)` (a `.GetResult()` on the UI thread otherwise deadlocks).
- **Async deadlock gotcha**: blocking on an async download from the UI thread (`.GetAwaiter().GetResult()`) deadlocks unless every await is `ConfigureAwait(false)` - hit and fixed during M4.

Deferred: live streaming preview (streaming zipformer `OnlineRecognizer`) and the whisper.cpp error-only fallback (`Whisper.net` + `ggml-small.en`).

## Verification model

Two layers, both required:

1. **Unit tests** (`dotnet test`) for pure logic: state transitions, denylist filtering, echo de-dup, title validation, heuristic extraction.
2. **Self-test CLI flags** (`--mictest`, `--loopbacktest`, `--asrtest`, …) producing real artifacts (a WAV you can play, a transcript you can read) run on a real PC. CI cannot cover audio/GPU/model paths - this is permanent, not a temporary gap.

A milestone is done only when its real artifact has been seen/heard (brief, lessons 12–13).

## Deferred

- **CI pipeline** - needs a GitHub remote; added when the repo is pushed (M0 note in README).
- **Outlook calendar, teams features, "Ask Toki" chat** - explicitly out of v1 scope.

## Storage & notes (M5)

Local SQLite (`MeetingStore`, `%APPDATA%\Toki\toki.db`): `meetings` (with `kind` = meeting|manual), `transcript`, `decisions`, `questions`, `action_items` (all child rows cascade-delete). Manual notes reuse the `meetings` table (freeform `body`), so they're searchable alongside meetings - the brief's new manual-note feature with a `kind` column. `you:`/`them:` search scopes to transcript lines by speaker.

`NoteExtractor` is the deterministic **notes floor** (brief §5): cue-based classification of sentences into decisions / actions (with owner + due heuristics) / open questions, a bounded TL;DR, and a validated heuristic title (lesson 9: no separate title LLM pass; `<app> - <date>` fallback). Always available, instant, no model - this is what a missing/failed LLM degrades to. M6 layers LLM-written notes on top of the same `NoteSet` shape.

`MarkdownMirror` writes each meeting to `%APPDATA%\Toki\Notes\*.md` - the DB is source of truth; the Markdown is a user-inspectable, sync-friendly mirror/export (the open-source-notetaker research's "every meeting is a file you own").

Verification note: data-driven views populate on `Loaded`, which does NOT fire in the offscreen `--render` harness - verify them with live DPI-aware `PrintWindow` captures (`--state library/notes/manualnote --noghost`) instead.

## LLM notes (M6)

> **Superseded in parts by "Refinements (M6.1)" below** - the notes are now a single structured pass (not three), GPU offload is VRAM-auto (not CPU-by-default), and the model is warmed at launch. The M6 record is kept for history.

On-device notes with **Qwen3-4B-Instruct-2507 Q4_K_M** via **LLamaSharp** (llama.cpp), behind an `ILlm` interface so the source is swappable (local model / BYO OpenAI-compatible key).

- **Three passes, TL;DR last** (`LlmNoteWriter`): decisions → action items (owner/due) → TL;DR written from the *reduced* sections, not the raw transcript. Questions stay heuristic (reliable). Every model output goes through `TextValidator` (rejects CJK/kana/Hangul drift and 6+ char repetition - lesson 8), retried once, then falls back to `NoteExtractor` per section. So the user never sees degenerate text and always gets notes.
- **Sampler**: Qwen's official params via `DefaultSamplingPipeline` (temp 0.7 / topP 0.8 / topK 20 / presence 0.5). Never greedy.
- **CPU by default** (`GpuLayers = 0`), context 4096. This is deliberate: on a 4GB GPU, offloading all layers loads fine but then OOMs allocating the compute buffers, and llama.cpp crashes **natively** (an `AccessViolation` .NET can't catch after the fact). CPU can't OOM VRAM, so it can't crash. GPU offload is an opt-in setting for 6GB+ cards. `LLamaWeights` is loaded once (`LlmService`, lazy - not warmed at launch, to respect RAM); a fresh `StatelessExecutor` context per generation.
- **Async deadlock**: LLamaSharp's `InferAsync` continuations must not be forced back onto a blocked UI thread - inference runs on a background thread (the recording flow already does; the self-test wraps in `Task.Run`). Symptom was a hang with flat CPU and the model paged out.
- **UX**: heuristic notes render immediately after a recording; the LLM upgrade replaces them in place (`ReplaceNotes` + `NotesUpdated` event) ~a minute later on CPU. The DB/Markdown are updated regardless of whether the Notes screen is still open.
- **Settings**: `AppSettings` (%APPDATA%\Toki\settings.json) - notes mode (Local / HeuristicOnly / ApiKey), GPU layers, and the BYO API key **DPAPI-encrypted** (per-user, never plaintext - brief §4). `NativeLibraryConfig` is set once at startup (Vulkan preferred, CPU auto-fallback) before any LLamaSharp call.

Deferred: chunked map-reduce for very long transcripts (current single-context handles typical meetings within 4096 tokens). (The "Built with" attribution screen shipped in M8.0 - see below.)

## Refinements (M6.1) - notes shape, speed, and polish

Driven by owner testing on real meetings. Three fronts: what the notes look like, how fast they're produced, and how the whole thing feels.

### Notes shape: Granola-style, one pass

**Decision: Summary → Key points → Action items** (dropped "Decisions" and "Open Questions"). Decisions just restated the to-dos right below them (the owner's exact complaint); open questions weren't wanted. Key points now capture *topics/context/decisions*, distinct from the action list. The `NoteSet.Decisions` field + `decisions` DB table are **repurposed** to store key points - no schema migration, older meetings still load.

**Decision: one structured generation, not three passes.** `LlmNoteWriter` asks for KEY POINTS + ACTION ITEMS + SUMMARY in a single call, with SUMMARY **last** so the model writes it from the details it just produced (preserves the "TL;DR last" property of lesson 8 within one generation). A tiny second pass writes the title from the summary. Each section still validates (`TextValidator`) and falls back to the heuristic on garbage. Halves the generations vs M6, and the output-token budget is trimmed (concise key points). Placeholder junk in owner/due (`->`, `-`, `tbd`) is stripped in the prompt, parser, **and** renderer (so pre-existing rows are clean).

### GPU offload: VRAM-auto, made coherent (`LlmRuntime`)

**Decision: decide GPU-vs-CPU once, up front, from real VRAM - and only load Vulkan if we'll actually offload.** The M6 "CPU by default" was safe but slow, and worse: the Vulkan backend was still loaded at `GpuLayers=0`, which is the **pathological path** - llama.cpp shuffles every layer CPU↔GPU (hundreds of graph splits). Measured on the owner's RTX 3050: that hybrid was **~65s**; clean CPU (no Vulkan) is **~46s**; a safe partial GPU offload is **~34s cold / ~13s warm**.

`LlmRuntime.Resolve()` runs before any LLamaSharp call. It reads true dedicated VRAM from the driver registry (`HKLM\...\Control\Class\{4d36e968-...}\NNNN\HardwareInformation.qwMemorySize`, max across adapters - no COM interop) and picks: ≥7 GB → full offload / ctx 4096; ≥3.9 GB → **20 layers / ctx 3072**; else CPU. `App` then loads the Vulkan backend **only** when `GpuLayers > 0`. Offload is deliberately conservative (20 of 36 layers ≈ 2.3 GB of 4 GB) to keep headroom - a live video call also holds VRAM, and a GPU OOM at model load is an **uncatchable native crash**. Tuning knobs: `AppSettings.GpuLayers` (0 = auto, -1 = force CPU, >0 = force N; Settings toggle maps to auto/CPU), env `TOKI_GPU_LAYERS` / `TOKI_CTX`.

**Both engines warmed at launch - gently, and memory-permitting.** On a machine with enough RAM, `WarmEnginesGently` loads the ASR then the LLM on a single **below-normal-priority** background thread, after a short delay and staggered, so a meeting-stop pays no model-load time without the launch making the PC stutter. On a small-RAM PC the LLM is **not** prewarmed (see below).

### Memory: one heavy model at a time on a small PC (`App.LowMemoryMode`)

**Finding (paid for on the owner's 6 GB laptop): holding both models resident is what actually crashed transcription, made first-launch clunky, and slowed everything.** Qwen3-4B (~2.5 GB) + Parakeet (~630 MB), on top of a browser and the meeting app, does not fit in 6 GB. The symptoms all traced to this: a real 35-min meeting saved *"nothing was said"* (onnxruntime threw the uncatchable-looking native `External component has thrown an exception` - it's an allocation failure), the mouse stuttered on first launch (heavy Windows memory-compression + paging while the 2.5 GB model preloaded), and processing dragged (swapping).

`Interop.SystemMemory.Physical()` (P/Invoke `GlobalMemoryStatusEx`) reads total RAM at startup; `< 8 GB` sets `App.LowMemoryMode`. In that mode Toki keeps **only one heavy model resident at a time**:
- **Before transcribing**, `TranscriptionService.Process` calls `LlmService.Release()` (dispose weights + GC).
- **Before writing notes**, `LlmService.EnsureLocal` calls `TranscriptionService.Release()`.
- **The LLM is never prewarmed** (the big preload was the main launch-clunk cause); it loads on demand at meeting-end.
- **After notes**, the LLM is released and the small recognizer is re-warmed, so idle Toki is light and the next meeting is ready.

Peak footprint drops from ~3.1 GB (both) to ~max(2.5, 0.63) GB. The cost on a small PC is a few seconds of model-load at meeting-end instead of it being pre-ready - a deliberate trade of a little latency for reliability and a responsive desktop. On an 8 GB+ PC nothing changes (both stay warm).

### Recording presence: calm, not a stuck animation

**Decision: the recording pill is a small, quiet chip - no looping waveform.** The old recording bar sat at bottom-center the whole meeting showing a **fake** animated "waveform" (a fixed decorative pattern, not real audio levels), which read as a distraction sitting on screen for 30+ minutes. It's now a compact chip (138×46): a slowly-breathing coral dot, the elapsed time, and a stop button. Click to expand for the transcript view. The expanded view's waveform became a calm "● Recording" indicator too.

### Click-outside dismissal: only ephemeral popups

**Decision: a stray click into your real work must never dismiss content you're using.** Click-outside used to close Home, Library, Settings, **Notes**, and NoSpeech - so clicking into the meeting app while reading notes made them vanish. Now `DismissOnClickOutside` is just `{Home, NoSpeech}` (the launcher menu and the transient "nothing was said" toast). Notes, Library, Settings, ManualNote - and recording/summarizing/prompt - stay until closed with their own button or Esc.

### The pill IS a translucent glass window - and ghost still works (corrected)

**Correction to an earlier claim:** translucency and ghost mode are NOT mutually exclusive. Cluely and "cheating daddy" (both Electron) ship a transparent window with `setContentProtection(true)` = `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`, and it works - the affinity is enforced at the DWM level (BitBlt / DXGI / Graphics-Capture all exclude it) regardless of transparency, on Win10 2004+.

**So `PillWindow` is now `AllowsTransparency=True`, `Background=Transparent`.** The Border is the pill: a translucent gradient fill (~0x9E alpha) + a faint top sheen, rounded by its own `CornerRadius` (no `SetWindowRgn` - outside the Border the window is genuinely transparent, so corners are clean). Verified on the owner's PC: `--noghost` capture shows a see-through pill; ghost-on capture shows the pill **absent** (still hidden from recording).

Two real gotchas: (1) **re-apply the affinity on every `Show()`** - Windows drops it across hide→show (`ApplyGhost()` runs after each show); (2) **do NOT use the DWM accent acrylic** (`SetWindowCompositionAttribute` / `ACCENT_ENABLE_ACRYLICBLURBEHIND`) - it fills the *square* window and shows sharp black corners around the rounded pill. Translucency comes from the Border brush, not the compositor.

### Icons: Segoe Fluent, not ambiguous unicode

**Decision: all icon buttons use the `Segoe Fluent Icons` font** (falls back to `Segoe MDL2 Assets` on Win10). The old unicode glyphs (`⧉` copy vs `⤢` open) rendered nearly identically and read as ugly. `IconButton` is now a clean floating glyph (`TextDim`) that lifts onto a subtle rounded highlight on hover. Glyphs: copy `E8C8`, open-in-window `E740`, close `E711`, back `E76B`, settings `E713`.

### Library open animation + drag-to-reorder

**Open animation - grows out of the pill.** The first attempt scaled the content in place from the window's own bottom-centre, which read as a "force open" because the pill sits at screen-bottom while the window is screen-centre (disconnected). Now `HomeView` passes the pill's on-screen rect (`PillWindow.ScreenDipRect()`, physical→DIP) and `LibraryWindow.PlayOpen(fromRect)` builds a `TransformGroup` (scale + translate) that starts with the content occupying that exact pill rectangle and animates to identity (`RenderTransformOrigin=0,0`; `scaleX0=pillW/winW`, `translate0=pillTopLeft−winTopLeft`), ~300ms `CubicEase`, plus a fade. GPU transform on laid-out content - no reflow. Falls back to a gentle centred grow if the rect is unavailable.

**Drag-to-reorder - settles cleanly on release.** Cards are pickable; past a 4px threshold the card lifts (ZIndex + opacity) and follows the pointer via a `TranslateTransform`, while the others animate a shift computed from a correct *insertion index among the non-dragged cards* (`shift = (i>from? −dh:0) + (oi>=ins? dh:0)`) - this fixed an off-by-one that mis-placed the drop. **On release the card animates into its target slot (settle, ~160ms) before the order commits + list rebuilds** - the earlier version returned early on a same-slot drop and left the card stuck between rows; now every path resets. Persistence: a new `meetings.sort_order` column via idempotent migration (`ColumnExists` guard), seeded to the existing newest-first order; `List` orders by `COALESCE(sort_order, …) ASC, started_at DESC`; new rows get `MIN(sort_order)-1` (top). A short click (no drag) still selects.

### Notes render monotone (owner preference over the coloured design)

**Decision: `NoteRenderer` section labels are monotone dim-grey uppercase - no coloured headers, no icons/ticks.** The design (`⑤`) uses green/blue/orange section headers, but the owner wanted an Apple-clean monotone look ("red and green never go on text"). So SUMMARY / KEY POINTS / ACTION ITEMS / TRANSCRIPT are quiet `TextDim` labels; bullets use a small dim dot; owner/due and YOU/THEM chips are neutral grey (YOU a shade brighter than THEM for distinction). Colour is reserved for content, never chrome. Spacing is deliberately generous and consistent so it always reads clean. The "● ON-DEVICE" badge was removed from both the pill Library view and the window.

### Library window cards match the design

Cards in `LibraryWindow` now render a calendar-style **date badge** (day over 3-letter month), title, a meta line (type · duration · relative time), the TL;DR snippet, and count chips ("N open" / "N people") - all already on `MeetingSummary`. Selected card gets a coral hairline. Matches `Toki-ProductDesign/⑥`.

### Long recordings never crash or OOM (windowed ASR)

**Finding: whole-file decode of a very long clip throws in the native recognizer, and the old loader OOM'd before it even got there.** Two fixes in the ASR path:
- `AudioLoader.To16kMono` now fills one exact-size array instead of a doubling `List<float>` + `ToArray` (which transiently held 3–4× the samples - it OOM'd on a 35-min file).
- `Transcriber.Transcribe` keeps the single whole-file path for normal-length meetings (best accuracy, lesson 3) but, past a safe length (~6 min; env `TOKI_ASR_MAXSEC`), walks the audio in ~3-min windows **cut at the quietest nearby 25 ms frame** so no word is split, decoding each with a fresh stream and offsetting timestamps to meeting time. A per-window `try/catch` means one bad window can't lose the whole meeting. Verified on the owner's actual 784 MB / 35-min recording that previously crashed.

### No "bad first draft" flash

**Decision: stay on the summarizing animation until the good notes exist, then reveal once.** M6 showed the heuristic notes immediately (raw transcript lines - ugly) and swapped in the LLM version ~10s later; the owner found the flash jarring. Now `StopRecording` writes the AI notes on the background thread *before* switching to the Notes screen (a 150s timeout guards a stuck generation; heuristic is still saved first as the DB floor and shown if there's no model). The user waits once, on the animation, and sees the finished notes.

### Transcription speed: measured, left sequential on purpose

**Finding: transcription - not the AI notes - is the bottleneck, and it's a hard floor here.** Parakeet runs at **~4× real-time on CPU**, and the two tracks are transcribed **sequentially**, so a 1-min two-party meeting ≈ 30s of ASR. Investigated every lever:
- **Parallelising the two streams is unsafe** - two `Transcriber` instances decoding at once gave **non-deterministic, corrupt** output (same audio → 13 segments one run, 270 the next; sequential is stable). sherpa-onnx's offline recognizer has shared global state. Reverted; correctness wins.
- **Thread count barely matters** (flat past ~6 threads; oversubscription hurts).
- **GPU ASR isn't available** - the shipped sherpa-onnx native is built CPU-only (`Provider="cuda"/"directml"` both fall back: "compile with -DSHERPA_ONNX_ENABLE_GPU=ON").

So total summarize time is ~35s on a 1-min meeting, dominated by ASR. The genuine path to <30s is **live transcription during the meeting** (task #14, parked) - then only the ~13s of notes remain at stop. A GPU speech engine (e.g. whisper.cpp on the Vulkan backend we already use) is the alternative, but a heavier dependency.

### UI polish

- **Library window** (`LibraryWindow`) as a resizable master–detail view - the maximise target and the Home/tray Library button - with a **"‹ Back to Toki"** button that returns to the pill. Manual notes are editable here with an explicit **Save** (`UpdateTitleAndBody`).
- **Shared `NoteRenderer`** draws the notes for both the pill Notes view and the window, so spacing/sections stay consistent (scales with a `spacious` flag).
- **Custom slim rounded scrollbars** and **custom rounded checkboxes** (coral fill + tick) as app-wide styles in `Theme.xaml`; soft opacity cross-fade when switching notes. **Copy transcript** + **maximise** buttons on the pill; the dead "search this transcript" placeholder was removed.

**WPF gotcha (cost time):** setting the `Template` property inside a `ControlTemplate.Trigger` throws *"'Template' cannot be set in the current element's Template"* at layout, which the global handler swallowed and the window rendered **black**. Hit it building the custom scrollbar; fix is to handle orientation in `Style.Triggers` (Width/Height) with a `Track Orientation="{TemplateBinding Orientation}"`, never swapping the template in a trigger.

### Transcript segmentation: split only at word boundaries (M6.6)

**Finding (paid for on a real 20-min meeting): the segment splitter was cutting words in half.** `Transcriber.GroupIntoSegments` starts a new transcript line when two decoded pieces are separated by a time-gap (>0.8s). On short, fast test meetings the gap never landed mid-word, so early results looked perfect. On a real meeting with hesitant speech ("sorry, my audio - just a minute") the gaps land *inside* words, because Parakeet emits words as sub-word pieces. The result: "Deepika" → four lines `D / eep / ik / a`, and notes built from those fragments read as "a udi o" / "min ute".

**The tokenizer marks word starts with a LEADING SPACE, not `▁`.** Verified by dumping `OfflineRecognizerResult.Tokens` on a real clip: `" R","as","h","m","i"` = "Rashmi". The old `Detokenize` did `Replace("▁"," ")` - a silent **no-op** here (there are no `▁`); the spaces were already literal in the tokens, so text *looked* right while any `▁`-based logic was dead. So a word-boundary guard must test for a leading space (`StartsWord` accepts a leading space **or** `▁`, to also cover other SentencePiece builds).

**Rule now: only break at a word boundary, on a pause OR end-of-sentence.** A split fires at a token that starts a new word (leading space) when either the pause exceeds 0.8s **or** the text so far ends a sentence (`. ? !`, which this build does emit). This keeps every word whole (sub-word pieces never trigger a split) and lands lines per-utterance - not per sub-word (the bug) and not one run-on paragraph per decode window (which would defeat the ±4s echo de-dup and You/Them interleaving). Measured on the meeting that shattered: **912 → 82 segments**, words whole, tail matches the audio end.

### Skip a near-silent stream before decoding (M6.6)

Real logs showed meetings where the user's mic was muted the whole call (`you peak 0%`), yet we still ran the full ~4×-real-time decode over a silent `you.wav` - about **half the total processing time, for nothing**. `TranscriptionService.TranscribeStream` now scans the loaded samples for any amplitude above a low floor (~-46 dBFS, early-exit) and skips the decode if the stream is silent - silence is not failure (lesson 4), a muted side just isn't transcribed. Halves processing whenever one side is muted; no effect on a two-active-party meeting (where live transcription remains the only real speed win).

### Library window opens instantly - profiled, three real causes (M6.6)

Opening the Library felt like a ~2s freeze (owner: "does not happen with WisprFlow"). Warming the window alone did **not** fix it - profiling with per-phase stopwatch logs found the cost was rebuilding content on every open, dominated by one thing:

- **Rendering the whole transcript synchronously.** `NoteRenderer.Meeting` did `foreach (seg in Transcript) body.Children.Add(TranscriptRow(...))` into a **non-virtualizing** `StackPanel`. The auto-selected newest meeting had **1140 segments** (× ~5 visual elements each) → thousands of elements built on the UI thread on the click. Fix: render a small first batch (25 rows) then **stream the rest in on `DispatcherPriority.Background`**, chunked, with a per-panel generation token so switching meetings cancels stale work. Render dropped from ~seconds to ~30ms.
- **`Reload` ran twice per open.** `Show()` raises `Loaded`, and a `Loaded += Reload` handler duplicated the explicit `Reload` in `ShowLibrary`/`Warm` - every open rebuilt the entire list+detail twice. Removed the handler.
- **Rebuilding when nothing changed.** The window is now kept alive (Hidden, never destroyed) and only re-`Reload`ed when a `_dirty` flag is set - `LibraryWindow.MarkDirty()` is called when a recording saves a meeting, a note is created/edited, or a meeting is deleted. A plain re-open with no data change is just `Show()`.

Measured (owner's PC, on the 1140-segment meeting): cold first-ever open ~940ms → **warm + dirty-skip open ~80ms**. Warming (`Warm()`, below) pre-pays the one-time create+layout+first-render off-screen at startup on a Background dispatcher tick, so even the first real open hits the fast path.

#### Kept warm, never destroyed

**The clunk was the window *building*, not the animation.** Opening the Library created a fresh 940×640 `Window` and laid out the whole master–detail view synchronously on the click - so the empty frame appeared, then visibly populated ("loads the window"), and only after all that did the pill finally hide. A slow, overlapping handoff. Earlier attempts animated the window's *opacity*, which made it worse: animating a whole `Window`'s Opacity forces Windows to make it a **layered window** and re-blit the entire surface on the CPU every frame → stutter.

**Fix: create the window once, keep it warm, open instantly.**
- `LibraryWindow` is created a single time and **pre-warmed off-screen at startup** (`Warm()`, on a Background-priority dispatcher tick): shown far off every monitor with `ShowActivated=false`/`ShowInTaskbar=false`, laid out (`UpdateLayout`), then hidden - so the expensive first measure/arrange/render is paid once, off the critical path.
- Opening is now just `Reload` + `CenterOnWorkArea` + `Show` on the warm instance → instant, no visible build. `WindowStartupLocation=Manual` (we place it ourselves).
- It's **hidden, never destroyed**, between opens: "‹ Back to Toki" and the OS **X** button both route to `HideToPill()` (hide + bring the pill back) via a `Closing` handler that cancels the close. Guarded by `App.Exiting` / `Dispatcher.HasShutdownStarted` so a real app-quit still closes it (otherwise the cancelled close would keep the last window alive and block shutdown).
- Handoff order flipped: the pill is dismissed **first**, then the warm window shows - no more "window builds while the pill lingers". (No window-level opacity/scale animation at all now; instant is the smoothest.)

### Hover-reveal pill: cursor polling, not WPF mouse events (M6.7)

The pill is now an always-present tiny Toki that reveals a bigger view on hover - the launcher (Collapsed → Home) and recording (RecordingBar → RecordingOpen) share one mechanism. Two things had to be right:

**Smooth grow, not a size-snap.** A hover expand/collapse **animates the window's Width/Height** (`AnimateSize`, ~300ms open / ~190ms close so closing feels snappy) with the pill re-centred bottom-anchored on every `SizeChanged` (via `PlaceBottomCenter`, which does NOT call `UpdateLayout` - that would throw inside a layout pass). Content fades in over the morph to mask the reflow. Only these hover pairs animate; every other transition stays instant.

**Hover detection polls the real cursor, NOT WPF `MouseEnter`/`MouseLeave`.** While the window animates its size and slides under a stationary pointer, WPF's hit-testing fires phantom leave/enter events, which flip-flopped the pill open/closed in an endless loop. Instead a 60ms `DispatcherTimer` (active only in the four hover states) compares the real cursor position (`GetCursorPos`) against the pill's real cached rect. Because the expanded rect contains the collapsed one, this gives clean **hysteresis** - on it → open, off it → closed, no flicker - with a **dwell** (~260ms before opening, so a graze doesn't) and a **quick close** (~120ms). Toki blinks (`TokiBlob.BlinkNow`) on the rising edge of hover.

**State machine:** the hover pairs must be allowed **both ways** (`Collapsed ⇄ Home`, `RecordingBar ⇄ RecordingOpen`) in `AppState.Allowed`, or `TryGo` silently rejects the collapse - that was a real "Home won't minimise back" bug (Home→Collapsed was missing).

### Library maximise: clamp to work area + fixed-width content (M6.7)

Two separate maximise problems, both fixed:
- **The frame maximised but the content lagged then snapped to fill.** Cause: the detail was a full-width `StackPanel`, so widening the window forced WPF to **re-wrap and re-measure every transcript line** (hundreds–1000+) at the new width - that reflow trails the OS maximise. Fix: the detail is a **fixed-width readable column** (`MaxWidth`, ≤ the default column width so restored vs maximised are the *same* text width → nothing to re-wrap → WPF re-renders instantly). Also better typography. Meetings centre the column; the manual-note editor left-aligns it.
- **Jump / white edges.** A per-monitor-DPI window with a manual position mis-sized on maximise. `WM_GETMINMAXINFO` now pins the maximised bounds to the current monitor's **work area**, and `DWMWA_BORDER_COLOR` is set dark so the Win11 border isn't white.

### The virtual notch (top-edge overlay) - M7 UI

Toki moved from a bottom-centre pill to a **top-centre virtual notch** (flush to the top edge, bottom-rounded, grows downward). Mechanically it's the same morphing overlay re-anchored: `PlaceTopCenter` fixes the TOP edge (so height animates downward), `HideAnimated` retracts the height (tucks up), the Border is `CornerRadius="0,0,22,22"` with no top border. The whole state machine / hover-morph / blink / ghost mode carried over.

**Peek vs. open.** The resting/recording peeks (`Collapsed`, `RecordingBar`) drop the dark frosted surface entirely - `Root.Background`/`BorderBrush` go transparent and the sheen hides, so Toki is *just the character* floating from the edge. The dark panel only appears once it opens into a usable view. Opening Home from the launcher peek is **deliberate** (long `OpenDwellMs` dwell, or a click on `CollapsedView`) - a brief hover only shows the peek, because after an edge-summon the cursor is already there and a quick dwell would auto-open (felt wrong).

**Edge-summon, tuned for the crowded top.** When tucked (`Hidden`) the hover poll runs `EdgeTick`: accumulate dwell only while the cursor is at the **absolute top row** (`EdgeTopPx`), inside a **narrow centre band** (`EdgeBandHalf`), and **moving slowly** (velocity gate - a fast fling toward a browser tab is ignored). `EdgeSuppressed()` blocks it while the **left mouse button is down** (a window drag / Snap Layouts in progress) or a **fullscreen** app is foreground (`SHQueryUserNotificationState` → busy / D3D-fullscreen / presentation). Env `TOKI_NO_EDGE` disables it.

**Auto-tuck behaviours.** The launcher peek tucks back to `Hidden` once the cursor leaves both the peek and the edge. **Results** (`Notes`, `NoSpeech`) run a 2s `_resultTuck` timer that keeps them up while the cursor is over them and tucks them away ~2s after the cursor leaves - a glance, not a panel that squats on your work. `Home` does NOT auto-tuck (it's an explicit summon, dismissed by Esc/click-away) - auto-collapsing it made a hotkey/launch summon vanish instantly.

**Multi-monitor.** `PhysicalWorkArea` now resolves the monitor under the **cursor** (`MonitorFromPoint`), so the notch summons on whichever screen you're using and events land on the active screen.

### Context-aware snap-bar: peek → page → sliver (M7.1)

The notch became a stateful snap-bar. Three visual levels on one morphing window: **Toki peek** (`Collapsed` - character only, no dark box), **full page** (Home/Notes/Settings/…), **sliver** (the page tucked to a ~13px strip, view kept behind it). Transitions:

- **Summon** (launch / edge / hotkey) → **`Collapsed`** (Toki first, greets `CollapsedGraceMs` before it can tuck). **Click** (`App.OpenPeek`, set by the pill) or a long dwell (`OpenDwellMs`) → the **remembered page** (`_context`, default Home), blooming open.
- **Tuck** - a page tucks to a sliver `IdleTuckMs` (~2.5s) after the cursor leaves, or immediately on **click-outside** (the `InputMonitor` dismiss now calls `TuckCurrent`, NOT `ForceForDemo(Hidden)` - that's what preserves context; clicking your work no longer resets Toki to Home). The loader (`Summarizing`) tucks after `SummarizeShowMs` regardless of hover; **results** (`Notes`/`NoSpeech`) *enter* tucked (a quiet peek, never a panel dropped in). A tucked sliver `_autoHide`s to `Hidden` after `AutoHideMs`, `_context` remembered - so re-summoning restores the page.
- **Expand** - hovering a sliver runs `ExpandCurrent` (animates height back to the state's full size). No view rebuild - context is the same view instance.

`TuckCurrent`/`ExpandCurrent` are height-only `AnimateSize` animations that never change `_state` or clear `Host`, so the page is preserved. `enterTucked`/`_revealTucked` let a reveal enter directly as a sliver.

**Timer priority - the "works in demo, not in real use" bug.** `DispatcherTimer` defaults to `DispatcherPriority.Background`. The idle/auto-hide/hover timers were therefore **starved while the app warms the ~3 GB of ASR/LLM models at launch** - exactly when Home and the loader are on screen, so they never tucked (Notes, appearing after warm-up, did - which is what the owner observed). All three pill timers are now `DispatcherPriority.Input`.

**Accidental-tap guard.** A ~5-word mis-click was saved as a meeting because the old guard needed `<3s AND <5 words`. Now `words < 12 || durationMs < 4000` - a mis-click yields a handful of garbled words and is dropped; a real meeting always clears the bar.

### Close-off that sticks - the real "context-aware" model (M7.2)

M7.1 tucked pages to a persistent sliver. In real use that was two problems: the sliver **squatted on the screen**, and it **re-summoned itself**. The log was unambiguous - `Home → Hidden → Collapsed → Home` cycling every ~2 s. Root cause: the top-centre edge-summon zone is **the same physical space as a maximised browser's tab strip**. While you work in Chrome your cursor lives up there, so the moment a page closed off, the cursor (still at the top edge) tripped the summon again. Close-off *worked*; it just couldn't *stay*.

The fix is a **priming/arming** model, the industry-standard way to disambiguate a deliberate reach from an incidental hover:
- **`TuckCurrent` closes a page off entirely** (`ForceForDemo(Hidden)`, `_context` kept) rather than leaving a sliver - the screen stays clean while you work. The **loader is the sole exception** (it keeps a clean shimmer sliver so you know it's working).
- **`EdgeTick` only summons when `_edgeArmed`.** The arm is set true only once the cursor is **≥ `EdgeReArmBelowPx` (150 px) below the top edge** (down in the page body), consumed on each summon, and reset to false on every transition to `Hidden`. So a cursor parked on the tab strip can never summon; you must reach *up from the page*. Dwell also went 220 → 300 ms. An `_edgeReadyTick` cooldown (700 ms after any close-off) covers the immediate hand-off. This is what makes close-off feel like it sticks.
- **Reach-the-edge restores what Toki is *doing*.** If `_context == Summarizing` (writing your notes) the edge brings the loader back so you can check progress; otherwise Toki peeks first. "Access whatever Toki is doing by hovering the top-centre."
- **Results are one-shot.** Leaving `Notes`/`NoSpeech` → `Hidden` resets `_context` to `Home`, so re-summoning gives Toki, never the dead result (the "click Okay → hover → nothing-was-said again → forever" loop). `Notes`/`NoSpeech` now **enter open** (a ~2 s announce, `ResultShowMs`) then tuck; the **loader enters directly as a persistent sliver** (no flash) - the inverse of M7.1, matching "after recording, straight to a working sliver; when notes are ready, open briefly then tuck."

**Surface = pitch black.** `Root.Background` is a fully-opaque `#000` slab, no white sheen (the sheen `Border` is kept, referenced by code, but painted transparent). The translucent-frosted look washed out and made 13 px slivers invisible; an opaque black notch reads as carved-out and makes the sliver a crisp dark tab.

**Loader sliver = shimmer only.** `SummarizingView`'s top row is fixed at exactly `SliverH` and carries only a sweeping coral shimmer; the Toki + status line live in a second row that's below the fold when tucked - so the sliver is never a clipped slice of Toki's head.

### Notes: map-reduce + adaptive/honest (M8)

**Finding first (lesson 1: garbage in, garbage out - check what the LLM is fed).** The saved notes plus the logs showed the real failures, not "the model is dumb":
- A 25-min transcript is far larger than the model's **~3072-token context** (`LlmRuntime`, partial-GPU). The single `CombinedPass` overran it, `CompleteAsync` threw, and `WriteAsync` silently fell back to the **heuristic** - whose TL;DR is literally the first sentences and whose "to-do" is every keyword-matched line. That is the "summary = first three lines" bug.
- A short meeting *fit* in context, so the LLM ran - and the prompt **forced** KEY POINTS / ACTION ITEMS on every meeting, so it padded a screen-share logistics chat into corporate filler.

**Design - do it in pieces, adapt the shape:**
- **Map-reduce.** `Chunk()` splits the transcript at whole-segment boundaries into windows under `MaxChunkChars()` ( `(ContextSize − 800) × 3` chars, sized to the model). `MapChunk` compresses each window to a factual digest (or `NOTHING`); `FinalNote` reduces the digests (or, for a single window, reads it raw). Summarising summaries can't overflow. `StatelessExecutor` makes every call independent, so the several passes are safe and serialized.
- **Adaptive by omission.** The reduce prompt emits only the sections that apply; `MarkdownMirror` and `NoteRenderer` already guard on `Count > 0`, so empty sections vanish with **no schema/DB change**. `NoteSet.Decisions` stays repurposed as KEY POINTS.
- **Honest, not filler.** `LowSubstanceNote()` is a one-line honest note for genuinely empty exchanges. The `NOTHING` trigger is deliberately **narrow** - an early over-eager version dismissed a real (but informal, and shattered - see M8.1) working session as "nothing substantive," so the prompt was tightened to fire only on pure greetings/small-talk/connection-checks.
- **Verified against real data** via `--renotes <id>` (re-runs the writer on a stored transcript). This is why the harness matters: it let the fix be proven on the exact meetings that failed, no re-recording.

**How this meets the parked live-transcription work (owner's "run both optimally").** The same chunk boundaries are the unit of live work: with **incremental transcription** (Phase 2) the transcript finalizes per-utterance *during* the meeting, and on capable machines the **map** passes run as a rolling summary as the meeting proceeds - so Stop needs only a final reduce. On low-RAM PCs the recognizer and LLM never sit resident together: transcribe live (recognizer resident), then release it and load the LLM for the chunked map-reduce at stop. Map-reduce is the design that makes notes and live transcription compose.

### Transcript grouping - pausey speech (M8.1)

`GroupIntoSegments` breaks a segment at a **word boundary** on a sentence end *or* a >0.8 s pause. The pause rule alone shattered slow/deliberate speech into one word per line (every word had a >0.8 s gap). Added a `words` counter: the **pause break only fires once the line has ≥ `MinWordsForPauseBreak` (8) words**; sentence ends still break freely. Slow speech now forms full-sentence lines; genuine long un-punctuated runs still get chunked at ~8-word boundaries (needed because Parakeet punctuation isn't always reliable, and echo de-dup needs utterance-sized lines, not one giant run-on).

### Bring-your-own API key - cloud-speed notes, provider-agnostic (M9)

The honest fix for the multi-minute local wait (see `mansplain/16`) is optional: let a user point Toki at a cloud model they already pay for. Constraint #1 still holds - **it's the user's key and the user's cost; Toki spends nothing.** Decisions:

- **One provider-agnostic client, not a Claude/OpenAI SDK.** `ApiLlm` speaks the OpenAI **chat-completions** wire format (`POST …/v1/chat/completions`), which virtually every provider and router implements. So OpenAI, Groq, OpenRouter, a local Ollama/LM Studio proxy - and **Claude, via Anthropic's OpenAI-compatibility endpoint** - all work through the same code with no provider branches. The owner was explicit: "any router, just paste a key," and rejected per-provider model chips as confusing.
- **`https://` is the protocol; the *address* is what differs per provider.** `TryBuildEndpoint` normalises whatever base URL a provider needs (`api.openai.com`, `api.anthropic.com`, `api.groq.com/openai/v1`, `openrouter.ai/api/v1`, a loopback proxy) to end in `/v1/chat/completions` exactly once. **https is required over the network; plain http is allowed only for a loopback host** (local model, traffic never leaves the PC). `TryBuildModelsEndpoint` shares the same core (`TryBuildV1`) for `…/v1/models`.
- **Security posture (the whole point of doing this carefully).** The key is **DPAPI-encrypted at rest** (`AppSettings.ApiKeyProtected`, `CurrentUser` scope - decryptable only by this Windows user on this machine) and stored **only** in `%APPDATA%\Toki\settings.json`, never in the repo. The cleartext `ApiKey` property is `[JsonIgnore]`, so plaintext is never serialised. Over the wire the key rides on a **per-request** header (never the shared `HttpClient`), goes only over https, is **never logged**, and is scrubbed from any error text (`ProviderDetail` does `Replace(_apiKey, "•••")`). Every failure surfaces as a short, key-free human message; `TestAsync`/`ListModelsAsync` never throw.
- **Claude's two auth shapes.** Chat-completions on Anthropic's compat endpoint takes a **Bearer** token like everyone else; its **`/v1/models`** list instead wants `x-api-key` + `anthropic-version`. `ListModelsAsync` branches on host `api.anthropic.com` for that - the one technical Anthropic special-case, and it is *not* a default (see below).
- **Provider-first UI, search-dropdowns.** The flow is **pick provider → address auto-set → paste key → pick model**. Provider + model are compact `SearchableDropdown` controls (a 34 px pill that opens a floating `Popup` - its own top-level window, so a long model list never grows the notch - with a type-to-filter box shown only past 8 items). An earlier **iOS scroll-wheel** version (`WheelPicker`) was built and **rejected by the owner as ugly/bloated** - deleted. "Custom" reveals a free-text address for anything unlisted or local.
- **Live model list.** The model dropdown is filled by a real `GET …/v1/models` against the chosen provider, so it's always current (owner chose live-fetch over a curated static list). A successful fetch doubles as a key check. Switching provider **clears the saved model** and refetches, so it's never "stuck" on the previous provider's pick.
- **No provider is the default.** An early version auto-targeted Claude and defaulted the model to `claude-sonnet` - the owner corrected this twice ("Claude is not default; you decided to build around it"). Removed: `PickDefault` now leans neutrally toward the cheaper tier via **generic markers** (`mini`/`instant`/`flash`/`small`/`haiku`/`8b`), never a provider name; the factory default is the stock OpenAI base.
- **Positive-enforcement state - one message, one green dot.** `AppSettings.ApiVerified` (persisted) is true **only after a successful Test connection** (which hits chat-completions, the real notes path); any key/model/provider change clears it. `LlmService.Active()` still gates notes on `ApiLlm.Ready` (key+model+endpoint present) - separate from *verified*, so we never claim a key works on faith. The UI shows a **single** connection line with a status dot: dim when unverified, **green with a soft outward pulse** when verified. (The owner first said "no green"; that meant the earlier garish tick - a tasteful green dot/pulse is the wanted signal. The duplicate "Connected" line and the redundant "Model status" line in API mode were removed.)

**Headless render must never touch the network or settings (a cost-real bug).** The `--render` PNG harness constructs the *live* `SettingsView` against the user's *real* `%APPDATA%\Toki\settings.json`. The model-fetch fired under render, hit the network, and `s.Save()`'d - racing across overlapping render invocations and corrupting settings. Fix: `App.Headless` is set before `RenderHarness.RenderAll`, and `RefreshModels`/the pulse animation early-return on it. **Lesson (documented in memory):** never mutate the real settings file to render a state - PowerShell JSON round-tripping mangles the DPAPI blob, and doing so once destroyed the owner's saved key. Verify settings-dependent states with an isolated/throwaway file written by a BOM-free writer, or not at all.

### Ghost mode - a per-window opt-in, not a blanket rule (M9.1)

Ghost mode = `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE = 0x11)`: the notch is excluded from **all** screen capture - Snipping Tool, Xbox Game Bar, **OBS**, and Teams/Zoom screen-share alike (the same mechanism Electron's `setContentProtection` uses). The point is polish + privacy - Toki never leaks into a screen you're sharing in a meeting. Windows can drop the affinity across hide→show, so the pill re-applies it on every show. The cost is that you can't record a demo of Toki either, and the owner needed to. Decisions:

- **A visible, opt-in Settings toggle - not the dev flag.** `--noghost` already forced ghost off for dev capture tools, but it isn't discoverable. Added `AppSettings.ShowInRecordings` (persisted, **default `false` = hidden**), surfaced as "Show Toki in screen recordings & sharing" under a new **Recording & sharing** section in Settings (same row shape as the GPU toggle - no new control invented). The default stays private; the toggle is a deliberate opt-*out* of ghosting, flipped only to record. Startup resolves `App.GhostDisabled = args.Contains("--noghost") || AppSettings.Current.ShowInRecordings`.
- **Applies live, in both directions.** `ApplyGhost` now sets `WDA_EXCLUDEFROMCAPTURE` **or** `WDA_NONE (0x00)` from `App.GhostDisabled` - applying `NONE` *actively clears* an exclusion already on the window, which the old one-way "return early if disabled" guard could not do (it only ever avoided *setting* the flag). `App.SetShowInRecordings(bool)` flips the static flag and calls `PillWindow.RefreshGhost()`, so a toggle takes effect immediately, no restart.
- **Ghost is the pill's feature alone - removed from the Library (owner decision).** The separate resizable `LibraryWindow` had quietly copied the pill's capture-exclusion "for consistency," applied once at window creation and never re-checked against the flag - so it ignored the new toggle and stayed invisible in recordings even with ghost off. The owner never requested it; it was scope-creep. Removed entirely (`SetWindowDisplayAffinity` call + its now-dead P/Invoke deleted): the Library is an ordinary window, always visible to capture. The trade-off is explicit and owner-accepted - a Library opened *during* a screen-share shows its meeting list to viewers; the pill's privacy default is deliberately **not** extended to it. (Lesson in memory: never generalize a behavior to another component "for consistency" without approval.)
- **The unsolved half (parked).** Ghost-off makes the notch *capturable*, but Snipping Tool still pins its recording control bar to the same top-centre strip the notch occupies, so they collide regardless of monitor count - its controls stay on the recorded screen. A recorder with detached controls (e.g. OBS on a second monitor, capturing the notch's screen) is the clean path; not yet built.

### Live light/dark theming - replace the brush, don't mutate it (M10)

Every themeable colour is a brush in `Theme.xaml`, referenced everywhere via **`DynamicResource`**. `ThemeManager.Apply` swaps the theme by **replacing** each brush resource (`app.Resources[key] = new SolidColorBrush(...)`); DynamicResource consumers re-resolve and repaint live, no rebuild. `ThemeManager.Palette` (key, darkHex, lightHex) is the single source of truth. Two approaches that FAILED - documented so they aren't retried: (1) `<Color>` resources referenced by DynamicResource, poked at runtime - only *some* consumers updated; (2) one mutable brush per key, setting `.Color` on toggle - **WPF freezes brushes after first render**, so the second toggle throws "read-only state" (the icon flipped but nothing repainted). A `--themetest` self-test asserts an already-rendered element recolours on swap. Code-built controls that cache a brush (Library cards, note content, dropdowns) recolour via a `ThemeChanged` event (rebuild) or `SetResourceReference`. Default dark; light is a warm Apple off-white, not stark `#FFFFFF`.

### The notch is a tinted mirror (M10)

The notch surface is a **semi-transparent** brush (~85 % black / ~88 % white) on the `AllowsTransparency` window, so the desktop colours behind it bleed through faintly - a see-through slab, **not** an acrylic blur. Acrylic was rejected: it fights the notch's top-flush / bottom-rounded shape and leaves square corners (the reason M7.2 went solid black). Kept fairly opaque so text stays readable and it never washes out to grey.

### Templated notes - flexible sections (M10)

Notes moved from a fixed Summary / Key-points / Actions shape to **template-driven sections**. `NoteTemplates` defines 8 shapes; `LlmNoteWriter` classifies the meeting (a short LLM pass), then writes that template's sections. `NoteSet.Sections` (heading + bullets) is stored as JSON on the meetings row (`sections_json` + `template` columns, idempotent migration); `NoteSet.EffectiveSections()` unifies new notes with the legacy decisions/questions tables so old meetings still render. Action items stay special (checkboxes + name-tags, where the owner is only set from a name actually spoken). Renderer + Markdown mirror render sections generically. Manual template override is the next step.

### Composited notch open/close (M10)

Open/close previously animated the transparent layered window's `Width`/`Height` every frame - a full relayout **plus** a Win32 re-center per frame (janky). Now the window is sized to its final bounds **once**, and the notch unfurls/retracts via an animated `RectangleGeometry` clip on the root Border (render-only, no per-frame layout or move). The pill→Library transition grows the window content from the pill's screen rect and retracts back toward it; scrollers use a composited animated-offset behaviour (`Controls/SmoothScroll`).

### Library becomes the hub (P1)

The Library window was restructured into a **sidebar + main-pane hub** with three pages - **Ask / Library / Templates** - switched by `ShowPage` (visibility + a fade). The existing list+detail became the *Library* page (`Cards`/`Detail`/`SearchBox` names preserved, so `LoadList`/`Select`/`RenderMeeting` are unchanged). Nav buttons, suggestion chips, template cards, and the per-note picker are code-built with `SetResourceReference` and rebuilt on `ThemeChanged`, so they recolour on a theme swap. The **greeting** types in via a `DispatcherTimer` (skipped under `App.Headless`). **Drag-reorder is opt-in** (`_dragEnabled`, default off): `OnCardMove` early-returns when off, so a plain click still selects a card. The **per-note template override** calls `App.RewriteNotesAsync`, which runs the LLM inside **`Task.Run`** - the local model loads synchronously, so it must be off the UI thread; `LlmService.CanWrite` checks availability WITHOUT loading. The hub renders to PNGs via **`--libshot`** (the Library is an ordinary window, not part of the pill render harness). Phase 1 = the shell; the Ask query currently routes into Library search, with retrieval-over-notes answering + voice as Phase 2. *(Superseded in v1-iter3: the Ask landing is now the Luma-style hero and the **suggestion chips were removed** - `BuildSuggestions()` collapses the panel. See "v1-iter3 decisions".)*

### Ask chatbot - streaming + FTS retrieval (P2)

`ILlm` gained **`StreamAsync`** (`IAsyncEnumerable<string>`); `LlamaLlm` yields from `StatelessExecutor.InferAsync` (holding the generation gate across the stream, stripping template markers) and `CompleteAsync` is now just an accumulation of it; `ApiLlm` streams OpenAI-style SSE (`stream:true`, `ResponseHeadersRead`, parse `data:` → `choices[0].delta.content`). Retrieval uses a **standalone FTS5 table** `meeting_fts(title, body)` (bundled in `e_sqlite3`), where `body` = summary + sections_json + manual body + transcript + action/decision/question text; kept in sync by `ReindexFts(id)` inside Save/ReplaceNotes/UpdateTitleAndBody and a delete hook, with a count-mismatch self-heal (`RebuildAllFts`) on startup - all wrapped so a missing FTS5 downgrades to the keyword LIKE fallback, never fatal. `SearchRelevant` ranks by `bm25(meeting_fts, 5.0, 1.0)` (title-weighted). `NotesQa` adds a **scope-aware task-query router** (strips the task-phrasing words to see if the user named a specific meeting: if so it lists *that* meeting's items via `SearchRelevant` top-1, otherwise the global open-tasks list - answered from `action_items` directly, never the model), a **context-first prompt** (notes → question last, today's date, 1–4 sentence constraint, ~3500-char cap), and `AskStreamAsync(onSources, onToken)` - sources shown up-front, tokens streamed into the bubble. The Ask UI streams into a live bubble (loader until first token), persists conversations to `chats.json` via `ChatStore`, and has **New chat** + a themed **History** popup (auto-titled, deletable). Verified: `--asktest`, `--llmtest`, and FTS unit tests.

### Ask - understand-first pipeline + conversation memory (P2.1)

The retrieval/routing failures (summarising the wrong "most recent" meeting, "meeting on July 30" not found, a title's "follow up" triggering the task list) shared one root cause: **deterministic questions were being answered by a probabilistic pipeline.** Fix = a deterministic "understand the query first" layer in front of retrieval (`QueryUnderstanding`, ~1ms, offline, no model):

- **Temporal parse** - `Microsoft.Recognizers.Text.DateTime` resolves absolute/relative dates ("July 30", "last week", "in March") against `now`, wrapped with a **past-preference** policy (meetings are history) + a rank-phrase regex for "most recent / latest / oldest" (these are *ranks*, not dates → `ORDER BY started_at`, which no date parser produces). The matched span is stripped, leaving the topical residual.
- **Intent** - rules over the **query text only** (never note titles/content - that scoping bug is why "…follow up" mis-fired): `SummarizeOne · LookupFact · ListTasks · ListMeetings · Count`. `ListMeetings` requires the *plural* "meetings" (singular "the most recent meeting about…" is a one-meeting summary).
- **Deterministic resolution** - `NotesQa` answers Count / ListMeetings / ListTasks straight from SQL (`MeetingStore.LatestMeetingIds` / `MeetingIdsInRange` / `OpenActions`), instant and hallucination-free; task answers scope by **topic and/or date**. SummarizeOne / LookupFact resolve the target meeting(s) deterministically *first*, then stream from the LLM with the date stamped in - so recency can't be mis-picked (the model is told the meeting, it doesn't choose it).
- **Conversation memory** - `ConversationContext` (last intent, resolved meeting IDs, date range) is held per Ask thread in the UI and passed to `AskStreamAsync`; `QueryUnderstanding.ResolveFollowUp` turns "before that / after that" into a range shift, "what about the action items / summarize it" into an aspect switch pinned to the prior meeting(s), and bare "this week" into the prior intent with a new window. Verified: `--querytest`, `--followuptest`, `--asktest` (prints plan + resolved meetings), 14 xUnit tests.

### Why on-device embeddings (Stage C) are deferred - a native backend conflict

Adding vector/meaning-search was planned as the next quality layer, but **both native routes conflict with an engine Toki already ships**, so it was NOT forced in (it would risk the core):

1. **ONNX Runtime route.** `Microsoft.ML.OnnxRuntime` publishes `runtimes/win-x64/native/onnxruntime.dll` - the *same relative path* sherpa-onnx (transcription) already ships. sherpa's native is v1.27; the MS package's is ≈v1.20. One `onnxruntime.dll` wins in the single-file bundle, and a managed wrapper calling the other version's ABI can crash. sherpa exposes no generic `OrtSession`, so its runtime can't be reused for arbitrary models.
2. **LLamaSharp route** (small BERT embedder as GGUF via `LLamaEmbedder`, no new runtime). Blocked differently: LLamaSharp/llama.cpp bind **one native backend per process**. Qwen needs the **Vulkan** backend for GPU offload; the embedding model **hangs in llama.cpp's `encode()`** under Vulkan (embedding models `encode` rather than `decode`; even `GpuLayerCount=0` still allocates Vulkan compute buffers and stalls - a test process hung >300s at "init: embeddings required… overriding"). A CPU-only embedder can't coexist with a Vulkan Qwen in the same process.

**Resolution (next iteration):** run the embedder **out-of-process** - a short-lived `Toki.exe --embed` helper started with a CPU-only `NativeLibraryConfig`, piping vectors back - giving clean backend isolation with no effect on Qwen's Vulkan path; alternatively unify a single onnxruntime version verified against both sherpa and Microsoft.ML.OnnxRuntime. The scaffolding (`EmbeddingService`, `ModelManager.Embed*`, `--embedtest`, bge-small-en-v1.5 Q8 GGUF) is committed but **inert** - nothing in the running app calls it and onboarding doesn't fetch the model, so there is no hang risk. Active retrieval stays FTS5/BM25 (strong for one user's own vocabulary; the reported bugs were temporal/intent, now fixed by the understand-first layer).

### Selectable note + layout (P1.1)

The open note is rendered as **one read-only `RichTextBox`** (`NoteRenderer.MeetingDocument`) hosting a single `FlowDocument`, not a stack of per-element read-only `TextBox`es. Only that shape lets a drag-selection span every block, so the whole note (summary → sections → to-dos → transcript) selects and copies together; `--copytest` proves it by selecting `ContentStart..ContentEnd` and asserting each part is captured (a mouse drag isn't screenshot-verifiable). `NoteRenderer.FullText` is the one-click "copy everything" text. The Win32 context menu is suppressed (`ContextMenuOpening` handled) - copy is our own themed split-button + popup (popup rows are real `Button`s; a `Border`+`MouseLeftButtonUp` got swallowed by the `StaysOpen=false` popup, killing the menu items). Detail layout is **left-aligned, full-width** (no centered `MaxWidth` column - that caused a large left gap when maximised and pushed the scrollbar inward); the body's right margin is 0 so its scrollbar is flush to the window edge, with internal `Padding` keeping text off it. **Segoe UI Variable** labels sit ~1px low, corrected in the `PillButton` template (`BlockLineHeight` clamp + a −1px `TranslateTransform`), measured against a true-center line drawn by the `--libshot` `buttons.png` probe. Panels use a themed `PaneSplitter` `GridSplitter` (thin seam, pink on hover); the note list also **collapses** on a second `Library` nav click via a timer-lerped column width (`AnimateCol`, since `GridLength` has no built-in animation).

### Notch keeps its dropdowns; one scroll from the top (P3)

A WPF `Popup` is its own top-level window, so a model/provider list that drops *below* the notch is geometrically "outside" it - the click-outside dismiss and the idle-tuck both fired and retracted the panel mid-selection. `NotchPopups` is a tiny registry: `SearchableDropdown` registers its popup on open/unregisters on close, and `PillWindow.PointInWindow` / `CursorOverPill` treat an open popup's on-screen rectangle (and simply "any open") as part of the notch. The same control also fixes the classic toggle-reopen (a `StaysOpen=false` popup closes on mouse-down, then the pill's mouse-up would re-open it) by ignoring a click within ~250 ms of the popup closing. The hover-open flash was a single un-clipped frame between `Show()` and the reveal animation - the zero-height clip is now set *before* `Show()`.

The open note is **one scroll that fills the pane**: the action toolbar (Copy/Delete/Template), the title, the date, and the body all live inside the single `RichTextBox` (`MeetingDocument(topBar:)` inserts the toolbar as the first `BlockUIContainer`). So the scrollbar runs the full height from the very top like a normal document - no pinned header above a shorter scroll region. The `DetailHeader` control was removed entirely; the RichTextBox carries its own left/top/right padding so the scrollbar sits flush at the pane edge.

**Design rules (applied, and saved as a memory).** Dividers not boxes: one seam between panes, never a doubled line or a full outline; the pane splitter shows no always-on line and only a subtle grey (not bright pink) on hover - the panels' own background difference is the boundary. Selection is a fill, not an outline (list rows are transparent; only the selected row fills). Motion is brief: the connection state is a single quiet line (green when verified) with no perpetual pulsing dot. Colour is for content: Delete is red *text* on the neutral button; a red fill is reserved for the confirm dialog. Everything that reads as text is selectable (title/date included).

### Ask by voice - reuse the meeting ASR (P4)

The Ask-bar mic records the default input with the existing `MicCapture` (WASAPI) to a temp WAV, then `TranscriptionService.TranscribeFile` decodes it to text through the SAME gated Parakeet recognizer the meetings use - so there's no second speech engine, no new download, and the recognizer is already warm from launch. It runs off the UI thread and returns null (model absent / decode error), "" (silence), or the joined text. The UI is Google-/Safari-like: click to record (mic turns coral, the box shows a pulsing "Listening…"), and it **auto-stops on end-of-speech** - a `DispatcherTimer` polls the level the passive `MicCapture.OnBuffer` tap records (a running `_lastSpeechTick` / `_heardSpeech`, cross-thread via atomic x64 long reads) and stops after a generous ~1.8 s silence once speech was heard (with a no-speech give-up and a hard length cap); you can still tap to stop. Then a pink "Transcribing…" loader shows while `TranscribeFile` runs off-thread, and the result fills the Ask box (append if non-empty) and focuses it, leaving the send to the user so an imperfect transcription never auto-fires. This is deliberately **user-initiated and mic-only** - it does not tap system audio and is not always-listening, staying within the meetings-only constraint; the always-on "Hey Toki" phrase is the separately-scoped, owner-approved carve-out. `--voicetest <wav>` exercises `TranscribeFile` directly.

### Mascot & the press-to-talk hotkey (P5)

`Controls/TokiBlob` was redrawn to the product-design look: the body is a vertical `LinearGradientBrush` (glossy light crown → deep pink base) with a soft `DropShadowEffect` and a radial top-gloss, so it reads dimensional instead of the old flat fill - and since every view embeds the same control, the redesign lands everywhere at once. Its animation vocabulary lives entirely inside the control over three composable transforms - `Breathe` (squash/stretch), `Hop` (bounce), `Tilt` (wobble/spin), plus per-eye blink scales - so `React()` can cycle distinct one-off reactions (squish/hop/wobble/wink/spin/big-eyes/nod) and `DropIn()` can play a springy hello without any view wiring; idle breathing/blink self-runs and pauses when off-screen.

Activation is **press-to-talk only** (owner decision, over a wake word): `HotkeyManager` fires `App.OpenVoiceAsk` → `LibraryWindow.ShowAndListen()`, which opens Ask and toggles listening through the same on-device voice path as the Ask-bar mic. There is **no always-on mic and no keyword spotter** - Toki listens only when the user presses the key (or taps the mic), so the meetings-only constraint is preserved without a carve-out. (sherpa-onnx keyword spotting was the researched wake-word route; it was dropped when the owner chose press-to-talk.)

### Push-to-talk on the notch (P6)

`Ctrl+Alt+Space` is hold-to-talk. `RegisterHotKey` only reports the press, so `Interop/PushToTalkHook` installs a `WH_KEYBOARD_LL` hook on its own thread (like the mouse `InputMonitor`): on Space-DOWN with Ctrl+Alt held it fires `onStart`; when any chord key releases it fires `onStop`, de-duping auto-repeat with an `_active` latch and never swallowing the keys. `App.PttStart` drops the notch into `PillState.Ask` and records the mic (`MicCapture` → temp WAV) **only while held**; `PttStop` stops it, `TranscriptionService.TranscribeFile`s the clip, then streams `NotesQa.AskStreamAsync` into the notch. The `Views/AskView` notch has three phases (listening waveform → thinking loader → streamed answer, selectable); it's AutoHeight and calls `App.GrowNotch()` → `PillWindow.GrowToContent()` to re-fit as content changes, and self-dismisses on Esc / × / a 30 s timeout. This is deliberately push-to-talk only - the mic is engaged solely while the key is held, so there is no always-on listening and no keyword spotter, keeping the meetings-only constraint intact (Wispr Flow works the same way). Full conversational Q&A rendered on the notch is a later refinement.

### Toki-as-assistant: command router, concise notch, screen-awareness (P7)

Three deterministic layers now sit in front of the Q&A model, grounded in research into shipping assistants:

- **Tier 0 - `Llm/CommandRouter`.** Navigation/action commands ("take me to the library", "new note") match an *anchored, whole-utterance* regex against a closed set, with a **question-shape veto checked first** (interrogative lead word or trailing `?` → never a command). Question always wins a tie - a missed command just falls through to Q&A; a false positive (a window popping open mid-question) is trust-destroying. Runs in both the notch (`App.ExecuteNotchCommand`) and the Library Ask box. `--`/xUnit `CommandRouterTests` cover 35 adversarial cases.
- **Concise notch answers.** `NotesQa.AskStreamAsync(concise: true)` swaps in a numbered-checklist system prompt (`SystemConcise`) tuned for a small Qwen to answer in 1–3 sentences, and caps the token budget. Length is enforced in code, never trusted to the model: `NotesQa.NotchTrim` cuts on a sentence boundary at ~280 chars and, when the model emits `[MORE]` or overflows, flags it so the notch shows a one-line teaser + a "See full answer in Library" hand-off (`LibraryWindow.AskInLibrary` re-asks it un-capped).
- **Screen-awareness (Tier-3, our own state).** `App.CurrentViewedMeeting` tracks the meeting note open in the Library. `AskStreamAsync(screenMeetingId:)` pins the plan to that meeting when the question is deictic ("this", "here") or underspecified ("what were the action items?") - but only when the user named no topic/date/recency (those always win), and only for Summarize/Lookup/ListTasks. Resolution is deterministic (in code), so "this" can never resolve to the wrong meeting; reuses the existing `QueryPlan.PinnedMeetingIds` injection point (`QueryPlan` became a `record` so `with` works). No screenshots or OCR - Toki reads only its own view-model, so nothing new leaves the machine.

The notch keeps a session `ConversationContext` (`App._notchCtx`) for follow-ups, reset when the notch reopens from closed.

### Assistant polish: patch streaming, capped generation, conversation thread (P7.1)

**Patch streaming.** Both the notch (`App.HandleNotchText`) and the Library Ask (`OnAsk`) no longer render each token; the `onToken` callback appends to a locked `StringBuilder` and a `DispatcherTimer` (~70 ms) flushes the accumulated text, so words appear in chunks. **Capped generation (no overshoot-cut).** In the notch, that callback also checks `NotchOverBudget` - a completed sentence past ~240 chars, or the model's `[MORE]` flag - and cancels the shared `CancellationTokenSource`, stopping generation at a clean boundary. So the notch never streams a long answer then visibly trims to a teaser; it simply stops where it should, and shows the "See full answer in Library" button when it stopped early. **Adjustable size.** `AskView` is AutoHeight; each flush calls `App.GrowNotch()` → `PillWindow.GrowToContent()` (150 ms) so the notch starts compact and grows to fit up to the thread's MaxHeight, then scrolls. **Conversation thread.** `AskView` is a scrollable `Thread` of user/Toki bubbles that persists the whole session (each push-to-talk appends a turn, streamed into the latest Toki bubble); `NewSession()` clears it when the notch reopens from closed. **Screen-aware trigger** was broadened: `NotesQa.OnlyGenericAsk` pins to the on-screen meeting when every content word is a generic ask-word ("what were the action items?"), not only on deixis. **Library window** `ShowLibrary` restores from minimized and `BringToFront()` (a Topmost flip) so it always surfaces; `AskInLibrary` opens a fresh conversation.

### Ask Toki redesign - persona tier, person slot, derived screen grounding, honest-first resolution (P8)

Five live-session failures (all screenshot-verified) were root-caused to routing/state bugs, not model weakness, and fixed deterministically - full design + research + live-test script in **`docs/ask-toki-redesign.md`**. In brief: (1) **topical-vs-list routing** - `ListMeetings` now requires the residual to be pure list-chrome (`OnlyListChrome`); any topic word ("Kai Sinat") makes it a `LookupFact` whose topic actually drives retrieval, and `NotesQa.ContentQuery` strips stop/generic words from every `SearchRelevant` call so the prefix-OR BM25 query can't be drowned by "meetings*"/"discussed*" (a true miss stays a miss). (2) **Screen grounding is derived, never stored** - `App.CurrentViewedMeeting` is now computed from `LibraryWindow.VisibleMeeting` (window visible ∧ not the off-screen pre-warm ∧ real meeting ∧ user-chosen, sticky across reloads); the old mutable static went stale (warm-up set it to an unseen note; hide cleared it and re-show never restored it) which is exactly what answered "this meeting" with the wrong meeting. The notch also grounds on the pill's Notes view (`_notchScreenId` captured at PttStart) and shows a dim **"About: \<title\>"** scope line (`AskView.Scope`) whenever an answer is pinned to the on-screen note - grounding must be visible to be trusted. (3) **Person/owner slot** - `MeetingStore.ActionOwners()` + `NotesQa.MatchPeople` resolve names against the closed vocabulary of owners that actually exist (first names + possessives handled, "You"/"me" guarded), filtering every task path (pinned/topic/global) with an honest miss that names the real owners; an unknown word can never trigger the filter. (4) **Persona tier** (`Llm/Persona.cs`, Tier 0.6) - anchored whole-utterance patterns for complaints ("why are you so rude?"), quality feedback, screen-awareness ("do you see my screen?" names the open note), capabilities, privacy, and AI-identity → warm canned replies, never retrieval. (5) **Voice** - sampler now matches the Qwen3-4B-Instruct-2507 model card exactly (T 0.7 / TopP 0.8 / TopK 20 / **MinP 0** explicit; presence 0.5 in the card's 0–2 band), `ILlm` gained optional **stop sequences** (LlamaLlm → AntiPrompts, ApiLlm → `stop` field; notch lookups stop at the first blank line instead of overrunning into `NotchTrim`), and both system prompts carry a **one-shot exemplar** in the target "sharp colleague" voice. LLM-route resolution now runs **before** the model-availability check, so honest "couldn't find it" answers need no model. Tests: `NotesQa.StoreOverride` seam + temp-file stores; every screenshot bug is a regression test (`NotesQaTests`, `PersonaTests`, extended `QueryUnderstandingTests`) - 228 green.

### Ask Toki reasoning route + self-reference + the full command set (P9)

P8 made the *deterministic* answers good; P9 makes the *thinking* answers possible - full design + research + live-test script in **`docs/ask-toki-reasoning.md`**. In brief: (1) **The `Analyze` intent** (`QueryIntent.Analyze`, `QueryUnderstanding.AnalyticalRx`) - analytical/synthesis questions ("what should I focus on?", "themes across my meetings", "how is X progressing?", "compare…", "biggest risks?") were all falling into the `LookupFact` pipe (top-3 retrieval, one-glance prompt, 85–170 tokens), which is structurally incapable of analysis. They now route to their own path: **breadth** (up to 6 topic-matched meetings via `NotesQa.RetrieveBroad` - analytical vocabulary like "themes/risks" is stripped from the retrieval terms since it describes the analysis, not note text; no anchor or no match → the recent 5, never a deterministic dead-end), a **reasoning prompt** (`BuildAnalyzePrompt`: conclusion first, 2–4 supporting points tied to meetings by name, partial-answer honesty - say what the notes don't cover), a **6,000-char context cap** and a **220/420-token budget**. Cue regex is phrase-level ("focus **on**", not bare "focus") and who/when/where-led questions stay factual - the Adaptive-RAG route-by-complexity pattern plus GraphRAG's local-vs-global finding, applied at Toki scale (no embeddings, still blocked). (2) **Self-reference** - "assigned to *me*" listed other people's items (screenshot bug): `NotesQa.SelfTaskRx` (tight phrases: "assigned to me", "on my plate", "mine"…) + `IsSelfOwner` resolve "me/my/I" to the notes' `Owner = "You"` values, with honest misses that name the real owners; multi-owner strings ("Lily, Mac") now split per person in `OwnerMatches`/`MatchPeople` (fixing "Mac's tasks" missing shared items); the LLM prompts state "'You' in the notes is the user". (3) **Nine new commands** with real handlers - `StartRecording` ("start taking notes" → the manual-record path, human-in-the-loop by definition; `PillState.Ask` also gained its missing transition-table entry, a latent `KeyNotFoundException`), `UseApiModel`/`UseLocalModel`/`UseHeuristicMode` (via `App.SwitchNotesMode` → normal settings path; refuses API honestly when no key is saved), `ThemeLight`/`ThemeDark` (`App.SwitchTheme`, live + persisted, idempotent), `GhostOn`/`GhostOff` (`App.SetGhostMode` → same live path as the Settings toggle), `OpenAsk` - and **"take me to the library" now lands on the notes LIST**: `ShowLibrary` gained a `page` parameter and `OpenHubPage` lands in one step (the old default always showed the Ask page). Question-veto discipline intact and negative-tested for every new command. Tests: new `NotesQa.LlmOverride` seam (fake `ILlm` records prompt/budget/stops) proves the analyze pipeline end-to-end without the real model - **300 green** (was 228).

### Ask Toki refinements - wake-name, identification, recall, the clipped notch (P9.1)

The next live run surfaced a cluster with one theme: Toki didn't recognise its own name (ASR renders "Toki" as *Doki/Toky/Docie/Toku/Tokyo*) or facts the DB already held, and then let the model improvise - full detail in **`docs/ask-toki-refinements.md`**. In brief: **`Llm/WakeName.cs`** (the single closed fuzzy alias list, used by CommandRouter/SmallTalk/Persona to strip a leading/trailing *address* - never rewriting the question itself); broader command vocabularies (articles, "api **key settings**" tails, word cap 6→8) and an **affection tier** in SmallTalk ("I love you, Toky" got a fabricated notes answer before); a deterministic **meeting-identification layer** (`NotesQa.IdentifyMeetingAnswer`: oldest/first/most-recent + title asks answered straight from SQLite; an ambiguous date that hits several meetings lists them all, never a silent pick); **title-match recall union** (`MeetingStore.TitleMatchIds` prepended at every retrieval site - the "Amazon" title miss) with content-term precision kept; anti-stretch honesty lines in both prompts; and **`Detection/AppNames.cs`** friendly app names + measure-before-reveal so the meeting prompt can't render clipped ("Com.tinyspeck.slackdesktop" → "Slack"). **389 green** (was 300).

### The dialogue brain - turn-type routing, user memory, general conversation (P10)

The owner's verdict after P9.1 was that Toki still "isn't smart - like first-gen ChatGPT", and the root cause was **architectural, not a bug**: the pipeline was retrieval-first for *every* utterance - chit-chat, identity ("what is my name" was FTS-searched and answered with three citations), user-stated facts ("call me Anirudh" → "no mention of Anirudh in the notes"), world-knowledge questions, and misheard commands ("switch to *like* theme" → the Analyze route's recent-5 fallback → a **fabricated** link to an unrelated meeting) were all force-fit into notes search. P10 rebuilds the orchestration into a **dialogue manager** (full design + research + live script in **`docs/ask-toki-brain.md`**): `NotesQa.AskStreamAsync` stays the single entry point but now routes typed turns - Command (UI tier, unchanged) → SmallTalk/Persona (unchanged) → **USER_FACT/IDENTITY** (`Llm/UserFacts.cs` + `Llm/UserProfile.cs`: "call me X" captured with a not-a-name guard and persisted through the existing `AppSettings.UserName` the greeting already uses, plus a capped `UserFacts` list; "what's my name / who am I / what do you know about me" answered from the profile, never a search) → **misheard-command clarification** (`CommandRouter.TryNearMiss`: command-shaped near-misses in the theme/ghost/model domains degrade to a targeted "did you mean…", never retrieval) → the deterministic SQL routes (unchanged) → **RAG-vs-parametric routing** (`Llm/TurnRouter.cs`: a turn with no meeting cue, no temporal, no pin, and no *evidence* in the store - a title/FTS probe over its content terms - is GENERAL) → either the P8/P9 grounded pipeline (one branch now, all wins intact) or **`Llm/GeneralChat.cs`**: the model converses and reasons from its own knowledge with the recent transcript (`ConversationContext.Turns`, appended on every answered path) and the profile in context - no retrieval, no citations, identical over both backends (local Qwen / BYO API). Two anti-fabrication rails: the **relevance floor** (an anchored Analyze that matches nothing returns empty → honest miss; the recent-5 fallback survives only for genuinely anchor-less corpus-level asks) and **no-citations-on-a-miss** (`NotesQa.LooksLikeMiss` on the answer's lead → sources dropped; the Library chip renderer retracts chips on an empty final list). Research: Adaptive-RAG/Self-RAG (route first, retrieve on demand), the RAG-robustness line (related-looking-but-unanswerable context misleads small models most), hybrid dialogue managers (chit-chat + task routing), MemGPT/ChatGPT-memory (a small durable user-fact store), VUI error recovery (clarify, don't act or guess). Test seams: `UserProfile.SettingsOverride` (in-memory settings - the real `settings.json` is never touched) + the existing `StoreOverride`/`LlmOverride`; `NotesQaTests` and the new `DialogueBrainTests` share an xUnit collection because the seams are static. **428 green** (was 389).

### Google Calendar foundation - OAuth (loopback + PKCE), raw-HTTP client, DPAPI tokens (M7.0)

The plumbing beneath the calendar features (notch preview, prep cards, meeting creator), built so the surfaces sit on one shared, verified base - full owner setup in **`docs/google-setup.md`**. Fable researched the current Google specifics; Opus built it; verified live against a real account. Decisions:

- **Installed-app OAuth, loopback + PKCE, no backend.** `Calendar/GoogleAuth.cs` runs the standard native-app flow: a CSPRNG `code_verifier`/`state`, an ephemeral-port `HttpListener` on `127.0.0.1` (probed via a throwaway `TcpListener` since `HttpListener` can't bind port 0), `Process.Start(UseShellExecute)` to the system browser, then the `authorization_code` → token exchange. Browser ↔ Google ↔ localhost only - **no Toki server touches the flow**, so hard constraint #1 ($0 upkeep) holds. The desktop `client_secret` is embedded (Google's own stance: "not treated as a secret" for installed apps; PKCE + exact loopback redirect + user consent are the real defenses) - a code comment forbids "fixing" it into a backend. Credentials resolve env → `%APPDATA%\Toki\google_client.json` (unwraps Google's `installed`/`web` shape) → embedded consts (empty until ship), so dev needs no code edit.
- **Least-privilege scopes, requested once.** `openid email https://www.googleapis.com/auth/calendar.events` - read **and** create (the meeting-creator needs write, and every Calendar-event scope is equally "sensitive" to Google, so a read-only variant now would only force a second consent later). No Gmail/Drive/contacts. `email`/`openid` are only to show "Connected as …" (the id_token's `email` claim, base64url-decoded locally, **not** signature-verified - it arrived over TLS straight from Google and is display-only).
- **Only the refresh token is persisted, DPAPI-encrypted.** `AppSettings.GoogleRefreshTokenProtected` reuses the existing `Protect`/`Unprotect` (per-user DPAPI) - the same pattern as the BYO API key. Access tokens are **never** written to disk: `CalendarService` holds one in memory with a 60 s early-refresh margin (clock-skew) and refreshes lazily behind a `SemaphoreSlim`. A refresh `invalid_grant` (revoked/expired) clears the stored grant, fires `ConnectionChanged`, and surfaces as "reconnect", never a crash mid-meeting. Tokens/codes are never logged (verified: `settings.json` carries only an opaque DPAPI blob; a `1//` plaintext grep returns zero).
- **Raw HTTP, no Google SDK.** `Calendar/CalendarClient.cs` hand-rolls `events.list` (timeMin/timeMax/singleEvents/orderBy - timestamps `Uri.EscapeDataString`'d, because a raw `+` in `+00:00` becomes a space and Google 400s - the one live bug found and fixed), `events.insert` (+`conferenceDataVersion=1` for an auto Meet link), and `events.delete`, all over the shared static `HttpClient` in the `ApiLlm` style (per-request Bearer, defensive `JsonDocument` parsing, short human errors). The official `Google.Apis.Calendar.v3` would drag several MB of assemblies + Newtonsoft into the single-file publish for three endpoints; rejected. `CalendarModels.cs` normalizes Google JSON into `CalEvent`/`CalAttendee` records (UTC times, all-day vs timed, Meet link from `hangoutLink` or the `video` entry point, cancelled instances dropped).
- **`CalendarService` is the single facade** every current/future surface calls (`Connect/Disconnect/Upcoming/Create/Delete` + `ConnectionChanged`); the seam the notch-preview / prep-card / meeting-creator work plugs into with no auth knowledge. Wired to the Home "Connect Google" button and a Settings "Google Calendar" connect/disconnect row.
- **Verification.** `--caltest connect|list|insert|disconnect` (head/tail output per house convention) - ran live: sign-in, read next 7 days, create+delete a throwaway event, token auto-refresh, encrypted-at-rest check. **446 green** (+18 calendar unit tests: PKCE against the RFC 7636 S256 vector, auth-URL construction, id_token decode, event-JSON parsing across timed/all-day/cancelled/conference shapes - all pure, no network). Deferred to M8: Google's free **sensitive-scope verification** (privacy-policy URL + demo video) to remove the "unverified app" consent warning before selling.

### Google Calendar UI - connected Home, agenda week view, NL create, edit, delete (M7.1)

The visible calendar, built entirely on the M7.0 `CalendarService` facade (Fable designed it against `Toki-ProductDesign/③ Home` + the prep-card language; Opus built it in two render-verified increments). Decisions:

- **Where it lives.** The connected-Home **"COMING UP"** glance is in the notch (per the reference), and the **week + full CRUD** is a new notch page (`PillState.Calendar`, 392×472, fixed height + internal scroll - NOT AutoHeight, same reasoning as Settings), NOT the Library hub: the entry point ("Meeting") is on Home, and Toki's rule is one element that morphs, not a second window. Home widened 320→344 for the Join pills. `Calendar` is `IsStayable` + `DismissOnClickOutside`, so it inherits the tuck/sliver/edge-restore lifecycle for free.
- **Agenda, not a grid.** A 344–392px column can't show a legible 7-column week; every premium compact calendar (Fantastical mini-window, Notion Calendar, Cron) shows an agenda-by-day list. So the week is day-grouped rows: `TODAY · TUE, JUL 8` (today in amber) with a live **Coral "now" line** inserted before today's first upcoming event, week nav (offset 0..3 - the backend is upcoming-only, so no fake past), Join always visible, and a hover-revealed **… menu** (Join · Edit · Open in Google · Delete) via `NotchMenu` (a Popup registered with `NotchPopups` so the notch doesn't tuck under it).
- **Create = one box, NL-first.** `Calendar/QuickEventParser` wraps `Microsoft.Recognizers` (already shipped) but **future-preferred** (a meeting is ahead - the opposite of `QueryUnderstanding`'s past-preference), pulls emails out by regex, and the remainder is the title. A live green preview line ("Tomorrow · 2:00 – 2:30 PM") builds trust as you type (the Fantastical-vs-Vimcal lesson: NL only works with a visible parse). Three chips (duration cycle / Google Meet default-on, sticky / people) are the entire "form"; no date-picker dialog. Double-submit guarded (button disabled before the first await); Esc/Enter handled in the TextBox so they never bubble to the notch.
- **Edit** reuses the composer prefilled (Save mode), gated by `CanEdit` (not a guest of someone else's event), backed by a new `events.patch` (`CalendarClient.PatchAsync` / `CalendarService.UpdateAsync`, `sendUpdates=all` so guests are notified; a Meet link is only added if absent). When no new time is typed the original time is preserved (the edit fallback in `ReparseComposer`).
- **Delete, research-split.** Solo event → delete immediately + a 6s **Undo** bar (re-creates via `CreateAsync` - a new id is fine for a solo event). Guested event → one inline **confirm** ("Guests will be notified"), no undo (re-inviting everyone would be dishonest to call reversible). Optimistic: the row leaves the cache immediately and reinserts on failure.
- **One data path.** `Calendar/UpcomingCache` is a stale-while-revalidate store (31-day horizon, 60s TTL, single in-flight request, `Snapshot` for instant paint, `Upsert`/`Remove` for optimistic mutations, `Invalidate` on connect/disconnect) shared by Home and Calendar so they never double-fetch and always agree. `Views/CalendarRows` builds every row (date badge, glance row, agenda row, skeleton, formatters) from Theme.xaml tokens by key, so both surfaces and a live theme swap stay consistent.
- **Provider-neutral.** Views touch only `CalendarService` + `CalEvent`/`CalAttendee`/`NewEvent`; the one place "Google" is named for real is `CalendarService.ProviderName`. Outlook later is a backend router + a string, not a UI rewrite.
- **Render-harness contract.** `Dev/CalendarSamples` seeds events (relative to now, so "in N min" + the now-line render) served by `UpcomingCache` under `App.Headless` - the views never hit the network offscreen. The entrance stagger is skipped under Headless (no clock advances, or rows render at opacity 0). Shots: `Home` (connected), `Calendar`, `Calendar-composer` (opened via an internal debug hook). Self-tests `--caltest update` (insert→patch→verify→delete) and `--caltest parse` proved edit + NL parsing live. **450 green** (+4 parser tests).

### Pre-meeting prep cards - deterministic content, restrained auto-surface (M7.2)

Fuses the calendar (M7.0/M7.1) with the notes store to prep the user before a meeting. Fable designed it against `Toki-ProductDesign/④`; Opus built it. Decisions:

- **Deterministic content, no LLM.** `Calendar/PrepBuilder.Build(CalEvent, MeetingStore)` is pure store-reads + string parsing → instant, and (deliberately) costs nothing on a low-RAM machine - it never loads the model. `PrepContent` = { Event, LastTime (MeetingRecord?), StillOpen (ActionItem[]), Agenda (string[]) }. An LLM recap was considered and rejected for the hot path (would defeat the load-on-demand memory goal); it's a possible deferred enhancement only.
- **Honest title-match.** Notes store only YOU/THEM, not attendee emails, so a calendar event matches a prior meeting by **title/topic**: term extraction reuses `NotesQa.ContentQuery` (one stopword source of truth) → `MeetingStore.TitleMatchIds` (confident) then `SearchRelevant` (FTS, gated by a shared-title-term bar). A candidate must be a *prior* `Meeting` with a non-empty recap (`Tldr`, else first key point). No confident match → `LastTime = null` and the view shows the **first-time** layout - never a fabricated "last time" (a wrong recap is worse than none).
- **The trigger is a restrained watcher, not always-on.** `Calendar/PrepWatcher` (singleton, `DispatcherTimer` 30 s at `Input` priority so model warm-up can't starve it - a real past bug) raises `PillState.PrepCard` for an event starting within `LeadMinutes` (5, `TOKI_PREP_LEAD` override). Restraint budget: real meetings only (`!IsAllDay && (HasMeetLink || other guests)`), **once per event-instance** (`_shown` set; `singleEvents=true` gives recurring meetings distinct ids), only while idle (`CanPrompt` = state Hidden/Collapsed - which alone excludes recording/summarizing/other prompts/open pages), never during fullscreen/presentation (`SHQueryUserNotificationState`), "Later" → permanent per-session `Snooze`, and an auto-retire that tucks a card the user ignored past start+2 min. Reads the network only via `UpcomingCache` (coalesced 60 s TTL - same cost as Home sitting open); `Changed` is marshaled to the dispatcher. Never starts under `App.Headless`.
- **Human-in-the-loop preserved (Hard Constraint #3).** The card *asks*; **"Join and record"** (`App.JoinAndRecord` - opens the Meet link + the same manual-start path as `StartQuickNote`, → `RecordingBar`) is the human choice; **"Later"** declines. Nothing records without the click. The auto-surface is a *visual, calendar-driven* nudge (zero audio), so it's compatible with the owner's push-to-talk-only mic stance - flagged for owner confirmation (auto vs hover).
- **State/shell.** `PillState.PrepCard` is AutoHeight (356×306 fallback; the first-time card measures short), NOT stayable and NOT dismiss-on-click-outside (a one-shot prompt with its own lifecycle, like MeetingPrompt - a stray click must not vanish the prep you're reading). `AppState`: Hidden/Collapsed → PrepCard; PrepCard → RecordingBar/Hidden/Calendar. `PillWindow.OnStateChanged` calls `PrepCardView.Refresh()` **before** the height measure (the clipped-notch lesson). **The ctor-grow recursion guard (from the Home crash fix) is applied verbatim** - `_ready`/`Grow()` - plus the systemic `_growing` re-entrancy guard now in `GrowToContent`.
- **Verification.** `Dev/PrepSamples` (Matched + FirstTime) feed render shots `PrepCard` / `PrepCard-firsttime` (checked light + dark, near-exact to ④). `--preptest` (temp store) proves title-match, open-item pull, unrelated-event rejection, and HTML/bullet agenda parsing. `--state PrepCard` demos the live card. The real T-5 auto-surface + "Join and record" recording need the owner at a live meeting. **450 green.**

### Voice-create a meeting - a confirm-gated command tier in the dialogue brain (M7.3)

"Schedule a call with Ana tomorrow at 3" on push-to-talk → a new deterministic command that CONFIRMS before booking. Fable designed the routing against the P8–P10 brain; Opus built it. Decisions:

- **Two tiers added to `NotesQa.AskStreamAsync`, no others touched.** **Tier 0.4 (pending-create resolution)** runs FIRST, before small-talk - if `ctx.PendingCreate` is set, this turn belongs to the confirmation, and the pending is cleared up front (single-shot). It runs before SmallTalk because "yes/okay/sure" are exactly what SmallTalk would otherwise swallow. **Tier 0.75 (create detection)** runs after the identity/user-fact tiers and before `CommandRouter.TryNearMiss` + all retrieval - a create must own its scheduling verbs and must never be "answered" from the notes. Both reply through the existing `Canned(...)` path, so recording/streaming behave identically on the notch and the Library.
- **Deterministic detection (`Llm/CalendarCommand.cs`), no LLM.** Fluff-strip + the **question-shape veto** are shared with `CommandRouter` (its `Fluff`/`TrailFluff`/`QuestionLead` promoted to `internal`) so voice-create and UI commands use one ASR source of truth. Create shape = a scheduling verb + article + event noun (`schedule/book/set up/create/… a meeting/call/sync/…`, anchored so "create a *summary* of the meeting" can't fire), OR "put/add … on/to my calendar", OR "remind me to …". Vetoes: any `\bnotes?\b` (that's `NewNote`), a question lead, >16 words. Time/title come from the already-built `QuickEventParser`; the person after "with" is captured for the confirm copy only.
- **Confirm before create is structural, not incidental.** `TryDetect` never calls `CreateAsync`. The only creation call site is tier 0.4's yes-branch, reachable only via a live `ctx.PendingCreate` (`QueryUnderstanding.ConversationContext.PendingCreate`), which is cleared before any branch - so a stray later "yes" can't create, a create never happens without confirmation, and there's no double-create (pending cleared pre-await; both surfaces serialize turns). The confirm always states the **resolved absolute** day/time + exact title, so a mishearing costs one "no", never a wrong event.
- **Confirm is a spoken yes/no follow-up, not buttons** - `AskView` renders read-only text rows and the Library ask has a different renderer, so a button row would mean two implementations + ctor-grow/measure risk; a `ConversationContext` field makes the confirm a first-class follow-up on both surfaces with **zero view changes**. `IsYes`/`IsNo` are tight anchored whole-utterance matchers.
- **Honest attendees + Meet off.** A spoken "with Ana" is a name; `QuickEventParser` only extracts emails by regex, so voice creates carry no attendees - the person stays in the title + a description note, never a fabricated invite. `AddMeet:false` for voice (a Meet link on an event no one else is invited to is decorative). `ExecuteAsync` is honest on not-connected / `invalid_grant` reconnect / failure, and optimistically `Upsert`s the created event into `UpcomingCache` so Home + Calendar refresh.
- **Shared-parser fix.** `QuickEventParser.PickFuture` now prefers a **business-hours reading (07:00–20:00)** among ambiguous future readings, so a bare "at 3" is 3 PM not 3 AM - benefiting the typed composer too; explicit meridiems ("9am"/"8pm") are untouched.
- **Verification.** `--voicecreatetest` (detection matrix + confirm gate, no mic/model/network) and `CalendarCommandTests` (`NotesQaSeams` collection; `StoreOverride`/`LlmOverride`/`CalendarCommand.CreateOverride` fakes): detection positives/negatives, title/time/person extraction, turn-1-never-creates, yes-creates-once, no-cancels, **stray-yes-never-creates**, no-time-asks-when, not-connected-honest. **472 green** (+22). Live speak→confirm→create needs the owner + mic + connected account.

### Premium redesign pass 1 + the glassy-notch blur (M9.0)

Fable did a deep design audit (Mobbin + Wispr/Linear/Vercel/Cron references); Opus built pass 1 in render-verified increments. Token system: a restrained type ramp in `Theme.xaml` (`Title/BodyStrong/Secondary/Caption/Micro` - replacing 18 scattered sizes), `SectionHeader` SemiBold (not Bold), `TokiCheck` redesigned (18px box, a fixed 12×12 tick - the old `Stretch=Uniform` distorted it - **green** fill + spring-pop), a new `TokiSwitch` (Settings booleans), a `ChipButton` (outlined→green-fill toggle). Screen work: `NoteRenderer` colour-coded headers (green/blue/purple by meaning) + left-align (justify→left); the Calendar composer flattened to a hairline-topped section with chip toggles; Home "Meeting"→"Calendar" + one-accent icons. Bug fixes: `PillWindow` idle-tuck skips a page with focused text input (typing no longer closes the composer); `QuickEventParser.PickFuture` prefers a business-hours reading (bare "at 3" = 3 PM).

**The glassy-notch blur - the hard part, fully solved.** True OS blur fights the notch's constraints (transparent/`AllowsTransparency` window, square-top/22px-rounded-bottom, ghost mode). The path that works:
- **Real acrylic** via undocumented `SetWindowCompositionAttribute` + `ACCENT_ENABLE_ACRYLICBLURBEHIND` (blurs the desktop behind; DWM `SystemBackdrop`/Mica don't render on a layered WPF window). Tint is `GradientColor` (AABBGGRR), **theme-aware** - dark frost in dark, white frost in light - re-applied on `ThemeManager.ThemeChanged` (it's a P/Invoke, not a DynamicResource, so it doesn't recolour on its own).
- **Rounded corners**: a window REGION (`SetWindowRgn`) does NOT clip the acrylic backdrop (DWM paints it across the full rect) - so corners are rounded by DWM itself (`DWMWA_WINDOW_CORNER_PREFERENCE = ROUND`, ~8px, which DOES round the backdrop). DWM's auto-drawn window border is killed (`DWMWA_BORDER_COLOR = NONE`) and the WPF rim is dropped in glass mode, so the frost is seamless.
- **One size**: DWM can only round at ~8px, not 22px - so BOTH modes use an 8px corner radius; toggling glass never changes the notch's size/shape.
- **Flush square top**: DWM rounds ALL corners (can't do bottom-only), which curved the top that must hang flush from the screen edge. Fix - in glass mode the window extends `GlassTopBleed` (8px) ABOVE the screen top (`_topBleedPx` offsets the placement `y`; window height + content top-margin grow by the bleed), so the rounded top corners fall off-screen, leaving a straight flush top with the rounded bottom. Applied on open (`OnStateChanged`) and on a live toggle (`RefreshSurface`).
- Wiring: `AppSettings.GlassyNotch` (default false), `ThemeManager.Palette` `NotchGlass`, `App.SetGlassyNotch` → `PillWindow.RefreshSurface`, a `TokiSwitch` row in Settings → Appearance. Ghost mode + the composited clip-reveal are untouched. 472 green.

### Calendar page revamp - one continuous agenda (M9.1)

The page had three arrows (back + two week-nav) and paged a week at a time, yet `UpcomingCache` already holds a ~31-day horizon - so the paging was artificial state. Killing it removed two arrows and two rebuild triggers. `RenderWeek` → `RenderAgenda`: one scroll, grouped by week (`WeekStartOf`, Sunday-start) into **bands** ("This week"/"Next week"/date-range) then by day. The header subtitle is **scroll-synced** (`OnScroll` → `UpdateSubtitleFromScroll` finds the top-most band via `TransformToAncestor`) and a **Today pill** fades in past a scroll threshold. Smoothness came from a **render-key** (`id:start:summary:hasMeet` per event + today's date **+ the theme**) that early-returns when nothing changed - the 60s now-tick and multi-source refresh no longer re-stagger identical rows - plus a **stagger-once** guard (`_entranceDone`). **Two bugs the revamp first introduced, then fixed:** (1) auto-height fought the `ScrollViewer` (a page that grows to fit its content never scrolls) → reverted to a **fixed 360×430** with an internal scroller, the Settings/Library pattern; (2) the render-key dedup skipped the rebuild on a theme swap, so the code-built rows kept the old palette (black-on-dark) → the theme is now **in the key** and the view subscribes to `ThemeManager.ThemeChanged` to invalidate + re-render.

### Notch input - scroll/clicks land on the notch, not the window behind (M9.2)

Symptom: with the cursor over an open page (Settings/Calendar/About), the scroll wheel scrolled the app *behind* the notch, and buttons felt like they only clicked near their centre. Root causes were two, fixed independently:

- **Scroll fell through.** The window is `ShowActivated="False"` (deliberate - a detected-meeting prompt must not yank focus out of your call), so the notch is never the *focused* window, and Windows sends the wheel to the focused window (or, only if the "scroll inactive windows" setting is on, the hovered one). Activation proved unreliable to force. The robust fix routes the wheel through the **global low-level mouse hook we already run** (`InputMonitor`, its own thread): on `WM_MOUSEWHEEL`, if the pointer is over an interactive notch page it marshals a smooth scroll of the notch's own `ScrollViewer` to the UI thread (`SmoothScroll.Nudge`, shared with the normal wheel path) **and returns non-zero to CONSUME the event** - so the window behind never moves. This works with zero dependency on focus, activation, or the OS setting. `CurrentScrollViewer()` walks the hosted view's visual tree; peeks (Hidden/Collapsed/RecordingBar) are excluded (`NeedsInput`) so a resting sliver never eats the wheel.
- **Edge clicks tucked the panel.** The click-outside dismiss (`InputMonitor` → `PointInWindow`) judged a borderline click just inside the visual notch as *outside* and tucked it away - which read as "buttons only work dead-centre." Widened the hit-test by `InsidePad` (6 px) so edge clicks count as inside.

A light `PreviewMouseDown` → `Activate()` remains so keyboard focus (Esc, typing) follows a click. Audio/detection are focus-independent, so recording is unaffected; peeks still never steal focus (Hard Constraint #3 holds).

### Attribution - the "Built with" screen (M8.0)

A **licence obligation**, not decoration: Parakeet is CC-BY-4.0 (requires name + source + licence link + a note that it was modified), Qwen3 and sherpa-onnx are Apache-2.0, the rest MIT - Toki cannot be sold without crediting them. `AboutView` (`PillState.About`) is a fixed-height scrollable credits list. *(Entry point moved in v1-iter3: it's now reached from the **Library window's Settings page → "Built with - open source & licences"** (`OnLibOpenSource`), since the notch Settings became UI/visibility only. The screen itself is unchanged - one canonical copy of the licence text.)* Data lives as a small `record Credit(Name, Role, Author, Licence, Url)` grouped into MODELS / ENGINES & LIBRARIES; rows are built in code so each opens its upstream source (`Process.Start … UseShellExecute`). Attribution-with-teeth licences (CC-BY, Apache) get **amber chips**; MIT is neutral. Two correctness calls: **Whisper is omitted** (it's a stubbed error-fallback, `TranscriptionService` never ships it - crediting it would assert a false dependency), and **Parakeet carries an "int8 quantised" note** to satisfy CC-BY's indicate-changes clause. The full, authoritative texts live in repo `THIRD-PARTY-NOTICES.md` (also referenced from the screen footer). State wiring: `PillState.About` added to the enum, the `AppState.Allowed` table (Settings⇄About, About→Home/Hidden), `PillWindow` `Sizes`/`ViewFor`/`DismissOnClickOutside`/`IsStayable`. Remaining M8: Google sensitive-scope verification, signed installer + auto-update, licence-key gate.

### The hardening pass - a full multi-agent audit, verified fix by fix (H1, 2026-08-04)

With the feature set settled, the whole codebase went through a deep audit (seven parallel subsystem
auditors + adversarial verification of every finding, alongside market research on the top consumer
notetakers). 67 findings survived verification - 0 critical, 14 high - and all highs/mediums plus
nearly every low were fixed in one pass. The load-bearing decisions:

- **Recording truth lives in the session, not the pill.** `App.IsRecording` now derives from
  `_session is not null`; every generic dismiss path (Esc, ✕, auto-hide, "close", tray toggle) goes
  through `App.CloseOverlay()`, which collapses to the recording bar instead of hiding while a session
  is live; the detector's auto-stop keys off the session; `BeginRecording` refuses to overwrite a live
  session. Before this, push-to-talk or Esc during a recording could orphan a running mic with no
  indicator - the one failure mode this product can never have.
- **Deletion means deletion.** The Library/pill "Delete" paths now use `App.DiscardMeeting` (DB row +
  Markdown mirror + WAV folder) - the confirm dialog's "its audio will be removed for good" promise was
  previously broken (three auditors found it independently). Trivial/silent takes also delete their
  folders, the mirror drops stale duplicates on retitle, and the %TEMP% diagnostic log is size-capped.
- **Crash/quit recovery (`RecordingRecovery`).** WAV headers are re-finalized every ~5s during capture
  (a crash leaves a playable file, not a zero-length RIFF), and a startup sweep transcribes any
  recording folder no meeting references into a saved Library entry (tiny/silent orphans are deleted).
  Live-verified on real orphans from the July M2 spikes - one real meeting recovered, four cleaned.
- **Mid-meeting device changes.** `MicCapture` now registers an `IMMNotificationClient`: when the
  default communications mic changes (or capture dies - Bluetooth drop, unplug), it rebuilds on the new
  default and keeps writing the SAME WAV, converting buffers through a streaming linear-resampler
  (`BufferConverter`) when the new device's format differs. The CLAUDE.md-mandated rebuild finally exists.
- **Detection correctness.** The denylist matches the app NAME only (matching the full path let a
  username like "Jacobs" - contains "obs" - kill detection for every per-user app); packaged apps
  resolve by real package family name via `GetPackageFamilyName` (the AUMID-prefix guess missed new
  Teams entirely → mic-only meetings).
- **LLM lifecycle safety.** `LlamaLlm.DisposeWhenIdle()` acquires the generation gate before freeing
  native weights (the low-memory `Release()` could previously dispose mid-inference - a native
  use-after-free); `ApiLlm` drops the 60s whole-operation timeout for per-call connect deadlines + a
  stream idle watchdog, and reads SSE with a cancelable async loop.
- **Notes degrade one rung, not two.** API mode with an unusable key now falls back to the installed
  on-device model before heuristic. (Found live: this machine sat in ApiKey mode with no key and was
  silently getting heuristic notes.)
- **Calendar contracts.** `CalendarClient` gained the one shared send/read/error-map helper (the four
  copies had diverged), full `nextPageToken` pagination (25-item cap silently emptied weeks 2–4 of the
  agenda), `sendUpdates=all` on insert with guests, timeout→`GoogleAuthException` mapping, an
  undecryptable-DPAPI self-heal in `CalendarService`, and an OAuth loopback listener that ignores
  requests without our CSRF state instead of tearing down (browser-JS port-scan resilience).
- **UI truthfulness.** Calendar/prep "Join" links only ShellExecute http(s) (calendar data is
  attacker-influenceable); the summarizing loader can no longer hang forever if the pill was forced
  away (the result retargets the remembered context); `NotchPopups` pre-computes rects on the UI thread
  so the hook-thread hit test actually works; the meeting prompt (like the edge-summon and PrepWatcher)
  is suppressed during fullscreen/presentation via one shared `PillWindow.QuietFullscreen()` - the
  prompt is delayed to the next poll, never dropped.
- Plus: atomic settings/chat writes (a torn settings.json wiped the API key + Google token), LIKE-term
  escaping, PTT chord-release fix (left/right modifier VKs), search debounce, theme-rebuild coverage
  (AboutView, manual-note body), SharpCompress CVE bump (0.38→0.48), dead `HotkeyManager` deleted, and
  the note prompts taught the two research-backed rules that separate good notes from slop: read past
  ASR mis-recognitions but never "correct" names, and record decisions WITH their stated reason.

Deliberately deferred (documented in `docs/backlog.md`): three pure-refactor consolidations
(NotesQa tokenizer triplication, notch/Library streaming plumbing, state→view factory) - regression
risk in delicate code with no user-visible payoff.

### Due-date task scoping, Library sort, and a security pass (H2, 2026-08-04)

**Task time-queries scope by DUE date (`TaskScheduling`).** The reported bug - "what tasks do I have for
this week" listing every open task - was `TaskAnswer` scoping a dated task query through
`MeetingIdsInRange` (tasks *from meetings held* in the window). On a recent library that's ≈ all meetings.
The market-validated model is deadline-based: `TaskScheduling.ParseDue` resolves each task's free-text due
against its meeting's date (future-biased - a due set on Monday for "Friday" is *this* Friday, via a
weekday roll-forward past Recognizers' past-occurrence default), and `Bucket` splits open tasks into
**Overdue** (due < today; suppressed for pure-future windows like "next week"), **Due-in-window**, and
**undated-from-window** (only when the window includes today). `DueWindowTaskAnswer` renders them grouped
with a **scope-echo header** ("2 tasks this week:"). Deadline cues ("due", "overdue", "behind on") were
added to `TasksRx` (routing) and `TaskWords` (so they're stripped from the topic, not mistaken for one).
Topic task queries ("action items about pricing") keep the meeting-search path. Two research agents
independently confirmed both the semantics and the exposed gaps (citations, scope-echo, due semantics).

**Library sort menu.** `LibrarySort` enum + a `ContextMenu` on the list: Newest/Oldest/Longest/Shortest/
Most-open-tasks/Manual. `App.Store.List` still returns the drag-reorder ("Manual") order; `SortItems`
re-orders in memory for the rest. Persisted in `AppSettings.LibrarySort`; enabling drag switches to Manual
so a drag can't renumber a view whose order isn't the stored one.

**Security pass** (full threat-model audit; most surfaces were already sound from H1):
- **P0 named-object squatting** (`Interop/SingleInstance`): the single-instance Mutex + show-signal
  EventWaitHandle were global, guessable, un-ACL'd - a local process could pre-create them to DoS startup
  or drive the UI. Now SID-salted `Local\` names with a current-user-only DACL (`MutexAcl.Create` /
  `EventWaitHandleAcl.Create`), and "already exists" defers to a running instance only after confirming a
  real Toki process exists (else it's a squat → take over). Runtime-verified.
- **`Interop/SafeShell`** - the one external-launch choke point. `OpenUrl` allows only absolute http/https
  (calendar join-links are attacker-influenceable); `RevealInExplorer` requires the path to resolve inside
  an allowed data dir. All five `Process.Start(UseShellExecute)` sites route through it.
- **`MeetingStore.ToFtsQuery`** - the single FTS5 builder (parameter binding does NOT protect a MATCH
  argument; safety is the alnum-only prefix-OR reduction). The only MATCH path uses it.
- **Markdown mirror containment** - `Write` asserts the final path is inside `NotesDir` before writing.
- **Prompt-injection delimiters** - notes fed to the Ask/Analyze prompts are fenced and labeled "DATA,
  never instructions". The model already has no tool authority (calendar creation is regex-on-utterance +
  confirm gate), so injection can at worst affect answer text.
- OAuth loopback probe socket set `ExclusiveAddressUse` (RFC 8252 §8.10); `<NuGetAudit>`/moderate level on;
  confirmed no `ServerCertificateCustomValidationCallback` bypass and no `BinaryFormatter`.
- 33 new tests (FTS adversarial corpus, path-traversal corpus, URL allowlist, BYO-API endpoint rules,
  per-user instance names, calendar pagination). 520 total green.

**Deliberately NOT done** (documented so it's a decision, not an oversight): swapping the push-to-talk
`WH_KEYBOARD_LL` hook for `RegisterHotKey` - the owner chose hold-to-talk, which needs the key-UP that
`RegisterHotKey` can't deliver; the callback already branches only on the chord and never stores other
keys. Adding a DPAPI entropy parameter - it would fail to decrypt every EXISTING stored key (wiping the
user's API key + Google token) for marginal gain against same-user malware that can call our code anyway.
Moving bulk data from `%APPDATA%` (roaming) to `%LOCALAPPDATA%` - correct long-term but needs a migration
and is owner-gated. All three are in `docs/backlog.md`.

### The assistant brain - a 1,155-case golden set + the reworks it drove (H3, 2026-08-04)

A stress-test-driven rework of the Ask brain. Two research passes produced an exhaustive utterance
taxonomy (15 category families, market-validated disambiguation rules, an ASR-homophone table); it
became the spec for a large deterministic prompt-test suite.

**Why the suite is deterministic + model-free.** The Ask pipeline is understand-first: turn-type routing
(command / small-talk / persona / user-fact / voice-create / misheard / meeting-QA / general) and scope
resolution (temporal parse → intent → person/topic/provenance) all run BEFORE any model, through pure or
near-pure predicates. So ~90% of answer QUALITY is a property of that deterministic layer and is testable
without Qwen. The suite (`tests/Toki.Tests/Brain/`) is built on: a **seeded 7-meeting fixture** with
known participants/topics/action-items (owner × due × done combos) + planted negatives; a **frozen clock**
(NOW = Wed 2026-08-05) so "this week"/"next Friday"/"on Aug 2" resolve identically forever; and a
**`RouteOf` classifier** that runs the real predicates (`CommandRouter.TryMatch`, `SmallTalk.TryReply`,
`Persona.TryReply`, `UserFacts`, `CalendarCommand.TryDetect`, `QueryUnderstanding.Parse`,
`TurnRouter.IsGeneralTurn`) in the documented pipeline order and returns the turn-type label. 1,155 cases
across turn-type routing, temporal windows, task provenance/due-date, end-to-end honesty (fake-LLM seam
proving "an honest miss never reaches the model"), person/topic/counting/identity, follow-up memory,
wake-name ASR variants, and a route-invariance perturbation overlay (§15).

**Reworks the suite surfaced (all brain-only, all via the deterministic layer):**
- **Task provenance vs due-date (`TaskScheduling.ModeFor`, the "August 2nd" fix).** A dated task query is
  DEADLINE ("what's due this week/Friday", "overdue") when it carries a due cue or a present/future
  window, and PROVENANCE ("what tasks did I have on Aug 2 / last week") on past-tense recall or a
  purely-past window. Provenance scopes to the tasks FROM meetings held in that window; a past date with
  no meetings is an honest miss. Pure function → unit-tested at every boundary.
- **Accurate privacy responder (`Persona`).** Data-handling and always-listening questions were only
  partially matched and fell through to retrieval - a wrong/fabricated privacy answer is a trust breach.
  The pattern is now broad (cloud/upload/store/train/local/private/who-can-see, mic/always-on) with two
  precise, honest canned answers (local-only + the BYO-key exception; no always-on mic).
- **General-knowledge shape override (`TurnRouter.HasGeneralKnowledgeShape`).** A definitional/how-to/
  creative/world-fact question ("what does OKR mean", "write me a haiku") is parametric even when a
  content word incidentally prefix-matches a meeting title - it no longer wrongly grounds. "what is A/AN
  x" (concept) is general; "what is THE x" (a specific thing) stays grounded - the article is load-bearing.
- **Coverage widening:** Count intent now spans calls/syncs/notes/recordings (not just "meetings");
  `TasksRx` gained on-my-plate/possessive-plate/owe/deadline cues; `NewChat` and name-setting phrasings
  ("i go by X") broadened; the analytical vocabulary gained recurring/common/frequent so a bare
  "what are the recurring themes" analyses the corpus instead of being treated as an absent topic.

No non-brain code was touched; 1,636 tests green, self-tests pass, dist republished + smoke-verified.

## v1-iter2 - decisions from the owner live-test sweep (2026-08-04)

**Notch sizing - content measures right, it just needed air.** The "text cut off everywhere" was NOT an
under-measure (a new headless `--measuretest` proved every auto-height view's out-of-tree measure equals
its true arranged height - it renders a real off-screen `Window`, no `RenderTargetBitmap`, so it works in
the sandbox where the PNG harness hangs). The last line just sat flush against the rounded bottom with ~0px
slack; DPI rounding tipped it into a clip. Fix = a **14px `BottomBreath`** added to every auto-height page's
height. Separately, rapid open/close corrupted the notch because a late-firing reveal/conceal clip
animation nulled the *current* clip / hid a *reopened* window - fixed with a **`_clipGen` generation token**
(a stale animation's `Completed` no-ops) + clearing the clip on every in-place/hover morph.

**Push-to-talk ends on SPACE-up only.** The hook used to fire `onStop` on *any* chord key release, so
relaxing Ctrl or Alt while still holding Space to talk cut the mic mid-sentence ("the waveform disappears
while I'm listening"). Space is the talk key; once the chord engages, only Space-up ends the utterance.

**Auto re-listen was a mistake; removed.** A "clarify + listen again" that opened a *second* `MicCapture`
grabbed the audio device and starved the next push-to-talk → every capture came back silent → an endless "I
didn't catch that". The real "no voice" cause was a **muted Windows mic** (`--mictest` now dumps the
Communications-vs-Console default capture device + all devices + peak). Kept: text clarification for a
misheard *command*, hold-to-retry.

**Answer voice is plain text, enforced in code.** Small models slip in a leading speaker label and emojis
("Toki: … 🚀") and parrot the date. `GeneralChat`'s prompt now bans labels/emojis/date-echo and ends
"Reply:" (not "Reply as Toki:", which caused the label); `GeneralChat.Sanitize()` strips a leading label +
emojis on every answer path (`GeneralAnswerAsync` + `NotchTrim`). The concise `"\n\n"` stop was removed -
Qwen emits a leading newline the stop treated as end-of-output → **blank answers**.

**Multi-turn calendar create is slot-filling, not one-shot.** `CalendarCommand.ApplyUpdate` merges a
follow-up turn's corrections (day/time/range/title/people) into the pending draft; the draft persists until
an explicit "yes" (create) or "no" (scrap), and releases if the user asks a real question / chit-chats.

**Name lives in the Library window, greeting refreshes on show.** Owner decision: name-change is passive,
so it moved out of the notch to a 4th Library "Settings" tab (Confirm/Cancel/✓Saved). `HomeView` now
re-reads the name in `RefreshGreeting()` every time Home is shown (it used to cache the greeting text at
construction and go stale). First-run onboarding is notch-native (`PillState.Onboarding`), gated on
`AppSettings.OnboardingComplete` so it fires once per install and corrects a stale stored name.

**Motion is one system.** `Theme/Motion.cs` centralizes durations + easings; arriving/expanding motion is
ease-OUT (Glass's 1−(1−p)³; Material/Fluent agree). The notch morph, message "arrive" (fade + rise), the
"↓ Latest" jump pill, and the 3-dot thinking indicator all draw from it. Start of the #39 revamp.

**"Meeting on now - Join?" is both-smart.** `App.LiveCalendarMeeting()` finds a calendar event with a Meet
link happening right now; the prompt then shows Join-call (opens the link + records) + Record, or, for an
ad-hoc detected call, Join-&-take-notes. `MeetingDetector.Snooze` now also clears `_prompted` so a still-
ongoing call re-offers once after the 10-min cooldown.

## v1-iter3 - decisions from the premium-UI pass (2026-08-04/05)

**Settings has one home, and the notch isn't it.** Owner decision: *"the notch should only have the
visibility, UI settings."* So the notch `SettingsView` keeps exactly three things - light/dark, ghost
(screen-recording visibility), glassy-notch - plus one row that opens the Library window's Settings page.
Everything with weight (AI summaries + BYO API key, calendar connect, your name, About/attribution) lives
there. The moved block is a **reusable `Views/AiSummariesSection` UserControl**, not a copy: one control,
one code path, hosted by the Library page - which is why the notch state shrank 360 → 208px high with no
feature lost. The rule going forward: *if a setting needs reading, it belongs in the Library window; the
notch only holds what you toggle at a glance.*

**Ghost mode must be re-asserted, not set once.** `WDA_EXCLUDEFROMCAPTURE` is a property of the HWND that
Windows silently drops when the window's composition is rewritten - and Toki rewrites it constantly
(`SetWindowCompositionAttribute` for acrylic, `SetWindowRgn` for the rounded bottom, `Show()` after a
hide). That's why ghost held on some states and fell off others: the states that re-composited lost it.
Fix = `ApplyGhost()` is idempotent and now runs **last** in every path that touches composition -
end of `ApplyBlur`, end of `ApplyRoundRegion`, after `Show()`, and at the end of every state change. Ghost
is a post-condition of "the window changed", not a one-time init.

**A date-scoped count is a count of the day, not of a meeting.** "How many tasks on Aug 2" was answering
"5" when two meetings that day had five each. Root cause was upstream of the SQL: the count/quantity
vocabulary ("many", "much", "count", "number", "total"…) survived into the extracted task **topic**, so
`SearchRelevant("many")` picked a single best-matching meeting and scoped the answer to it. Stripping
those words from the topic (`NotesQa.TaskWords`) makes the query fall through to the date scope, which
already spanned every meeting on the date. The lesson is the H3 lesson again: **when an answer is scoped
wrong, suspect the parse, not the query.** Covered by `tests/Toki.Tests/Brain/TaskCountScopeTests.cs`.

**Acknowledgements are small-talk, not questions.** "okay cool", "ok cool thanks" carried no retrievable
content, so retrieval returned *something* - a random meeting. `SmallTalk` now owns the acknowledgement
tier (and an "I just meant X" dismissal tier) before retrieval ever runs. Same shape as the anti-
fabrication rails: **the honest answer to a non-question is a non-answer.**

**Attendee avatars are derived, never fetched.** The calendar/COMING-UP/prep redesign shows overlapping
avatar stacks, but they're initials rendered from the attendee string with a deterministic colour hash -
no Gravatar, no Google profile-photo call, no network. Hard Constraint #2 holds: a prettier row must not
become a request that leaves the laptop.

**The app icon needs every size, not the biggest one.** The first `--makeicon` wrote a single 256px frame.
Windows will accept that, then downscale it itself for the 16/24/32px taskbar, title-bar and shortcut
slots - badly - which is why the owner kept seeing "the old ugly picture" after the mascot was already
redrawn. `Dev/SelfTests.MakeIcon` now renders `TokiBlob` at **seven sizes (16/24/32/48/64/128/256)** and
writes a proper multi-entry `ICONDIR`/`ICONDIRENTRY` table (0 = 256 in the byte field). Two Windows facts
that cost time and are worth keeping: the icon inside the exe and the icon Windows *shows* are different
things (Explorer/taskbar caches aggressively - unpin/re-pin or `ie4uinit.exe -show`), and the mascot is a
WPF vector, so the `.ico` is **generated from the live control** rather than maintained as a second asset
that can drift.

**No suggestion chips on the Ask landing.** The Luma-style landing shipped with three example-question
pills; the owner removed them outright (*"flat out ugly and never encourage"*). `BuildSuggestions()` now
clears the panel and collapses it - kept as a seam rather than deleted, so the decision is visible and
reversible. Design rule reinforced: **an empty state should invite, not instruct.**

## N1–N7 - decisions from the hand-written-notes batch (2026-08-05)

**Markdown is the storage format for hand-written notes, and that's a product decision, not a technical
one.** Toki already mirrors every note as a real `.md` file in `%APPDATA%\Toki\Notes\`. If the editor had
invented its own representation for tick boxes and headings, hand-written notes would have become the one
kind of note you can only read inside Toki - quietly breaking "all data local, and yours." Markdown also
happens to give exactly the feature set the owner asked for and no more: `#`/`##` sizes, `- [ ]` tick
boxes, lists, blank-line spacing. There is deliberately **no syntax for font family or colour**, because
those aren't offered. `Storage/NoteMarkdown.cs` is pure (no WPF) so the round-trip is unit-testable; the
editor turns blocks into a `FlowDocument` and back.

**Bullets and numbers use WPF's native `List`; to-dos don't.** Native lists give Enter-makes-the-next-item
and Tab-indent for free - it behaves like Word because it *is* the same machinery. A to-do can't be a
native list item (it needs a real interactive `CheckBox`), so it's a tagged `Paragraph` whose first inline
is an `InlineUIContainer`. `Focusable = false` on the box keeps typing and arrow keys flowing past it. The
"Enter on a to-do makes another to-do, Enter on an empty one ends the list" behaviour is hand-written,
because that's the one list convention users expect and WPF doesn't supply.

**A blank line is a beat, not an empty row.** The document sets `LineHeight`, which beats `FontSize` - so
shrinking a blank paragraph's font did nothing and sections sat in chasms. Blank paragraphs override
`LineHeight` directly. The user's spacing is still preserved verbatim on save; only its rendered height
changes.

**The prep card is a page now, not a prompt - so it stops appearing on its own.** `PrepWatcher` used to
call `TryGo(PillState.PrepCard)` ~5 min before a meeting, guarded by idle-only and fullscreen checks. The
owner found that intrusive, so it now only **arms** `App.CurrentPrep`; the guards went with it, because
arming shows nothing and there is nothing left to interrupt. Two doors replace the ambush: hovering the
resting peek opens the card when one is armed, and the recording page carries a **Prep card** button.
`PrepCard` became *stayable* (it's a page you write on, so it must not retract while you're on it) and
gained a Back button that is **always** visible - without it, an armed card made the peek unable to reach
Home at all. Logged for revisit: the auto-surface was also the de-facto "your meeting is starting" nudge.

**`JoinAndRecord` was discarding the thing it had just prepared.** `var prep = CurrentPrep; CurrentPrep =
null;` made sense when the card was a one-shot prompt that had to get out of the way. Once the card became
a page you return to mid-meeting, that line meant the card vanished during the exact meeting it was built
for. The armed card now survives the whole recording, and `PrepWatcher` only disarms it once the meeting
has ended AND nothing is recording.

**The scratchpad lives outside the meeting database.** `Storage/PrepScratch.cs` is a small atomically
written JSON file keyed by calendar event id. Two reasons it isn't a table: the text exists *before* a
meeting record does (you type while waiting for people to join), and losing something the user typed by
hand is categorically worse than losing a summary, which can always be regenerated. Nothing is ever
promoted into the Library automatically - a meeting that ends flags the entry `Awaiting`, and the Library
offers Keep / Discard **on the note list itself**. An entry the user never asked for is a hollow entry,
which is the same rule that stops a silent meeting from being saved.

**Typed notes steer the note; they never merely decorate it.** The scratchpad text is injected into the
final note-writing prompt *before* the transcript and phrased as a hard constraint ("Cover EVERY point
they wrote"), because a 4-bit 4B model treats soft suggestions as optional. Tick state is preserved into
the prompt as `[task]` / `[done]` - "still outstanding" versus "already handled" is exactly the signal
that should reach ACTION ITEMS rather than being flattened into prose. Crucially, the typed text is also
re-attached on **every** failure path (no model, model error, low-substance verdict) as a "Your notes"
section with unticked boxes as action items: the model is allowed to fail, but a sentence the user typed
is not allowed to disappear. The block is length-capped so the user's own notes can never be what
overruns the context window.

**Evidence links are computed at render time and stay silent when unsure.** `Storage/Evidence.cs` scores
word overlap between a bullet and windows of up to 3 consecutive transcript lines, with a floor (≥2 shared
content words and ≥34% coverage). Below the floor it returns null and no link is drawn. That asymmetry is
the whole design: **a link to the wrong line is worse than no link**, because it makes the note look like
it's citing something it isn't - the same anti-fabrication rule the assistant follows. Computing at render
time rather than storing means it applies retroactively to every note ever written and adds nothing to the
database. Clicking a link whose line is past the lazy-loaded preview expands the transcript first and
scrolls on completion.

## S1–S7 - decisions from the sticky-note rework (2026-08-06)

**A framework's text serialisation is not your data model.** The note editor's first version used WPF's
native `List`/`ListItem` to get "Enter makes the next bullet" for free, and read a line's text back with
`TextRange.Text`. For a paragraph inside a list, `TextRange.Text` **includes the list's marker**, as
`"•\t"`. So every save wrote the marker into the line's own text, every reload put a fresh list marker in
front of it, and the note gained a bullet per cycle - the owner's reached seven before they reported it
(`- •\t•\t•\t•\t•\t•\t•\tConfirm launch blockers`). Nothing in the Markdown layer was wrong, which is why
23 green round-trip tests never flickered. *The generalisable rule: when you ask a UI framework for the
text of something it also decorates, you get the decoration too - and if that text is your storage format,
the decoration compounds.*

**The same structure caused two more defects**, found in the first run of the new test: a paragraph inside
a `ListItem` isn't a child of `Document.Blocks` (so inserting the next line threw, and no toolbar button
could lift a line back out of the list), and a marker the user *typed* survived into the saved Markdown on
top of the one the block kind already implies (`- - two`); a blank line also split one list into two,
restarting the numbering. The fix was structural, not a patch: **flat
paragraphs, each tagged with its kind, with the marker in an `InlineUIContainer` that is not text.** Every
line is addressable, kind changes are local, markers cannot leak, and numbering is positional - recomputed
on every change, so it cannot go stale. Nesting is the only capability lost, and the Markdown format never
supported it. *Generalisable rule: when a framework primitive owns part of your data model, it will
eventually disagree with you about what the data is.*

**One definition of "marker", shared by three callers.** `NoteMarkdown.TryMarker` is used by `Parse`
(reading a file), `Write` (which strips exactly one marker - the precise inverse of the one `Parse`
consumes) and the editor (which promotes a typed `- ` into a real bullet and eats the characters). They
used to disagree, and disagreement between a reader and a writer is exactly how a file grows a character
every time it's saved. The invariant now tested: repeated save/reload **converges downward** - a stray
marker is shed, never added.

**A test that can't see the bug isn't a test.** The reported fault was invisible to the pure Markdown
round-trip tests, because it lived in the document structure. `--notetest` drives the real control: it
types, presses Enter, clicks toolbar buttons and ticks boxes, then reads the Markdown back. It needs a real
offscreen `Window` - a `RichTextBox` refuses a caret position until it's in a live visual tree, which is
also why the same trick appears in `--measuretest`.

**And a test that can't see the DATA isn't enough either.** The root cause was only pinned down by dumping
what the note actually contained on disk (`--dumpnote`), which showed seven literal `•` characters that no
amount of reading the rendering code would have revealed. "What the screen shows" and "what is saved" are
different questions; when they disagree, only the second one explains a growing file.

**Damage already written is healed on read, and healing is deliberately narrow.** `NoteMarkdown.Heal`
strips leading marker glyphs from a line's text, but *only* when followed by a TAB - that's how WPF
serialises a marker, and it isn't something a person types at the start of a note line. A hand-typed
"• " (glyph then space) is left exactly as written. When repairing a user's own words, the repair must be
narrower than the damage.

**The summary is written from the transcript alone.** Typed notes used to be injected as a hard constraint
ahead of the transcript (the Granola model). The owner removed it, and the reason generalises: a summary
exists so you can check it against what happened in the room. One that reshapes itself around your private
jottings can no longer serve that purpose - and the user cannot see that it has. Notes and summary now sit
side by side, neither touching the other.

**Sticky notes get their own tables because they have their own lifecycle.** They are written *before* the
meeting record exists, may outlive any single meeting, and must survive a crash mid-sentence. So: a real
row from the first keystroke, in `toki.db` - not a column on `meetings` (wrong cardinality), and not the
JSON file they used to live in (which never pruned, and could only ever bind a note to the one event it was
born on).

**Linking is explicit and by id - never by matching titles.** Meeting titles change, recurring events get
rescheduled, and two unrelated meetings are routinely both called "Weekly sync". Any heuristic here fails
in the two worst ways available: it buries your notes, or it shows you someone else's. There are exactly
two links (`sticky_event`, `sticky_meeting`), both written only because a person chose them. A picker
offers recent notes with when they were last touched; "start a new one" is always the first option.

**The blast radius is a design decision, not an accident.** Sticky notes are deliberately absent from the
meetings table (so the Library list *cannot* render one - the owner's complaint was a row you couldn't
open), from the FTS index (so Ask cannot surface private text), and from the note-writer's inputs. Deleting
a meeting cascades the *link* and leaves the note; deleting the note cascades every link. Each of those is
covered by a test that asserts the absence, because "it doesn't leak" is only true if something checks.

**Where a feature lives is a layout constraint too.** The sticky-note reveal was first put in the
Copy/Delete toolbar, where it squeezed the template picker down to "Template · Dec". Moved to the top of
the note body, under the title: it's *content*, not an action performed on the note, and it no longer
competes for the row. The note itself opens after everything Toki wrote and before the transcript.

**A `StaysOpen=false` popup eats mouse-up.** The Library's sort menu had already learned this and left a
comment; the note editor's picker had not, so every item in it was silently dead. Both notch pickers now
share `Views/NotePicker.cs`, built on real `Button`s (Click survives; MouseLeftButtonUp does not) and
registered with `NotchPopups` so an open menu doesn't let the notch retract underneath it.

**The pre-meeting nudge is restrained, not absent.** Auto-surfacing was removed on 2026-08-05 and restored
here, because it had been carrying a second job nobody noticed: it was the only "your meeting is starting"
signal. It now opens the card **once**, and only when opening costs nothing - notch idle, nothing
fullscreen. Otherwise it stays armed and one hover away. Restraint means picking the moment, not never
appearing.

## No em dashes (2026-08-06)

Owner rule: em dashes are out everywhere, replaced by a plain hyphen. 3,089 across 188 files, including
every markdown doc. Two things about it are worth keeping written down.

**Three em dashes survive, and each is load-bearing.** `Evidence.cs` uses one as a word-separator
character for the tokenizer; `LlmNoteWriter.IsEmptyMarker` matches one the model emits meaning "this field
is empty"; `GeneralChat.PlainDashes` is the function that removes them. `NoEmDashTests` lists all three by
name and asserts each file contains exactly one, so the exemption is a named fact rather than a blanket
skip that quietly grows.

**The model is a source of text too, and asking it nicely is not enough.** The same reasoning already
applied to emojis: a small model told not to do something still does it occasionally, and the occasion the
user sees is the only one that counts. So the prompts ask, AND `PlainDashes` enforces, at three funnels -
`LlmNoteWriter.TextPass` (every line of every written note), `GeneralChat.Sanitize` (every reply), and
per streamed token in `NotesQa`. The per-token one matters: an answer is read as it streams, so cleaning
the finished string would leave the em dash visible for the second it was on screen.

**EN dashes stay in ranges** ("9:41 – 10:11 AM", "2–4 sentences"). That's the character's actual job, it's
Toki's own formatting rather than model output, and it isn't what looked wrong.

## S8 - decisions from the polish pass (2026-08-06)

**Never shorten a line by shrinking its font.** The note editor drew a blank spacing line at
`FontSize = 6`. Type on one and you got 6px text - and because the line was still tagged `Blank`, and
`NoteMarkdown.Write` emits nothing for a Blank block, the sentence was **deleted on the next save**. A
visual trick had become a data-loss path. `LineHeight` shortens a line; `FontSize` decides how big text
is, and those two jobs must not be conflated. Two supporting rules fell out of it: a kind derived from a
stale tag must be re-derived from *content* (a blank line with text in it is not blank), and because WPF
stamps the caret's formatting onto typed Runs, restyling a paragraph has to `ClearValue` FontSize and
FontWeight on its Runs - otherwise the character inherits the state you just corrected. `LineHeight` must
be cleared on kind change too, or the short height outlives the blank line forever.

**Weight is what makes a font look like a different font.** Every page used the same family and still
looked inconsistent, because the headers had drifted to different weights (Templates 22 Bold vs Settings
22 SemiBold vs note titles Bold). Named styles - `PageTitle`, `PageSubtitle`, `SectionTitle`, `PanelTitle`,
`NoteTitle` - replace per-page numbers, and **Settings is the reference**: SemiBold everywhere, Bold
reserved for deliberate micro-elements (date badges, speaker chips). The notch keeps its own smaller scale
(`PanelTitle`) but the same voice; one system, two sizes.

**A hover state is not decoration, it is the button's hit area made visible.** `PillButton` expressed
hover as an opacity change. That works on a filled pill and does *nothing* on the many buttons that are
deliberately `Background="Transparent"`, so those had no feedback and read as bare text you had to aim at
- which is exactly what the owner reported. The template now has a dedicated overlay layer that tints on
hover/press, so the rounded box appears precisely where the click lands, transparent or not. Related WPF
trap, worth stating once: an **unset** `Background` is `null`, and a null-background `Border` is invisible
to hit testing. Every template border carries `Background="Transparent"` explicitly for that reason.

**Prefer a real `Button` to a `Border` with mouse handlers.** The notch's "All other settings" was a
Border with MouseEnter/MouseLeave/MouseLeftButtonUp on negative margins; its hover box spilled past both
card edges (reading as a rectangle, not a button) and the chevron beside it looked like the control. It is
now a shared `RowButton` - keyboard and automation for free, the hover box inside the card, and the
chevron `IsHitTestVisible="False"` so it can only ever be decoration. The same reasoning already applies
to popup menus (a Border's mouse-up is swallowed by a closing popup).

**Verifying hover needs its own frame.** Hover is invisible to a headless render, so `--libshot` now
emits `hub-nav-hover.png` with the state set directly. Otherwise "the whole row is clickable" is a claim
nobody can check - the same reason `--notetest` types instead of asserting on the serializer.

## T1-T8 - decisions from the first real meetings (2026-08-11)

**Two parallel transcript streams must be RE-ASSEMBLED, not just interleaved.** Toki records the mic and
the loopback separately, transcribes each, and merged them by sorting on start time. That is correct as
an ordering and wrong as a transcript: a listener's "yeah" lands between two halves of the speaker's
sentence, and the recogniser's per-breath segmentation turns one explanation into twelve paragraphs. The
merge now joins consecutive same-speaker segments into turns and displaces short backchannels out of the
middle of a sentence. *The general lesson: when two sources describe one conversation, ordering them is
only the first half of merging them.*

**Where the fix goes is a product decision, not only a technical one.** For mis-heard names the textbook
answer is contextual biasing in the DECODER, and sherpa-onnx supports it for Parakeet TDT. It was
rejected on two facts: the shipped model archive has no `bpe.vocab` (hotwords on a BPE model need one),
and it would force `modified_beam_search` over greedy at real CPU cost. Post-ASR correction wins on a
third argument that had nothing to do with accuracy: it can repair meetings that are **already saved**.
A late fix that is retroactive can beat an early fix that is not.

**Count across the corpus, not the sample.** The same person came out "Craig" on one stream and "Greg" on
the other - one occurrence each, undecidable within the meeting. Across the library it is not close.
Toki's authority for a name is therefore the whole history, overridden only by something a human typed
(the user's own name, a calendar attendee, a pinned term). The sticky-note linking decision has the same
shape: local evidence ties, global evidence settles.

**Asymmetric costs set the thresholds.** Missing a name correction leaves a slightly wrong transcript.
Making a wrong one rewrites what somebody said into somebody else's name. Because those costs are nowhere
near equal, the matching is tuned conservative and refuses dead ties, "Yashtren" is knowingly left
uncorrected, and the escape hatch is a name the user pins - at which point the thresholds widen, because
the risk is now bounded by a human assertion. *Tune to the cost of being wrong, not to recall.*

**A fallback reachable in a state its author did not intend is a bug waiting.** `NoteExtractor.Tldr` -
the first ~140 characters of raw transcript - is a sound floor for a user with no model. It was also
silently used when the model DID answer but skipped the summary, so two consecutive real meetings opened
with "Hello. Uh hi Sai, you are actually on mute." under otherwise good AI sections. The writer now
retries with a summary-only prompt and, failing that, emits **nothing**. An absent section is honest; a
mislabelled one is not.

**Downstream text inherits upstream mistakes silently.** The bad summary produced a bad TITLE, because
the title is generated from the summary. Nothing logged an error; the note simply claimed a 6-person
meeting about report tracking was called "Evacuate Immediate Action Required". When output B is derived
from output A, A's failure mode is B's failure mode.

**A model will format anything you ask it to, including nonsense.** Asked for action items, it turned a
chairperson's register ("Anirud, Bindu.", "Dakshita, Ganesh.") into eleven to-dos that were only names.
The prompt cannot be the only defence; the parser now requires a task to contain an action, and drops
near-duplicates. *Validate the SHAPE of model output, not just its presence.*

**Speaker separation is partly free here, and that changes the design.** Because the mic is its own
stream, the local user is already perfectly diarised - better than general diarisation achieves. Only the
loopback needs splitting, and it arrives with a labelled sample of the owner's voice for reference. That
makes diarisation a smaller problem for Toki than for a single-stream recorder, and it is why
`SpeakerEmbeddingManager` (enroll a voice once, recognise it later) is the piece that matters rather than
clustering quality alone.

**One cause can explain several symptoms.** Segment fragmentation made the transcript unreadable, bloated
the note document to 182 paragraphs, and gave the note writer bad chunk boundaries. Three complaints, one
fix. Worth asking, before building three, whether one thing upstream explains them all.

## UI performance and diarisation - decisions (2026-08-12)

**Fix what fires constantly before what is merely large.** The Library felt slow because `Select()`
rebuilt all 54 cards to move one highlight and `OnSearch` rebuilt per keystroke - dozens of rebuilds a
minute. Virtualization, the textbook answer, would have made ONE rebuild cheaper while leaving the
frequency untouched. Fixing frequency first took a click from 424 ms to 0.4 ms; virtualization was then
justified by a measurement (a build is 424 ms, 81% of it WPF arranging cards nobody can see) rather than
by reputation. Full analysis in `docs/ui-performance.md`.

**A performance claim needs a harness, like any other claim.** `--uitest` runs the Library's real work
against the real database and asserts budgets (16 ms to move a selection - one frame at 60 Hz; 250 ms to
build the list; 400 ms to open a note). "Slow and clunky" became three numbers and a pass/fail, which is
what makes it possible to decide about virtualization at all instead of arguing about it.

**Wiring is part of building.** `NameCorrector` was complete and tested while doing nothing, because the
LIVE transcription path skips `TranscriptionService.Process` and correction lived only inside `Process`.
The fallback path did work, so a test would have passed and the fast path - the one most meetings take -
would have silently done nothing. *When a system has a fast path and a fallback, verify the fast path
first: it is both the common case and the one that hides failures.*

**Reuse the information you already computed.** Standard diarisation sweeps a segmentation model across
the whole file in one-second hops and produces thousands of embeddings. Toki already knows exactly when
every utterance starts and ends, from ASR timestamps - information the textbook pipeline discards and
recomputes. Embedding once per utterance measured **10x faster** on real audio (7.1 s versus 59.6 s for
8.4 minutes) and is published prior art (Turn-to-Diarize, ICASSP 2022), which reports it is also *more*
accurate, because a fixed window straddles speaker boundaries and manufactures confusion errors.

**Complete linkage is the wrong question for "is this the same voice".** It requires every pair in a
cluster to be close, so one degraded moment splits a person permanently: measured, a single remote
speaker became 5 clusters and never fewer than 2 even at a 0.95 threshold. Average linkage asks whether a
cluster is close *on the whole*, and reaches the correct answer of 1.

**Some parameters cannot be tuned to a single value, and that is a design input.** No clustering threshold
serves both a two-person call and an eleven-person one: 0.80 gets the first exactly right and collapses
the second to 2; 0.55 gets the second roughly right and shatters the first into 5. So the design stops
trying: use the calendar attendee count as a prior when it exists, default conservative when it does not
(under-splitting is the status quo, over-splitting invents people who were never there), and expose the
count to the user - affordable only because re-clustering takes about five seconds. *When estimation is
genuinely unreliable, make correction cheap instead of making estimation confident.*

**Check the whole release before copying a model URL.** `sherpa-onnx-reverb-diarization-v1/v2` sit in the
same GitHub release as the safe segmentation model, one line away in any copy-paste, under Rev's
non-production licence. The safe filenames are pinned in `ModelManager` with a comment saying why, so the
next person to add a model does not reach for the neighbouring one.

## T8 decisions - speaker naming without voiceprints (2026-08-12)

Full reasoning and measurements: [`docs/speaker-naming.md`](docs/speaker-naming.md).

**Decision: transient only. No stored voiceprints, ever.** Voice enrollment - keep a fingerprint, name a
person once, recognise them next time - is the obvious design and is what Circleback ships. It was
rejected by the owner. Two reasons carried it. First, it stores biometric data for colleagues who never
agreed to it, which is the fact pattern in *In re Otter.AI* and *Cruz v. Fireflies.AI*. Second, and more
persuasive: **Wispr Flow shipped a bot-free notetaker in August 2026 and made "does not create voiceprints
or biometric profiles" a product promise**, naming speakers from calendar attendees, a saved vocabulary
and conversational context instead. A company with servers and an enterprise pitch decided the trade was
not worth it. Toki already had every signal they use.

Consequence, accepted knowingly: a recurring participant who is not on the invite is renamed each time.
`AppSettings.PinnedNames` covers the people who actually recur.

**Decision: `Speaker` stays a two-value enum; identity lives in `TranscriptSegment.Tag`.** The enum is
which STREAM the bytes came from, which is a fact and is what the `you:`/`them:` search prefixes and
`COUNT(DISTINCT speaker)` depend on. Who spoke is an inference: it can be absent, wrong, or renamed by
the user, so it gets its own nullable column (`transcript.speaker_tag`) and a `Label` property that falls
back to You/Them. Conflating the two would have made a guess look like a fact in the search index.

**Decision: `TranscriptMerger` keys on resolved identity, not the enum.** `SameVoice(a, b)` compares the
tag as well. This is the highest-risk line in the change: every remote participant answers to "Them", so
without it two different people talking back to back with a short gap - the most ordinary thing in a
group call - are welded into one turn attributed to whoever spoke first.

**Decision: the clustering threshold leans conservative (0.80, was 0.55).** Measured, no single value
serves both a one-to-one and an eleven-person call. Under-splitting merges quiet people into one label
and reads as coarse; over-splitting invents colleagues who were never in the room and puts words in their
mouths. 0.55 turned one person reading announcements into six speakers on a real recording. The attendee
count from the calendar overrides the threshold entirely when it is known, which is the real fix.

**Decision: naming is pure text, in its own class.** `SpeakerNamer` takes turns and a roster and returns
labels. No audio, no model, no state. That is what makes it testable against real transcripts, and it is
also the structural guarantee behind the privacy claim - there is nowhere for a voiceprint to live.

**The guard that generalised.** The first real run named participants "Please" and "Otherwise". Rather
than extend the stopword list, the fix is: **a word that appears in lowercase anywhere in the same
meeting is not a name.** An ordinary word always does; a real name essentially never does. It caught two
of three. The third needed a specific rule - the bare vocative is indistinguishable from a discourse
marker, so it only counts for a human-supplied name. Both layers are needed; the principled fix alone was
not enough, and that is worth remembering rather than tidying away.

**Decision: it runs AFTER the notes.** The notes are what the user waits for. Cost is ~7s on an
8-minute recording, ~50s for an hour. The price is that the note writer does not see the names on a fresh
meeting (it does on a rewrite, since `LlmNoteWriter.Chunk` now feeds it `seg.Label`). Getting names into
first-pass notes would require running diarisation before them - open, flagged for the owner.

## T5 decision - virtualizing the Library list (2026-08-12)

`Views/MeetingList.cs`: an `ItemsControl` subclass whose `PrepareContainerForItemOverride` calls the
existing code-built `Card(MeetingSummary)`, so the row-building code is reused untouched - a
`DataTemplate` could not have called it. Measured 424.2 ms -> 46.1 ms at 54 meetings, and the cost no
longer grows with the library.

**The ScrollViewer must live INSIDE the control's template.** An `ItemsControl` wrapped in an outer
`ScrollViewer` is measured with infinite height, every row is realised, and virtualization silently does
nothing at all - the failure mode here is not an error, it is a number that quietly stops improving.
`ScrollUnit=Pixel` keeps `SmoothScroll`'s per-pixel glide; item-unit scrolling would jump a whole card
per notch.

**Drag-reorder was removed to make this safe.** It walked `Cards.Children` BY INDEX, and under
virtualization those are positions in the realised window rather than in the list - the one feature
virtualization breaks without any visible symptom. The owner's call ("it does not add much productivity
as I thought it would") turned a hazard into a deletion. The `sort_order` column and `MeetingStore.Reorder`
are left in place; nothing writes them now, and the "Manual (drag order)" sort mode went with the feature.

## Two-wave notes (2026-08-12)

**Decision: the notes never wait for diarisation; they gain names in place instead.** Owner: *"I don't
like the idea of increasing the wait time, it is already taking a while. Instead, to-do has a loading as
well, and shows up in the window."*

The note is written and shown at the usual speed with "You"/"Them", then a few seconds later the speakers
resolve and the transcript and to-do owners update in place. `App.SpeakersResolved` fires on the UI
thread; the notch's `NotesView` and the Library both re-read the meeting and re-render.

**The loader is load-bearing, not decoration.** While wave two is pending, the two sections that are
about to change show `Views/ThinkingDots` (the same three-dot pulse the notch's Ask view already used,
extracted so there is only one loading language in the product). Without it the note appears to rewrite
itself under the reader, which is more alarming than a slower note - the change needs to be expected
before it happens.

**Decision: to-do owners are derived, not generated.** `Llm/ActionOwners.cs` reads the owner off the
transcript line the to-do's evidence link already points at. No second LLM pass, no added wait, and
deterministic - which matters for something that puts a person's name against a commitment. Only a
first-person commitment names its speaker ("I'll send the deck" -> the speaker; "Craig will send the
deck" -> nobody), because attributing someone else's work to whoever mentioned it produces a to-do that
looks finished and is quietly false. The `UPDATE` is guarded on `owner IS NULL OR owner=''` so it can
never overwrite the model's answer or the user's.

This closed **T4** as a side effect of T8. The generalisation worth keeping: **when a slow step would
delay a fast one, look at what the fast result already knows.** The evidence links had held the answer
since N7.

## Button fundamentals (2026-08-12)

Full write-up: [`docs/button-fundamentals.md`](docs/button-fundamentals.md). Scoped deliberately to
fundamentals - a full design and haptics revamp is coming, so look is throwaway work while hit targets,
centring and clipping are inherited by whatever replaces the styling.

**Decision: `Transparent` is not `null`, and this is now enforced by a test.** A `Background` left unset
is null, and a null-background element is invisible to hit testing. A `TextBlock` with a click handler
paints only its glyphs, so only the glyphs respond - while a parent drawing a hover fill lights the whole
box. Hover looks right, clicking does not, and the highlight becomes a lie about where the button is.
`Views/HitTarget.cs` is the one wrapper for this, and it also clears the cursor on the wrapped content so
the inner element cannot become a second, smaller button inside the real one.

**Decision: no negative side margins on anything clickable.** The About credit rows used
`Margin="-6,2,-6,0"` to bleed their hover fill to the panel edges; the parent clipped it, so 6px on each
side of every row looked pressable and was dead.

**Decision: centre labels on cap height, computed from font metrics** (`Theme/OpticalCenter.cs`). WPF
centres the *line box*, which reserves asymmetric descender space, so a label centred by layout sits low.
The previous fix clamped `LineHeight` to `FontSize` and applied a hard-coded `TranslateTransform Y="-1"`.
That fights the font rather than measuring it, and a constant cannot be right at more than one type size:
measured, every pill label still sat 1.7px low and the smaller calendar buttons 1.5px. The replacement
reads `FontFamily.LineSpacing`, `FontFamily.Baseline` and `GlyphTypeface.CapsHeight`, so the correction
scales with the em and survives a font change.

**Decision: `--hittest` interrogates the live visual tree, not a picture of it.** Both defects are
invisible in a screenshot. The test walks every notch state, collects every `ButtonBase` plus everything
wearing the hand cursor (the cursor is the promise made to the user), and asks
`VisualTreeHelper.HitTest` what would receive a click at nine points inside each one.

**Its own first version was wrong three ways, each worth remembering.** It probed literal corners, which
fall outside a 13px corner radius and reported every healthy pill as broken. It measured centring on
composite rows, where the first TextBlock is a date badge rather than a label. And it judged controls
inside collapsed or clipped containers, which says something about the container. A test that does not
know the shape of what it measures produces confident nonsense - the findings had to be triaged before
any of them could be trusted.

## Claim-by-rename: the sticky migration (2026-08-13)

**Decision: a one-shot migration claims its input file by RENAMING it before reading a byte.**

The old order was read, migrate, rename. That is check-then-act, and it is not atomic across processes.
Toki's single-instance guard stops a second window, but `StickyMigration.RunOnce` executes on a
background thread three seconds after startup - so two launches in quick succession really did overlap.
Both saw the file, both parsed it, both wrote the same note. The owner's log showed
`sticky: migrated 1 prep-card note(s)` three times in under a second: three copies of one note.
`StickyForEvent` cannot prevent that; it is the check half of the very race it is trying to stop.

Renaming is the one operation the filesystem makes atomic. Whoever wins `File.Move(path, path +
".migrating", overwrite: false)` owns the migration; every loser gets `IOException` and returns having
done nothing. The generalisation: **to make a one-shot job idempotent across processes, claim the input
atomically rather than checking whether the work was already done.**

**Decision: an input that can never be parsed is PARKED, not retried.** The old catch left an
unparseable file in place "to be retried next launch", which sounds careful and is actually a permanent
error: a malformed file will still be malformed tomorrow. It logged
`'t' is an invalid start of a property name` on every launch, forever, with no path to success. Now it
moves to `.unreadable` with a log line saying where the text went. Distinguish a **transient** failure
(a locked database - put the file back, retry later) from a **permanent** one (bad bytes - stop trying
and say so). Treating the second as the first produces log noise that trains everyone to ignore the log.

## Pinned names get a UI (2026-08-13)

`AppSettings.PinnedNames` was read by `NameLexicon.Trusted` from the day T2 shipped, and no code path
could write it. A trusted-names roster that only a JSON editor can fill is not a feature. It now has a
multi-line box in Library → Settings, beside the user's own name, sharing that section's
Save/Cancel/confirmation pattern. The confirmation counts what `NameLexicon` actually **parses**, not
what was typed, so a trailing comma or a blank line shows up as the wrong number rather than silently
doing nothing.

Worth noting as a smell: a setting that is read but never written is dead weight that looks like a
feature. It was in the handoff notes as "exists and is already read - there is no UI" for two sessions
before anybody could use it.

## Focus after layout: the sticky note that ate keystrokes (2026-08-13)

**Decision: never call `Focus()` in the same breath as adding a control to the tree.**

`MeetingStickyView.Reveal` created the editor, assigned it to the panel's slot, and immediately called
`FocusEditor()`. WPF's `Focus()` on a control that is not yet in a laid-out visual tree returns false and
does nothing - **silently, with no exception and no log**. Keyboard focus therefore stayed on the
read-only `RichTextBox` that HOSTS the sticky panel, and a read-only RichTextBox swallows every keystroke
without a sound. The user clicked "Add a sticky note", the panel opened, and typing went nowhere.

Focus now happens at `DispatcherPriority.Loaded`, and a `PreviewMouseLeftButtonDown` on the panel puts the
caret back in the note - the host RichTextBox wants focus back and will take it on any click that lands
on the panel's padding or header. The panel's `Background` is `Transparent` rather than unset for the
same reason as every other clickable surface: unset is null and null is invisible to hit testing.

**The generalisable part is about the TEST, not the fix.** `--notetest` drives the note editor through 20
checks and passed every one of them while this feature was unusable, because it builds the editor
standalone. The editor was never broken - the HOSTING was. New `--stickytest` builds a real meeting
document, puts the sticky panel inside it, opens it the way the button does, and asserts that focus
actually lands in the editor. It reproduced the bug on its first run. **When a control works in isolation
and fails in the product, test it in the product.** Same lesson as the hover-contrast bug the day before,
and the third time this month that a green test was measuring a model rather than the thing.

## A choice from a set belongs in a list (2026-08-13)

The calendar's meeting-length control was a chip that CYCLED `30 -> 60 -> 90 -> 30`. Two consequences,
both reported by the owner as "impractical": **15 and 45 minutes were unreachable** no matter how many
times you clicked, and reaching a length you wanted meant clicking through lengths you did not. It is now
a dropdown of 15 / 30 / 45 / 1 hour / 1 h 30 m / 2 hours, with a chevron so the control reads as a choice
rather than a toggle.

The same reasoning collapsed the note editor's three Title / Heading / Body chips into one "Aa" size
picker: three chips spent the toolbar's whole width and still read as three unrelated buttons rather than
one property with three values.

**Rule: if a control has more than about three values, or any value it cannot reach, it is a list - not a
button that advances.** A cycling control also hides its own options, which is how four of six lengths
went missing in plain sight.

## Why Bold / Italic was NOT added (2026-08-13)

Requested, and deliberately deferred - recorded here because "we didn't get to it" and "this is dangerous
to rush" are different things and the difference matters next time.

`NoteMarkdown` has no inline formatting at all, so bold needs a `**` round-trip through save and reload as
well as toolbar buttons. More importantly it collides head-on with an existing fix: `ApplyStyle` clears
`FontWeight` on every Run, deliberately, because WPF stamps the caret's formatting onto typed Runs and the
resulting rule was "the LINE decides how its text looks, always". That clear would wipe user-intended bold
on every restyle, so shipping bold means first teaching the editor to tell deliberate formatting from
stamped formatting.

Both previous defects in this control were DATA LOSS (bullets multiplying on every save; blank lines
eating a typed sentence). It holds the user's own words. That is a control where a half-finished feature
costs more than a missing one.

## A template is a claim, not a layout (2026-08-13)

**Decision: the model proposes a note template; the transcript has a veto.** `Llm/TemplateGate.cs`.

"Decision log" does not describe a shape, it asserts that a decision was made - and then asks the
model to fill in "Options considered", "Decision" and "Rationale". Both of the first real meetings
were classified that way and neither decided anything, so Rationale came out as a justification for
something that never happened. The note did not look wrong. It looked authoritative, which is worse.

Every non-General template now has to clear a deterministic evidence check against the raw transcript.
Failing is not an error and is never surfaced: it falls back to General, whose only section is "Key
points", which asserts nothing about the meeting and therefore cannot be wrong about it.

The gates are deliberately **loose about what counts as evidence and strict about needing some** - the
same asymmetry that sets `NameCorrector`'s thresholds. A missed specialisation costs a slightly
plainer note; a false one puts words in people's mouths.

Two of the gates use facts rather than words, which is why diarisation was worth having: "1:1" needs
exactly two distinct voices, and "Lecture" needs one voice carrying 80% of the words.

## What Toki rewrote is now part of the record (2026-08-13)

**Decision: store the edits, show them in the transcript only.** `MeetingRecord.Corrections`, a
`corrections_json` column, rendered as a dotted underline with the original on hover.

Toki quietly rewrites two things - names it unifies against the library, and the discarded half of a
spoken self-correction. Both make the transcript easier to read and both make it a slightly edited
record of what a person said. A transcript that silently disagrees with the room is the one artefact
Toki cannot afford.

The wording is honest at MEETING level rather than occurrence level: Toki stores the pair it changed,
not which instance it changed, so the tooltip says "also transcribed as" - true of every occurrence -
rather than claiming this particular word was wrong.

Notes stay unmarked (owner's rule). A note peppered with hover marks is harder to read, and the
transcript is where anyone goes to check a claim anyway.

## Re-cleaning is offered, never done (2026-08-13)

**Decision: a button in Settings, and it never touches a note.** `Storage/TranscriptCleanup.cs`.

Every improvement to turn-merging or name correction makes older transcripts look worse by
comparison, and the tempting move is to fix them quietly at the next launch. A transcript is a record
of what people said; silently rewriting one the user has already read - possibly quoted, possibly
acted on - is not a favour.

It rewrites the transcript and nothing else. Summaries and to-dos are left exactly as they are, even
where the transcript underneath them changed: re-writing them would need the model, would take
minutes, and would throw away edits and tick states a person owns. A slightly stale summary over a
cleaner transcript is the right trade; the reverse is not.

## The size dial, and why it is not a point size (2026-08-13)

**Decision: 1 to 12, scaling the whole note, not a size on a selection.** `NoteEditor.TextSize`.

The owner asked for a size control with more than three values. The obvious build - a point size
applied to the selection, like Word - is the same build that is currently blocked for Bold and Italic,
for the same two reasons:

1. **Markdown cannot record it.** The note is stored as plain Markdown so it survives in Obsidian and
   in the file mirror. "This word is 14pt" has nowhere to live, so a per-selection size could not
   survive a save.
2. **Per-Run formatting on this surface caused data loss once already.** S8's mosquito-text bug was a
   line whose Run carried its own FontSize, outranking its paragraph and then being written out as an
   empty block. The rule since then is that the LINE decides how its text looks.

So the ramp stays semantic (Title / Heading / Body - real Markdown, travels with the file) and the
numeric dial sits on top of it, scaling body, headings, gutter and leading together. It is stored per
PERSON rather than per note, because somebody who wants larger text wants it in every note.

## A note page is a document, not a form (2026-08-13)

**Decision: a capped 720px measure, centred.** `LibraryWindow.MeasureWidth`.

The owner's report was that the manual-note page looked machine-made. The causes were structural:

- It ran the full width of the pane. Typography's rule of thumb is 45-75 characters a line; past that
  the eye starts losing its place on the carriage return. Every word processor solves this with a
  fixed measure and margins, and that is most of what makes a page look like a page.
- A hard rule sat under the title, which is what a FORM does between fields. A document separates its
  title from its body with space and puts the quiet facts underneath.
- Delete sat immediately after the last line of writing, so the document appeared to end in a button.

Two rendering fixes came out of the same pass. **List markers are set RIGHT in their gutter** with one
fixed gap, because left alignment gave a tick box and a four-pixel bullet completely different
relationships to their own text. And **blank lines honour their line height** - WPF's default
`LineStackingStrategy` is `MaxHeight`, which takes the LARGER of the requested height and the font's
natural height, so a 9px beat was silently rendering at 17px and every section drifted apart.

## Two calendars at once (2026-08-13)

**Decision: Google and Outlook are independent connections, not a choice.** `CalendarService`,
`MicrosoftAuth`, `OutlookClient`, `CalEvent.Provider`.

A work Outlook calendar and a personal Google one is an ordinary combination, and a notetaker watching
only one of them misses half the meetings without ever saying so. Both can be connected; the agenda is
the two merged and sorted; connecting or disconnecting one never touches the other. One provider
failing does not empty the agenda - the exception is only rethrown when NOTHING came back.

`CalEvent.Provider` is what makes writes safe. An event id from Graph means nothing to Google, and
sending one to the other fails in a way that looks like a broken Edit button rather than a routing
mistake. It defaults to Google so every existing construction site keeps meaning what it meant.

**Three ways Graph differs from Google, each of which bites:**

1. **`/me/events` returns the series MASTER of a recurring meeting**, once, with a recurrence rule to
   expand yourself. `/me/calendarView` takes a range and returns instances. Use the wrong one and a
   daily standup appears once, on the day it was created.
2. **Timestamps arrive with NO offset**, plus a separate `timeZone` field. Parsed like RFC3339 they
   read as local - a 2pm meeting shows at 7:30pm in another zone and nothing looks broken. Toki sends
   `Prefer: outlook.timezone="UTC"` and attaches the zone explicitly rather than assuming.
3. **Microsoft rotates the refresh token on EVERY refresh** and invalidates the previous one. Storing
   what comes back is not an optimisation here: skip it and the connection dies at the next restart.

**No client secret.** Microsoft registers a desktop app as a public client, where a secret is not
merely unnecessary but rejected - PKCE is the whole proof. That removes the one piece of the Google
setup that looks like a secret and isn't.

## Evidence audio: Otter's usefulness, Granola's caution (2026-08-13)

**Decision: playing is free and silent; exporting warns once and is a separate act.**
`Storage/EvidenceAudio.cs`, `Views/EvidencePlayer.cs`.

Researched before building, as the owner asked:

- **Granola keeps no audio at all.** It streams to a transcription service and deletes the audio the
  moment the text exists, and markets that as the privacy feature - which it is. Its evidence links
  can therefore only ever reach the transcript. You can check the model against what was written down,
  never against what was said.
- **Otter keeps the audio, in its cloud.** A shared snippet is a hosted LINK Otter continues to
  control; the audio never leaves as a file, and revoking the link revokes the access.
- **Toki already holds the audio on the user's own disk, forever, at zero cost.** That answers the
  loudest complaint in this category - "no way to verify what the AI claims" - without a server. It is
  also a real liability, because a local file has no revoke.

Hence: playback asks nothing (it is the user and their own disk), plays a **window with a run-up**
rather than the meeting, and there is deliberately no "play the whole recording" affordance. Export is
behind a right-click, exports exactly the window that was played through the same code path, and warns
once in plain words. WAV rather than MP3 - an encoder is a dependency and a licence question to ship
one clip.

**The text jump is kept beside it, not replaced.** Reading is instant, silent and works in an open
office; audio is slower and not always socially possible. They answer the same question at different
costs, so the line offers both.

## The sticky note leaves the document (2026-08-13)

**Decision: an editable control is never hosted inside a read-only RichTextBox's document.** The
meeting note splits at the sticky into three siblings - notes document, the real `NoteEditor`, the
transcript document - under one ScrollViewer, each document's own scrolling disabled so the page
stays one surface.

The nested version failed three documented ways at once: FlowDocument-hosted elements are disabled by
design unless `IsDocumentEnabled` intervenes; the outer TextEditor claims keyboard focus on mouse-down
even when read-only; and it registers class-level handlers for the entire editing key vocabulary. No
caret, no keystrokes, no selection - each symptom a different mechanism, which is why point-fixes kept
passing their own tests while the product stayed broken. The split is the standard fix in the
literature, and the sticky self-test now asserts the STRUCTURE (no RichTextBox ancestor) rather than
focus, because the structure was the bug.

Two costs, both accepted: drag-selection cannot cross the sticky (it could before - into an editor
that could not be typed in, which was not a feature), and the two documents forward their mouse wheel
to the outer scroll (a scroll-disabled RichTextBox still eats the wheel).

## Evidence audio: the range, the snap, and the guarantee (2026-08-13)

**The "That." bug:** `Evidence` scored a window of up to three transcript lines but returned only the
first line's index, and the first line of a winning window can contribute NOTHING to the match - the
union collects its words from the lines after it. A real meeting's clips played one stray word while
the evidence sat unplayed. `BestRange` now returns the span, trimmed at both ends to lines that hold
matched words, and refuses if no single line does.

**The cut rule** (from Descript's edit-point patent + transducer timing realities): never cut at a raw
ASR end-time - Parakeet's TDT emits a token before its sound finishes, so end-times run early. 300ms
lead, 700ms tail, and when the same stream's next line is known the cut snaps to just before it -
into the silence, never into the next sentence.

**The guarantee** (owner's rule, verbatim: "only show audio if it's guaranteed present"): a play
button renders only when the range is one stream, timestamps are monotonic and non-degenerate, the
moment clears 1.5 seconds, and the WAV header proves the file covers the window. Checked at render
with cached headers; a button that appears is a button that works.

Considered and rejected for now: Otter's seek-and-continue model (position the playhead, keep
playing), which eliminates the end-timestamp problem entirely by never bounding playback. It is the
stronger design for a media-player surface; Toki's evidence affordance is deliberately a bounded
moment, and the bounded version now earns its bounds. Revisit if a full playback surface (#60's
original framing) ever ships.

## The home card gets a hierarchy (2026-08-13)

**Decision: one hero, quiet secondaries, setup as an errand.** The 2x2 grid (Calendar + Take Notes
over Connect Google + Connect Outlook) put a once-ever setup task at the same visual weight as the
product's core verb - equal size asserts equal importance, and symmetry forced four slots regardless
of state. Researched pattern across Granola, Loom, Krisp, Wispr Flow, Fantastical and Raycast: one
dominant CTA, content in the middle, secondary destinations subordinate, account setup as a row that
exists only while unconnected. Home is now: Take notes (hero) → Calendar (quiet pill) → COMING UP
(content) or one "Connect a calendar ▾" row offering both providers → footer with provider marks and
the Library link. The connect row deletes itself the moment a calendar connects.

## The redesign lands as tokens first (2026-08-13)

**Decision: the Figma redesign (288:4) enters the codebase as a DESIGN SYSTEM before any screen.**
New theme tokens carry the material - Glass/GlassOpaque (the rgba(33,30,30,.5) card and its no-acrylic
stand-in), GlassCapsule for inner rows, GlassBorder (the 1px white-10% rim), TextHi/TextMid/TextLow
(text as white opacities, never grey hexes - what keeps type legible over any glass), the accent set,
JoinPill/JoinPillText, StopRed. Each has a contrast-checked light-theme counterpart so the material
survives the theme flip.

Why tokens first: the file has 16 notch states and three full windows, and restyling them screen by
screen without a shared vocabulary is how a design drifts apart mid-migration. With the tokens in,
each surface migrates by re-pointing at them - and surfaces not yet migrated keep their old brushes
untouched.

Also decided: the notch's two surface modes (acrylic on/off) now share ONE look - the glass tint and
rim on both, with the opaque brush approximating the glass reading. The pitch-black slab is retired;
the redesign's card IS the notch.

## The motion constitution, and why the mascot was software-rendered (2026-08-14)

**Decision: motion, haptics and glass rules live in a project skill** -
`.claude/skills/motion-and-feel/SKILL.md` - written from research (Apple HIG springs, Material 3
motion tokens, IBM Carbon, Emil Kowalski's animation standards, Duolingo's Rive mascot practice,
the WPF rendering docs) and applied on every visual change. The tightest rules: animate only
transforms/opacity/clip (never Width/Height/Margin, never the HWND when avoidable); enter =
strong ease-out, exit = accelerate at ~70% of the enter's duration; press = the whole box to 0.97
in 90ms, spring back in 200ms; overshoot 2-5% max; no Effect on anything that animates.

**The root cause of the "hard and ugly" rendering was that last rule.** A WPF `DropShadowEffect`
renders its whole subtree in SOFTWARE and re-renders on every change. TokiBlob carried three
(body, both eyes) inside a subtree that breathes and blinks continuously - the mascot was a
software render loop. The notch's shadow caster had the same disease during morphs: the window
resize resized the caster every frame, re-blurring in software 60x a second. Fixes: shadows became
cheap gradients/static seats inside the blob; the caster hides during any morph/reveal and fades
back on settle (which also reads as the panel "settling into place").

**Curves are code, not adjectives.** `Theme/Motion.cs` gained `BezierEase` (CSS-semantics cubic
bezier, Newton solve) and three named curves: ArriveStrong (0.23,1,0.32,1), Depart
(0.3,0,0.8,0.15), Drawer (0.32,0.72,0,1 - the iOS-sheet settle; the notch morph and reveal ride
it). WPF trap recorded: `EasingFunctionBase` defaults to EaseOut, which MIRRORS a curve that
already encodes its full shape - BezierEase pins EasingMode=EaseIn (the raw passthrough).

**The mascot is placed per state, by the frames.** `PlaceMascot` glides the (now 58px) bubble on a
composition translate: centred on compact pills, bottom-left overlapping the corner of the expanded
recording panel, level with the header on tall panels. Recording surfaces hug the top (`TopGap` 2px
vs the hover's 12px) - a status strip, not a floating panel. Aliveness is layered and prioritised:
reaction > fidget > idle; fidgets (glance/nod/wobble/double-blink) fire every 30-60s only in the
Idle mood - a wobble mid-scribble would read as a glitch, not charm.

## One mascot per surface, and the design-system mirror (2026-08-14)

**Decision: there is ONE Toki on any surface - the floating bubble beside the card.** The owner's
live check found the push-to-talk panel showing two (the panel's own inline blob plus the new
floating one). Every panel-state view now HIDES its inline blob (Ask, RecordingOpen, Summarizing,
Onboarding; the Calendar's two empty-state blobs became a quiet calendar glyph) - hidden, not
deleted, so the poke/animation wiring survives and a hidden TokiBlob pauses itself. The exception
that proves the rule: PEEK states (collapsed summon, recording bar) hide the floating bubble, so
their own tiny Toki IS the mascot there - which is why the recording bar's blob now carries the
Writing mood itself.

Geometry settled the same day: the bubble gets a clean 10px AIR GAP to the card (CardPadL 68 =
58px bubble + 10px; the old 4px overlap read messy), and the hover-summon peek is FLUSH with the
top edge (TopGap 0 for Collapsed - it drops out of the screen edge; a gap there read "too low").

**Decision: Toki's design system lives on claude.ai/design as a hand-authored React mirror**
(`design-web/`, 15 components; project pinned in `.design-sync/config.json`, gotchas in
`.design-sync/NOTES.md`). The WPF theme is the source of truth; the mirror is a sketchpad for the
design agent and a shareable reference. The owner's verdict after using it: the FIGMA FRAMES stay
the boss - adopted screens (Ask, Settings) are implemented in WPF from the frame, never ported
from the agent's HTML. The mirror drifts silently when Theme.xaml changes; port token changes by
hand and re-sync.

## The capsule states, and one card that had to get out of the way (2026-08-14)

The owner supplied the raw Figma export of the whole board (`css code for all the design layers.txt`,
46,687 lines, 23 screens; moved 2026-09-03 to `docs/design/figma-layer-css.txt`, see
`docs/design/README.md`). Building its six "Menu Bar" frames forced two structural decisions.

**Decision: some states draw their own capsules; the window's card steps aside.** Every notch state
so far has been content laid on ONE glass card that the window draws. Three of the menu-bar frames
are not that shape: "Record This Meeting" is a 169x40 pill with a separate 40px mascot circle 12px
to its left, and "Idle" is two 144x40 pills with a 12px gap and a close puck floating past the right
edge. One card cannot express two capsules with air between them - laying glass pills on a glass
card gives you glass-on-glass, and stretching one card across both loses the gap that IS the design.
So `PillWindow.DrawsOwnCapsules` names the states whose view supplies the visible surfaces, and for
those the card drops its fill, rim, sheen and shadow caster. Toki's bubble stays (unlike a peek):
the frames still draw him beside the pills.

**Decision: a view may own its card width.** `Sizes` gives every state one fixed width, which was
right until the recording state became two frames - the compact bar ("Menu Bar - Transcribing", 320)
and the transcript panel ("Live Transcription (Expanded)", 344), with a chevron moving between them
and no state change in between. `IWidthAware` lets the cached view have the last word; `CardSize()`
is now the single place all three sizing paths ask. `GrowToContent` had to start comparing WIDTH as
well as height, or a change that only widened the card was swallowed by its early-out.

**Decision: "Notes ready" is a tucked appearance, not a state.** The frame gives a finished note its
own pill. Making it a PillState would mean threading it through the hover pairs, the context memory,
the idle/auto-hide timers and the reveal path, and would risk losing the note's scroll position and
its Keep-or-Discard choice on every tuck. Instead the state stays `Notes` and only the Host content
swaps while tucked (`ShowNotesReady` / `RestoreTuckedView`). This also fixes a real ambiguity: a
ready note and a working loader both used to tuck into an identical blank 13px strip.

**The frames' widths are not our widths, and that is on purpose.** Three frames reserve 64px of left
padding INSIDE the pill for a mascot that overlaps it. Toki floats BESIDE the card here - the owner
explicitly replaced a 4px overlap with a 10px air gap the day before - so those 64px come off the
card and the content row keeps the frame's own 242px. What sits between the rounded corners is the
frame's row at the frame's spacing; the overlap is the one thing deliberately not ported.

**Lesson: a render harness with its own width table will draw a layout the app does not have.**
`--render` hardcoded 288px for the meeting prompt and kept drawing a card behind it, so the PNGs
showed a wide box around a 169px pill. The shot widths now come from the same numbers the window
uses, and `Shot(bare:)` asks `PillWindow.IsCapsuleState` whether to draw a card at all. A harness
that renders a *model* of the product is the failure mode this project keeps paying for.

**Lesson (build hygiene): `src/Toki/bin/Debug/` has two output folders.** A live
`net8.0-windows10.0.19041.0/` and a stale `net8.0-windows/` from an older target framework, last
written 15 July. A recursive search for `Toki.exe` returns the July one first; it hangs on startup
after the "llm runtime" log line and reads exactly like a regression in the code you just wrote.
Half an hour went into bisecting a hang that was a stale binary. Always run the TFM-suffixed path.

## Where the notch's frames actually go, measured (2026-08-14)

The owner asked why the notch struggles to render clean, smooth animation, especially the mascot.
Three plausible answers were on the table and they need opposite fixes, so `--fpstest` was written
to settle it instead of reasoning about it. It runs the same animated mascot in three windows and
counts frames: transparent (the notch), opaque (the control), and transparent-while-resizing (what
a state change actually does). On the dev PC, 120Hz, render tier 2:

| case | avg fps | worst frame | frames over budget / 4s |
|---|---|---|---|
| transparent window, static bounds | 116 | 12-16 ms | - |
| opaque window, same content | 118 | 11-17 ms | - |
| transparent window, animating its own size | (meaningless) | **39-48 ms** | **56-76** |

**Two theories died.** Transparency costs about 1%: `AllowsTransparency=True` was assumed to be the
culprit, and it is not, at least on a tier-2 machine. The mascot's vector art costs about 2%: the
gradient-filled geometry is not too heavy to animate, so BitmapCache would have bought nothing and
would have cost quality (a cached bitmap scaled down to 44px is worse than the vector drawn at 44px).

**What is left is the WINDOW MORPH.** Animating `Window.Width`/`Height` - which every state change
does, via `AnimateSize` - drops 56 to 76 frames every four seconds, roughly fifteen visible stutters
a second, with worst frames three to four times the 8.3ms budget. Average fps in that case is a lie:
it reads *higher* than the refresh rate because layout churn fires extra `CompositionTarget.Rendering`
events. The eye does not average; it sees the 48ms hitches.

The cause is not throughput. Each frame the window's bounds are set, rounded to whole device pixels,
and the window re-centred by `PlaceTopCenter` on `SizeChanged` - a Win32 window move plus a layered
surface reallocation, per frame, on the UI thread, in the middle of a layout pass.

**The fix is architectural, and it is the same one the Dynamic Island and every good Electron
overlay uses: hold the window at a FIXED size and animate the CARD inside it.** The window is placed
once and never moves during a transition; the card's size, clip and transform do the work on the
composition thread. `PinContentForMorph` (added the same day, for the reflow problem) is the first
half of this - the content is already laid out once at its final size and revealed through an
animated rounded clip. The second half is to stop the window's own bounds moving at all.

The obstacle is that everything positional keys off the WINDOW rect: `PointInWindow`,
`CursorOverPill`, `_rectL..B`, the wheel hook, the click-outside dismissal. A fixed oversized window
would report the pointer as "on the notch" from 200px away, and a `Background="Transparent"` window
swallows clicks across its whole area. So the move has to come with a card rect that all of those
consult instead. `CursorOverVisiblePeek` is the pattern: derive the visible rectangle from the pads
rather than trusting the window.

**Lesson: measure which layer is slow before optimising any of them.** The instinct was to blame the
transparent window and cache the mascot. Both would have been wasted work, and the mascot cache
would have made it look worse. One 4-second measurement pointed at a layer neither of us suspected.

**The fix, and what it bought.** `AnimateSize` no longer animates the window. It sets the window
ONCE to the UNION of where it is and where it is going, holds it there for the whole transition,
and animates a rounded clip on the card instead - centred horizontally, top-anchored, with Toki's
bubble tracking the clip's left edge on the same curve so the air gap between them stays constant.
The content is laid out once at the union size and never re-laid-out. One further resize settles
onto the exact target after the motion ends. Two window resizes per transition instead of ~120.
The reveal-from-hidden path already worked this way; this brings the morph in line with it.

**What the evidence does and does not support.** The synthetic rows in `--fpstest` are solid and
reproducible: a transparent window animating its own bounds drops 56-76 frames every four seconds,
while the identical content in a window with static bounds drops effectively none. That is the
finding, and the rework follows from it directly - the number of window resizes per transition goes
from about 120 to two, which is a property of the code rather than of a measurement.

**The live-notch number could not be made to prove it, and an earlier claim of a "36-54%
improvement" is WITHDRAWN.** An interleaved A/B against the real notch produced that figure across
four runs; a later standalone measurement of the same build reported 6.3 dropped frames per
transition where the interleaved run had reported 1.8. Two harnesses, one build, a 3x disagreement.
Three hypotheses for the gap were tested and all three were wrong: a double resize in the in-place
path, the window's Background brush, and an added UpdateLayout call. None moved it.

The reason the live number resists is that it is dominated by the VIEW SWAP: every state change
clears `Host` and lays out a fresh page, tens of milliseconds that have nothing to do with the
morph. The animation's contribution is small enough to sit inside that noise. So the live row is
kept as a REGRESSION ALARM (if it doubles, something got slower) and is explicitly not a benchmark.
Reducing the view-swap cost - caching arranged views rather than rebuilding them - is the next real
lever, and a bigger one than the morph was.

**Lesson: a sequential A/B measures the minute, not the code.** The first version ran the old path
for four seconds, then the new one, and reported -16%, -124% and +72% on three consecutive runs of
identical code. Interleaving the conditions inside one run, alternating every whole cycle, made it
look reproducible - but "reproducible" is not "correct", as the standalone disagreement then showed.
Alternating every TRANSITION would have been worse still: it hands one condition all the shrinks and
the other all the grows, a confound that would have looked exactly like a result.

**Lesson: state what a measurement supports, not what you hoped it would.** The architectural
argument here stands on its own. Dressing it in a percentage that later failed to replicate added
nothing to it and cost credibility.


## Contrast is arithmetic, not taste (2026-08-14)

Owner: "fix the text colors, it kinda blends in with the background, both for white and black
themes". Rather than nudge colours by eye, every text token was composited over every surface it can
land on and scored with the WCAG ratio. Three separate faults came out, and only one of them was
what the report sounded like.

**1. The light theme's accents were never checked.** AccentAmber measured 2.42:1 on a light chip,
AccentTeal 2.74:1, TextDim 2.94:1, and light `TextMid` 3.83:1 - under the 4.5 bar for body text.

**2. Three accents had no light value at all.** Coral, Pink and Purple were documented in
ThemeManager as "constant brushes that don't invert". They are pastels tuned for a near-black card;
painted onto a near-white one, Pink measured **1.66:1**. Not dim - invisible. Any colour ever used
as TEXT needs both ends of the palette table, and that is now written above the table.

**3. The real one: the card's own opacity.** The frame specifies rgba(33,30,30,0.5), so the card is
half whatever is behind it. Over a bright window the "warm dark glass" composites to mid-grey and
white-90% text falls to **2.96:1** - failing even the 3:1 non-text bar. No text colour can fix a
background that ranges from near-black to grey. This is why the report said BOTH themes: each one
fails against the opposite wallpaper. The card is now 85% (`#D9`), which still reads as glass - the
desktop shows through - while holding a floor of 8.7:1 for primary text over any wallpaper.

Secondary text went from 50% to 65% opacity and tertiary from 25% to 45% (both `TextLow`s were at
1.8-2.3:1, which is decoration nobody can see). Every accent was then re-derived by walking its HLS
lightness until it cleared 4.5:1 on the glass card over black, grey AND white, with hue and
saturation preserved so the "one meaning, one colour" mapping is untouched. `CoralText` is new:
white on the dark theme's coral measured 2.73:1, so a filled coral button now carries a near-black
label in dark and a white one in light.

**This is now enforced, not remembered.** `ContrastTests` reads the palette straight out of
`ThemeManager.cs` and fails the build if any text colour drops under 4.5:1 on any surface in either
theme, or under 3:1 for decoration, including against the translucent card over three wallpapers. It
also asserts the parse actually found the palette, because a check that silently reads nothing
passes perfectly while measuring nothing.

**Lesson: when something looks invisible, measure the contrast before debugging anything else.**
This is the second time the project has paid for that. In August 2026 a 12% white hover wash on a
black notch (1.15:1) was diagnosed for hours as a broken input layer. The same mistake in a new
costume cost another session here. A ratio takes a minute to compute and settles the argument.

**Lesson: a translucent surface has no fixed contrast.** It depends on the user's wallpaper, so a
token can look correct against the opaque stand-in in every screenshot and still fail in use. Any
see-through surface must be checked composited over both extremes, and its alpha is a contrast
decision as much as an aesthetic one.


## A captured brush never hears about a theme switch (2026-08-14)

The contrast pass above fixed the palette, and the light theme still shipped white labels on
"Start a meeting", "Take notes" and "Record this meeting". The palette was right; the views were not
reading it.

**The cause.** Those pills are built in CODE, and the natural thing to write is
`Foreground = (Brush)Application.Current.Resources["TextHi"]`. That hands back the brush that exists
at that instant. `ThemeManager.Apply` switches themes by REPLACING the resource, not by mutating the
brush (it cannot - WPF freezes a brush once it has been rendered). So a captured brush is a snapshot:
correct when the view is built, wrong forever after a switch. XAML's `DynamicResource` does not have
this problem, which is why the same views look right in a screenshot taken with `--render --theme
light`: that process STARTS in light, so the snapshot is taken from the right palette.

The rule: **in code-built UI, bind themed colours with `SetResourceReference`, never by reading the
resource.** `PillParts.Themed()` is the helper; `LiveWave`, `NotesReadyView` and the live-transcript
rows were converted with it. Fixed brushes are still fine where the theme must NOT repaint them -
the white square on the red stop disc, a speaker's initial on its tinted disc - and `PillParts` now
has two `Solid` overloads to make that choice explicit rather than accidental.

**The test that missed it, and what it looks like now.** `--themetest` already existed and passed
throughout: it created one `Border`, wired it up with `SetResourceReference`, and checked it
recoloured. That proves the MECHANISM works on an element somebody wired correctly. It says nothing
about whether the app's own views use it - testing the model instead of the product, again.

It now walks every notch view and every label in it, computes the contrast of each against whatever
is actually painted behind it, and fails on anything under 3:1. The critical detail is the ORDER:
the views are built ONCE, in dark, and then the theme is switched under them. Building a fresh view
per theme passes even with the bug present, because a constructor that snapshots the palette
snapshots the RIGHT one. The bug only exists for views that outlive a switch, which is exactly what
the notch's cached views are. The check was written that way first and would have been useless.

It was then validated by putting the bug back deliberately: the sweep named all three labels at
1.26:1. A check that has never failed on the defect it exists for is not evidence.

**Two false alarms worth remembering**, both from asking WPF what the background is:
  · A templated `Control.Background` carries whatever the default style put there (#FFDDDDDD, the
    classic grey button) even when the custom `ControlTemplate` never paints it. Only `Border` and
    `Panel` fills are real.
  · A hover overlay (#38FFFFFF) is not a background. Anything under ~75% alpha is a wash that is
    absent most of the time; skip it and fall through to the surface underneath.


## Making a meeting is a mode, not a banner (2026-08-14)

The calendar composer was reported as "so so so bad", with four specific faults. All four had the
same shape: a layout decision that was never checked against the panel it lives in.

**Two hairlines under the header.** The header drew one, and the composer's own top border drew
another 12px below it. Two tram-lines across the panel the instant you pressed New.

**Cancel and Create fell off the bottom.** The composer was docked ABOVE the agenda, so its height
came out of the list's - but the calendar panel is a fixed 430 tall, and a full form does not fit in
what is left. It is a MODE now: the composer takes the whole panel, the agenda hides, the header
reads "New meeting", the New button steps aside, the fields scroll, and the two buttons are pinned
to the bottom where nothing can push them off.

**The fields were six boxes at four different heights.** A filled 38px box, a filled 36px box, an
OUTLINED 36px chip, another filled box, a bare switch. They are all the same kind of thing - a
property of the meeting - and now look like it: one card of 46px rows split by hairlines, label
left, value right, the same shape as the Library settings page. The end-time chip stopped being an
outlined control and became a row like its neighbours.

**Two competing sentences under the title.** A permanent hint ("Type a day and time, or pick them")
and a separate green parse preview, stacked. The hint is the empty state now; once the preview
exists it takes the line. An error outranks both, and has to re-show the hint explicitly - hiding a
line and then reporting a failure into it is a bug waiting to be filed.

**The cause of the half-width card, which took two wrong fixes to find.** The composer was a bare
child of the root DockPanel with no `DockPanel.Dock` set, and that silently means `Dock=Left`: it
was handed only its desired width, so the field card sized to its widest row and sat at about 56% of
the panel with dead space beside it. `HorizontalScrollBarVisibility` was blamed first, then the
ScrollViewer's measuring. Neither was it. The composer and the agenda are mutually exclusive, so
they now share the one filling slot at the end of the DockPanel.

**The new theme sweep earned its place within a minute.** Adding the composer to `--themetest`
immediately failed it: `ParsePreview.Foreground = (Brush)FindResource("Green")` captures the brush at
parse time, so a preview rendered under one theme kept that colour into the other - the light
theme's green on the dark card, at 1.98:1. Same bug as the pill labels, in code written long before
them, found by a test that was written an hour earlier and had never seen this view.


## The Library window, slice one: the shell (2026-08-14)

The 1440px frames in the CSS export ("Library - List + Detail") had never been built. This is the
shell: sidebar, header, search, and the list rows.

**The sidebar is a floating CARD, not a flush rail.** The frame draws it 240 wide, inset from the
window edges, radius 18, warm glass, a 1px white-10% rim and the same shadow every surface in this
design carries. A rail that runs edge to edge reads as window chrome; a card reads as a panel, which
is what it is. The seam that used to divide it from the main pane is gone - with the card floating,
a rule there draws a line down the middle of the gap.

**The nav rows were Borders with `MouseLeftButtonUp` handlers.** That is the standing rule this
project has broken three times ("a clickable thing is a Button"), and it survived because `--hittest`
only audits `ButtonBase` - so the app's most-used control was breaking the rule in the one place the
test could not look. They are real Buttons now, at the frame's sizes (44 tall, 24px icon, 16px label
at weight 500/white 60% idle and 600/white 90% active), and the hittest count went up accordingly.

**The list rows are filled capsules.** The frame gives EVERY row a fill (rgba(118,118,128,0.12),
radius 16, padding 12); only the selected one had one before, so the list read as a wall of text with
one highlighted line in it. Selection is now a brighter fill, which is a stronger signal than "has a
fill at all". Type went to the frame's sizes: title 16 SemiBold, meta 12 Medium, snippet 12 - it was
13/9.5/10.5, a size smaller than anything else in the app, which is why the list read as fine print.

**The search field had no icon and no placeholder** - a bare rounded rectangle you had to guess at.
It is the frame's 44-tall radius-24 glass pill now, with a magnifier and "Search these notes - you: /
them", and the sort control beside it is a real 44x44 button rather than a floating glyph.

**The header is one line.** "Library   56 | all on this PC", with the frame's 1px rule between the
count and the scope, instead of a title with a caption stacked under it costing a second line for
two facts that fit beside the word.

Still to do on this window: the reading pane's section cards, the Ask page, and the Settings
segmented controls.


## The reading pane's section rails (2026-08-14)

Slice two of the Library window. The frame does not draw sections as cards, which is what the
screenshot suggests at a glance - it draws a coloured circle badge and label, then a **3px vertical
bar in the same accent at 12%** running down the left of that section's content, with the text
inset past it. The badge already existed (`DocHeader`); the bar did not, and the bar is the part
that makes a section read as a group. Without it a coloured heading is the only thing tying its
bullets together, and by the third bullet the eye has lost which heading it is under.

**One Section per group, not a border per paragraph.** `Block.BorderThickness` would put a bar on
each paragraph and leave a gap at every paragraph margin - a dotted line rather than a rail. A
FlowDocument `Section` is a block container, so the whole group takes one continuous border.

The transcript rides the same rail. Its blocks are inserted and removed as it expands, so that code
now writes to the Section's `Blocks` rather than the document's - six call sites, all of them
already routed through one local.

**`--copytest` is what made this safe to do.** Restructuring a read-only FlowDocument risks breaking
selection ACROSS the new containers, which is invisible in a screenshot and is exactly the kind of
thing that would be discovered by the owner trying to copy a note. It proves select-all still
captures summary, bullet, to-do and transcript through the Sections.


## A button you cannot see, and a check that could not tell (2026-08-14)

The owner spotted "Back to Toki" at the bottom of the sidebar. Measured, its fill sat **1.02:1**
against the card behind it, in BOTH themes - not dim, invisible. The cause was mine: when the
sidebar became a glass card I gave the button `GlassCapsule`, and the Glass tokens are tuned to sit
on the NOTCH. Over the sidebar's own fill they composite to within a rounding error of it. A new
opaque `SidebarButton` token steps clearly in both directions.

**The text sweep could never have caught this.** It checks that a LABEL contrasts with what is
behind it, and this label measured 10:1. The control was invisible; its text was perfect.

**So a button-fill check was written, and then deleted.** It compared each button's fill to the
surface behind it. On the real app it reported seventeen cases, of which one was the bug. The
redesign has buttons defined by a 1px rim rather than a fill, and buttons sitting on gradients -
which are not `SolidColorBrush`, so "the surface behind" resolves to whatever opaque ancestor is
further up rather than what is actually painted. Real faults and correct controls both landed
between 1.01 and 1.15:1; no threshold separated them. A gate that cries wolf is worse than no gate,
and this project has already paid for that once. The reasoning is left as a comment where the check
would go: if it comes back it must sample the ACTUAL rendered pixel behind the control, not walk the
tree guessing.

**What did survive is the Library window joining the sweep**, which is worth more than the check
that prompted it. Two things came out of that:

  · The first run reported sixteen "invisible" labels that were simply mid-fade - the page entrance
    animation. Effective opacity is now part of the test, and anything under 95% is skipped: its
    contrast measures the clock, not the colour.
  · What remained was real. The list row's DATE NUMBER is the one label in the row that does not go
    through a Style, so it captured its brush and stayed dark-theme white on a light-theme card at
    1.02:1 until something happened to rebuild the list. Same captured-brush class as the pill
    labels and the calendar's parse preview, third occurrence, found by the sweep the moment it was
    pointed at a new surface.


## Half the transcript is hallucinated silence (2026-08-14, MEASURED)

Owner: "we are struggling to do transcription with good and clean accuracy" - Talat runs the same
Parakeet and the same Qwen3-4B and gets cleaner results. So the gap is ours. It is, and this is the
measurement that found it.

**The baseline.** Today's real recording, 13:15 of a two-stream meeting, run through `--asrtest`:

    you.wav   794.8s -> TEN segments, the first at 02:51
              "Oh my god, singy cocky and the beef in the table."
              "Vacuum kinna bital pin button up okay I will laugh okay okay"

That is not transcription, it is a model hallucinating words out of noise. So the two tracks were
measured second by second (RMS per one-second block, mono-mixed):

| | you.wav (mic) | them.wav (meeting) |
|---|---|---|
| format | 2ch f32 48kHz | 2ch s16 44.1kHz |
| speech-ish (rms > 0.010) | **3%** | 66% |
| near-silent (rms < 0.002) | **73%** | 32% |
| median rms | **0.0008** | 0.0635 |
| p90 rms | 0.0052 | 0.1614 |

**The mic track's median level is about EIGHTY TIMES quieter than the meeting track** - roughly 38dB
apart - and three quarters of it is silence. We hand both to the same model, at the same gain, with
no voice-activity gate. Parakeet's features are log-mel; fed a track whose median RMS is 0.0008 it
does not return nothing, it returns confident nonsense.

**Why this matters more than any other item on the accuracy list.** Lesson 1 of this project is
"garbage in, garbage out - check what the LLM is fed before tuning prompts". Half of every
transcript is this. It then feeds the note writer, the diarisation (which is counting speakers in
noise), the echo de-dup, the evidence links and the Ask index. Tuning a prompt downstream of it is
tuning against invented input.

**Two fixes, in order:**
1. A REAL voice-activity gate before decode. Not `QuietCut`, which finds the least loud frame near a
   target and always returns one - a genuine VAD that says "there is no speech here" and declines to
   decode it. Silence is not failure (lesson 4); it should produce nothing, not fiction.
2. PER-STREAM GAIN NORMALISATION. The two streams arrive 38dB apart from different capture paths
   (WASAPI mic vs per-process loopback) and there is no reason to expect them to match. Normalise
   each to a target level before the model sees it.

**THE CONTROL that proves it.** The other half of the same meeting, same model, same code, same
settings, run the same way:

| | you.wav (mic) | them.wav (meeting) |
|---|---|---|
| median rms | 0.0008 | 0.0635 |
| speech-ish seconds | 3% | 66% |
| **segments from ~13 min** | **10** | **242** |
| sample | "Oh my god, singy cocky and the beef in the table." | "Yeah, I pinged you in the team space itself." |

Nothing differs but the audio. The model is fine, the decode path is fine, and on a well-levelled
stream this pipeline produces a genuinely good transcript. It is the MIC stream that is broken, and
we decode its silence instead of declining to.

This also answers the second half of the owner's complaint - "same with LLM responses". The note
writer, the diarisation, the echo de-dup, the evidence links and the Ask index are all downstream of
a transcript that is half fabricated. No prompt change can survive that input.

**The fix is available without a new dependency.** The sherpa-onnx build already shipping in this
app exports `SileroVad`, `SileroVadModelConfig`, `TenVad`, a circular buffer and speech-segment
types - so a real VAD is one small model file (silero_vad.onnx, ~2MB via ModelManager) away, with no
second ONNX runtime and therefore no risk of the native-backend conflict CLAUDE.md forbids.

Order: gate with the VAD FIRST, then normalise per-stream gain. Not the other way round - normalising
a track that is 73% silence just amplifies its noise floor eighty-fold and would make the
hallucinations worse.

**And a measurement obligation.** 71 real recordings sit under `%APPDATA%\Toki\Recordings` (read
only). No change to this pipeline should land again without running the same audio before and after
and showing the diff - the remaining levers are all plausible and none of them is proven.


## The speech gate, and the tuning that had to be measured twice (2026-08-14)

Built on the finding above: decode only where somebody is speaking. Silero VAD, through the
sherpa-onnx runtime already shipping here (`SileroVad` is in the binding), so no second inference
runtime and no conflict with the rule against Microsoft.ML.OnnxRuntime. `Transcription/SpeechGate.cs`;
`Transcriber.Transcribe` decodes each region and offsets its timestamps. When the model is missing
or the gate throws it returns null and the old path runs - silencing a whole meeting because a
629KB download failed would be far worse than the bug being fixed.

It also replaces the arbitrary windowing. The old long path cut every 3 minutes at whatever
`QuietCut` judged the least-loud frame, which in continuous speech is still mid-word. Regions now
come from the model's own speech boundaries.

**Measured, on the same real recording.**

The quiet stream (you.wav, 13 minutes, 73% silence):

| | ungated | gated |
|---|---|---|
| decode | 159s (5.0x RT) | **30s (26.8x RT)** |
| worst line | "Vacuum kinna bital pin button up okay I will laugh okay okay" | "Okay I will laugh." / "Okay, okay." |

**The first tuning was wrong, and only an A/B caught it.** With Silero's defaults (threshold 0.5,
min-speech 0.25s) the gate looked fine on the quiet stream and quietly damaged the good one: a
word-level diff against the ungated decode showed **187 words of real speech thrown away**, and they
were all short - "yep", "yes", "go ahead craig", "hey david what you got", "craig you're not
audible". Real conversation is full of one-word turns and a 0.25s floor eats them.

The insight is that the two errors are not symmetric. **A false negative is a word the user said and
will never see; a false positive is a second of quiet handed to a recogniser that will mostly return
nothing for it.** So the gate must not sit at the middle of the curve: threshold 0.30, min-speech
0.10s, min-silence 0.50s, 0.35s of padding either side of every region. Retuned:

| them.wav (the good stream) | ungated | gated (v1) | gated (retuned) |
|---|---|---|---|
| words | 1580 | 1549 (**-31**) | 1596 (**+16**) |
| audio decoded | 100% | 70% | 72% |

No net word loss on good audio, hallucinations gone from bad audio, and a quarter of the audio never
reaches the recogniser at all.

**What this does NOT prove.** There is no ground-truth transcript, so none of this is a WER claim.
What is measured is word retention against the previous behaviour, decode time, and the
disappearance of specific hallucinations. Honest limits.

**Two smaller things found on the way.** Sherpa logged "Overflow! ... Increase capacity" twice: the
VAD buffer was sized for one region on the assumption that is all it holds, when on a continuous
talker the detector accumulates far more before it can emit. And `--asrtest` only printed three
lines of head and tail, which is why the first regression was nearly missed - it now writes the full
transcript to `--out` and names the file `.gated`/`.ungated`, so an A/B is two runs and a diff
(`TOKI_ASR_NOGATE=1` forces the old path).

**Lesson: a sorted line-diff cannot compare two transcripts.** The first comparison sorted lines and
reported dozens of "removed" utterances, which read as a catastrophic regression. It was an
artefact: the gate changes segment BOUNDARIES, so almost every line differs textually even when the
words are identical. Compare word sequences, not lines.


## Levelling, and why it has to happen BEFORE the gate (2026-08-14)

Second half of the accuracy fix: lift a quiet stream to the level the recogniser was trained for.
The two streams come from different capture paths - a WASAPI microphone and a per-process loopback
tap - and on a real call they arrived 38dB apart.

**Two things were wrong in the first version, and both were caught by measuring rather than reading.**

**1. The true peak vetoed the gain.** The lift was capped so the loudest sample could not clip, which
sounds obviously correct and is not: one transient - a cough, a desk knock - held a thirteen-minute
recording down to **x1.7** when its speech needed roughly ten times that. A single sample must not
decide the gain for a whole meeting. The ceiling now comes from the 99.9th percentile magnitude, and
the same recording gets **x9.4**.

**2. The gate was running on the un-levelled audio.** Gain was applied after the regions were chosen,
so Silero - a trained model with its own expectations about input level - was being handed the very
signal it was least able to read, on exactly the stream this whole exercise exists for. Levelling
now happens FIRST and the gate sees the lifted stream. Speech found on the quiet mic went from
**17% to 46%** of the recording.

The level itself is the 95th percentile of 25ms frame energy, not a mean. A mean over a track that
is three quarters silence reports a level nobody ever spoke at; the 95th percentile of a
mostly-silent recording is speech. It also needs no voice-activity pass to compute, which is what
makes it available before the gate.

**It does what it should on both streams**, which is the test that matters:

| | gain chosen | speech found | words |
|---|---|---|---|
| you.wav (mic, 38dB down) | **x9.4** | 17% -> 46% | 73 |
| them.wav (already good) | **x1.0** | 72% | 1595 (was 1596) |

A stream that is already at level is left alone entirely. That is the whole design: only ever lift,
never attenuate, and never touch audio that does not need it.

**What is still not fixed, and cannot be fixed here.** The mic transcript still contains
hallucinations ("Oh my money is a bumblebee"). The source is genuinely poor - a microphone running
38dB below the meeting audio is barely capturing the speaker - and no amount of post-processing
invents the information that was never recorded. The real fix is at CAPTURE: the input level on
that device. Worth telling the owner plainly rather than tuning further against a broken input.


## The mascot morphs, and the harness that could not see it (2026-08-14)

Task #11. Toki changed mood by flipping Visibility: a pen appeared, a bubble vanished, nothing moved
between the two. That is a doll having its accessories swapped, and it is the difference the owner
pointed at in the Grok mascot they sent, where nothing is ever attached and every state is the same
shape becoming something else.

**Two changes, and the second is the one that matters.**

1. **Props morph.** Every overlay now carries its own ScaleTransform whose origin is the point on the
   BODY it belongs to: the bubble's bottom-left at the shoulder, the pen's nib, the sparkles' crown.
   A mood change shrinks the outgoing prop back into the body and grows the incoming one out of it.
   260ms in, 170ms out, per the motion rules.

2. **The body carries the mood too.** Before this the silhouette was identical in all four moods and
   the prop was doing all the acting. Now Toki leans and sways slowly when thinking, dips on the
   SAME 260ms period as its own pen stroke when writing (an identical period is what makes it one
   motion instead of two), bounces once when pleased, and in every mood its EYES look at whatever it
   is doing. The eyes are three lines of code and do more than everything else combined.

The mood motion lives on its own transform channel (MoodBob/MoodLean), separate from the reaction
channel (Hop/Tilt). A mood runs for minutes and a reaction for 400ms; sharing a transform would mean
every tap jerked the body out of its mood and every mood swallowed the reaction.

**A prop is hidden by a mood-keyed timer, not by an animation-completed callback.** Moods flap - a
recording stops and restarts, a summary finishes mid-morph - and a completion handler that fires
after its animation was already replaced hides a prop that is at that moment being shown.

### The harness that could not see any of it

The --render-anim flag exists to render a view twice, ~550ms apart, "so the two PNGs can be compared
to confirm the loading animation actually moves". Its two files were **byte-identical, every run,
since the day it was written.**

WPF's timing engine is driven by the compositor, the compositor only runs for a visual tree attached
to a presentation source, and that harness never made one. RenderTargetBitmap.Render draws whatever
the properties hold right now, and with no clock tick that is forever the base value. So the one
check that existed to confirm an animation moves could not have detected an animation that did not.
CLAUDE.md lesson 12, again: a self-test that never produced its real artifact is not a test.

Fixed with a real off-screen host window. The clock then ticks as it does in the app, IsVisible turns
true so self-animating controls actually start, and the second frame is a genuine settled pose. A
second bug surfaced immediately underneath: RenderTargetBitmap.Render honours a visual's offset
within its parent, so the second subject in the host's stack rendered 200px below the top of a 100px
bitmap and came out as a 614-byte PNG of nothing - the kind of artifact that gets glanced at and
believed. Painting through a VisualBrush re-anchors it at 0,0.

The pair now shows what it claims: at t0 the props are absent and the eyes are level; settled, the
props are full size, Happy's eyes are squinted and Thinking's body is mid-sway.

## The notch's glass gets its highlight, and the mascot loses its Effect (2026-08-14)

Two parts of task #12.

**The mascot bubble's DropShadowEffect is gone.** That Effect sat inside MascotZone, which is the
subtree that animates - MascotPop springs it in, MascotShift glides it between states - so every
frame of every mascot entrance re-rendered the bubble in software. It is rule 1.3 of the motion
skill and the diagnosed cause of the mascot "rendering hard" in the first place. Replaced by a soft
gradient contact ellipse beneath the disc: same read, no per-frame cost. A surrounding glow would
have been clipped by the window's left edge (the zone is only 2px wider than the bubble), and there
is unlimited room below.

**The Sheen element finally does something.** The design system asks a glass panel for two things:
a 1px rim, and a top-edge inner highlight where light catches the surface. The notch has carried a
Sheen border for the second one since the redesign, painted Transparent - drawn but wired to
nothing, which is the exact shape of bug this project keeps paying for. It is now a themed gradient
(CardSheen, new in ThemeManager) fading down over a 48px band pinned to the top, not the full card
height: spread across a tall panel, the same amount of light stops reading as an edge catching it.

Gradients needed a second table in ThemeManager - the palette builds one SolidColorBrush per hex.
Dark sits at 11% white, deliberately BELOW the rim's 15%, because a highlight brighter than the edge
it sits inside reads as a smudge rather than as gloss. Light needs 55% to register at all against a
near-white card. Text under the band stays above 5:1 in both themes over any wallpaper.

**Still unpictured:** --render wraps each view in a stand-in pill, so the notch's real chrome - the
glass fill, the rim, this sheen, the mascot bubble - has never appeared in any artifact, which is
precisely the layer the owner keeps saying looks wrong. Tracked as its own task rather than papered
over with an approximation, because a model of the chrome is what every previous failed check was.


## The life layer: Toki watches you (2026-08-14)

Owner, after the morph work landed: "toki looks much better now, but ... it only blinks, doesn't see
to the side, it needs to ... more life to Toki".

They were right, and the reason was a number rather than a design. Fidgets fired every **30-60
seconds**, and only one of the four was a glance - so a sideways look happened roughly once every
three minutes. For the notch mascot, which is on screen in bursts of a few seconds, that is
statistically never. Toki was technically alive and observably a picture.

What actually reads as alive, from how eyes work and from the mascots that get this right, is the
EYES, moving often, in small fast darts. A human eye saccades several times a second. It is not a
fidget to be spent sparingly; it is the baseline, and everything else layers on top.

**Four layers now, constant to rare:**

| | what | how often |
|---|---|---|
| **Watch** | the eyes follow the pointer while it is near | continuous, 15Hz |
| **Saccade** | when nobody is near, the eyes dart to a new point | every 0.9-3.4s |
| **Drift** | the whole body floats and leans, so it is never truly still | 4.3s and 5.6s cycles |
| **Fidget** | the old vocabulary (look-around, nod, wobble, perk, double-blink) | every 9-20s |

**Pointer tracking is the one that does most of the work.** A character that tracks you is paying
attention; one that does not is a drawing. It is polled (GetCursorPos at 15Hz) rather than hooked,
because the notch is a click-through, never-focused window whose mascot is not hit-testable, so
there is no MouseMove to subscribe to. Deflection is normalised by the ATTENTION RADIUS, not by the
distance - dividing by distance would peg the eyes at full travel the moment the pointer left dead
centre. It re-aims only when the target has actually moved more than 0.12 art units, so a still
pointer does not start two animations fifteen times a second for no visible change.

**Gaze is composed, not owned.** A mood sets a BASE direction (writing = down on the page); saccades
and the pointer are OFFSETS from it. So a writing Toki still glances up at your cursor and returns
to the page rather than to dead centre. One shared ApplyGaze writes the transform; nothing else
touches it.

Measured cost with all of it running: **0.25 CPU-seconds over 20 idle seconds** (~1.2% of one core),
and `--fpstest` reports 1.2 dropped frames per notch transition, unchanged.

### The two moments the owner named

**Sparkles (first-run setup).** They pulsed and scaled; they now also TURN, each on its own period
and alternating direction, and there is a fourth low on the left flank so the set surrounds the
character instead of sitting in a neat arc above it. A four-point star that only pulses is a pulsing
star; the same star turning a few degrees while it pulses catches the eye the way a real glint does.
The fourth is placed OUTSIDE the body silhouette on purpose - a gold star in the middle of the blue
body reads as a blemish, not a sparkle.

**Writing (while transcribing).** The scribble was a 260ms sine loop, which is a metronome, and the
eye reads perfectly even motion as mechanical however good the curve is. It is now one 2.2-second
PHRASE: six quick strokes of uneven size, a beat of nothing where the hand lifts between words,
three more, then a longer rest. The body shares the same phrase (not merely the same tempo) at 16%
of the travel, which is what keeps pen and body reading as one motion.

## Two things a picture was lying about (2026-08-14)

**The Ask page's ambient bloom had hard edges.** It was a full-width rectangle of teal tint pinned to
the bottom of the pane, which gives you two straight lines: a vertical one down the gap beside the
sidebar card, and a square corner where it meets the window's rounded one. The owner sent a
before/after pair for it - "only have the curved corner". It now carries an opacity mask that fades
it out over the outer 14% on each side. Atmosphere with a straight edge is not atmosphere, it is a
panel. One static brush, no per-frame work.

**Every dark-theme Library shot was unreadable.** `--libshot` renders `RootGrid`, and the page's
surface is the WINDOW's background, which RootGrid does not have - so the shots came out as
dark-theme text on the bitmap's white. A picture of a bug the product does not have, which is the
same failure as the animation harness that photographed nothing: an artifact that gets glanced at
and believed. The harness now paints the background by resource reference (harness-only, so the real
window does not carry a background it has no use for).

**And the landing mascot was a Border with a mouse handler** - the fifth time this project has
shipped that pattern. No press feedback, no keyboard, no hover: the biggest, most obvious Toki on
screen was the one you could poke and feel nothing from, which is exactly the "haptics" half of the
owner's complaint. It is a Button now, so the shared style compresses the whole box on press.


## The summon flash, and the bug class behind it (2026-08-14)

Owner: "when i try to open toki by taking my cursor to the top of the screen, the toki in the bubble
flicker appears for a second before the actual toki gets rendered, this happened before as well".

**A delayed animation does not hold its own From value while it waits.** Toki's bubble springs in
90ms behind the card so the pair reads as two objects rather than one printed graphic, and that delay
was expressed as `BeginTime`. Until an animation's clock starts, the property shows the element's
BASE value - and those were scale 1 and opacity 1, which is Toki's ENDING. So every summon painted a
full-size, fully opaque Toki beside a card that had not unfurled yet, then snapped him down to 0.55
and sprang him back in. The delay was a preview of the result.

The fix is `ArmMascotReveal()`: park the properties at the animation's From, THEN build the
character, THEN make the zone visible, THEN start the animation. Order matters and it was wrong.

**The same class, swept.** Every `BeginTime` in the app was checked. Two more were wrong, both in the
mascot: the thinking dots sat at full opacity for up to 320ms before dropping to 0.25, and the
staggered sparkles sat at FULL size and opacity for up to 690ms before snapping to half size. Both
now park at their From. The rest (the Library's staggered page entrance, the calendar rows, the
status flashes, the wave bars) were already correct - they set the start value first.

### --revealtest

New flag, because nothing could have caught this. It is not a hit target, not a colour, not a
measured height, and it cannot be screenshotted: the overlay is a layered window and the whole thing
is over in a tenth of a second. So it drives the REAL PillWindow from Hidden into a panel state and
reads the properties back at the instant of truth: the card must be clipped shut, Toki must not be at
full size or opacity, the content must be faded out.

Verified by deliberately removing the fix: it reports `opacity 1.00` and `scale 1.00` - exactly what
the owner saw - and passes with the fix in.

### A flag can be registered in one place and silently do nothing

`--revealtest` printed nothing and exited 0 on its first run. Self-test flags are registered TWICE:
in the switch in `SelfTests.Run` and in a whitelist in `App.OnStartup`. A flag in only the switch
falls through, the app decides it is a second instance, and exits 0 with no output - which is
indistinguishable from a test that ran and passed. Noted in a comment at the whitelist.

## The UI sweep the owner asked for (2026-08-14)

Beyond the reported flash, from the app's own click log and a pass over the known anti-patterns:

**Three more clickable things that were not buttons.** The "Latest" jump pill in the notch's Ask
view, its twin in the Library's Ask page, and the "Built with - open source and licences" row in
Settings. All three were Borders with mouse handlers: pill- or row-shaped, wearing the hand cursor,
giving nothing back when pressed. The About row had two hand-written MouseEnter/MouseLeave handlers
re-implementing what the shared style does for free. That is the fifth, sixth and seventh instance of
this pattern in the project.

**A whole screen was outside the hit-target audit.** The live log had recorded a real hover miss on
the calendar composer's Create button (inside its box, `IsMouseOver` false, `DirectlyOver` a Grid) -
and `--hittest` could never have found it, because the composer is a MODE of the Calendar page rather
than a PillState, and the audit walks states. The composer and the month day-picker are both audited
now: 259 controls became 311, all passing.

## One Toki per surface, enforced (2026-08-14)

Owner, with two screenshots: the Ask page's mascot grew a grey rounded-rectangle background on hover,
and the sidebar masthead (mascot plus wordmark) did the same.

The hover box was mine, from an hour earlier: making the landing mascot a real Button - correct, it
had no press feedback at all - brought `RowButton`'s hover fill with it. **New `BareButton` style**:
press-compress only, no hover or press fill, transparent background retained so the whole box stays
hit-testable. It is for the one case where the thing you click IS the artwork. Drawing a grey
rectangle behind a soft character puts a box around a shape that is deliberately not a box; the
affordance is the hand cursor plus Toki reacting when poked, which is a better one.

**The sidebar mascot is gone entirely** - the masthead is the wordmark alone. CLAUDE.md has said "one
Toki per surface" since the redesign, and the Library window had two: a small one in the rail and the
real one in the middle of the Ask page. A masthead identifies the app; it is not a control.

**And no icon in the title bar.** WS_EX_DLGMODALFRAME plus a frame-change, which is the documented
way to get the icon-less dialog look, plus clearing ICON_SMALL because that is the one the title bar
draws. ICON_BIG is deliberately left alone: that is what the taskbar and alt-tab use, and it is the
icon generated from Toki by --makeicon. Clearing both would have quietly blanked the taskbar entry.


## The first-summon flash: it was the LAST frame, not the first (2026-08-14)

The owner tested the previous fix and it was still there, with a detail that changed everything:
"the FIRST time i hover over the top, the notch kinda pops open for a split second and then the toki
drops down from the top ... everytime i hover back again after the first time has no issues."

The arming fix (parking the entrance at its From) was necessary but was not this bug. This one is at
the other end of the cycle.

**Closing the notch left a picture of an OPEN notch in the window's surface.** `ConcealUp` finished
with `Root.Clip = null; Hide();` - it un-clipped the card, to its full open size, and then hid the
window. The render pass queued by that clip change still ran, against a window that was already
hidden, so the layered surface Windows keeps for the HWND ended up holding a fully open notch. The
mascot made it worse: `MascotZone` is a SIBLING of the card, not a child, so the clip never masked
it - the stale surface had Toki's bubble in it at full opacity too.

On the next `Show()`, DWM presents that surface until the first new frame lands.

**Which is exactly why it was the first time only.** On the first summon the view for that state is
being constructed and laid out for the first time, so the gap between `Show()` and the first real
frame is long enough to see. Afterwards the view is cached, the new frame lands within a vsync or
two, and the stale frame is never on screen long enough to notice. The owner's "only the first time"
was not a clue about startup - it was a clue about how long the gap is.

The fix is `ParkAfterHide()`: hide FIRST, then put everything back to its "before it opens" state and
leave the clip SHUT. The last thing the window ever composes is empty.

**A second bug fell out of it.** The mascot's entrance only ever ran ONCE per app run. `SetSurface`
arms and plays it only when the mascot was hidden, and nothing ever hid it - so after the first open,
Toki simply appeared with the card, with none of the spring he was written to have. Parking on hide
fixes that too. The owner's "everytime i hover back again after the first time has no issues" was
half right: no flash, and also no entrance.

**Proven, not asserted.** `--revealtest` grew a hide/show cycle: after a hide, Toki must not be left
showing, the card must be left clipped shut, the content must be left faded out, and the SECOND
summon must arm the entrance like the first. Reverting the one line reports all three failures,
including `opacity 1.00, scale 1.00` for the second summon. The test pumps the real dispatcher for
500ms so the retract animation genuinely runs - a test that only calls methods would be measuring a
model of the animation.

## The mascot bubble in dark theme (2026-08-14)

`MascotBubble` was `#D9F4F7FB` in the DARK theme - a near-white disc - on the reasoning that a bright
puck lifts the character off the dark glass. On a dark desktop with a dark notch it is the single
brightest thing on screen. Owner: "the bubble doesn't turn dark enough when we switch to dark theme,
its just white with a shade of black."

Now `#E6383433`: the same warm dark glass as the card, a step lighter so the disc still reads as its
own object beside it rather than a hole in it, with the rim dropped from white-40% to white-20% to
suit. The character stays legible either way - he is a pale blue cloud (#3D9BEA up to #EAF6FF),
which is a bigger step against #383433 than it ever was against near-white. Rendered in both themes
to check rather than reasoned about.

## No icon in the Library's title bar (2026-08-14)

Owner: "even on the window bar with the minimise, maximise and close buttons, on the left side, it
says mascot and Toki, remove all the mascot, only Toki."

WPF has no ShowIcon property. `WS_EX_DLGMODALFRAME` plus a frame-change gives a window the dialog
look, which has no title-bar icon. **Setting only the style was measured on the real window and was
NOT enough** - the style bit was on and the mascot was still there - for two reasons: `StyleChrome`
runs on `SourceInitialized` and WPF applies its own icon AFTER that, and the title bar draws whatever
icon the window holds regardless. So the strip runs again at `Loaded` priority, and both icon slots
are cleared. Verified by screenshotting the real title bar: "Toki", no icon.

**Not verified:** the taskbar/alt-tab icon. Those read the icon from the process, and the app icon is
compiled into the exe (the one `--makeicon` generates from Toki), so clearing the window's icons
should not touch them - but the owner's taskbar is auto-hidden and could not be captured in this
session, so this is reasoning rather than a measurement, and it is flagged as such.

**Incidental finding worth keeping:** this machine has "Show Toki in screen recordings" ON, which
means ghost mode is off and **the notch IS screen-capturable here**. Every note in this repo saying
the overlay cannot be screenshotted assumed the default. Task #15 (picture the notch's real chrome)
is therefore much cheaper than planned on this PC.


## Two morphs, 19ms apart: the actual split-second pop (2026-08-14)

The owner reported the flash a third time. Both earlier fixes were real bugs and neither was this
one. This time I stopped reasoning and photographed it: 90 screenshots of the top-centre strip while
the app woke up, assembled into a contact sheet. (This PC has "Show Toki in screen recordings" ON, so
ghost mode is off and the notch is capturable - every note in this repo saying otherwise assumed the
default.)

The frames showed the card appearing at FULL SIZE and EMPTY in a single frame, then the content
fading in, then Toki arriving. Not a stale surface, not a delayed animation: no growth at all.

Then one line of instrumentation in `PinContentForMorph` said exactly why:

```
22:06:33.294  morph clip  52x42 -> 344x211   union 344x211  (150ms)
22:06:33.295  transition Collapsed->Home path=hover-morph  card=344x211
22:06:33.313  morph clip 344x211 -> 344x211  union 344x211  (300ms)
```

**Two morphs run for one transition, 19ms apart, and the second is a no-op that cancels the first.**

- The first (150ms) is `GrowToContent`, fired by the freshly-built view as it lays out. It is the
  real growth, 52x42 to 344x211, and it is correct.
- The second (300ms) is the state change's own morph. Starting it supersedes the first (`_pinGen`),
  so the genuine growth is cancelled 19ms in - and because its own from == to, it animates nothing.
  The card is left sitting at full size.

**Root cause: `_cardW/_cardH` is written at the START of a morph, not at its end.** So while one is in
flight it already claims the destination, and any morph that begins during it reads its own target as
its origin. That is a general fault - ANY two overlapping morphs snap - and the first summon is simply
where two reliably overlap, because that is the one time the view is being built and grows as it
lays out. On later opens the view is cached, there is nothing to grow into, one morph runs, and it
looks fine. That is the whole of "only the first time".

**The fix:** a starting morph reads the CURRENT size off the live clip (`RectangleGeometry.Rect`
returns its animated value), so a superseding morph picks up where the previous one had reached and
carries on. Second half of the fix: the hover-morph path was doing `Root.Clip = null` immediately
before calling the morph, which destroyed that information - it no longer does, and
`PinContentForMorph` clears the clip itself on the one path where it sets no new one.

Measured after: `morph clip 52x42 -> 344x211` for both, and the contact sheet shows the card growing
across five or six frames instead of appearing in one.

**Both diagnostic lines stay in.** One per state transition (which path, is the window visible, what
size is the card) and one per morph (from, to, union, duration). This bug took three attempts and a
90-frame burst to find, and it would have been one line of log the first time. Same reasoning as
`LogClicksThatMissAButton`.


## The flicker, frame 34 (2026-08-14)

The owner sent a screen recording. Extracted at 30fps, the answer is one frame:

```
frame 33  (1089ms)  the peek
frame 34  (1122ms)  THE COMPLETE SETTINGS PANEL - full size, fully opaque, fully laid out
frame 35  (1155ms)  blank
frame 36+ (1188ms)  the panel fading in properly
```

One frame of the finished panel, then nothing, then the real entrance. That is the flicker, and it
is not the same bug as either of the two fixed before it (the un-armed entrance, and the two
colliding morphs - both real, both shipped, neither this).

**The content was put into a visible window at full opacity before anything hid it.** `OnStateChanged`
adds the new view to `Host` near the top; each of the three transition paths then sets `Host.Opacity`
to 0 and applies a clip - but only AFTER doing the sizing work. And the sizing work ends in
`SetWindowPos`, which is a synchronous Win32 call that can let the window paint. So there was a
one-frame window in which the new view existed on screen at full opacity, at the new full size, with
no clip. Exactly frame 34.

**Three changes, in the order the frames demanded:**

1. `Host.Opacity = 0` moved to BEFORE the view is added, not after the sizing. There is now no moment
   at which the content is on screen without having been faded in.
2. In `AnimateSize`, the clip goes on BEFORE the window grows. The worst thing that can be painted
   mid-resize is now the small rectangle the card is about to grow out of, instead of the whole panel.
3. The mascot fades out WITH the card during the retract. The clip masks only `Root`, and the mascot
   is a sibling, so through the whole 170ms close the bubble sat at full size beside a card shrinking
   to a stub - which is the owner's second screenshot exactly. `ParkAfterHide` did put it away, but
   only once the retract had already finished.

**Measured after**, same transition, log-synchronised burst on the real app: the panel comes up over
frames 44-50 (~230ms) as a smooth grow-and-fade, and there is no full-size frame anywhere in the
sequence. The morph log also shows the clean handover the previous fix bought: `52x42 -> 344x211`
followed by `141x93 -> 344x211` - the second morph picking up the live value and carrying on.

**How this was found, and the lesson.** Three attempts failed because I reasoned from the code about
what the frames must contain. The recording settled it in one pass. ffmpeg is not installed on this
PC but `pip install imageio-ffmpeg` ships a binary, and a contact sheet with frame numbers and
timestamps drawn on turns "a fast flicker" into a specific frame index. When a visual bug survives a
fix, get the frames before touching the code again.


## Toki, from the actual vector (2026-08-14)

The owner described the gap precisely: "it has one blob on top, 2 for each side, and 2 legs kinda
shape, but ours is a blob on top of its head, and a fat mascot that just is fat at the bottom part
... it is a semi circle shape at the bottom."

That is exactly what the old art was - four overlapping ellipses (a wide base plus three head
circles), which can only ever make a fat semicircle - and no amount of tuning ellipses would have got
to the real shape. So the character was rebuilt from the design file instead of from a reading of it.

**What came out of Figma** (Udtb5KHB2iTLIVP5FhKaSl, "Assets - Characters", 325:278), via
`get_design_context` on the vector node, which returns a real SVG:

| | before | now |
|---|---|---|
| body | 4 ellipses, 120x106 (ratio 1.13) | the design's single path, 57.43x43.90 (ratio 1.31) |
| silhouette | fat semicircle | crown bump, a lobe each side, two feet with a notch between |
| gradient | pale top -> saturated bottom | **#01A4FF flat to 35%, then -> #F0FAF9** (the design's own) |
| eyes | 16x30, far apart | 7.05x15.26 at x 18.99 / 31.49 - the design's numbers |
| happy | squashed pills | ^ ^ arcs |
| thinking | r9 bubble, 4.5px dots | 28x12 at r4, three 4px dots |
| reading | **did not exist** | the note in hand: 26x18 card, two rows of a dot and a line |
| writing | a hand-drawn chunky barrel | the design's pen paths, navy #3E5E92-#608EC6 with a silver ferrule |

The gradient being upside down is worth naming on its own: it is why the old one read as a heavy blob
sitting on the floor rather than a light one floating.

**The board is 67x52, not the body's 57.43x43.90**, because the props live outside the body exactly as
the design frames do (the thinking and notes frames are 67 wide). The body is drawn at (0,4) and every
prop uses the design's own coordinates plus that offset, so the numbers in the file can be checked
against the file they came from.

**Two things measured rather than eyeballed.** The crown glow was set by sampling the reference
render: 10% down the body it reads (138,216,252), which is #C7F0FC at 69% over #01A4FF, and 25% down
it reads (40,178,254), which is 20%. The overlay was at 56% fading out by 34% - far too weak at the
crown, which is why ours looked harder and more saturated than the design.

And the pen: its path already carries the angle it is held at, so the scribble animation had to move
a few degrees around ZERO. It was sitting at 26 degrees of REST, which rotated the whole pen off the
body and out of frame - visible in the render as a pen floating outside the mascot's disc entirely.

**Moods are re-wired to match what they mean.** Recording (live transcription) is now READING - the
note in hand - and Summarizing is WRITING - the pen. They were the other way round in spirit
(recording showed a pen, summarising showed a thinking bubble), and the note-in-hand character
existed in the design and not in the app at all.

**A bug worth remembering from the rebuild:** `DotFill` was declared inside the thinking bubble and
referenced by the note card, its SIBLING. WPF resource lookup walks UP the tree, never sideways, so
the whole control threw a XamlParseException at construction - which surfaced as `--render-anim`
hanging rather than as an error, because the harness had no try/catch around the view it was building.

## Home says what it does (2026-08-14)

Owner: "the start a meeting button is not the relevant button for what its purpose is, it opens the
calendar page", and on the row beneath it: "the buttons, connect a calendar, library > and +google,
are so ugly and weird, what is +google, we have outlook as well, why is that even there?"

Both are fair, and the second is a correctness point rather than a taste one: naming ONE provider on
the home screen of an app that supports two is a bug.

- **"Start a meeting" is now "Calendar"**, with a calendar glyph instead of a video camera. It has
  always opened the calendar page; the label now says so. (The owner's call, from two options.)
- **The whole footer is gone** - the account marks, the "+ Google" pill, the "Library >" pill and the
  "Connect a calendar" dropdown. Connecting a calendar happens once, ever, and Settings already has
  both providers with their sign-in state and a disconnect.
- **UPCOMING moved below the two actions** (the verbs first, then what is coming), and when nothing
  is connected it shows one quiet row: "Connect to Google or Outlook calendar in Settings", which
  opens Settings.
- **The Library door became an icon** beside Settings in the header, where the redundant calendar
  icon used to be.

## The notch's Settings page (2026-08-14)

- The "Appearance" heading is gone. One setting and a link to the rest; a category label above a
  single row is a word that earns nothing.
- **The two rows now start at the same x.** "Glassy notch" was a bare Grid at inset 0 while "All
  other settings" is a RowButton whose style pads 12 - so the two read as belonging to different
  pages (owner: "alignment and symmetry is missing"). The row now carries the same 12px inset on both
  sides, so the switch and the chevron line up as well as the labels.


## The Grok lesson, applied where it was being broken (2026-08-14)

The owner looked at the new mood strip and said two things in a row. First: "the sparkly toki has
eyes and ^ ^, it can only have ^ ^". Then, once that was patched: **"remember the dynamic lesson from
grok bot, use that."**

Both were about the same thing, and the second is the one that mattered. The happy face was a SWAP -
collapse the two eye ellipses, reveal a separate arc Path - which is exactly the doll-with-swappable-
accessories pattern that task #11 exists to kill. It was written into ARCHITECTURE as the principle
and then violated in the very next feature.

**The eyes are now ONE shape that morphs.** Each eye is a closed path of four cubic segments -
thirteen points. Open, those points are an exact ellipse (kappa 0.5523, the design's 7.046 x 15.257).
Happy, the SAME thirteen points sit in a caret: the top rises to an apex, the sides swing out into
arms, the bottom lifts to the inner V. A `PointAnimation` per point takes one to the other, so the
eye BENDS into the smile and unbends out of it. Nothing appears; nothing is hidden.

Two implementation notes worth keeping:

- **Absolute board coordinates with `Stretch="None"`, not `Canvas.Left`.** The caret reaches outside
  the oval's bounding box, and a Path positioned by its bounds would slide sideways as those bounds
  grew mid-morph. The blink scale is centred on explicit numbers for the same reason.
- **The design's 1px eye shadow is gone**, along with the second copy of the eye that faked it. That
  copy was what stayed on screen under the arcs and produced "eyes AND ^ ^" - collapsing the eye did
  not collapse its shadow. At the size an eye actually renders, it was contributing nothing.

**And the body is centred.** The board is 67 wide for the props but the body is 57.43, and all the
spare room was on the right, so Toki sat 4.8px left of centre in his bubble. One fixed translate.

## Home and Settings, refined (2026-08-14)

Owner's list, each item its own defect:

- **"the library should have a proper button mentioning library and the symbol, not just the symbol"**
  and **"library button should be at the bottom right corner, like last time"**. It had been a text
  pill in a footer, then an unlabelled header icon. Neither was a button you could read. It is now a
  real pill, bottom right, with the shelf icon AND the word, on the same `Chip` fill every other
  secondary action uses - so its shade and size stop being a one-off.

- **"connect to google or outlook calendar button takes me to the notch settings, but the notch
  settings doesnt even have connect google or outlook buttons"**. Correct, and worse than untidy: a
  button that opens a page which cannot do what it promised. New `LibraryWindow.ShowLibrarySettings`
  opens the Library's Settings and brings the calendar rows into view - the only page that has Google
  AND Outlook with their sign-in state.

- **"the button itself is so long and so much text it cannot have that"**. It read "Connect to Google
  or Outlook calendar in Settings", which is an instruction printed on a button instead of a button.
  Now: an icon, "Connect a calendar", a chevron.

- **"the settings has an extended bottom empty area"**. The notch's Settings page was pinned at a
  FIXED 208px while its content - one switch and one link - measures far less, so the card carried a
  stretch of empty glass under the last row. It has joined the AutoHeight set (120 is now only a
  floor). A fixed height is only honest while the content is fixed, and this page lost most of its
  content when AI/calendar/name/about moved to the Library.


## Toki on white, and four smaller fixes (2026-08-14)

**The mascot is drawn for a dark notch.** The design fades his feet to #F0FAF9 and washes his crown
with pale #C7F0FC at high alpha - beautiful on dark glass, and on the light theme's near-white bubble
it means the top and bottom of the character simply stop existing. The owner sent a screenshot: a
blue band floating in a white disc.

Measured, our colour profile was actually FAITHFUL - sampling down the centre gave (131,214,253) at
15% against the reference's (138,216,252) at 10%, and (25,173,254) at 37% against (23,171,254) at
40%. The design is genuinely that pale at the ends. It survives on the Figma artboard because that
board is pure white and the eye supplies the missing edge; it does not survive in a product where the
same character has to sit on a #E6FFFFFF puck.

So three colours now have a light-theme value, applied in code on construction and on every theme
swap (gradient stops cannot take a DynamicResource cleanly):

| | dark | light |
|---|---|---|
| feet | `#F0FAF9` | `#4FC0FF` |
| crown wash | 86% / 69% / 20% | 35% / 24% / 8% |
| sparkles | `#FDE685` - `#FCC942` | `#FFC93C` - `#F59300` |

The saturated middle is untouched: the character is the same character, it just keeps its edges on a
pale background.

**The bubble was clipped during recording.** `PlaceMascot` centres the 58px bubble on the card, and a
compact pill is 40-50 tall - so centring wanted a NEGATIVE offset, which put the top of the circle
above the window's own top edge, where it was cut flat. Clamped to 0: a few pixels low beats a circle
with a flat top.

**The moods were the wrong way round again.** Owner: "toki writing should be when toki is recording,
and toki notes should be when toki is transcribing." So recording shows the pen and summarising shows
the note. My previous guess had it inverted.

**"Connect a calendar" now glides.** New `SmoothScroll.GlideTo` animates a ScrollViewer so a target
element lands near the top, on the same curve family as a wheel nudge but longer and ease-in-out -
this is a journey across a page, not a nudge. `BringIntoView` jumps, which is right for a keyboard
tab and wrong for "take me to that setting": arriving somewhere new should show you that you
travelled, or the page just seems to have changed under you. It fires 260ms after the page lands, so
the motion is not spent on a page nobody has seen yet.

**The app icon is the new mascot**, regenerated at all seven sizes with `--makeicon`. It is built
from the LIGHT-theme colours deliberately: the dark-theme feet fade to near-white, which is right on
a dark notch and invisible on a light taskbar, and an icon has exactly one requirement. Windows
caches shortcut and taskbar icons, so a pinned shortcut may need re-pinning to pick it up.


## "Replay first-run setup" landed on the last page (2026-08-15)

Owner: "replay toki first setup takes me to you're all set page".

The notch caches one instance of every view, and OnboardingView holds its position in the flow in
nothing but the three steps' Visibility. So the first run walked name -> privacy -> done and left the
cached instance sitting on "You're all set" - and every later replay handed that same instance
straight back, already finished. It would have been invisible on a fresh install and permanent
afterwards, which is why nothing caught it.

`ResetToStart()` rewinds the three visibilities and re-reads the name from settings (which may have
changed in Settings since the last run - seeing and correcting it is the point of replaying). It is
called from `OnStateChanged` when the view is fetched, NOT from the view's own `Loaded`: the notch
measures the view to size the card, and that measure happens before Loaded runs, so a reset on Loaded
would size the card for whatever step was left showing and then swap the content under it.

**The general shape here is worth naming:** a cached view that holds a FLOW must be rewound on entry.
The cache is right (rebuilding every view on every state change is task #10's whole complaint), but a
view with a position in it is state, and state that outlives its visit is a bug waiting for someone
to come back.

### The reveal test stopped racing the clock

Adding the onboarding check to `--revealtest` made three of its existing checks fail - on a build that
was correct. They sampled the entrance LIVE (`is Toki still faint?`) a few instructions after the
state change, and the extra work in the test was enough that, on a loaded machine, the 90ms delay had
elapsed and the spring was 90% done. A test that fails because the machine is busy is worse than no
test.

`PillWindow` now snapshots the four entrance values at the instant of `Show()` - clip height, mascot
opacity, mascot scale, content opacity - and the test asserts on that snapshot. It is the number that
actually matters (what the entrance starts FROM) and it never moves. The thresholds went back to
"essentially zero" instead of the loose 0.35 the sampling had forced.

Verified the same way as the others: with the fix reverted, the check reports `showing step 2`.


## Toki sitting on the pills (2026-08-15)

Three screenshots, one root cause. During a morph the window is held at the UNION of the old and new
sizes and the card is CENTRED in it, so the mascot is shifted right by half the slack to stay beside
the card's moving left edge. That shift was never cleared. The moment the morph finished and the
window settled onto the target, the card filled its area again - and the bubble stayed parked at the
inset, on top of the pill.

It only showed on a SHRINK, which is why it looked like three unrelated bugs: a panel folding to the
"Notes ready" pill, to the recording bar, or to the loader's sliver each left Toki further right than
the last. The timer read "0:17" with the bubble over the "0". Cleared in the clip animation's
Completed, at the same instant the clip is dropped and the card starts filling its area again.

**And a second, older one underneath it:** the window is only as tall as the card plus its shadow pad,
but the mascot zone is a fixed 58px pinned to the window's top - so any state whose card is shorter
than about 40px cut the bottom off the circle. The loader's sliver made it unmissable: a 13px card
gives a 37px window and two thirds of Toki's head. `WinH` now floors at the mascot's height. The extra
is transparent pad below the card, exactly like CardPadB, so nothing else moves.

Photographed on the real notch afterwards, state by state (ghost mode is off on this PC, so the
overlay is capturable): Summarizing and Notes both show a full circle with Toki beside the pill.

### The frame budget, and an instrument I cannot currently trust

`--fpstest` after all of this reported 16.8 dropped frames per notch transition against a 1.2 baseline
from before the mascot rebuild - which would be a serious regression and exactly the "hard rendering"
this project has fought before.

So I cached the two things that deserve it. The body is THREE complex bezier paths, each
gradient-filled, and the whole mascot is permanently under transform animation (breathing, the idle
float, the mood lean), so every frame re-rasterised all three; the pen is five more paths under a
scribble that runs for as long as a recording. Both are static geometry that only ever moves under a
transform, which is the textbook BitmapCache case, and there is no text in either so the skill's
ClearType warning does not apply. RenderAtScale 4; verified crisp at notch size and in the harness.

**Then the measurement fell apart.** Three consecutive runs of the SAME binary gave 10.5, 18.4 and
15.4, with the CONTENT cost line swinging between 5% and 33%. The spread between runs is larger than
the difference being chased, so the instrument currently cannot answer the question. This PC is
syncing this repository to OneDrive and has been building continuously for hours.

I am recording that rather than picking whichever number flatters the change. Tracked as its own task,
with the next suspects named (the eyes, which replaced two Ellipses with two PathGeometry paths and
cannot be cached because they blink and morph; the idle float and lean, which are two NEW permanent
animations; the 15Hz pointer watch) and a note that the test should report a median of several runs
instead of a single one.

## The four ways the notes lied, and the shape of each fix (2026-08-18)

The owner asked what was wrong with the two newest recordings. All four defects shared one root:
a layer that was allowed to be confidently wrong where it should have stayed silent. The fixes are
all narrowing moves, and each encodes a rule worth keeping.

**Distance budgets must scale with length.** The name corrector allowed a trusted name to claim any
word within 5 edits and 2 phonetic-key edits. On a 3-letter word that is a licence to rewrite
anything: "day" became "Toki", "get" became "Craig", the real person David became "Toki", and the
damage compounded because notes are written FROM the transcript ("a two-Toki wait period is
suggested"). `SoundAlike` is now three explicit branches - homophone (key distance 0, raw <= 3),
near-spelling (key distance 1, floor of 4 letters, raw <= 2 or 3 by length), and the trusted reach
(key distance <= 2, raw <= 5, ONLY at 6+ letters with the first two key characters matching). Each
branch's comment carries the real example that justifies it and the real failure it must not
readmit. This matches what the contextual-biasing literature warns about: a correction layer must
be measured against DISTRACTORS, not just against the names it should catch.

**Detection conditions are replacement conditions.** The old rewrite replaced every case variant of
a mapped word anywhere in the text, while detection required a capitalised mid-sentence token. Any
gap between what qualifies a word and what gets rewritten is a bug factory: one stray capitalised
"Day" poisoned every lowercase "day" in the meeting. The rewrite now applies the same tests
detection does (capitalised; sentence-initial only with mid-sentence evidence or a trusted target).

**A fix that is not on the executed path does not exist.** The Silero speech gate (2026-08-14) ran
only in the stop-time whole-file decode - and every real meeting takes the LIVE path, which fed raw
15-second windows to Parakeet ungated. Lesson 12 already said a self-test that never produced its
artifact is not a test; the corollary is that a fix verified on a path meetings never take is not a
fix. The gate now lives in `TranscribeSlice` (persistent Silero session, levelled before gating,
same order and reasons as the whole-file path), and the whole-file path no longer falls through to
an ungated decode when the gate finds zero speech.

**Cross-references resolve against final identity, at bounded range.** "Still open from last time"
matched previous meetings while this meeting still had its placeholder title (the transcript's
first words), by SUBSTRING ("just" matched "AdJUSTing", "even" matched "EVENt Coverage"), with no
age limit (a seed record with 2023 due dates was handing out homework). It now runs after the LLM
title exists, matches whole words only, and looks back at most 45 days. Wrong evidence links are
worse than none - the same rule the evidence player follows.

**A prompt is a request; a validator is the enforcement.** The model echoed the prompt's own
"drop the due field when no time was stated" into an actual due slot, and ParseActions stored it.
Owner and due are claims now checked in code: due must look like a time, owner must be "You" or
appear word-for-word in the transcript (the faithfulness literature's point that a mis-attributed
task is the compounding error - the wrong name in the note becomes the wrong name in the follow-up).
This is the same lesson as sampler-params-over-prompts (lesson 8), applied to output instead of
decoding.

Checked and rejected: the loopback "clipping" theory. them.wav's 99% peak scanned clean - zero
saturated samples, max 0.985. Loud is not clipped, and turning it down would have cost quiet
speakers for nothing.

## The five-critic audit, and what it says about guessing layers (2026-08-18)

The owner asked for the processing system to be adversarially reviewed end to end. Five independent
critic agents each audited one layer and reported only provable failure scenarios; every claim was
re-verified against the code before a fix. Twenty-four confirmed defects were fixed in one pass.
The patterns matter more than the list (the list is in the build log):

**State machines must not own session facts.** The recording clock lived in AppState and reset on
any re-entry into a recording state - but the SESSION keeps recording while the pill detours
through Ask. A fact about the session (when recording started) now has session lifetime: set once
while null, cleared explicitly at stop. Any fact stored one level above the thing it describes
will eventually disagree with it.

**Deletion needs a tombstone, recovery needs a claim.** DiscardMeeting deleted the DB row first
and the folder second; a locked WAV left a folder that looked exactly like a crash orphan, and the
recovery sweep faithfully resurrected the meeting the user had deleted. Now the folder is marked
`.discarded` BEFORE the row goes; the sweep deletes marked folders and claims unmarked ones by
rename (the repo's own claim-by-rename rule, which the sweep itself had never applied). The sweep
also re-checks the DB at claim time - its `known` snapshot goes stale over a minutes-long backlog.

**Validation against model output is not validation.** The owner gate checked names against the
map-reduce DIGESTS - text the model wrote - so an invented name validated itself. Ground truth
for a claim must be text the model cannot have produced: the transcript. Same shape as the
template gate's "1:1" check, which counted stream labels (always two before diarisation) and so
cleared every meeting there is: a gate whose condition is always true is not a gate, and both were
invisible because every test fed them exactly the shape that made them pass.

**Failure and absence are different answers.** Three separate layers folded "I could not do it"
into "there was nothing to do": a map-chunk timeout became "nothing substantive was discussed", a
thrown live decode became "nobody spoke in this window", and a parse miss on headerless output
became a low-substance note. Each now propagates failure distinctly (heuristic fallback, counted
lost seconds with a whole-file fallback past 8s, null-means-parse-failure) - because the cost of
conflating them is an invented VERDICT, which is worse than an honest degradation.

**Priors may extend evidence, never override it.** The attendee-count prior stopped diarisation's
merging the moment the cluster count matched the invite, force-splitting one voice into four
"speakers" on a partially-attended meeting. The rule now: distances below threshold always merge;
the prior only lets merging continue past the threshold. A prior that can create structure the
data does not support is a fabricator, not a prior.

**Symmetric-looking operations need their direction checked.** The echo de-dup's containment score
divided by the shorter line, which deletes the LONGER line - backwards for an echo, which is a
degraded COPY. The trusted-name reach, the corpus vote, the union-find transitivity (A~B and
B~winner never tested A~winner) - all had the same shape: a relation applied without asking which
way it points.

Deferred (documented in docs/backlog.md with reasons): VAD-boundary live windowing, device-gap
silence injection, handover-vs-backchannel attribution, evidence clip cap anchoring, live Library
refresh on title upgrade.

## An accuracy instrument, and cosmetics as a separate last pass (2026-08-18)

**`--wertest` exists because every accuracy claim before it was inference.** The project rule says
a self-test that never produced its real artifact is not a test; by that standard the whole
transcription stack was unmeasured. The harness scores the PIPELINE (gate + levelling +
readability), not the bare model, because the pipeline is what the user reads - and it skips name
correction deliberately, because that layer depends on the user's real library and settings,
which a test must never touch. Both sides of the comparison go through one normalizer (lowercase,
punctuation out, fillers out) so formatting can never masquerade as recognition error. Names get
a separate recall table: a 6% WER with the attendees' names all wrong is a worse product than 9%
with them right, and a single average hides exactly that.

The instrument paid for itself on its first run: synthesized eval audio came back with "Abhishek"
as "Ubishek", and tracing why pinning would NOT have fixed it exposed that SoundAlike's
first-sound gate required identical opening phonemes. Recognisers swap the opening vowel of
unfamiliar names constantly (a distinctly Indian-name-shaped failure, and these are the owner's
meetings). Opening vowels are now one sound class at that gate; consonant openings stay strict.
An instrument that finds a real defect in the thing it measures, on day one, is the argument for
building instruments first.

**Readability is its own pass, last in the chain, and rule-based on purpose.** Converting
"august twenty first" to "August 21st" or dropping "uh" could have been prompt lines in the note
writer or extra cases inside SelfCorrection. Both would have been wrong: a model rewrite can
invent, and mixing cosmetics into a repair pass couples "how it reads" to "what was said". The
pass runs after names and self-corrections so cosmetics never feed evidence into riskier layers;
its conversions are recorded as "tidy" fixes (the transcript shows its working, like name fixes);
its deletions are not recorded (nothing to hover); and every rule keeps the conservative default:
counts stay words, small ordinals stay words, "may" needs its capital before it can be a month,
and a line that was pure hesitation survives as itself rather than being emptied - the record
that somebody made a sound is still a record.

## One waveform, driven by real audio (2026-08-18)

**Decision: there is exactly ONE waveform control, `Controls/VoiceWave`, and its only per-site knobs
are `BarCount` and `Height`.** Two unrelated decorative waves had grown up independently (a
travelling-sine `LiveWave` on the recording bar, a row of pink bars built inline in `AskView`), and
the owner's read was that they did not match and neither was good. Anything richer than a count and a
height as a per-site knob is how two waveforms happen again, so the knobs stop there. `LiveWave` is
deleted; the pink bars are deleted with their captured-brush theme bug.

**Decision: the wave is driven by measured audio, and silence is drawn as silence.** Both old waves
animated at a fixed amplitude regardless of what the microphone was doing. That is a small lie, but
it is the same *kind* of lie the entire product is built to avoid, and it is the one the user can
check against the room in real time. `Audio/LevelMeter` is the source: capture buffers report RMS
(sum of squares over the buffer already in hand, no allocation, safe on the audio thread), and the
value self-decays through a sliding two-bucket max, so a stopped capture parks the row at its resting
dots on its own rather than through teardown wiring somebody can forget to call.

Two static meters, not one. `LevelMeter.Recording` is fed by BOTH the mic and the per-process
loopback, so the bar shows max(you, them) - a wave that only answers to your own voice is dead for
most of a meeting you are listening to. `LevelMeter.Voice` is fed only by push-to-talk, because
asking Toki a question mid-meeting must not make the Ask wave dance to the call.

**Decision: both ends of the level mapping track the recent history.** `LevelScale` maps
`sqrt(clamp((rms - noise) / (ref - noise), 0, 1))`, with `noise` following the 10th percentile of ~3s
of history and `ref` the 95th, separated by a minimum span. A fixed floor cannot serve both mics that
exist here: the owner's mic records around 38dB below the meeting stream, so a floor tuned for a
normal mic renders that one permanently flat, and a floor tuned for that one makes room tone dance on
a normal mic. A tracked floor answers both, and it is what makes steady room tone read as 0 rather
than as a quiet conversation. The history starts FULL of silence rather than empty: an empty ring
normalises the very first loud buffer against itself and calls it noise, which left the row flat for
the first second of every push-to-talk - the exact second somebody is looking at it.

**Decision: the recording bar's disclosure is one chevron in a glass puck, overruling the frame.**
The Figma frame draws lucide chevrons-up-down on a transparent disc. The owner rejected it directly
("sooooo ugly"), and the owner outranks the frame. It is now a 26px `RoundPuck` carrying `GlassFill`
+ `GlassRim` with a single chevron-down that rotates 180 degrees on expand (200ms on the new
`Motion.Move` in-place curve, applied to the GLYPH so the button keeps the shared style's
press-compress transform). The timer moved to amber, which is what time is everywhere else in Toki.

**What the arithmetic tests could not have caught.** The level mapping and the envelope are pure
functions and are unit-tested with mutation probes. The *feel* is not: the new `--wavetest` flag runs
the real control on the real dispatcher with a synthetic level and prints the bar heights at each
phase, and it immediately failed two things reasoning had passed. The first decay constant (0.10/tick)
left the row visibly above its resting dots a second and a half after the room went quiet, and the
per-bar jitter multiplied by the wobble peaked bars at 1.21x the control's own height, growing them
silently over whatever sat underneath. The envelope is now normalised to a tallest bar of exactly 1
with explicit headroom for the wobble, and the decay is 0.14/tick. Neither defect was visible to a
green test suite or to a static PNG.

## The Apple-glass material (2026-08-18)

**Decision: the glass is faked, and that is final for this window architecture.** The supported
Windows 11 backdrop (`DWMWA_SYSTEMBACKDROP_TYPE` Acrylic/Mica) requires normal DWM frame
composition and cannot combine with a per-pixel-alpha `WS_EX_LAYERED` window, which
`AllowsTransparency=True` makes us. The undocumented `SetWindowCompositionAttribute` acrylic path
blurs the window's FULL rectangle, and the notch window is deliberately bigger than the card
(mascot gutter left, shadow pads right/bottom), so the blur would frost empty desktop. This is the
same conclusion the earlier glass work reached (`ApplyBlur` exists and is called with `false` on
every path); the research pass confirmed it with three independent blockers and closed the
question. Apple's own material runs its tint plate at 82-95% opacity, so beyond the blur the look
is layering, and layering is buildable: plate gradient, grain, lit rim, inner edge light, stacked
shadows. `ApplyBlur`/`ApplyRoundRegion` stay as legacy machinery, still permanently off.

**Decision: one material, defined once, worn everywhere.** `Theme/CardMaterial.cs` is the layer
stack (radial top sheen, fixed-seed 64x64 grain at 2.5% dark / 2.0% light, 2px inner top highlight
and bottom shade drawn as rings so they follow the corner arcs), and the `Gradients` table in
`ThemeManager` builds and freezes every gradient (plate, rim, sheen, mascot) per theme swap, so
code-built consumers stay theme-correct despite frozen brushes. The rim is a vertical gradient,
brightest at the crown: the cheap 2D form of Liquid Glass's specular edge. Light mode rims end on a
dark bottom hairline because a white card on a white app otherwise has no edge at all. The Library
and all popups consume the same stack through `Theme/GlassSurface.cs` (`GlassCard`), not copies.
The Glassy notch setting now selects only plate opacity (~95% vs 100%); both modes get the full
stack, so the toggle can no longer strand the notch on a flat un-layered look.

**Decision: hover brightens a sibling overlay; it never dims content.** The old grammar (an opacity
dim on the whole button, or a white wash painted OVER a translucent fill) greys glass instead of
lighting it, and it fades labels with the box. Every shared interactive style now carries a fading
highlight layer (130ms in, 90ms out) that is a SIBLING of the padded content. The highlight brush
follows the family: free-floating glass mini-cards (GlassPill, RoundPuck) use the glass tokens; 
controls resting on the card keep the 22% HoverOverlay the owner signed off after the invisible-
hover day, because measured on the plate the glass token is 1.49:1 and 22% is 2.0:1. The rule is
recorded next to the tokens so a future pass does not harmonise it back below visibility.

**Decision: squircle corners at rest only, and honesty about what they bought.** `Theme/Squircle.cs`
implements the figma-squircle parameterisation (cubic, arc, mirror cubic per corner; smoothing 0 is
exactly a rounded rect, so one code path serves both). The card's clip and rim swap to the squircle
only when the notch is settled; every morph and reveal runs the exact pre-existing animated
rounded-rect machinery, and one method owns all four corner properties so a double rim or a rimless
frame is impossible by construction. Measured result: at radius 24 the squircle departs from a
circular arc by at most ~0.35px and is not visible at 1:1 (pixel-diffed renders, ~420 differing
pixels of 450,000, corners only). The visible smoothness wins came from elsewhere in the same pass:
every hairline on a rounded edge now lands on whole DEVICE pixels from the live DPI (a 1 logical px
stroke at 125% is a blurry 1.25px; the fix is 0.8 DIP = 1 device px), and the settle paths that
nulled the clip a frame early no longer flash a hard rectangle. The smoothing constant stays one
number (`Squircle.DefaultSmoothing`) should a larger radius ever make it worth seeing.

## The details pass (2026-08-19)

**Decision: the stop red is Apple system red, and it lives in the theme, nowhere else.** The stop
disc painted a frozen `#FF114D` copied from the Figma frame, while the theme token `StopRed
#FF2D55` (which is Apple systemPink, not red) had zero consumers - two reds, both wrong, one dead.
`StopRed` is now `#FF453A` dark / `#FF3B30` light (Apple system red), the disc consumes it via
`SetResourceReference` through `PillParts.RoundButton`, which now takes a resource KEY, not a
Brush - a captured brush in a code-built control is the theme-bug class that has now bitten four
times, so the helper's signature forbids it. The owner asked for Apple's clean red by name; the
CLAUDE.md colour line and the design-web mirror were updated in the same pass.

**Decision: small glyphs are authored Path geometry, never font characters.** The off-centre play
triangle the owner photographed was `Text="▶"` with a hand-tuned margin: font glyphs carry
baseline and side-bearing offsets that shift with font fallback and DPI, so they can never be
reliably centred in a 20px chip. Every glyph-in-a-disc is now drawn geometry at its final pixel
size (`PillParts.PlayTriangle`, `StopSquare`, `Cross`), optically centred (a right-pointing
triangle sits a hair right of box centre because its visual centroid is left of it), and opted out
of layout rounding (`PillParts.SubPixel`) so fractional-DPI margins centre sub-pixel instead of
snapping to one side. Corollary discovered on the way: nudging a glyph inside a centre-aligned
ContentPresenter by Margin is a measured no-op (layout re-centres the widened box); positional
nudges use a RenderTransform.

**Decision: a control must FIT its layout slot; the hit-test gate now proves it.** The clipped
chevron puck was a 26px control in a 24px row - WPF silently layout-clips and the bottom of the
circle ships flat. `--hittest` now fails any clickable control whose `GetLayoutClip` is non-null,
so an oversized control in an undersized bar is a broken build, not a screenshot from the owner.

**Decision: Library accent bars mean something or they are uniform - never random.** The per-card
bar was coloured by `id % 6` across six accents, which reads as meaning and carries none. It is now
coded by `MeetingKind`: blue for recorded meetings, purple for manual notes, written so collapsing
to one uniform colour is a one-line change. One meaning, one colour extends to the list.

**Decision: the transcribing loader is a status strip, so it behaves like one.** `Summarizing`
joins the recording states at TopGap 2 (flush with the screen top) instead of hovering 12px into
the screen, and its auto-tuck idle timer both re-arms when the cursor is over the pill (it used to
fire once and die silently, leaving the loader open forever) and runs on the 2s result beat rather
than the 9s page beat - a loading pill has nothing to interact with. The idle decision is a pure
function (`IdleDecision`) so the timer's behaviour is unit-testable without a window.

**Decision: the mascot's appearances are paid for up front.** Two first-show glitches shared a root
shape: work done on the animation's first frames. Collapsing to a peek deferred the surface swap
240ms while the peek's own Toki faded in - two Tokis, then a one-frame disappearance; the bubble
disc now fades out over 180ms and the deferred swap re-arms the reveal. And the first expand built
its views and rasterised the mascot's `RenderAtScale=4` bitmap cache synchronously mid-animation,
eating the opening 50-150ms; `WarmFirstOpen` now builds the first-open views and renders the
mascot once off-screen at `ApplicationIdle` during launch, mirroring the model-warm philosophy: a
meeting never waits on a download, and an animation never waits on a rasteriser.

**Decision: the peek flip is character motion, sized to be seen.** The peeking Toki shrank to 27px
and sits 5px lower so its eyes clear the card's clip with air above (the old geometry put the top
3px of eye above the clip - measured from the eye path's coordinates, not eyeballed). The flip
dropped its two-bounce `BounceEase` (jitter at 30px) for one 520ms turn on `Motion.Move` with a
single soft `BackEase` overshoot on the fall, ~540ms total: slow enough to read as fun, inside the
workflow bound, and exempt from the 350ms cap because character motion is the one named exception.

## The film round (2026-08-19, late night)

**Decision: the peek blooms into everything; in-place snaps are for refreshes only.** The white
flicker the owner filmed was the meeting prompt's own white pill rendering alone over the desktop:
`Collapsed -> MeetingPrompt` was missing from the hover-pair list, so it took the in-place path
(instant full-size snap, content fading in, plate and mascot late). `IsHoverPair` now treats the
collapsed peek (and the loader sliver) as blooming into EVERY target. Second cause, same frame: a
refresh-triggered `GrowToContent` raced the transition because AppState notifies App's
view-refresh handlers before the window; the grow morphed the box while surface and content were
still the old state's, so the real transition arrived with nothing left to animate. Grows are now
gated on a `_shownState` stamp: a grow may only run for the state the window is actually showing.

**Decision: the resting squircle clip trusts the CARD, not the arranged box.** The settle rebuilt
the resting clip from Root's arranged size, and a tucked 13px sliver arranges at the window's
~50px bubble floor - so the settle silently un-clipped every tucked sliver back to a full card.
The clamp: if the card's asked size differs from the arranged box by more than 1px, the card is
deliberately smaller and the card's size wins. Corollary in the same fix: the mascot bubble and
his say-bubble are siblings OUTSIDE the card clip, so a tucked state must hide the mascot zone
explicitly (opacity, restored by SetSurface's existing path) - clipping the card never touched him.

**Decision: transient surfaces open on dwell and close on leave, not on timers.** The loader
ping-ponged (graze opens instantly, a 2s idle beat tucks, repeat). The grammar now: a 360ms dwell
(`SliverDwellMs`) means "the user came here on purpose"; a 540ms grace after the cursor leaves
(`LoaderTuckGraceMs`) means "they left"; the idle timer stays only as a backstop. The notes-ready
announce follows the same grammar: `Summarizing -> Notes` enters as a "Notes ready" pill + Toki
for `NotesReadyPeekMs = 2000`, click (or the same dwell) opens the note, ignored folds to the
ambient peek with `_context` still on Notes so the note stays one summon away.

**Decision: nothing inside a disc trusts inherited text metrics.** `Paragraph.LineHeight` is an
inherited dependency property: the transcript's 19.5px line height reached the TextBlock inside a
16px avatar and inflated its line box, pinning the capital's ink to the disc top (a standalone
probe reproduced the owner's screenshot to the half-pixel). `PillParts.DiscLabel` is now the one
way to put a letter in a disc: LineHeight pinned to the font size, BlockLineHeight, a -0.5px
optical lift, and the SubPixel opt-out - and `Outline()` glyphs got the same opt-out (an 11px
chevron in a 26px puck at 125% is 9.375 device px a side; layout rounding split it 9/10, which
was the visible leftward lean).

## The ghost frame round (2026-08-19 afternoon)

**Decision: a layered window may only Hide() after its final frame has COMPOSED.** The persistent
corner flicker was the OS, not our painting: `Hide()` on the animation clock's Completed fires a
frame before the fully-shut clip is composed, and a WS_EX_LAYERED window RETAINS its last composed
frame - so the next `Show()` presents the stale ~13px card stub until the reveal's first real
frame. Theme-independent by mechanism (the surface replays whatever it held). The rule: pin every
leaving element at its parked value, wait two `CompositionTarget.Rendering` frames, then Hide and
park, generation-guarded on both sides. A Dispatcher priority hop is NOT a substitute - priorities
order UI-thread work, and presentation happens on the render thread; only Rendering callbacks
prove frames actually composed (the priority-hop attempt was filmed still flashing).
`--revealtest` now reads a hide-audit seam and fails if the hide composed a non-empty frame.

**Decision: transitions measure BEFORE they mutate.** The expand painted the new surface, then
stalled ~100ms in `ContentHeight` measuring the incoming view - a bare card with neither state's
content, moving as layout churned. Transitions now do ALL measuring first; the old state owns
every pixel through any stall, and the visual mutations are packed to compose in one frame.

**Decision: the notes announce exits as character motion, and the landing is silent.** Unclicked
after `NotesReadyPeekMs`, Toki hops out of his dissolving bubble (the disc is HELD Hidden after
its fade - an opacity alone was resurrectable), somersaults once (-360, a dedicated `MascotFlip`
transform), shrinks 44 to 28px, and flies 560ms to the collapsed peek's exact resting spot while
the pill conceals beneath him. `Notes -> Collapsed` is a hover pair with a dedicated silent
branch: the card bare-ifies in place (including `Material.Visibility` - the grain layer was
filmed as a rimmed card-ghost when skipped), the window sizes silently, and the peek's own Toki
cross-fades in under the flyer's fade-out. The flyer's X compensates the window recentre by the
computed card-width delta, because `Left` updates asynchronously and reading it back was a filmed
~30px landing offset. Click on the landed peek opens the notes via the existing context path.

## The unparseable note (2026-08-19 night)

Meeting 101's note shipped as the heuristic floor because the final LLM pass answered in a shape
the parser refused: every line a bullet, headers included ("- SUMMARY: ..."). Decisions:

- **A parse failure earns a retry, not a fallback.** The passes were "validated, retried once"
  only against DEGENERATE output (CJK, repeat runs); a fluent answer in the wrong shape went
  straight to the floor. At sampling temperature (0.7) a second ask is a genuinely different
  sample, so `FinalNote` now retries once on a parse miss, with an explicit header reminder
  appended. Only after two unparseable answers does the heuristic floor ship.
- **Strictness keeps its reason, loses its monopoly.** The rule "a bullet is never a header"
  exists so a KEY POINTS bullet reading "Next steps: ..." cannot hijack the actions parser. It
  stays. But when the strict parse matches NOTHING, there is no parsed content left to protect,
  so a rescue pass (`PromoteBulletHeaders`) un-bullets exactly the lines that match a known
  header and runs the same strict parser again. Header recognition itself was deduplicated into
  `HeadOf`/`MatchHeader` so the strict parser and the rescue can never disagree.
- **Log what the model wrote, not just that it failed.** The original failure line carried only
  a character count; the actual 1230 chars were gone forever, and the diagnosis above is partly
  inference. Parse failures now log a bounded excerpt of the raw output.
- Guarded by two MUTATION PROBES in `LlmNotesTests` (bulleted-headers rescue, retry-once), both
  verified failing against the unfixed code.

## The production pass (2026-08-19 late night)

Four decisions from the pre-field-test quality round:

- **The LLM never owns every core.** llama.cpp defaults to all logical cores, and a generation
  visibly hitched the loader animation - the render thread starved. `LlamaLlm` now sets
  `Threads`/`BatchThreads` to `ProcessorCount - 2`. UI smoothness is a budget the model must
  live inside, not compete with; a note that lands two seconds later is invisible, a stuttering
  loader is not.
- **Transient surfaces open on click, never hover.** The tucked loader sliver and the "Notes
  ready" pill live in the top-centre band every cursor crosses; any hover trigger there, however
  dwelled, interrupts work. The recording peek set the precedent (owner rule); the loader and
  the ready-pill now follow it. A window-level MouseLeftButtonUp expands a tucked
  Summarizing/Notes state; the hover poll no longer can.
- **One note surface, one document.** The notch's Notes page now embeds the same FlowDocument
  the Library renders (`MeetingDocument` grew a `scrollHost` embedded mode: no title block, no
  own scrollbar, evidence links scroll the host page). The parallel panel-of-TextBoxes renderer
  is deleted - it capped drag-select at one line and was a second implementation of the same
  note that could drift. One renderer, two hosts.
- **The heuristic floor is capped.** Uncapped, the floor is a transcript sieve (92 questions on
  a 17-minute meeting). It now keeps at most 10 questions / 10 decisions / 12 actions, drops
  sub-4-word lines, and prefers owner/due-carrying actions. The full transcript remains one
  section away.

Also in this round: "Built with" + THIRD-PARTY-NOTICES now credit Silero VAD v5 (MIT), pyannote
segmentation-3.0 (MIT) and 3D-Speaker CAM++ (Apache-2.0) - all three ship and were uncredited,
which is a licence gap, not cosmetics. Credit rows became real Buttons (RowButton).

**The comment cleanup.** Six parallel agents removed ~2,300 lines of session-diary comments
across src/Toki (dated stories compressed to their invariants; code lines byte-identical;
doubled/orphaned `<summary>` blocks repaired). Dead-code candidates surfaced by the pass, kept
pending an owner decision: `MeetingStore.Reorder` + the `sort_order` column (drag-reorder was
removed), `MicrosoftAuth.RevokeAsync`, `CalendarProviders.MeetingLinkName`,
`CalendarRows.GlanceSubtitle`/`RightAffordance`, `NotesView.Ago`, `LibraryWindow.CopyTranscript`
and the superseded `CopyButton`/`SeamButton`/`Flash` path, `PillWindow.LoaderBubble`,
`StreamCtl.Append`, the dormant `GlassTopBleed` machinery, unused region P/Invokes,
`HitTargets.OffCentre`, and the single-segment `EvidenceAudio` overloads (test-only).

## Low frequency, not high: the grain comes out (2026-08-20)

**Why the plate is not the frame's 0.5 alpha, recorded once so it stops being re-asked.** Figma node
293:1875 builds the card from three things: a `rgba(33,30,30,0.50)` fill, a `#333` layer in
color-dodge, and the frame's background blur. The 50% is only legible BECAUSE of the blur - it is a
recipe, not a target value. We cannot run the recipe (see "The Apple-glass material", 2026-08-18:
three independent blockers, question closed) and Windows has no color-dodge compositing here, so
`CardPlate` bakes the recipe's RESULT at ~95%. The frame's inner capsules are a different case: they
sit on our own card, not the wallpaper, so those DO render at the frame's translucency and match it
directly. The token table's `--tk-glass: rgba(33,30,30,0.50)` is therefore the SOURCE value, not
what the notch paints.

**Decision: the card carries NO fill of its own. Plate, sheen, edge rings, nothing else.**
Supersedes the "fixed-seed 64x64 grain at 2.5% dark / 2.0% light" layer in the 2026-08-18 material
stack, and supersedes the light field that briefly replaced it the same day.

Two rounds, and the second is the interesting one. The grain went first: a 64px tile repeats about
five times across a 344px card, and the owner read the result as texture, which is the opposite of
glass. The reasoning for its replacement was sound as far as it went - a blur destroys HIGH spatial
frequencies and keeps low ones, so grain is the one thing a blurred backdrop cannot leave behind,
and a soft diagonal sweep is what it DOES leave. That shipped as `CardGlow`.

The owner then reported "strands of lines that go sideways on it very faintly". That is BANDING. A
gradient spanning ~344px at under 6% alpha has only a handful of 8-bit quantisation steps across
its whole run, so the steps land as wide diagonal stripes. **The grain had been dithering them away
all along**, which is the classic reason noise sits in a material in the first place. Removing the
dither did not just remove texture; it exposed what the dither was hiding. The earlier render check
missed it because banding of a few code values does not survive image scaling in a screenshot the
way it survives on a real display.

Rather than reintroduce noise at a lower amplitude (which re-opens the texture complaint the owner
raised in the first place), the card is now plain. Depth comes from the parts that cannot band:
the 1-device-pixel gradient rim, the fixed 48px radial top sheen, the 2px inner edge rings, and the
two stacked shadow casters.

**The generalisable rule: a broad, very-low-alpha gradient needs dithering, and a material that
wants no visible texture therefore cannot have one.** Pick a shape whose value range is either wide
enough to step smoothly or small enough in area to hide the steps.

`CardMaterial` loses the noise builder, its `ImageBrush` cache, the `ThemeChanged` subscription
that existed only to rescale grain opacity, and now the glow layer too, so the control is three
static layers with no lifetime. `Grad.Sweep` and the `CardGlow` token are removed rather than left
unused. `GlassCard`'s `grain:` parameter is renamed `material:`.


## Brevity is enforced, not requested (2026-08-23)

**Decision: a deterministic tightening pass runs over every written note, on BOTH paths.**
`Llm/Brevity.cs`, applied at the end of `LlmNoteWriter` and at the end of `NoteExtractor`.

The owner's report was that the to-dos and deliverables read long and full of filler next to other
notetakers. The prompt has asked for concision since the note writer was built, and it does not
hold: a 4B model at sampling temperature writes reported speech ("Craig mentioned that the pilot
runs four weeks") and hedges ("basically", "I think", "in order to") however firmly it is told not
to. This is the same shape as the em-dash rule, where a prompt line was not enough and a scrubbing
funnel was.

What the research says, and why the fix is shaped this way. The meeting-summarization literature is
clear that transcripts have low information density and that disfluency is what makes extracted
utterances unreadable, so removing disfluency and fusing content is the standard remedy, not a
stylistic preference. And the products the owner compares us against are short for a structural
reason rather than a prompting one: a bullet is one line on a card, the owner's name lives in its
own capsule beside the text, and the supporting detail lives behind a link to the transcript. The
line does not have to carry any of it.

So the pass: strip the reported-speech frame, strip hedges, drop the owner's name from an action
whose row already prints it, then cap length. Three rules keep it honest.

- **It only ever removes words.** No paraphrase, so it cannot invent.
- **It refuses to mangle.** The cap keeps whole sentences while they fit, then cuts at a clause
  boundary, and a line offering no clean cut is returned WHOLE. A readable long bullet beats a
  bullet clipped mid-thought.
- **A name that is not the owner is content, not a frame.** "Craig will review the contract" on an
  item owned by Priya keeps Craig, because stripping him would attribute the work to the wrong
  person.

Caps: 14 words for an action, 22 for a bullet. Guarded by a probe verified failing without the pass.

## The carry-forward matched on one word (2026-08-23)

**Decision: "Open from previous meetings" requires distinctive overlap, not any overlap.**

The matcher took the current meeting's title, split it into words of three letters or more, and
accepted any earlier meeting within 45 days sharing ONE of them. The age cap had been added for
exactly this reason, and the file's own comment warned that "a stale record hands its to-dos to
every later meeting whose title shares one word with it", but the overlap rule itself was never
tightened. In a real library where titles are "Pricing sync", "Design sync", "Weekly sync", that
rule connects almost everything.

Now: generic title words (sync, review, weekly, standup, call, catch-up, update, team, and the rest
of that family) are removed before comparing, and a single shared word only carries a match when
one side has nothing else to offer - a one-word project title like "PixelFlow" is a strong signal
on its own, while one word in common between two three-word titles is a coincidence.

The asymmetry is the same one the name corrector is tuned to: a missed carry-forward is a note that
says nothing, a wrong one attributes somebody else's unfinished work to this meeting. Title text
alone cannot be made reliable, so the other half of the answer is making CORRECTION cheap, which is
the dismiss control the owner asked for (still to build).


## How to know whether a matching change is an improvement (2026-08-23)

**Decision: an evidence-linking change ships on a paired measurement, never on an argument.**
`--linktest` is the instrument; `Evidence.WeightByRarity` is the first thing it judged, and it said no.

The problem is that you cannot eyeball a retrieval change. Whatever the matcher returns looks
plausible, because it returns the line that shares the most words with the bullet, and shared words
are exactly what makes a line look relevant. There is no independent answer to compare against.

Information retrieval's answer is a ground truth set plus precision@1, and where nobody has
hand-labelled anything, SYNTHETIC queries whose correct answer is known by construction. So:
take a real transcript line, keep some fraction of its content words, add words borrowed from
elsewhere in the same meeting (a summary generalises and borrows vocabulary, and the borrowed words
are the distractors), and the line it came from is the answer. Deterministic seed, because a
benchmark that resamples between runs cannot separate an improvement from a different sample.

Three design decisions that make it honest:

- **TWO conditions, both reported, chosen before looking at either.** The first attempt used one
  easy condition and both scorers hit 94%, a ceiling where nothing can be learned. The hard
  condition (keep a third of the words, three distractors) is the case that matters, because a real
  bullet is abstractive. Reporting only the flattering condition, or only the one that agrees with
  you, is how a measurement becomes an argument.
- **Paired, on identical cases, judged by McNemar.** Only the cases where the two scorers DISAGREE
  carry information; the ones they both get right or both get wrong say nothing about which is
  better. Continuity-corrected chi-square, 3.84 for p<0.05.
- **The absolute number is not a quality claim.** A synthetic bullet is built from its source line's
  own words, which is the friendliest possible case for word overlap. Only the paired delta is a
  claim.

**The result, which contradicted the reasoning.** Rarity weighting (inverse document frequency over
the meeting's own lines) is intuitively right: "PixelFlow" said three times should identify a moment
better than "deck" said twenty. Measured over 400 cases per condition: easy, identical with zero
disagreements; hard, flat 82.9% versus weighted 81.9%, nine disagreements, chi-square 1.78. Noise,
trending slightly the wrong way. It stays off.

**The remaining gap, stated rather than papered over.** Synthetic queries are bags of words with no
syntax; real bullets are sentences that may share almost nothing with their source. The true gold
standard is a hand-labelled set of REAL bullets, which is the next step if link quality matters
enough to spend the owner's attention on. And the deeper fix is architectural rather than
algorithmic: record provenance AT GENERATION (which transcript chunk a bullet came from) instead of
recovering it afterwards by text similarity, which is what the attribution literature recommends and
what Otter does by keeping word timings rather than matching summary text.


## Provenance beats recovery (2026-08-23)

**Decision: a written line records the transcript range the model was READING when it wrote it, and
the evidence link searches that range instead of the whole meeting.**

Recovering provenance after the fact is a search for the line that shares the most words with a
bullet, and shared words are exactly what makes a wrong line look right. A meeting that says
"send the pricing deck" twice has two equally good answers and no way to choose. The attribution
literature says the same thing about citations in generated text, and it is what Otter does by
keeping word timings rather than matching summary text back to audio.

The note writer already had the answer and threw it away. It reads a long meeting in windows, maps
each to a digest, then writes the note from the digests, so every digest has a known line range.
Now: `Chunk` returns a `Window(Text, First, Last)`; the digests keep their range; after the final
note is parsed, each bullet and to-do is matched against the DIGESTS (two short texts in the same
vocabulary, a far easier match than a summary against raw speech) and inherits that digest's range.
`Evidence.BestRange` takes it as a hint and searches only those lines, with one line of slack either
side because a chunk boundary can fall mid-thought.

Three refusals keep it honest:

- **A digest match under a third of the bullet's words records nothing.** A confident wrong range is
  worse than no range, and no range simply falls back to the old search.
- **A second reduce round drops provenance entirely.** A digest of digests cannot be traced to a
  line range without guessing.
- **A nonsense stored range falls back** rather than returning nothing, so a hand-edited note or a
  future schema change cannot silently remove every link.

**Measured, paired, on identical cases** (`--linktest`, 400 synthetic bullets per condition):

| Condition | Whole transcript | With provenance | McNemar |
|---|---|---|---|
| easy (half the words kept) | 95.9% | 97.7% | +7, chi 5.14, p<0.05 |
| hard (a third kept, 3 distractors) | 82.9% | 87.5% | +16, chi 14.06, p<0.01 |

Zero cases got worse in either condition, which is the property that matters: it is a smaller
haystack, not a different guess. Compare with rarity weighting, measured the same way on the same
day, which was noise and stayed off.

Storage: `action_items` gained nullable `src_first`/`src_last`; `NoteSection` gained an
index-aligned `Sources` list inside the existing `sections_json`. Both are null on every note
written before this, which reads as "no record" and costs nothing.

## One way to move the caret (2026-08-23)

**Decision.** `NoteEditor.SetCaret(TextPointer)` is the only place in the editor that assigns
`Editor.CaretPosition`. Nothing else may.

**Why.** WPF's `TextEditor` keeps a `_suggestedX`, the horizontal position a vertical arrow key
aims to preserve, and it is only reset when the user moves the caret, never when code does
(dotnet/wpf#3757). Every programmatic caret move therefore left a stale column behind, and the next
Up or Down jumped to it. Measured at 67.3px in, Down landed at 5.0px.

Scattering the reset across the eight call sites would have worked once and rotted the first time
somebody added a ninth. Making it impossible to move the caret without the reset is the version that
survives, and it is the same rule as `TryMarker`: one definition, no second way to do it.

**Cost.** The field is internal, so `ClearSuggestedX` finds it by reflection, probing both the
`TextEditor` property and the `_textEditor` field, and caches the lookup. It is wrapped in a
try/catch that degrades to plain WPF behaviour: a future .NET that renames the field costs the
column, not the note.

**Guard.** `--notetest` measures the caret's x before and after a real `MoveDownByLine`, with a
floor on the starting x so it cannot pass at zero. Reverting `SetCaret` to a bare assignment fails
it.

**Researched, open, and deliberately not decided here** (see the README entry): Tab's meaning inside
a note, Enter and Backspace over a multi-block selection, and skipping the derived repair pass while
an IME composition is active.


## The marker's box is a clip, not a hint (2026-08-24)

A note's bullet, number and tick box are `InlineUIContainer`s carried in the paragraph's inlines, so
they can never be read back as part of the line's text. Their host `Border` used to be given an
explicit `Height` of the body text's cap height, on the reasoning that a marker should occupy the
same optical band as the capitals beside it.

**The finding: an `InlineUIContainer` CLIPS its child to the host's layout box.** The Border does not
draw a clip and `ClipToBounds` is false; the clip comes from the text line's hosting of the element.
So the Height was not a hint about where to sit, it was a knife. At a 16px body the host was 11px and
the tick box is 24px: thirteen pixels of every box were cut away, and the top of every digit with
them. Removing the Height alone proved it in one render.

**The rule this leaves behind: never give a marker's host a Height.** Sizing a container to less than
its child, anywhere inside a FlowDocument, deletes ink rather than compressing it.

**How the marker lands on the band instead.** The host sizes to its child, `BaselineAlignment.Baseline`
puts the host's bottom on the text baseline, and a `TranslateTransform` on the host sinks it by half
the difference between the host and the cap height. A transform moves INK without moving the layout
box, which is the only correction that does not re-create the clip. The number is the exception: a
dot and a tick box are shapes whose ink is centred in their box, but digits stand on a baseline with
the font's descent as empty space below them, so the number rides back up against that sink and is
aligned by baseline.

**And the row height comes from the paragraph, not the marker.** A marked line is pinned with
`LineStackingStrategy.BlockLineHeight` to the body text's natural line height, so a marker taller than
the line overflows it (the line does not clip; only the host ever did) instead of stretching it. Before
this, a numbered or to-do row measured 28px against a bullet's 21.3px and a mixed list came out ragged.

**Superseded, dated:** the comment on `MakeMarker` claiming the host "is then given exactly the cap
height, so the marker occupies the same optical band" described a mechanism that had never worked as a
picture. Kept here because the INTENT was right and is preserved; only the mechanism changed.

**A second finding, not acted on.** `LineStackingStrategy.MaxHeight` is a FlowDocument's default and it
ignores `LineHeight` entirely, sizing each line to its own content. `Editor.Document.LineHeight` has
therefore been inert since it was written: the note's documented 1.5em leading has never been in force
and the real leading is the typeface's own (~1.33em at every size). Marked lines are pinned to the real
one, so nothing on screen moved. Raising the whole document to a true 1.5em is a visible change to every
note ever written and is left as the owner's decision.

## What a test measures, and what it does not (2026-08-24)

`--notetest` asserted that each marker's centre sat on the text's cap band, to a tolerance of 0.6px, and
reported 0.10px for months while two thirds of every tick box was being clipped away. The assertion was
true. It was also the wrong question, and the mutation probe that guarded it (reverting the alignment)
could not fail in the new way because centring and clipping are independent.

The check added beside it asks the second question: measure the marker with NOTHING constraining it
(`Measure` at infinity, then put the layout back) and compare to the box it was given. Reading
`DesiredSize` as it stands is useless here, because the constraint has already clamped it - the first
version of this check did exactly that and passed against the unfixed code.

**The shape to look for:** when a check reports a property of an element (position, size, count), ask
what it would still report if the element were invisible, clipped, or off screen. If the answer is "the
same", the check is measuring a model of the product.

The same gap existed in the pictures. The render harness photographed the note page only in a narrow
three-column window, where a centred measure and a left-aligned one look identical, and it had no
picture of a template at all. Both are now shot: `hub-manual-note-wide.png`, `hub-manual-note-blank.png`
and `NoteEditor-template.png`. A page only tests as wide as you photograph it.

**Harness gotcha found doing it:** after changing a window's Width, `RenderTargetBitmap` photographs an
empty panel even though the content has arranged correctly (measured at 600x775 inside a 1184px pane).
It draws what has been RENDERED, not what has been arranged. Pumping the dispatcher to
`DispatcherPriority.Render` before the shot is what puts the text on the visual.

## One commit path, or the mouse ships a different product (2026-08-24)

The slash menu's typed text ("/todo", "/standup template") was taken back out of the document by the
Return case in `NoteEditor.OnKey`. The row's own Click handler closed the menu and ran the command. Two
ways to choose a command, one cleanup, so every command picked with a pointer left its "/" in the
writing - and picking a template with the mouse is exactly how a person picks a template.

`SlashMenu.Run` is now the single path both go through, and the cleanup is a callback the menu owns
rather than something a caller has to remember. The self-test drives a real row Click, because a test
that only ever presses Enter cannot see a bug that only the mouse reaches.

## The canvas measured against the references (2026-08-24, second pass)

The owner's follow-up was that the canvas still did not feel like Apple Notes, Notion, Notability or
Notepad, and asked why. Audited against the numbers already measured from Notion's production CSS in
`.claude/skills/block-editor/SKILL.md` §12. Four deltas, all now closed.

**1. The markers did not share a left edge.** `MakeMarker` right-aligned every marker inside the
gutter, on the reasoning that a constant gap to the text is what makes a list even. MEASURED: a
bullet's ink started at 24.0px, a number's at 19.6px and a tick box's at 8.0px - a 16px spread down
the left of any mixed list, against headings and paragraphs that all start at 0. Right-alignment is
the correct rule for NUMBERS ALONE (so "9." and "10." end flush), and the skill said so; it was
applied to all three. Now every marker is left-aligned on the text margin, which is one left edge for
the whole canvas. The text still starts at a fixed column, so nothing after the marker moved.
Guarded by "every marker starts on the same left edge"; reverting to Right fails it.

**2. The marker column was 1.75em.** Notion's measured column is 24px at a 16px body. `MarkerRatio`
is 1.5 now, which is that number as a ratio.

**3. The leading was the typeface's, not the document's.** Recorded in the entry above: a
FlowDocument's default `MaxHeight` stacking ignores `LineHeight`, so the note's stated 1.5em had
never applied and the real leading was ~1.33em. Every kind now sets `BlockLineHeight` explicitly:
body and list items at 1.5em, headings at 1.3 of their own size (a heading wants tighter leading or a
two-line title reads unstitched). This DOES re-space every existing note, deliberately, and it is the
single largest reason a page of text reads as calm.

**4. A heading owned space below it as well as above.** Notion's rule, measured, is that the heading
owns the space ABOVE it and nothing owns the space below. Toki had 1.75em above and 0.62em below, so
a heading followed by an empty line - which is exactly what a template is - opened a gap of nearly
seventy pixels that read as a hole in the page. Below is 0 now.

**SUPERSEDED, dated 2026-08-24: "a blank line is a beat, not a full empty row."** A `Blank` was drawn
at 9/13 of a line. The intent was that pressing Enter twice should give a small breath rather than a
canyon. In use it was neither predictable nor clickable, and it is not what any editor a person has
already learned does - Notepad, Apple Notes, Notion and Word all give a full empty line. The owner
asked for a notepad. A blank line is a full line now. The original reasoning is kept here because it
is why the smaller value existed, and if a future pass wants a tighter beat it should come from the
paragraph gap, not from a line that the caret has to live on.


## Ask reaches hand-written notes (2026-08-24)

The owner asked whether manual notes could be queried, or at least retrieved. They now can, and the
line drawn is deliberate.

**Retrieval is open; enumeration is not.**

- `SearchRelevant` (FTS content search) and `TitleMatchIds` no longer filter on `kind`. A note is
  found by what it SAYS and what it is CALLED, which is how a person looks for something they wrote.
- `MeetingTimes` - which backs `LatestMeetingIds`, `OldestMeetingIds` and `MeetingIdsInRange`, and
  through them "my last meeting", "how many meetings did I have" and "meetings last week" - is STILL
  `kind='Meeting'`, on purpose. A hand-written note is not a meeting, and counting one would make an
  answer the user can check against their own calendar wrong. A miss is recoverable; a confidently
  wrong count is not.

**The bug this closes is worse than "cannot find".** The router's evidence probe used the same two
filtered queries, so a question whose only answer was in a hand-written note was classified as a
GENERAL turn and answered from the model's parametric knowledge with no citation. Not an empty
result: a confident answer about something else. Opening the two content queries fixes the probe as a
side effect, because it is the same call.

**`BuildContext` had to change too, and this was the trap.** It renders Tldr, sections and actions -
none of which a manual note has, all of it being in `Body`. Letting notes through retrieval WITHOUT
this would have retrieved a note and then cited it as empty, which is the "wrong evidence is worse
than none" failure in its purest form. A manual note now renders its body, labelled "(your own
note)", so the assistant can say "in your note" instead of implying somebody said it out loud.

**`VisibleMeeting` also required `Kind: MeetingKind.Meeting`**, so opening your own note in the
Library and asking "summarise this" grounded on nothing at all. It accepts any viewed record now.

**Sticky notes remain excluded** and that stays deliberate: they live in their own tables, are absent
from the `FtsSelect` definition, and are the one surface Toki never reads.

**Guard:** `--dbtest` asserts a hand-written note is reachable by text and by title AND is absent
from `LatestMeetingIds` and `MeetingIdsInRange`. Putting either `kind` filter back fails it.

## Acting on the taste audit (2026-08-24)

Applied from `docs/design-audit-2026-08-24.md`. What was NOT applied is listed after, with reasons -
the audit is a report, and a review that only produces additions was not a review.

**Hierarchy.** The hub Ask greeting dropped from 30px to 19px. It was the largest type in the window
and the only element carrying no information; the suggestion chips it was shouting over are the reason
the page exists. Demoted, not replaced - the rule is that emphasis comes from de-emphasising.

**Narration deleted.** `LandingHint` was "Your meeting assistant." - one line telling somebody looking
at Toki what Toki is. The constant is now empty (the slot still carries voice hints). The "Appearance"
caption "How Toki looks and what it shows in a share." is gone; its first half restated the heading and
its second half was real information, which has moved onto the "Show Toki in screen recordings" row as
a CONSEQUENCE ("Off keeps Toki out of screen shares and recordings"), which is what helper text is for.

**Voice.** The notch greeting lost its exclamation mark - the hub's version ended in a full stop and
the notch's did not, so one sentence had two voices depending on the surface. "New Chat" became "New
chat": Title Case On Every Label reads as a template.

**Templates rebuilt as rows.** Each template was a rounded card with 16px of padding, which put every
template's text 16px right of the page title above it - two left edges in one column of content. The
cards are gone: space and a hairline separate them, and the page now sits on one gutter. The section
names are plain text rather than a row of grey pills, because they never change and none of them is
clickable ("a constant is a label"), and nine identical chip rows flattened the page so nothing could
rank. The current default now carries a "Default" tag, which is a badge whose value actually changes,
and the all-caps "DEFAULT SHAPE FOR NEW MEETINGS" became sentence case on the same gutter.

**The loader names its stage.** `App.SummarizeStage` is set to "Transcribing" and then "Writing your
notes", and `SummarizingView` reads it. The wait was a single unchanging "Tidying up your notes" across
transcription plus several local-model calls, which on a long meeting is minutes and is
indistinguishable from a hung app. NO time estimate is shown: one would have to be measured, and a
fabricated estimate is the same lie as a progress bar that tracks nothing.

### Deliberately not changed

- **The icon-plus-label buttons on notch Home.** The audit flags an icon repeating its own label. Icon
  plus label on a primary action is a convention users arrive with, the notch is glanceable-first, and
  the skill explicitly yields to platform convention. Recorded so it stays a decision.
- **Green Settings toggles and the pink Summarizing sliver.** Both are second meanings for colours the
  system has already assigned (green=decisions, pink=questions). Green-for-on is also a strong platform
  convention, so this is a choice between two conventions and it belongs to the owner, not to a pass
  that was fixing a note editor.
- **Ragged suggestion chips on hub Ask.** Content-sized pills that wrap are normal; forcing a grid
  would cost more than it buys.
- **notch Home's three actions in two treatments.** Real finding, but the fix is a layout redesign of
  the product's front door, not a refinement, and it needs the owner's eyes on a proposal first.


## A size floor is not a size (2026-08-24)

`PillWindow.ContentHeight` returns `Math.Max(SizesHeight, measuredContent + BottomBreath)` for every
auto-height page. The `Sizes` table height is therefore a FLOOR and never a cap, which is correct and
is what lets a page grow, but it means a floor set ABOVE a page's real content pads the card out with
space the page never uses.

Onboarding sat at a floor of 250 while its three steps measure 203, 227 and 152: up to 98px of dead
card on the last step, and 47 on the first, which is the one the owner reported. The floor is 130 now,
low enough that the measure always wins and high enough to guard a measure that returns nothing.

**The general shape, and why no test saw it.** Every check around panel sizing asked whether a page was
CLIPPED - whether the out-of-tree measure under-reported the true arranged height. Nothing asked the
opposite question. `--measuretest` now walks every auto-height state, compares its `Sizes` floor
against its real measured content, and names the number of dead pixels. Reverting Onboarding to 250
prints "DEAD SPACE 47px (needs 203, floor 250)" and fails the run.

This is the same failure shape as the clipped note marker earlier the same day: an assertion that is
accurate about the thing it measures, and silent about the thing that is wrong. Ask what a check would
still report if the element were invisible, clipped, off screen, or floating in dead space.

## The heuristic floor must be honest, not just present (2026-08-25)

A recent meeting's summary and to-dos were its opening lines of conversation, verbatim. The diagnosis
was two failures stacked, and both matter:

- **Trigger:** the on-device model failed to load that session with `The type initializer for
  'LLama.Native.NativeApi' threw`. That is the llama.cpp/Vulkan NATIVE backend failing to
  initialize. A .NET type initializer that throws is cached for the life of the process, so once it
  fires, `LlmService._localFailed` sticks and EVERY meeting that session drops to the floor. This is
  why the leak "kept coming back": it is intermittent at the GPU/driver level and total once tripped.
- **Leak:** the floor (`NoteExtractor`) was itself dishonest. Its TL;DR concatenated the first ~140
  chars of speech, and its to-do rule accepted any sentence that CONTAINED an action word - which
  the brevity pass then truncated into fragments. The model path had already been taught that raw
  first lines are not a summary (leave it empty; the renderer drops it); the floor never learned it.

Decisions:

1. **The floor emits no summary** (empty) and only accepts a to-do that is SHAPED like one - a
   commitment (`I'll`, `will`, `need to`, `action item`, ...) or an imperative opener (`send`, `fix`,
   `review`, ...). A statement of fact is not a task. This mirrors the model path exactly, so the two
   can no longer diverge. Guarded by two mutation probes in `NoteExtractorTests`.
2. **A native poisoning is distinguished from a transient failure** (`LlmService.LocalPoisoned`), and
   `RetryLocal` / `App.RegenerateNotesAsync` clear the stuck flag and re-attempt. A transient failure
   (busy GPU) recovers in-process; a poisoning cannot (the exception is cached) and the banner says
   to restart. There is no way to un-poison a static type initializer without a new process, so the
   honest floor is the real guarantee - the retry is the upgrade path, not the safety net.
3. **The fallback is surfaced, never silent** (`NoteRenderer.FallbackBanner`): a floor note that a
   model could improve shows "Basic notes" + Show transcript + Try again; API-mode-without-a-key
   shows a privacy-clean on-device nudge (not an error - local notes are good). A note the user
   deliberately set to Heuristic-only gets neither (CanWrite is false).
4. **Ghost mode is reachable from the recording bar** (an eye / eye-off puck via `PillParts.Eye`/
   `EyeOff`), for the case where recording was started before ghost mode was turned on. It calls the
   same `App.SetGhostMode` the Settings toggle does, which re-asserts WDA_EXCLUDEFROMCAPTURE live.

The recurring shape, worth naming: a fallback that dresses raw input up as finished output is worse
than one that admits it is a fallback. When the good path is hardened against a mistake, harden the
degraded path against the SAME mistake - it is the one that runs when things are already going wrong.

## Evidence clips: precision, not non-overlap; and the note page's one gutter (2026-08-25)

Two owner complaints, one about the audio behind a note point and one about the note layout.

**Audio.** The 2026-08-24 `DisjointRanges` (make every point's clip use DIFFERENT transcript lines so
none overlap) was the wrong model and is removed. It is a greedy assignment: when several points come
out of one run-on utterance it gives the FIRST point the whole stretch and the rest scraps or nothing
("the first to-do's clip covers all the to-dos"). The right model is PRECISION - each point resolves
on its own (`Evidence.BestRange`), and `EvidenceAudio.Window` NARROWS the clip to the phrase inside the
utterance that carries the point. Overlap is then natural and small (two points that share words), never
containment. `MaxClipMs` dropped 45s -> 12s: a clip is a moment, not the utterance.

The narrowing distributes word times PROPORTIONALLY across each utterance's duration, because we store
per-UTTERANCE timestamps, not per-word. That is a guess, so it is gated: narrow only when >= half the
point's words are actually in the utterance, else keep the whole (capped) utterance clip - a clip that
plays the right moment beats a confident cut to the wrong second (measured: a to-do that played "Okay.").
`--evidencetest` re-transcribes each cut and now flags CONTAINMENT (one clip inside another) as the
defect, not any overlap. HONEST LIMIT: for several to-dos spoken in one breath, sub-second cuts need the
recogniser's per-WORD timestamps, which Parakeet/sherpa emit and we currently discard when grouping
tokens into segments. Storing them is the proper fix (schema + transcriber + a migration) - owner-gated
TODO left in `EvidenceAudio`.

**Play control + jump.** The play button was a 20px filled disc with a rim (a bead in the sentence);
it is now `EvidencePlay` - a bare triangle, dim at rest, brightening on hover, with the house
press-compress. The "↳" jump-to-transcript link is removed: one quiet evidence affordance per line, not
two.

**The note template's one gutter.** The section heading sat at the far left while its rows sat inside a
3px accent RAIL indented ~28px - two left edges, "two documents stapled together" (symmetry-and-spacing
skill). The rail is gone; a section is now its coloured header plus its rows at the SAME gutter, grouped
by space (heading-above 24, heading-below 8, rows 8) - space groups, a line is a last resort. Action
checkboxes are centre-aligned on the text line (were top-aligned, which read as "the tick is not on the
same line as the words"). One renderer, so the notch and the Library change together.

## Tight deliverables + delete-only transcript filler (2026-08-25)

**Deliverables are Tight (owner's pick over Balanced / summary-only).** The concise pass (`Brevity`) only
ever tightened bullets, decisions, questions and actions - it SKIPPED the summary, so `note.Tldr` stayed
whatever the model wrote (a 3-4 sentence corporate paragraph). Now `Brevity.Tighten` also runs
`TightenSummary`: strip spoken hedges, keep a 2-sentence outcome-first lead, leave empty-empty for the
renderer to drop, and (unlike a bullet) NEVER strip a reported-speech frame, since "The team decided ..."
is the right shape for a summary. Backstop caps dropped: bullet 22->15 words, action 14->11. The writer
prompt now asks for 6-12 word bullets, outcome-first, no reported-speech frame, and a 1-2 sentence
summary. The same pass runs on the LLM note and the heuristic floor so the two cannot drift.

**Transcript filler: the safe floor, not a rewrite.** Researched Wispr Flow, Otter, Granola (agents, per
the standing rule). Verdict: Otter deletes disfluencies at the recognition layer (no verbatim mode);
Wispr goes further and re-tones, and reviewers catch it "improving" what was said; Granola keeps the
transcript verbatim as auditable ground truth and cleans only the notes. Toki IS Granola's model - the
transcript is the ground truth evidence clips are cut from, so a rewrite would desync the clips and break
the "don't invent" rule. `Readability.cs` therefore extends only DELETE-ONLY cleanup: the er/eh/ah
interjection family joins um/uh, and comma-parenthetical discourse fillers ("it was, you know, huge" ->
"it was huge") are dropped. Discourse removal is comma-bounded ONLY - space-bounded "you know" would eat
"do you know what time"; the literal uses have no comma and are untouched, so the exact wording of
everything real is preserved and evidence links still match. Genuinely rambling speech and side-talk that
was actually said stays: faithful, like Granola. Every change ships a mutation probe (14 new tests).

## Animate by default; snapping is the exception (2026-09-02)

`OnStateChanged` gated ALL size choreography behind `IsHoverPair`, an allowlist of five state pairs.
Everything else - including `MeetingPrompt -> RecordingBar` and `RecordingOpen -> Summarizing`, the
two transitions a user actually clicks - fell through to a branch that called `SizeWindowNow` and
teleported. The rule is inverted now: the in-place branch animates through `AnimateSize`, and a snap
must be asked for explicitly (`SnapResize`, used by onboarding). The machinery did not change; it was
already generic and already correct about the hard parts (window sized twice per transition to the
union box, content pinned so it lays out once, the only Effect hidden for the ride).

Corollary found in the same pass, and worth stating as a rule: **a clip must be dropped only after the
window has actually reached its target size.** `PinContentForMorph` cleared `Root.Clip` on the
animation's `Completed`, 20ms before `AnimateSize`'s settle timer resized the window, and Root
stretches - so the card repainted at the union width for 1-2 frames on every collapse. The clip now
holds the target rect and `SettleCorners` replaces it after the resize.

## The note writer's context window is a product constraint, not a runtime detail (2026-09-02)

`MaxChunkChars()` derives the map/reduce chunk size from `LlmRuntime.ContextSize`, which is chosen
from detected VRAM. On a 4GB card that is 3072, giving 6,816 chars per window - about eight minutes of
speech. Above that the note is written from digests, not from the transcript, and the digest budget
("2 to 5 short dash bullets" per window) was set without reference to how much of the meeting it had
to carry. The consequence was invisible in every test because the suite tests the layers, not the
artifact, and both CLI instruments printed the wrong field.

Two decisions follow:

1. **The digest is the product, not an intermediate.** For any meeting over ~8 minutes the digest IS
   what the note is written from, so it is budgeted accordingly (5-12 bullets, 420 tokens, and an
   explicit instruction that names, numbers, dates and quantities survive verbatim because nothing
   downstream can recover them).
2. **Output caps scale with input.** A fixed 5-bullets-per-section cap claims a 90-minute meeting
   holds no more than a 3-minute one. It is 5/7/9 by content length now.

**The writer is given the meeting's facts.** Date, duration, host app, calendar title, invitees, and
the voices diarisation found. This is the one place a hint is safe, and only because every line is
something Toki OBSERVED. It states context and asks for nothing - consistent with "a template is a
claim": a prompt that tells the model to go looking for something is a claim the transcript may not
support, and that is how sections fill with invention.

## A dev seam that writes to the real store is a data bug (2026-09-02)

`--seed` constructed `new MeetingStore()` with no path - the real `%APPDATA%\Toki	oki.db` - and
mirrored hand-written demo notes into the real `Notes\` folder. It was not `#if DEBUG` guarded, while
`--dbtest` beside it correctly used a temp path. Two literal-string notes have been in the owner's
Library since, and being hand-written they read better than anything the pipeline produces, which
silently biased every quality judgement made by browsing. Rule: **a harness names its store
explicitly; a default-constructed store is the user's.**

## Two paths that must agree will disagree (2026-09-03)

`OnStateChanged` gated animation behind `IsHoverPair`, an allowlist of five state pairs. The first
fix widened the allowlist. That was the wrong shape, and it failed within a day: `deferPeekBg` still
read `peek && hoverMorph`, so a newly-animated collapse stripped the card's surface on frame one and
then shrank an empty box. The owner reported it as glitching, and it was worse than the snap it
replaced.

The correction is structural, not a patch. `hoverMorph` is now `!fromHidden` and there is ONE branch
for every visible-to-visible transition, sharing one surface-deferral and one mascot path.
`SnapResize` is the single explicit opt-out.

**The rule: when a behaviour must be consistent across cases, do not widen the gate - remove it.** A
second path that has to be kept in step with the first is a defect with a delay on it.

Corollary already recorded on 2026-09-02 and reaffirmed here: a clip must be dropped only after the
window has reached its target size, and a change in `TopGapFor` rides the morph as a composited
transform (`GlideTopGap`) rather than a per-frame window move, because a layered window pays a native
move plus a DWM surface update on every frame.

## Reduced motion is gentler motion, not no motion (2026-09-03)

`Motion.Reduced` reads `SystemParameters.ClientAreaAnimation` live (it can change while running).
`Ms()` caps a duration, `Rise` zeroes entry travel, `Stagger()` zeroes a cascade. The rule applied
everywhere: keep opacity and colour, which carry meaning; drop translation, scale and overshoot,
which carry spatial drama. Cutting transitions outright is its own accessibility bug - elements then
teleport and the viewer loses what moved where.

## The ghost-mode blur claim was wrong, and the gap was a recipe (2026-09-03)

`apple-glass-material-system.md` recorded blur-behind as permanently impossible on the notch because
of `WDA_EXCLUDEFROMCAPTURE`. **SUPERSEDED.** Microsoft's `SetWindowDisplayAffinity` reference lists
exactly one caveat - DWM must be composing, which is a requirement not a conflict - and no report of
an incompatibility exists in the Electron, WPF or overlay-app corpus. One project *requires* the
exclusion for its blur: a window that captures the screen behind itself without excluding itself
samples its own output and greys out within frames.

The notch's actual blockers, both verified as documented behaviour:

1. `AllowsTransparency=True` makes it a per-pixel layered window (`WS_EX_LAYERED`), which bypasses the
   frame surface DWM paints a system backdrop into.
2. It is never activated (`ShowActivated=False`), and Microsoft documents that acrylic and mica
   "replace translucency and texture with a solid color" when a window deactivates. A backdrop there
   would render its flat fallback essentially always - the plate it already has, for a rewrite.

The reframing finding is Apple's own: *"A material blurs a background that's part of your app, but
not what appears behind your app on the screen."* The admired glass is largely not sampling the
desktop, so the quality gap was a RECIPE gap.

**Decisions taken:** the Library window, which has neither blocker, gets real DWM acrylic
(`DWMWA_SYSTEMBACKDROP_TYPE = 38`, `DWMSBT_TRANSIENTWINDOW = 3`, Win11 build 22621+), requiring BOTH
`Background=Transparent` and `HwndSource.CompositionTarget.BackgroundColor=Transparent` - setting only
the former leaves WPF clearing the surface to opaque black before DWM sees it. `InkScrim` (Ink at
0xD9) keeps body text on a stable ground. Pattern adapted from WPF-UI's `WindowBackdrop` (MIT); no
dependency taken, because it is ~40 lines of P/Invoke and the package drags a competing style system.

The notch instead gets the material recipe: 3% tiled grain at ABSOLUTE viewport units (grain that
scales with the card is an instant tell), and an eased four-stop luminance falloff on `t^1.6` instead
of a two-stop ramp. Both are Brushes, so the standing no-Effect-inside-an-animated-subtree rule holds
and they cost nothing per frame or per resize.

**Rejected, with reasons:** `SetWindowCompositionAttribute` acrylic (documented input lag on drag,
unfixed since 2018, unsupported API); WinUI 3 / XAML Islands (officially not embeddable in WPF);
Windows Graphics Capture live blur (draws a coloured capture border unless the app requests borderless
access, which shows a consent prompt and needs a packaged manifest capability - three violations of
hard constraint 4); `CreateHostBackdropBrush` (high interop cost, reported not to update live).

**Still open:** a captured-and-blurred backdrop for the notch (GDI `BitBlt` a padded region, downscale,
blur, `ImageBrush`), if the recipe proves insufficient beside the Library's real glass. Ghost mode
makes that technique work rather than breaking it.

## Film the transition; do not reason about it (2026-09-03)

Four separate mechanisms produced one symptom - the owner's "glitch resize" - and each was fixed in a
separate round because each was only visible once the one above it was gone. Reading the code found
none of them. Filming the notch at 60fps and tiling the frames found all four, and then immediately
caught two more faults that the fixes themselves had introduced.

**The method, now that it is proven:** `dist\Toki.exe --noghost --state a,b,a,b`, ffmpeg `gdigrab` at
60fps, then `fps=60,crop=...,tile=6x8` into a contact sheet. `--noghost` is mandatory -
`WDA_EXCLUDEFROMCAPTURE` hides the notch from every screen recorder, which is exactly its job, and
discovering that cost a round.

**The four causes, in the order they became visible:** content arranged at the union width and then
re-laid-out on completion; the outgoing page destroyed so the card showed the new page cropped to the
old size; the clip's origin left relative to the union box after the window had already resized; and
the settle running on a `DispatcherTimer` at `ms + 20` rather than the animation's own completion.

**The rule worth keeping:** a symptom that survives a correct fix is not evidence the fix was wrong.
Verify the fix landed, then look again for the next cause. And when a fix "improves but does not
cure", that is the signature of stacked causes, not of an insufficient fix.

## Interruptibility: animate from the presentation value (2026-09-03)

Every animation must start from what is on screen, never from where a fresh animation would start.
The clip rect always did this (it reads its live `Rect`, because `_cardW/_cardH` already claim the
destination while a morph runs). Two things did not, and both are fixed: the zoom had an
unconditional `From`, so interrupting snapped the content back and re-zoomed; and the outgoing
snapshot rendered `Host.Children[0]` rather than `Host`, discarding the half-faded old page that an
interrupting morph was actually looking at.

Corollary pinned by test: **nothing may disable hit-testing for the duration of a transition.** A
morph that swallows clicks is not interruptible however carefully its values are computed.

## A reading surface holds its own colour (2026-09-03)

Real DWM acrylic on the Library window worked, was confirmed on the owner's machine, and is now OFF.
Translucency means the desktop tints the page; in the LIGHT theme that read as the whole window
turning grey. This is not a tuning problem - it is what translucency does to a light surface, and no
scrim opacity makes it both glassy and stable.

Reverted with it: the eased `t^1.6` gradient stop distribution, which was aimed at the notch plate but
applied to every Down/Up gradient with more than two stops and so silently changed `CardRim` as well.
**A change to a shared builder is a change to every one of its callers**; enumerate them before
editing one.

Grain came out for the second time in this codebase's history. `CardMaterial` records both attempts
and the reason, so it is not tried a third time.

## The artifact is the proposal, the render is the acceptance test (2026-09-03)

The owner asked why the app never looks as good as the mockup. The honest answer has four parts, and
only one is closeable:

1. Chromium beats WPF at this. Sub-pixel text positioning, better small-size hinting, cheap real blur,
   a compositor that does not drop small animations. WPF snaps to whole device pixels, renders 12-13px
   text heavier, and puts anything with an `Effect` on the CPU.
2. Mockup content is chosen; real content is "Barbarasad", a title wrapping to three lines, and a
   section with one bullet.
3. A mockup has no loading, empty, error or overflow states, and those are most of the app.
4. **Attention asymmetry, and this is the closeable one.** The artifact gets full attention on one
   screen in the medium it is built in, and it gets LOOKED AT. The app change is a diff into a large
   file and is mostly verified by tests.

**Decision:** `--render` output is the acceptance test. Nothing visual ships until the rendered PNG is
compared against the mockup, and where it cannot match, say why rather than quietly shipping worse.
This caught two things in one session that reasoning missed - a waveform spanning under the buttons,
and note text running 140 characters wide.


## Provenance is recorded at generation, never recovered afterwards (2026-09-03)

Two halves of this existed for weeks and were never joined. `ValidateDigest` returned verified,
narrowed transcript ranges for every map window; `action_items` had carried `src_first`/`src_last`
columns since 2026-08-23. Nothing assigned them. So every evidence link in the product was recovered
after the fact by matching bullet text against the WHOLE transcript, and the gain from searching
inside a recorded window was never actually in the product.

**Re-measured 2026-09-03** with `--linktest 300` over 16 real meetings, because the previously
documented "+4.6pp" no longer matched the library. What confining the search actually buys:

| Condition | Flat (whole transcript) | Confined to the window | Paired verdict |
|---|---|---|---|
| EASY (half the words kept, 1 borrowed) | 96.7% | 98.0% | McNemar 2.25 on 4 disagreements, NOT significant |
| HARD (a third kept, 3 borrowed) | 85.6% | 88.5% | McNemar 4.17 on 6 disagreements, significant at p<0.05 |

Read it honestly: the gain is real only on the HARD condition, where the bullet shares little
vocabulary with its source line, and that is exactly the case a real note produces. On easy cases
flat matching already wins and provenance is noise. Refusals also rise (1 to 4 on hard), which is the
trade being made on purpose: a confined search that finds nothing stays silent instead of reaching
across the meeting for a plausible wrong line.

Note also what `--linktest` simulates: a 40-line chunk hint. The shipped implementation records the
NARROWED range `ValidateDigest` computed, which is tighter, so the real effect should be at least
this and is not yet measured separately.

**Decision: a written line records where it came from at the moment it is written.**

`LlmNoteWriter.AttachProvenance` matches each finished bullet and action against the DIGEST WINDOW it
was rewritten from, not against the transcript. That is a much easier problem: a digest is short, it
is already verified line-by-line against what it cites, and it is in the speaker's own words.
`Evidence.BestRange` then narrows within the recorded span instead of searching the whole meeting.

Three constraints that are easy to get wrong, each pinned by a test:

**It runs LAST.** `NoteSection.Sources` is index-aligned with `Bullets`, and both the name gate and
`Brevity.Tighten` can drop a line. Attributing before them leaves every source after the dropped
bullet pointing one line to the left. That failure is silent and looks entirely plausible, which
makes it worse than a crash.

**It is silent when unsure.** A bullet must cover at least half its own content words in a window,
and must beat the runner-up by 0.15, or it keeps a null source and falls back to text matching. Two
windows that explain a bullet equally well are an ambiguous attribution, and CLAUDE.md's rule stands:
a wrong evidence link is worse than none, because a reader sent confidently to the wrong minute
trusts the feature less than one offered no link at all.

**An observed source is never overwritten by an inferred one.** `a.Source ??=`, not `=`.

`ReplaceNotes` now writes the provenance columns that `Save` always wrote. That omission meant
regenerating a note silently stripped provenance off every action item, so the one path most likely
to IMPROVE a note was also the one that destroyed its evidence links.

## A metric must print its denominator (2026-09-03)

`--regentest 6` reported "MEAN PRECISION 93.3% -> 100.0%", six meetings out of six at perfect
precision. That reads like a triumph and is not safe to believe, because of how the two metrics are
defined in `Grounding.Report`:

```
Precision => Checked == 0 ? 1.0 : ...
Recall    => TranscriptEntities == 0 ? 1.0 : ...
```

Both return **1.0 on an empty denominator**. A note containing no checkable facts scores 100%
precision. A transcript with no repeated entities scores 100% recall. The conventions are defensible
on their own (there is nothing invented in a note that claims nothing), but the report printed only
the percentages, so a perfect score and an empty note are indistinguishable in the output.

This is the same shape as two lessons already paid for: "a gate that always passes is not a gate",
and the diagnostics that cried wolf on a healthy app. An instrument that cannot report the difference
between "measured and passed" and "had nothing to measure" will eventually flatter the thing it is
supposed to audit.

**Decision: every quality number Toki prints carries the count it was computed from.** `--regentest`
now prints `facts checked N -> M` beside precision and `kept/total` beside recall, and warns
explicitly when any meeting in the run scored on an empty denominator.

The same run also showed why this matters in the other direction. Mean recall moved 30.4% to 34.4%,
which looks like a small gain, but the spread was 46% to 12% and 38% to 0% on two meetings against
15% to 62% and 50% to 100% on two others. A four-point mean over six meetings with that variance is
not evidence of anything. Meeting 45 returning zero recall is a specific thing to investigate, and it
is invisible in the mean.

## The context budget overflowed, and the floor caught it silently (2026-09-03)

The single most consequential bug found this session, and it was arithmetic rather than prompting.

`MaxChunkChars` decided how much transcript goes into one map window:

```
Math.Max(1800, (ctx - 800) * 3)      // the old formula
```

`LlmRuntime` sets `ContextSize = 3072` on the partial-offload branch, which is what a 3.9GB to 7GB
card takes, and that is the owner's RTX 3050 at 4.0GB. So a window was **6,816 characters**.

Numbered transcript lines do not tokenise at 3 chars per token. `[147] Craig: ` is punctuation and
digits, and a mis-heard name splits into pieces; the real rate is nearer 2.6. So the window was
~2,600 tokens, the instructions ~500, and the answer up to 760. That is over 3,072 before the model
writes a word.

Every window threw `LLamaDecodeError: llama_decode failed: 'NoKvSlot'`.

**And nothing said so.** `TextPass` had a bare `catch { return null; }`. A null return means "this
window failed", every window failed, and `WriteAsync` fell back to the heuristic floor, which writes
raw transcript lines as to-dos. Meeting 28 came back with no title, an empty summary, and twelve
"action items" reading like "Um, I will ask Pooja to give you access to this uh".

Short meetings fit in a single pass and were written properly. **Long meetings were never written by
the model at all**, on the owner's own hardware. That is a large part of "the notes are mediocre".

**Three decisions.**

**The reserve names all three costs.** `ReserveTokens = 1600` covers the instructions, the longest
generation (760, the final pass), and margin; `ChunkCharsPerToken = 2.6` is measured against
transcript lines, not prose. On a 3072 context a window drops from 6,816 to 4,347 characters. More
map calls and a slower note: meeting 28 went from 91s to 255s. **A slow correct note beats a fast
floor note**, and the floor note was worthless.

**A model call that throws is logged.** Both the exception and, separately, the fall to the floor,
with the two "no note" outcomes told apart: a failed model and a quiet meeting look identical to the
user and are opposite faults. Before this the only symptom of a total map failure was the ABSENCE of
the "digest kept" lines a working pass emits, which is not something anyone would notice.

**The budget is asserted, not assumed.** `ContextBudgetTests` checks that a window plus instructions
plus the longest generation fits, at both context sizes `LlmRuntime` actually sets. It fails at both
against the old formula. A constant that is only exercised on one machine's hardware path will drift
again otherwise.

**The general lesson.** A fallback that is good enough to ship is also good enough to hide the
failure that triggered it. The heuristic floor exists so a broken model never costs the user their
meeting, and that is right; but a fallback must be LOUD. This is the second time the floor has
concealed a total model failure (see "the floor leaked the transcript again", 2026-08-25). The first
time the cause was a native load crash, this time a context overflow, and both were invisible for the
same reason.

## A union of observed ranges is not a guess (2026-09-03)

When the digests themselves overflow the final pass's context they are mapped again, and that step
used to call `provenance.Clear()`. The comment gave the reason: a digest of digests cannot be traced
to a line range without guessing, and a guessed provenance is worse than none because it points the
reader confidently at the wrong moment.

The first half is correct. The second half assumed guessing was the only alternative, and it is not.
A re-digest is produced from a KNOWN set of first-round windows. The union of their ranges is a
statement about where that text came from that was observed, not inferred. It is coarser than a
first-round range, and it is still true.

This became urgent the moment the window budget shrank to fix the context overflow: smaller windows
make the re-reduce loop run far more often. Measured on the same six meetings, coverage fell from
20/30 bullets to 8/44, with three meetings recording nothing at all. The provenance feature had been
wired and then immediately gutted by an unrelated fix.

**Decision: `GroupWindows` carries the union forward, with one rule that keeps it honest.** A window
that came back uncited contributes its TEXT to the group but never widens the RANGE. An unverified
window cannot manufacture a claim about lines nothing checked, so the range only ever covers windows
that passed citation validation. A group with nothing verified stays unknown and its bullets fall
back to text matching, exactly as before.

**And read the pooled number, not the mean of percentages.** `--regentest` averaged per-meeting
precision and recall, which let a meeting with 2 transcript entities count as much as one with 26; a
single 2/2 becoming 1/2 moved the headline eight points and reversed the apparent verdict. Pooled
totals are now printed beside the macro mean, and a rate with no denominator prints `n/a (0)` rather
than 100%.

**The limit of the metric, recorded so nobody tunes against it blindly.** `Grounding` measures
faithfulness: are the note's hard facts present in the transcript, and how many of the transcript's
facts survived. A note that copies the transcript verbatim scores near-perfectly. The heuristic
floor, which is the worst note Toki can produce, therefore scores WELL. That is why the old stored
notes out-score the fixed pipeline on recall while being worse notes to read. Faithfulness is
necessary and it is not sufficient, and the final gate stays `--evidencetest` on real meetings plus
the owner's eyes.

## When the evidence overflows, select from it; never summarise it again (2026-09-03)

The map step exists so the writer reads EVIDENCE rather than a paraphrase. When the digests
themselves did not fit the final pass, the pipeline mapped them again, up to two extra rounds. That
reintroduced the very thing the map step prevents: the writer ends up reading a paraphrase of a
paraphrase, which is the failure recorded in lesson 1 and in the 8-minute context cliff.

**Measured, on two meetings in one run.** Meeting 48 mapped cleanly, keeping 25/26, 19/21, 29/31,
22/23 and 21/22 lines across seven windows. Its digests came to 6,645 characters, over the budget, so
one re-reduction ran. The finished note had **zero bullets** and its checkable facts fell from 25 to
2, recall 46% to 4%. Meeting 28, which did not re-reduce, went from 15% to 62% recall in the same
run, with 6 of 7 bullets sourced. One variable, opposite outcomes.

**Decision: `TrimToFit` drops the least informative lines instead.** Ranking, in order: lines
carrying hard entities (names, numbers, dates, money) outrank lines carrying none, because those are
the facts a reader came for and the ones `Grounding` measures; among equals, a line from a window
whose citation VERIFIED outranks one from a window that came back uncited; length breaks the rest,
shortest first, since a very long digest line is usually a run-on rather than a dense one. Reading
order is restored afterwards, because a writer handed its evidence out of sequence invents
transitions to explain the jumps.

**Selecting is lossy in a way a reader can reason about. Re-summarising is lossy in a way nobody can
see.** Every surviving line keeps the speaker's own words and its exact provenance, rather than every
line becoming a vaguer version of itself.

## The context size is measured, not assumed (2026-09-03)

The partial-offload branch (VRAM 3.9GB to 7GB, which is the owner's RTX 3050) held `ContextSize` at
3072 defensively. Measured on that machine with 20 layers offloaded and 3,370 MiB reported free:

| | ctx 3072 | ctx 4096 |
|---|---|---|
| model buffer | 1401 MiB | 1401 MiB |
| KV cache | 228 MiB | 304 MiB |
| compute buffer | 302 MiB | 302 MiB |
| total VRAM | 1931 MiB | 2007 MiB |
| headroom | 1439 MiB | 1363 MiB |

**76 MiB buys a 49% larger map window** (4,347 to 6,489 characters), which means fewer windows, fewer
model calls, and far less trimming. Over 1.3GB of headroom remains, which matters because a video
call is using the same GPU while Toki records it.

If a machine ever fails to load here, drop this back to 3072 rather than lowering `ReserveTokens` in
`MaxChunkChars`. The reserve is what stops `NoKvSlot`; the context size is only how much fits.

## The extraction budget belongs to the meeting, not to the chunking (2026-09-03)

The map prompt asked for a flat "5 to 12 lines" however much transcript the window held. That makes
the extraction budget a property of the CHUNKING, which is an implementation detail nobody outside
this file should be able to feel, rather than of the MEETING, which is the thing being summarised.

Raising `ContextSize` to 4096 exposed it within one run. Windows grew 49%, meeting 28 went from seven
windows to four, each still capped at the same line budget, and its measured recall fell from 62% to
38% while finishing 100 seconds faster. It was extracting less of the meeting and doing it sooner,
which is the signature of a budget attached to the wrong thing.

**Decision: `DigestLines` scales with the window**, about one point per 450 characters, clamped to
6 and 20. Below 6 a window stops saying anything useful about its stretch of talk; above 20 the model
pads, and padding is exactly what the citation gate then throws away, which is work done twice to
arrive back where we started. The map pass's token cap scales with it and is capped at 700, staying
under the 760 that `ReserveTokens` is sized against: a map pass that outgrew the reserve would bring
`NoKvSlot` straight back.

**The pattern across today's three note-quality fixes is worth naming.** Every one was a number that
described the machinery rather than the work:

- the chunk budget assumed 3 characters per token for text that runs at 2.6
- the re-reduce loop summarised summaries because the digests did not fit a window size
- the digest budget counted lines per window instead of per minute of meeting

None of them were prompt problems. That is why repeated rounds of prompt tuning could not move the
quality, and it is the reason to reach for a measurement before reaching for the prompt: a constant
that is a proxy for something real will drift away from it the moment anything around it changes.

## The note instrument has a 12-point noise floor (2026-09-03)

Recorded because it invalidated a diagnosis made the same day, and would have invalidated more.

Meeting 28 was run three times through near-identical code and returned **62%, 38% and 46% recall**.
Qwen is sampled at temperature 0.7 by design (lesson 8: sampler params over prompts, never greedy),
so a single meeting's grounding score moves about twelve points run to run for nothing.

The 62% to 38% step was read as a regression, attributed to the per-window digest budget, and acted
on. The resulting change is defensible on its own reasoning and is not measurable as a gain: pooled
recall was 28.2% before it and 28.2% after, because it lifted one meeting and lowered the other by
about the same. It is kept and labelled as unmeasured.

**Decision: `--regentest` is a detector of STRUCTURAL faults, not a quality meter.** What it found
today is exactly what it is good for: a meeting producing zero bullets, notes containing no checkable
facts at all, and an entire pipeline silently falling to the heuristic floor. Those are visible at
n=2 because they are categorical. A four-point move in recall is not.

Before treating any pipeline change as an improvement, either run n >= 10 meetings, or run the same
small set repeatedly and compare distributions rather than single numbers.

`--linktest` gets this right and is the model to copy: a fixed seed so the sample cannot drift, two
difficulty conditions, and a paired McNemar test over the cases where two scorers actually disagree.
`--regentest` cannot be made deterministic that way, because every run re-samples the model.

**The general form: know your instrument's resolution before you trust a difference it reports.** A
measurement that cannot separate the effect you are looking for from its own noise will happily
confirm whatever you already believe.

## A line is not a unit of meaning; a few seconds of speech is (2026-09-03)

`ValidateDigest` accepted the cited transcript line plus one either side. That measures the
neighbourhood in LINES, and a line is not a fixed quantity: it is however much speech landed between
two pauses, which varies by a factor of three between meetings.

Measured on two stored meetings, same pipeline, same day:

| meeting | segments | minutes | seconds per line | what +/-1 covers | lines kept |
|---|---|---|---|---|---|
| 48 | 468 | 26 | 3.3s | ~10s | 25/26, 29/31, 22/23, 21/22 |
| 28 | 1140 | 20 | **1.05s** | ~3s | **4/10, 15/30, 12/25, 3/9** |

A spoken sentence runs five to eight seconds. On meeting 48 the neighbourhood covered a whole
sentence; on meeting 28 it covered a third of one. So a point drawn faithfully from a sentence, and
citing its middle, failed a check that only looked at three seconds of it.

The evidence that this is the gate deleting TRUE content rather than catching invention: on meeting
28 essentially every loss was `unsupported`, not `uncited` or `out-of-range`. The model was citing a
real line, inside the window, in the right format. The words were simply spread across more lines
than the check read.

**Decision: `NeighbourLines` scales the neighbourhood to cover a comparable amount of SPEECH**, about
six seconds either side, derived from the transcript's own duration and segment count. A transcript
without usable timing keeps the old single-line behaviour.

**The cap matters as much as the scaling.** This widens a safety gate, and a wide enough haystack
lets a genuinely invented point find its words somewhere nearby. Five lines either side is the
ceiling, whatever the arithmetic says.

**And the thing NOT to do, written down because it is the tempting one:** never fix a poor keep rate
by lowering `CitationOverlap`. That lowers the bar for every meeting including the clean ones, and
this gate is the only thing standing between a digest and invention. Fix the unit, not the threshold.

**What it actually measured, and a correction.** The keep rate is a better instrument than recall
here, because it is a direct count over dozens of digest lines rather than a sampled quality
judgement. Aggregated across whole runs:

| configuration | lines kept |
|---|---|
| context fix only (windows 4,347 chars) | 250/355 = 70.4% |
| plus ctx 4096, TrimToFit, scaled digest budget | 138/158 = 87.3% |
| plus the time-based neighbourhood | 101/110 = 91.8% |

**The neighbourhood change on its own is 87.3% to 91.8%, about +4.5 points.** An earlier reading of
the very first window (26/27) was reported as a far larger swing; that window was one of the good
ones and the claim was wrong. At n=110 lines the standard error is near 3 points, so +4.5 is roughly
1.5 SE: directionally right, mechanistically well motivated by the density numbers, and NOT
conclusively outside noise on this sample.

Kept because the unit was genuinely wrong and the mechanism is understood, not because the number
proves it. The honest summary is "the gate now measures the right thing", not "the gate improved by
X%".

The wider lesson stands: pick the measurement that can see the effect you are claiming, then do not
read a single sample of it as the result.


---

## The state machine's only exit must not be on the happy path (2026-09-04)

**Decision: the dispatcher call that advances the notch out of `Summarizing` runs from a `finally`,
and the summarize lambda contains no bare `return`.**

`AppState` deliberately allows only two transitions out of `Summarizing` (`Notes`, `NoSpeech`) and
there is no watchdog or timeout. That is a reasonable design - a loader that times out into a wrong
state is worse than one that waits - but it makes the single line that performs the transition a
**single point of failure for the whole app's perceived liveness**. Anything that skips it hangs the
UI over work that has already completed and been saved.

That shipped. The thin-input gate (below `ThinInputWords`, write an honest minimal note and do not
consult the model) ended in `return;`. The author's intent was "skip the model"; the language's
meaning, inside that lambda, was "abandon the pipeline". The note for the meeting was written, saved
to SQLite and mirrored to Markdown 8 seconds after the recording stopped, and the notch span for
fifteen minutes.

**Why the fix is not "remove the return".** The `return` was one way to skip the advance; an
exception from any step not individually wrapped was another, and `InferOwners` was exactly such a
step. Fixing the instance leaves the shape. So the entire lambda body is now a `try`, the advance is
its `finally`, and `transcript`/`meeting` are hoisted so the finally can advance on whatever the
pipeline produced before it gave up. A note that failed to generate still shows the user *something*.

This is the same lesson as [one path, not a wider gate]: when correctness depends on every path
reaching one line, make reaching it unconditional rather than auditing the paths.

**Why no test saw it.** The gate only fires on a nearly silent meeting. Every test meeting had enough
words and took the model path, which reaches the advance normally. The guard is now a source-shape
test with mutation probes, because the defect is control flow, not a value: `SummarizeExitTests`
holds five detectors plus four probes that feed those detectors the pre-fix code and assert each
catches it.

## A cached view's field initialisers measure process uptime (2026-09-04)

**Decision: anything in a notch view that means "when this showing began" is stamped in `Loaded`, and
event subscriptions on long-lived objects are made in the constructor.**

`PillWindow.ViewFor` caches one `UserControl` per `PillState` in `_views` for the life of the
process - correct, and load-bearing for animation smoothness (a cold build inside a transition eats
the head of the animation). The consequence is that a view's constructor runs **once, ever**.

`SummarizingView` had `readonly DateTime _started = DateTime.UtcNow;`. On a two-minute meeting the
loader read **1275m 37s**, which is precisely the time since the app had launched the previous
evening. The elapsed display was a genuinely good idea - a long wait with no number reads as a hang -
made actively harmful by being anchored to the wrong event.

The mirror-image bug sat three lines away: `_elapsedTimer.Tick += ...` inside `Loaded`. `Loaded`
fires on every show, `Unloaded` never removed it, so the handler count grew by one per meeting.
Subscriptions to an object that outlives the showing belong in the constructor; subscriptions to
something external (`App.SummarizeStageChanged`) correctly stay paired across Loaded/Unloaded.

**The general rule for this codebase:** in a cached view, a constructor is "once per process", not
"once per appearance". Ask which of the two a line actually wants.


## The offload tier is measured, and generation is the only cost that matters (2026-09-04)

**Decision: the 4GB partial-offload tier moves from 20 layers to 32, and `LlmRuntime.Tier` becomes a
pure function so the numbers can be asserted.**

Until now nothing timed a model call. `TextPass` logs per pass: prompt chars, token cap, output
chars, milliseconds. The first reading reframed the problem. Prompt processing is close to free
(~3s for 3.4k chars) and **generation is essentially the whole wait**, at 8-11 tok/s for ~1000
tokens per note. Anything that changes generated-token count changes the wait proportionally;
anything that only changes prompt size barely registers.

Generation rate is set by the share of the model resident on the GPU. The 20 was chosen defensively
because a native VRAM OOM is an uncatchable crash, and it was never timed. Measured (one meeting,
RTX 3050 4GB): 20 layers 121.8s, 28 layers 75.7s, 32 layers 45.8s, 34 layers 47.5s/48.7s, full
offload 44.7s.

**Why 32 and not full offload.** Full is 3% better and takes most of the remaining headroom on a card
that is also driving the video call being summarised. The failure mode is not a slow note, it is a
process death with no exception to catch, which lands the user on the heuristic floor. Three percent
does not buy that risk. 32 keeps over a gigabyte free.

**The honest limit.** This is still a constant, and the warning from
[a constant that describes the machinery] applies: it cannot know how busy the GPU is at load time.
`DetectVramBytes` reads TOTAL card size from the driver registry key, never free VRAM. The correct
instrument is DXGI `IDXGIAdapter3::QueryVideoMemoryInfo`, whose `Budget` field is exactly "how much
you may use right now"; sizing layers from it would give full offload on an idle machine and
automatic backoff on a busy one. Backlogged rather than rushed, because the code path it guards
crashes rather than throws. The Windows `GPU Adapter Memory` performance counter was evaluated and
rejected: its instances are keyed by adapter LUID with no reliable mapping back to the adapter
llama.cpp chose, and on the owner's machine it reported 2035 MiB in use on an adapter where full
offload demonstrably fit.

**Consequence for note quality.** Bullet and digest budgets that scale with the meeting cost
generated tokens, which is the expensive axis. At 32 layers a full 14-bullet note is cheaper than
the old capped 7-bullet one, so the quality work of 2026-09-03 is paid for by this change with room
to spare. Future quality work should still be costed in generated tokens, not in prompt size.


## The map step selects, it does not summarise (2026-09-04)

**Decision: the map pass emits transcript LINE NUMBERS only, and Toki copies those lines out of the
transcript verbatim. `LlmNoteWriter.MapMode.Select`, the default.**

Per-pass instrumentation established two facts that reframe the pipeline. Generation is the entire
wait (a 12-token pass over a 3.4k-char prompt costs 2-3s; a 430-token pass over the same prompt costs
20-58s), and the map phase is ~85% of a long meeting's generation because it runs once per window and
each window emitted ~430 tokens of rewritten prose.

Selection cuts a window to ~70 generated tokens. `Materialise` reconstitutes the identical
`[n] Label: text` format the extract path produced, so `ValidateDigest`, `TrimToFit`, `AttachProvenance`
and the citation neighbourhood all keep working with no branch.

**Grounding was expected to rise and did not fall.** A copied line cannot drift from its own citation,
so the "window came back uncited, keeping it without provenance" fallback - which once cost a quarter
of meeting 48 - cannot fire. Over 5 meetings: precision 100.0% to 96.8%, recall 38.9% to 33.3%,
provenance 68% to 69%, facts claimed 22 to 31; each difference is <=3 facts at n=5.

**The finding that nearly went the other way.** A single side-by-side on meeting 128 showed the new
path losing every action owner, and it was almost written up as a regression. Repeating on meeting
107 reversed it completely and twice: the old path named nobody across 6 actions, the new one named
3-4 across 5. Note generation runs at temperature 0.7; one sample is not a result. This was the third
time in one day a single stochastic run nearly became a conclusion (see also the offload timing).

**The cost that is real:** the digest is now raw speech rather than the model's tidied version, so the
final pass has more to do and the digest is longer. `TrimToFit` absorbs the length. If a future
regression shows up in prose quality rather than facts, this is where to look.

## Two measurements that overturned their own arithmetic (2026-09-04)

**Full offload is refused despite being 33% faster.** Three runs per config: 43.0s at 32 layers,
28.9s at 37. But llama.cpp reports "3370 MiB free" and a full offload allocates model 2375.91 + KV
576.00 + compute 301.75 + host 18.01 = ~3254 MiB. A 116 MiB margin against an uncatchable native OOM
is not a margin. With the select map both configurations are under the target (35.9s vs 24.7s on the
longest meeting), so the risk buys nothing. nvidia-smi's more generous 3965 MiB is not the number
that matters: the allocator's view is.

**KV quantisation lost to its own documentation.** q8_0 for K is published at KLD 0.00094 and would
have funded a parallel batch. Measured on this Vulkan build: 99s against 28.9s, 2.3x slower - the
fused attention path degrades badly, the same class as llama.cpp #12526 / #27137. `GpuTierTests`
asserts f16 so that the next person to read the KLD table has to read the measurement first.


## Streaming is a display concern and must not change the answer (2026-09-04)

**Decision: `TextPass` takes an optional per-line observer and streams only when given one; the final
note pass uses it to publish `App.SummarizeTitle` while the rest is still being written.**

The title is the first thing the final prompt asks for and that pass runs ~12s, so the name of the
meeting existed 9.1 seconds before the note was shown (measured, meeting 128). Showing it turns the
last third of the wait from a description of Toki into a fact about the user's meeting.

Three constraints are load-bearing:

**Per completed LINE, never per token.** A stream delivers several pieces inside one line; reporting
on each would put "AI-Dri" on screen and then correct it. This is also the shape of the documented
notch-flicker bug, where a flush 14 times a second regrew the window under the user.

**The buffered and streamed paths must return identical text.** `Generate` is the only place that
branches, and `TitleRevealTests.Streaming_does_not_change_the_note` asserts the two produce the same
title, summary, bullets and actions. A display feature that quietly altered notes would be a bad
trade at any latency.

**The parse must be the SAME parse.** The observer runs `ParseNote` on the partial text, so the notch
can never show a title the finished note then disagrees with.

This surfaced a real defect in the test doubles: `ScriptedLlm` stubbed `StreamAsync` as
`yield return ""`. Harmless while nothing streamed, and it silently emptied six notes the moment
something did. `ILlm` documents the two methods as equivalent, so the fake was wrong, not the change.
**A fake that does not honour its interface is a test that measures the fake.**

Also fixed here: `SummarizingView` applied its state only in `Loaded`. A cached view can be measured
or photographed without ever being connected to a window - the render harness does exactly that - so
it showed the XAML placeholder. State is now applied in the constructor too.


## A Border paints its Background behind its CHILD (2026-09-23)

**Decision: the notch card has no photographed backdrop. `ScreenBackdrop` is deleted, not fixed.**

The captured-backdrop layer added on 2026-09-03 was meant to give the card's translucency something
real to reveal: photograph the screen the card is about to cover, shrink it, blur it, paint it under
the plate tint. The comment in `PillWindow.xaml` claimed exactly that, and it was wrong about WPF.
`Root` is a Border whose Background paints behind its Child; the `Backdrop` border was the first
child of that Child. So the photograph painted ON TOP of the plate, at full opacity, and every card
wore a blurred picture of the desktop behind it.

The owner saw it as "an ugly yellow color" on every screenshot - their browser, smeared.

**Why deleted rather than re-ordered.** The feature existed to answer an earlier ask to fix the
notch's fake glass. A photograph of the desktop is not glass; it is a still image that does not
track what moves behind it, cannot be right at more than one moment, and was shipped without anyone
ever looking at it (a prior session's own note: "built and compiled but never seen"). Re-layering it
correctly would produce a subtler version of a thing that should not exist. The card's material is
painted, honest, identical in both themes, and it is the floor the rest of the design was built on.

Two things went with it: `GlassTopBleed` (8px of upward growth that existed only so the photograph
could reach the screen edge) and the `Backdrop` border itself.

**The general rule:** in WPF, a decorator's Background is BEHIND its content. A layer that must sit
under a tint has to be under a *sibling* that carries the tint, or behind a transparent parent.
Writing "sitting UNDER the plate tint" in a comment does not make it so, and this shipped for three
weeks because nobody ran it and looked.

## A highlight may own the top third of a surface, never the whole of it (2026-09-23)

**Decision: the card sheen is capped at a third of the card's height.**

`CardMaterial.SheenHeight` was deliberately a FIXED 48px band rather than a fraction - the reasoning
(still correct) is that spreading the same light over a 400px panel stops it reading as an edge
catching it. What was missing is the other end. Nothing bounded it against SHORT cards, and the notch
morphs down to a 52px recording bar and a 50px loader, where a 48px sheen covers 92% of the card. The
highlight stops being a highlight and becomes the surface: the pill reads as one glossy gradient.

`SheenFor(cardHeight) = Min(48, cardHeight * 0.34)`, re-applied on every SizeChanged because the card
morphs. Tall panels are unchanged by construction. Guarded by `CardMaterialTests`, which pins both
ends: the short pill must be under 40% shine, and the 320px panel must still get exactly 48.

This is the same shape as the constants logged in [a constant that describes the machinery]: a value
that was right for the case it was written against and was never asked what it becomes at the other
extreme.


## A token is right for the surface it was measured against (2026-09-23)

**Decision: `ChipOnCard` and `SwitchKnobOn` exist because two tokens were being asked to work on
surfaces they were never tuned for.**

`Chip` (`#2C2C2E` / `#E7E7EC`) is correct against `Surface` (`#0E0E10`), the Library's near-black
page. The notch's plate is `#343130` - far lighter - and `Chip` lands within a few luminance points
of it, so a secondary button on the card stops reading as a box. That is the project's own "the box
IS the button" rule failing not through a missing Background but through a Background measured
elsewhere.

`WhiteBtn` (`#F2F2F2` / `#1D1D1F`) is the primary CTA fill, and inverting per theme is exactly right
for a button on a page. It was also the switch knob. But the switch TRACK turns `Blue` when on, and
`Blue`'s light value is `#1C579B` - dark. So in the light theme the knob went near-black at the same
moment the track went dark blue, and every "on" switch in the app became unreadable. The knob's
contrast partner is the track, not the page, so it gets its own token that does not invert.

**The general rule: a colour token is only valid against the surfaces it was checked on.** When a
token crosses into a new context - a different plate, a different state's fill - it needs re-checking
there, and usually its own name.

## Contrast is necessary and not sufficient (2026-09-23)

**Decision: `DesignTokenTests` asserts amber's SATURATION, alongside `ContrastTests`' readability.**

`Amber`'s light value was `#794F1A`. It cleared 4.5:1 against every surface, so `ContrastTests`
passed it, `--themetest` passed it, and the recording timer rendered as a brown stain on a white
card. **The defect was real, visible, and orthogonal to every number being measured.**

Replacing it exposed the opposite trap. `#B45309` looked right and failed at 3.79:1 - not on any
opaque surface, but on the light card composited over a BLACK wallpaper, because the card is `Glass`
at 85%. A second attempt, `#A04B06`, passed every opaque surface and failed the same case identically.
`#87430A` is the most saturated amber that survives both ends.

So the palette now has two guards with different jobs: contrast for "can it be read", saturation for
"is it the colour we said it was". The second exists because the first was green for a bug the owner
found in one glance - which is the same lesson as the hover-contrast bug of 2026-08, in reverse.


## An arrival is a cursor that slows down (2026-09-23)

**Decision: the peek's open decision moves out of `HoverTick` into `State/HoverIntent.cs`, and is
gated on cursor SPEED and DIRECTION rather than on dwell alone.**

The notch's peek sits in the top-centre band that every cursor crosses on its way to browser tabs
and the window controls. The old rule - accumulate 60ms per poll while inside a 3px-slack rectangle,
open at 850ms - failed in both directions at once, which is exactly why it read as random:

- **False opens:** no speed term at all, so a cursor that crossed the band and paused for a beat was
  indistinguishable from a deliberate hover.
- **Missed opens:** a single sample outside the rectangle zeroed the entire dwell. On a 52x42 target,
  a pixel of hand tremor discarded 800ms of honest intent.

The dwell was long BECAUSE it was the only defence, and its length was the second failure.

**What transferred from the iOS home gesture, and what did not.** The swipe mechanics do not port:
velocity projection (`v/1000 * rate / (1 - rate)`, already vendored in the emil-apple-design skill)
decides where a RELEASED object lands, and a hover has no release. What ports is the framing from
WWDC 2018 session 803 - intent is legible in the derivatives of motion, not in elapsed time - and
hoverIntent's concrete version: an arrival is a cursor that slows down inside the target (under 6px
per 100ms, about 60px/s); a transit is one that does not.

Three rules, only the first about the clock: dwell accrues only while inside AND settled AND not
descending through; inside-but-fast HOLDS the dwell rather than losing it, because the user may be
homing in; a departure is forgiven for 300ms before the dwell is lost. The caller then widens its
hit rectangle from 3px to 20px once `DwellMs > 0` - enter tight, stay loose, the asymmetry every
mature hover menu ends up shipping.

**The downward bound is half the speed gate, on purpose.** At the same threshold the direction rule
rejects nothing the speed gate has not already caught. A rule that can never fire is worse than no
rule, because it reads in the source as a defence that is not actually there.

With the gates carrying the rejection, `OpenDwellMs` fell from 850 to 360. Verified on the real
window rather than only in a test: eight fast sweeps across the top band produced zero state changes;
one deliberate reach-and-rest opened it 386ms after the peek appeared.

Also unified here: `EdgeTick` kept its own private copy of "how far did the cursor move", updated on
some paths and skipped on others. Both consumers now read one `TrackCursorStep`, because two answers
to that question drift apart the moment one caller returns early.

## A refusal is scoped to the call, not to a clock (2026-09-23)

**Decision: declining the record prompt silences it for that meeting, with no re-ask.**

`MeetingDetector.Snooze` set a 10-minute cooldown AND cleared the "already offered" mark, so the
cooldown's expiry re-armed the question. On a long call that is a prompt every ten minutes, which
the owner reported as redundant and pointless.

A test asserted the old behaviour deliberately: a single "Not now" should not silence a whole
mic-session, because an ongoing meeting you change your mind about is a real case. That reasoning is
sound and it loses to the actual cost - starting a recording by hand is one click, and being asked
the same question repeatedly during a meeting is not free. The test is superseded rather than
deleted, with its reasoning preserved, because it will be proposed again.

**Time does not make a refusal stale; a new meeting does.** The refusal lifts once the app has been
off the mic for `SessionEndPolls` (about 15s). The subtlety that makes this work: the previous code
treated a SINGLE absent poll as the end of a call, and muting, changing headset or a driver hiccup
all drop the mic for a moment - so a one-poll rule would hand the prompt straight back.

## Toki ships its own typefaces (2026-09-27)

**Decision: the interface is set in Inter, note titles in Spectral, both bundled in the exe.**

Segoe UI Variable is the face every Windows utility is set in. Using it made Toki look like one, and
that was the single largest visual difference between Toki and the reference notetaker the owner
supplied. Both families are SIL OFL 1.1, which permits bundling with an application and requires the
licence travel with it; both OFL texts ship as resources beside the fonts and both are credited on
the "Built with" screen, on the same footing as Parakeet and Qwen.

STATIC weights (Regular/Medium/SemiBold), never the variable TTFs: WPF cannot address variable-font
axes, so a variable file renders every weight at 400 and lets WPF synthesise the rest.

This SUPERSEDES the three-optical-cuts decision of 2026-09-26, whose reasoning is kept because it
still constrains us: a face drawn for display sizes has stems too fine to survive at 12px, which is
why Toki must never set body text in a display cut. Inter needs no optical cuts, so the three font
keys now differ only in intent.

A typeface change invalidates every measured size floor. `--measuretest` immediately named PrepCard
as holding 13px it never filled, because Inter sets those rows tighter than Segoe did. A floor is a
claim about CONTENT and has to be re-measured whenever the typeface changes.

## The page is the lightest surface (2026-09-27)

**Decision: the light theme's canvas is the lightest thing in the window and cards are tints DOWN
from it.**

This reverses the layering decided one day earlier ("canvas tinted, card lightest", on the Apple
grouped-list and Carbon patterns). What overturned it: every value in the owner's reference was
sampled directly, and the reference does the opposite - page `#FCFCFB` at 82% of all pixels, card
`#F5F4F0`, chip `#EEEBE3`, rule `#DAD9D5`, ink `#1A1A1A`. Four greys and one ink for a whole window.

The old note's reasoning is preserved in `ThemeManager` because it was sound. What it got wrong is
the analogy: the Apple grouped-list pattern is for LISTS OF GROUPS, and Toki's reading pane is one
document. Inverting the stack means a card needs neither border nor shadow to read, so the number of
edges in the window drops - which is most of what "cheap" was.

The warmth finding from 2026-09-26 survives and is reinforced: every light value is still warm, and
so is the reference. The light `Glass` token now carries the page colour too, so the notch and the
Library read as one sheet - and because a lighter card composited over a dark wallpaper lands
lighter, this RAISES the contrast ceiling documented in the translucent-card finding.

## Toki draws its own window chrome (2026-09-27)

**Decision: `WindowStyle=None` + `WindowChrome`, not `AllowsTransparency`.**

`WindowChrome` keeps the non-client frame real and only lets us paint it, so Aero Snap, snap layouts
on the maximise button, drag-to-restore, edge resizing and the system shadow all survive. The
`AllowsTransparency` recipe gives up every one of those and produces a window that feels broken on
Windows. `CaptionHeight` must equal the title row's height, and anything in that strip needing clicks
sets `IsHitTestVisibleInChrome`.

The title row is NOT a bar: it has no background of its own, because in the reference the controls
sit straight on the page with no fill, rule or shadow, which is why that window reads as one surface.
No window title is drawn either - this window has one identity and the sidebar masthead already says
it.

The trap this normally introduces - a maximised window covering the taskbar - was ALREADY solved:
`StyleChrome` has hooked `WM_GETMINMAXINFO` and answered with the monitor work area since the
per-monitor-DPI maximise fix. It was checked before writing a second hook, which would have
double-corrected the bounds.

Close means HIDE, as the old back button did: Toki lives in the tray with a warm model and possibly a
live recording behind it. Caption glyph and tooltip follow `WindowState`, because a maximise button
that still says maximise is the classic hand-rolled-chrome bug.

## Never null an animation you are about to replace (2026-09-27)

**Decision: `ConcealUp` reads the current animated opacity and animates FROM it.**

The owner reported the notch close as "it just cuts back up". Two independent causes, both measured.

First, the easing. The retract rode `Depart`, a bezier that has covered **15.4%** of its distance at
the halfway point (Material's accelerate: 32.5%). For the first 85ms of a 170ms close the card had
barely moved, then covered the rest in a few frames. `Motion.Retract` reaches 34.1% at the midpoint,
over 200ms - roughly 70% of the 280ms open, per the house exit/enter ratio. Worth recording because it
will be proposed again: "close it the same way it opens" cannot mean mirroring the curve. The literal
time-reverse of the opening ease-out is an even more severe dead-start; mirroring an ease-out ALWAYS
yields a harsh ease-in, and that is exactly the trap the old value fell into. An exit has to share the
enter's character, not its geometry.

Second, and larger: the content was not fading at all. The code called
`Host.BeginAnimation(OpacityProperty, null)` and then started a To-only fade to 0. Clearing an
animation does not freeze a property at what you can see - it reverts it to the LOCAL value, and
`MorphContent` leaves that at 0 (it sets opacity 0 and animates UP to 1, so 1 was only ever the
animation's doing). The null therefore dropped the content to invisible in ONE frame, and the fade
that followed ran 0 to 0 and was never visible. Filmed at 60fps: legible at t=0, gone at t=16ms, with
the card still at full height behind it.

Reading `Host.Opacity` first yields the current animated value, and a From/To animation replaces the
running one with no null needed. A mutation probe in `--revealtest` guards it, verified to FAIL
against the unfixed code. The probe also had to learn to pump the entrance first: without that it was
measuring a card that had never become visible, and it failed against the correct fix.

The clip is only a MASK, so on its own it eats the card while the text inside stands still. The
content now rides up 10px with the furl, mirroring the reveal's settle.

Also fixed here: the `--state` dev seam mapped unparseable names to `Hidden` and then dropped every
`Hidden`, so the one transition the seam exists to drive was the one it could not drive. It now
filters on the parse result.

## A text mode belongs to the typeface, not the window (2026-09-27)

**Decision: `TextFormattingMode=Ideal` + `TextRenderingMode=Auto`, on both windows.**

> **CORRECTED the same day.** This entry originally said `Grayscale`, "measured on a real screen
> capture". THAT MEASUREMENT WAS INVALID and the paragraph below is kept only as the record of how.
> The capturing process was not DPI-aware, so Windows handed it a virtualised 1536x864 view of a
> 1920x1080 screen - every pixel had been through a 0.8x resample, which is precisely the operation
> that destroys subpixel structure. ClearType's fringes became noise, so Grayscale "won" a contest
> it could not lose. Grayscale is WPF's documented FALLBACK quality, blurrier at small sizes; Auto
> means ClearType unless the user has turned font smoothing off. Explicit ClearType is also wrong
> (dotnet/wpf#2025 hardcodes CLEARTYPE_NATURAL instead of asking DirectWrite for NATURAL_SYMMETRIC);
> Auto never hits it. The layered-window half of the old note stands and is now served properly by
> `RenderOptions.ClearTypeHint="Enabled"` on the opaque card plate.

This REVERSES the `Display` decision of 2026-09-26, whose reasoning is preserved in LibraryWindow.xaml
because it was correct for the font it was written for. Display mode rounds glyph advances to whole
pixels and rasterises through the font's TrueType hinting: a win for Segoe UI Variable, which is
hinted heavily and specifically for it. Inter ships almost no hinting - it is drawn for rasterisers
that position glyphs at fractional pixels - so Display forced integer advances onto a design never
fitted to them and returned uneven, broken stems. **The font changed and the mode did not.**

~~Grayscale over ClearType, measured on a real screen capture rather than a `RenderTargetBitmap`:
mean per-pixel edge colour spread 93 vs 0.3, with mean ink luminance 51 vs 41, i.e. the fringing
disappears and the strokes get marginally HEAVIER.~~ (Superseded - see the correction above. The
numbers are real; what they measure is Windows' resampling filter, not Toki.) The owner's own reference screenshot measures 21
on the same metric, so it is not subpixel rendering they were objecting to - it was the broken stems.

Set explicitly rather than left on Auto, and this is the part that matters for the notch: WPF
silently drops ClearType for text under a RenderTransform or on a layered window, and `PillWindow` is
both. Its text was therefore changing rendering mode at the start and end of every state morph.

**Verification note worth keeping: `--render` and `--libshot` cannot see this class of bug at all.**
`RenderTargetBitmap` does not apply ClearType, so every PNG I produced looked clean while the real
screen did not. Text rendering has to be checked with a screen capture.

## Oversampling a bitmap cache makes minification worse (2026-09-27)

**Decision: `TokiBlob`'s `BitmapCache.RenderAtScale` is computed from the size Toki is actually drawn
at (`SyncCacheScale`), not fixed at 4.**

`RenderAtScale` is relative to the cached element's own box, which is the 67x52 board - not to the
screen. A fixed 4 always rasterised 268x208 and let WPF shrink it to whatever size Toki appeared at.
For the 44px notch bubble that is a five-fold minification through a bilinear filter, which samples
four texels out of twenty-five: aliasing, not softness. It is the stair-stepped silhouette and dark
fringe the owner photographed. The old comment claimed 4 "keeps it crisp everywhere it is drawn,
from the 44px notch bubble up to the 256px app icon" - it is backwards for every size except the icon.

Now clamped to [1, 6] with 1.5x headroom (Toki is permanently under a breathe/hop/tilt, and rotation
resamples the cache), quantised to quarter steps so hover growth does not rebuild the bitmap every
pixel. Measured on a screen capture: blended edge pixels per row 2.9 -> 4.5.

## The card keeps its squircle in motion (2026-09-27)

**Decision: `Root.Clip` is ALWAYS a squircle. The animated rect is data (`_clipBox`), and `DriveClip`
rebuilds the shape from it on `CompositionTarget.Rendering`.**

The owner reported a split-second resize on page switches twice. The first time I found a real cause
(pages measuring cold inside the transition), fixed it, and stopped looking. Filming it and measuring
the card's geometry per frame shows **the width never changes** - the morph log reads 380 -> 380 - so
it was never a resize. Every clip animation ran on a `RectangleGeometry` with circular corner radii
while the resting card wore a squircle, so the silhouette snapped shape at both ends of every
transition: 3px per side, measured 20px down from the top edge, plainly visible frame by frame.

Two claims in `Squircle.cs` had made that look safe, and both were wrong; both are corrected in place
there. The half-pixel deviation is the maximum along the corner DIAGONAL, not the horizontal extent
at a given y (smoothing starts the curve at 1.6r = 38px where an arc starts at 24px). And "never
rebuild a squircle per frame" was never measured: across repeated `--fpstest` runs the content cost
stays at 2-3% and ~7 dropped frames per transition, indistinguishable from before. A StreamGeometry
of a dozen segments is nothing beside the layout pass the clip exists to avoid.

Because the clip and the rim now agree, the real squircle rim stays visible through the motion
instead of handing over to Root's arc `Border` - the reason that handover existed has gone.
`ApplyCornerGeometry` returns early while pinned instead of installing the arc, and `DriveClip`
self-parks after six unchanged frames so a caller that forgets to release cannot leak a per-frame
handler. Guarded by `AuditClipIsSquircle` in `--revealtest`.

## An exit copies the entrance's character, it does not derive it (2026-09-27)

**Decision: `Motion.Retract` is FRONT-loaded, like the open, at 180ms.**

Superseding the accelerating curve adopted hours earlier, whose own note argued (correctly) that
mirroring an ease-out yields a harsh ease-in and therefore an exit should share the enter's
character. It then picked a curve that did not. Filmed, both directions, as a fraction of travel:

    50ms in    OPEN 79%    CLOSE 23%

The open is a pop-and-settle - `Drawer` puts it three quarters of the way out in the first 50ms and
spends the rest easing into place. ANY accelerating exit is its mirror image in feel, however smooth
the curve, because it spends its first half barely moving. Both directions now reach ~75% within
33ms.

The duration is matched to the reveal's MEASURED travel (~180ms), not its nominal 280ms: most of
that 280 is the tail of a curve already 99% complete, so matching the constant would have made the
close feel longer than the open while looking shorter on paper.

## Five type sizes, and a floor at 12 (2026-09-27)

**Decision: every piece of text in Toki is 12, 13.5, 15, 17 or 20 DIP, plus 27 for the serif note
title. Nothing below 12. Guarded by `TypeScaleTests`.**

It had SEVENTEEN distinct sizes, thirty instances at 11px or below and three at 9px - which on the
owner's 125% display is eleven physical pixels. The owner named this as the tell: "the fully small
text is the worst obvious tell for AI generated slop". The structural reason they are right is that
a screen with seventeen sizes has no hierarchy at all, because a reader cannot rank seventeen
things. Five steps can be ranked at a glance.

Icon-font sizes are excluded on purpose and the test skips them: a `FontSize` on a Fluent glyph is a
GLYPH BOX chosen to fit a disc or a row, and the chrome glyphs are hinted on a 10px grid. That
exclusion needs a two-line context window either side, because the `FontFamily` usually sits on its
own line - three real text sizes hid behind a nearby icon and had to be fixed by hand.

The guard is a source assertion rather than a runtime one deliberately: the size of any given label
is a design decision, but the EXISTENCE of a scale is an invariant, and what needs constraining is
what a future edit is allowed to type.

## A caption under a heading is generated copy (2026-09-27)

**Decision: no muted explanatory line under a section heading, anywhere.**

This rule already existed, from the owner on 2026-08-20 about the notch: *a settings page that
explains each of its own rows reads as generated copy, not as a product; a row whose label does not
carry it should be renamed, not annotated.* It was written into `SettingsView.xaml` and never
applied to the Library window, which carried one under every section.

Applying it forces the rename it implies: "AI summaries" + "On-device by default, or bring your own
key" became "How notes get written", which says the same thing in the place that was already there.
A syntax hint ("One per line, or comma separated") is not a caption at all - it is guidance for
TYPING, so it belongs in the input's placeholder, where it disappears once you have.

`TypeScaleTests.No_caption_is_tucked_under_a_heading` pins the SHAPE that produced it - a muted
caption pulled up under a heading by a negative top margin - rather than any particular string.

## A heading must outrank the content it owns (2026-09-27)

**Decision: in a note, section headings are `fs + 1.5` SemiBold and the summary is `fs` Normal.**

The summary was `fs + 1.5` at Medium while the headings beneath it were `fs + 1` SemiBold: bigger
AND heavier, so one paragraph of content outranked the page's own organising structure. The owner
read exactly that - "notes now have summary in bold, and everything else is smaller and weird".

The 2026-09-03 intent behind the old sizing was right and survives: *someone who reads nothing else
still leaves with the point.* What carries that is POSITION - first under the title, full ink, air
beneath it - not extra weight. A lead paragraph does not need to shout to be read first. The result
is one clean descending step per level: 27 serif title, 15 heading, 13.5 body, 12 detail.

## An optically-centred glyph should be geometry (2026-09-27)

**Decision: the Ask send arrow is a `Path`, not a font character.**

A glyph carries the font's own side bearings, so "centred" means centred INCLUDING whitespace that
is not equal on both sides. The previous fix was a 1px `TranslateTransform` measured by hand against
that asymmetry, and the owner still read the button as off-centre. A `Path` has no bearings: its
bounding box IS the drawn shape, so `Stretch="Uniform"` in a fixed box centres it exactly, at every
DPI, with no constant that can drift out of date.

This is the third control to make the same move (the play triangle, the back chevron, now send), so
it is the house rule rather than a one-off: **a small glyph that has to sit centred is drawn, not
typed.** `ActionButtonTests` was updated to guard the new invariant - it had been pinning the
nudge's presence, which is a SPELLING, not a behaviour.

## The morph's first 72ms are the only ones that matter (2026-09-27)

**Decision: the mascot's idle suspends for the duration of a morph, and the clip repaints only on
changes of a quarter pixel or more.**

The owner reported the notch still "resizing" after the corner-shape fix. Filmed at 60fps it looked
like an 87px snap in one frame - and that snap was in the CAPTURE; ffmpeg was dropping frames. A
per-frame trace of the clip rect showed the animation running its full 300ms smoothly, and showed
the real defect: the first ~72ms ran at 15-30fps while the tail ran at 120. `Drawer` is heavily
front-loaded and spends 78% of its travel in that window, so the stuttering part is the part the eye
actually reads, and the smooth part is the part where nothing moves.

Both causes were self-inflicted by the quality work that preceded them. The mascot now draws as live
vector with no bitmap cache, so every transform write re-rasterises three gradient-filled beziers -
during the one moment the UI thread is already building and laying out the incoming page. And the
squircle was being rebuilt on frames where the edge moved by hundredths of a pixel, because the
guard was exact `Rect` equality, which never fires on animated doubles.

Measured the same way afterwards: first-90ms frame gaps went from an 11ms freeze plus a 31ms stall
to a steady 8-9ms. Nothing is lost visually - sub-pixel idle drift is invisible under a resizing
card.

**`TOKI_CLIPTRACE=1` is kept as a dev seam.** It is the only instrument that answers "is this
smooth" honestly; a screen recording cannot, and on this occasion it actively lied.

## A silent null is indistinguishable from a fault (2026-09-27)

**Decision: every path in `LlmService.Active()` and `WriteNotesAsync` that returns null logs why.**

`--llmtest` failed reporting "LLM returned no notes (model unavailable?)". The model was fine:
`NotesMode` was `HeuristicOnly`, so `Active()` returned null and the model was never asked. Two
paths did that in complete silence, which made a deliberate SETTING and a broken MODEL identical
from outside - and the self-test then papered over both with a guess that sent the diagnosis the
wrong way for some time.

Both paths now log. The self-test reports the state it found (mode, whether Qwen is installed)
instead of speculating, and names the setting when that is the cause. This is the same rule the
heuristic-floor incident produced, applied to the floor but never to the switch above it: **a
fallback good enough to ship is good enough to hide the failure that triggered it, so make it
loud.**

Worth recording separately: the first explanation offered for that failure was memory pressure,
supported by a real number (3.5GB free of 11.4GB). The number was true and the causal claim was not
- the machine sits there normally, and the test had passed in that same state forty minutes
earlier. The owner pushed back and was right. A measurement is not a diagnosis.
