# CLAUDE.md - Toki for Windows

This file is the CONSTITUTION: the rules, constraints and expectations that govern every session.
It holds no state and no history. **Never append build-log entries, milestone summaries, or
"current state" sections here** - that is how the last version of this file rotted into 300 lines
of stale changelog. State lives where state belongs (see "Where the truth lives", bottom).
Rewritten fresh 2026-08-14 at the owner's ask; the old file is archived at
`docs/claude-md-archive-2026-08-14.md`.

## What Toki is

A meeting assistant that lives at the TOP edge of the screen as a hovering glass card (a "virtual
notch") plus one Library window. It detects when you're in a meeting, offers to record,
transcribes and summarizes entirely on-device, and keeps every meeting in a searchable local
Library. One-time $20, no subscription, nothing leaves the laptop. Restraint is the design.

## Hard constraints - every decision filters through these

1. **$0 monthly upkeep, forever.** No servers, no cloud costs. On-device ASR + LLM. Optional BYO
   API key = the user's cost, never ours.
2. **All data local.** SQLite + files under `%APPDATA%\Toki\`. No account, backend, telemetry, sync.
3. **Toki only activates for meetings.** Never taps system audio globally; detects a call, asks,
   taps only that app's process tree. Human in the loop. Owner decision - do not relax.
4. **Minimal permissions.** Nothing that triggers a scary system prompt.
5. **Restraint is the design.** Appear only with something worth showing.

## Working with the owner

The owner (Sid, they/them) is **non-technical**. Most past friction has been alignment friction -
these rules are how we stay aligned:

- **Explain plans in plain language before big changes.** Recommend, don't assume; ask before
  product decisions. Do exactly what was asked; never spread a change to other components "for
  consistency" without approval.
- **Verify by RUNNING the app and watching real output appear** - never by describing what code
  should do. A green test suite has repeatedly missed bugs the owner saw in one glance; for
  anything visual, the owner's eyes are the final gate. Self-tests measure a model of the product;
  when they disagree with the owner, the owner is right.
- **Never run git commands.** The owner does ALL git, including staging. My job ends at "safe to
  commit" plus a list of what changed.
- **Never touch `%APPDATA%\Toki\settings.json`** (DPAPI-protected API key and calendar tokens;
  corruption is irrecoverable). Harnesses and tests must never touch the real note store.
- **No em dashes, anywhere** - code, comments, docs, UI, prompts. Hyphen, comma, or a new
  sentence. Guarded by `NoEmDashTests` (3 named functional exceptions). En dashes in ranges stay.
- **A clickable thing is a Button.** Never a Border with a mouse handler (this bug shipped three
  times). The box IS the button: hit target = hover highlight = visible box.
- **A control wired to nothing is worse than no control.** Never ship UI for a feature that
  doesn't exist yet, even if the design frame draws it.
- **One Toki per surface.** On any panel or window there is exactly one mascot (the floating
  bubble beside the notch card; peeks carry their own tiny one because the bubble hides there).
- **Small milestones**, each verified before the next. After every milestone: update `README.md`
  (build log), `ARCHITECTURE.md` (decisions + rationale), `mansplain/` (plain-language story).
- Never add `Microsoft.ML.OnnxRuntime` (native backend conflict with sherpa-onnx).

## Design - the Figma file is the boss

- **Reference**: Figma file `Udtb5KHB2iTLIVP5FhKaSl`, node `288:4` ("Redesign by SKz") - THE
  design. When building any screen, fetch the frame and replicate it; approximations get rejected.
  Adopted screens are implemented in WPF from the FRAME (never ported from generated HTML).
- **The material**: warm dark glass rgba(33,30,30,0.5) (opaque stand-in #2B2828), radius 24 cards
  with a 1px white-10% rim, text as WHITE OPACITIES (90/50/25), Segoe UI Variable. Radii are
  concentric: card 24 > inner 16 > capsule 12 > pill 999. One meaning one colour: amber=time/counts,
  teal=actions, blue=people, green=decisions, pink=questions, coral=record CTA, #FF2D55=Stop only.
  Tokens live in `Theme/Theme.xaml` + `Theme/ThemeManager.cs` (Color+brush pairs, theme-swapped).
- **Motion & feel**: apply `.claude/skills/motion-and-feel/SKILL.md` on EVERY visual change. The
  short version: animate only transforms/opacity/clip; no Effect inside an animated subtree;
  curves from `Theme/Motion.cs`; every button compresses on press; exits ~70% of enters.
- **The notch architecture**: the window is BIGGER than the card (mascot gutter left, shadow pads
  right/bottom, all in `PillWindow` CardPad constants); the card hovers below the top edge except
  recording (flush-ish) and the collapsed summon peek (flush). Toki's bubble position is per-state
  (`PlaceMascot`). Ghost mode (WDA_EXCLUDEFROMCAPTURE) must be re-asserted after every
  composition write (`ApplyGhost()`).
- A web mirror of the design system lives in `design-web/` and syncs to claude.ai/design
  (see `.design-sync/NOTES.md`). It drifts when the theme changes; port token changes by hand.

## Tech stack (owner-confirmed - do not re-litigate)

| Concern | Decision |
|---|---|
| Framework | C# / .NET 8 + WPF, `net8.0-windows10.0.19041` |
| Overlay | ONE morphing window, TOP-center hovering notch (state machine in `AppState`; views only read it) |
| "Them" audio | WASAPI Application Loopback, per-process-tree, Win 10 2004+ |
| "You" audio | WASAPI mic capture via NAudio |
| Detection | Registry mic-consent-store poll, 2.5s; DENYLIST not allowlist; debounce 2 polls |
| Final ASR | Parakeet TDT 0.6b v2 (ONNX int8) via sherpa-onnx, whole-file per stream |
| ASR fallback | whisper.cpp small.en - only on ERROR, never on silence |
| Name repair | Post-ASR correction (`NameCorrector`, corpus-wide + trusted names), never decode-time hotwords |
| Diarisation | ASR-guided (one embedding per utterance), AVERAGE linkage, threshold 0.80, attendee-count prior; NO persistent voiceprints (owner decision) |
| Notes LLM | Qwen3-4B-Instruct Q4_K_M via LLamaSharp, Vulkan, VRAM-auto GPU offload; heuristic extraction always available as the floor |
| Storage | SQLite at `%APPDATA%\Toki\toki.db` + Markdown mirror in `Notes\` |
| Calendars | Google AND Outlook, both connectable at once, loopback+PKCE, DPAPI tokens, merged agenda |
| Voice | Push-to-talk ONLY (Ctrl+Alt+Space hold). NO wake word, NO always-on mic (owner decision) |
| Echo fix | Cross-stream text de-dup post-ASR (±4s window); OS AEC opt-in only |
| Distribution | `dist\Toki.exe` self-contained single-file test build; signed installer + Velopack = M8, PARKED until product final |

## Lessons paid for - do NOT relearn

1. Garbage in, garbage out - check what the LLM is fed before tuning prompts.
2. A meeting never waits on a download. Download at onboarding, warm at launch, degrade to heuristic.
3. Never hand-chunk audio. One whole-file ASR call per stream.
4. Silence is not failure. Empty Parakeet result = nobody spoke; Whisper only on actual error.
5. Reset recognizer state between meetings.
6. OS echo-cancel is fragile; opt-in only.
7. A silent meeting saves nothing - no hollow Library entries.
8. Sampler params over prompts (Qwen3: temp 0.7, topP 0.8, topK 20, presence ~0.5, never greedy;
   validate output, retry once, fall back to heuristic).
9. Titles from the summary, heuristically, with validation and a plain fallback.
10. Duration is wall-clock, not UI ticks.
11. Attribution is a licence obligation (Parakeet CC-BY-4.0, Qwen Apache-2.0) - the "Built with"
    screen ships or we're in breach.
12. A self-test that never produced its real artifact is not a test.
13. Test the packaged artifact, not just the dev build.
14. (Windows additions) A summary the user can't check against the room is worse than a plain
    one - the transcript alone writes the notes. A template is a claim: the transcript must
    confirm a structure or it gets filled with invention. Wrong evidence links are worse than
    none - stay silent below the match floor.

## Build, test, verify

```
"C:\Program Files\dotnet\dotnet.exe" build Toki.sln
"C:\Program Files\dotnet\dotnet.exe" test Toki.sln
# publish the owner's test build (close the running Toki first - check for a live recording):
dotnet publish src/Toki/Toki.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

Self-test flags on the built exe (headless, exit pass/fail) - run the relevant ones after every
change, and ALL of the UI ones after visual work:
`--hittest` (every clickable box, label centering) · `--uitest` (Library perf budgets vs the REAL
db) · `--notetest` (note editor, 23 checks) · `--measuretest` (arranged heights) · `--stickytest`
· `--render <dir>` / `--libshot <dir>` (PNG the views; the transparent overlay can't be
screenshotted) · `--asrtest <file>` · `--notestest` · `--llmtest` · `--dbtest` · `--caltest` ·
`--preptest` · `--diartest` · `--makeicon`. CI can never verify GPU/model/audio paths - those are
run-on-a-real-PC jobs forever.

## Where the truth lives (read these, not this file, for state)

- **`README.md`** - the build log, newest entry first. The top entry IS the current state.
- **`ARCHITECTURE.md`** - every decision with its rationale, append-only.
- **`docs/backlog.md`** - the roadmap and everything parked, with reasons.
- **`mansplain/`** - the plain-language story per milestone, for the owner.
- **Memory** (`MEMORY.md` index) - cross-session lessons, gotchas, owner preferences.
- **The task list** - live work items (#93/#94 Figma revamp remainders are the open front).
- `WINDOWS-BUILD-PROMPT.md` - the original full brief (historical; this file supersedes where
  they conflict).
