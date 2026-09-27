# The numbers behind "premium", and where Toki's own card breaks them

Researched 2026-09-26/27 after the owner said Toki read as "a cheap AI slop product". Two threads:
a teardown of Granola and Wispr Flow against the design systems that publish real values, and a
survey of eleven notetakers on whether a meetings Library earns its place.

This file is REFERENCE, not opinion. Every number has a source. Read it before proposing a type
scale, a palette value, a chip, or an empty state.

**The most important thing in here is the last section.** Three published values that pass in every
other product FAILED in Toki, all for the same reason, and that reason is structural.

---

## 1. What "cheap" actually was, in measurements

The owner was not describing taste. Five defects, four of them one line each:

| Defect | Measured | What the platform says |
|---|---|---|
| One font cut for every size | `Segoe UI Variable Display` used 9.5px to 34px | Microsoft assigns **Small <=12, Text 14-18, Display >=20**. Display is the optical cut for LARGE text: thin strokes, tight counters. Body set in it is illegible-adjacent at 12px. |
| Fractional glyph positions | `TextOptions.TextFormattingMode` never set anywhere -> WPF default `Ideal` | WPF text team: at small sizes Ideal's benefits "are negated by blurry rendering"; `Display` snaps to whole pixels. Ideal only wins **above ~15pt**. |
| No measure cap | note body ran **118-148 characters per line** | Microsoft's own typography page: **"Keep to 50-60 letters per line"**, and "don't... more than 60". Bringhurst's ideal is 66; WCAG 1.4.8 caps at 80. |
| Colour per section | headings in **four hues** + a tinted chip per action + an amber count pill | **A Granola note uses ZERO colour.** |
| Two colour keys in one theme | light surfaces COOL (R-B -2 to -5) beside warm glass (+5) | Every warm-key reference is consistently warm: Granola canvas `#F7F7F2` (+5), Notion page `rgb(247,246,243)` (+4), Wispr `#E4E4D0` (+20). |

Conversion for measure: `px = CPL x 0.5 x font-size`. At 15px, 60ch = **450px**, 66ch = **495px**.
A cap of **520-560px** satisfies every source.

---

## 2. Granola's note UI, from real screens

- **Ground is not white.** Warm grey-green canvas; the note is a LIGHTER warm off-white card with
  ~20-24px radius and real margins. Tokens: canvas `#f7f7f2`, surface-1 `#eaebe5`, raised `#ffffff`.
- **Title** is an editorial serif at **regular weight 400**, ~28-32px. Not bold, not caps.
- **Body** ~15-16px / ~1.45-1.5, near-black `#292929`; ink ramp `#292929 -> #454745 -> #72726e`.
- **Section headings are body-size SEMIBOLD with a colon.** No size jump, no colour, no divider, no
  all-caps. Body starts on the very next line.
- **Action items are plain bullets under a bold heading.** No checkboxes, no owner chips, no due
  pills, no priority labels.
- **Dividers: none.** Sections separated by whitespace only.
- The one deliberate colour device is VALUE-based, not hue-based: your notes stay black, AI
  additions render grey.

## 3. Wispr Flow

Figtree body at **14/16/20/22/24/32px, line-height 1.3 at every size**; warm palette (`#ffffeb`,
`#e4e4d0`, fog `#8a8a80`, ink `#1a1a1a`); radius cards 32 / buttons 12; 8px spacing base.
**"This design is deliberately shadowless."** Pure-white cards, no border, no shadow - the card edge
alone separates. Transcript rows use **inset** hairlines that start at the text edge, not full-bleed.
**One accent (green)**, spent on the toggle, one hero number, and three action buttons on reveal.

---

## 4. Light theme: what actually reads as premium

"Pure white is cheap" is **half wrong** - Apple ships literal `#FFFFFF`/`#000000` in light mode and
Notion's page is `#fff`. What premium light UIs really do differently:

1. **White is the CARD, not the canvas.** Apple grouped lists: page `#F2F2F7`, rows `#FFFFFF`.
   Carbon: "layers alternate between White and Gray 10 with each added layer." A reading pane that
   is the same lightness as its ground does not read as a page - it reads as one white field, and
   that flatness is a large part of "there's just so much text".
2. **The muted ramp is hue-TINTED, never black-alpha.** Apple `rgba(60,60,67, .60/.30/.18)`; Notion
   `rgba(55,53,47, .8/.6/.4/.16)`; Primer `#59636e`. The tint is tiny - a 4-8 point delta between
   extreme channels. Black-alpha only composites correctly over one background and loses the hue.
3. **Region separation, in the published order of preference:** whitespace -> background tint ->
   hairline -> shadow. Material 3 demoted shadows for tonal surfaces. NN/g: whitespace alone
   "reduces the visual complexity", and excessive borders create **"false floors" that discourage
   scrolling**.
4. **Hairline band, light mode** (everyone converges): alpha **0.06-0.16**, or opaque `#D9`-`#EB`.
   Radix step 6 `#d9d9d9`, Apple separator `#C6C6C8`, Notion `rgba(55,53,47,0.16)`, Geist
   `rgba(0,0,0,0.08)`. **The line COLOUR is rarely the bug. The COUNT and the full-bleed width are.**
   Material 3, verbatim: dividers only "if elements cannot be separated using white space".
   Apple has inset row separators to the leading text edge **by default since iOS 16**.
5. **Shadow recipe:** 2-4 layers at **3-12% alpha each** (never one 20%+ layer), and the colour a
   **dark TINTED neutral** - Primer keys to `#1f2328`, Stripe to navy, Notion to `rgb(25,25,25)`.
   Pure black on a warm surface greys it instead of darkening it.
6. **Text tiers: TWO is the modal published answer** (Material, Primer, Radix all ship exactly two);
   three is the ceiling. Primary L* 10-20, secondary L* 40-48, and the jump between them is
   **>=25 L\*** in every system.

---

## 5. Typography

- **Line-height:** Butterick 120-145%; WCAG AAA >=1.5; 1.5-1.6 for wide body, 1.3-1.45 for narrow
  columns, **headings ~1.1**, UI/short text 1.2-1.3. Tailwind `prose` base is 1rem/1.75.
- **Scale:** 4-5 sizes is the convergent answer for a dense reading surface. Primer ships **6 for
  all of GitHub**; Fluent **9 for all of Windows**, only 3 at or below 18px. Refactoring UI's scale:
  12, 14, 16, 18, 20, 24, 30, 36, 48 - no two adjacent closer than ~25%.
- **Hierarchy:** roughly three text colours and two weights. **Never de-emphasise with a lighter
  weight** - use colour or size. **Windows 11 excludes Bold from the ramp entirely: "Use Semibold
  instead of Bold for emphasis."** M3 encodes the same - Title vs Body differ at the SAME size by
  weight alone.
- **Windows floors:** "14px Semibold, 12px Regular" is the legibility floor. Sentence case for all
  UI text including titles.
- **Tracking:** negative only at display sizes, zero at body, never letterspace lowercase body.
  Microsoft publishes no tracking for Segoe - the `opsz` axis is meant to do that job.
- **Half-pixel sizes are a real cost.** They contribute directly to blurry stems under `Ideal`.

## 6. "Too much text", with numbers

- NN/g, 45,237 page views: users read **at most 28% of words**; reading time ~= "25 seconds plus 4.4
  seconds per 100 words"; "users read approximately half the information only on pages with **111
  words or fewer**." **A meeting note meant to be READ rather than skimmed is a ~100-150 word
  artifact.** That is a product number - the real fix for "so much text" is partly upstream in the
  note writer, not only in the renderer.
- **Bullets: 3-7 per section.** Microsoft style guide: "at least two items but no more than seven",
  each short enough that "the reader should be able to see at least two, and preferably three, list
  items at a glance".
- Progressive disclosure: "designs beyond 2 disclosure levels typically have low usability."
  Apple: "Use no more than one disclosure button in a single view."
- Vertical rhythm (Tailwind `prose` at 16px): items 8px, paragraphs 20px, section 32px.
  **Ratio 1 : 2.5 : 4.**
- Perceived density is "highly subjective - arrangement and organization affect perception more than
  actual element count", which is why fixing measure, one type ramp and one accent makes the same
  word count feel shorter.

## 7. Chips and badges: the decision rule

**Across every product examined, an outstanding-item count is a plain muted numeral. Not one uses a
coloured pill.** Verbatim: `In Progress 5` (Linear), `Overdue 4` (Todoist), `Do today 1` (Asana),
`Action items 5` (Calendly), `Today 3` (Attio).

The instructive exception: **Things 3 shows a grey `5` beside a red circular `2`** - colour spent
only on the subset actually at risk. Cultured Code files "a red badge when it contained no items
with deadlines" as a **bug**.

Take the FIRST treatment that is sufficient:

| Treatment | When |
|---|---|
| **Nothing** | the value is the default / unremarkable / derivable from context |
| **Plain muted numeral** beside the noun | any count of outstanding items |
| **Glyph** | metadata that must be known to EXIST more than to be read |
| **Inline muted text** | a value the user must actually read: date, owner, project |
| **Coloured text, no box** | the value crossed a threshold that changes what to do (overdue) |
| **Pill** | only if ALL of: user-chosen category, in a column where pills align, interactive or a true status, AND tinted not saturated |

Rules a coloured count pill breaks: Apple HIG ("Don't use a badge to convey numeric information that
isn't related to notifications"), Atlassian (badge/tag/lozenge "have distinct semantic jobs"),
GitLab ("Match the variant to the meaning of the metadata, not to how much attention you want to
attract" - and "open" is the DEFAULT state, least deserving of colour), and the pill-means-clickable
convention. Adobe Spectrum: "If your design requires multiple badges, consider using regular text
metadata and reserve a single badge for only the most important status."

**In a notes DOCUMENT the evidence is one-sided: action items are prose.** Granola's own spec is a
sentence - "Pedro will update the pitch deck before Thursday's partner meeting" - owner as
grammatical subject, deadline as a prepositional phrase. A chip mid-sentence forces a reading-mode
switch from prose to form-scanning on every line, and with no column to align to, twenty chips at
twenty x-positions make a speckled page.

---

## 8. Does a meetings Library earn its place? Yes. Don't demote it.

The owner's instinct was that nobody re-reads past notes. **The best measurement contradicts it.**

Whittaker et al., "Am I wasting my time organizing email?", CHI 2011, IBM Research: 345 users, 85,000
logged refinding actions. Percent of all access operations:

- **Scrolling: 62%**
- Search: 18%
- Folder access: 12%
- Sorting: 7%
- Tag access: 1%

Their own design implication: "current search-based clients ignore scrolling, the most prevalent
refinding behavior". Search *was* most correlated with success (r=0.15, p<0.005), so it belongs one
keystroke away - but it is not what people reach for.

Supporting: Teevan et al. CHI 2004 on orienteering - "most of the search behavior we observed did
not involve keyword search... **even when they knew exactly what they were looking for in advance**."
Bergman et al. JASIST 2013 - tags retrieve slower and less successfully than folders.

**Eleven notetakers surveyed (Granola, Fathom, Circleback, Spinach, Supernormal, Otter, Fireflies,
tl;dv, Read.ai, Avoma, Krisp): every one keeps a full chronological archive. Zero deprecated one.**
Read.ai moved the opposite way in Sept 2025, making its list MORE prominent and defaulting the date
filter to "anytime".

Three transferable patterns:
- **Landing screens are calendar-first, not archive-first.** Granola puts "Coming up" above "Your
  notes". Circleback's Home is upcoming meetings. *The next meeting above the past ones.*
- **The always-on surface is never the archive.** Fathom's desktop app is a **top-center bar**;
  Supernormal "lives in your tray". **Fathom ships literally Toki's architecture** - hovering bar for
  the live moment, archive as a separate destination. Toki's notch-plus-window split is validated.
- **Chat is promoted into the nav, never in place of the list.** Supernormal explicitly demoted it:
  "Notes take center stage... Chat stays accessible on the right" (changelog, 15 Jan 2026).

**Mem is the one real deprecation, and it was not about chronology:** they killed their Inbox because
it was "a point of added complexity", and their People view because it "led to much confusion on
whether to use notes, collections, or a 'person' page". **The lesson is: do not ship two rival entry
points into the same content.**

**Things 3 is the best frame:** the landing surface should be a *computed relevance* list, not an
inventory. "Today is the list for to-dos that you want to start before the day ends." Recency is a
cheap proxy for relevance, not the goal.

### Empty states

No surveyed notetaker opens directly into the newest note's full text. Apple's shipped shape is
`ContentUnavailableView`: **a short noun-phrase label plus one forward-looking sentence**, action
optional. Nothing-selected panes converge on one muted line: Apple Mail `No Message Selected`;
Obvious `Select a note to view`; Aboard and Tines show **nothing at all**.

The warning case is **Notion AI Meeting Notes**: on an empty recording it writes the user a letter
*in the note body, under the real meeting title*. The failure is that **the empty state impersonates
the artifact** - same title, same typography, same slot, so nothing-happened looks like a successful
note about nothing.

### Recordings

Granola **stores no audio at all**. Circleback, Otter, Krisp and tl;dv all ship explicit retention
or deletion controls. **Every one treats the recording as the deletable layer and the note as the
durable one, and none of them hides the list.** Toki matches the Granola position (audio reachable
only as inline evidence clips) but **ships no retention policy at all**, which every comparable has.

---

## 9. THE PART THAT IS TOKI-SPECIFIC, AND THE REASON TO READ THIS FILE

**Every published palette value assumes an OPAQUE surface. Toki's card is `Glass` at 85%.**

So what any colour sits on depends on the user's wallpaper, and the worst case - the light card over
a BLACK desktop - sets a contrast ceiling the references never had to clear. Three recommended
values were adopted, measured, and rejected on exactly that case:

| Value | Where it comes from | Measured here |
|---|---|---|
| Amber light `#B45309`, then `#A04B06` | a saturated amber that clears 4.5:1 on every opaque surface | **3.79:1** both times. Settled at `#87430A`. |
| `TextDim` light `#646464` | where Radix, Geist, Primer AND Material independently landed for secondary text | **3.74:1**. The real ceiling is ~`#575757`; the existing `#56565A` was already on it. |
| Neutral dark `Glass` `#1F1F21` | replacing a warm grey at the same lightness | put Amber at **4.50:1** and Blue at **4.49:1** against a 4.5 bar, purely for being a fraction lighter. Darkened to `#1D1D1F`. |

`ContrastTests.EveryTextColourSurvivesTheTranslucentCardOverAnyWallpaper` caught all three.

**Before proposing any colour from any reference in this file, check it against THAT test, not
against the opaque surfaces.** And note the corollary: contrast is necessary and not sufficient.
`#794F1A` cleared 4.5:1 everywhere and read as a brown stain - the axis that was wrong there was
SATURATION, which no contrast test measures.

---

## 10. Where the references contradict Toki's house style

| Reference | House style | Verdict |
|---|---|---|
| Granola and Wispr both use an **editorial serif** for display type | Segoe UI Variable only | **Do not adopt** (no licensed serif). **Do adopt the free half:** their titles are REGULAR/semibold weight, and Windows 11 removes Bold from the ramp. |
| Wispr is **deliberately shadowless**, 2px solid borders, 32px radii | warm glass, radius 24, 1px rim | **Do not adopt.** Radius 24 is inside the reference band (Granola note card ~20-24). |
| House rule: "text as WHITE OPACITIES (90/50/25)" | - | **This is a DARK-theme rule the light theme inherited as black opacities, and that is the defect.** Every premium light system tints. The house style yields here, and only on the light side. |
| Apple's `secondaryLabel` is **~3.4:1 and fails WCAG**, leaning on APCA | Toki measures every token to >=4.5:1 | **Keep the stricter house rule.** |
| Material 3 demoted shadows for tonal elevation | `design-taste`: spacing first, tint second, hairline third, shadow last | **No conflict - they agree. The CODE is what disagreed with both.** |

## 11. Two honesty notes carried forward

- **Linear's light-theme hex is not vendor-published.** Values circulating come from scraper sites.
  Linear's *method* (LCH, 3 variables, deliberate cool-to-warm shift) is primary-sourced and is the
  more useful half.
- A widely-quoted "NN/g January 2026 study finding hairline borders read as more authoritative than
  shadows by a factor of 1.6" **appears to be fabricated** and surfaces only on AI-generated design
  blogs. Do not cite it. Likewise: **nobody in the notetaker category publishes note-revisit rates
  or command-palette usage data.** Any such figure you meet is unsourced.
