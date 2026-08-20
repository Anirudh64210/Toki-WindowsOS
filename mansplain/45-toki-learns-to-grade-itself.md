# Toki learns to grade itself

You said quality of the transcript and the summary is priority number one, production grade. Two
things shipped for that, and the first one caught a real bug within a minute of existing.

## The report card

Until today, if you asked "how accurate is Toki's transcription?", the honest answer was "we
don't know". There was no number. Every claim was me reading logs and guessing. Real
transcription companies publish accuracy benchmarks; we couldn't even measure ourselves.

Now there's a test: you read a two-minute script aloud (it's in `docs/wer-eval.md`), point Toki
at the recording, and it prints a grade - the percentage of words it got wrong, split into
"misheard", "missed", and "made up" (each points at a different part of the machine), plus a
separate scorecard for NAMES, because a transcript that gets every word right except who was
talking to whom is still a bad meeting note.

The script is deliberately nasty: five names including three Indian ones, spoken dates and
times, percentages, a year said as "twenty twenty seven".

## It caught a bug on its first run

To prove the test works I had Windows' text-to-speech read the script and graded that recording:
9.2% word error rate, at 6.5x faster than real time. Respectable. But the name scorecard read:
Priya heard, Craig heard, **Abhishek, Sita and Deepika all missed** - Abhishek came out
"Ubishek", Deepika came out "Deepaka".

That's fine on its own (it's exactly what pinning names in Settings is for). Except when I
checked whether pinning WOULD fix "Ubishek", it wouldn't have: the name-repair logic insisted a
mishearing must start with the same sound as the real name, and "Ubishek" starts with U where
"Abhishek" starts with A. Recognisers swap the opening vowel of unfamiliar names all the time -
especially Indian names, which is to say, your meetings. Vowel openings now count as the same
sound; consonant rules stay strict, so this loosens nothing that mattered.

That is the whole argument for building measuring tools before more features: the tool found in
sixty seconds what three days of code reading hadn't.

## Transcripts stopped reading like a court stenographer with hiccups

Second thing. Compare a line from your actual saved Sunday meeting, before and after:

> Before: "your last working date is uh july twenty second"
> After: "your last working date is July 22nd"

Spoken dates, times, years and percentages become digits. "Uh" and "um" disappear. Stutters the
recogniser kept ("even if if they are not responding" - a real line) collapse. But it is
deliberately timid: "two questions" stays words because that's correct English, "Mm-hmm" stays
because it's an answer, "you may second that motion" never becomes "you May 2nd that motion",
and every date conversion is recorded so the transcript can show what was actually said on
hover. No AI involved - pure rules, so it cannot invent anything.

## What's still on the table

The one big accuracy gap left is language: the recogniser is English-only, and your meetings mix
in Hindi. The honest fix is offering a second, multilingual recogniser as a choice in Settings -
that's a model download and a product decision, so it's yours to call.

1,991 tests green. The build on your machine has all of it. Next real meeting, look at the
transcript - it should read like something you'd forward.
