# The Toki design language

Distilled from the Figma redesign ("Toki - Product", file Udtb5KHB2iTLIVP5FhKaSl, node 288:4,
"Redesign by SKz") and the shipping Windows app. Read this before designing any Toki screen.

## What Toki is

A meeting notetaker that lives at the top edge of a Windows desktop as a HOVERING glass card (a
"virtual notch"), plus one full Library window. Everything is on-device and private; the design's
personality is calm, warm, homely - restraint is the design. Appear only with something worth
showing.

## The world

- Surfaces are warm dark glass: rgba(33,30,30,0.5) with backdrop blur (or #2B2828 opaque), radius
  24, a 1px white-10% rim with a faint top-edge highlight, soft layered shadow below.
- Text is WHITE AT OPACITIES, never grey hexes: 90% (primary), 50% (secondary), 25% (tertiary).
- Type is Segoe UI Variable. SemiBold structures; Bold only for micro-labels and the big document
  title. Overlines are 9.5-10px, letter-spaced, uppercase.
- Radii are concentric: card 24 → inner 16 → capsule 12 → pill 999. Inner radius = outer - padding.
- Dividers, not boxes: hairlines (white 9%) separate content; filled boxes are reserved for truly
  raised things, which then owe a rim and shadow.

## One meaning, one colour

- Amber #F5A05C - time, countdowns, "N open" counts, transcript sections
- Teal #32C7D3 - action items
- Blue #5AA9F9 - people, owners
- Green #3DD68C - decisions, success, the ON switch
- Pink #F09CC7 - open questions, selection accents
- Coral #FF6F61 - the record CTA · Stop-red #FF2D55 - "Stop & summarize" only
- Destructive elsewhere is red TEXT (#E5484D) on a neutral shape; red fill is the final confirm.

## The notch states (the frames)

- Toki the blue cloud floats BESIDE the card in a round translucent bubble - never inside it,
  never twice. Compact pills: he is vertically centred on the pill. Expanded recording panel: he
  overlaps the bottom-left corner. Tall panels: level with the header row.
- Record prompt: a small capsule "Record this meeting" with Toki's bubble at its left.
- Recording bar: timer + live waveform + pause + red stop, tucked at the very top edge
  (non-invasive); expanding it shows live transcript lines with per-speaker initial avatars and a
  red "Stop & summarize" pill, Toki writing at the bottom-left corner.
- Notes ready: a capsule "Notes ready ›" with a happy (squinting, sparkling) Toki overlapping its
  left edge.
- Toki's mood narrates state: thinking (dots bubble) while summarizing, writing (pen) while
  recording, happy when notes land, idle otherwise.

## The Library window

Sidebar (logo row: small Toki + wordmark; nav: Ask / Library / Settings, active = filled rounded
row) → list pane (search field, sort button, TODAY/YESTERDAY groups, MeetingCards with the date
OUTSIDE the card) → reading pane (big Bold title ~28px; meta row with "N open" and "N people"
capsules; a summary paragraph; coloured sections - green DECISIONS bullets, teal ACTION ITEMS with
circular checks and owner/due capsules under each item, pink QUESTIONS, amber TRANSCRIPT with
initial-avatars; a rounded icon-group toolbar top-right ending in a red trash).

## Feel

- Enter fast-then-soft (cubic-bezier 0.23,1,0.32,1); exits accelerate and run ~70% of the enter.
- Panels settle on the drawer curve (0.32,0.72,0,1). Overshoot never exceeds ~5%.
- EVERY button compresses on press (whole box to 0.97, icons 0.94, rows 0.985) and springs back.
- The mascot breathes ~3.5s at tiny amplitude and blinks asymmetrically; he reacts to events and
  occasionally fidgets - alive, not busy.
