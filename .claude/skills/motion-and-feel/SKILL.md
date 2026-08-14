# Motion, haptic feel, and UI design - the Toki rules

Research-derived (Apple HIG springs, Material 3 motion tokens, IBM Carbon, Emil Kowalski's
animation standards, Duolingo's Rive mascot practice, Josh Comeau's shadow system, and the
WPF rendering docs). Apply this skill on EVERY visual or animated change to Toki. When a
rule here conflicts with older code, the rule wins; when it conflicts with the owner or the
Figma file, they win.

## 1. What may animate (the WPF jank triad)

WPF animation looks "hard and ugly" for exactly three reasons; all three are banned:

1. **Never animate layout properties** (Width/Height/Margin/Padding) on elements - they
   re-run Measure/Arrange on the whole subtree every frame on the UI thread. Animate
   `ScaleTransform` / `TranslateTransform` / `Opacity` (and Clip for the notch reveal)
   instead. Pre-attach the TransformGroup; animations update transform properties, never
   replace the Transform object.
   - Exception, by architecture: the notch window's size morph (AnimateSize) animates the
     HWND because the hovering-card design needs a true resize. Mitigate: nothing with an
     Effect may be inside it while it runs (see 3), and morphs stay under 300ms.
2. **Never animate the Window's own size when avoidable** - a layered
   (AllowsTransparency) window pays native resize + full layout + DWM surface realloc per
   frame. Reveals/conceals use the composited Clip; only cross-state morphs resize.
3. **No `DropShadowEffect` (or any Effect) on anything that animates or contains changing
   text.** An Effect renders its whole subtree in SOFTWARE and re-renders on every change;
   an Effect inside a breathing mascot re-renders the mascot 60x/s on the CPU. Shadows are
   soft gradient ellipses, layered translucent borders, or a static separate caster that
   is HIDDEN while a morph runs and faded back on settle.

Supporting: `Freeze()` brushes/geometry used in animation; no storyboard survives its
view; `BitmapCache` a complex subtree only for the duration of a scale/translate, then
drop it (the cache kills ClearType). Text that gets scale-animated uses Ideal formatting
and must LAND at exactly 1.0.

## 2. Durations and easing (Theme/Motion.cs is the single home)

| Interaction | Duration |
|---|---|
| Press feedback | 100ms in, ~200ms release |
| Hover fill | 110-150ms |
| Tooltip / small popover | 150-200ms |
| Panel expand / content morph | 200-300ms |
| Notch open/close reveal | 300-450ms |
| Anything interactive | never over 500ms |

- **Exits run at ~70% of their enter.** Users watch things arrive, not leave.
- **Enter = strong ease-out** `KeySpline 0.23,1 0.32,1` (Motion.Enter).
- **Exit = accelerate** `0.3,0 0.8,0.15` (Motion.Exit).
- **Move (already visible) = ease-in-out** `0.77,0 0.175,1` (Motion.Move).
- **Notch/sheet morph = the drawer curve** `0.32,0.72 0,1` (Motion.Drawer) - the Dynamic
  Island settle.
- Never ease-IN on anything the user just triggered. Never Linear except progress.
- Overshoot 2-5% max (scale settles from <=1.05), reserved for the mascot and one-time
  arrival moments - not hover states.
- Enter = scale 0.95-0.97 -> 1.0 + fade. Never scale from 0; never fade alone for a
  spatial element. Transform-origin = point of causation (notch: top-center; popover: its
  trigger corner).
- Sibling stagger 30-80ms; the last sibling lands within 400ms of the first.

## 3. Haptic feel without haptics

- **Press = the whole box compresses to scale 0.97 within 100ms** (the box is the button);
  release springs back ~200ms strong ease-out. Applied centrally in the shared button
  styles (PillButton / IconButton / RowButton), never per-site.
- **Hover = a real background fill change** (not an opacity wash - the contrast-bug
  lesson), 110-150ms.
- No UI sounds; none of the reference class (Raycast, Arc, Linear) use them.

## 4. The mascot (alive, homely, noticeable)

- Breathing ~3.5s/cycle at <=2% amplitude, always on. Blink 250ms (close fast, open
  slower), randomized 2-6s, occasional double-blink.
- **Fidgets every 30-60s** (the squish/hop/wobble vocabulary), layered over idle, never
  interrupting a user-triggered reaction. Priority: reaction > fidget > idle.
- Reactions fire <100ms and cancel whatever else is running.
- Size: a companion glyph is 24-40px; in hero/narrator moments (beside the notch card)
  he reads at up to ~1/3 of the panel height. "Unnoticeable" is a bug; "in the way" is a
  bug.
- Placement follows the Figma frames PER STATE: compact pills = vertically centered on
  the pill; the expanded recording panel = bottom-left, overlapping the corner; tall
  content panels = level with the header.

## 5. Glass, borders, shadows, radii

- Dark glass panel = 1px white 10-15% outer rim + a top-edge inner highlight (light
  catches the rim). Hairline separators (white 8-12%) instead of filled boxes.
- **Concentric radii: inner = outer radius - padding** (floor 4px). Same-radius nesting
  is a bug. Toki's ramp: card 24 -> inner 16 -> capsule 12 -> pill 999.
- Layered shadows: 2-3 stacked, vertical offset ~2x horizontal, opacity falls as blur
  grows; deeper shadow + brighter rim = higher elevation. Never pure black at high
  opacity.

## 6. Review checklist (run before shipping any visual change)

1. Does anything animate Width/Height/Margin? (Only the notch morph may.)
2. Is any Effect inside an animated subtree? (Grep for DropShadowEffect near transforms.)
3. Are all easings from Motion.cs, enter-out/exit-in, exits shorter?
4. Do buttons compress on press, from the shared style?
5. Are nested radii concentric? Is the rim 1px white-alpha, not grey?
6. Is the mascot the right size AND position for this state's Figma frame?
7. Did you verify by RUNNING the app (or a --render/--uitest harness), not by reading
   the code?
