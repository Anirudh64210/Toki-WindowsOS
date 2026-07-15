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

## M1 — Pill shell (next)

Borderless topmost pill window, the full state machine with placeholder content, morph animations, tray icon, hotkey. Verified by clicking through every state on screen against the design PNGs.
