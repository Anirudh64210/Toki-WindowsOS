# The details pass

You came back from actually using the glass build with a list: buttons cut off, a lipstick-red stop
button, a glitching corner when Toki first appears, a loading screen floating in the middle of the
screen and refusing to minimise, a peeking Toki with its eyes chopped off, and note-list colour
bars that look random. Here is what each one turned out to be, in plain language.

## The cut-off buttons

The chevron button in the recording bar is a 26-pixel circle that was sitting in a row only 24
pixels tall. Windows does not complain about that; it silently slices off whatever does not fit, so
the bottom 2 pixels of the circle shipped flat, shadow and all. The row got its 2 pixels back (the
bar is still exactly the same height), and the button-checking test now FAILS the build if any
clickable thing is being sliced by its container, so this cannot quietly happen again.

The play button's triangle was off-centre for a sneakier reason: it was not a drawn triangle at
all, it was the CHARACTER "▶" from a font, nudged into place by hand. Font characters carry
invisible spacing that shifts with the font and your display scaling, so they can never be reliably
centred in a tiny circle. The triangle, the stop square and the little ✕ are now actually drawn
shapes, centred the way the eye wants them (a play triangle sits a hair right of true centre,
because its visual weight leans left).

One more invisible one: at 125% display scaling, a 10-pixel shape in a 24-pixel circle leaves
8.75 hardware pixels of margin on each side. Windows used to round that unevenly, shoving the shape
up and left. Those small glyphs now centre between hardware pixels instead of snapping.

## The stop button's red

You called it lipstick red, and you were exactly right: the button was painted a crimson-pink from
an old design file, and the colour the theme CALLED stop-red was literally Apple's pink, which
nothing even used. The stop button is now Apple's actual system red, the one you know from every
iPhone: `#FF453A` in dark mode, `#FF3B30` in light. One red, defined once, used everywhere.

## The random colour bars in the Library

Each meeting card's little coloured bar was picked by the meeting's database number: divide by six,
take the remainder, pick a colour. Pure roulette. Now the bar means something: blue for recorded
meetings, purple for notes you wrote yourself. If you would rather have every bar the same single
colour, that is a one-line change - say the word.

## The corner that glitched before Toki appeared

This was two bugs wearing one trench coat. First: when the notch folds down to the little peeking
Toki, the big bubble Toki was supposed to hand over - but its dark disc stayed on screen for a
quarter second NEXT TO the peeking Toki, then vanished in a single frame. That one-frame
disappearance is the "corner popping off" you saw. It now fades out gently instead. Second: the
very first time the notch expands, it was building the page and preparing Toki's artwork WHILE the
animation was already running, so the animation's opening moments were eaten by the stall. All of
that preparation now happens quietly at launch, before you ever summon the notch.

## The loading screen that floated and would not minimise

The transcribing loader was treated like a normal hovering page, 12 pixels below the top edge. It
is a status strip, so it now sits flush with the top of the screen exactly like recording does.

And the not-minimising had a real cause: the loader auto-tucks on a timer, but that timer fired
exactly once - and if your mouse happened to be over the loader at that instant, it died silently
and the loader stayed open forever. The timer now politely re-arms and tries again, and the loader
tucks after 2 seconds instead of 9.

## The peeking Toki

Measured precisely: the top 3 pixels of its eyes were above the card's edge. It is now a touch
smaller and sits a little lower, so both eyes are fully visible with air above them. And the flip
you asked to be visible fun: the old one did two fast bounces, which at that tiny size reads as
jitter. The new one is a single unhurried turn with one soft settle, about half a second - slow
enough to enjoy, fast enough to never hold you up.

## How it was checked

Two audit agents found the causes with fresh screenshots and code reading before anything was
touched; two build agents fixed all thirteen; a third audit re-verified every fix on the finished
code. 2,035 tests pass, and every new test was deliberately run against the UNFIXED code first to
prove it catches the bug. The mascot fade, the eyes, the flush loader and the 2-second tuck were
all confirmed by filming the real app frame by frame. The one thing no test could film: the flip
as it happens on a genuine recording start mid-meeting - that one is yours to enjoy live.
