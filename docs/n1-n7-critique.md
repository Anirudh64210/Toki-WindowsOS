# N1–N7 — an honest critique

Written 2026-08-05, immediately after building the hand-written-notes batch and **before** anyone had used
it in a real meeting. This is my own assessment of what's weak, missing, or likely to break — not a
changelog. The build log is in README "N1–N7"; the reasoning is in ARCHITECTURE "N1–N7 decisions".

Ordered by how much I'd expect each to actually bite. **P1 = likely to be noticed and to hurt.**

> **Status update, 2026-08-06 (after the S1–S7 rework).** The owner's first real test found the one thing
> this document missed entirely — see "What this critique got wrong" at the bottom. Since then:
> **#1 (action items have no evidence links) — FIXED** (S7). **#2 (editing a prep card breaks its live
> tasks) — still open**, and now *less* harmful than described, because the sticky note is explicitly the
> user's own page rather than a live view of Toki's data; the STILL OPEN checkboxes in read mode still
> write through, and the copies inside the note are plainly a copy. **N1's nested-list data loss —
> resolved by removal**: Tab no longer nests, because the whole native-`List` structure is gone.
> **N6 (typed notes steer the summary) — REMOVED by the owner**, so every critique point about steering
> quality, prompt dilution and the 4B model ignoring the constraint is now moot.

---

## The two things I'd fix first

### 1. Action items have no evidence links at all — P1, and it's the wrong omission
N7 wired the `↳` into section bullets only. Action items render through `DocAction`, which I never
touched. So the one kind of line a person is *most* likely to want to verify — "did they really say I'd
own this?" — is the one kind with no link. The owner noticed within a day. Same for the SUMMARY paragraph,
though that one is defensible (a summary is synthesised from the whole meeting; pointing it at one line
would be a lie). Fix: extend `WithEvidence` to action items, matching against `a.Text` plus `a.Owner`.

### 2. Editing a prep card silently breaks its live tasks — P1, a real regression
The read-only prep card's STILL OPEN checkboxes write through to the store (`App.Store.SetActionDone`), so
ticking one there actually closes the task. The moment you press **Edit**, those items become plain
Markdown `- [ ]` lines in a scratchpad. Ticking them now does nothing to the real `ActionItem` records.
The user cannot tell: they look identical. Either the editor needs to carry an id per line (and write
through), or the STILL OPEN block must stay a live, non-editable region above the editable area. As built,
the feature quietly downgrades something that used to work.

---

## Per-milestone

### N1 — the note editor
- **Nested lists lose their indentation on save — P1, actual data loss.** Tab creates a nested WPF `List`;
  `NoteMarkdown.Write` flattens every level to one. Type an indented sub-list, reopen, it's flat. Either
  serialise indent depth (`  - item`) or stop Tab from nesting.
- **Pasting from a browser looks rich, then isn't — P2.** Paste keeps bold/colour/size on screen because
  `RichTextBox` accepts it; the serializer drops all of it on save. The user sees formatting appear and
  then vanish, which reads as a bug even though "no fonts/colours" is the intended rule. Fix: intercept
  paste and insert plain text.
- **Undo dies on template insert — P2.** `InsertTemplate` sets `Markdown`, which rebuilds the whole
  `FlowDocument` and throws away the undo stack (and the caret, and the scroll position). Should insert at
  the caret as document operations instead.
- **No keyboard shortcuts — P3.** Everything is toolbar-only. Ctrl+1/2/3 for sizes and something for
  to-do would matter to anyone typing during a live call, which is exactly the use case.
- **Nested scrolling on the prep card — P3.** The editor has `MaxHeight`, inside a card that also scrolls.
  Two scrollbars in a 356px-wide surface is unpleasant.
- **`Compact` hides the sizes behind "Aa" — P3.** Discoverable only by clicking. Acceptable, but it's the
  one place the notch version is meaningfully worse than the Library one.

### N2 — Library manual notes
- Thin, and that's fine. The only real gap: **no "last edited" anywhere**, so a note you half-wrote three
  days ago looks identical to one from ten minutes ago.

### N3 — the editable prep card
- **Once edited, the card stops updating — P2.** The generated content is frozen into text at the moment
  you press Edit. If the underlying meeting changes (an item gets closed elsewhere, the agenda is edited
  in Google Calendar), the card never reflects it, and there's no indication it's stale.
- **No way back to the generated version — P3.** No "reset to Toki's version". Once you've edited, the
  original is gone.

### N4 — surfacing
- **Nothing tells you a card is armed — P1 for the product, by design for now.** The auto-surface was the
  de-facto "your meeting is about to start" signal. Removing it removed the reminder as well as the
  interruption. The peek looks identical whether or not a card is waiting. Cheapest honest fix: tint the
  peek amber when armed. Tracked as task #58.
- **The Prep card button only evaluates on `Loaded` — P2, a real bug.** If a card arms *while* the
  recording page is already open, the button never appears until the view is rebuilt. Should react to
  the arming, or re-check on a timer.
- **An armed card hijacks the hover — P2.** While armed, hovering the peek always opens the prep card;
  Home is one extra click away via Back. Fine for five minutes before a meeting, wrong if a card stays
  armed for an hour. Consider only hijacking inside the lead window.
- **`PrepWatcher.Armed` is just "CurrentPrep is not null" — P3.** A demo card armed from the tray hijacks
  hover until its sample event's end time passes. Harmless, but sloppy.

### N5 — Keep / Discard
- **A crash before Stop loses the offer — P2.** `Awaiting` is only set in `StopRecording`. If Toki dies
  mid-meeting, the text is safely on disk but nothing ever offers it back, so it's invisible forever
  (see #59). The startup recovery sweep should also flag orphaned scratch entries.
- **Pending cards vanish while searching — P2.** Deliberate (they aren't indexed), but a user who types
  in the search box and sees the card disappear will read that as data loss.
- **No undo after Discard — P3.** Two-step confirm, then it's gone. For hand-typed text, a 10-second undo
  would be kinder than a confirmation.
- **Keep produces an orphan — P3.** The kept note has the meeting's title but no link to the meeting
  record, so from the note you can't get to the meeting or vice versa.

### N6 — typed-notes steering
- **The same text can end up in two places — P2.** It's merged into the meeting note *and* offered as a
  separate Keep note, with no dedup and nothing telling the user that's what's happening. Keeping both
  gives you the content twice.
- **Untested against a real model — P1 for confidence.** Every test asserts the prompt is *built*
  correctly and that the fallbacks work. Nothing verifies a real Qwen3-4B actually honours "cover EVERY
  point". A 4-bit 4B model may well ignore it under a long transcript. This needs one real meeting and a
  read-through, and it's the single biggest unknown in the batch.
- **Silent truncation at 1500 chars — P3.** A heavy note-taker's page gets cut with no indication.
- **Regeneration loses it — P2.** Re-writing a note with a different template drops the steering entirely,
  because a meeting record doesn't store its calendar event id. Tracked with #59.

### N7 — evidence links
- **The floor is untuned — P1 for quality.** Tested against clean, hand-written transcripts. Real ASR
  output has mis-hearings, filler and broken sentences, so real coverage is probably lower than my tests
  suggest. Needs one real meeting to calibrate; the dial is `Floor` / `MinShared` in `Evidence.cs`.
- **Word overlap can't see synonyms — P2, structural.** A bullet that paraphrases ("blockers" → "what's
  holding us up") won't match. This is precisely what the blocked on-device-embeddings work would fix, so
  it stays a known ceiling rather than something to patch with a bigger stopword list.
- **One bullet, one link — P3.** A point synthesised from three separate moments points at whichever
  scored highest, implying a single source that doesn't exist.
- **"No link" is indistinguishable from "feature broken" — P2.** There's no legend and no hint. A user
  seeing arrows on three of five bullets can't tell whether Toki is being honest or failing.
- **Hyperlink vs drag-select — P3.** The inline `Hyperlink` sits inside a document whose whole selling
  point is continuous drag-selection. Worth checking that dragging *through* a link still selects cleanly.
- **No audio.** By owner choice at the time, now requested — the WAVs, timestamps and NAudio are all
  already there, so it's a small build. Tracked as its own task.

---

## Cross-cutting

- **Nothing here has met a real meeting.** 56 new tests, all deterministic; every one of them tests my
  assumptions, not reality. The riskiest assumptions are (a) that a 4B model honours the typed-notes
  constraint and (b) that evidence links resolve often enough on real ASR output to feel useful.
- **No way to measure either.** Toki has no telemetry by design, which is right — but it means the two
  tunable numbers in this batch can only be set by the owner using it and reporting back. A local-only
  debug flag that prints match scores would make that conversation concrete instead of impressionistic.
- **The scratch file still grows forever** and isn't in the Markdown mirror, so hand-typed notes are the
  one kind of note that *isn't* portable — the exact promise the Markdown decision was made to protect.
  This is the sharpest inconsistency in the batch (#59).

---

## What this critique got wrong (added 2026-08-06)

Worth recording, because the miss is more instructive than the hits.

**I reviewed the text format and never drove the control.** This document lists eleven things about the
note editor and does not contain the bug the owner hit within a day of using it: typing `- ` at the start
of a line saved a marker *on top of* the one the block kind already implies, so bullets multiplied on every
save/reload. Twenty-three round-trip tests over `NoteMarkdown` were all green throughout, because the fault
was never in the format — it was in the document structure, where a paragraph inside a WPF `ListItem`
wasn't a child of `Document.Blocks` and the list's marker lived outside the text model entirely.

Two lessons, both already in the project's own rules and both skipped here:

- *"A self-test that never produced its real artifact is not a test."* The real artifact for an editor is
  what comes out after somebody types. `--notetest` now does exactly that, and found three defects in its
  first run.
- I critiqued the layer I had just written tests for, which is the layer I already understood. The
  untested layer is where the bugs were. **Critique should start at whatever has no test, not at whatever
  is easiest to reason about.**

**I also over-weighted N6.** Several paragraphs here worry about how well a 4B model would honour the
typed-notes constraint — tuning questions for a feature the owner removed outright, because a summary
that reshapes itself around your private notes can't be checked against the room. The product question
("should this exist?") outranked every implementation question I raised about it, and I never asked it.
