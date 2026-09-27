# Two thirds of your graphics card was sitting idle

You said summaries used to be faster. I could not answer, because **nothing in Toki had ever timed
the AI**. Not once. We had spent weeks tuning how it writes notes without ever measuring how long
any part of it took.

So I added a stopwatch to every call, replayed one of your stored meetings, and got this:

| what it was doing | how long |
|---|---|
| reading the meeting and picking out key points | 58 seconds |
| condensing those points | 31 seconds |
| a tiny one-word decision | **3 seconds** |
| writing the final note | 29 seconds |

That third row is the important one. It hands the AI a wall of text roughly the same size as the
others, and it comes back in three seconds - because it only has to produce **one word**.

That tells you where all the time goes. The AI **reading** is nearly free. The AI **writing** is the
whole wait. Every second you spend staring at the loader is Toki producing words, roughly ten a
second.

## The number nobody had checked

How fast it writes depends on how much of the AI model sits on your graphics card rather than your
main processor. Your card holds about 4GB. Toki was loading **20 of the model's 36 layers** onto it.

That 20 was never measured. Somebody - me, weeks ago - picked it as a cautious guess, because if you
ask a graphics card for more memory than it has, the program does not politely fail. It dies
instantly, and there is no way to catch that and recover. So the guess was deliberately timid, and
then it was never revisited.

I measured it properly, on your machine, on a real meeting:

| layers on the card | time to write the note |
|---|---|
| 20 (the guess) | 2 minutes 2 seconds |
| 28 | 1 minute 16 |
| **32 (what ships now)** | **46 seconds** |
| 34 | 48 seconds |
| everything | 45 seconds |

**Your summaries are now about two and a half times faster than they have ever been.**

## Why I did not just use the whole card

Because the bottom two rows are almost identical. Going from 32 layers to all of them buys three
percent, and spends nearly all the memory left over. That memory matters: the same graphics card is
running the video call Toki is taking notes on. If it ever runs out, Toki does not slow down or show
an error, it vanishes mid-summary.

Three percent is not worth that. 32 leaves more than a gigabyte spare.

## Were you right that yesterday made it slower?

**Partly, and it is worth saying so plainly.** Yesterday I made Toki write more - the note length now
scales with how long the meeting was, instead of being capped at seven lines. More writing is
literally the expensive thing, so yes, that cost time.

But the comparison was misleading in two ways I had to dig for. The old summaries you remember as
fast were **short meetings**. And the very longest ones were fast for a bad reason: they were
failing, falling back to copying lines out of your transcript, which takes no time at all because no
AI runs.

The upshot is that a full-length note today costs **less** than the old cut-down one did. The quality
work is paid for, with change left over.

## What I have not fixed

Toki still cannot tell how *busy* your graphics card is at the moment it loads. It only knows how big
the card is, which is why the setting has to be a cautious fixed number rather than "use whatever is
free right now". Doing that properly is a real piece of work and it touches the code that crashes
rather than errors, so I have written it up as the recommended next step instead of rushing it
tonight. It would buy that last few seconds and, more importantly, make Toki safe on machines very
different from yours.
