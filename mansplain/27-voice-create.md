# 27. "Toki, schedule a call with Ana tomorrow at 3"

**What we set out to do.** Let you *make* a meeting the way you'd say it out loud. Hold the talk button,
say "schedule a call with Ana tomorrow at 3," and Toki puts it on your calendar — after checking with you
first.

## How it works, in one breath

Hold **Ctrl+Alt+Space**, speak, let go. Toki hears the scheduling request, works out the title and time,
and — instead of silently booking it — **reads it back and asks:**

> *Create "Call with Ana" — tomorrow, 3:00–3:30 PM on Google Calendar? Say yes to create it, or no to skip.*

Say **"yes"** and it's on your calendar. Say **"no"** and nothing happens. That confirm step is the whole
point: a microphone mishears things, and a wrong meeting silently appearing would be worse than useless.
So Toki never books anything off a single sentence — the worst a mishearing can cost you is saying "no."

## The careful bits

- **It knows a request from a question.** "Schedule a call with Ana" creates; "*when* is my call with
  Ana?" or "what meetings do I have tomorrow?" still just *answer* — they never accidentally book
  anything. (The trick: anything that starts like a question can never be a command.)
- **It's honest about people.** You say "with Ana" — that's a name, not an email address, and Toki can't
  invite someone it can't email. So Ana goes in the *title* ("Call with Ana"), and Toki says so out loud,
  rather than inventing an email and pretending to invite her.
- **It asks when it needs to.** "Schedule a call with Ana" with no time → *"When should it be? Say a day
  and time."* No timeless events.
- **"At 3" means the afternoon.** A bare hour like "at 3" now sensibly means 3 PM, not 3 AM — and that fix
  helps the typed calendar composer too. Say "9am" or "8pm" and it takes you literally.
- **Not connected?** It says so plainly and books nothing.

## Where it lives

This slots into Toki's existing "brain" — the same thing that decides whether you're chatting, asking
about your notes, or giving a command. Voice-create is a new kind of command, placed *before* Toki would
ever go search your notes, so a scheduling request is always understood as an action, never answered from
old meetings. And it reuses the exact same understanding-of-time we built for the typed calendar, so
there's one place that turns "tomorrow at 3" into a real date — no duplication.

## Proving it

The whole thing was tested without a microphone: a batch of real phrases proves the right ones are
recognised as "create a meeting" and the tricky look-alikes (questions, "add a note", past-tense
narration) are *not*; and the confirm gate is proven to **never** create until you say yes — including the
nasty edge case where you say "yes" much later about something unrelated (it can't sneak a meeting in).
What needs you: actually holding the button, speaking, hearing the confirmation, and watching the event
land on your real Google Calendar.
