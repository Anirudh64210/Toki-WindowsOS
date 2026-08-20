# The notes stopped lying

You asked one question: "check the most 2 recent recordings, tell me whats wrong". Here is what
was wrong, in plain language, and what changed.

## What your two newest notes actually said

Your Sunday evening meetings saved with sentences nobody spoke:

- "wait for a **day** or two" was saved as "wait for a **Toki** or two"
- "my last working **day**" became "my last working **Toki**"
- "**make**-up days" became "**Mac** up days"
- "I didn't **get** you" became "I didn't **Craig** you"
- "I'm the team **lead**" became "I'm the team **Lily**"
- "the ser**vice** portal" became "the **Slack** Portal"

And because the summary is written from the transcript, the invention climbed upward: one note's
Key Points said *"A two-Toki wait period is suggested"*. On an earlier meeting the same bug
renamed a real person, David, to "Toki".

On top of that, both notes carried a "Still open from last time" section full of to-dos owned by
people who were never in your meetings (David Kim, Lisa Park, Ryan, Alita, Cormac), and one to-do
was marked due "*(when no time stated)*" - which is not a date, it is a sentence from my own
instructions to the AI, leaking into your note.

## Why each one happened

**The name fixer was too eager.** Toki has a feature that repairs mis-heard names using names it
trusts - your name, your calendar attendees, names you pin. The rules for "how different can a
word be and still count as the same name" were fixed numbers, and fixed numbers that are fair on a
long name like "Abhishek" are a blank cheque on a three-letter word like "day". Worse, once it
decided "Day" was a mis-heard "Toki", it rewrote every lowercase "day" in the whole meeting too.

The research on this is unambiguous, and it matches what happened to you: every system that
"fixes" names after transcription fails in exactly this direction if it is only ever tested on the
names it should catch, and never on the ordinary words it must leave alone.

**The silence gate was guarding an empty road.** Remember the fix where half your transcript was
invented because your quiet mic stream was fed to the recogniser raw? The gate we built for that
was real and it worked - but it was installed on the code path that runs when a meeting is
transcribed *after* it ends. Your meetings are transcribed *during* the call, on a different path,
which had no gate at all. The fix existed; your meetings just never drove past it.

**"Still open from last time" matched on gibberish.** That section finds your previous meeting by
comparing titles. But it ran a moment before the real title existed, so it compared against a
placeholder - literally the first words you said, like "So, um, just to give you a...". And it
matched word *fragments*: "just" matched "Ad**just**ing", "even" matched "**Even**t Coverage". One
of the meetings it kept pulling tasks from was a leftover test record from July with 2023 due
dates - a ghost that had been handing out homework for weeks.

**The due-date leak.** The AI is told "drop the due field when no time was stated". A small model
sometimes copies the instruction instead of obeying it, and nothing checked what landed in the
due slot before showing it to you.

## What changed

1. Name fixing now scales its strictness with word length, refuses to touch about 180 everyday
   words, and only ever rewrites a word that is capitalised the way a name is. Every one of the
   bad rewrites above is now a test that must stay impossible.
2. The silence gate (and the volume levelling that goes with it) now runs on the live path - the
   one your meetings actually use.
3. "Still open from last time" waits for the real title, matches whole words only, and never
   reaches back more than 45 days.
4. Due dates and task owners are now checked in code before they reach your note: a due date has
   to look like a time, and an owner has to be "You" or a name that was actually said in the
   meeting. If the check fails, the field is dropped - a blank is honest, a guess is not.

One thing I looked at and did NOT change: the meeting audio showing "99% peak". I scanned the
actual recording sample by sample - it is loud, but not distorted. Loud is fine.

## What to expect

Your next recorded meeting is the real test. The transcript should contain only words that were
said; "day" should stay "day". The two Sunday notes are still corrupted as saved - the original
audio for both is intact, so if you want, I can rebuild those two from their recordings with the
fixed engine. Your call.
