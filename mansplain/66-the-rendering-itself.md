# 66 - The rendering itself

You said this, and it is the most useful thing anyone has said to me on this project:

> what you are doing is, oh here's this thing i have to do, and then, okay let me do it and as long
> as i just went from a to b its cool, no no, quality needs to be top, rendering needs to be perfect,
> the actual issue needs to be addressed and fixed

That is exactly what happened with the notch glitch. You reported it, I found *a* cause, fixed it,
and stopped looking. It was still there because there was a second cause underneath. So this round I
stopped reading code and started measuring the actual screen, and the answers were not what I would
have guessed.

## The text was broken by my own font change

Windows has a setting that says "line every letter up exactly on whole pixels". It makes text
sharper, but it only works if the font was *drawn* for it, with special instructions baked in telling
the computer how to squeeze each letter onto the pixel grid. Segoe UI - the old font - has those
instructions, in enormous detail. Inter, the new one, deliberately does not: it is drawn for modern
screens that place letters wherever they actually fall.

I changed the font and left that setting switched on. So Windows was squeezing letters onto a grid
using instructions that did not exist, and the strokes came out uneven and half-missing. That is your
screenshot of the word "Contact" - some strokes fat, some nearly gone.

**The lesson is one line: that setting belongs to the font, not to the window.** I moved one and not
the other.

I also turned off the colour-fringing kind of smoothing, which is where the orange and blue edges
came from. I measured it: on the same line of text, the colour spread per pixel went from 93 down to
0.3, and the letters came out very slightly *darker*, not thinner. Worth noting, your reference app
has some colour fringing too - so that was never really the problem. The broken strokes were.

## Toki was being shrunk, not drawn

Toki is drawn as maths, not as a picture, so he can be any size. But for speed we bake him into a
picture once and reuse it. That picture was being baked at 268 pixels wide and then squashed down to
about 55 to fit the little bubble on the notch - a five-fold shrink. When a computer shrinks a
picture that hard in one step, it only looks at a handful of the original pixels and throws the rest
away. That is why his edges looked like a staircase with a dark smudge around them.

The old note in the code said baking him big "keeps him crisp everywhere". It is the opposite:
baking him bigger than he is drawn makes it *worse*. Now the picture is baked at the size he is
actually shown at. Measured on a screenshot, the number of smoothly blended pixels along his outline
roughly doubled.

## The notch was never resizing

This is the one you reported twice. I filmed it at 60 frames a second and measured the card in every
single frame, and the width does not change - not by one pixel. The app's own log agrees: 380 going
in, 380 coming out.

What changes is the **corner**. Toki's card has a special corner shape - the smooth, continuous kind
Apple uses, rather than a plain circle. But every time the card moved, it quietly swapped to a plain
circular corner for the duration and swapped back at the end. Two corner changes on every single page
switch. Measured three pixels per side, and once you see it in the frames it is obvious.

Two notes in the code had made this look harmless. One said the two shapes differ "by under half a
pixel". That is true at one specific spot on the corner and nowhere else. The other said the smooth
shape must never be recalculated while something is moving, for speed. Nobody had ever tested that -
I did, and it costs nothing measurable. The card now keeps its proper corner the whole time. Both old
notes are corrected where they sit rather than quietly deleted, because both sounded completely
reasonable and would be believed again.

## The close, again

You said it "still shuts back up like a shutter". Here is what the film says, as a fraction of the
distance travelled 50 milliseconds in:

- opening: **79% of the way there**
- closing: **23% of the way there**

The notch *opens* by leaping most of the way out and then gently settling. Last time I gave the close
a smooth speeding-up curve, which is the textbook answer for something leaving - and it is the exact
opposite feel, because it spends its first half barely moving. "Close it the same way it opens" is
not something to work out from principles. It is something to copy. Now both directions are about
three quarters done within 33 milliseconds.

## How I checked

Two things I had been doing wrong:

The screenshots the app takes of itself **cannot show this class of problem**. They are drawn
straight to an image file, and Windows skips the text-smoothing step when it does that. So every
picture I generated looked fine while your screen did not. Text now gets checked by photographing the
real screen.

And when something survives a fix, it means there is a *second* cause, not that the fix was wrong.
That is written down now so I stop declaring things done after finding the first one.

Two new automatic checks were added, and I verified both by deliberately putting the bugs back and
confirming the checks caught them. Everything else still passes: 2237 tests, all eight interface
checks, and four checks on the packaged build.
