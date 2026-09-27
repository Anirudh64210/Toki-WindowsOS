# The loader that never ended

You recorded a short meeting and Toki sat there summarizing for fifteen minutes, with the progress
bar counting up to **1275 minutes and 37 seconds**. That is twenty-one hours.

Here is the uncomfortable part: **your notes were finished eight seconds after you stopped
recording.** They were written, saved to the Library, and mirrored to a Markdown file on disk. All of
that had happened before you even started wondering what was taking so long. The only thing that
never happened was Toki telling the notch to stop spinning and show you the result.

Two separate bugs. The first caused the hang; the second made it look sixty times worse than it was.

## Bug one: a word that meant two things

Toki has a rule I added last week: if a recording has almost nothing in it - fewer than seventy real
words - do not wake up the big AI model. Just write an honest short note that says so. That rule is
right, and it is why your note came back in eight seconds instead of two minutes.

Your clip had twenty-one words, so that rule fired. It wrote your note. And then it said, in code,
"stop here" - meaning *stop bothering with the AI*.

But "stop here" was written in a place where it meant *stop doing everything*. And the very last
thing Toki does, after writing your notes, is tell the notch: **you can stop loading now, here are
the notes**. That instruction was one line further down. It never ran.

There is no timeout on that loading state. It is allowed to end in exactly two ways, and both of them
were now unreachable. So it span. It would have span until you closed Toki.

**Why no test caught it.** That "almost nothing was said" rule only fires on a nearly silent meeting.
Every meeting I tested had plenty of words, so every one took the normal route through the AI - and
that route reaches the "stop loading" line perfectly well. The bug was hiding in the one branch that
only a quiet room ever walks down.

**How I fixed it.** I could have just deleted the offending word. I did not, because that fixes this
one bug and leaves the trap. Anything else that went wrong later in that sequence would have hung the
loader the same way, and there was already one such step sitting there unprotected.

Instead I made the "stop loading, show the notes" instruction into something Toki runs **no matter
what happens** - success, failure, or a crash halfway through. If the notes fail entirely, you now
get an empty-state screen instead of a spinner. The loader can no longer outlive the work.

## Bug two: a stopwatch started at the wrong moment

The number itself. 1275 minutes.

Toki builds each of its little screens once and keeps them, because rebuilding one while it is
animating makes the animation stutter. Good decision, still stands. But it means the loading screen
you saw had been *built* the previous evening, when you launched Toki.

And the stopwatch on that screen started when the screen was built, not when your meeting ended. So
it was not showing you how long your notes were taking. It was showing you **how long Toki had been
open**. Twenty-one hours and fifteen minutes. Which is 1275 minutes, exactly.

The elapsed timer is a good idea - a long wait with no number on it feels like a crash - but it was
measuring the wrong thing entirely. It now starts when the loader actually appears.

There was a third, smaller version of the same mistake three lines away, which I fixed while I was in
there: the timer was re-subscribing itself every time the loader appeared and never unsubscribing, so
after ten meetings it was doing the same work ten times over. Harmless, but it is the same
misunderstanding, so it went too.

## What I want you to take from this

Two things.

**Your data was never at risk.** Every meeting Toki has hung on is sitting complete in your Library.
This was a bug in Toki telling you it had finished, not in Toki finishing.

**The rule you stated is the right one.** You said Toki is never that late - quick, fast, quality
top. You were right to treat fifteen minutes as impossible rather than as slow, because it *was*
impossible. Nothing about summarizing a fifty-second recording could take fifteen minutes. The number
being absurd was the evidence, and it pointed straight at a bug rather than at performance.

I have added nine tests around this. Four of them are the important kind: they take the broken code
as it shipped, hand it to the new safety checks, and confirm each check actually catches it. A test
that passes on both the broken and the fixed version is not a test, and this area now has none of
those.
