# Step 14 — Toki moves to the top, like a notch

*July 28, 2026*

## The big idea

Toki used to be a little pill at the *bottom* of your screen. You felt that was too close to another app's look — so we moved Toki to the **top of the screen**, hanging down from the middle edge like a **notch** (the way the Mac version was always meant to be). It's flush with the top, rounded only on the bottom, and it grows *downward* when it opens and *tucks up* when it's done.

## How you use it now

- **Toki is invisible until you want it.** Nothing sits on your screen.
- **Reach the top-middle edge** (push your mouse up there) or **open the app** → **Toki peeks out** — just the cute character, saying hello.
- **Click Toki** (or rest on it a moment) → it **opens into your page** (the welcome screen, or wherever you last were).
- **Move on / click into your work** → the page **closes off completely** and gets out of your way. You bring it back whenever you like by reaching up to the top-middle again. It still **remembers where you were** — if you were on Settings, coming back brings you to Settings, not the welcome page.

## The one that took a few tries: "close off" that actually stays closed

At first, when you moved on, Toki left a little strip hanging at the top. You didn't like that it sat on your screen — and, testing in Chrome, it did something worse: **it kept popping back on its own.** We looked at the log and it was flip-flopping every couple of seconds.

The reason is simple once you see it: **the spot where you summon Toki (top-middle) is exactly where your browser tabs live.** So the moment Toki closed, your mouse was still up near the tabs, and Toki thought you were asking for it again.

The fix: to bring Toki back you now have to **reach *up* to the top from inside the page** — a cursor that's just sitting up on the tabs doesn't count. So you can close Toki, keep clicking around your tabs, and it **stays gone** until you actually mean to call it. (The hotkey `Ctrl + Alt + Space` and the tray icon always work too — and the hotkey now brings the **small Toki**, not the big welcome window.)

## A few more polish fixes

- **Pitch-black notch.** It used to be a slightly see-through grey that looked washed-out. It's now a **solid black** notch — cleaner, and it reads like a real "cut-out" at the top of the screen.
- **"Nothing was said" stopped looping.** If a recording caught nothing, you'd tap *Okay*, it would close — and then reappear the next time you looked up, forever. Now dismissing it is final: Toki forgets it and greets you fresh next time.
- **The loading strip is tidy.** While Toki writes your notes, it leaves a thin dark strip with a gentle glowing shimmer — its "I'm working" heartbeat. (It used to show a squashed slice of Toki's head, which looked bad. Now it's just the shimmer.)
- **Recording Toki is tinier** — a very small pair of upside-down eyes, so it never bumps into your browser tabs or the snipping tool.

## While recording

Toki does a little **flip and fall** and hangs there **upside-down**, with just its **eyes peeking** over the edge, blinking — tiny and alive, so it never gets in the way of your browser tabs during a call. Hover it for the timer and Stop.

## Two earlier fixes

- **It worked in testing but not for real.** The hide-and-tuck timing quietly stopped working right after launch — because Toki was busy loading its speech/AI brains at that moment and the timers got starved. We gave them priority so they always run.
- **A mis-click made a fake meeting.** A one-second accidental click recorded a few garbled words and Toki saved it as a "meeting." Now anything that short or that empty is thrown away — a slip of the mouse is never a meeting.

## What's next

The **Settings page is still placeholder** (it looks right but isn't fully wired), and the **big speed win** — turning speech into text *during* the meeting — is the next real project (see step 15 for why it matters).
