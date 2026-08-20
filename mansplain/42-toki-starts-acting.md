# 42 - Toki starts acting

*2026-08-14. The mascot revamp you asked for from the Grok bot, the notch's missing glass highlight,
and the test that had never worked.*

## What was wrong with Toki

You sent me the Benji Taylor Grok mascot and said ours was trash next to it. The difference is not
the drawing. It is that the Grok bot is one shape becoming something else, and ours was a doll with
accessories.

Concretely: Toki changed mood by making a pen appear and a speech bubble disappear. Nothing moved
between those two states. And the body itself was **identical in all four moods** - same silhouette,
same posture, same eyes, whether it was thinking or writing or pleased. The prop was doing all the
acting.

## What it does now

**The props morph.** Each one now grows out of the exact point on the body it belongs to and shrinks
back into it: the speech bubble out of the shoulder, the pen out of the paw that holds it, the
sparkles out of the crown. There is no longer a frame where something is simply gone.

**The body acts.** This is the bigger half:

- **Thinking**: leans back slightly and sways on a slow rhythm, eyes lifted toward the bubble.
- **Writing**: dips in time with its own pen. Not roughly in time - on the *identical* rhythm, which
  is the whole trick. A pen moving against a still body reads as a prop waggling next to a mascot;
  the same period makes it one motion.
- **Pleased**: one bounce, eyes squinted into a smile.

**The eyes look at whatever it is doing.** Three lines of code, and they do more than everything
else combined.

One structural detail worth knowing, because it is the sort of thing that breaks later: the mood
motion and the tap-to-react motion run on separate channels. A mood lasts minutes and a reaction
lasts a third of a second, so if they shared one, every tap would jerk Toki out of its mood and
every mood would swallow your tap.

## The mascot's shadow was costing you frames

The soft shadow under Toki's bubble was a real drop-shadow effect, and it sat inside the part that
animates. Every single frame of Toki springing in or gliding to a new position was re-rendering that
shadow **in software, on the main thread**. This is the same category of thing we diagnosed as the
cause of the "hard rendering" back in entry 38, still sitting there in one more place.

It is now a soft gradient ellipse under the disc. Looks the same, costs nothing.

## The notch's glass had a highlight that was never turned on

Real glass catches light along its top edge. Our design system says so, and the notch has carried an
element for exactly that since the redesign - painted fully transparent, drawing nothing. A control
that exists and does nothing, which is a bug this project keeps paying for.

It is on now: a soft band of light down from the top edge, themed for both light and dark. Dark is
deliberately dimmer than the panel's own rim, because a highlight brighter than the edge it sits
inside looks like a smudge instead of gloss.

## The test that had never worked

This is the part I would want to know if I were you.

There is a check called `--render-anim`. Its job is to photograph a screen twice, half a second
apart, so the two pictures prove an animation actually moved. Its exact words, in its own
description: "so the two PNGs can be compared to confirm the loading animation actually moves".

**The two pictures were byte-for-byte identical. Every run. Since the day it was written.**

The reason is technical but the shape of it is simple: an animation clock only runs when there is a
window on screen driving it, and that check never made one. So it photographed the same frozen
values twice and called it a comparison. The one test that existed to confirm an animation runs
could not possibly have detected an animation that did not.

That is the fourth time this project has hit the same lesson: a test that never produced its real
artifact is not a test. It has a real (off-screen) window now, and the two pictures genuinely differ
- I can see Toki's eyes squint in the second one and not the first, which is what proves the mood is
playing rather than just being set.

## What is still not verified

The notch's own chrome - the glass panel, its rim, this new highlight, Toki's bubble - has **never
been photographed by anything**. The render harness draws each screen inside a plain stand-in
rectangle, not the real notch. Which means the exact layer you keep saying looks wrong is the one
layer with no picture of it anywhere.

I have not faked one. Building an approximation of the chrome in the test harness is precisely what
every previous failed check did, and it is why bugs you could see in one glance survived green test
runs. It is the next thing on the list, done properly.

Until then: it is in the running build, so your eyes are the check.


---

# Addendum, same evening: "it only blinks"

You came back with: *"toki looks much better now, but ... it only blinks, doesn't see to the side, it
needs to ... more life to Toki."*

You were right, and the reason turned out to be a single number rather than anything about the
design. Toki's little autonomous motions fired **once every 30 to 60 seconds**, and only one of the
four was a glance. So a sideways look happened roughly **once every three minutes**. The notch
mascot is on screen for a few seconds at a time. It was never going to glance while you were
watching. Toki was technically alive and observably a picture.

## What actually makes something look alive

It is the eyes, and it is frequency, not size. A human eye moves several times a second in small
fast darts. That is not a special animation you spend sparingly - it is the resting state, and
everything else sits on top of it. That was the mistake: treating a glance as a rare treat.

So there are four layers now, from constant to rare:

**1. Toki follows your pointer.** Whenever your cursor is anywhere near, its eyes track it. This is
the one that does most of the work, and it is what the Grok bot you sent is really doing. A
character that tracks you is paying attention; one that does not is a drawing.

**2. When nobody is near, the eyes dart on their own** - every second or two, to a new point.
Sometimes back to centre, because eyes do not wander evenly around a room, they leave, look and come
back. About one dart in six comes with a blink, which is what actually happens when you look away.

**3. The body is never perfectly still.** A slow float and a slow lean, on four- and six-second
cycles, about one percent of its size. You will never catch these moving. You would notice
immediately if I switched them off.

**4. The bigger motions** - look around, nod, wobble, perk up - now come every 9 to 20 seconds
instead of every 30 to 60.

One design detail worth knowing: the eyes are **composed**, not owned. A mood points them somewhere
(writing puts them down on the page), and your cursor pulls them from *there*. So a writing Toki
glances up at you and then goes back to the page, not to dead centre. Without that, the pointer and
the mood would have fought over the same control and one of them would have won permanently.

## The two moments you named

**The sparkles in first-run setup.** They pulsed and grew. They now also *turn*, each on its own
rhythm and alternating direction, and there is a fourth one low on the opposite side so the set
surrounds Toki instead of sitting in a tidy arc above it. A four-point star that only pulses is a
pulsing star; the same star turning a few degrees while it pulses catches your eye the way a real
glint does.

**The writing while transcribing.** This was a metronome - a perfectly even tick, every 260
milliseconds, forever. Smooth, correct, and completely mechanical, because your eye reads perfectly
even motion as a machine no matter how nice the curve is. It is now a **phrase**: about two seconds
of a few quick strokes, then a beat of nothing where a hand lifts between words, then a few more,
then a longer rest. And the body moves on the same phrase, not just the same tempo, which is what
makes the pen and Toki read as one thing rather than a prop waggling beside a mascot.

## Your line on the Ask page

Fixed, and thank you for the before/after pair - it made it obvious in five seconds.

That soft tinted band at the bottom of the Ask page was a plain rectangle. A rectangle of tint has
hard edges, so you got a straight vertical line running down the gap beside the sidebar and a square
corner where it met the window's rounded one. It now fades out at both sides. Only the curved
corner, as you asked.

## One more thing under "haptics"

The big Toki on the Ask page was a clickable box that was not a button. That means poking it gave
you no press feedback whatsoever - no compress, no keyboard, no hover. The most obvious Toki on
screen was the one that felt like nothing when you touched it, which is the "haptics" half of what
you wrote. It is a real button now, so it compresses when you press it like everything else does.

This is the fifth time this exact pattern has shipped in this project. I have no excuse for it.

## What it costs

A quarter of a CPU-second per twenty idle seconds, and the notch still drops the same 1.2 frames per
transition it did before any of this. Nothing here is paid for in smoothness.


---

# Addendum 2: the flicker, and a sweep for its relatives

You reported that opening Toki from the top edge shows the bubble Toki for a moment before the real
one appears, and that this had happened before too.

## What it actually was

Toki springs in 90 milliseconds after the card, so the two read as a panel arriving and a character
arriving beside it rather than one printed graphic. That delay was the bug.

A delayed animation does not hold its own starting value while it waits. For those 90 milliseconds
the property shows the element's NORMAL value - and Toki's normal value is his finished state, full
size and fully opaque. So every summon painted a completed Toki next to a card that had not opened
yet, then snapped him small and sprang him back in.

The delay was showing you the ending first. Fixed by parking him at the beginning before anything
makes him visible.

## Two more of the same, found by looking

I checked every delayed animation in the app. Two more had milder versions of it - the thinking dots
and the sparkles both appeared at full size for a moment before snapping small when their turn came.
Everything else was already correct.

## A new check, because nothing could have caught this

This bug is invisible to every test we have. It is not a hit target, not a colour, not a measured
size, and it cannot be screenshotted - the overlay is invisible to screen capture, and the whole
thing is over in a tenth of a second.

So `--revealtest` drives the real notch window from hidden into a panel and reads the values back at
the instant it appears: is the card still shut, is Toki still small and faint, is the content still
faded out. I proved it works by taking the fix back out - it reports "opacity 1.00, scale 1.00",
which is exactly what you were seeing - and then putting the fix back.

## The sweep you asked for

Toki keeps its own log of clicks that land on nothing, and it had caught things:

**Three more clickable things that were not buttons.** The "Latest" pill that appears when you scroll
up in a conversation (in both Ask views), and the "Built with - open source and licences" row in
Settings. All pill- or row-shaped, all wearing the hand cursor, none giving anything back when
pressed. That is the fifth, sixth and seventh time this pattern has shipped here.

**A whole screen was outside the button audit.** The log had recorded a genuine dead hover on the
calendar composer's Create button. The audit could never have found it, because it walks the app's
screens and the composer is a *mode* of the calendar rather than a screen of its own. It and the
month picker are covered now - the audit went from 259 controls to 311, all passing.

## Your two screenshots

**The grey box behind Toki on the Ask page is gone.** That one was mine, from an hour earlier. Making
him a real button was right - he had no press feedback at all before - but it brought a hover
background with it. There is a bare button style now: he still compresses when you press him, he just
has no box. A grey rectangle behind a soft drawn character puts a box around a shape that is
deliberately not a box.

**The mascot is off the side panel.** It just says "Toki" now. There were two Tokis in that window,
and the rule has been "one Toki per surface" since the redesign. A masthead identifies the app; it is
not a thing to poke. The real one, big and pokeable, is in the middle of the Ask page where it
belongs.

**And no icon in the title bar** beside the minimise/maximise/close buttons - just the word. Your
taskbar and alt-tab icon is untouched, deliberately: that is a different icon slot, and clearing it
too would have quietly blanked your taskbar entry.


---

# Addendum 3: the flash was the LAST frame, not the first

You tested it and told me it was not fixed, and the detail you added is what actually solved it: it
is the **first** hover only, and every hover after that is clean.

## What it really was

I had been looking at the wrong end of the cycle. The arming fix in the last addendum was a real bug
and worth having, but it was not this one.

**Closing the notch was leaving a picture of an open notch behind.** The close animation ended by
un-clipping the card back to its full open size and *then* hiding the window. Windows keeps the last
image a window drew, and that image was now a fully open notch - with Toki's bubble in it, because
the bubble sits beside the card rather than inside it, so the clip never covered it anyway.

Then you hover, the window is shown again, and Windows puts that old picture up until the first new
frame is ready.

**And that is exactly why it was only the first time.** On the first open, the screen you are opening
is being built from scratch, so the gap between "show the window" and "here is the new frame" is long
enough for your eye to catch the old picture. Every time after that the screen is already built, the
new frame arrives almost instantly, and the old one is never up long enough to see.

Your "only the first time" was not really a clue about waking up. It was a clue about how long the
gap is.

The notch now hides first and is put back to its closed state afterwards, so the last thing it ever
draws is nothing at all.

## A second bug fell out of it

Toki's entrance was only ever playing **once per launch**. The code only springs him in when he had
been put away, and nothing ever put him away - so after the first open he simply appeared alongside
the card, with none of the spring he was written to have.

Your "every time after the first has no issues" was half right. No flash, and also no animation.

## I proved it this time instead of claiming it

I put the old line back and ran the check: it reports all three faults, including Toki sitting at
full size and full opacity on the second open. Then I put the fix back and it reports clean. The
check runs the real animation on the real window for half a second rather than just calling methods,
because a test that only calls methods is measuring a model of the animation, not the animation.

## The bubble in dark theme

You were right and the reasoning behind the old colour was wrong. It was a near-white disc, on the
theory that a bright puck "lifts him off the dark glass" - which ignores that on a dark desktop with
a dark notch it becomes the single brightest thing on your screen.

It is the same warm dark glass as the card now, one step lighter so it still reads as its own object
beside the card rather than a hole cut in it. I rendered it in both themes to look rather than
assuming: he is a pale blue cloud, so he stands out more against dark than he ever did against white.

## The title bar

Just "Toki" now, no icon, beside the minimise/maximise/close buttons. I screenshotted the real title
bar to confirm, because my first attempt at this *looked* correct in the code and did nothing on
screen - Windows was putting the icon back after we removed it.

One thing I could not check: your taskbar is set to auto-hide, so I could not photograph the taskbar
icon. It should be untouched - that icon comes from the app file itself, not from the window - but
please glance at it and tell me if it went blank.

## Something useful I learned by accident

Your "Show Toki in screen recordings" setting is ON, which means the notch is **not** hidden from
screen capture on your machine - so I can actually photograph the real notch here. Every note in this
project says the overlay cannot be screenshotted, and that was written assuming the default setting.
That makes the one remaining "we have never pictured this" item much cheaper than I thought.
