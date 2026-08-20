# Five critics walked the pipeline

You asked me to send agents to criticise everything in the processing system, find the problems,
and fix them. Here is what happened, in plain language.

## How it worked

I sent five independent reviewers into the code at the same time, one per layer: the audio
capture, the transcript clean-up, the note writer, the who-said-what layer, and the
saving-and-recovery machinery. Each had one instruction: find things that provably go WRONG - a
real situation in, a wrong result out - and show the exact line of code. No opinions about style.

They came back with about 45 accusations. I checked every one against the code myself before
touching anything, because reviewers - human or not - sometimes cry wolf (ours have before). 24
were real. All 24 are fixed, and 14 of them now have tests that make the old behaviour impossible.

## The scariest ones, in plain words

**Toki could delete a whole meeting because you asked it a question.** The recording stopwatch
restarted every time the notch detoured somewhere mid-meeting - opening Ask, glancing at the prep
card. Come back, and the clock started from zero. If you did that near the end of a 40-minute
call, Toki measured a "3-second recording", decided it was an accidental tap, and deleted it -
audio, transcript, everything. The stopwatch now belongs to the recording itself and nothing else
can touch it.

**A deleted meeting could come back from the dead.** When you delete a meeting, Toki removes the
Library entry and then the audio folder. If the folder was briefly busy, the delete failed
quietly - and on the next launch, the crash-recovery sweep found "orphaned audio", helpfully
transcribed it, and put the meeting you deleted BACK. Deleted folders are now marked with a
gravestone the recovery sweep respects.

**Your ticks and titles could be silently erased.** While the AI writes the good notes (up to
five minutes), the Library is fully usable. Anything you did in that window - ticking off a
to-do, fixing a title - was overwritten the moment the AI finished. Now your edits win.

**The "was that task really given to that person?" check was checking the AI's own homework.**
For longer meetings, we verified a to-do's owner against notes the AI itself had written - so if
it invented a name, the invention confirmed itself. It also accepted names welded from two real
words ("Craig" + "Portal" = a person named Craig Portal). Owners are now checked against the one
text the AI cannot have written: the transcript, as an exact phrase.

**One voice could be split into four people.** If a calendar invite had five people but only one
person talked, the speaker-separation logic forced the audio into four "speakers" because the
invite said so. The invite count can now only help GROUP voices, never invent extra ones.

**Tim could become Tom.** For short names the sound-alike matcher ignored vowels, so a workplace
with both a Tim and a Tom would slowly rename one into the other. Short names now need their
vowels to agree, and a name Toki has seen often across your library is treated as a real person
that no frequency vote can erase.

**A four-word "yeah okay sounds good" could delete a thirty-word real sentence.** The
echo-removal maths was backwards: it asked "do these lines share words?" instead of "is the short
line a copy of the long one, or the other way round?". Direction fixed, both cases tested.

## What I deliberately did not fix today

Eight smaller findings are written up in the backlog with reasons - things like a word
occasionally garbling at 15-second boundaries during unbroken monologues, and clips longer than
45 seconds playing their beginning rather than the part that matters. Real, but each fix is
invasive compared to the harm, and today's pile was already large.

## The bottom line

1,963 tests green (14 new ones written from these exact failures). Everything above ships in the
build now on your machine. The judge that matters is still your next few real meetings.
