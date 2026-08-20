# 34 - Notes you can actually write in

Sid went through the whole pending list - forty-odd things - and picked one thread to pull: **you should
be able to write your own notes properly, and what you write should change what Toki writes.** Seven
steps, each one checked before starting the next.

## The thing everything else is built on

Toki's "write your own note" was, until now, a single plain text box. You could type. That was it. No
tick boxes, no headings, no lists.

So the first job was building a proper one - **once** - and then using that same piece everywhere: in the
Library, on the prep card, and on the scratchpad you use during a meeting. It does three text sizes,
tick boxes you can actually tick, bulleted and numbered lists, spacing, and it can drop in any of Toki's
eight note templates.

What it deliberately *doesn't* do: fonts and colours. Sid was clear about that - *"nothing outside normal
text, no font/color just size, templates, spacing, freedom."* That restraint turned out to be useful for
a second reason. Because there's no fancy formatting, your notes save as **plain readable text files** on
your machine, same as everything else Toki writes. You could open them in Notepad, or Obsidian, or email
them to yourself. If I'd invented some special format for tick boxes, your hand-written notes would have
become the one thing you could only read inside Toki - which quietly breaks the promise that your data is
yours.

Lists behave the way you'd expect (Enter makes the next one, Tab indents) because they use the exact same
machinery Word does. Tick boxes needed hand-building: press Enter on a to-do and you get another to-do;
press it on an empty one and the list ends.

## The prep card stops jumping out at you

Sid's verdict on prep cards: stop appearing on your own. So they don't any more. A card gets **made ready**
before a meeting, and then waits. You get to it by hovering Toki, or - during the meeting - by hovering
Toki so the recording bar drops down, where there's now a **"Prep card"** button. It stays open while
you're using it, and a back arrow returns you to recording. The microphone never stops.

And Sid wanted to *write on* the card, not next to it. So **Edit turns the card into the note**: last
time's recap becomes text you can change, the still-open items become real tick boxes, the agenda becomes
bullets, and there's a space at the bottom to keep going. Once you've edited it, it stays your version.

Along the way I found a genuine bug worth naming: when you pressed "Join and record", Toki **threw the
prep card away**. Which meant that during the exact meeting the card had been prepared for, there was no
card. That was left over from when the card was a pop-up that had to get out of your way; nobody had
revisited it once it became something you'd want to come back to.

## What happens to what you wrote

Everything you type is saved to your machine the moment you pause - a crash or a quit can't eat it.

Then, when the meeting ends, Toki **asks**. A card appears at the top of your Library: *"you wrote this
during Design review - Keep or Discard."* Not a pop-up in your face, and nothing saved behind your back.
If you'd rather bin it, Discard asks "Sure?" once first - that's text you typed by hand, and one stray
click shouldn't lose it.

## And now the good part

Two things that change what Toki actually produces.

**One: what you type steers the summary.** This is the feature everyone praises Granola for. The reasoning
is simple - if you bothered to write something down during a meeting, that's the strongest possible signal
that it mattered. So your notes go to the AI *before* the transcript, with instructions to cover every
point you made, fill them in with detail from what was said, and lead with them. If you ticked something
off, Toki knows it's done; if you didn't, it becomes a follow-up.

I was careful about one thing here. The AI is allowed to fail - the model might not be loaded, it might
time out, it might decide the meeting was all small talk. In **every** one of those cases, your typed
notes still come through as their own section, with your unticked boxes as action items. Toki's summary
can always be written again. Your sentence can't.

**Two: you can check Toki's work.** Every point in a note that can be traced back now has a small **↳**
next to it. Click it and the transcript jumps to the line it came from and highlights it.

The honest part is what happens when a point *can't* be traced: **you get nothing**. No arrow, no guess.
A link that jumps to the wrong line is worse than no link at all, because it makes the note look like it's
quoting something it isn't. In the very first test on a real meeting, four points got arrows and one -
"rotation cycle begins immediately" - correctly got none.

That last feature is also the cheapest fix for something real: Toki's AI runs on your laptop, so it writes
less elegantly than the giant cloud ones. It turns out people forgive that almost entirely once they can
check the source in one click. Toki has been keeping every transcript this whole time; it just wasn't
using them for this.

## Not finished

Three things I left open rather than quietly papering over:

- The prep card popping up on its own was *also* how you knew a meeting was starting. That's gone now. If
  meetings start slipping, the answer probably isn't bringing the whole card back - something quieter,
  like Toki glowing amber.
- Notes you write for a meeting you never record still sit in a file you can't see. They're safe, just not
  browsable yet.
- If you regenerate a note with a different template, it loses the steering from what you typed.

All three are written down. Sid's holding off on saving any of this until they've used it in real
meetings, which is the right call - none of it has met a real conversation yet.
