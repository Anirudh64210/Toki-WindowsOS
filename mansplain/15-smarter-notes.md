# Step 15 — Notes that are actually smart (and honest)

*July 28, 2026*

You tested two real meetings and both notes were wrong in different ways. This step is the fix — and it started, as it should, by **looking at what actually happened** rather than guessing.

## What we found

We read the saved notes *and* the timing log. The story was clearer than "the AI is dumb":

- **The 25-minute meeting.** The transcript itself was fine. But the **summary and title were just the first two or three sentences of the call**, and the "to-do" list was a wall of random lines. Why? Toki's little on-device AI can only read a few thousand words at once. A 25-minute meeting is far more than that, so the AI **choked on the size**, quietly failed, and Toki fell back to a dumb backup that literally copies the opening sentences. That backup is the "summary is the first few lines" you saw.
- **The 5-minute meeting where nothing happened.** Here the AI *did* run — and it **made things up to sound important**: "Participants confirmed screen sharing… no concrete decisions were made." It was padding a nothing-conversation into something that looked serious, exactly what you called out.
- **Also:** the biggest chunk of the 6–7 minute wait wasn't the notes at all — it was **transcription**. Toki turned the whole recording into text *after* you hit stop, and for that 25-minute call that alone took about **ten minutes**.

## What changed — reading a meeting in pieces

Instead of trying to swallow the whole meeting in one gulp (and choking), Toki now reads it **in chunks**, the way you'd summarize a long book chapter by chapter:

1. **Break the meeting into bite-size windows** (at natural speaker breaks, never mid-sentence).
2. **Jot down the key facts from each window** — a few honest bullets each.
3. **Combine those notes into the final summary.**

Summarizing a handful of short notes never overwhelms the AI, so **a two-hour meeting works just as well as a five-minute one.** The 25-minute meeting now gets a real title — *"Refine Weekend Attendance Tracking"* — a summary of the *whole* conversation, the actual key points, and the real to-dos.

## What changed — honest, and shaped to the meeting

Not every meeting has decisions, goals, and action items. Some are just a chat. So Toki now **decides what kind of note fits** and only writes the sections that are really there:

- A genuine working session → summary + key points + to-dos.
- A quick, empty exchange → **one honest line** ("mostly greetings and logistics, nothing substantive"), and nothing else. **No made-up filler.**

We had to tune this carefully. Our first attempt was *too* quick to say "nothing here" — it dismissed a real (if rambling) editing session as empty. So we taught it to only say "nothing" for genuine small-talk and "can you hear me" checks, and to always capture real content even when the conversation is messy.

To prove all this without making you record again, we added a tool that **re-runs the notes on your already-saved meetings** and shows the result. That's how we confirmed both your meetings come out right now.

## A transcript bug we caught too

When someone talks **slowly**, pausing between words (like reading a list), the transcript was putting **one word per line**:

> **Them:** And
> **Them:** professors
> **Them:** Brown

Now it waits until a line has a real sentence's worth of words before it breaks, so it reads properly:

> **Them:** And professors and grad students at Brown University.

## What's still coming

The **ten-minute transcription wait** is the next big fix. Toki will start turning speech into text **while the meeting is happening** — a little at a time, in the background — so when you hit stop, the transcript is basically already done and you get your notes in seconds instead of minutes. That's the next project.
