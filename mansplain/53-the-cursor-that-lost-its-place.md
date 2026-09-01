# The cursor that lost its place

*2026-08-23, night*

You asked me to research how Notion's note editor works, properly, so Toki's could learn from the
best one anybody has built. I did, and the research immediately found two things broken in Toki that
nobody had noticed. That is the useful part of this story, so I will start there.

## What was wrong with the cursor

Type a few lines in a note. Put the cursor in the middle of one of them, halfway along a word. Now
press the Down arrow.

It should drop straight down and land under the same spot. Instead it jumped to the far left of the
line below.

Word does this correctly. Notes does. Notion does. Toki did not, and it was worst right after you
pressed Enter or ran a slash command, which is exactly when you are most likely to press an arrow
key next.

## Why

When you move the cursor yourself, Windows quietly remembers which column you were in, so that Up
and Down keep you in a straight line rather than wandering. That memory is the whole trick behind
arrow keys feeling right.

But when the program moves the cursor for you, Windows does not refresh that memory. It keeps the
old value, which starts life as "column zero", meaning the far left. Toki moves your cursor for you
constantly: every time you press Enter, every time you paste, every time a slash command runs. So
every one of those left a stale memory behind, and the next arrow key obeyed it.

This is a known fault in Windows itself. It has been reported, it is still open, and the people who
maintain it have written that a fix is "very unlikely". So it is ours to work around.

## The fix

There is now exactly one way for Toki to move your cursor, and it wipes that stale memory every
single time. One way, not eight, so a future change cannot forget to do it.

I measured it rather than trusting it. With the cursor 67 pixels into a line, pressing Down used to
land at 5 pixels, which is the left edge. It now lands at 66 pixels, the nearest letter.

Then I broke the fix on purpose and ran the test again, to be sure the test could actually catch the
problem rather than just agreeing with me. It caught it.

## The second thing, which was worse

When you asked me to publish the app so you could try it, I did, launched it, and it died in about
four seconds. It did not even get far enough to write a line in its own log.

The version I had been testing with all evening worked perfectly. Same code, same everything. The
difference was purely in how the app gets packaged into that single file on your desktop.

The recipe we use to package Toki is written down in the project's rulebook, and that recipe is
missing one instruction. Without it, a set of files Windows needs to draw any window at all never
make it into the package. So the app started, tried to open its first window, could not find those
files, and gave up. The packaged app went from 191 megabytes to 375 once I added the missing
instruction, which tells you how much was being left out.

The uncomfortable part: this means any copy of Toki packaged with the written-down recipe was broken
the same way. I have not changed the rulebook, because that file is yours and changing it is your
call. But I would change it.

## What the research itself produced

Rather than leaving a pile of notes nobody will read again, I turned it into a checklist the next
session has to follow before touching the note editor. It covers what a block is, what every key
should do and where the cursor should land afterwards, which typing shortcuts convert to what, how
the slash menu should behave, and the traps that have bitten everyone who has built one of these.

A few of the measurements are taken straight from Notion's own stylesheet rather than from somebody's
blog post, so they are facts rather than folklore: how wide their writing column really is, how much
space sits above a heading, how far left the drag handle sits, and what colour a selection is. That
last one settles an argument: Notion's block selection is a plain rectangle with no rounded corners.

## Three things I did not do

I left three questions alone, because each one changes how writing feels and that is your decision,
not mine.

**The Tab key.** Right now, pressing Tab in a note types an invisible tab character that gets saved
into your file. It neither indents the line nor does nothing, which is the worst of both. In Notion,
Tab tucks a line underneath the one above it. I can do that, or I can make Tab do nothing at all.

**Selecting across lines and typing over it.** If you select text spanning two lines and start
typing, Toki hands that job to Windows, and Windows keeps the look of a line but loses its bullet.
The editor's own code comments say that is the exact bug it was built to prevent, yet this one path
still goes around it.

**Chinese, Japanese and Korean typing.** In those languages you type several keystrokes to build one
character, and the app must hold still while you do. Toki tidies the note after every keystroke,
which would interrupt that. I did not fix it, because there is no way to install those keyboards on
this machine and test it, and a fix I cannot watch working is not a fix.


## What happened after this was written

Two corrections landed the same evening, and both are the same lesson wearing different clothes.

The first fix for the cursor reached into a private part of Windows' own text machinery. It worked
perfectly on the development build and did absolutely nothing in the app on your desktop, because
the packaged version carries its own copy of that machinery and the private part was not where the
code went looking. The failure was swallowed silently, so nothing complained. It was caught only by
running the checks against the packaged app rather than the development one: 39 checks passed in
development, 38 passed and one failed in the real thing, every single time. The cursor now uses
ordinary public commands instead, and passes in both.

And the publish recipe is corrected in writing, after proving it both ways rather than taking anyone
at their word: published exactly as documented into an empty folder, it produces the app plus eight
separate Windows files; copy the app out on its own and it dies instantly; add the one missing
instruction and it becomes a single file that runs anywhere. The reason nobody noticed for weeks is
that the folder on this PC still had those eight files lying around from older builds.
