# 33 - The premium pass

Day two of the same session, and the goal changed. Yesterday was *stop the app from hurting*. Today was
**make it look like something you'd pay for** - plus a handful of "wait, that's still wrong" fixes Sid
caught while using it.

## Settings finally has one home

Toki's settings had grown into two half-places: some in the little notch at the top of the screen, some in
the Library window. Sid's call was clean and correct - **the notch only gets the things you flick on and
off at a glance** (light/dark, hide-from-screen-recordings, the glassy look). Everything with actual words
to read - the AI summaries, your API key, your calendar, your name, the credits - now lives in the
Library window's Settings page, and the notch just has one row that takes you there.

The AI-summaries block wasn't copy-pasted into its new home; it was **lifted out into its own piece** that
the Library page borrows. One copy, one behaviour, no drift. The notch page got about 40% shorter as a
result, and nothing was lost.

## The invisible-Toki bug

Toki can hide itself from screen recordings and screen sharing - that's the whole point of "ghost mode",
so it never shows up in a Zoom share. Except it was only hiding on *some* screens. Open the calendar, or
the assistant, and there it was in the recording.

The cause is a genuinely obnoxious Windows detail: **"hide me from capture" is a setting Windows quietly
throws away whenever the window redraws itself in certain ways** - and Toki redraws itself constantly
(that's how the frosted glass and rounded corners work). So the screens that used more effects were the
ones that lost it. The fix isn't to set it *better*, it's to set it **again, every single time, at the end
of everything** - after the glass, after the corners, after every screen change. Ghost is now a promise
Toki re-makes continuously rather than once at startup.

## "Two meetings, five tasks each - why does it say five?"

Sid asked Toki *"how many tasks on August 2nd"*. There were two meetings that day, five tasks in each.
Toki said five.

The bug wasn't in the counting. It was in the **listening**. When Toki pulls the topic out of your
question, the word *"many"* was sneaking through as if it were part of what you were asking *about* - so
Toki went looking for "the meeting about many", found one, and counted only that one's tasks. Teach it
that "many / much / count / number / total" are counting words and not topics, and the question falls
through to what it always should have been: *everything on that date*. Fourteen new tests so it stays
fixed.

Same flavour of fix for **"okay cool"** - Toki used to treat that as a question, find nothing to answer
with, and helpfully surface some random meeting. Now a nod is just a nod.

## The pretty part

- **The calendar, the "Coming Up" glance, and the prep card** got a proper redesign - a coloured spine
  down the left of each event, little circles with your attendees' initials stacked up, an amber *"in 12
  min"* when a meeting's nearly on you, and a Join button where you'd expect one. (Those initial-circles
  are drawn on your machine from the names Toki already has - no photos fetched from anywhere. Nothing
  leaves the laptop for a nicer-looking row.)
- **The recording screen shrank** - it was a big airy panel for what is basically "recording, 04:12,
  Stop". It's now one tight line.
- **The Ask welcome screen** got a soft glow behind the mascot and a proper entrance. It briefly also had
  three example-question bubbles; Sid's verdict was *"flat out ugly and never encourage"*, so they're
  gone. It's the mascot, one line, and the box you type in.
- **The sort menu in the Library** was a bare Windows dropdown crashing the party - now it's Toki's own
  panel, with a pink tick on whatever you've picked.
- **Motion** all comes from one place now, so everything moves like the same product.

## The icon that refused to update

Sid redrew the Toki mascot, and I redrew the vector version to match - brighter, glossier, with proper
black eyes. Then made it the app's icon. Sid: *"it's still the old ugly picture."*

I'd generated the icon at one big size (256 pixels) and assumed Windows would shrink it for the taskbar.
Windows *does* - badly. An icon file is supposed to contain **several hand-sized versions**, and the tiny
16- and 32-pixel ones are what you actually see on the taskbar and on a shortcut. Regenerated it with
**seven sizes** in the file, and it's sharp everywhere.

One last Windows trick worth knowing: it *caches* shortcut and taskbar icons, so even a correct new icon
can keep showing the old picture. Unpin and re-pin Toki (or remake the shortcut) and it appears.

## Where that leaves us

1,682 tests green, published, and Sid checked it on the real machine. What's left isn't really "make it
nicer" any more - it's **shipping**: a signed installer so Windows doesn't warn people, automatic updates,
and a way to actually sell a licence key. Plus the speech-recognition quality work we parked yesterday,
still parked, still written down.
