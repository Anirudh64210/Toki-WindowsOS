---
name: symmetry-and-spacing
description: The spacing scale, vertical rhythm, one-gutter alignment, optical versus mathematical centring, symmetry, grouping, and how to assert all of it in code. Load before laying anything out, and whenever a screen looks "off" without an obvious cause.
---

# Symmetry, spacing and alignment

A screen looks designed when a stranger can guess every gap in it. This turns that into numbers you
can assert. It applies to both surfaces: the notch card (narrow, dense, one column) and the Library
window (wide, multi-column). Apply it alongside `motion-and-feel` on every visual change.

The rule underneath all of it: **spacing is a claim about relatedness.** Elements near each other
are read as one thing. So a gap is never "what looked ok", it is an assertion that these two things
belong together and those two do not.

## 1. The spacing scale

**Base unit 4. Preferred increment 8.** One scale, in device-independent pixels:

```
2   4   8   12   16   24   32   48   64
```

- **2** is not a spacing value. It is for optical corrections and hairlines only. Never use 2 to
  separate two pieces of content.
- **4** is the half step, inside small components only (icon to its label in a chip, value to unit).
- **8** is the default. If you cannot justify another number, the gap is 8.
- **12, 16** group and pad. **24, 32** separate sections.
- **48, 64** are page-level breathing room in the Library window ONLY. The notch card is 340 to 560
  wide; a 48 gap inside it is a bug, not a decision.

8 wins the middle of the scale for a mundane reason: it survives 1x, 1.25x, 1.5x and 2x as a whole
device pixel, and this app is per-monitor DPI aware.

**Choose a step by relationship, never by eye:**

| Relationship | Step |
|---|---|
| Two parts of one atom (icon + its label, number + unit) | 4 |
| Siblings inside one group (rows, chips) | 8 |
| A heading and the content it introduces | 8 |
| Inside a card, edge to content | 16 |
| Between groups inside one card | 16 or 24 |
| Between sections of a screen | 24 or 32 |

**The doubling test.** Every gap must be at least 1.5x the gap one level below it and at most 2x, or
the hierarchy is ambiguous. 8 inside and 24 between is unambiguous; 8 inside and 12 between is not.

**Adjacent steps are the only legal neighbours.** When a value falls off the scale, the cause is
usually that you measured from the wrong edge (section 3), not that the scale is too coarse.

## 2. Vertical rhythm

**A heading belongs to the content below it.** More space above than below, roughly 2:1.

- Section heading to its first row: **8** (the app's window pages use 12 for a 14.5px title)
- Last row of a section to the next heading: **24**
- Between rows within a section: **8**
- Card bottom padding after the last row: **16**

If a heading looks equidistant between the block above and the block below, the reader has to parse
the structure instead of seeing it.

### Space belongs to one owner, and the owner is the container

The classic bug: a heading sets a bottom margin, a caption below it sets another, and when the
caption is removed the heading sits flush against its card. Nobody wrote that layout; it emerged.
**This exact bug shipped here on 2026-08-23** when every settings caption was deleted in one pass.

**Rule: no child sets an outer margin. The container distributes the gaps.** A component must be
droppable anywhere without carrying spacing baggage. Where the framework has no gap property, put
the gap in the STYLE of the heading (one definition, one edit) rather than at each call site.

Three checkable consequences:

1. Grep for a margin on a text element that is a direct child of a stack. Each hit is a candidate.
2. Collapsing any single optional child must change the total height by exactly that child's height
   plus one gap. Never by the height alone (orphan gap) and never by more (double-owned gap).
3. The bottom of the last child to the bottom of the card must equal the card's own padding, not
   padding plus a leftover child margin.

**Vertical rhythm is not a baseline grid.** Do not snap text baselines to a 4px grid; that fights
every font fallback and every DPI. Snap the GAPS to the scale, and fix the optical error once, in
the type ramp.

## 3. One gutter

**Every element in a column shares one left inset**, and it does not change as you scroll.

The failure mode is specific: a section heading at inset 0 while the rows below sit at inset 12
because the rows are inside a container with its own padding. Two left edges 12 apart read as two
different documents stapled together. Users do not notice whether a margin is 18 or 20; they notice
immediately that two edges disagree. Inconsistency is visible, absolute values are not.

This app's gutters: **notch card content 16 from the card edge; Library window 24 from the window
edge, 16 inside a card.** Nested rows may indent only when the indent carries meaning. Decoration
never indents.

### Reconciling a button's padding with the container's

A button with 12px padding placed at a 16px gutter puts its LABEL at 28. The label is what the eye
aligns, not the invisible hit box. Two legal resolutions:

1. **The button is a box** (visible fill or rim): the box aligns at the gutter and the label sits
   inside it. The reader aligns box edges.
2. **The button is bare** (transparent until hover, which describes most list rows): negative-inset
   it by its own padding so the LABEL lands on the gutter. The hover box then bleeds to the card
   edge, which is what a full-width row should do anyway.

**Never resolve it by zeroing the padding.** That shrinks the hit target.

**Borders count.** Padding is measured inside the border, so a control with a 1px rim uses
`scaleStep - rimThickness` (this is why the pill button is 15,8 and not 16,8). Write that in the
comment or the next person will "fix" the 15.

## 4. Optical versus mathematical

Layout engines centre boxes. Eyes centre mass. Where they disagree, the eye wins.

**Text in a box sits low.** A line box reserves room for ascenders and descenders most strings never
use. Measured here: about 1.7px low on button labels at 13 to 15px, enough to look sloppy in a 32px
capsule. Correct it from real font metrics, never a guess:

```
offset = -((Baseline - CapsHeight / 2) - LineSpacing / 2) * FontSize
```

Two non-negotiables: **snap the result to whole device pixels** (a transform is applied after layout
rounding, so a fractional nudge softens every glyph), and **compute per element**, because the same
button needs different numbers at 100% and 150%.

Apply it to any single line vertically centred in a fixed-height box. Do NOT apply it to multi-line
paragraphs, where the descender space is real.

**A single glyph in a circle is geometry, not text.** Centring TEXT centres a line box, and the
offset between that box and the cap ink depends on the font, the size, and whether a line height was
pinned or inherited. Two attempts here with a text label both left the letter visibly low. Draw the
glyph as a path: its layout box IS its ink, so centring the element centres the letter, at any size,
in any font, with nothing to keep in sync.

**Asymmetric glyphs need a horizontal nudge.** A play triangle centred by its bounding box always
reads left, because its mass is at the left edge. Shift it right by about a tenth of its width.
Symmetric glyphs need none; if one looks off, its artboard is off-centre, so fix the geometry.

**Icons occupy a fixed square with a live area 4 smaller.** Then an icon-to-label gap of 8 is a real
8, not "8 minus whatever slack this glyph has". Mixing a tightly cropped icon with a padded one in
one row is the most common cause of a row that will not align whatever gap you set.

**Circles overshoot.** A 20px circle beside a 20px square reads smaller; make the circle 1 to 2px
larger when they must match.

## 5. Symmetry

**Default to symmetry. Break it structurally, never accidentally.**

Symmetric by default: card and popover padding (left equals right, always; top equals bottom within
2px), a control's own padding, and anything centred (measure ink, not boxes, or a leading icon's
trim will pull the group left).

Deliberately asymmetric, with the reason written down: the notch WINDOW carries a mascot gutter left
and shadow pads right and bottom, which is structural and constant, while the CARD inside it is
symmetric. A trailing chevron may sit 2 to 4 closer than a leading icon, because its optical mass is
smaller; that is optical symmetry, not asymmetry.

**The two-hairline defect.** A rimmed element flush inside a rimmed card gives a seam carrying two
hairlines, reading as one thicker line belonging to neither. One boundary, one rim. Inner surfaces
differentiate by FILL. If an inner element must have a rim, inset it by at least 8.

**The double-border defect.** A divider drawn directly above a card's own rim, or under the last row
before the container's bottom rim. A divider never touches another line.

## 6. Grouping

Three ways to say "these belong together", in order of preference:

1. **Proximity.** 8 inside, 24 between. Free, silent, always correct.
2. **A card.** Earns its place when the group has its own background, its own affordance, or must
   survive reordering.
3. **A divider.** Last resort. Heavy divider use dilutes their impact.

A divider earns its place ONLY when two adjacent groups have the same visual weight, the same left
inset and the same background, and the gap between them cannot be increased. If you can spend 24
instead of drawing a line, spend 24.

When one is justified: 1 physical pixel, snapped; white or black at 8 to 10%, never a grey hex;
inset to the content gutter whenever rows have a leading icon; equal space above and below.

**Concentric radii, and it is not negotiable:** inner radius = outer radius minus the padding
between them. This app's ladder falls straight out of it: card 24 with 8 padding gives an inner
surface at 16; inner 16 with 4 gives a capsule at 12; a pill is height-derived. If a designer hands
you 16 inside 24 with 16 of padding, one of the three numbers is wrong, so ask which.

**Two levels of nesting maximum inside the notch card.** A third level leaves about 200px of usable
width and every gap in it will look cramped whichever step you pick.

## 7. Review checklist

1. Is every gap on the scale? More than five distinct vertical gaps on one screen means the
   hierarchy is not designed.
2. Does every element in the column start at the same x? Max minus min must be 0, or an intentional
   indent that appears more than once.
3. Is heading-to-content clearly smaller than section-to-section, by at least 2x?
4. Can any single child be collapsed without leaving an orphan gap?
5. Is card padding symmetric? Left equals right exactly; top and bottom within 2.
6. Is every centred single-line label cap-height centred, and is the offset a whole device pixel?
7. Does every icon sit in a fixed square with a live area 4 smaller?
8. Does any line touch another line? Zero permitted.
9. Does inner radius plus padding equal outer radius, for every nested rounded box?
10. Is the hover highlight the same box as the hit target?
11. Does the screen have one heading style per level, one row height per list, one gutter per column?
12. Do the answers change under the other theme? They must not.

## 8. Verifying spacing programmatically

Layout truth lives in arranged bounds, not in markup. These belong beside the existing hit-target
checks in the app's own harness.

**Get real numbers.** Realise the view offscreen and lay out TWICE (the first pass can leave
virtualised or templated children unarranged), then transform into a common ancestor. Also read the
layout clip: a clipped control has bounds that lie, and that clip is exactly how a correct 16px
inset silently becomes 9.

Four assertions, in order of how many bugs each catches:

1. **One gutter.** Collect every child's left edge in the content column and assert the spread is
   under half a pixel. Report the offender BY NAME: "SectionHeading is 12 left of Row0" is
   actionable, "left edges differ" is not.
2. **Gaps come from the scale.** Walk visible children in order, compute next.Top minus
   current.Bottom, and assert each is within 0.5 of a scale member. This catches accumulated
   margins that no single line of markup explains.
3. **The heading's bottom gap is not its own invention.** Assert the gap below every heading is the
   same constant, and strictly less than the gap above. This is the most common spacing regression
   and it never looks wrong enough to notice in a screenshot.
4. **Symmetry.** For a card, assert left inset equals right inset exactly. For a centred row,
   measure the INK of the outermost children and assert the two outer gaps match within 1.

**The orphan-gap probe**, which is the only test that catches the caption bug before a user does:
for each optional child, record the host height, collapse the child, lay out twice, and assert the
height dropped by exactly that child's height plus ONE gap. Not zero, not two.

Two cautions. Assert device pixels where a rendering claim is made and DIPs where a layout claim is,
or failures will reverse when the window moves to the other monitor. And these tests measure a model
of the product: they pass on screens the owner can see are wrong, and they have. Run them, then look
at it.

## Sources

- https://carbondesignsystem.com/elements/spacing/overview/
- https://carbondesignsystem.com/elements/2x-grid/overview/
- https://m3.material.io/foundations/layout/understanding-layout/spacing
- https://m1.material.io/style/icons.html
- https://m3.material.io/components/divider/guidelines
- https://developer.apple.com/design/human-interface-guidelines/layout
- https://alistapart.com/article/axiomatic-css-and-lobotomized-owls/
- https://blog.kizu.dev/cap-height-align/
- https://iamvdo.me/en/blog/css-font-metrics-line-height-and-vertical-align
- https://medium.com/ringcentral-ux/eyeballing-or-optical-alignment-in-design-4ef5ab2d326f
- https://cloudfour.com/thinks/the-math-behind-nesting-rounded-corners/
- https://piccalil.li/blog/relative-rounded-corners/
- https://jacobshannon.com/blog/books/refactoring-ui/layout-and-spacing/
- https://uxdesign.cc/goodbye-8-point-grid-hello-4-point-grid-1aa7f2159051
- https://www.uxpin.com/studio/blog/symmetry-vs-asymmetry-in-design/
