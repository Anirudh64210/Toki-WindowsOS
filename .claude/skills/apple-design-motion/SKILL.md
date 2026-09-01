---
name: apple-design-motion
description: "The documented Apple motion + haptic-feel NUMBERS - spring presets, per-context durations, the enter/exit asymmetry, stagger timing, press states, reduce-motion, and the haptic/feedback grammar. Load alongside motion-and-feel on any visual/animation change: motion-and-feel is Toki's HOUSE STYLE and wins on conflict; this is the source-of-numbers layer beneath it. Adapted (MIT) from s1gmamale1/apple-design-motion; SwiftUI/web code stripped, values re-anchored to Toki's Theme/Motion.cs (WPF Storyboards/KeySplines)."
---

# Apple Design - Motion (the numbers under motion-and-feel)

Attribution: adapted from `s1gmamale1/apple-design-skills` (`apple-design-motion`), MIT, (c) 2026
s1gmamale1. Values are `[documented]` = WWDC/HIG unless noted. **Precedence: the user's words > Toki's
Figma/`motion-and-feel` house style > these numbers.** Where a Toki curve already encodes a value, keep
Toki's; use this to CATCH a value that drifted, not to relitigate a deliberate one.

## The rules Toki already holds (confirmed here, don't re-derive)
- Springs/eased curves over raw `ease`; curves live in `Theme/Motion.cs`, never inline magic beziers.
- Animate TRANSFORM + OPACITY only (GPU-composited). No `Effect` (DropShadow etc.) inside an animated
  subtree - that forces a per-frame software render (the "hard rendering" bug).
- Every actionable control gets a visible press state; exits are quicker than enters.
- Honour reduced motion: replace a big spatial move with a dissolve, never a hard cut of meaningful motion.

## The numbers to hold Toki against

**Per-context DURATIONS** (copy these bands; `micro < 250ms` to feel instant, `full-screen < 500ms`
to not feel slow):

| Context | Duration |
|---|---|
| Button/tap feedback (press-compress) | 100-200 ms |
| Chip / toggle / small state change | 200-300 ms |
| Sheet / panel / card present | 300-400 ms |
| Full-screen nav / page push | 350-450 ms |
| Large content reveal | 400-500 ms |

**Spring feel presets** (targets, not literals): `smooth` (no bounce) for background/modal;
`snappy` (~15% bounce) for button press / toggle / chip; `bouncy` (~30% bounce) for an icon press or a
rewarding arrival. SwiftUI default spring ~= `response 0.55, damping 0.825`; snappy-interactive
`0.30 / 0.80`. In WPF, approximate with a KeySpline/BackEase - e.g. smooth overshoot `cubic-bezier
(0.34, 1.56, 0.64, 1)`.

**The ASYMMETRY rule** (Toki's "exit ~70% of enter"): appear is SLOWER than disappear. Present ~380 /
dismiss ~320; push ~420 / pop ~360; element enter (fade+scale) ~350 / exit ~180. **Exit-only motion =
pure opacity fade 150-220 ms, ease-in, no spring, no scale.**

**FREQUENCY penalty:** anything fired dozens of times a session (a row tap, a toggle, a hover) animates
`< 250 ms`. Reserve the long, expressive durations for rare, significant moments (first launch, the
notes-ready flight, a destructive confirm). A slow animation on a frequent action is the #1 "sluggish"
tell.

**STAGGER** (list/grid reveals): 20-50 ms per item; total `<= 300 ms`, hard cap 400 ms.
2-3 items -> 40-60 ms each; 4-6 -> 30-50; 7-12 -> 20-35; 13+ -> 15-25 with ease-out on the stagger.
Below ~15 ms is imperceptible; above ~80 ms items read as unrelated. Entry order = hierarchy
(container -> primary -> secondary -> actions); exit = reverse.

**PRESS states** (maps to Toki's shared button templates): a bare/system button dims to ~0.4 opacity on
press over ~80 ms, NO scale; a filled/custom button does scale 0.96 + a slight dim (Toki's press-compress
- confirmed correct). Respond on press-down, never block input behind the animation; a gesture's release
velocity should feed the settle.

**REDUCE MOTION:** swap translate/scale for a dissolve; keep brief functional flashes (<100 ms); do not
just disable transitions.

## Haptic / feedback GRAMMAR (intent, even without a Taptic Engine on Windows)
Restraint is rule #1: feedback must correlate to a user action; never double-fire; never stack on a
system control. The vocabulary (use it to reason about visual+sound feedback intent, and if a trackpad-
haptic or sound path is ever added): **notification** fires only on a multi-step OUTCOME - success /
warning / error - never a single tap; **impact** is a collision metaphor - light (pick) / medium
(drop-land) / heavy (big snap); **selection** is a light tick per increment (a picker detent, a reorder
row-crossing), not per scroll event. Multi-sensory = haptic + micro-animation + subtle sound as ONE
triad within a frame. **Sound is additive-only**: a UI that needs sound to be understood has failed;
respect silent mode; slightly vary pitch on repeats; cut any sound whose removal loses nothing.

## How to use this on a Toki change
1. Identify the interaction's CONTEXT + FREQUENCY -> read the duration band + the frequency penalty.
2. Check Toki's `Theme/Motion.cs` value against the band. If it's inside, leave it. If it drifted
   outside (a frequent action > 250ms, an exit as slow as its enter, a stagger > 80ms/item), fix TOWARD
   the band - and verify with `--fpstest` / `--revealtest` and the owner's eyes, never numbers alone.
3. Never introduce an `Effect` into an animated subtree; never animate layout (`Width`/`Margin`) where a
   transform will do.
Related house skill: `motion-and-feel` (wins on conflict), `design-taste` (the slop audit),
`symmetry-and-spacing`.
