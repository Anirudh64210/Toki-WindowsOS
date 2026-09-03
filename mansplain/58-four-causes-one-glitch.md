# Four causes, one glitch

You told me four times that the notch still glitched when it changed size. Each time I fixed
something real, each time you said it was still there, and each time you were right. There were four
separate faults producing one symptom, stacked on top of each other, and each only became visible
once the one above it was gone.

This is the story of that, because the way it was eventually found matters more than the fixes.

## Why I could not see it

I was reading code and reasoning about what it should do. That found nothing.

What worked was **filming it**. I installed a video tool, recorded the notch at sixty frames a
second, and laid the frames out side by side as a contact sheet. Every single cause was obvious
within a few seconds of looking at that.

One thing got in the way first: Toki hides itself from screen recordings. That is a deliberate
feature, and it is why your recording worked and my first three did not - you had the toggle off.
There is a startup switch that turns it off just for testing, which is now written down.

## The four faults

**One. The new page was built at the wrong width.** When the card changes size, the window is briefly
held at the larger of the two sizes. The incoming page was being laid out at *that* width and then
cropped down, so its heading sat hard against the edge with the text cut off. Then at the end it was
released and the whole page re-arranged itself in a single frame. That last part is what you
described as "and then it readjusts".

**Two. The old page was thrown away instantly.** Fixing the width did not help, and this is why: the
moment a change began, the previous page was deleted. So for the entire animation you were looking at
the *new* page cropped to a rectangle that was still the *old* size. Cropping was the problem, not
arrangement. Now Toki takes a photograph of the old page before anything changes and dissolves it over
the new one - a real cross-fade. There is never a moment where you see a page cut to the wrong shape.

**Three. One bad frame right at the end.** While the card is moving it sits centred inside that
larger window, so its mask is offset to the middle. The instant the window shrank to its real size,
that offset became wrong and cropped the card - and the code that corrected it ran one frame later.
Every single size change ended with one frame shifted sideways. One frame is enough to read as a
snap, which is why it kept surviving fixes that made everything before it smoother.

**Four. Two clocks.** The window resize was fired by a timer set to the animation's length plus twenty
milliseconds. That gap was deliberate, and it meant the resize was always a separate, later event
rather than the end of the motion. Worse, that kind of timer is best-effort: under load - which is
exactly when Toki is writing your notes - it drifts, and the gap grows. It now happens on the
animation's own completion, so the last frame of movement and the resize are the same moment.

## Toki's flicker

Same story, different shape. The small peek Toki *is* the whole peek - that state contains nothing
else. So my new cross-fade was photographing him and dissolving that picture in the middle of the
card, while the real Toki sprang in at the side. Two of him, in two places, for a few frames.

That is why three attempts at delaying his arrival did nothing. The duplicate was the photograph, not
the timing. Toki is simply never photographed now.

And filming the fix immediately showed a new fault I had just created: about ten frames with *no*
Toki at all, because he was still waiting for a duplicate that no longer existed.

## The grey window

You caught this one and it was a plain mistake of mine.

I had made the Library window slightly see-through so Windows could paint real frosted glass behind
it. In the dark theme that looks like depth. In the light theme it means your desktop shows through
and tints the page, and a white page over a dark background goes grey. That is not something I can
tune - it is what transparency does to a light surface.

It is off. The window is solid again. I also reverted a change to the card's shading, and one to the
faint texture on it, which had gone in the same day.

Worth admitting: one of those changes was broader than I intended. I adjusted how colour gradients
are spaced to make the card look more like a lit surface, and that adjustment applied to *every*
gradient of its kind in the app, including the thin rim around the card. I changed more than I meant
to and did not check what else it touched.

## Why the mockups look better than the app

You asked this and it deserves a straight answer. Four reasons.

Browsers are simply better at drawing than the framework Toki is built on - sharper small text, real
blur, smoother small animations. The mockup uses words I chose, while the app gets real names that
speech recognition mangled and titles that wrap to three lines. The mockup has no loading state, no
empty state and no error state, and those are most of a real app.

And the last one is on me: I give the mockup my full attention and I *look* at it. The app change is a
small edit inside a large file, and I mostly check it by running tests.

So that changes. Toki can already produce real pictures of its own screens, and from now on those are
what a design gets checked against - not the mockup. Twice in one session that caught things my
reasoning had missed.

## Where things stand

The size change is smooth, Toki no longer flickers, the colours are back, the waiting screen now shows
which of the three stages it is on and how long it has been, and animations can be interrupted
part-way instead of starting over.

The recording bar has a design you approved and is not built yet. It is written down with the reasons,
and so is everything above.
