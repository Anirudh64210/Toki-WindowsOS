# 26. Toki preps you before a meeting

**What we set out to do.** Make the calendar *earn its keep*. Knowing a meeting is coming is fine - but
what you actually want, a few minutes before, is a quiet reminder of **what this meeting is, what happened
last time, and what's still open.** That's a prep card: Toki drops down before a meeting with exactly that,
and one button to join and start taking notes.

## What the card shows

A few minutes before a real meeting, Toki surfaces a small card:

- **The meeting + when** - "Design review - PixelFlow · in 8 min" (amber, so it reads as *soon*).
- **LAST TIME (2 days ago)** - a one-line recap of the previous related meeting, pulled straight from the
  notes Toki already took.
- **STILL OPEN** - the open to-dos from that meeting, with who owns each (amber name tags: "**Priya** -
  confirm brand font", "**Sid** - wire the webhook"). You can even tick one off right from the card.
- **AGENDA** - the bullet points from the calendar invite, if it has any.
- **Join and record** / **Later.**

If it's a meeting Toki has no history for, it's honest about it - no invented "last time." It just shows
the title, the agenda if there is one, and *"First time with this one - I'll take notes if you record."*

## How it finds "last time"

Toki matches the calendar meeting to a past one by its **title/topic** - "Design review" on your calendar
finds your past "Design review" notes. It's deliberately careful: if it isn't confident there's a real
match, it says nothing rather than showing you an unrelated meeting's recap. A wrong "last time" is worse
than none. And it's all **instant** - it reads your existing notes directly, no AI model runs, so it
costs nothing and never makes your PC work harder.

## When it appears (and how it stays out of your way)

The card auto-surfaces about **5 minutes** before a meeting - because the whole point is to catch you
*before* you join, which a card you'd have to remember to open can't do. But it's built to be
**unmissable-once, never annoying:**

- Only for **real meetings** (has a video link or other people) - never a solo calendar block.
- **Once per meeting, ever.** "Later" means it won't come back for that meeting.
- Only when Toki is **idle** - never over your notes, a recording, or anything you're doing.
- Never while you're in a **fullscreen app or presentation.**
- If you walk away, it **tucks itself away** rather than sitting there through the whole meeting.

And it never records on its own - "Join and record" is *your* click. That keeps Toki's core promise
intact: it only listens when you say so.

> **One thing for the owner to confirm:** we chose to have the card *auto-appear* before a meeting rather
> than wait for you to open Toki. It's a visual, calendar-driven nudge - nothing to do with the microphone
> - so it doesn't touch the "no always-listening" rule. But if you'd rather it only show when you hover
> Toki, that's a one-line change. Try it live and tell us which feels right.

## Proving it

Both cards - the full "matched" one and the honest "first-time" one - were rendered and checked by eye in
light and dark themes (a close match to the product design). The matching logic was tested against a
temp database: it correctly finds a prior meeting by title, pulls its open items, **rejects** an unrelated
event, and parses an agenda out of a messy calendar description. What needs you: seeing it actually pop up
5 minutes before a *real* meeting, and "Join and record" opening the call - you can force it early for
testing, and the live moment is yours to try.
