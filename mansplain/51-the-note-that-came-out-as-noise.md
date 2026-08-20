# The note that came out as noise

*2026-08-19, night*

## What you saw

Tonight's 9 PM meeting came out titled "Wait 10 minutes, I guess", with a "summary" that was
just the first lines of the meeting repeated back, 92 "open questions" that were every sentence
ending in a question mark, and a to-do list full of half-sentences. Not notes - a transcript
shaken through a sieve.

## What actually happened

Toki has two note writers. The smart one is the AI model. The dumb one is a simple rule machine
(every question mark becomes an "open question", every "I'll..." becomes a to-do) that exists
only as a safety net for computers with no model at all.

Tonight the AI DID write your notes. But it formatted them in a way Toki's reader didn't
expect: it put a dash in front of every line, including the section names, like "- SUMMARY:
...". Toki's reader has a rule that a line starting with a dash is content, not a section name
(that rule exists for a good reason - it stops a bullet that happens to say "Next steps" from
being mistaken for the start of the to-do section). So the reader looked at the AI's answer,
recognised none of it, threw the whole thing away, and the dumb safety net wrote your note
instead. Silently.

One unlucky answer, no second chance, and no record of what the AI had actually written.

## Why this "keeps happening"

It's not one bug coming back - it's one WEAKNESS showing a new face each time. The pattern is
always: the model's answer doesn't fit what Toki expects, and instead of asking again, Toki
quietly ships the safety net. Earlier faces of it: the summary that was raw first lines (model
ran out of room - fixed), the model that skipped the summary section (fixed). Tonight's face:
the model answered fine but in bullets.

## What changed

Three things, all in the note writer:

1. **A second chance.** If the answer can't be read, Toki now asks the model once more, with a
   firm reminder of the exact format. The model doesn't repeat itself word-for-word, so the
   second answer usually lands.
2. **A rescue reader.** Before giving up on an answer, Toki now checks: are these "bullets"
   actually the section names in disguise? If so, it un-bullets exactly those lines and reads
   again. The protective rule stays for normal notes.
3. **Evidence.** When an answer really can't be read, Toki now writes down what the model said
   in the log. Tonight's diagnosis was partly detective work because the actual answer was gone
   forever - never again.

## Proof

Two new tests that replay tonight's failure were written FIRST and confirmed to fail on the old
code, then pass on the new. All 2,053 tests green. Then the real test: tonight's actual meeting
was fed back through the fixed writer on your machine, and it produced a real note - "Ops and
Volunteer Tracker Updates", a three-sentence summary you can check against the room, and nine
concrete to-dos (join Ivan and Monica tonight, fix Nyla's listing, update column A).

## Still open (your call, nothing done yet)

- **The fix is not in your running Toki yet.** It needs a fresh `dist` build - close Toki when
  you're done with tonight's meetings and say the word.
- **Tonight's saved note is still the bad one.** The meeting and transcript are safe; the note
  can be rewritten by the fixed writer if you want.
- **The name repairer overreached tonight.** It renamed some people it shouldn't have: Sudanva
  became "Susan", Nyla became "Neil", Mastan became "Martin" - real names from tonight pulled
  toward more familiar names from your past meetings. Fixing that well needs a careful pass
  with measurements, not a quick tweak.
