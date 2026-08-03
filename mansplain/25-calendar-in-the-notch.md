# 25. Your week, in the notch

**What we set out to do.** Now that Toki can safely reach your Google Calendar (chapter 24), give it a
face: your next meetings on the home card, a full week you can browse, and — the part that makes it feel
alive — creating, editing, and deleting meetings right from the notch, with as little friction as
possible.

## The home card, redesigned

The moment you're connected, Home changes. Under the greeting there are now two buttons side by side —
**Meeting** (opens your calendar) and **Take Notes** — and below them a **"COMING UP"** list: your next
few meetings, each with a little date badge, the time, and a white **Join** button when there's a video
link. The meeting that's about to start gets a gentle amber nudge — *"in 8 min"* — so the thing you need
is the thing that stands out. At the bottom, a small green dot and your account, so you always know it's
connected and to whom. It matches the product design exactly.

## The week, the way a calm app shows it

Tap **Meeting** and the notch becomes a week view. We deliberately did **not** cram a tiny 7-column grid
into a narrow notch — every good calendar app on a small surface (Fantastical, Notion Calendar, Cron)
shows an **agenda**: a simple list grouped by day. So Toki does the same. Each day is a heading; today is
picked out in amber with a thin **"now" line** showing the current time against your meetings. You can
step forward a week and jump back to "Today." It reads instantly, no squinting.

## Creating a meeting — just type it

This is the part built for least friction. Hit **＋ New** and you get **one box**. Type the way you'd say
it: *"Design sync tomorrow 2pm with ana@work.com."* As you type, a green line shows Toki's understanding —
*"Tomorrow · 2:00 – 2:30 PM"* — so you trust it before you commit. It pulls out the time and the email for
you. Three small chips let you change the length, toggle a **Google Meet** link (on by default), or add
more people. Press **Create** and it's on your calendar, with the Join button already there. No forms, no
date-picker dialogs.

## Editing and deleting — safe, but fast

Hover any meeting and a **…** menu appears: **Join · Edit · Open in Google Calendar · Delete.**
- **Edit** reopens that same one-box composer, pre-filled — change the title or type a new time to move it.
- **Delete** is where we were careful. If it's just you, it deletes instantly and shows an **Undo** bar
  for a few seconds — no nagging dialog for a harmless action. But if the meeting has **guests** (who'd
  get a cancellation email), Toki asks once — *"Cancel this meeting? Guests will be notified"* — because
  that one isn't really undoable. Fast where it's safe, careful where it counts.

## Little touches that make it feel finished

Meetings fade in with a soft stagger. The imminent one glows amber. The "now" line ticks along your day.
Empty weeks show Toki and a calm *"Nothing scheduled this week."* Everything is built on one shared piece
of code, so the home glance and the full week always agree — and it's all provider-neutral underneath, so
**Outlook** can slot in later without redrawing a single screen.

## Proving it (not guessing)

Every screen was rendered and checked by eye — the connected home card, the week view, the create
composer with its live preview. And the real actions were run against a real Google account: create an
event, move it to a new time, delete it — all confirmed, all cleaned up. One bug got caught and fixed
along the way (a "＋" in a web address being read as a space). What needs a human — signing in, a real
Join opening a real call — is yours to try; the rest is verified.
