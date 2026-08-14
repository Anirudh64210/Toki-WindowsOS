# The smoothness pass - why Toki looked "hard", and the rulebook that fixed it

You said the animation was poorly designed, that Toki was too low and too small, and that the
rendering looked hard and ugly. You also asked for something unusual and smart: research it first,
write the findings down as a rulebook, and only then build. That is exactly what happened.

## The detective story

The "hard and ugly" rendering had one root cause, and it is a classic Windows trap. The soft
drop-shadows we had put on things (Toki's body, his eyes, the panel) are drawn by the CPU in
software, not by the graphics card - and they get redrawn EVERY frame anything inside them moves.
Toki breathes and blinks constantly. So your computer was hand-drawing the entire mascot about
sixty times a second, badly. Same for the panel's shadow during every open/close animation.

The fix is not "less shadow", it is "shadow done differently": Toki's lift is now a soft gradient
(cheap, drawn once), his eyes sit on a subtle dark seat instead of casting a shadow, and the
panel's shadow politely hides while the panel is moving, then fades back in once it lands - which
actually reads nicer, like the panel settling into place.

## The rulebook

The research (Apple's motion guidelines, Google's, IBM's, the designer behind some of the
best-feeling web apps, and how Duolingo animates its owl) became a permanent skill file in the
project. Highlights that now apply to every screen:

- Things ARRIVE with a fast start and a soft landing, and LEAVE quicker than they arrived.
- The notch opens and closes on the same curve Apple's Dynamic Island uses.
- Every button in the app now physically compresses when you press it and springs back when you
  let go - that "click feel" good apps have, done once centrally so every button behaves the same.
- Toki breathes slower and calmer now, blinks the way blinks look natural on screen (snap shut,
  ease open), and every half-minute or so does something small on his own - glances to the side,
  nods, wobbles - so he feels alive even when you ignore him.

## Toki himself

He is a third bigger, sits where each design frame actually puts him (centred beside the small
pills, peeking from the bottom corner of the recording panel, up by the title on tall panels), and
he GLIDES between those spots when the notch changes shape. And while recording, the whole strip
now hugs the very top of the screen - out of the way, like we discussed.

## The Library got its details too

The reading pane now matches the design file closely: a big bold title, little status capsules
("2 open", "3 people"), round tick circles that fill teal and strike the line through when done,
the person and deadline as small chips under each to-do, tiny coloured initial-circles in front of
each transcript line, and one rounded toolbar cluster with a copy icon and a red trash instead of
two text buttons.

## The catch

Everything is tested (1,915 tests, all the visual self-checks green), but the new build is NOT in
your dist folder yet - your running Toki was holding the file locked, and killing an app that
might be mid-recording is not my call. Quit Toki from the tray and say the word, and it publishes
in a minute.
