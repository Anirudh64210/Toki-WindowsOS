# Step 14 — Toki moves to the top, like a notch

*July 28, 2026*

## The big idea

Toki used to be a little pill at the *bottom* of your screen. You felt that was too close to another app's look — so we moved Toki to the **top of the screen**, hanging down from the middle edge like a **notch** (the way the Mac version was always meant to be). It's flush with the top, rounded only on the bottom, and it grows *downward* when it opens and *tucks up* when it's done.

## How you use it now

- **Toki is invisible until you want it.** Nothing sits on your screen.
- **Reach the top-middle edge** (push your mouse up there) or **open the app** → **Toki peeks out** — just the cute character, saying hello.
- **Click Toki** (or rest on it a moment) → it **opens into your page** (the welcome screen, or wherever you last were).
- **Move on / click into your work** → the page **tucks itself into a thin sliver** at the top. It's not gone — a little hangs there. **Hover that sliver** and the exact same page slides back down. Leave it long enough and it fully tucks away.
- It **remembers where you were** — if you were on Settings and click away, coming back brings you to Settings, not the welcome page.

## While recording

Toki does a little **flip and fall** and hangs there **upside-down**, with just its **eyes peeking** over the edge, blinking — tiny and alive, so it never gets in the way of your browser tabs during a call. Hover it for the timer and Stop.

## Two things we fixed along the way

- **It worked in testing but not for real.** The hide-and-tuck timing quietly stopped working right after launch — because Toki was busy loading its speech/AI brains at that moment and the timers got starved. We gave them priority so they always run.
- **A mis-click made a fake meeting.** A one-second accidental click recorded a few garbled words and Toki saved it as a "meeting." Now anything that short or that empty is thrown away — a slip of the mouse is never a meeting.

## What's next

The **Settings page is still placeholder** (it looks right but isn't fully wired), and the **big speed win** — turning speech into text *during* the meeting — is still the next real project.
