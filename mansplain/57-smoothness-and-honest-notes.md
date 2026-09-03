# Smoothness, honest notes, and real glass

You said two things were wrong. Toki did not feel smooth or finished, and the notes it wrote were
mediocre. Both turned out to have one specific cause, and neither was what it looked like.

## Why it did not feel smooth

Toki's card grows and shrinks as it moves between states: the little peek, the "record this meeting"
prompt, the recording bar, the loader, the finished notes.

There was a list inside the app of which of those changes were allowed to be animated. Five pairs
were on the list. Everything else just teleported: the card changed size in a single frame with no
motion at all.

The three that teleported were the three you actually click. Accepting a meeting. Pressing Stop.
Opening the notes. Meanwhile the two most beautifully animated moments in the whole app were the
drop-down and the little somersault Toki does when notes are ready, neither of which anyone asks
for. That inversion is the entire feeling of "unfinished": the parts you touch were the cheap ones.

We took the list away. Everything animates now, and anything that genuinely must snap has to say so.

## The thing I broke on the way

My first fix made that list longer instead of removing it. That left two separate pieces of code
doing the same job, and they had to agree with each other. Within a day they did not.

One line still followed the old short list: the line deciding *when* to take away the card's dark
glass as it shrinks into a small state. So the glass and Toki both disappeared on the very first
frame, and then an empty invisible box shrank for a quarter of a second. That was the glitching you
saw, and it was worse than what it replaced.

The lesson is worth keeping: two pieces of code that must agree will eventually disagree. The fix
was not to correct the second one. It was to delete it, so there is only one.

## Four other things that were quietly wrong

**A flash at the end of every shrink.** When the card finished shrinking, it snapped back to full
width for one or two frames before the window caught up. Every single time.

**A third of every close was invisible.** When Toki retracts upward, it was measuring from the wrong
edge, so the first chunk of the animation happened above the card where nothing could be seen.

**The first drop-down was always the slow one.** Toki was being built *while* appearing for the first
time, rather than beforehand. The very first thing you ever see was the only cold one.

**Padding everywhere.** Every panel had fourteen pixels of empty space under its content. That number
existed to stop the last line getting clipped, which needs one or two pixels, not fourteen. Because
content sits at the top, it did not read as balanced padding - it read as the card being slightly too
big for what was in it, on every page.

## Why the notes were mediocre

This one is arithmetic, not opinion.

Toki's writing model can only hold so much text at once. On your graphics card that works out to
about eight minutes of speech. Anything longer got squashed into a handful of dash-bullets first,
and the note was then written from *those*.

So for any real meeting, Toki was not writing notes from your meeting. It was writing notes from a
summary of a summary. A forty-minute meeting became at most twenty-five bullets, and the note came
out of that. No amount of rewording the instructions fixes it.

The squashing step now keeps far more, and every point it keeps has to **quote the line it came
from**. If a point cannot point at where it was said, it is dropped.

## Measuring, before improving

We had no way to tell whether a change made notes better. So the first thing built was a
measurement: go through every note in your Library and check every name, number and date against
what was actually said.

Its first reading was that a third of the facts were invented. That reading was wrong, and it was
wrong four different ways - it was counting the first word of a bullet as a person's name, counting
"70%" as invented when someone had said "seventy percent", counting Toki's own bullet numbering, and
worst of all, flagging the "still open from last time" section, which is correctly carried over from
*other* meetings.

Once those were fixed the real number was **96%**. Your note writer was never as bad as my ruler
said. That is the most useful thing I learned all week: a measuring tool that cries wolf is worse
than no measuring tool, because it sends you off repairing something that was not broken.

## Two things that cannot happen any more

**Notes invented from almost nothing.** The worst note in your Library came from a meeting where one
person said a single sentence: "I propose we do a sort of two-month rotation." Toki produced three
"options considered" (two of which nobody said), a firm decision that "begins immediately", and three
reasons for it. Nobody decided anything and nobody else spoke.

That is not really the model lying. It is that the note format had no way of saying "there was not
enough here", so it filled in the shape it was handed. Now, below a certain amount of actual content,
Toki does not ask the model at all and writes an honest short note instead.

**People who were not there.** If a note names someone who never appears in the transcript, that line
is removed. With one safeguard: if the transcript contains no names at all, nothing is removed,
because then an absent name means the microphone missed it rather than that Toki made it up.

## The glass

You said the glass looked fake, and it did.

Our own notes claimed real glass was impossible because Toki hides itself from screen recordings.
That turned out to be untrue - and worth saying plainly, because it had been written down as settled
and then believed. There is no evidence anywhere that hiding from capture affects glass. Some apps
actually *need* to hide themselves to do glass properly, because otherwise they photograph their own
reflection and turn grey.

The real reasons were different. The notch window is built in a way Windows cannot paint glass
behind, and Windows deliberately turns glass into a flat colour on any window that is not the active
one - which Toki's notch never is. So glass there would have been a flat plate anyway.

The genuinely useful discovery was in Apple's own documentation: their glass "blurs a background
that's part of your app, but not what appears behind your app on the screen." The glass you admire
is mostly not looking at your desktop at all. So the gap was never about blurring - it was about the
recipe.

Two things changed:

- **The Library window now has real Windows glass.** It is an ordinary window, so nothing blocks it.
  This is the honest comparison: you can now see real glass and Toki's version side by side.
- **The notch got the missing ingredients.** A very faint grain, and light that falls off properly
  from top to bottom. Grain sounds like nothing, but it is a named layer in Microsoft's own glass
  recipe: a perfectly smooth surface has no fine detail, and our eyes read that as fake, because real
  frosted glass scatters light at a scale smaller than a pixel.

## Where this leaves things

Everything above is built, tested and running. What is still open: making animations interruptible so
you can change your mind mid-motion, connecting the note-source tracking that is built but unused,
the waiting screen while notes are written, and the recording bar's own design.
