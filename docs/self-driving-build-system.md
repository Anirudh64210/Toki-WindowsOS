# A spec-driven self-building loop (parked idea — 2026-07-31)

**Status: PARKED, to revisit.** This is a design note, not a built feature. The goal: instead of
Sid prompting Claude step by step, Sid writes one detailed brief (goals, guardrails, rules, a
definition of "done"), and Claude prompts itself to build and iterate — interrupting Sid only at
real decisions.

## The honest framing

The goal is **not** "remove the human." It is **"remove the human from the boring loop, keep them
at the decisions."** A good self-driving setup is ~80% guardrails, ~20% autonomy. This project has
already seen the failure modes of an un-supervised agent (working from stale context; spreading a
feature to the Library that was never approved). Autonomy has to be built to prevent exactly those.

## The one thing that makes it possible: a machine-checkable "done"

An agent can only iterate without a human if it can tell success from failure **by itself**. If
"done" lives only in Sid's head, the loop produces confident garbage and calls it finished. So the
engine is not a clever prompt — it is an **oracle**: something that returns pass/fail with no human.

Toki already has one: `dotnet test` (82 tests) plus the self-test flags (`--asrtest`,
`--notestest`, `--dbtest`, `--detecttest`, `--livetest`, `--linktest`, `--wertest`, …). That is
what lets a loop run: *implement → run the named check → green = move on, red = fix → repeat.*
**Every goal in the brief must name the check that proves it.**

## The three layers to design

### 1. The Brief (written once, by Sid)
A file — a new `BRIEF.md`, not CLAUDE.md — containing:
- **Goal / outcome**, stated checkably ("`--livetest` passes", not "make it fast").
- **Guardrails** — the hard constraints (Toki's 5).
- **Definition of done per task** — *which test/flag proves each one.*
- **Checkpoints** — the explicit "STOP and ask me before…" list (product/UX choices, anything
  irreversible, git, touching another component "for consistency").
- **Out of scope / do-not-touch.**
- **A living task list** (`TASKS.md`) the loop reads and checks off.

### 2. The Loop (the self-prompting engine)
Claude Code mechanisms, lightest to heaviest:
- **`/loop` (self-paced)** — run a command repeatedly with no interval, Claude paces it.
  `/loop /build-next` = "keep doing the next task until done or blocked." Closest to the ask, and
  needs zero code.
- **A custom slash command** — `.claude/commands/build-next.md` encodes the self-prompt once:
  *"Read BRIEF.md + TASKS.md, take the top unchecked task, implement per CLAUDE.md, run the named
  test, if green check it off and update docs, if it needs a decision STOP and ask."* Then
  `/loop /build-next` drives it.
- **`/schedule`** — cron-style, for "work on this each morning" across days.
- **Workflows (ultracode)** — deterministic multi-agent fan-out for big parallel jobs (build N
  features, review each, verify). More power, more tokens, explicit opt-in.
- **Headless / Agent SDK** — `claude -p "…"` or a script that feeds the brief, runs tests, and
  re-invokes on failure. "True" autonomy, but someone has to write the harness.

### 3. The Guardrails (what keeps it from going off a cliff)
Encode this project's paid-for lessons into the brief so autonomy can't repeat them:
- **Verify by running, never by describing** (build + run + watch the artifact appear).
- **Human-in-the-loop for product / UX / irreversible / git** — those are checkpoints, not
  autonomous.
- **No scope creep** — do the task, nothing adjacent; "consistency" is not permission.
- **A fix-attempt budget** — after N failed iterations on one task, stop and ask instead of
  thrashing.
- **Cost awareness** — autonomous loops burn tokens; cap the run.

## Recommended path for Toki

Start at the light end — no SDK needed: **`BRIEF.md` + `TASKS.md` + a `/build-next` command +
`/loop /build-next`**, with tests / self-test flags as the oracle and hard checkpoints for anything
product-facing. That delivers "Sid writes the brief, Claude builds and iterates and only interrupts
at real decisions" **today**. Graduate to Workflows or a headless harness only if the backlog grows
big enough to want parallel agents.

Mental model: **Sid becomes the reviewer and decision-maker; the loop becomes the builder.** It
works exactly as well as the brief's "done" checks and checkpoints are sharp — vague brief, vague
results.

## Starter kit to build when we revisit
1. A **`BRIEF.md` template** filled in for Toki, with definition-of-done and checkpoint sections.
2. A **`/build-next` slash command** wired to the existing tests and self-test flags.
3. A seeded **`TASKS.md`** from the parked items (Snipping-Tool/OBS recording collision, live
   transcription Phase 2, etc.).
