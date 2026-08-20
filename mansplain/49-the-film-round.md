# The film round

You filmed your screen, and that recording solved in one evening what reasoning had been circling
for days. Here is what the film showed, in plain language.

## The white flicker, caught in the act

Scrubbing your recording frame by frame found the flicker at second 3.3: for a few frames the
screen shows ONLY the meeting prompt's white "record?" pill and its close button, floating alone
over your desktop. No dark card behind them, no Toki. That is the white flash.

Two things caused it. First, the little peeking state had a list of destinations it knows how to
"bloom" into smoothly - and the meeting prompt was not on the list, so arriving there used the
blunt path: snap to full size instantly, fade the contents in, mascot arrives late. The peek now
blooms into everything. Second, a background "grow to fit the content" step was firing a moment
too early - before the window had actually switched to the new page - so it did the animation
while the old page was still up, and the real transition arrived with nothing left to animate.
It now checks it is growing the page that is actually on screen.

## Why the loader really never tucked

This one is embarrassing and satisfying in equal measure: the tuck WAS happening - and then being
undone. The fancy corner-curve work from the glass build re-applies the card's rounded clipping
once things settle, and it asked the window "how big are you?" instead of "how big is the card?".
For the tucked sliver those are different answers (the window stays tall because Toki's bubble
needs room), so settling quietly un-clipped the sliver back into a full card. On top of that,
Toki himself and his "Tidying up.." speech bubble live OUTSIDE the card's clipping entirely, so
they stood on screen through the whole transcription no matter what the card did.

Both fixed. And the rhythm is now the one you described:

- While transcribing, all you see is the thin shimmering line at the top of the screen.
- Resting your cursor on it for a third of a second pops the loader down. A drive-by graze does
  nothing - that pause is how it knows you meant it.
- Move away and it slips back up half a second later. No timers deciding for you.
- When the notes are ready, Toki pops up in his bubble next to a "Notes ready" pill for 2
  seconds. Click it and your notes open. Ignore it and everything folds back to the quiet peek -
  the notes stay one hover away. (The 2 seconds is a single number in the code; if it feels too
  quick to catch, say so and it changes in one line.)

## The chevron's lean, measured

At 125% display scaling, the chevron's margins inside its circle come out to 9.375 hardware
pixels per side - not a whole number, so Windows gave one side 9 and the other 10. That single
hardware pixel was the leftward lean you kept seeing. The glyph now centres between hardware
pixels, like the other small glyphs already did.

## The Y that sat on the ceiling

The speaker discs turned out to be sabotaged from a distance: in WPF, a paragraph's line spacing
quietly INHERITS into everything inside it, including the little letter inside the avatar circle.
The transcript's tall line spacing was inflating the letter's invisible text box until the "Y"
pinned itself to the top of the disc, exactly as your screenshot showed. A test rig reproduced
your screenshot to the half-pixel, and both avatar circles (note page and live transcript) now
use one shared recipe that pins the line box to the letter and centres the ink optically.

## How it was checked

One agent this round, an hour start to finish. 2,044 tests pass (9 new, each first proven to
catch the bug by running it against the broken code), and the fixed behaviours were all filmed:
the bloom with no white frame, the sliver ignoring a graze and tucking when you leave, the
2-second announce folding back to the peek. The packaged app was rebuilt, so what you launch is
what was filmed.
