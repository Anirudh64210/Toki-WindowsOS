# Step 3 — Toki can hear

*July 15, 2026*

## What we set out to do

Give Toki ears. Two of them, kept separate:

- **"You"** — your microphone (your voice).
- **"Them"** — the sound coming *out of the meeting app*, and only that app (the other people on the call).

Keeping them on separate tracks is how Toki later labels who said what, for free. And the golden rule: Toki must record the meeting app **only** — never your music, never a YouTube tab, never everything on your screen.

## What we did

1. **The microphone.** The easy ear. Toki records your default mic to a file. Tested it: recorded 5 seconds, got a real sound file, and the meter showed it picked up the room. You can play the file and hear yourself.

2. **The meeting-app ear.** The hard one — the single most technically risky part of the whole project. Windows lets a program record the audio of one specific app (and its helper processes, which matters because web meetings play their sound through a hidden helper). Wiring this up meant talking to Windows at a very low level. It works.

## How we proved it — without needing a real meeting yet

I didn't want to *assume* it worked. So I made Toki play a steady test tone in one window, and had a second Toki record **that specific window**. Two results, side by side:

- Recording the tone-playing app → the meter read **20%**, which is exactly the volume the tone was set to. It caught the real sound.
- At the same moment, recording a **different, silent app** (Slack) → the meter read **0%**. Even though the tone was audible on the computer, Toki heard nothing from the app that wasn't making noise.

That second result is the important one. It proves Toki records *only the app you point it at* — not whatever else is playing. That's the privacy promise, demonstrated, not hoped for.

## What's left on this step

The final sign-off is your ears on a real call: join a Google Meet with someone, let Toki record both tracks, then play them back — your voice on one, their voice on the other. Everything is in place for that test whenever you're ready.

## What's next

**Noticing meetings by itself.** Right now Toki records when told which app to listen to. Next, it learns to *notice* that a call has started (by watching which app switches your microphone on), quietly slide in its "Record this meeting?" pill, and — when the call ends — stop on its own.
