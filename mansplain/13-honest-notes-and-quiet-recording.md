# Step 13 - Honest transcripts, quiet recording, and small fixes

*July 27, 2026*

## What you told me

After testing real 20-minute meetings you had a sharp list:

1. The transcript was breaking **in the middle of words** - nonsense.
2. A 20-minute meeting took about **6 minutes** to process (it used to feel like 40 seconds).
3. Small things: an em dash in the "Chrome is in a call" prompt; "Welcome back, Sid!" being stuck on one name; the record button's red being too harsh; the Library not really growing *out* of the pill; and the recording pill sitting there with a timer and a button the whole meeting.

## What was actually wrong (and the fixes)

**1. The transcript was cut mid-word - fixed, and proven.**
Toki turns speech into text in tiny pieces (a long word like "Deepika" comes out as several pieces). To make readable lines, it starts a new line whenever there's a pause. The bug: it was allowed to start a new line *in the middle of a word* - so "Deepika" became four broken lines, and the notes turned into gibberish like "a udi o". I dug into exactly how the speech engine labels words and found my first assumption was wrong about how it marks where a word begins. Once I fixed that, it only ever breaks lines **between whole words, at the end of a sentence or a real pause**. I re-ran it on the exact meeting that broke: it went from **900+ broken fragments to 82 clean sentences**, every word whole.

**2. The 6 minutes - the honest story.**
Nothing got slower. The engine chews through about 4 seconds of audio every 1 second. A 20-minute meeting has **two** microphones (you + the other side) = about 40 minutes of audio to get through, which is several minutes. The "40 seconds" you remember was a 1–2 minute test call. Real meetings are just much longer.
- **One easy win now:** on calls where your mic was muted the whole time, Toki was still spending *half* the time "listening" to pure silence. It now skips a silent side entirely - so muted-mic meetings process about twice as fast.
- **The real fix** (next up) is transcribing **live during the meeting**, so when you hit stop there's almost nothing left to do.

**3. The recording pill gets out of your way.**
While recording, Toki now shrinks to just the **little character** (breathing, blinking) sitting quietly at the bottom - no timer or button in your face. Click it if you want the timer and Stop. (It also used to show a *fake* transcript during recording - that's gone; it was pretending.)

**4. The small stuff, all done.**
- Softer, warmer record button (the harsh red is gone).
- "Welcome back" now uses your actual name instead of a hardcoded one.
- No more em dash in the "in a call" prompt.
- The Library opens fast and cleanly now, instead of popping a box and then animating inside it.

## What's next

**Live transcription** - the real answer to the wait after a long meeting.
