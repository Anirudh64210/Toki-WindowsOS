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

## Data location

Everything under `%APPDATA%\Toki\`: `toki.db` (SQLite), `Notes\` (Markdown mirror), model files, logs. Local-only by hard constraint #2.

## Verification model

Two layers, both required:

1. **Unit tests** (`dotnet test`) for pure logic: state transitions, denylist filtering, echo de-dup, title validation, heuristic extraction.
2. **Self-test CLI flags** (`--mictest`, `--loopbacktest`, `--asrtest`, …) producing real artifacts (a WAV you can play, a transcript you can read) run on a real PC. CI cannot cover audio/GPU/model paths — this is permanent, not a temporary gap.

A milestone is done only when its real artifact has been seen/heard (brief, lessons 12–13).

## Deferred

- **CI pipeline** — needs a GitHub remote; added when the repo is pushed (M0 note in README).
- **Outlook calendar, teams features, "Ask Toki" chat** — explicitly out of v1 scope.
