# 55 - The summary that was just the conversation

You opened a recent meeting and the "summary" was the first few things people said, copied word for
word. So were the to-dos. Not a summary at all - just the top of the transcript wearing a summary's
clothes. And you'd seen it before, and thought it was fixed.

## What was really happening

Toki writes your notes with a small AI model that runs on your own laptop. That day, the model
**crashed while starting up**. Not the notes - the model's engine itself, deep in the graphics part.
And here's the trap: once that kind of crash happens, Windows remembers it for as long as Toki stays
open. So it wasn't one bad meeting - every meeting for the rest of that session quietly gave up on
the AI and fell back to Toki's simple backup.

That backup is the problem's other half. When the AI can't run, Toki has always had a plain,
no-AI way to pull out notes so a meeting is never lost. But that backup was lazy: for the "summary"
it just grabbed the opening sentences, and for the to-dos it grabbed any sentence with a word like
"fix" or "confirm" in it - even a sentence that wasn't a task at all. So when the AI died, you got
raw conversation labelled as notes.

Why did you think it was fixed? Because we *had* fixed it - but only for the AI. We taught the AI
"don't ever paste raw conversation as a summary; an empty summary is more honest." We never taught
the backup the same lesson. And the backup is exactly what shows up when the AI breaks.

## What I changed

1. **The backup tells the truth now.** No fake summary made of raw speech. And a to-do has to
   actually look like a task ("I'll send the deck", "Fix the login bug"), not just any sentence with
   a task-ish word hiding in it.
2. **A real "try again".** If the AI crashed, the note now says so, with a **Try again with the
   on-device model** button. Sometimes the crash is a one-off (the graphics card was busy for a
   second) and trying again just works. Sometimes it needs Toki restarted - and now it tells you that
   instead of silently serving basic notes forever.
3. **An honest heads-up on the notes.** If you picked "use my API key" but never saved a key, the
   note now quietly says your notes were written privately on your own machine instead - nothing left
   your computer - and to add a key in Settings if you want the API model.
4. **A hide/show button on the recording bar.** You mentioned starting a recording and only then
   realising ghost mode was off. There's now a little eye button right on the recording bar - one tap
   to hide Toki from screen recordings without going anywhere.

The lesson, plainly: a backup that hands you raw junk dressed up as finished notes is worse than one
that admits "these are just the basics." When we protect the good path from a mistake, we have to
protect the backup path from the same mistake - because the backup is what runs precisely when things
have already gone wrong.
