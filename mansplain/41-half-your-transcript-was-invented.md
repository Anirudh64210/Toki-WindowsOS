# 41 - Half your transcript was invented

*2026-08-14. You asked why talat.app gets clean transcripts out of the same Parakeet model and the
same Qwen model that we use. I stopped guessing and measured your own recording. The answer was not
the models.*

## What the measurement found

Toki records a meeting as two separate files: `you.wav` (your microphone) and `them.wav` (the
meeting audio). Same recording, same code, same settings, same model. Here is today's call, thirteen
minutes:

| | your mic | the meeting |
|---|---|---|
| how loud, typically | 0.0008 | 0.0635 |
| how much of it is speech-ish | 3% | 66% |
| how much is near-silence | 73% | 32% |
| lines of transcript produced | **10** | **242** |
| what a line looks like | "Oh my god, singy cocky and the beef in the table." | "Yeah, I pinged you in the team space itself." |

Your microphone is running about **38 decibels quieter** than the meeting audio. That is not a small
difference - on a volume slider it is roughly the difference between a whisper and a conversation.

And here is the part that matters: when you hand a speech recogniser thirteen minutes of near-silence,
it does not politely return nothing. It returns confident nonsense. "Oh my god, singy cocky and the
beef in the table" is not a mishearing of something you said. Nobody said anything. The model was
asked what was there, it is not allowed to say "nothing", so it invented.

**So roughly half of every transcript Toki produced was fiction.** And the summary is written from the
transcript. And the speaker separation runs on the transcript. And search indexes the transcript. The
note quality problem you have been complaining about was never the note writer. It was being fed
made-up input, and it faithfully summarised the fiction.

That is lesson one in CLAUDE.md, by the way: garbage in, garbage out. I had to relearn it.

## Fix one: teach Toki to say "nothing was said here"

There is now a gate in front of the recogniser. Before any audio is transcribed, a small model called
Silero looks at it and marks where somebody is actually speaking. The recogniser only ever sees those
parts. Silence is never transcribed, so silence can never be invented.

Two things I got wrong on the first attempt, both caught by testing against a real recording rather
than reasoning about it:

**I threw away short words.** My first setting ignored anything shorter than a quarter of a second, on
the logic that a quarter-second of sound is a cough or a keyboard. Comparing against the ungated
version, that binned **187 words of real speech** - and they were exactly the words you would miss:
"yep", "yes", "go ahead craig", "hey david what you got". Real conversation is full of one-word turns.

**I put the confidence threshold in the middle.** Silero's default is 0.5, which treats "wrongly
called it speech" and "wrongly called it silence" as equally bad. They are not equally bad. Calling
silence speech costs a second of wasted computation. Calling speech silence costs you a sentence you
said and will never see. The threshold is 0.30 now, deliberately biased toward keeping audio.

## Fix two: turn the quiet stream up before anything reads it

The gate helps, but Silero is itself a trained model, and a signal 38dB below what it expects is
exactly the case it gets wrong. So each stream is now levelled first - lifted to roughly the volume
the recogniser was trained on - and *then* gated.

Two mistakes here too, and this pair is worth telling you about because they are both the same kind
of mistake: a rule that sounds obviously correct and quietly ruins the result.

**"Never let it clip" held the gain to nothing.** I capped the lift so the single loudest sample in
the file could not distort. That sounds unarguable. In practice one transient - a cough, a knock on
the desk - held your entire thirteen-minute recording down to **1.7x** when its speech needed about
ten times that. One sample was deciding the volume for a whole meeting. Now the ceiling is measured
from the loudest 0.1% of the file rather than the single peak, and the same recording gets **9.4x**.

**I was levelling in the wrong order.** The gain was applied after the gate had picked its regions,
which meant Silero was doing its hardest work on the worst possible input, on precisely the stream
this whole exercise exists for. Levelling now happens first.

The other thing to know: the "how loud is this" number is the 95th percentile of the file, not the
average. An average over a recording that is three quarters silence reports a level nobody ever
spoke at. The 95th percentile of a mostly-silent recording is speech.

## Where it landed

| | lift applied | speech found | words transcribed |
|---|---|---|---|
| your mic | **9.4x** | 17% → 46% | 73 |
| the meeting audio | **1.0x - left completely alone** | 72% | 1595 (was 1596) |

Both halves of that table are the result. The broken stream got fixed, and the healthy stream was not
touched: 1595 words against 1596 before, which is one word of boundary noise. A stream that is already
fine gets nothing done to it. Toki only ever lifts, never turns anything down.

Decoding also got faster, because it is no longer spending minutes transcribing silence.

## The honest part

Your microphone transcript still has hallucinations in it. Less of it, and the worst of it is gone,
but it is still there - "Oh my money is a bumblebee" survived.

I can keep tuning this, but I would be tuning against a broken input. Your mic is capturing you 38dB
below the meeting audio, which means it is barely capturing you at all, and no amount of processing
after the fact invents information that was never recorded in the first place.

**The real fix is one Windows setting.** Right-click the speaker icon in your taskbar → Sound settings
→ Input → pick your microphone → push the input volume up, and turn ON any "microphone boost" if the
device offers one. That single change is worth more than everything described above, on your
recordings specifically. Everything above is what makes Toki behave for *everyone else*, whose mics
we do not control.

## What this did not touch

The notch and the mascot revamp you asked for are not dropped - they are tracked as tasks 11 and 12
and they are next. This round was accuracy, because you said accuracy over anything.
