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

Deferred: chunked map-reduce for very long transcripts (current single-context handles typical meetings within 4096 tokens). (The "Built with" attribution screen shipped in M8.0 — see below.)

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

The Library window was restructured into a **sidebar + main-pane hub** with three pages — **Ask / Library / Templates** — switched by `ShowPage` (visibility + a fade). The existing list+detail became the *Library* page (`Cards`/`Detail`/`SearchBox` names preserved, so `LoadList`/`Select`/`RenderMeeting` are unchanged). Nav buttons, suggestion chips, template cards, and the per-note picker are code-built with `SetResourceReference` and rebuilt on `ThemeChanged`, so they recolour on a theme swap. The **greeting** types in via a `DispatcherTimer` (skipped under `App.Headless`). **Drag-reorder is opt-in** (`_dragEnabled`, default off): `OnCardMove` early-returns when off, so a plain click still selects a card. The **per-note template override** calls `App.RewriteNotesAsync`, which runs the LLM inside **`Task.Run`** — the local model loads synchronously, so it must be off the UI thread; `LlmService.CanWrite` checks availability WITHOUT loading. The hub renders to PNGs via **`--libshot`** (the Library is an ordinary window, not part of the pill render harness). Phase 1 = the shell; the Ask query currently routes into Library search, with retrieval-over-notes answering + voice as Phase 2. *(Superseded in v1-iter3: the Ask landing is now the Luma-style hero and the **suggestion chips were removed** — `BuildSuggestions()` collapses the panel. See "v1-iter3 decisions".)*

### Ask chatbot — streaming + FTS retrieval (P2)

`ILlm` gained **`StreamAsync`** (`IAsyncEnumerable<string>`); `LlamaLlm` yields from `StatelessExecutor.InferAsync` (holding the generation gate across the stream, stripping template markers) and `CompleteAsync` is now just an accumulation of it; `ApiLlm` streams OpenAI-style SSE (`stream:true`, `ResponseHeadersRead`, parse `data:` → `choices[0].delta.content`). Retrieval uses a **standalone FTS5 table** `meeting_fts(title, body)` (bundled in `e_sqlite3`), where `body` = summary + sections_json + manual body + transcript + action/decision/question text; kept in sync by `ReindexFts(id)` inside Save/ReplaceNotes/UpdateTitleAndBody and a delete hook, with a count-mismatch self-heal (`RebuildAllFts`) on startup — all wrapped so a missing FTS5 downgrades to the keyword LIKE fallback, never fatal. `SearchRelevant` ranks by `bm25(meeting_fts, 5.0, 1.0)` (title-weighted). `NotesQa` adds a **scope-aware task-query router** (strips the task-phrasing words to see if the user named a specific meeting: if so it lists *that* meeting's items via `SearchRelevant` top-1, otherwise the global open-tasks list — answered from `action_items` directly, never the model), a **context-first prompt** (notes → question last, today's date, 1–4 sentence constraint, ~3500-char cap), and `AskStreamAsync(onSources, onToken)` — sources shown up-front, tokens streamed into the bubble. The Ask UI streams into a live bubble (loader until first token), persists conversations to `chats.json` via `ChatStore`, and has **New chat** + a themed **History** popup (auto-titled, deletable). Verified: `--asktest`, `--llmtest`, and FTS unit tests.

### Ask — understand-first pipeline + conversation memory (P2.1)

The retrieval/routing failures (summarising the wrong "most recent" meeting, "meeting on July 30" not found, a title's "follow up" triggering the task list) shared one root cause: **deterministic questions were being answered by a probabilistic pipeline.** Fix = a deterministic "understand the query first" layer in front of retrieval (`QueryUnderstanding`, ~1ms, offline, no model):

- **Temporal parse** — `Microsoft.Recognizers.Text.DateTime` resolves absolute/relative dates ("July 30", "last week", "in March") against `now`, wrapped with a **past-preference** policy (meetings are history) + a rank-phrase regex for "most recent / latest / oldest" (these are *ranks*, not dates → `ORDER BY started_at`, which no date parser produces). The matched span is stripped, leaving the topical residual.
- **Intent** — rules over the **query text only** (never note titles/content — that scoping bug is why "…follow up" mis-fired): `SummarizeOne · LookupFact · ListTasks · ListMeetings · Count`. `ListMeetings` requires the *plural* "meetings" (singular "the most recent meeting about…" is a one-meeting summary).
- **Deterministic resolution** — `NotesQa` answers Count / ListMeetings / ListTasks straight from SQL (`MeetingStore.LatestMeetingIds` / `MeetingIdsInRange` / `OpenActions`), instant and hallucination-free; task answers scope by **topic and/or date**. SummarizeOne / LookupFact resolve the target meeting(s) deterministically *first*, then stream from the LLM with the date stamped in — so recency can't be mis-picked (the model is told the meeting, it doesn't choose it).
- **Conversation memory** — `ConversationContext` (last intent, resolved meeting IDs, date range) is held per Ask thread in the UI and passed to `AskStreamAsync`; `QueryUnderstanding.ResolveFollowUp` turns "before that / after that" into a range shift, "what about the action items / summarize it" into an aspect switch pinned to the prior meeting(s), and bare "this week" into the prior intent with a new window. Verified: `--querytest`, `--followuptest`, `--asktest` (prints plan + resolved meetings), 14 xUnit tests.

### Why on-device embeddings (Stage C) are deferred — a native backend conflict

Adding vector/meaning-search was planned as the next quality layer, but **both native routes conflict with an engine Toki already ships**, so it was NOT forced in (it would risk the core):

1. **ONNX Runtime route.** `Microsoft.ML.OnnxRuntime` publishes `runtimes/win-x64/native/onnxruntime.dll` — the *same relative path* sherpa-onnx (transcription) already ships. sherpa's native is v1.27; the MS package's is ≈v1.20. One `onnxruntime.dll` wins in the single-file bundle, and a managed wrapper calling the other version's ABI can crash. sherpa exposes no generic `OrtSession`, so its runtime can't be reused for arbitrary models.
2. **LLamaSharp route** (small BERT embedder as GGUF via `LLamaEmbedder`, no new runtime). Blocked differently: LLamaSharp/llama.cpp bind **one native backend per process**. Qwen needs the **Vulkan** backend for GPU offload; the embedding model **hangs in llama.cpp's `encode()`** under Vulkan (embedding models `encode` rather than `decode`; even `GpuLayerCount=0` still allocates Vulkan compute buffers and stalls — a test process hung >300s at "init: embeddings required… overriding"). A CPU-only embedder can't coexist with a Vulkan Qwen in the same process.

**Resolution (next iteration):** run the embedder **out-of-process** — a short-lived `Toki.exe --embed` helper started with a CPU-only `NativeLibraryConfig`, piping vectors back — giving clean backend isolation with no effect on Qwen's Vulkan path; alternatively unify a single onnxruntime version verified against both sherpa and Microsoft.ML.OnnxRuntime. The scaffolding (`EmbeddingService`, `ModelManager.Embed*`, `--embedtest`, bge-small-en-v1.5 Q8 GGUF) is committed but **inert** — nothing in the running app calls it and onboarding doesn't fetch the model, so there is no hang risk. Active retrieval stays FTS5/BM25 (strong for one user's own vocabulary; the reported bugs were temporal/intent, now fixed by the understand-first layer).

### Selectable note + layout (P1.1)

The open note is rendered as **one read-only `RichTextBox`** (`NoteRenderer.MeetingDocument`) hosting a single `FlowDocument`, not a stack of per-element read-only `TextBox`es. Only that shape lets a drag-selection span every block, so the whole note (summary → sections → to-dos → transcript) selects and copies together; `--copytest` proves it by selecting `ContentStart..ContentEnd` and asserting each part is captured (a mouse drag isn't screenshot-verifiable). `NoteRenderer.FullText` is the one-click "copy everything" text. The Win32 context menu is suppressed (`ContextMenuOpening` handled) — copy is our own themed split-button + popup (popup rows are real `Button`s; a `Border`+`MouseLeftButtonUp` got swallowed by the `StaysOpen=false` popup, killing the menu items). Detail layout is **left-aligned, full-width** (no centered `MaxWidth` column — that caused a large left gap when maximised and pushed the scrollbar inward); the body's right margin is 0 so its scrollbar is flush to the window edge, with internal `Padding` keeping text off it. **Segoe UI Variable** labels sit ~1px low, corrected in the `PillButton` template (`BlockLineHeight` clamp + a −1px `TranslateTransform`), measured against a true-center line drawn by the `--libshot` `buttons.png` probe. Panels use a themed `PaneSplitter` `GridSplitter` (thin seam, pink on hover); the note list also **collapses** on a second `Library` nav click via a timer-lerped column width (`AnimateCol`, since `GridLength` has no built-in animation).

### Notch keeps its dropdowns; one scroll from the top (P3)

A WPF `Popup` is its own top-level window, so a model/provider list that drops *below* the notch is geometrically "outside" it — the click-outside dismiss and the idle-tuck both fired and retracted the panel mid-selection. `NotchPopups` is a tiny registry: `SearchableDropdown` registers its popup on open/unregisters on close, and `PillWindow.PointInWindow` / `CursorOverPill` treat an open popup's on-screen rectangle (and simply "any open") as part of the notch. The same control also fixes the classic toggle-reopen (a `StaysOpen=false` popup closes on mouse-down, then the pill's mouse-up would re-open it) by ignoring a click within ~250 ms of the popup closing. The hover-open flash was a single un-clipped frame between `Show()` and the reveal animation — the zero-height clip is now set *before* `Show()`.

The open note is **one scroll that fills the pane**: the action toolbar (Copy/Delete/Template), the title, the date, and the body all live inside the single `RichTextBox` (`MeetingDocument(topBar:)` inserts the toolbar as the first `BlockUIContainer`). So the scrollbar runs the full height from the very top like a normal document — no pinned header above a shorter scroll region. The `DetailHeader` control was removed entirely; the RichTextBox carries its own left/top/right padding so the scrollbar sits flush at the pane edge.

**Design rules (applied, and saved as a memory).** Dividers not boxes: one seam between panes, never a doubled line or a full outline; the pane splitter shows no always-on line and only a subtle grey (not bright pink) on hover — the panels' own background difference is the boundary. Selection is a fill, not an outline (list rows are transparent; only the selected row fills). Motion is brief: the connection state is a single quiet line (green when verified) with no perpetual pulsing dot. Colour is for content: Delete is red *text* on the neutral button; a red fill is reserved for the confirm dialog. Everything that reads as text is selectable (title/date included).

### Ask by voice — reuse the meeting ASR (P4)

The Ask-bar mic records the default input with the existing `MicCapture` (WASAPI) to a temp WAV, then `TranscriptionService.TranscribeFile` decodes it to text through the SAME gated Parakeet recognizer the meetings use — so there's no second speech engine, no new download, and the recognizer is already warm from launch. It runs off the UI thread and returns null (model absent / decode error), "" (silence), or the joined text. The UI is Google-/Safari-like: click to record (mic turns coral, the box shows a pulsing "Listening…"), and it **auto-stops on end-of-speech** — a `DispatcherTimer` polls the level the passive `MicCapture.OnBuffer` tap records (a running `_lastSpeechTick` / `_heardSpeech`, cross-thread via atomic x64 long reads) and stops after a generous ~1.8 s silence once speech was heard (with a no-speech give-up and a hard length cap); you can still tap to stop. Then a pink "Transcribing…" loader shows while `TranscribeFile` runs off-thread, and the result fills the Ask box (append if non-empty) and focuses it, leaving the send to the user so an imperfect transcription never auto-fires. This is deliberately **user-initiated and mic-only** — it does not tap system audio and is not always-listening, staying within the meetings-only constraint; the always-on "Hey Toki" phrase is the separately-scoped, owner-approved carve-out. `--voicetest <wav>` exercises `TranscribeFile` directly.

### Mascot & the press-to-talk hotkey (P5)

`Controls/TokiBlob` was redrawn to the product-design look: the body is a vertical `LinearGradientBrush` (glossy light crown → deep pink base) with a soft `DropShadowEffect` and a radial top-gloss, so it reads dimensional instead of the old flat fill — and since every view embeds the same control, the redesign lands everywhere at once. Its animation vocabulary lives entirely inside the control over three composable transforms — `Breathe` (squash/stretch), `Hop` (bounce), `Tilt` (wobble/spin), plus per-eye blink scales — so `React()` can cycle distinct one-off reactions (squish/hop/wobble/wink/spin/big-eyes/nod) and `DropIn()` can play a springy hello without any view wiring; idle breathing/blink self-runs and pauses when off-screen.

Activation is **press-to-talk only** (owner decision, over a wake word): `HotkeyManager` fires `App.OpenVoiceAsk` → `LibraryWindow.ShowAndListen()`, which opens Ask and toggles listening through the same on-device voice path as the Ask-bar mic. There is **no always-on mic and no keyword spotter** — Toki listens only when the user presses the key (or taps the mic), so the meetings-only constraint is preserved without a carve-out. (sherpa-onnx keyword spotting was the researched wake-word route; it was dropped when the owner chose press-to-talk.)

### Push-to-talk on the notch (P6)

`Ctrl+Alt+Space` is hold-to-talk. `RegisterHotKey` only reports the press, so `Interop/PushToTalkHook` installs a `WH_KEYBOARD_LL` hook on its own thread (like the mouse `InputMonitor`): on Space-DOWN with Ctrl+Alt held it fires `onStart`; when any chord key releases it fires `onStop`, de-duping auto-repeat with an `_active` latch and never swallowing the keys. `App.PttStart` drops the notch into `PillState.Ask` and records the mic (`MicCapture` → temp WAV) **only while held**; `PttStop` stops it, `TranscriptionService.TranscribeFile`s the clip, then streams `NotesQa.AskStreamAsync` into the notch. The `Views/AskView` notch has three phases (listening waveform → thinking loader → streamed answer, selectable); it's AutoHeight and calls `App.GrowNotch()` → `PillWindow.GrowToContent()` to re-fit as content changes, and self-dismisses on Esc / × / a 30 s timeout. This is deliberately push-to-talk only — the mic is engaged solely while the key is held, so there is no always-on listening and no keyword spotter, keeping the meetings-only constraint intact (Wispr Flow works the same way). Full conversational Q&A rendered on the notch is a later refinement.

### Toki-as-assistant: command router, concise notch, screen-awareness (P7)

Three deterministic layers now sit in front of the Q&A model, grounded in research into shipping assistants:

- **Tier 0 — `Llm/CommandRouter`.** Navigation/action commands ("take me to the library", "new note") match an *anchored, whole-utterance* regex against a closed set, with a **question-shape veto checked first** (interrogative lead word or trailing `?` → never a command). Question always wins a tie — a missed command just falls through to Q&A; a false positive (a window popping open mid-question) is trust-destroying. Runs in both the notch (`App.ExecuteNotchCommand`) and the Library Ask box. `--`/xUnit `CommandRouterTests` cover 35 adversarial cases.
- **Concise notch answers.** `NotesQa.AskStreamAsync(concise: true)` swaps in a numbered-checklist system prompt (`SystemConcise`) tuned for a small Qwen to answer in 1–3 sentences, and caps the token budget. Length is enforced in code, never trusted to the model: `NotesQa.NotchTrim` cuts on a sentence boundary at ~280 chars and, when the model emits `[MORE]` or overflows, flags it so the notch shows a one-line teaser + a "See full answer in Library" hand-off (`LibraryWindow.AskInLibrary` re-asks it un-capped).
- **Screen-awareness (Tier-3, our own state).** `App.CurrentViewedMeeting` tracks the meeting note open in the Library. `AskStreamAsync(screenMeetingId:)` pins the plan to that meeting when the question is deictic ("this", "here") or underspecified ("what were the action items?") — but only when the user named no topic/date/recency (those always win), and only for Summarize/Lookup/ListTasks. Resolution is deterministic (in code), so "this" can never resolve to the wrong meeting; reuses the existing `QueryPlan.PinnedMeetingIds` injection point (`QueryPlan` became a `record` so `with` works). No screenshots or OCR — Toki reads only its own view-model, so nothing new leaves the machine.

The notch keeps a session `ConversationContext` (`App._notchCtx`) for follow-ups, reset when the notch reopens from closed.

### Assistant polish: patch streaming, capped generation, conversation thread (P7.1)

**Patch streaming.** Both the notch (`App.HandleNotchText`) and the Library Ask (`OnAsk`) no longer render each token; the `onToken` callback appends to a locked `StringBuilder` and a `DispatcherTimer` (~70 ms) flushes the accumulated text, so words appear in chunks. **Capped generation (no overshoot-cut).** In the notch, that callback also checks `NotchOverBudget` — a completed sentence past ~240 chars, or the model's `[MORE]` flag — and cancels the shared `CancellationTokenSource`, stopping generation at a clean boundary. So the notch never streams a long answer then visibly trims to a teaser; it simply stops where it should, and shows the "See full answer in Library" button when it stopped early. **Adjustable size.** `AskView` is AutoHeight; each flush calls `App.GrowNotch()` → `PillWindow.GrowToContent()` (150 ms) so the notch starts compact and grows to fit up to the thread's MaxHeight, then scrolls. **Conversation thread.** `AskView` is a scrollable `Thread` of user/Toki bubbles that persists the whole session (each push-to-talk appends a turn, streamed into the latest Toki bubble); `NewSession()` clears it when the notch reopens from closed. **Screen-aware trigger** was broadened: `NotesQa.OnlyGenericAsk` pins to the on-screen meeting when every content word is a generic ask-word ("what were the action items?"), not only on deixis. **Library window** `ShowLibrary` restores from minimized and `BringToFront()` (a Topmost flip) so it always surfaces; `AskInLibrary` opens a fresh conversation.

### Ask Toki redesign — persona tier, person slot, derived screen grounding, honest-first resolution (P8)

Five live-session failures (all screenshot-verified) were root-caused to routing/state bugs, not model weakness, and fixed deterministically — full design + research + live-test script in **`docs/ask-toki-redesign.md`**. In brief: (1) **topical-vs-list routing** — `ListMeetings` now requires the residual to be pure list-chrome (`OnlyListChrome`); any topic word ("Kai Sinat") makes it a `LookupFact` whose topic actually drives retrieval, and `NotesQa.ContentQuery` strips stop/generic words from every `SearchRelevant` call so the prefix-OR BM25 query can't be drowned by "meetings*"/"discussed*" (a true miss stays a miss). (2) **Screen grounding is derived, never stored** — `App.CurrentViewedMeeting` is now computed from `LibraryWindow.VisibleMeeting` (window visible ∧ not the off-screen pre-warm ∧ real meeting ∧ user-chosen, sticky across reloads); the old mutable static went stale (warm-up set it to an unseen note; hide cleared it and re-show never restored it) which is exactly what answered "this meeting" with the wrong meeting. The notch also grounds on the pill's Notes view (`_notchScreenId` captured at PttStart) and shows a dim **"About: \<title\>"** scope line (`AskView.Scope`) whenever an answer is pinned to the on-screen note — grounding must be visible to be trusted. (3) **Person/owner slot** — `MeetingStore.ActionOwners()` + `NotesQa.MatchPeople` resolve names against the closed vocabulary of owners that actually exist (first names + possessives handled, "You"/"me" guarded), filtering every task path (pinned/topic/global) with an honest miss that names the real owners; an unknown word can never trigger the filter. (4) **Persona tier** (`Llm/Persona.cs`, Tier 0.6) — anchored whole-utterance patterns for complaints ("why are you so rude?"), quality feedback, screen-awareness ("do you see my screen?" names the open note), capabilities, privacy, and AI-identity → warm canned replies, never retrieval. (5) **Voice** — sampler now matches the Qwen3-4B-Instruct-2507 model card exactly (T 0.7 / TopP 0.8 / TopK 20 / **MinP 0** explicit; presence 0.5 in the card's 0–2 band), `ILlm` gained optional **stop sequences** (LlamaLlm → AntiPrompts, ApiLlm → `stop` field; notch lookups stop at the first blank line instead of overrunning into `NotchTrim`), and both system prompts carry a **one-shot exemplar** in the target "sharp colleague" voice. LLM-route resolution now runs **before** the model-availability check, so honest "couldn't find it" answers need no model. Tests: `NotesQa.StoreOverride` seam + temp-file stores; every screenshot bug is a regression test (`NotesQaTests`, `PersonaTests`, extended `QueryUnderstandingTests`) — 228 green.

### Ask Toki reasoning route + self-reference + the full command set (P9)

P8 made the *deterministic* answers good; P9 makes the *thinking* answers possible — full design + research + live-test script in **`docs/ask-toki-reasoning.md`**. In brief: (1) **The `Analyze` intent** (`QueryIntent.Analyze`, `QueryUnderstanding.AnalyticalRx`) — analytical/synthesis questions ("what should I focus on?", "themes across my meetings", "how is X progressing?", "compare…", "biggest risks?") were all falling into the `LookupFact` pipe (top-3 retrieval, one-glance prompt, 85–170 tokens), which is structurally incapable of analysis. They now route to their own path: **breadth** (up to 6 topic-matched meetings via `NotesQa.RetrieveBroad` — analytical vocabulary like "themes/risks" is stripped from the retrieval terms since it describes the analysis, not note text; no anchor or no match → the recent 5, never a deterministic dead-end), a **reasoning prompt** (`BuildAnalyzePrompt`: conclusion first, 2–4 supporting points tied to meetings by name, partial-answer honesty — say what the notes don't cover), a **6,000-char context cap** and a **220/420-token budget**. Cue regex is phrase-level ("focus **on**", not bare "focus") and who/when/where-led questions stay factual — the Adaptive-RAG route-by-complexity pattern plus GraphRAG's local-vs-global finding, applied at Toki scale (no embeddings, still blocked). (2) **Self-reference** — "assigned to *me*" listed other people's items (screenshot bug): `NotesQa.SelfTaskRx` (tight phrases: "assigned to me", "on my plate", "mine"…) + `IsSelfOwner` resolve "me/my/I" to the notes' `Owner = "You"` values, with honest misses that name the real owners; multi-owner strings ("Lily, Mac") now split per person in `OwnerMatches`/`MatchPeople` (fixing "Mac's tasks" missing shared items); the LLM prompts state "'You' in the notes is the user". (3) **Nine new commands** with real handlers — `StartRecording` ("start taking notes" → the manual-record path, human-in-the-loop by definition; `PillState.Ask` also gained its missing transition-table entry, a latent `KeyNotFoundException`), `UseApiModel`/`UseLocalModel`/`UseHeuristicMode` (via `App.SwitchNotesMode` → normal settings path; refuses API honestly when no key is saved), `ThemeLight`/`ThemeDark` (`App.SwitchTheme`, live + persisted, idempotent), `GhostOn`/`GhostOff` (`App.SetGhostMode` → same live path as the Settings toggle), `OpenAsk` — and **"take me to the library" now lands on the notes LIST**: `ShowLibrary` gained a `page` parameter and `OpenHubPage` lands in one step (the old default always showed the Ask page). Question-veto discipline intact and negative-tested for every new command. Tests: new `NotesQa.LlmOverride` seam (fake `ILlm` records prompt/budget/stops) proves the analyze pipeline end-to-end without the real model — **300 green** (was 228).

### Ask Toki refinements — wake-name, identification, recall, the clipped notch (P9.1)

The next live run surfaced a cluster with one theme: Toki didn't recognise its own name (ASR renders "Toki" as *Doki/Toky/Docie/Toku/Tokyo*) or facts the DB already held, and then let the model improvise — full detail in **`docs/ask-toki-refinements.md`**. In brief: **`Llm/WakeName.cs`** (the single closed fuzzy alias list, used by CommandRouter/SmallTalk/Persona to strip a leading/trailing *address* — never rewriting the question itself); broader command vocabularies (articles, "api **key settings**" tails, word cap 6→8) and an **affection tier** in SmallTalk ("I love you, Toky" got a fabricated notes answer before); a deterministic **meeting-identification layer** (`NotesQa.IdentifyMeetingAnswer`: oldest/first/most-recent + title asks answered straight from SQLite; an ambiguous date that hits several meetings lists them all, never a silent pick); **title-match recall union** (`MeetingStore.TitleMatchIds` prepended at every retrieval site — the "Amazon" title miss) with content-term precision kept; anti-stretch honesty lines in both prompts; and **`Detection/AppNames.cs`** friendly app names + measure-before-reveal so the meeting prompt can't render clipped ("Com.tinyspeck.slackdesktop" → "Slack"). **389 green** (was 300).

### The dialogue brain — turn-type routing, user memory, general conversation (P10)

The owner's verdict after P9.1 was that Toki still "isn't smart — like first-gen ChatGPT", and the root cause was **architectural, not a bug**: the pipeline was retrieval-first for *every* utterance — chit-chat, identity ("what is my name" was FTS-searched and answered with three citations), user-stated facts ("call me Anirudh" → "no mention of Anirudh in the notes"), world-knowledge questions, and misheard commands ("switch to *like* theme" → the Analyze route's recent-5 fallback → a **fabricated** link to an unrelated meeting) were all force-fit into notes search. P10 rebuilds the orchestration into a **dialogue manager** (full design + research + live script in **`docs/ask-toki-brain.md`**): `NotesQa.AskStreamAsync` stays the single entry point but now routes typed turns — Command (UI tier, unchanged) → SmallTalk/Persona (unchanged) → **USER_FACT/IDENTITY** (`Llm/UserFacts.cs` + `Llm/UserProfile.cs`: "call me X" captured with a not-a-name guard and persisted through the existing `AppSettings.UserName` the greeting already uses, plus a capped `UserFacts` list; "what's my name / who am I / what do you know about me" answered from the profile, never a search) → **misheard-command clarification** (`CommandRouter.TryNearMiss`: command-shaped near-misses in the theme/ghost/model domains degrade to a targeted "did you mean…", never retrieval) → the deterministic SQL routes (unchanged) → **RAG-vs-parametric routing** (`Llm/TurnRouter.cs`: a turn with no meeting cue, no temporal, no pin, and no *evidence* in the store — a title/FTS probe over its content terms — is GENERAL) → either the P8/P9 grounded pipeline (one branch now, all wins intact) or **`Llm/GeneralChat.cs`**: the model converses and reasons from its own knowledge with the recent transcript (`ConversationContext.Turns`, appended on every answered path) and the profile in context — no retrieval, no citations, identical over both backends (local Qwen / BYO API). Two anti-fabrication rails: the **relevance floor** (an anchored Analyze that matches nothing returns empty → honest miss; the recent-5 fallback survives only for genuinely anchor-less corpus-level asks) and **no-citations-on-a-miss** (`NotesQa.LooksLikeMiss` on the answer's lead → sources dropped; the Library chip renderer retracts chips on an empty final list). Research: Adaptive-RAG/Self-RAG (route first, retrieve on demand), the RAG-robustness line (related-looking-but-unanswerable context misleads small models most), hybrid dialogue managers (chit-chat + task routing), MemGPT/ChatGPT-memory (a small durable user-fact store), VUI error recovery (clarify, don't act or guess). Test seams: `UserProfile.SettingsOverride` (in-memory settings — the real `settings.json` is never touched) + the existing `StoreOverride`/`LlmOverride`; `NotesQaTests` and the new `DialogueBrainTests` share an xUnit collection because the seams are static. **428 green** (was 389).

### Google Calendar foundation — OAuth (loopback + PKCE), raw-HTTP client, DPAPI tokens (M7.0)

The plumbing beneath the calendar features (notch preview, prep cards, meeting creator), built so the surfaces sit on one shared, verified base — full owner setup in **`docs/google-setup.md`**. Fable researched the current Google specifics; Opus built it; verified live against a real account. Decisions:

- **Installed-app OAuth, loopback + PKCE, no backend.** `Calendar/GoogleAuth.cs` runs the standard native-app flow: a CSPRNG `code_verifier`/`state`, an ephemeral-port `HttpListener` on `127.0.0.1` (probed via a throwaway `TcpListener` since `HttpListener` can't bind port 0), `Process.Start(UseShellExecute)` to the system browser, then the `authorization_code` → token exchange. Browser ↔ Google ↔ localhost only — **no Toki server touches the flow**, so hard constraint #1 ($0 upkeep) holds. The desktop `client_secret` is embedded (Google's own stance: "not treated as a secret" for installed apps; PKCE + exact loopback redirect + user consent are the real defenses) — a code comment forbids "fixing" it into a backend. Credentials resolve env → `%APPDATA%\Toki\google_client.json` (unwraps Google's `installed`/`web` shape) → embedded consts (empty until ship), so dev needs no code edit.
- **Least-privilege scopes, requested once.** `openid email https://www.googleapis.com/auth/calendar.events` — read **and** create (the meeting-creator needs write, and every Calendar-event scope is equally "sensitive" to Google, so a read-only variant now would only force a second consent later). No Gmail/Drive/contacts. `email`/`openid` are only to show "Connected as …" (the id_token's `email` claim, base64url-decoded locally, **not** signature-verified — it arrived over TLS straight from Google and is display-only).
- **Only the refresh token is persisted, DPAPI-encrypted.** `AppSettings.GoogleRefreshTokenProtected` reuses the existing `Protect`/`Unprotect` (per-user DPAPI) — the same pattern as the BYO API key. Access tokens are **never** written to disk: `CalendarService` holds one in memory with a 60 s early-refresh margin (clock-skew) and refreshes lazily behind a `SemaphoreSlim`. A refresh `invalid_grant` (revoked/expired) clears the stored grant, fires `ConnectionChanged`, and surfaces as "reconnect", never a crash mid-meeting. Tokens/codes are never logged (verified: `settings.json` carries only an opaque DPAPI blob; a `1//` plaintext grep returns zero).
- **Raw HTTP, no Google SDK.** `Calendar/CalendarClient.cs` hand-rolls `events.list` (timeMin/timeMax/singleEvents/orderBy — timestamps `Uri.EscapeDataString`'d, because a raw `+` in `+00:00` becomes a space and Google 400s — the one live bug found and fixed), `events.insert` (+`conferenceDataVersion=1` for an auto Meet link), and `events.delete`, all over the shared static `HttpClient` in the `ApiLlm` style (per-request Bearer, defensive `JsonDocument` parsing, short human errors). The official `Google.Apis.Calendar.v3` would drag several MB of assemblies + Newtonsoft into the single-file publish for three endpoints; rejected. `CalendarModels.cs` normalizes Google JSON into `CalEvent`/`CalAttendee` records (UTC times, all-day vs timed, Meet link from `hangoutLink` or the `video` entry point, cancelled instances dropped).
- **`CalendarService` is the single facade** every current/future surface calls (`Connect/Disconnect/Upcoming/Create/Delete` + `ConnectionChanged`); the seam the notch-preview / prep-card / meeting-creator work plugs into with no auth knowledge. Wired to the Home "Connect Google" button and a Settings "Google Calendar" connect/disconnect row.
- **Verification.** `--caltest connect|list|insert|disconnect` (head/tail output per house convention) — ran live: sign-in, read next 7 days, create+delete a throwaway event, token auto-refresh, encrypted-at-rest check. **446 green** (+18 calendar unit tests: PKCE against the RFC 7636 S256 vector, auth-URL construction, id_token decode, event-JSON parsing across timed/all-day/cancelled/conference shapes — all pure, no network). Deferred to M8: Google's free **sensitive-scope verification** (privacy-policy URL + demo video) to remove the "unverified app" consent warning before selling.

### Google Calendar UI — connected Home, agenda week view, NL create, edit, delete (M7.1)

The visible calendar, built entirely on the M7.0 `CalendarService` facade (Fable designed it against `Toki-ProductDesign/③ Home` + the prep-card language; Opus built it in two render-verified increments). Decisions:

- **Where it lives.** The connected-Home **"COMING UP"** glance is in the notch (per the reference), and the **week + full CRUD** is a new notch page (`PillState.Calendar`, 392×472, fixed height + internal scroll — NOT AutoHeight, same reasoning as Settings), NOT the Library hub: the entry point ("Meeting") is on Home, and Toki's rule is one element that morphs, not a second window. Home widened 320→344 for the Join pills. `Calendar` is `IsStayable` + `DismissOnClickOutside`, so it inherits the tuck/sliver/edge-restore lifecycle for free.
- **Agenda, not a grid.** A 344–392px column can't show a legible 7-column week; every premium compact calendar (Fantastical mini-window, Notion Calendar, Cron) shows an agenda-by-day list. So the week is day-grouped rows: `TODAY · TUE, JUL 8` (today in amber) with a live **Coral "now" line** inserted before today's first upcoming event, week nav (offset 0..3 — the backend is upcoming-only, so no fake past), Join always visible, and a hover-revealed **… menu** (Join · Edit · Open in Google · Delete) via `NotchMenu` (a Popup registered with `NotchPopups` so the notch doesn't tuck under it).
- **Create = one box, NL-first.** `Calendar/QuickEventParser` wraps `Microsoft.Recognizers` (already shipped) but **future-preferred** (a meeting is ahead — the opposite of `QueryUnderstanding`'s past-preference), pulls emails out by regex, and the remainder is the title. A live green preview line ("Tomorrow · 2:00 – 2:30 PM") builds trust as you type (the Fantastical-vs-Vimcal lesson: NL only works with a visible parse). Three chips (duration cycle / Google Meet default-on, sticky / people) are the entire "form"; no date-picker dialog. Double-submit guarded (button disabled before the first await); Esc/Enter handled in the TextBox so they never bubble to the notch.
- **Edit** reuses the composer prefilled (Save mode), gated by `CanEdit` (not a guest of someone else's event), backed by a new `events.patch` (`CalendarClient.PatchAsync` / `CalendarService.UpdateAsync`, `sendUpdates=all` so guests are notified; a Meet link is only added if absent). When no new time is typed the original time is preserved (the edit fallback in `ReparseComposer`).
- **Delete, research-split.** Solo event → delete immediately + a 6s **Undo** bar (re-creates via `CreateAsync` — a new id is fine for a solo event). Guested event → one inline **confirm** ("Guests will be notified"), no undo (re-inviting everyone would be dishonest to call reversible). Optimistic: the row leaves the cache immediately and reinserts on failure.
- **One data path.** `Calendar/UpcomingCache` is a stale-while-revalidate store (31-day horizon, 60s TTL, single in-flight request, `Snapshot` for instant paint, `Upsert`/`Remove` for optimistic mutations, `Invalidate` on connect/disconnect) shared by Home and Calendar so they never double-fetch and always agree. `Views/CalendarRows` builds every row (date badge, glance row, agenda row, skeleton, formatters) from Theme.xaml tokens by key, so both surfaces and a live theme swap stay consistent.
- **Provider-neutral.** Views touch only `CalendarService` + `CalEvent`/`CalAttendee`/`NewEvent`; the one place "Google" is named for real is `CalendarService.ProviderName`. Outlook later is a backend router + a string, not a UI rewrite.
- **Render-harness contract.** `Dev/CalendarSamples` seeds events (relative to now, so "in N min" + the now-line render) served by `UpcomingCache` under `App.Headless` — the views never hit the network offscreen. The entrance stagger is skipped under Headless (no clock advances, or rows render at opacity 0). Shots: `Home` (connected), `Calendar`, `Calendar-composer` (opened via an internal debug hook). Self-tests `--caltest update` (insert→patch→verify→delete) and `--caltest parse` proved edit + NL parsing live. **450 green** (+4 parser tests).

### Pre-meeting prep cards — deterministic content, restrained auto-surface (M7.2)

Fuses the calendar (M7.0/M7.1) with the notes store to prep the user before a meeting. Fable designed it against `Toki-ProductDesign/④`; Opus built it. Decisions:

- **Deterministic content, no LLM.** `Calendar/PrepBuilder.Build(CalEvent, MeetingStore)` is pure store-reads + string parsing → instant, and (deliberately) costs nothing on a low-RAM machine — it never loads the model. `PrepContent` = { Event, LastTime (MeetingRecord?), StillOpen (ActionItem[]), Agenda (string[]) }. An LLM recap was considered and rejected for the hot path (would defeat the load-on-demand memory goal); it's a possible deferred enhancement only.
- **Honest title-match.** Notes store only YOU/THEM, not attendee emails, so a calendar event matches a prior meeting by **title/topic**: term extraction reuses `NotesQa.ContentQuery` (one stopword source of truth) → `MeetingStore.TitleMatchIds` (confident) then `SearchRelevant` (FTS, gated by a shared-title-term bar). A candidate must be a *prior* `Meeting` with a non-empty recap (`Tldr`, else first key point). No confident match → `LastTime = null` and the view shows the **first-time** layout — never a fabricated "last time" (a wrong recap is worse than none).
- **The trigger is a restrained watcher, not always-on.** `Calendar/PrepWatcher` (singleton, `DispatcherTimer` 30 s at `Input` priority so model warm-up can't starve it — a real past bug) raises `PillState.PrepCard` for an event starting within `LeadMinutes` (5, `TOKI_PREP_LEAD` override). Restraint budget: real meetings only (`!IsAllDay && (HasMeetLink || other guests)`), **once per event-instance** (`_shown` set; `singleEvents=true` gives recurring meetings distinct ids), only while idle (`CanPrompt` = state Hidden/Collapsed — which alone excludes recording/summarizing/other prompts/open pages), never during fullscreen/presentation (`SHQueryUserNotificationState`), "Later" → permanent per-session `Snooze`, and an auto-retire that tucks a card the user ignored past start+2 min. Reads the network only via `UpcomingCache` (coalesced 60 s TTL — same cost as Home sitting open); `Changed` is marshaled to the dispatcher. Never starts under `App.Headless`.
- **Human-in-the-loop preserved (Hard Constraint #3).** The card *asks*; **"Join and record"** (`App.JoinAndRecord` — opens the Meet link + the same manual-start path as `StartQuickNote`, → `RecordingBar`) is the human choice; **"Later"** declines. Nothing records without the click. The auto-surface is a *visual, calendar-driven* nudge (zero audio), so it's compatible with the owner's push-to-talk-only mic stance — flagged for owner confirmation (auto vs hover).
- **State/shell.** `PillState.PrepCard` is AutoHeight (356×306 fallback; the first-time card measures short), NOT stayable and NOT dismiss-on-click-outside (a one-shot prompt with its own lifecycle, like MeetingPrompt — a stray click must not vanish the prep you're reading). `AppState`: Hidden/Collapsed → PrepCard; PrepCard → RecordingBar/Hidden/Calendar. `PillWindow.OnStateChanged` calls `PrepCardView.Refresh()` **before** the height measure (the clipped-notch lesson). **The ctor-grow recursion guard (from the Home crash fix) is applied verbatim** — `_ready`/`Grow()` — plus the systemic `_growing` re-entrancy guard now in `GrowToContent`.
- **Verification.** `Dev/PrepSamples` (Matched + FirstTime) feed render shots `PrepCard` / `PrepCard-firsttime` (checked light + dark, near-exact to ④). `--preptest` (temp store) proves title-match, open-item pull, unrelated-event rejection, and HTML/bullet agenda parsing. `--state PrepCard` demos the live card. The real T-5 auto-surface + "Join and record" recording need the owner at a live meeting. **450 green.**

### Voice-create a meeting — a confirm-gated command tier in the dialogue brain (M7.3)

"Schedule a call with Ana tomorrow at 3" on push-to-talk → a new deterministic command that CONFIRMS before booking. Fable designed the routing against the P8–P10 brain; Opus built it. Decisions:

- **Two tiers added to `NotesQa.AskStreamAsync`, no others touched.** **Tier 0.4 (pending-create resolution)** runs FIRST, before small-talk — if `ctx.PendingCreate` is set, this turn belongs to the confirmation, and the pending is cleared up front (single-shot). It runs before SmallTalk because "yes/okay/sure" are exactly what SmallTalk would otherwise swallow. **Tier 0.75 (create detection)** runs after the identity/user-fact tiers and before `CommandRouter.TryNearMiss` + all retrieval — a create must own its scheduling verbs and must never be "answered" from the notes. Both reply through the existing `Canned(...)` path, so recording/streaming behave identically on the notch and the Library.
- **Deterministic detection (`Llm/CalendarCommand.cs`), no LLM.** Fluff-strip + the **question-shape veto** are shared with `CommandRouter` (its `Fluff`/`TrailFluff`/`QuestionLead` promoted to `internal`) so voice-create and UI commands use one ASR source of truth. Create shape = a scheduling verb + article + event noun (`schedule/book/set up/create/… a meeting/call/sync/…`, anchored so "create a *summary* of the meeting" can't fire), OR "put/add … on/to my calendar", OR "remind me to …". Vetoes: any `\bnotes?\b` (that's `NewNote`), a question lead, >16 words. Time/title come from the already-built `QuickEventParser`; the person after "with" is captured for the confirm copy only.
- **Confirm before create is structural, not incidental.** `TryDetect` never calls `CreateAsync`. The only creation call site is tier 0.4's yes-branch, reachable only via a live `ctx.PendingCreate` (`QueryUnderstanding.ConversationContext.PendingCreate`), which is cleared before any branch — so a stray later "yes" can't create, a create never happens without confirmation, and there's no double-create (pending cleared pre-await; both surfaces serialize turns). The confirm always states the **resolved absolute** day/time + exact title, so a mishearing costs one "no", never a wrong event.
- **Confirm is a spoken yes/no follow-up, not buttons** — `AskView` renders read-only text rows and the Library ask has a different renderer, so a button row would mean two implementations + ctor-grow/measure risk; a `ConversationContext` field makes the confirm a first-class follow-up on both surfaces with **zero view changes**. `IsYes`/`IsNo` are tight anchored whole-utterance matchers.
- **Honest attendees + Meet off.** A spoken "with Ana" is a name; `QuickEventParser` only extracts emails by regex, so voice creates carry no attendees — the person stays in the title + a description note, never a fabricated invite. `AddMeet:false` for voice (a Meet link on an event no one else is invited to is decorative). `ExecuteAsync` is honest on not-connected / `invalid_grant` reconnect / failure, and optimistically `Upsert`s the created event into `UpcomingCache` so Home + Calendar refresh.
- **Shared-parser fix.** `QuickEventParser.PickFuture` now prefers a **business-hours reading (07:00–20:00)** among ambiguous future readings, so a bare "at 3" is 3 PM not 3 AM — benefiting the typed composer too; explicit meridiems ("9am"/"8pm") are untouched.
- **Verification.** `--voicecreatetest` (detection matrix + confirm gate, no mic/model/network) and `CalendarCommandTests` (`NotesQaSeams` collection; `StoreOverride`/`LlmOverride`/`CalendarCommand.CreateOverride` fakes): detection positives/negatives, title/time/person extraction, turn-1-never-creates, yes-creates-once, no-cancels, **stray-yes-never-creates**, no-time-asks-when, not-connected-honest. **472 green** (+22). Live speak→confirm→create needs the owner + mic + connected account.

### Premium redesign pass 1 + the glassy-notch blur (M9.0)

Fable did a deep design audit (Mobbin + Wispr/Linear/Vercel/Cron references); Opus built pass 1 in render-verified increments. Token system: a restrained type ramp in `Theme.xaml` (`Title/BodyStrong/Secondary/Caption/Micro` — replacing 18 scattered sizes), `SectionHeader` SemiBold (not Bold), `TokiCheck` redesigned (18px box, a fixed 12×12 tick — the old `Stretch=Uniform` distorted it — **green** fill + spring-pop), a new `TokiSwitch` (Settings booleans), a `ChipButton` (outlined→green-fill toggle). Screen work: `NoteRenderer` colour-coded headers (green/blue/purple by meaning) + left-align (justify→left); the Calendar composer flattened to a hairline-topped section with chip toggles; Home "Meeting"→"Calendar" + one-accent icons. Bug fixes: `PillWindow` idle-tuck skips a page with focused text input (typing no longer closes the composer); `QuickEventParser.PickFuture` prefers a business-hours reading (bare "at 3" = 3 PM).

**The glassy-notch blur — the hard part, fully solved.** True OS blur fights the notch's constraints (transparent/`AllowsTransparency` window, square-top/22px-rounded-bottom, ghost mode). The path that works:
- **Real acrylic** via undocumented `SetWindowCompositionAttribute` + `ACCENT_ENABLE_ACRYLICBLURBEHIND` (blurs the desktop behind; DWM `SystemBackdrop`/Mica don't render on a layered WPF window). Tint is `GradientColor` (AABBGGRR), **theme-aware** — dark frost in dark, white frost in light — re-applied on `ThemeManager.ThemeChanged` (it's a P/Invoke, not a DynamicResource, so it doesn't recolour on its own).
- **Rounded corners**: a window REGION (`SetWindowRgn`) does NOT clip the acrylic backdrop (DWM paints it across the full rect) — so corners are rounded by DWM itself (`DWMWA_WINDOW_CORNER_PREFERENCE = ROUND`, ~8px, which DOES round the backdrop). DWM's auto-drawn window border is killed (`DWMWA_BORDER_COLOR = NONE`) and the WPF rim is dropped in glass mode, so the frost is seamless.
- **One size**: DWM can only round at ~8px, not 22px — so BOTH modes use an 8px corner radius; toggling glass never changes the notch's size/shape.
- **Flush square top**: DWM rounds ALL corners (can't do bottom-only), which curved the top that must hang flush from the screen edge. Fix — in glass mode the window extends `GlassTopBleed` (8px) ABOVE the screen top (`_topBleedPx` offsets the placement `y`; window height + content top-margin grow by the bleed), so the rounded top corners fall off-screen, leaving a straight flush top with the rounded bottom. Applied on open (`OnStateChanged`) and on a live toggle (`RefreshSurface`).
- Wiring: `AppSettings.GlassyNotch` (default false), `ThemeManager.Palette` `NotchGlass`, `App.SetGlassyNotch` → `PillWindow.RefreshSurface`, a `TokiSwitch` row in Settings → Appearance. Ghost mode + the composited clip-reveal are untouched. 472 green.

### Calendar page revamp — one continuous agenda (M9.1)

The page had three arrows (back + two week-nav) and paged a week at a time, yet `UpcomingCache` already holds a ~31-day horizon — so the paging was artificial state. Killing it removed two arrows and two rebuild triggers. `RenderWeek` → `RenderAgenda`: one scroll, grouped by week (`WeekStartOf`, Sunday-start) into **bands** ("This week"/"Next week"/date-range) then by day. The header subtitle is **scroll-synced** (`OnScroll` → `UpdateSubtitleFromScroll` finds the top-most band via `TransformToAncestor`) and a **Today pill** fades in past a scroll threshold. Smoothness came from a **render-key** (`id:start:summary:hasMeet` per event + today's date **+ the theme**) that early-returns when nothing changed — the 60s now-tick and multi-source refresh no longer re-stagger identical rows — plus a **stagger-once** guard (`_entranceDone`). **Two bugs the revamp first introduced, then fixed:** (1) auto-height fought the `ScrollViewer` (a page that grows to fit its content never scrolls) → reverted to a **fixed 360×430** with an internal scroller, the Settings/Library pattern; (2) the render-key dedup skipped the rebuild on a theme swap, so the code-built rows kept the old palette (black-on-dark) → the theme is now **in the key** and the view subscribes to `ThemeManager.ThemeChanged` to invalidate + re-render.

### Notch input — scroll/clicks land on the notch, not the window behind (M9.2)

Symptom: with the cursor over an open page (Settings/Calendar/About), the scroll wheel scrolled the app *behind* the notch, and buttons felt like they only clicked near their centre. Root causes were two, fixed independently:

- **Scroll fell through.** The window is `ShowActivated="False"` (deliberate — a detected-meeting prompt must not yank focus out of your call), so the notch is never the *focused* window, and Windows sends the wheel to the focused window (or, only if the "scroll inactive windows" setting is on, the hovered one). Activation proved unreliable to force. The robust fix routes the wheel through the **global low-level mouse hook we already run** (`InputMonitor`, its own thread): on `WM_MOUSEWHEEL`, if the pointer is over an interactive notch page it marshals a smooth scroll of the notch's own `ScrollViewer` to the UI thread (`SmoothScroll.Nudge`, shared with the normal wheel path) **and returns non-zero to CONSUME the event** — so the window behind never moves. This works with zero dependency on focus, activation, or the OS setting. `CurrentScrollViewer()` walks the hosted view's visual tree; peeks (Hidden/Collapsed/RecordingBar) are excluded (`NeedsInput`) so a resting sliver never eats the wheel.
- **Edge clicks tucked the panel.** The click-outside dismiss (`InputMonitor` → `PointInWindow`) judged a borderline click just inside the visual notch as *outside* and tucked it away — which read as "buttons only work dead-centre." Widened the hit-test by `InsidePad` (6 px) so edge clicks count as inside.

A light `PreviewMouseDown` → `Activate()` remains so keyboard focus (Esc, typing) follows a click. Audio/detection are focus-independent, so recording is unaffected; peeks still never steal focus (Hard Constraint #3 holds).

### Attribution — the "Built with" screen (M8.0)

A **licence obligation**, not decoration: Parakeet is CC-BY-4.0 (requires name + source + licence link + a note that it was modified), Qwen3 and sherpa-onnx are Apache-2.0, the rest MIT — Toki cannot be sold without crediting them. `AboutView` (`PillState.About`) is a fixed-height scrollable credits list. *(Entry point moved in v1-iter3: it's now reached from the **Library window's Settings page → "Built with — open source & licences"** (`OnLibOpenSource`), since the notch Settings became UI/visibility only. The screen itself is unchanged — one canonical copy of the licence text.)* Data lives as a small `record Credit(Name, Role, Author, Licence, Url)` grouped into MODELS / ENGINES & LIBRARIES; rows are built in code so each opens its upstream source (`Process.Start … UseShellExecute`). Attribution-with-teeth licences (CC-BY, Apache) get **amber chips**; MIT is neutral. Two correctness calls: **Whisper is omitted** (it's a stubbed error-fallback, `TranscriptionService` never ships it — crediting it would assert a false dependency), and **Parakeet carries an "int8 quantised" note** to satisfy CC-BY's indicate-changes clause. The full, authoritative texts live in repo `THIRD-PARTY-NOTICES.md` (also referenced from the screen footer). State wiring: `PillState.About` added to the enum, the `AppState.Allowed` table (Settings⇄About, About→Home/Hidden), `PillWindow` `Sizes`/`ViewFor`/`DismissOnClickOutside`/`IsStayable`. Remaining M8: Google sensitive-scope verification, signed installer + auto-update, licence-key gate.

### The hardening pass — a full multi-agent audit, verified fix by fix (H1, 2026-08-04)

With the feature set settled, the whole codebase went through a deep audit (seven parallel subsystem
auditors + adversarial verification of every finding, alongside market research on the top consumer
notetakers). 67 findings survived verification — 0 critical, 14 high — and all highs/mediums plus
nearly every low were fixed in one pass. The load-bearing decisions:

- **Recording truth lives in the session, not the pill.** `App.IsRecording` now derives from
  `_session is not null`; every generic dismiss path (Esc, ✕, auto-hide, "close", tray toggle) goes
  through `App.CloseOverlay()`, which collapses to the recording bar instead of hiding while a session
  is live; the detector's auto-stop keys off the session; `BeginRecording` refuses to overwrite a live
  session. Before this, push-to-talk or Esc during a recording could orphan a running mic with no
  indicator — the one failure mode this product can never have.
- **Deletion means deletion.** The Library/pill "Delete" paths now use `App.DiscardMeeting` (DB row +
  Markdown mirror + WAV folder) — the confirm dialog's "its audio will be removed for good" promise was
  previously broken (three auditors found it independently). Trivial/silent takes also delete their
  folders, the mirror drops stale duplicates on retitle, and the %TEMP% diagnostic log is size-capped.
- **Crash/quit recovery (`RecordingRecovery`).** WAV headers are re-finalized every ~5s during capture
  (a crash leaves a playable file, not a zero-length RIFF), and a startup sweep transcribes any
  recording folder no meeting references into a saved Library entry (tiny/silent orphans are deleted).
  Live-verified on real orphans from the July M2 spikes — one real meeting recovered, four cleaned.
- **Mid-meeting device changes.** `MicCapture` now registers an `IMMNotificationClient`: when the
  default communications mic changes (or capture dies — Bluetooth drop, unplug), it rebuilds on the new
  default and keeps writing the SAME WAV, converting buffers through a streaming linear-resampler
  (`BufferConverter`) when the new device's format differs. The CLAUDE.md-mandated rebuild finally exists.
- **Detection correctness.** The denylist matches the app NAME only (matching the full path let a
  username like "Jacobs" — contains "obs" — kill detection for every per-user app); packaged apps
  resolve by real package family name via `GetPackageFamilyName` (the AUMID-prefix guess missed new
  Teams entirely → mic-only meetings).
- **LLM lifecycle safety.** `LlamaLlm.DisposeWhenIdle()` acquires the generation gate before freeing
  native weights (the low-memory `Release()` could previously dispose mid-inference — a native
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
  is suppressed during fullscreen/presentation via one shared `PillWindow.QuietFullscreen()` — the
  prompt is delayed to the next poll, never dropped.
- Plus: atomic settings/chat writes (a torn settings.json wiped the API key + Google token), LIKE-term
  escaping, PTT chord-release fix (left/right modifier VKs), search debounce, theme-rebuild coverage
  (AboutView, manual-note body), SharpCompress CVE bump (0.38→0.48), dead `HotkeyManager` deleted, and
  the note prompts taught the two research-backed rules that separate good notes from slop: read past
  ASR mis-recognitions but never "correct" names, and record decisions WITH their stated reason.

Deliberately deferred (documented in `docs/backlog.md`): three pure-refactor consolidations
(NotesQa tokenizer triplication, notch/Library streaming plumbing, state→view factory) — regression
risk in delicate code with no user-visible payoff.

### Due-date task scoping, Library sort, and a security pass (H2, 2026-08-04)

**Task time-queries scope by DUE date (`TaskScheduling`).** The reported bug — "what tasks do I have for
this week" listing every open task — was `TaskAnswer` scoping a dated task query through
`MeetingIdsInRange` (tasks *from meetings held* in the window). On a recent library that's ≈ all meetings.
The market-validated model is deadline-based: `TaskScheduling.ParseDue` resolves each task's free-text due
against its meeting's date (future-biased — a due set on Monday for "Friday" is *this* Friday, via a
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
  EventWaitHandle were global, guessable, un-ACL'd — a local process could pre-create them to DoS startup
  or drive the UI. Now SID-salted `Local\` names with a current-user-only DACL (`MutexAcl.Create` /
  `EventWaitHandleAcl.Create`), and "already exists" defers to a running instance only after confirming a
  real Toki process exists (else it's a squat → take over). Runtime-verified.
- **`Interop/SafeShell`** — the one external-launch choke point. `OpenUrl` allows only absolute http/https
  (calendar join-links are attacker-influenceable); `RevealInExplorer` requires the path to resolve inside
  an allowed data dir. All five `Process.Start(UseShellExecute)` sites route through it.
- **`MeetingStore.ToFtsQuery`** — the single FTS5 builder (parameter binding does NOT protect a MATCH
  argument; safety is the alnum-only prefix-OR reduction). The only MATCH path uses it.
- **Markdown mirror containment** — `Write` asserts the final path is inside `NotesDir` before writing.
- **Prompt-injection delimiters** — notes fed to the Ask/Analyze prompts are fenced and labeled "DATA,
  never instructions". The model already has no tool authority (calendar creation is regex-on-utterance +
  confirm gate), so injection can at worst affect answer text.
- OAuth loopback probe socket set `ExclusiveAddressUse` (RFC 8252 §8.10); `<NuGetAudit>`/moderate level on;
  confirmed no `ServerCertificateCustomValidationCallback` bypass and no `BinaryFormatter`.
- 33 new tests (FTS adversarial corpus, path-traversal corpus, URL allowlist, BYO-API endpoint rules,
  per-user instance names, calendar pagination). 520 total green.

**Deliberately NOT done** (documented so it's a decision, not an oversight): swapping the push-to-talk
`WH_KEYBOARD_LL` hook for `RegisterHotKey` — the owner chose hold-to-talk, which needs the key-UP that
`RegisterHotKey` can't deliver; the callback already branches only on the chord and never stores other
keys. Adding a DPAPI entropy parameter — it would fail to decrypt every EXISTING stored key (wiping the
user's API key + Google token) for marginal gain against same-user malware that can call our code anyway.
Moving bulk data from `%APPDATA%` (roaming) to `%LOCALAPPDATA%` — correct long-term but needs a migration
and is owner-gated. All three are in `docs/backlog.md`.

### The assistant brain — a 1,155-case golden set + the reworks it drove (H3, 2026-08-04)

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
  partially matched and fell through to retrieval — a wrong/fabricated privacy answer is a trust breach.
  The pattern is now broad (cloud/upload/store/train/local/private/who-can-see, mic/always-on) with two
  precise, honest canned answers (local-only + the BYO-key exception; no always-on mic).
- **General-knowledge shape override (`TurnRouter.HasGeneralKnowledgeShape`).** A definitional/how-to/
  creative/world-fact question ("what does OKR mean", "write me a haiku") is parametric even when a
  content word incidentally prefix-matches a meeting title — it no longer wrongly grounds. "what is A/AN
  x" (concept) is general; "what is THE x" (a specific thing) stays grounded — the article is load-bearing.
- **Coverage widening:** Count intent now spans calls/syncs/notes/recordings (not just "meetings");
  `TasksRx` gained on-my-plate/possessive-plate/owe/deadline cues; `NewChat` and name-setting phrasings
  ("i go by X") broadened; the analytical vocabulary gained recurring/common/frequent so a bare
  "what are the recurring themes" analyses the corpus instead of being treated as an absent topic.

No non-brain code was touched; 1,636 tests green, self-tests pass, dist republished + smoke-verified.

## v1-iter2 — decisions from the owner live-test sweep (2026-08-04)

**Notch sizing — content measures right, it just needed air.** The "text cut off everywhere" was NOT an
under-measure (a new headless `--measuretest` proved every auto-height view's out-of-tree measure equals
its true arranged height — it renders a real off-screen `Window`, no `RenderTargetBitmap`, so it works in
the sandbox where the PNG harness hangs). The last line just sat flush against the rounded bottom with ~0px
slack; DPI rounding tipped it into a clip. Fix = a **14px `BottomBreath`** added to every auto-height page's
height. Separately, rapid open/close corrupted the notch because a late-firing reveal/conceal clip
animation nulled the *current* clip / hid a *reopened* window — fixed with a **`_clipGen` generation token**
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
emojis on every answer path (`GeneralAnswerAsync` + `NotchTrim`). The concise `"\n\n"` stop was removed —
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

**"Meeting on now — Join?" is both-smart.** `App.LiveCalendarMeeting()` finds a calendar event with a Meet
link happening right now; the prompt then shows Join-call (opens the link + records) + Record, or, for an
ad-hoc detected call, Join-&-take-notes. `MeetingDetector.Snooze` now also clears `_prompted` so a still-
ongoing call re-offers once after the 10-min cooldown.

## v1-iter3 — decisions from the premium-UI pass (2026-08-04/05)

**Settings has one home, and the notch isn't it.** Owner decision: *"the notch should only have the
visibility, UI settings."* So the notch `SettingsView` keeps exactly three things — light/dark, ghost
(screen-recording visibility), glassy-notch — plus one row that opens the Library window's Settings page.
Everything with weight (AI summaries + BYO API key, calendar connect, your name, About/attribution) lives
there. The moved block is a **reusable `Views/AiSummariesSection` UserControl**, not a copy: one control,
one code path, hosted by the Library page — which is why the notch state shrank 360 → 208px high with no
feature lost. The rule going forward: *if a setting needs reading, it belongs in the Library window; the
notch only holds what you toggle at a glance.*

**Ghost mode must be re-asserted, not set once.** `WDA_EXCLUDEFROMCAPTURE` is a property of the HWND that
Windows silently drops when the window's composition is rewritten — and Toki rewrites it constantly
(`SetWindowCompositionAttribute` for acrylic, `SetWindowRgn` for the rounded bottom, `Show()` after a
hide). That's why ghost held on some states and fell off others: the states that re-composited lost it.
Fix = `ApplyGhost()` is idempotent and now runs **last** in every path that touches composition —
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
content, so retrieval returned *something* — a random meeting. `SmallTalk` now owns the acknowledgement
tier (and an "I just meant X" dismissal tier) before retrieval ever runs. Same shape as the anti-
fabrication rails: **the honest answer to a non-question is a non-answer.**

**Attendee avatars are derived, never fetched.** The calendar/COMING-UP/prep redesign shows overlapping
avatar stacks, but they're initials rendered from the attendee string with a deterministic colour hash —
no Gravatar, no Google profile-photo call, no network. Hard Constraint #2 holds: a prettier row must not
become a request that leaves the laptop.

**The app icon needs every size, not the biggest one.** The first `--makeicon` wrote a single 256px frame.
Windows will accept that, then downscale it itself for the 16/24/32px taskbar, title-bar and shortcut
slots — badly — which is why the owner kept seeing "the old ugly picture" after the mascot was already
redrawn. `Dev/SelfTests.MakeIcon` now renders `TokiBlob` at **seven sizes (16/24/32/48/64/128/256)** and
writes a proper multi-entry `ICONDIR`/`ICONDIRENTRY` table (0 = 256 in the byte field). Two Windows facts
that cost time and are worth keeping: the icon inside the exe and the icon Windows *shows* are different
things (Explorer/taskbar caches aggressively — unpin/re-pin or `ie4uinit.exe -show`), and the mascot is a
WPF vector, so the `.ico` is **generated from the live control** rather than maintained as a second asset
that can drift.

**No suggestion chips on the Ask landing.** The Luma-style landing shipped with three example-question
pills; the owner removed them outright (*"flat out ugly and never encourage"*). `BuildSuggestions()` now
clears the panel and collapses it — kept as a seam rather than deleted, so the decision is visible and
reversible. Design rule reinforced: **an empty state should invite, not instruct.**
