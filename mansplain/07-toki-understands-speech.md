# Step 7 — Toki turns talking into text

*July 15, 2026*

## What we set out to do

Give Toki the ability to actually **understand speech** — take the two audio recordings (your voice, and the meeting app's audio) and turn them into an accurate written transcript, with each line labelled as either you or them, all running **on your computer** with nothing sent to the cloud.

## What we did

1. **Prepared the sound.** The speech AI expects audio in a specific format (16,000 samples a second, single channel). Your recordings come in at higher quality and in stereo, so Toki quietly converts them first — like translating a document into the language the reader understands.

2. **Installed the speech engine.** Toki now uses **Parakeet**, NVIDIA's on-device speech-recognition model — the same class of model the Mac version uses. The first time it's needed, Toki downloads it once (about 630 MB) and keeps it on your machine forever. After that it's instant and offline.

3. **Transcribed both sides separately.** Your mic and the meeting-app audio are transcribed independently, then stitched together into one conversation on a shared timeline, with each line tagged **YOU** or **THEM**.

4. **Removed the echo.** When you use speakers (not headphones), your microphone also picks up the other person's voice coming out of your speakers — so their words can show up twice. Toki spots those duplicates and removes the mic's copy, keeping the clean one. It's careful with short words like "yeah" or "okay" — those only get removed if they're an exact match, so it never deletes something you actually said.

## How I proved it works

I had the computer read a paragraph aloud and recorded it, then ran it through Toki. The result came back almost word-perfect — including the very last sentence, which proves Toki isn't cutting off the end of long recordings (a bug the Mac version fought for months). It ran faster than real time (a 23-second clip transcribed in 10 seconds).

Then I recorded two different sentences as separate "you" and "them" tracks. Toki transcribed both correctly and merged them into one labelled conversation. And I confirmed it all still works in the final packaged app, not just my development version.

## One honest note

Two smaller pieces are deferred for later: a **live preview** that shows words appearing while you're still talking, and a **backup engine** (Whisper) that kicks in only if the main one ever fails. The main event — accurate, private, on-device transcription — is done and working.

## What's next

**Remembering meetings.** Now that Toki can produce a transcript, the next step is saving each meeting into a searchable library on your machine, and doing a first pass at pulling out the useful bits (decisions, to-dos) — before the smarter AI-written summaries come after that.
