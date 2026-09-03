# The wire that was never connected

When Toki writes a bullet in your notes, you can click it and hear the moment it came from. For that
to work, each bullet has to remember which part of the meeting it was written from.

Toki had every piece needed to do that. It had the step that works out the answer. It had the place
to put the answer. It had the column in the database to store it, the code to save it, the code to
load it, and the code in the notes page that reads it back and uses it.

It never once wrote the answer down.

## How something can be finished and still do nothing

The best way I can put it: imagine a house wired for a light. There is a switch on the wall, there is
a bulb in the ceiling, there is a fitting, there is a cable in the wall, and there is power at the
box. Everything is installed and everything is correct. But the cable was never joined to the switch.

Flick the switch and nothing happens, and no inspection of any single part finds a fault, because
there isn't one. Each piece is fine. The join is missing.

That is exactly what this was. The code even worked out the answer, put it in a variable, and then
never looked at that variable again.

Nothing failed, so nothing complained. Every test passed. The feature had been sitting there for
weeks doing nothing at all, and the only reason it surfaced now is that I went looking for the
assignment rather than reading the algorithm again.

## What Toki was doing instead

Falling back. When a bullet has no recorded source, Toki finds the moment by searching the entire
meeting for lines that share words with the bullet. That works, and it is what has always been
shipping. It is just a much harder problem than it needs to be, because it is searching an hour of
talk instead of the two minutes the bullet actually came from.

## What I connected

Two joins.

**The first** attributes each finished bullet to the part of the meeting it was written from. The
clever bit is what it matches against. It does not compare the bullet to the raw transcript. It
compares it to the short summary of that section, which Toki had already checked line by line
against what was said. Short, verified, in the speaker's own words. Much easier to match, and much
harder to get wrong.

**The second** was a plain bug. There are two places that save a note: the one that saves it the
first time, and the one that replaces it when notes get rewritten. The first saved the source
information. The second did not. So regenerating a note quietly threw away its evidence links, which
means the thing most likely to make your notes better was also the thing that broke the click-to-hear.

## Three ways to get this wrong, all now guarded

**Order matters more than it looks.** Two later steps can delete a bullet: the one that removes
invented names, and the one that shortens lines. The sources are stored as a list matched up by
position, so if a bullet gets deleted after the sources are attached, every source after it points at
the wrong bullet. Nothing crashes. It just quietly sends you to the wrong minute. So attribution now
happens last, after everything that can delete a line.

**Say nothing rather than guess.** If a bullet does not clearly belong to one section, or two
sections explain it equally well, Toki records no source and falls back to searching. Sending you
confidently to the wrong moment is worse than offering nothing, because after that you stop trusting
the feature.

**A recorded fact beats a guess.** If a source was already known for certain, the new guessing code
is not allowed to overwrite it.

## I had to correct our own number

Our notes claimed this change was worth "+4.6 percentage points" of accuracy. I re-ran the
measurement over 300 test cases from 16 of your real meetings, and that number does not hold any
more.

What it actually buys: on easy cases, where the bullet uses many of the same words as the line it
came from, there is no real gain. Flat searching already gets those right. On hard cases, where the
bullet has been reworded so it shares little vocabulary with the original, accuracy goes from 85.6%
to 88.5%, and that one is statistically solid rather than luck.

The hard case is the realistic one, because a good note does not repeat the transcript word for word.
So the change is worth having. It is just worth less than we had written down, and in a narrower way.

There is also an honest cost: Toki now declines to link slightly more often (about three more cases
in three hundred). That is the deliberate trade. A search confined to the right part of the meeting
that comes up empty stays quiet, instead of reaching across the whole hour for something
plausible-looking.

## While I was in there

The repo root had a 46,687-line file of raw design code sitting loose in it. I have not deleted it,
because it is the only offline copy of the exact values from your Figma file. It moved into a proper
folder with a note explaining what it is and, more importantly, what it must never be used for: it is
a place to look up an exact colour, never something to build a screen from.

Reading it was actually reassuring. It confirms the material, the corner radii and the text shades
that our rules describe, which means those were taken from the real design and have not drifted.

It did turn up one thing worth your decision: four of the six accent colours in the app differ
slightly from the design file. All four differ in the same direction, which looks like someone tuned
them on purpose rather than a mistake. I have not touched them. It is written down and waiting for
your call.
