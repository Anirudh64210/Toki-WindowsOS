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

**Decision: a *manual* start still captures the other side.** Starting a recording from Home/hotkey used to be mic-only (`BeginRecording(null)`), so a user who muted themselves and hit record saved nothing but silence — a real lost meeting. Now `BeginRecording` resolves a target when none is given (`ResolveMeetingTarget` → the non-denylisted app currently holding the mic, via `MicUsageReader`) and taps its process tree too. Auto-stop (target releases the mic) stays armed **only** for prompt-accepted meetings — a manual start is stopped by the user, because some apps drop the mic on mute and that must not end a manual recording. A sub-3s recording with <5 words is treated as an accidental tap and discarded (no hollow Library entry).

## Overlay dismissal & focus (M3 fix)

Click-away/Esc dismissal uses **global low-level mouse + keyboard hooks** (`InputMonitor`), not WPF window activation. Activation-based closing fired on unrelated focus changes and flickered the pill shut on launch. The hooks let Toki stay a true no-focus overlay (fits ghost mode) and only dismiss on a real click *outside* the shell rect (with a 500ms grace period after opening). Escape is handled the same way so it works without the window holding keyboard focus.

## Window architecture (revised M3.2) — non-layered, hardware-accelerated

**Decision:** the pill is a **non-layered, opaque, hardware-accelerated window** whose corners are rounded by DWM (`DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND`, plus a subtle `DWMWA_BORDER_COLOR`). The window *is* the pill — it resizes to each state's content and anchors bottom-center.

> **Superseded (2026-07-17):** the "non-layered/opaque is required for ghost mode" claim below was a **misdiagnosis**. The pill is now `AllowsTransparency=True` (a genuinely translucent glass window) AND ghost mode works — the old black rectangle was the pre-2004 `WDA_MONITOR` fallback / a hide→show affinity revert, not a transparency conflict. See "The pill IS a translucent glass window" further down.

**Original reasoning (kept for history):** that path (a) forced slow software rendering, and (b) was thought **incompatible with `SetWindowDisplayAffinity`** (ghost mode) — the layered + capture-exclusion combination appeared to render a black rectangle. This turned out to be fixable (use `WDA_EXCLUDEFROMCAPTURE` 0x11, not `WDA_MONITOR`, and re-apply on show).

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

**Both engines warmed at launch — gently, and memory-permitting.** On a machine with enough RAM, `WarmEnginesGently` loads the ASR then the LLM on a single **below-normal-priority** background thread, after a short delay and staggered, so a meeting-stop pays no model-load time without the launch making the PC stutter. On a small-RAM PC the LLM is **not** prewarmed (see below).

### Memory: one heavy model at a time on a small PC (`App.LowMemoryMode`)

**Finding (paid for on the owner's 6 GB laptop): holding both models resident is what actually crashed transcription, made first-launch clunky, and slowed everything.** Qwen3-4B (~2.5 GB) + Parakeet (~630 MB), on top of a browser and the meeting app, does not fit in 6 GB. The symptoms all traced to this: a real 35-min meeting saved *"nothing was said"* (onnxruntime threw the uncatchable-looking native `External component has thrown an exception` — it's an allocation failure), the mouse stuttered on first launch (heavy Windows memory-compression + paging while the 2.5 GB model preloaded), and processing dragged (swapping).

`Interop.SystemMemory.Physical()` (P/Invoke `GlobalMemoryStatusEx`) reads total RAM at startup; `< 8 GB` sets `App.LowMemoryMode`. In that mode Toki keeps **only one heavy model resident at a time**:
- **Before transcribing**, `TranscriptionService.Process` calls `LlmService.Release()` (dispose weights + GC).
- **Before writing notes**, `LlmService.EnsureLocal` calls `TranscriptionService.Release()`.
- **The LLM is never prewarmed** (the big preload was the main launch-clunk cause); it loads on demand at meeting-end.
- **After notes**, the LLM is released and the small recognizer is re-warmed, so idle Toki is light and the next meeting is ready.

Peak footprint drops from ~3.1 GB (both) to ~max(2.5, 0.63) GB. The cost on a small PC is a few seconds of model-load at meeting-end instead of it being pre-ready — a deliberate trade of a little latency for reliability and a responsive desktop. On an 8 GB+ PC nothing changes (both stay warm).

### Recording presence: calm, not a stuck animation

**Decision: the recording pill is a small, quiet chip — no looping waveform.** The old recording bar sat at bottom-center the whole meeting showing a **fake** animated "waveform" (a fixed decorative pattern, not real audio levels), which read as a distraction sitting on screen for 30+ minutes. It's now a compact chip (138×46): a slowly-breathing coral dot, the elapsed time, and a stop button. Click to expand for the transcript view. The expanded view's waveform became a calm "● Recording" indicator too.

### Click-outside dismissal: only ephemeral popups

**Decision: a stray click into your real work must never dismiss content you're using.** Click-outside used to close Home, Library, Settings, **Notes**, and NoSpeech — so clicking into the meeting app while reading notes made them vanish. Now `DismissOnClickOutside` is just `{Home, NoSpeech}` (the launcher menu and the transient "nothing was said" toast). Notes, Library, Settings, ManualNote — and recording/summarizing/prompt — stay until closed with their own button or Esc.

### The pill IS a translucent glass window — and ghost still works (corrected)

**Correction to an earlier claim:** translucency and ghost mode are NOT mutually exclusive. Cluely and "cheating daddy" (both Electron) ship a transparent window with `setContentProtection(true)` = `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`, and it works — the affinity is enforced at the DWM level (BitBlt / DXGI / Graphics-Capture all exclude it) regardless of transparency, on Win10 2004+.

**So `PillWindow` is now `AllowsTransparency=True`, `Background=Transparent`.** The Border is the pill: a translucent gradient fill (~0x9E alpha) + a faint top sheen, rounded by its own `CornerRadius` (no `SetWindowRgn` — outside the Border the window is genuinely transparent, so corners are clean). Verified on the owner's PC: `--noghost` capture shows a see-through pill; ghost-on capture shows the pill **absent** (still hidden from recording).

Two real gotchas: (1) **re-apply the affinity on every `Show()`** — Windows drops it across hide→show (`ApplyGhost()` runs after each show); (2) **do NOT use the DWM accent acrylic** (`SetWindowCompositionAttribute` / `ACCENT_ENABLE_ACRYLICBLURBEHIND`) — it fills the *square* window and shows sharp black corners around the rounded pill. Translucency comes from the Border brush, not the compositor.

### Icons: Segoe Fluent, not ambiguous unicode

**Decision: all icon buttons use the `Segoe Fluent Icons` font** (falls back to `Segoe MDL2 Assets` on Win10). The old unicode glyphs (`⧉` copy vs `⤢` open) rendered nearly identically and read as ugly. `IconButton` is now a clean floating glyph (`TextDim`) that lifts onto a subtle rounded highlight on hover. Glyphs: copy `E8C8`, open-in-window `E740`, close `E711`, back `E76B`, settings `E713`.

### Library open animation + drag-to-reorder

**Open animation — grows out of the pill.** The first attempt scaled the content in place from the window's own bottom-centre, which read as a "force open" because the pill sits at screen-bottom while the window is screen-centre (disconnected). Now `HomeView` passes the pill's on-screen rect (`PillWindow.ScreenDipRect()`, physical→DIP) and `LibraryWindow.PlayOpen(fromRect)` builds a `TransformGroup` (scale + translate) that starts with the content occupying that exact pill rectangle and animates to identity (`RenderTransformOrigin=0,0`; `scaleX0=pillW/winW`, `translate0=pillTopLeft−winTopLeft`), ~300ms `CubicEase`, plus a fade. GPU transform on laid-out content — no reflow. Falls back to a gentle centred grow if the rect is unavailable.

**Drag-to-reorder — settles cleanly on release.** Cards are pickable; past a 4px threshold the card lifts (ZIndex + opacity) and follows the pointer via a `TranslateTransform`, while the others animate a shift computed from a correct *insertion index among the non-dragged cards* (`shift = (i>from? −dh:0) + (oi>=ins? dh:0)`) — this fixed an off-by-one that mis-placed the drop. **On release the card animates into its target slot (settle, ~160ms) before the order commits + list rebuilds** — the earlier version returned early on a same-slot drop and left the card stuck between rows; now every path resets. Persistence: a new `meetings.sort_order` column via idempotent migration (`ColumnExists` guard), seeded to the existing newest-first order; `List` orders by `COALESCE(sort_order, …) ASC, started_at DESC`; new rows get `MIN(sort_order)-1` (top). A short click (no drag) still selects.

### Notes render monotone (owner preference over the coloured design)

**Decision: `NoteRenderer` section labels are monotone dim-grey uppercase — no coloured headers, no icons/ticks.** The design (`⑤`) uses green/blue/orange section headers, but the owner wanted an Apple-clean monotone look ("red and green never go on text"). So SUMMARY / KEY POINTS / ACTION ITEMS / TRANSCRIPT are quiet `TextDim` labels; bullets use a small dim dot; owner/due and YOU/THEM chips are neutral grey (YOU a shade brighter than THEM for distinction). Colour is reserved for content, never chrome. Spacing is deliberately generous and consistent so it always reads clean. The "● ON-DEVICE" badge was removed from both the pill Library view and the window.

### Library window cards match the design

Cards in `LibraryWindow` now render a calendar-style **date badge** (day over 3-letter month), title, a meta line (type · duration · relative time), the TL;DR snippet, and count chips ("N open" / "N people") — all already on `MeetingSummary`. Selected card gets a coral hairline. Matches `Toki-ProductDesign/⑥`.

### Long recordings never crash or OOM (windowed ASR)

**Finding: whole-file decode of a very long clip throws in the native recognizer, and the old loader OOM'd before it even got there.** Two fixes in the ASR path:
- `AudioLoader.To16kMono` now fills one exact-size array instead of a doubling `List<float>` + `ToArray` (which transiently held 3–4× the samples — it OOM'd on a 35-min file).
- `Transcriber.Transcribe` keeps the single whole-file path for normal-length meetings (best accuracy, lesson 3) but, past a safe length (~6 min; env `TOKI_ASR_MAXSEC`), walks the audio in ~3-min windows **cut at the quietest nearby 25 ms frame** so no word is split, decoding each with a fresh stream and offsetting timestamps to meeting time. A per-window `try/catch` means one bad window can't lose the whole meeting. Verified on the owner's actual 784 MB / 35-min recording that previously crashed.

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

### Transcript segmentation: split only at word boundaries (M6.6)

**Finding (paid for on a real 20-min meeting): the segment splitter was cutting words in half.** `Transcriber.GroupIntoSegments` starts a new transcript line when two decoded pieces are separated by a time-gap (>0.8s). On short, fast test meetings the gap never landed mid-word, so early results looked perfect. On a real meeting with hesitant speech ("sorry, my audio — just a minute") the gaps land *inside* words, because Parakeet emits words as sub-word pieces. The result: "Deepika" → four lines `D / eep / ik / a`, and notes built from those fragments read as "a udi o" / "min ute".

**The tokenizer marks word starts with a LEADING SPACE, not `▁`.** Verified by dumping `OfflineRecognizerResult.Tokens` on a real clip: `" R","as","h","m","i"` = "Rashmi". The old `Detokenize` did `Replace("▁"," ")` — a silent **no-op** here (there are no `▁`); the spaces were already literal in the tokens, so text *looked* right while any `▁`-based logic was dead. So a word-boundary guard must test for a leading space (`StartsWord` accepts a leading space **or** `▁`, to also cover other SentencePiece builds).

**Rule now: only break at a word boundary, on a pause OR end-of-sentence.** A split fires at a token that starts a new word (leading space) when either the pause exceeds 0.8s **or** the text so far ends a sentence (`. ? !`, which this build does emit). This keeps every word whole (sub-word pieces never trigger a split) and lands lines per-utterance — not per sub-word (the bug) and not one run-on paragraph per decode window (which would defeat the ±4s echo de-dup and You/Them interleaving). Measured on the meeting that shattered: **912 → 82 segments**, words whole, tail matches the audio end.

### Skip a near-silent stream before decoding (M6.6)

Real logs showed meetings where the user's mic was muted the whole call (`you peak 0%`), yet we still ran the full ~4×-real-time decode over a silent `you.wav` — about **half the total processing time, for nothing**. `TranscriptionService.TranscribeStream` now scans the loaded samples for any amplitude above a low floor (~-46 dBFS, early-exit) and skips the decode if the stream is silent — silence is not failure (lesson 4), a muted side just isn't transcribed. Halves processing whenever one side is muted; no effect on a two-active-party meeting (where live transcription remains the only real speed win).

### Library window opens instantly — profiled, three real causes (M6.6)

Opening the Library felt like a ~2s freeze (owner: "does not happen with WisprFlow"). Warming the window alone did **not** fix it — profiling with per-phase stopwatch logs found the cost was rebuilding content on every open, dominated by one thing:

- **Rendering the whole transcript synchronously.** `NoteRenderer.Meeting` did `foreach (seg in Transcript) body.Children.Add(TranscriptRow(...))` into a **non-virtualizing** `StackPanel`. The auto-selected newest meeting had **1140 segments** (× ~5 visual elements each) → thousands of elements built on the UI thread on the click. Fix: render a small first batch (25 rows) then **stream the rest in on `DispatcherPriority.Background`**, chunked, with a per-panel generation token so switching meetings cancels stale work. Render dropped from ~seconds to ~30ms.
- **`Reload` ran twice per open.** `Show()` raises `Loaded`, and a `Loaded += Reload` handler duplicated the explicit `Reload` in `ShowLibrary`/`Warm` — every open rebuilt the entire list+detail twice. Removed the handler.
- **Rebuilding when nothing changed.** The window is now kept alive (Hidden, never destroyed) and only re-`Reload`ed when a `_dirty` flag is set — `LibraryWindow.MarkDirty()` is called when a recording saves a meeting, a note is created/edited, or a meeting is deleted. A plain re-open with no data change is just `Show()`.

Measured (owner's PC, on the 1140-segment meeting): cold first-ever open ~940ms → **warm + dirty-skip open ~80ms**. Warming (`Warm()`, below) pre-pays the one-time create+layout+first-render off-screen at startup on a Background dispatcher tick, so even the first real open hits the fast path.

#### Kept warm, never destroyed

**The clunk was the window *building*, not the animation.** Opening the Library created a fresh 940×640 `Window` and laid out the whole master–detail view synchronously on the click — so the empty frame appeared, then visibly populated ("loads the window"), and only after all that did the pill finally hide. A slow, overlapping handoff. Earlier attempts animated the window's *opacity*, which made it worse: animating a whole `Window`'s Opacity forces Windows to make it a **layered window** and re-blit the entire surface on the CPU every frame → stutter.

**Fix: create the window once, keep it warm, open instantly.**
- `LibraryWindow` is created a single time and **pre-warmed off-screen at startup** (`Warm()`, on a Background-priority dispatcher tick): shown far off every monitor with `ShowActivated=false`/`ShowInTaskbar=false`, laid out (`UpdateLayout`), then hidden — so the expensive first measure/arrange/render is paid once, off the critical path.
- Opening is now just `Reload` + `CenterOnWorkArea` + `Show` on the warm instance → instant, no visible build. `WindowStartupLocation=Manual` (we place it ourselves).
- It's **hidden, never destroyed**, between opens: "‹ Back to Toki" and the OS **X** button both route to `HideToPill()` (hide + bring the pill back) via a `Closing` handler that cancels the close. Guarded by `App.Exiting` / `Dispatcher.HasShutdownStarted` so a real app-quit still closes it (otherwise the cancelled close would keep the last window alive and block shutdown).
- Handoff order flipped: the pill is dismissed **first**, then the warm window shows — no more "window builds while the pill lingers". (No window-level opacity/scale animation at all now; instant is the smoothest.)

### Hover-reveal pill: cursor polling, not WPF mouse events (M6.7)

The pill is now an always-present tiny Toki that reveals a bigger view on hover — the launcher (Collapsed → Home) and recording (RecordingBar → RecordingOpen) share one mechanism. Two things had to be right:

**Smooth grow, not a size-snap.** A hover expand/collapse **animates the window's Width/Height** (`AnimateSize`, ~300ms open / ~190ms close so closing feels snappy) with the pill re-centred bottom-anchored on every `SizeChanged` (via `PlaceBottomCenter`, which does NOT call `UpdateLayout` — that would throw inside a layout pass). Content fades in over the morph to mask the reflow. Only these hover pairs animate; every other transition stays instant.

**Hover detection polls the real cursor, NOT WPF `MouseEnter`/`MouseLeave`.** While the window animates its size and slides under a stationary pointer, WPF's hit-testing fires phantom leave/enter events, which flip-flopped the pill open/closed in an endless loop. Instead a 60ms `DispatcherTimer` (active only in the four hover states) compares the real cursor position (`GetCursorPos`) against the pill's real cached rect. Because the expanded rect contains the collapsed one, this gives clean **hysteresis** — on it → open, off it → closed, no flicker — with a **dwell** (~260ms before opening, so a graze doesn't) and a **quick close** (~120ms). Toki blinks (`TokiBlob.BlinkNow`) on the rising edge of hover.

**State machine:** the hover pairs must be allowed **both ways** (`Collapsed ⇄ Home`, `RecordingBar ⇄ RecordingOpen`) in `AppState.Allowed`, or `TryGo` silently rejects the collapse — that was a real "Home won't minimise back" bug (Home→Collapsed was missing).

### Library maximise: clamp to work area + fixed-width content (M6.7)

Two separate maximise problems, both fixed:
- **The frame maximised but the content lagged then snapped to fill.** Cause: the detail was a full-width `StackPanel`, so widening the window forced WPF to **re-wrap and re-measure every transcript line** (hundreds–1000+) at the new width — that reflow trails the OS maximise. Fix: the detail is a **fixed-width readable column** (`MaxWidth`, ≤ the default column width so restored vs maximised are the *same* text width → nothing to re-wrap → WPF re-renders instantly). Also better typography. Meetings centre the column; the manual-note editor left-aligns it.
- **Jump / white edges.** A per-monitor-DPI window with a manual position mis-sized on maximise. `WM_GETMINMAXINFO` now pins the maximised bounds to the current monitor's **work area**, and `DWMWA_BORDER_COLOR` is set dark so the Win11 border isn't white.

### The virtual notch (top-edge overlay) — M7 UI

Toki moved from a bottom-centre pill to a **top-centre virtual notch** (flush to the top edge, bottom-rounded, grows downward). Mechanically it's the same morphing overlay re-anchored: `PlaceTopCenter` fixes the TOP edge (so height animates downward), `HideAnimated` retracts the height (tucks up), the Border is `CornerRadius="0,0,22,22"` with no top border. The whole state machine / hover-morph / blink / ghost mode carried over.

**Peek vs. open.** The resting/recording peeks (`Collapsed`, `RecordingBar`) drop the dark frosted surface entirely — `Root.Background`/`BorderBrush` go transparent and the sheen hides, so Toki is *just the character* floating from the edge. The dark panel only appears once it opens into a usable view. Opening Home from the launcher peek is **deliberate** (long `OpenDwellMs` dwell, or a click on `CollapsedView`) — a brief hover only shows the peek, because after an edge-summon the cursor is already there and a quick dwell would auto-open (felt wrong).

**Edge-summon, tuned for the crowded top.** When tucked (`Hidden`) the hover poll runs `EdgeTick`: accumulate dwell only while the cursor is at the **absolute top row** (`EdgeTopPx`), inside a **narrow centre band** (`EdgeBandHalf`), and **moving slowly** (velocity gate — a fast fling toward a browser tab is ignored). `EdgeSuppressed()` blocks it while the **left mouse button is down** (a window drag / Snap Layouts in progress) or a **fullscreen** app is foreground (`SHQueryUserNotificationState` → busy / D3D-fullscreen / presentation). Env `TOKI_NO_EDGE` disables it.

**Auto-tuck behaviours.** The launcher peek tucks back to `Hidden` once the cursor leaves both the peek and the edge. **Results** (`Notes`, `NoSpeech`) run a 2s `_resultTuck` timer that keeps them up while the cursor is over them and tucks them away ~2s after the cursor leaves — a glance, not a panel that squats on your work. `Home` does NOT auto-tuck (it's an explicit summon, dismissed by Esc/click-away) — auto-collapsing it made a hotkey/launch summon vanish instantly.

**Multi-monitor.** `PhysicalWorkArea` now resolves the monitor under the **cursor** (`MonitorFromPoint`), so the notch summons on whichever screen you're using and events land on the active screen.

### Context-aware snap-bar: peek → page → sliver (M7.1)

The notch became a stateful snap-bar. Three visual levels on one morphing window: **Toki peek** (`Collapsed` — character only, no dark box), **full page** (Home/Notes/Settings/…), **sliver** (the page tucked to a ~13px strip, view kept behind it). Transitions:

- **Summon** (launch / edge / hotkey) → **`Collapsed`** (Toki first, greets `CollapsedGraceMs` before it can tuck). **Click** (`App.OpenPeek`, set by the pill) or a long dwell (`OpenDwellMs`) → the **remembered page** (`_context`, default Home), blooming open.
- **Tuck** — a page tucks to a sliver `IdleTuckMs` (~2.5s) after the cursor leaves, or immediately on **click-outside** (the `InputMonitor` dismiss now calls `TuckCurrent`, NOT `ForceForDemo(Hidden)` — that's what preserves context; clicking your work no longer resets Toki to Home). The loader (`Summarizing`) tucks after `SummarizeShowMs` regardless of hover; **results** (`Notes`/`NoSpeech`) *enter* tucked (a quiet peek, never a panel dropped in). A tucked sliver `_autoHide`s to `Hidden` after `AutoHideMs`, `_context` remembered — so re-summoning restores the page.
- **Expand** — hovering a sliver runs `ExpandCurrent` (animates height back to the state's full size). No view rebuild — context is the same view instance.

`TuckCurrent`/`ExpandCurrent` are height-only `AnimateSize` animations that never change `_state` or clear `Host`, so the page is preserved. `enterTucked`/`_revealTucked` let a reveal enter directly as a sliver.

**Timer priority — the "works in demo, not in real use" bug.** `DispatcherTimer` defaults to `DispatcherPriority.Background`. The idle/auto-hide/hover timers were therefore **starved while the app warms the ~3 GB of ASR/LLM models at launch** — exactly when Home and the loader are on screen, so they never tucked (Notes, appearing after warm-up, did — which is what the owner observed). All three pill timers are now `DispatcherPriority.Input`.

**Accidental-tap guard.** A ~5-word mis-click was saved as a meeting because the old guard needed `<3s AND <5 words`. Now `words < 12 || durationMs < 4000` — a mis-click yields a handful of garbled words and is dropped; a real meeting always clears the bar.

### Close-off that sticks — the real "context-aware" model (M7.2)

M7.1 tucked pages to a persistent sliver. In real use that was two problems: the sliver **squatted on the screen**, and it **re-summoned itself**. The log was unambiguous — `Home → Hidden → Collapsed → Home` cycling every ~2 s. Root cause: the top-centre edge-summon zone is **the same physical space as a maximised browser's tab strip**. While you work in Chrome your cursor lives up there, so the moment a page closed off, the cursor (still at the top edge) tripped the summon again. Close-off *worked*; it just couldn't *stay*.

The fix is a **priming/arming** model, the industry-standard way to disambiguate a deliberate reach from an incidental hover:
- **`TuckCurrent` closes a page off entirely** (`ForceForDemo(Hidden)`, `_context` kept) rather than leaving a sliver — the screen stays clean while you work. The **loader is the sole exception** (it keeps a clean shimmer sliver so you know it's working).
- **`EdgeTick` only summons when `_edgeArmed`.** The arm is set true only once the cursor is **≥ `EdgeReArmBelowPx` (150 px) below the top edge** (down in the page body), consumed on each summon, and reset to false on every transition to `Hidden`. So a cursor parked on the tab strip can never summon; you must reach *up from the page*. Dwell also went 220 → 300 ms. An `_edgeReadyTick` cooldown (700 ms after any close-off) covers the immediate hand-off. This is what makes close-off feel like it sticks.
- **Reach-the-edge restores what Toki is *doing*.** If `_context == Summarizing` (writing your notes) the edge brings the loader back so you can check progress; otherwise Toki peeks first. "Access whatever Toki is doing by hovering the top-centre."
- **Results are one-shot.** Leaving `Notes`/`NoSpeech` → `Hidden` resets `_context` to `Home`, so re-summoning gives Toki, never the dead result (the "click Okay → hover → nothing-was-said again → forever" loop). `Notes`/`NoSpeech` now **enter open** (a ~2 s announce, `ResultShowMs`) then tuck; the **loader enters directly as a persistent sliver** (no flash) — the inverse of M7.1, matching "after recording, straight to a working sliver; when notes are ready, open briefly then tuck."

**Surface = pitch black.** `Root.Background` is a fully-opaque `#000` slab, no white sheen (the sheen `Border` is kept, referenced by code, but painted transparent). The translucent-frosted look washed out and made 13 px slivers invisible; an opaque black notch reads as carved-out and makes the sliver a crisp dark tab.

**Loader sliver = shimmer only.** `SummarizingView`'s top row is fixed at exactly `SliverH` and carries only a sweeping coral shimmer; the Toki + status line live in a second row that's below the fold when tucked — so the sliver is never a clipped slice of Toki's head.

### Notes: map-reduce + adaptive/honest (M8)

**Finding first (lesson 1: garbage in, garbage out — check what the LLM is fed).** The saved notes plus the logs showed the real failures, not "the model is dumb":
- A 25-min transcript is far larger than the model's **~3072-token context** (`LlmRuntime`, partial-GPU). The single `CombinedPass` overran it, `CompleteAsync` threw, and `WriteAsync` silently fell back to the **heuristic** — whose TL;DR is literally the first sentences and whose "to-do" is every keyword-matched line. That is the "summary = first three lines" bug.
- A short meeting *fit* in context, so the LLM ran — and the prompt **forced** KEY POINTS / ACTION ITEMS on every meeting, so it padded a screen-share logistics chat into corporate filler.

**Design — do it in pieces, adapt the shape:**
- **Map-reduce.** `Chunk()` splits the transcript at whole-segment boundaries into windows under `MaxChunkChars()` ( `(ContextSize − 800) × 3` chars, sized to the model). `MapChunk` compresses each window to a factual digest (or `NOTHING`); `FinalNote` reduces the digests (or, for a single window, reads it raw). Summarising summaries can't overflow. `StatelessExecutor` makes every call independent, so the several passes are safe and serialized.
- **Adaptive by omission.** The reduce prompt emits only the sections that apply; `MarkdownMirror` and `NoteRenderer` already guard on `Count > 0`, so empty sections vanish with **no schema/DB change**. `NoteSet.Decisions` stays repurposed as KEY POINTS.
- **Honest, not filler.** `LowSubstanceNote()` is a one-line honest note for genuinely empty exchanges. The `NOTHING` trigger is deliberately **narrow** — an early over-eager version dismissed a real (but informal, and shattered — see M8.1) working session as "nothing substantive," so the prompt was tightened to fire only on pure greetings/small-talk/connection-checks.
- **Verified against real data** via `--renotes <id>` (re-runs the writer on a stored transcript). This is why the harness matters: it let the fix be proven on the exact meetings that failed, no re-recording.

**How this meets the parked live-transcription work (owner's "run both optimally").** The same chunk boundaries are the unit of live work: with **incremental transcription** (Phase 2) the transcript finalizes per-utterance *during* the meeting, and on capable machines the **map** passes run as a rolling summary as the meeting proceeds — so Stop needs only a final reduce. On low-RAM PCs the recognizer and LLM never sit resident together: transcribe live (recognizer resident), then release it and load the LLM for the chunked map-reduce at stop. Map-reduce is the design that makes notes and live transcription compose.

### Transcript grouping — pausey speech (M8.1)

`GroupIntoSegments` breaks a segment at a **word boundary** on a sentence end *or* a >0.8 s pause. The pause rule alone shattered slow/deliberate speech into one word per line (every word had a >0.8 s gap). Added a `words` counter: the **pause break only fires once the line has ≥ `MinWordsForPauseBreak` (8) words**; sentence ends still break freely. Slow speech now forms full-sentence lines; genuine long un-punctuated runs still get chunked at ~8-word boundaries (needed because Parakeet punctuation isn't always reliable, and echo de-dup needs utterance-sized lines, not one giant run-on).

### Bring-your-own API key — cloud-speed notes, provider-agnostic (M9)

The honest fix for the multi-minute local wait (see `mansplain/16`) is optional: let a user point Toki at a cloud model they already pay for. Constraint #1 still holds — **it's the user's key and the user's cost; Toki spends nothing.** Decisions:

- **One provider-agnostic client, not a Claude/OpenAI SDK.** `ApiLlm` speaks the OpenAI **chat-completions** wire format (`POST …/v1/chat/completions`), which virtually every provider and router implements. So OpenAI, Groq, OpenRouter, a local Ollama/LM Studio proxy — and **Claude, via Anthropic's OpenAI-compatibility endpoint** — all work through the same code with no provider branches. The owner was explicit: "any router, just paste a key," and rejected per-provider model chips as confusing.
- **`https://` is the protocol; the *address* is what differs per provider.** `TryBuildEndpoint` normalises whatever base URL a provider needs (`api.openai.com`, `api.anthropic.com`, `api.groq.com/openai/v1`, `openrouter.ai/api/v1`, a loopback proxy) to end in `/v1/chat/completions` exactly once. **https is required over the network; plain http is allowed only for a loopback host** (local model, traffic never leaves the PC). `TryBuildModelsEndpoint` shares the same core (`TryBuildV1`) for `…/v1/models`.
- **Security posture (the whole point of doing this carefully).** The key is **DPAPI-encrypted at rest** (`AppSettings.ApiKeyProtected`, `CurrentUser` scope — decryptable only by this Windows user on this machine) and stored **only** in `%APPDATA%\Toki\settings.json`, never in the repo. The cleartext `ApiKey` property is `[JsonIgnore]`, so plaintext is never serialised. Over the wire the key rides on a **per-request** header (never the shared `HttpClient`), goes only over https, is **never logged**, and is scrubbed from any error text (`ProviderDetail` does `Replace(_apiKey, "•••")`). Every failure surfaces as a short, key-free human message; `TestAsync`/`ListModelsAsync` never throw.
- **Claude's two auth shapes.** Chat-completions on Anthropic's compat endpoint takes a **Bearer** token like everyone else; its **`/v1/models`** list instead wants `x-api-key` + `anthropic-version`. `ListModelsAsync` branches on host `api.anthropic.com` for that — the one technical Anthropic special-case, and it is *not* a default (see below).
- **Provider-first UI, search-dropdowns.** The flow is **pick provider → address auto-set → paste key → pick model**. Provider + model are compact `SearchableDropdown` controls (a 34 px pill that opens a floating `Popup` — its own top-level window, so a long model list never grows the notch — with a type-to-filter box shown only past 8 items). An earlier **iOS scroll-wheel** version (`WheelPicker`) was built and **rejected by the owner as ugly/bloated** — deleted. "Custom" reveals a free-text address for anything unlisted or local.
- **Live model list.** The model dropdown is filled by a real `GET …/v1/models` against the chosen provider, so it's always current (owner chose live-fetch over a curated static list). A successful fetch doubles as a key check. Switching provider **clears the saved model** and refetches, so it's never "stuck" on the previous provider's pick.
- **No provider is the default.** An early version auto-targeted Claude and defaulted the model to `claude-sonnet` — the owner corrected this twice ("Claude is not default; you decided to build around it"). Removed: `PickDefault` now leans neutrally toward the cheaper tier via **generic markers** (`mini`/`instant`/`flash`/`small`/`haiku`/`8b`), never a provider name; the factory default is the stock OpenAI base.
- **Positive-enforcement state — one message, one green dot.** `AppSettings.ApiVerified` (persisted) is true **only after a successful Test connection** (which hits chat-completions, the real notes path); any key/model/provider change clears it. `LlmService.Active()` still gates notes on `ApiLlm.Ready` (key+model+endpoint present) — separate from *verified*, so we never claim a key works on faith. The UI shows a **single** connection line with a status dot: dim when unverified, **green with a soft outward pulse** when verified. (The owner first said "no green"; that meant the earlier garish tick — a tasteful green dot/pulse is the wanted signal. The duplicate "Connected" line and the redundant "Model status" line in API mode were removed.)

**Headless render must never touch the network or settings (a cost-real bug).** The `--render` PNG harness constructs the *live* `SettingsView` against the user's *real* `%APPDATA%\Toki\settings.json`. The model-fetch fired under render, hit the network, and `s.Save()`'d — racing across overlapping render invocations and corrupting settings. Fix: `App.Headless` is set before `RenderHarness.RenderAll`, and `RefreshModels`/the pulse animation early-return on it. **Lesson (documented in memory):** never mutate the real settings file to render a state — PowerShell JSON round-tripping mangles the DPAPI blob, and doing so once destroyed the owner's saved key. Verify settings-dependent states with an isolated/throwaway file written by a BOM-free writer, or not at all.

### Ghost mode — a per-window opt-in, not a blanket rule (M9.1)

Ghost mode = `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE = 0x11)`: the notch is excluded from **all** screen capture — Snipping Tool, Xbox Game Bar, **OBS**, and Teams/Zoom screen-share alike (the same mechanism Electron's `setContentProtection` uses). The point is polish + privacy — Toki never leaks into a screen you're sharing in a meeting. Windows can drop the affinity across hide→show, so the pill re-applies it on every show. The cost is that you can't record a demo of Toki either, and the owner needed to. Decisions:

- **A visible, opt-in Settings toggle — not the dev flag.** `--noghost` already forced ghost off for dev capture tools, but it isn't discoverable. Added `AppSettings.ShowInRecordings` (persisted, **default `false` = hidden**), surfaced as "Show Toki in screen recordings & sharing" under a new **Recording & sharing** section in Settings (same row shape as the GPU toggle — no new control invented). The default stays private; the toggle is a deliberate opt-*out* of ghosting, flipped only to record. Startup resolves `App.GhostDisabled = args.Contains("--noghost") || AppSettings.Current.ShowInRecordings`.
- **Applies live, in both directions.** `ApplyGhost` now sets `WDA_EXCLUDEFROMCAPTURE` **or** `WDA_NONE (0x00)` from `App.GhostDisabled` — applying `NONE` *actively clears* an exclusion already on the window, which the old one-way "return early if disabled" guard could not do (it only ever avoided *setting* the flag). `App.SetShowInRecordings(bool)` flips the static flag and calls `PillWindow.RefreshGhost()`, so a toggle takes effect immediately, no restart.
- **Ghost is the pill's feature alone — removed from the Library (owner decision).** The separate resizable `LibraryWindow` had quietly copied the pill's capture-exclusion "for consistency," applied once at window creation and never re-checked against the flag — so it ignored the new toggle and stayed invisible in recordings even with ghost off. The owner never requested it; it was scope-creep. Removed entirely (`SetWindowDisplayAffinity` call + its now-dead P/Invoke deleted): the Library is an ordinary window, always visible to capture. The trade-off is explicit and owner-accepted — a Library opened *during* a screen-share shows its meeting list to viewers; the pill's privacy default is deliberately **not** extended to it. (Lesson in memory: never generalize a behavior to another component "for consistency" without approval.)
- **The unsolved half (parked).** Ghost-off makes the notch *capturable*, but Snipping Tool still pins its recording control bar to the same top-centre strip the notch occupies, so they collide regardless of monitor count — its controls stay on the recorded screen. A recorder with detached controls (e.g. OBS on a second monitor, capturing the notch's screen) is the clean path; not yet built.

### Live light/dark theming — replace the brush, don't mutate it (M10)

Every themeable colour is a brush in `Theme.xaml`, referenced everywhere via **`DynamicResource`**. `ThemeManager.Apply` swaps the theme by **replacing** each brush resource (`app.Resources[key] = new SolidColorBrush(...)`); DynamicResource consumers re-resolve and repaint live, no rebuild. `ThemeManager.Palette` (key, darkHex, lightHex) is the single source of truth. Two approaches that FAILED — documented so they aren't retried: (1) `<Color>` resources referenced by DynamicResource, poked at runtime — only *some* consumers updated; (2) one mutable brush per key, setting `.Color` on toggle — **WPF freezes brushes after first render**, so the second toggle throws "read-only state" (the icon flipped but nothing repainted). A `--themetest` self-test asserts an already-rendered element recolours on swap. Code-built controls that cache a brush (Library cards, note content, dropdowns) recolour via a `ThemeChanged` event (rebuild) or `SetResourceReference`. Default dark; light is a warm Apple off-white, not stark `#FFFFFF`.

### The notch is a tinted mirror (M10)

The notch surface is a **semi-transparent** brush (~85 % black / ~88 % white) on the `AllowsTransparency` window, so the desktop colours behind it bleed through faintly — a see-through slab, **not** an acrylic blur. Acrylic was rejected: it fights the notch's top-flush / bottom-rounded shape and leaves square corners (the reason M7.2 went solid black). Kept fairly opaque so text stays readable and it never washes out to grey.

### Templated notes — flexible sections (M10)

Notes moved from a fixed Summary / Key-points / Actions shape to **template-driven sections**. `NoteTemplates` defines 8 shapes; `LlmNoteWriter` classifies the meeting (a short LLM pass), then writes that template's sections. `NoteSet.Sections` (heading + bullets) is stored as JSON on the meetings row (`sections_json` + `template` columns, idempotent migration); `NoteSet.EffectiveSections()` unifies new notes with the legacy decisions/questions tables so old meetings still render. Action items stay special (checkboxes + name-tags, where the owner is only set from a name actually spoken). Renderer + Markdown mirror render sections generically. Manual template override is the next step.

### Composited notch open/close (M10)

Open/close previously animated the transparent layered window's `Width`/`Height` every frame — a full relayout **plus** a Win32 re-center per frame (janky). Now the window is sized to its final bounds **once**, and the notch unfurls/retracts via an animated `RectangleGeometry` clip on the root Border (render-only, no per-frame layout or move). The pill→Library transition grows the window content from the pill's screen rect and retracts back toward it; scrollers use a composited animated-offset behaviour (`Controls/SmoothScroll`).

### Library becomes the hub (P1)

The Library window was restructured into a **sidebar + main-pane hub** with three pages — **Ask / Library / Templates** — switched by `ShowPage` (visibility + a fade). The existing list+detail became the *Library* page (`Cards`/`Detail`/`SearchBox` names preserved, so `LoadList`/`Select`/`RenderMeeting` are unchanged). Nav buttons, suggestion chips, template cards, and the per-note picker are code-built with `SetResourceReference` and rebuilt on `ThemeChanged`, so they recolour on a theme swap. The **greeting** types in via a `DispatcherTimer` (skipped under `App.Headless`). **Drag-reorder is opt-in** (`_dragEnabled`, default off): `OnCardMove` early-returns when off, so a plain click still selects a card. The **per-note template override** calls `App.RewriteNotesAsync`, which runs the LLM inside **`Task.Run`** — the local model loads synchronously, so it must be off the UI thread; `LlmService.CanWrite` checks availability WITHOUT loading. The hub renders to PNGs via **`--libshot`** (the Library is an ordinary window, not part of the pill render harness). Phase 1 = the shell; the Ask query currently routes into Library search, with retrieval-over-notes answering + voice as Phase 2.
