# Step 5 - Never freezes, and properly alive

*July 15, 2026*

## What we set out to do

Two things you asked for: Toki must **never hang or freeze** - always there, always ready, but only shows up when you call it. And the animations should feel **smooth, cute, and alive** like the Mac version - especially the "working on your notes" screen, which should feel like a little character actually doing things, not just some glowing paper.

## What we did

**Found and killed the freeze.** There was a specific bug: when a recording stopped, Toki waited for the audio files to finish saving *before doing anything else* - and while it waited (up to 3 seconds), the whole thing locked up, including the part that listens for your clicks. So it looked dead. Now the saving happens quietly in the background and Toki reacts instantly. I also made the part that watches for clicks do almost nothing (just a quick "was that click on Toki or not?"), so it can never slow anything down. And I removed a piece that was watching every key you press - unnecessary and a bit invasive.

**Made re-launching work.** Toki only ever runs as one copy (so you never get two pills). Before, if you double-clicked while it was already running, nothing happened. Now double-clicking a running Toki just brings it to the front. There's also a **"Restart Toki"** option in the tray menu (right-click the pink icon) if you ever want a fresh start.

**Redid the loading animation.** The "working on your notes" screen is now a little scene where Toki does a different thing at each step, and you watch it progress:

1. **Listening back** - sound-wave rings ripple out from Toki.
2. **Reading it over** - a little document appears with a highlight sweeping down it, like Toki is reading each line.
3. **Finding to-dos** - checkboxes tick off one by one.
4. **Writing it up** - a pen draws an underline across the page.

The whole time, Toki is breathing, blinking, and gently bobbing, with a soft glow - and a progress bar fills up underneath. Rounded, clean, and spaced out. It actually feels alive now.

I also softened the way the pill grows and shrinks between screens so it glides instead of snapping.

## How I checked it

I can't screenshot Toki normally (it's invisible to screen capture - that's the ghost feature). So I added a hidden developer switch that turns ghost mode off just for testing, then photographed the loading screen at four different moments. The photos clearly show the four different steps cycling - rings, then the document, then the ticking checkboxes, then the pen. It's genuinely animating.

Everything still passes its 21 automatic tests, and I ran it through every screen over and over with zero crashes.

## What's next

**Turning the recordings into words** - the transcription step. Feeding the two audio tracks into the on-device speech model so we get an accurate written record, your voice and theirs kept apart, echoes removed. That's the groundwork the real notes are built on.
