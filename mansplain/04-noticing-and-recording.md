# Step 4 — Toki notices, and a real app you can open

*July 15, 2026*

## What we set out to do

Three things you asked for:

1. Make Toki a real app you can **double-click and open** — no typing commands.
2. Make it **never crash** and feel smooth.
3. Let Toki **notice a call on its own** and come alive to ask if you want it recorded.

Plus a bonus: make Toki **invisible to screen recording** (like Cluely) so it never shows up when you share your screen.

## What we did

**A double-click app.** There's now a `Toki` shortcut on your Desktop (and in the project folder). Double-click it and Toki opens. It's a single self-contained file, so it just runs — no "this program can't start" errors, because everything it needs is baked in.

**Never crashes.** Toki now catches every possible error, writes it to its diary, and keeps running instead of falling over. I also stopped it from opening twice by accident (a second double-click just focuses the one that's already there). I ran it through all nine of its screens in a row, over and over — zero crashes.

**Ghost mode.** Toki is now invisible to screen recorders and screen-sharing. You can see it on your screen; a recording of your screen cannot. I proved it: a tool that grabs exactly what a screen recorder sees came back completely blank while Toki was clearly on screen. So Toki can quietly take notes during a shared-screen call without ever appearing on it.

**Noticing a call.** This is the big one. Toki now watches which app is using your microphone. When a real call starts (Meet in your browser, Zoom, Teams, Discord…), Toki waits a few seconds to be sure it's real, then slides in and asks: *"Chrome is in a call — record it?"* — with **Record** or **Not now**. Say Record and it captures both sides (your mic + that app's sound) into files. When you hang up and the app lets go of the mic, Toki notices and **stops on its own**, then moves to writing notes.

It's deliberately polite: it only asks, never records silently. It ignores things that aren't meetings (dictation tools, screen recorders, music apps). If you say "Not now," it won't nag you about that same app for ten minutes.

**How Toki closes now.** Click anywhere outside it, press Esc, or use the shortcut — all close it back to hidden. I rebuilt this part because the old version was flickering shut by accident; now it only closes on a real click outside, and never in the first split-second after opening.

## What broke (and got fixed)

- The app briefly **flashed open then shut** on launch. Cause: it was closing itself whenever it lost the keyboard's attention, which happens constantly. Fix: it now only closes on an actual click outside it.
- A hidden bug where Toki would have **re-asked to record every few seconds** — caught by an automatic test before you ever saw it. Fixed so it asks once per call.

## Homework I did

You asked me to study other free, private notetaker apps (Meetily and friends). I read through about a dozen of them. The short version: our approach is on the right track, Windows actually gives us a cleaner way to grab per-app audio than the Mac apps have, and the biggest lesson for later is how to feed the transcript to the note-writing AI in small chunks so it doesn't get overwhelmed. All of that is written down for when we build the notes step.

## What's next

**Turning the recordings into words** (transcription): feeding the two audio tracks through the on-device speech model so we get an accurate transcript, with your voice and theirs kept separate and echoes removed. That's the foundation the actual notes are built on.
