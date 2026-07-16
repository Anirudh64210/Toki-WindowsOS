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

## Ghost mode

`SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` makes the overlay visible to the user but omitted from DWM captures — screen recording and screen-share never show it. Plus `WS_EX_TOOLWINDOW` to stay out of alt-tab. Verified: a `PrintWindow(PW_RENDERFULLCONTENT)` grab returns fully black while the app is on screen.

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

## Verification model

Two layers, both required:

1. **Unit tests** (`dotnet test`) for pure logic: state transitions, denylist filtering, echo de-dup, title validation, heuristic extraction.
2. **Self-test CLI flags** (`--mictest`, `--loopbacktest`, `--asrtest`, …) producing real artifacts (a WAV you can play, a transcript you can read) run on a real PC. CI cannot cover audio/GPU/model paths — this is permanent, not a temporary gap.

A milestone is done only when its real artifact has been seen/heard (brief, lessons 12–13).

## Deferred

- **CI pipeline** — needs a GitHub remote; added when the repo is pushed (M0 note in README).
- **Outlook calendar, teams features, "Ask Toki" chat** — explicitly out of v1 scope.
