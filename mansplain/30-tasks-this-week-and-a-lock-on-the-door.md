# 30 - "Tasks this week", a proper sort menu, and a lock on the front door

Three things this round: the assistant bug you caught, the Library sort you asked for, and a security
pass so strangers can't poke at Toki.

## The bug you found

You asked Toki "what tasks do I have for this week" and it dumped *every* open task instead of this
week's. You were right - that's a real bug, and a revealing one.

Under the hood Toki was reading "this week" as "meetings that *happened* this week." But most of your
meetings are recent, so "this week's meetings" was basically all of them - hence the dump. What you
actually meant (what anyone means) is "what's *due* this week." Those are different questions.

So Toki now thinks about deadlines the way a to-do app does. For each open task it works out when it's
actually due - reading the free-text deadline ("Friday", "next week", "tomorrow") relative to the
meeting it came from - and then sorts your tasks into three honest buckets:

- **Overdue** - deadlines that have already passed.
- **Due this week** - the ones actually landing in the window you asked about.
- **From this week's meetings, no deadline set** - so nothing silently disappears.

And it tells you the window it used ("2 tasks this week:") so you can see it understood you. "What's
overdue," "what's due Friday," "anything due tomorrow" all work now too - they used to slip through to
a generic search. One nice touch: if a task is due "Friday" and it was set on a Monday, Toki knows you
mean *this* Friday, not last Friday.

I checked this the careful way - 15 new automated tests, including one that recreates your exact
question and proves the old dump is gone.

*(Aside: I had two research passes study how Granola, Fireflies, Otter and the task-app world handle
this, and they landed on exactly this design - deadline-based, with the undated fallback shown, and the
window echoed back. It also confirmed the same three things are where every competitor is weakest:
saying which meeting an answer came from, showing the scope, and getting deadlines right. Toki now does
all three.)*

## The Library sort menu you asked for

There's now a sort control (the little up/down arrows above the list). Newest first, oldest first,
longest first, shortest first, most open tasks first, or your own manual drag order. It remembers your
choice. Turning on drag-to-reorder automatically switches to manual order, so a drag never scrambles a
sorted list.

## A lock on the front door

I had a security specialist go through every way Toki touches the outside world. The good news: most of
it was already solid from last round. One real hole worth explaining in plain terms:

Toki makes sure only *one* copy of itself runs at a time, using a couple of named Windows "flags." The
problem was those flags had well-known names and no lock on them - so in theory another program on your
PC could grab the names first and either stop Toki from starting at all, or nudge its window around. Now
the flags are named uniquely per Windows user and locked to you alone, and Toki double-checks that a real
Toki is actually running before it steps aside. I tested it live: launching Toki twice correctly leaves
exactly one running.

A handful of smaller hardening steps too: every "open this link" now refuses anything that isn't a real
web address (so a booby-trapped meeting invite can't launch a program), meeting-note text handed to the
AI is clearly fenced off as "information, not commands," and file names built from meeting titles can
never escape Toki's own folder. Plus 33 new security tests that keep all of this honest.

A few things I deliberately *didn't* change, and wrote down why: Toki's push-to-talk key-listener looks
scary on paper (it's the same Windows feature keyloggers use) but it only ever watches for your one
shortcut and is the only way "hold to talk" can work - so it stays, documented. And I left the way your
API key is encrypted alone, because "improving" it would have wiped the key you already saved.

## The tally

520 automated tests pass (up from 472 two rounds ago). Every built-in self-check passes. The
double-clickable app is rebuilt and confirmed running. Worth a try: ask it "what tasks do I have this
week" and "what's overdue," and play with the new Library sort menu.
