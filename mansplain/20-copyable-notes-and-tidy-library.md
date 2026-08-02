# 20 — Copyable notes and a tidier Library

*Plain-language story of the P1.1 polish.*

## The big one: you can now copy the whole note

Before, if you opened a note and tried to select the text, you could only grab **one bullet at a time** — useless if you wanted to paste the whole thing somewhere. The reason was technical: each line was its own little text box, and you can't drag a selection from one box into the next.

Now the entire note is **one document**. You drag from the very top to the very bottom and it all highlights together — summary, key points, to-dos, transcript — and Ctrl+C copies the lot. There's also a **Copy** button: press it and the whole note goes to your clipboard (it flashes "Copied ✓"); the little **▾** next to it lets you copy just the summary, just the transcript, or jump to the note's file on disk.

Because a screenshot can't prove that a mouse-drag works, this one is backed by a self-test (`--copytest`) that selects the whole note and checks nothing is missing.

## The note reads cleanly

- Text now **starts at the left and fills the width** of the panel, at any window size. (It used to float in a narrow centered column, leaving a big empty gap on the left when you maximized — that's gone.) The scrollbar sits at the far right where you'd expect it.
- The **Template** picker moved up next to **Copy** and **Delete**, above the title, out of the way of reading.
- **Delete** is now a clear red instead of a washed-out grey that looked broken.
- Names on action items (who owns a task, when it's due) are **bright and easy to see**.

## Buttons that sit right

The button labels were sitting slightly too low — a quirk of the Windows font. Rather than guess, Toki now draws a faint center line through each button during testing and nudges the text until it lands exactly in the middle. Little thing, but it's the difference between "polished" and "off".

## Resizing and hiding panels

The dividers between the panels now behave like the ones in VS Code: invisible until you hover, then a thin pink line you can grab and drag. And if you want the note to take the whole space, **click "Library" again** — the list of meetings slides closed. Click it once more and it comes back.
