# Step 2 — The pill is alive

*July 15, 2026*

## What we set out to do

Build Toki's body: the little dark pill that floats at the bottom-center of the screen and smoothly changes shape depending on what's happening — before teaching it to actually record anything.

## What we did

1. **The brain.** A small piece of code that knows every "mode" Toki can be in (hidden, asking to record, recording, writing notes, showing the library…) and the legal moves between them. It refuses illegal moves — Toki can never jump from "hidden" straight to "showing notes," the same way a traffic light can't jump from red to yellow. Twelve automatic checks prove the rules hold.

2. **The body.** One always-on-top window with no borders. Its trick: the window is actually a large invisible rectangle that never moves — only the dark pill inside it grows and shrinks, with a smooth quarter-second animation. Mouse clicks pass straight through the invisible parts.

3. **The faces.** Every screen from the design, rebuilt as placeholders: the "Record this meeting?" question, the recording bar with a dancing waveform, the live-transcript view, "Finding action items," the notes page, home, library, and settings. The pink Toki blob is there too. The words are fake for now — the look and the flow are real.

4. **The handles.** A pink icon in the system tray (right-click it for demos of every mode) and a keyboard shortcut — **Ctrl+Alt+Space** — that summons or hides the pill from anywhere.

## What broke

Two things, both small:

- The tray-icon component wanted a more specific description of which Windows versions we support. We gave it one ("Windows 10 2004 or newer") — which we needed anyway, because the audio tech coming in step 3 requires exactly that.
- A funny one: my automatic screenshots kept showing *no pill*, and I chased a "bug" that didn't exist. Transparent windows like Toki's are invisible to the basic screenshot technique — like a vampire that doesn't show up in mirrors. Sid could see the pill perfectly the whole time. New rule: Toki keeps a diary of everything it does (a log file), and that diary — plus human eyes — is how we verify, not screenshots.

## How we know it works

Sid drove it: summoned the pill with the shortcut, walked through home, library, settings, dismissed a fake meeting prompt, ran a fake recording start-to-finish. Toki's diary recorded every step, and every step matched what was clicked.

## What's next

**Ears.** The riskiest piece of the whole project: capturing two separate audio streams — your microphone, and the sound coming *from the meeting app only* (not your music, not YouTube). We'll prove it works by recording a real call and listening to the files. Nothing else proceeds until our ears confirm it.
