# Taste audit: the notch states and the Library window

*2026-08-24. Run with `.claude/skills/design-taste/SKILL.md` against freshly rendered PNGs of every
state (`--render` and `--libshot`), at the owner's request.*

**Nothing here has been changed.** This is a report. Each finding names the state, what you can see,
which rule it breaks, and what would replace it. Ordered by how much it costs the product, not by
screen.

The taste skill is a tiebreaker, not an authority: it loses to the Figma frame, to the owner, and to
contrast and hit-target floors. Several findings below are judgement calls and are marked as such.

---

## The three that cost the most

### 1. The greeting is the loudest thing on two screens, and it says nothing

**Where:** notch Home ("Welcome back, Sam!"), hub Ask ("Welcome back, Sam." at about 40px, the
largest type in the window).

**Squint test:** blur either screen and the greeting is the only thing that survives. It is also the
only element carrying no information at all. On Ask, the four suggestion chips - the actual reason
the page exists - read as secondary.

**Rule:** *"Pick the one thing the user came for, and emphasise it by de-emphasising everything
else."* Emphasis went to the safest element rather than the most useful one.

**Replacement:** demote the greeting to body size (or drop it on Ask, where the mascot already says
hello), and let the chips or the upcoming meeting be what the eye lands on. Demote, do not promote.

Related, same screens: **the exclamation mark.** "Welcome back, Sam!" on the notch. Rule 3 of how a
senior designer writes UI text is "No exclamation marks. None." The hub version already ends in a
full stop; the notch one does not. They should at least agree.

### 2. "Your meeting assistant." and "How Toki looks and what it shows in a share."

**Where:** hub Ask, under the greeting; hub Settings, under "Appearance".

**Rule:** *"No narration. Delete any sentence describing the interface itself."* This is the single
most reliable AI tell in copy. "Your meeting assistant" tells a user of a meeting assistant nothing.
"How Toki looks" restates the word "Appearance".

**Replacement:** delete both. "and what it shows in a share" is the only load-bearing half of the
Settings one, and it belongs on the "Show Toki in screen recordings" row it actually describes.

Note the accidental accuracy: Ask genuinely cannot see hand-written notes today, so "meeting
assistant" is more literally true than intended. That is a reason to fix the retrieval, not a reason
to keep the sentence.

### 3. The longest wait in the product says nothing about the wait

**Where:** notch Summarizing - "Tidying up your notes", with a moving sliver.

**Rule:** *"Specific beats friendly. 'Transcribing, about 40 seconds left' beats 'Hang tight'."*
This screen can sit for a minute or more while the local model writes. It offers no stage, no
estimate, and no evidence anything is happening beyond the sliver.

**Replacement:** name the stage and, once it is measurable, the estimate: "Transcribing", then
"Writing your notes, about 40 seconds". And if that bar is indeterminate it must not be shaped like
progress - *"a skeleton loader that fakes work is a lie."*

---

## Colour: one meaning one colour is broken in two places

The system is amber=time/counts, teal=actions, blue=people, green=decisions, pink=questions,
coral=record CTA, Apple red=Stop only.

- **Settings toggles are GREEN when on.** Green already means "decisions" in a note. A toggle's
  on-state is now a second meaning for the same colour.
- **The Summarizing progress sliver is PINK.** Pink already means "questions".

Both are defensible in isolation and neither looks wrong on screen. They are flagged because the
system is what makes the colours readable at a glance, and it has two exceptions now. Owner's call:
add "on" and "in progress" as named roles, or recolour.

---

## Alignment: two left edges on the same page

- **hub Templates:** the page title and its description start at one x; every template block
  ("General", "Standup", "1:1") starts about 30px further right. Nothing separates them - it is one
  column of content - so the page has a ragged left edge at the exact spot the eye scans down.
- **hub Ask:** the four suggestion chips are content-sized, so neither their left nor their right
  edges line up across the two rows.

**Rule:** *"Draw the left edges."* One gutter per page. See `symmetry-and-spacing`.

The note canvas had this same disease in three places; those are fixed (README, 2026-08-24).

---

## Chips and labels carrying no information

- **hub Templates:** every template lists its sections as grey pills - "Summary", "Key points",
  "Action items". They never change, every template has "Summary" and "Action items", and they are
  not clickable. *"A badge earns its place if its value CHANGES and the change matters. A constant is
  a label."* Plain text says the same with less furniture, and lets the template NAMES rank.
- **All-caps grey labels above a single control**, three instances: "UPCOMING" (notch Home), "DEFAULT
  SHAPE FOR NEW MEETINGS" (hub Templates), "STICKY NOTE" (the sticky panel). *"Labels compensating
  for a layout that does not group visually. Group with space instead."* The sticky one is arguably
  earned - it is the one panel whose content belongs to a different author - but the other two label
  a single row each.

## A list where nothing is ranked

**hub Templates** is nine identical blocks: heading, one grey line, a row of identical chips.
*"The grid removed the need to rank the content, so nothing is important."* The one fact that differs
per row - when Toki would pick this shape - sits in the grey line at the lowest emphasis on the
block. Worth considering: mark the default or auto-detected one, quieten the rest.

## Icons repeating their own labels

**notch Home:** a calendar glyph beside "Calendar", a note glyph beside "Take notes", a books glyph
beside "Library". *"An icon either REPLACES a label or adds what the word cannot carry."*

**Judgement call, and I would leave these.** Icon plus label on a primary action is a convention
users expect, the notch is glanceable-first, and the skill explicitly yields to platform convention.
Recorded so the decision is deliberate rather than accidental.

## Three actions, three treatments, one small card

**notch Home** has two equal-weight pills at the top (Calendar, Take notes) and a third action
(Library) floating bottom-right in a different style and position. No primary. Either Library joins
the pair, or one of the three earns the accent and the others go quiet.

---

## What is genuinely good (do not "fix" these)

Named so a future pass does not undo them:

- **notch Notes:** "Delete" quiet, "Save to Library" black and dominant. One accent, correct target,
  clear hierarchy. The best-ranked screen in the product.
- **notch RecordingOpen:** a clock, a level, a red stop, a chevron. Nothing else. Restraint working.
- **notch MeetingPrompt:** one action and a dismiss, and nothing narrating either.
- **The empty state on Home** ("Connect a calendar" with a chevron) names the thing, says how to make
  one, and offers the action - exactly what the skill asks of an empty state, and it is the first
  screen most users will see.
- **Settings helper text "One per line, or comma separated."** is a FORMAT, which is what helper text
  is for. Keep.
- **The Library list rows** rank properly: date, then title, then quiet metadata, then counts. Four
  levels, used consistently.

---

## Not audited, and this is the gap

Focus rings, error states, disabled states and long-data behaviour were not checked - the harness
photographs the happy path in both themes and nothing else. *"Missing states"* is listed in the skill
as "the tell that survives a screenshot review and fails a real user", so this is the hole in the
audit itself, and the honest next step if the owner wants it taken further.
