# Step 11 — Making Toki fit your PC (and never lose a meeting)

*July 16, 2026*

## What we set out to do

You tested Toki in a real 30-minute meeting and hit a bad one: you were muted, the
other people were talking, and at the end Toki said **"nothing was said."** A whole
meeting's worth of notes, gone. You also noticed the PC felt **clunky and glitchy**
the first time you opened Toki each day, and that processing was still slow. So this
step is about reliability: never lose a meeting, and never get in the way of your PC.

## What I found (the real reason)

I read the logs and the actual recording from that meeting, and two things had gone
wrong at once:

1. **The other people were never recorded.** When you start Toki *yourself* — from the
   Home screen or the shortcut — it only recorded *your* microphone. It never listened
   to the meeting app, so the other side was blank. And since you were muted, there was
   nothing on your side either. Result: silence.

2. **Even your own audio crashed the transcriber.** The speech engine tried to process
   the entire 30-minute recording in one giant gulp and fell over.

Then I found the thing underneath *both* of those, and behind the clunkiness too: **your
laptop has about 6 GB of memory, and Toki was trying to hold two big things in it at
once** — the 2.5 GB notes-writer *and* the 630 MB speech engine — on top of your browser
and your meeting app. That doesn't fit. When memory runs out, Windows starts frantically
shuffling things to disk (that's your laggy mouse), and programs that ask for memory and
don't get it simply crash (that's your lost meeting).

## What I did

1. **Toki now records the other people even when you start it yourself.** If a meeting
   app is active when you hit record, Toki taps it too — so muting yourself and hitting
   record finally captures the room.

2. **Long meetings can't crash it anymore.** Instead of one giant gulp, Toki now
   transcribes a long recording in ~3-minute pieces, each cut at a natural pause so no
   word gets sliced in half. I tested this on your actual failed recording and it went
   right through where it used to die.

3. **Toki now fits in your memory.** On a smaller PC, Toki keeps **only one big thing in
   memory at a time**: it puts the notes-writer away while it's listening, and puts the
   speech engine away while it's writing notes. It also **stops preloading** the big
   notes-writer the moment you launch — that preload was the main thing making your PC
   feel clunky. Everything loads quietly, in the background, only when it's actually
   needed.

4. **Small stuff:** accidental one-second taps no longer show up as empty meetings in
   your Library, and the startup warm-up now runs at low priority so it never fights
   with whatever you're doing.

## The honest part

Your PC being at 6 GB is genuinely tight for on-device AI — the notes-writer alone is a
big model. What I've done makes Toki *fit* and *stay reliable* on it, by never asking for
more than the machine can give at one moment. The trade-off is that on a small machine
Toki loads a model right when a meeting ends (a few seconds) instead of having it ready —
but in exchange, your PC stays smooth the whole time and a meeting never gets lost.

## What's next

Your list still has the **see-through "glass" pill** and getting processing faster.
The glass look is very doable on Windows 11, but it touches the same delicate window
plumbing that once turned the whole thing black, so I want to do it carefully on its own
rather than rush it in next to these reliability fixes.
