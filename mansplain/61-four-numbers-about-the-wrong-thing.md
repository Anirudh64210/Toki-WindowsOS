# Four numbers that were about the wrong thing

Yesterday's story was that for long meetings, the AI was never writing your notes. Fixing that turned
over three more problems underneath it, and by the end they all had the same shape.

Every one was a number inside Toki that described **how Toki works** rather than **what a meeting
is**. That is why none of them could ever have been fixed by rewriting the instructions we give the
AI, and it is why we spent so long rewriting those instructions without getting anywhere.

## The four

**One. How much text fits in one go.** Covered yesterday. The estimate was wrong by about a third, so
the AI choked and Toki quietly fell back to copying lines out of your transcript.

**Two. What to do when it still does not fit.** Toki cuts your meeting into chunks, has the AI pull
the key points out of each, then writes the note from those points. If there are so many points that
they *also* do not fit, the old code summarised the summaries.

That is the thing this whole design exists to avoid. The point of pulling out quotes first is that
the writer gets to see real evidence instead of a vague retelling. Summarising the summaries brings
the vague retelling straight back.

I could show you exactly what it costs. Your "AI Tool Review" meeting had its points extracted almost
perfectly, 25 of 26 kept in one chunk, 29 of 31 in another. Then it went through one round of
re-summarising and **the finished note had no bullet points at all.** Meanwhile your attendance
meeting, which did not need that step, came out fine in the same run.

Now, if the points do not fit, Toki **drops the weakest ones** instead. Lines with a name, a number
or a date beat vague ones; lines Toki could verify beat lines it could not. The survivors keep the
speaker's own words. Losing some points in a way you could reason about is much better than keeping
all of them in a form where every one has gone fuzzy.

**Three. How many points to pull from each chunk.** Toki asked for "5 to 12 points" from every chunk,
no matter how much of the meeting that chunk held. So the budget belonged to *how Toki happened to
cut things up*, which is an internal detail, rather than to how much was actually said. It now scales
with the amount of transcript.

Honest note: I made this change after reading a drop in one meeting's score, and I later found that
drop was not real (see below). The reasoning still holds so I kept it, but I am not claiming it as a
measured improvement, because it is not.

**Four. How far to look when checking a quote.** This one is my favourite because it is so easy to
see once you look.

When the AI pulls out a point, it has to say which line of the transcript it came from, and Toki
checks that the words are really there. It checked the cited line plus one line either side.

But a "line" is not a fixed amount of speech. It is however much was said between two pauses. Your
"AI Tool Review" meeting averages 3.3 seconds a line. Your attendance meeting averages **1.05
seconds** a line, because it is a fast, interrupted, overlapping conversation.

A spoken sentence takes about five to eight seconds. So on one meeting the check was reading a whole
sentence, and on the other it was reading a third of one. A perfectly honest point, taken from the
middle of a sentence, failed the check on the choppy meeting simply because the rest of the sentence
was three lines away.

And Toki was throwing those points away. On the choppy meeting it was discarding **a third to two
thirds** of everything the AI extracted. Not because it was catching mistakes, but because it was
looking through too small a window.

The check now looks at about six seconds of speech either side, whatever that works out to in lines.
Capped, deliberately: widening a safety check makes it easier to pass, and past a point a genuinely
made-up line could find its words somewhere nearby.

There was an easier fix available: just lower the bar for what counts as a match until the numbers
looked better. I did not do that, and I want to be explicit about why. That check is the only thing
standing between the AI's extract and invented content. Lowering it would have made every meeting
worse, including the ones that were fine, in exchange for a nicer-looking number.

## Where I got it wrong today

Twice, in the same way, and the second time I had already written the lesson down.

I saw one meeting's score drop from 62% to 38% and treated it as a real regression. It was not. That
same meeting has now scored 62%, 38% and 46% on essentially identical code, because the AI makes
slightly different choices every run by design. The swing I "diagnosed" was entirely inside the
normal wobble.

Then, checking the fourth fix, I looked at the first result that came back, saw a big improvement,
and told you the pass rate had gone from about 40% to 96%. It had not. That was one lucky chunk. Over
the whole run it went from 87% to 92%, which is a real but modest gain, and small enough that I
cannot swear it is not luck.

Both times the mistake was the same: reading one sample as if it were the answer. Both are now
written into our notes as a rule, and the tool has been changed so it prints how many things it
counted next to every percentage, instead of just the percentage.

## What actually improved, and what I will not claim

Things I can stand behind, because they are the difference between working and not working:

- long meetings are written by the AI again, instead of quietly falling back to copying your transcript
- the meeting that produced a note with **no bullet points** now produces six
- Toki now records where each bullet came from for **10 of 11** bullets and **7 of 7** to-dos, up from none at all this morning
- the quote check keeps **92%** of extracted points, up from **70%**
- and it is faster: that meeting went from about six minutes to four

What I will not claim is that the notes score better on our quality measure. That measure has too
much wobble in it to prove anything at this size, and it also cannot tell a genuinely good note from
one that just copies the transcript. Your eyes are still the real test, which is why Toki is running
on your machine now.
