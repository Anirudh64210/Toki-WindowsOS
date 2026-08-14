# Building with the Toki design system

Toki is a Windows meeting notetaker: a hovering glass "notch" at the top of the screen plus one
Library window. This library is the web mirror of that app's design system. Screens you design
here get ported to WPF/XAML, so build ONLY from these components and tokens - no other UI kit,
no invented colors.

## Setup

No provider is needed. Import components from the package and rely on `styles.css` (already
bound) for tokens and component classes. Every surface must sit on a dark ground: use
`var(--tk-ink)` (#161618) for window interiors, `var(--tk-surface)` (#0E0E10) for the app frame,
or a wallpaper image/gradient behind `GlassPanel`/`NotchShell` - never white, never transparent.

## Styling idiom

Style your own layout glue with inline styles or small CSS that reads TOKENS - always
`var(--tk-*)`, never literal hexes. The vocabulary (defined in `styles.css`):

- Ground: `--tk-ink`, `--tk-surface`, `--tk-surface-hi`, `--tk-chip`
- Glass: `--tk-glass`, `--tk-glass-opaque`, `--tk-glass-capsule`, `--tk-glass-border`, `--tk-hairline`
- Text (white opacities, never grey): `--tk-text-hi` (90%), `--tk-text-mid` (50%), `--tk-text-low` (25%)
- Accents, one meaning each: `--tk-amber` (time/counts/transcript), `--tk-teal` (actions),
  `--tk-blue` (people), `--tk-green` (decisions/success), `--tk-pink` (questions/selection),
  `--tk-coral` (record CTA), `--tk-stop-red` (Stop only), `--tk-danger` (destructive text)
- Radii (concentric: inner = outer - padding): `--tk-r-card` 24, `--tk-r-inner` 16,
  `--tk-r-capsule` 12, `--tk-r-pill` 999
- Font: `--tk-font` (Segoe UI Variable stack). Motion: `--tk-ease-arrive`, `--tk-ease-depart`,
  `--tk-ease-drawer`. Shadows: `--tk-shadow-card`, `--tk-shadow-raised`.

## Rules that define Toki

- Dividers, not boxes: separate content with hairlines (`--tk-hairline`), not filled containers.
- Text is white at opacities; overlines are 9.5-10px, 600, letter-spaced, uppercase.
- Buttons: `PillButton` for text actions (red FILL only for variant="stop"; destructive elsewhere
  is `dangerText`), `IconButton` for glyphs, `RowButton` for settings lines. Never a div with a
  click handler.
- Action items always use `ActionItem` (circular teal check, owner/due capsules below the text).
- Status facts are `Capsule`s ("2 open" amber, "3 people" neutral, owners blue, due dates amber).
- The notch is composed with `NotchShell` (Toki floats BESIDE the card in his bubble - never
  inside it, never twice); his `mood` narrates state: writing while recording, thinking while
  summarizing, happy when notes are ready.
- Read the "Toki design language" guide bound with this system (under `guidelines/`) before
  designing any screen - it describes every notch state and the Library window layout from the
  Figma file.

## Example

```tsx
import { NotchShell, PillButton, UpcomingList, GlanceRow } from "toki-design";

<div style={{ background: "var(--tk-surface)", padding: 24 }}>
  <NotchShell width={344} mascot="header" mood="idle">
    <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 12 }}>Welcome back, Sid!</div>
    <div style={{ display: "flex", gap: 8, marginBottom: 14 }}>
      <PillButton variant="glass">Start a meeting</PillButton>
      <PillButton variant="glass">Take Notes</PillButton>
    </div>
    <UpcomingList>
      <GlanceRow day={14} month="AUG" title="Design review - PixelFlow"
                 meta="2:00 PM · 30 min" countdown="in 12 min" joinable accent="teal" />
    </UpcomingList>
  </NotchShell>
</div>
```
