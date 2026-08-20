# The ghost frame

The corner flicker outlived two rounds of fixes for a good reason: the last cause was not in our
code at all. It was Windows showing you a photograph of the past.

## What the flicker actually was

When the notch hides, it plays a closing animation and then tells Windows "hide me". The bug: it
said "hide me" the moment the animation's STOPWATCH finished - which is one frame before the
final closed image was actually drawn. And windows of this special transparent kind have a quirk:
Windows keeps their last drawn picture. So the operating system kept a little snapshot of the
card mid-close - a small stub with rounded corners - and the next time the notch appeared,
Windows briefly showed that stale snapshot before the first new frame arrived. That stub is the
corner you kept seeing. It looked white in the light theme and dark-with-a-bright-rim in the dark
theme because the snapshot simply replays whatever it held - which is exactly why switching
themes never helped.

The fix: before hiding, the notch now waits until it has PROVEN two real frames have been drawn
(not just waited a moment - an earlier attempt that merely waited politely was filmed still
flashing), so the snapshot Windows keeps is an empty one. Nothing to replay, nothing to flicker.
The entrance self-test now checks this too, so a stale snapshot can never quietly come back.

A second, smaller stutter fell out of the same filming: when expanding, the notch would switch to
the new look and THEN pause a tenth of a second working out how tall the new page needed to be -
showing a bare card with nobody's content on it. It now does all its measuring first and only
then touches the screen.

## Toki's flight

The notes-ready ending is now the animation you asked for. When "Notes ready" goes unclicked,
Toki's bubble dissolves under him, he hops out, does a single mid-air somersault while shrinking
down, and flies up to the top edge, landing exactly where the peeking Toki rests - the pill
folding away beneath him as he goes. Click him up there and your notes open.

Getting the landing clean took three filmed attempts: the normal fold-away animation resurrected
the white pill behind him (cut), skipping the card's grain layer left a ghostly rimmed rectangle
(cut), and reading the window's position mid-move landed him 30 pixels off target (computed
instead). The final film shows: hop, somersault, arc, landing, no artifacts.

## How it was checked

2,051 tests pass (7 new, almost all first proven to catch their bug against the broken code), the
entrance/click/height/contrast self-tests pass on both the development build and the shipped
file, and both behaviours are on film frame by frame. One practical note: your running Toki was
closed during testing (after checking no recording was live), so launch it again - what you
launch is the fixed build.
