# 37 - The first real meetings

*2026-08-11*

You recorded two actual meetings and sent me the notes. This is the most useful thing that has happened to
this project, and I want to start with the uncomfortable part.

---

## 1,800 tests, all green, all missing it

Every single problem in those two notes was invisible to the test suite. Not "a test was wrong" - the
tests were right about the things they tested. They tested the *pieces*. Nobody was testing the thing you
actually read.

That is now a rule I have written down twice, and this is the second time it has cost us.

---

## The Summary was just the transcript

Your first note opened with:

> **Summary:** Hello. Uh hi Sai, you are actually on mute. Hello. Hey Davy. Yeah...

Here is what was happening. There are two ways Toki can write a summary: the good one (the AI reads the
meeting) and the crude fallback (take the first hundred and forty characters and hope). The fallback
exists for people who have no AI model installed at all - it is better than nothing.

The AI *did* work. It wrote the sections, the decisions, the title. It just didn't write a summary that
time. And the code said "no summary? use the fallback" - so it pasted raw transcript into the most-read
part of the note, sitting underneath a set of perfectly good AI sections.

It now asks the AI again, specifically for a summary. If that fails too, it prints **nothing** and the
Summary heading disappears. A missing section is honest. A section labelled "Summary" that is actually
somebody saying "you're on mute" is not.

**This also explains "Evacuate Immediate Action Required."** The title is written *from* the summary. The
summary was the first thing anyone said - people who weren't team leads being asked to drop off the call.
So the title described the first ten seconds instead of the meeting.

## The transcript was confetti

182 separate pieces in eight minutes. One every 2.6 seconds. A single sentence of yours became twelve
paragraphs, each with your name stamped on it.

And worse:

```
You:  "did Craig mention ask"
Them: "idea."
You:  "you to like do this?"
```

Someone said "idea" while you were mid-sentence, and it got wedged into the middle of your question.

The cause was simple and a bit embarrassing: Toki records you and them on separate channels, transcribes
each one, then sorts everything by time. That is a correct *ordering* and a useless *transcript*. Nothing
ever joined your consecutive sentences back together, because nothing was written to do that.

Now it does. Your consecutive lines join into one turn, and a short "yeah" or "idea" that lands mid
sentence gets moved to after it. Long monologues still break into readable paragraphs rather than one
enormous block.

That one fix does three jobs: the transcript is readable, the note is a quarter the size (which helps the
slowness you mentioned), and the AI gets given whole thoughts instead of fragments.

## Greg is Craig

You told me this one. Here is what I found when I went looking.

**Your transcript contains both.** "Craig" once, from your microphone. "Greg" once, from their audio. Two
recordings of the same word, heard differently. Inside that one meeting it is a perfect tie and there is
no honest way to pick.

But your *second* meeting says "Craig" four times and "Greg" not at all. Across your library it isn't
close.

So Toki now settles names by looking at your whole history rather than one meeting. Anything you have
typed yourself outranks that - your own name from Settings, a calendar attendee - because a human typed
it, so it's simply right.

I want to be straight about the guard rails, because this is code that **rewrites what people said**:

- "Great" and "Craig" sound identical to a computer. Ordinary words are thrown out before any matching
  happens. Toki can never turn "that's great news" into "that's Craig news".
- If two spellings are *both* names you've confirmed, Toki assumes they're two different people and
  leaves both alone.
- If it genuinely can't tell, it does nothing. A coin flip is not an answer.

**One I chose not to fix:** "Yashtren" should be "Yashina". Those are four letters apart and don't even
sound alike to the matcher. I could loosen the rules until it catches that - but the same loosening starts
merging people who are genuinely different, and turning one colleague's name into another's is far worse
than leaving a typo. There'll be a "names Toki should get right" box in Settings for exactly these.

## Eleven people became eleven to-dos

Your team call had a chair reading out a register - "Anirud, Bindu.", "Dakshita, Ganesh." - and Toki
produced eleven to-dos that were nothing but people's names.

The AI will format whatever you ask it to, including nonsense. Asked for action items, it dutifully made
action items out of a list of names. So the check is no longer only in the instructions: a to-do now has
to contain something to *do*. A name on its own is a person.

## And the real one: Toki can't tell your colleagues apart

Eleven people in that call, and Toki labelled every one of them "Them". That's the actual reason the
register became to-dos - it had no way to know that a name being *called out* is not a name being *given
work*.

You told me not to fake this, so I'm building it properly. Good news: Toki's design already does half the
job. Because your microphone is recorded separately, **you** are already perfectly identified - that's
something most transcription tools have to work hard for. Only the other side needs splitting up.

Better news: the speech library Toki already ships can do this. It includes speaker separation *and*
something called a speaker embedding manager - which means Toki can learn a voice once, you tell it "that's
Priyanka", and it recognises her in every future meeting. No new dependency, just two small downloads.

I'm waiting on the research before I commit to it, because the questions that matter are the ones I
shouldn't guess at: are the models licensed for a product you sell, how big are they, and how long do they
take on a normal laptop. If a model turns out to be non-commercial, the whole plan changes.

---

## Where it stands

Fixed and tested: the shredded transcript, the fake summary, the poisoned title, the name confusion, the
roll-call to-dos. 1,807 tests, and the new ones are all built from your two actual transcripts rather than
examples I invented.

Still to come: speaker separation, the missing owners on to-dos, the UI slowness (I know exactly what
causes it now - Toki rebuilds the whole meeting list every time you click a note, and again on every
letter you type in the search box), and the Settings box for teaching it names.

Full technical write-up, including everything I decided against and why, is in
`docs/real-meeting-findings.md`.
