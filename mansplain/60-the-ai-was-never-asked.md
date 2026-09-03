# The AI was never asked

You told me the note quality was mediocre at best, and that something was missing. You were right,
and it was not the thing either of us was looking at.

For long meetings, on your laptop, **the AI was never writing your notes at all.**

## What was actually happening

To write notes, Toki cuts the transcript into chunks and hands the AI one chunk at a time. There is a
limit to how much text the AI can hold at once, so the size of each chunk is calculated from that
limit.

The calculation was wrong. It was handing over roughly a third more text than the AI could hold.

So the AI choked. Every single time, on every chunk. It threw an error that means, roughly, "I have
run out of room."

And here is the part that made it invisible: the code that calls the AI caught that error and quietly
returned "nothing". Not a warning, not a log line. Just nothing. The next layer up saw that every
chunk had come back empty, decided the AI must be unavailable, and fell back to the simple
non-AI method: pick out lines from the transcript that look important, and use those.

That fallback is why you still got *a* note. It is also why the note was bad.

## Why this hid for so long

Short meetings fit inside the limit. They were written properly by the AI and looked fine.

Only long meetings overflowed. So the symptom looked like "the AI is bad at long meetings", which is
a believable thing for an AI to be bad at, and sent us looking at the instructions we give it. We
tuned prompts. We measured note quality. We changed the writing style. None of it could have worked,
because for those meetings the AI was not involved.

## The same meeting, before and after

This is your 20-minute attendance meeting. Before:

> Title: *(none)*
> Summary: *(empty)*
> To-dos: "Um, I will ask Pooja to give you access to this uh" · "That's what you have to do." ·
> "So, how you will do it is"

Those are literally sentences lifted out of the transcript. That is the fallback doing its best
without an AI.

After:

> **Weekend Attendance & Makeup Days Process**
> Updated attendance rules for weekend meetings: mark M for Saturday-Sunday events. Two makeup days
> assigned per absence.
> - Two makeup days given per day of absence
> - Sivasai Ganesha attended July 25-26 with M marked
> - **To do:** Mark attendance for July 26th as M, due July 26th

Names, dates, actual rules, and a to-do with a deadline.

## The cost

That meeting now takes about four minutes to write instead of a minute and a half, because it is
being cut into more, smaller pieces and the AI is called more times. I think that is obviously the
right trade: the fast version was worthless.

## The lesson I have written into our rules

**A safety net that is good enough to ship is good enough to hide the accident.**

The fallback exists for a good reason: if the AI breaks, you should still get something rather than
losing your meeting. That is right and it stays. But it must be *loud*. It was silent, so a total
failure looked exactly like a slightly disappointing note.

This is the second time this exact thing has happened. In August the AI failed to load at all, and
the same silent fallback hid it the same way. Different cause, identical shape. So now: every failure
to reach the AI is written to the log, and "the AI broke" is recorded differently from "the meeting
genuinely had nothing in it", because those two look the same to you and are opposite problems.

I also added a test that checks the chunk size actually fits, on both of the settings Toki uses. It
fails on the old calculation, which is the point of writing it.

## Our own measurements were flattering us

While chasing this I found the note-quality tool was reporting "100% precision" on notes that
contained **no checkable facts at all**. If a note claims no names, numbers or dates, then nothing in
it can be wrong, so it scored perfectly. Three of six notes were in that state.

The tool now prints the count next to every percentage, and warns when a score came from an empty
note. With that turned on, the real picture appeared: notes were losing facts badly, one going from
25 checkable facts down to 4.

That was the thread that led to the bug above.

## A mistake of mine, for the record

To prove a test is worth having, I break the fix on purpose and check the test fails. Doing that
rebuilds the app. I then restored the code but forgot to rebuild before running the verification, so
for a few minutes I was "confirming" the fix while actually running the broken version. I caught it,
killed it, rebuilt and re-ran. Worth writing down because it would have produced a confident, wrong
result.
