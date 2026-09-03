# The Figma layer dump, and what it is good for

`figma-layer-css.txt` is a raw "copy as CSS" export of the whole redesign canvas
(Figma file `Udtb5KHB2iTLIVP5FhKaSl`, node `288:4`, "Redesign by SKz"): 46,687 lines,
655KB, every layer on an 11432x11662 board, flattened into comment-labelled blocks with
no selectors and no nesting. It lived at the repo root as
`css code for all the design layers.txt` until 2026-09-03.

## Read this before you use it

**It is not a port source.** CLAUDE.md is explicit: adopted screens are implemented in
WPF from the FRAME, never ported from generated HTML or CSS. This dump has no component
boundaries, no states, and no hierarchy. Every position in it is absolute on an 11k
canvas. Building from it would produce exactly the flat, generated-looking UI that
`.claude/skills/design-taste/SKILL.md` exists to catch.

**It is a VALUE reference.** That is the whole of its job. When you need the exact stop
in a gradient, the exact rim opacity, or confirmation that a radius really is 24 and not
22, this file has the answer offline, frozen at the state of the design file on
2026-08-20. Grep it. Do not read it.

It is also the fallback when the Figma MCP is unreachable. The live file is still the
boss; this is a snapshot of it.

## What is in there (extracted 2026-09-03)

**The material.** `rgba(33, 30, 30, 0.5)` appears 72 times, with `0.4` (44) and `0.2` (6)
as the lighter variants. `border: 1px solid rgba(255, 255, 255, 0.1)` appears 38 times.
Both match CLAUDE.md exactly, which is the best evidence that the constitution's material
description was taken from this file and has not drifted.

**Radii**, by frequency: 99 (134 uses), 24 (71), 12 (25), 16 (24), 4 (14). The concentric
ladder card 24 > inner 16 > capsule 12 > pill 99 is confirmed by usage, not just by
assertion. The 1.5px (35) and 7px (17) values are icon and illustration internals, not
surface radii.

**Type.** `SF Pro Rounded` on 478 of 496 text layers, `SF Pro` on the remaining 18. Toki
ships Segoe UI Variable because SF is not licensable on Windows; that substitution is a
known, accepted difference. Sizes: 10 (132), 12 (109), 14 (91), 16 (76), 20 (40), 8 (32).
Weights: 600 (251), 500 (162), 400 (59).

**Text colour** is white opacity throughout, and the 90/50/25 ladder in CLAUDE.md is
confirmed: `0.9` (237 uses), `0.5` (76), `0.25` (55).

**Blur.** Only `blur(10px)` (22), `blur(5px)` (15) and `blur(4px)` (15) appear on real
surfaces; `blur(250px)` (3) is a background bloom, not a material. Worth knowing, because
Toki cannot do true backdrop blur behind the notch at all (ghost mode); see
`.claude/skills/` and the CardMaterial notes for why the frosted look is painted.

## Accent drift: an open question, not a bug

The design file and `Theme/Theme.xaml` disagree on four of the six accents. These are
small shifts, all in the same direction (the shipped ones are slightly more saturated and
slightly lighter), which reads like a deliberate hand-tune rather than an accident. It is
recorded here rather than changed, because accent colour is a visible product decision and
CLAUDE.md forbids spreading a change for consistency without approval.

| Meaning | Figma file | Theme.xaml | Match |
|---|---|---|---|
| amber, time and counts | `#F5A05C` | `AccentAmber #F5A05C` | yes |
| green, decisions | `#45C98A` | `Green #3DD68C` | no |
| blue, people | `#4DB8F5` | `Blue #5AA9F9` | no |
| pink, questions | `#EA72B0` | `Pink #F09CC7` | no |
| purple | `#B477E8` | `Purple #C08AF0` | no |
| teal, actions | not in the dump | `AccentTeal #32C7D3` | n/a |

Note that Theme.xaml carries a second, older family too (`Amber #E89B3C`, alongside
`AccentAmber #F5A05C`). Which of the two is authoritative per surface is not obvious from
the tokens alone, and is part of the same question.

The mascot's own colours (`#C7F0FC`, `#91E4F9`, `#01A4FF`) appear in the dump but not in
the theme, which is correct: TokiBlob paints them itself rather than taking theme tokens.

Nothing here has been acted on. It is logged in `docs/backlog.md`.
