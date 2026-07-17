# Step 12 — Real glass, calm recording, and cleaner buttons

*July 17, 2026*

## What we set out to do

You tested the app and had a clear list: the recording pill was a distracting animated bar stuck on screen, clicking outside kept closing things you were reading, the "glass" pill wasn't actually see-through, it had ugly sharp black corners, and the little buttons (copy, resize, close) were ambiguous and ugly. Also: make the Library window match the design.

## What we did

1. **The recording pill is calm now.** The old bar showed a fake, forever-looping "sound wave" (it wasn't even your real audio) and sat there the whole meeting. It's now a small, quiet chip — a gently breathing dot, the time, and a stop button.

2. **Toki stops disappearing when you click away.** Before, clicking into your real work while reading notes made them vanish. Now only the little Home menu and the "nothing was said" message close on an outside click — your notes, Library, and settings stay put until you close them yourself.

3. **The pill is genuinely see-through now.** You were right: apps like Cluely have transparent windows that *still* hide from screen recordings — so my earlier "it's impossible" was wrong. I reworked the window to be truly translucent, and confirmed both things at once: you can see your wallpaper through it, **and** it's still completely invisible in a screen recording. The ugly black corners are gone too.

4. **The buttons are crisp and clear.** Copy and "resize" used to look almost identical because they were made from plain text symbols. They now use Windows' proper icon set, so copy, open-in-window, and close are instantly distinguishable — everywhere in the app.

5. **The Library looks like the design.** Each meeting now shows a little calendar date badge, how long it was, when it happened, a short summary, and small tags for open to-dos and how many people were on the call.

6. **The Library grows out of the pill.** Opening the Library no longer snaps a window open — it gently scales up from the bottom (toward where the pill sits) with a soft fade, so it feels like the pill stretching into a window.

7. **You can reorder your notes by dragging.** Pick up any note in the Library and drag it up or down; the others slide aside to make room, just like rearranging apps on an iPhone home screen. The new order is saved.

## A second polish pass

After a first look you had clear notes, and I fixed them:

- **The notes are now monotone and calm.** The coloured section headings and the little green tick looked busy, so everything is now a quiet grey — clean and Apple-like, with plenty of breathing room. (I also removed a small "on-device" badge you didn't ask for.)
- **Opening the Library now truly grows out of the pill.** Before, it popped up in the middle of the screen while the pill sat at the bottom — disconnected. Now the window literally expands out of the pill's exact spot.
- **Dragging a note feels right.** A dropped note used to sometimes get stuck halfway between two others; now it always glides cleanly into place, and the "make room" motion is smoother.

## What's next

The remaining big item is **live transcription** — turning speech into text *while* the meeting happens, so notes are ready almost instantly when you stop.
