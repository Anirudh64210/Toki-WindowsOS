# 65 - Its own typeface, and its own window

You sent two screenshots and one sentence that was completely fair:

> instead of removing all the little subheading, and unecessary and overwhelming text in the library
> window on all states, you just increased its size basically of all the text and made it ugly

You had asked me to remove text. I made it bigger. That is the opposite, and it is the thing this
round actually fixes.

## Toki was wearing Windows' clothes

Toki was set in Segoe UI Variable. That is the font Windows itself uses - Settings, File Explorer,
the Store. It is a perfectly good font, and that is exactly the problem: anything set in it looks
like part of Windows rather than like a product someone made on purpose.

Toki now ships its own typeface inside the app, called Inter. It is the font this whole category of
software is set in, and it is free to bundle as long as we credit it, which we do on the "Built with"
screen next to Parakeet and Qwen.

And note titles are now set in a **serif** - the kind of letterform with little feet on it, the kind
books are set in. That was the quietest and biggest thing in your screenshots: their note title is a
serif while every button around it is not. A meeting note is a document, and that one difference is
what makes it feel like something that was written rather than something a dialog box is telling you.
It costs nothing - same words, different voice.

## Four greys and one black

I pulled the actual colours out of your screenshots instead of guessing at them. Their entire window
is four greys and one near-black, and 82% of every pixel on screen is a single off-white.

The important part is which way round it goes. Toki had a slightly grey page with white cards
floating on top. Theirs has a white page with the cards as barely-there tints pressed into it. That
sounds like nothing, but it means a card no longer needs an outline or a shadow to be visible - so a
whole layer of lines and edges just disappears from the window. Most of what read as "cheap" was
edges.

The notch now wears that same page colour, so the little card at the top of your screen and the big
Library window finally look like they were cut from the same sheet of paper.

## Toki closes its own window now

That grey Windows title bar at the top is gone. Toki draws its own: a small back arrow on the left,
and thin minimise / maximise / close marks on the right, sitting directly on the page with no strip
behind them. The back button used to be a big filled slab at the bottom of the sidebar - the heaviest
thing in the window, for the button you press least. It is a small chevron now.

The fiddly part was doing this without breaking Windows. Snapping a window to half the screen, the
menu that pops up when you hover maximise, dragging a maximised window to un-maximise it, resizing
from the edges - all of that is Windows' job, and the easy way of drawing your own title bar throws
every bit of it away. Toki keeps all of it.

## Less text, not bigger text

A row in your meeting list used to be a large title that wrapped onto two lines, a two-line summary
under it, and then a line reading "Chrome, 24 min, Sept 17, 4 open". Five lines per meeting, eighty
five meetings down the list.

It is three short lines now: the title, one line of what happened, and "Sept 17, 24 min". Eight
meetings fit on screen where four did before.

Also removed: the "85" that sat permanently next to the word Library (it only appears now when you
search or filter, where it is actually answering a question), the search box that explained its own
query syntax at you, the day of the week and the app name under every note title, and the last two
coloured icons in the note toolbar - a red bin and an orange pin that were the brightest things on a
page full of grey.

## The notch was closing twice as badly as you thought

You said it "just cuts back up, not that nice". You were right twice.

**The movement was mostly a pause.** The card's retract was following a curve that, at the halfway
point of the animation, had only moved a seventh of the way. So it sat there looking frozen for the
first half, then jumped the rest in a few frames. Your eye reads that as a cut, because it very
nearly is one. New curve, and it now takes the same kind of time going as it does coming.

**And the words were genuinely being cut.** This one was a real bug and I only found it by filming
the notch at 60 frames a second and looking at the frames one by one. The text inside the card was
supposed to fade out as the card rolled up. Instead it vanished completely in a single frame while
the card was still at full height, and the "fade" that was meant to follow was fading something that
was already invisible. One wrong line, and it had been there a while. The film after the fix shows
the words dimming properly as the card furls.

The little hairlines you called ugly are gone too, from the About, Calendar and Settings pages. A
line drawn all the way across a narrow card just chops it in half, and it was only separating a
heading from the thing that heading already owns. Space does that job.

## What I could not check myself

Everything here passes its tests - 2237 of them, plus eight self-tests on the real window, plus three
on the packaged build. But tests measure a model of the product. Whether the new font, the new white,
and the new close actually feel right is your eyes, not mine. That is the part to look at.
