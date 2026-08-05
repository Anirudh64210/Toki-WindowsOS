# 32 — A long day of real use

This wasn't a "build a milestone" day. It was a **use-the-app-and-fix-what-hurts** day. Sid ran Toki like a
real person would, hit real problems, and we fixed them one at a time — then added the features they'd been
asking for, and started making the whole thing *feel* nicer.

## The annoying stuff, gone

- **Scrolling fought back.** In a chat with Toki, if you scrolled up to re-read something, it snapped you
  right back to the bottom every time a new word arrived. Now it only follows along if you're *already* at
  the bottom — scroll up and it leaves you in peace. (And there's a little **"↓ Latest"** button to jump
  back down when you want.)
- **Text was cut off, everywhere.** The bottom line of a lot of panels sat jammed against the edge and
  looked chopped. Turns out the text was measured perfectly — it just had no breathing room, and on some
  screens that tipped into a real clip. Gave every panel a little bottom padding, and fixed a nastier bug
  where opening and closing the notch a hundred times slowly corrupted how it drew itself.

## The name, the greeting, the onboarding

Toki kept calling Sid the wrong name. So now, the very first time you open Toki, it asks **"What should I
call you?"**, shows a friendly one-screen privacy promise (*everything stays on this laptop*), and says
you're all set. And you can change your name later from a new **Settings tab in the Library window** — with
a proper Save/Cancel and a little green *"Saved"* — and it updates your greeting everywhere, live.

## Teaching Toki to listen better (and the day voice "broke")

This was the hard one. Voice suddenly stopped working — Toki just said *"I didn't catch that"* over and
over, forever. We chased it hard: I'd added an "auto-listen-again" feature that opened a *second*
microphone, and that second mic was stealing the device so the *real* one recorded silence. Ripped that
out. But it *still* didn't work — and the real culprit was hiding in plain sight: **Sid's microphone was
muted in Windows.** Once that was on, voice came back. (I added a one-command mic checker so we never spend
an hour on that again.)

While we were in there: when Toki mishears a *command* ("start reporting" instead of "start recording") it
now says *"did you mean start recording?"* instead of guessing. And **making a meeting by voice is a real
conversation now** — you can say "actually make it 6 to 7pm tomorrow", change the title, add people, and
only when you say "yes" does it get created.

We also killed an embarrassing one: Toki was replying with *"Toki: Hey Anirudh! … 🚀"* — a name label it
shouldn't say and an emoji it's told never to use. Fixed the wording it's given, and added a scrubber so
those never slip through.

## Calendar bits

The meeting-maker now has **separate boxes** for title, day, and time instead of one cramped line. **"Copy
link"** now copies the actual **Google Meet** link you'd send someone (it used to copy the useless calendar
page). And when a meeting's happening right now, Toki offers to **Join** it.

## Making it feel nice (the start of the big polish)

We began the real UI polish. There's now **one place** that decides how fast things move and how they ease,
so the whole app moves as one system — the notch now *glides* to a stop instead of that robotic
slow-fast-slow, new messages **float up** into view, and "thinking" shows three calm pulsing dots. Small
things, but they're what separates "an app" from "a *nice* app."

## What we deliberately left for later

The **speech recognition itself** — Toki mis-hearing "meetings" as "eating things" — is the on-device model
being a small model on short clips. Fixing *that* properly is its own research project, and Sid said park it
for now and keep polishing. It's written down for when we come back to it.
