# Step 6 - The black window, and why the PC got slow

*July 15, 2026*

## What you saw

Good news first: you tried screen-recording over Toki, and Toki correctly stayed invisible in the recording - the ghost feature works. But your PC got slow and buggy, and a big **black rectangle** popped up in the middle of the screen. You sent a screenshot. I dug in.

## What was actually wrong

Two separate problems, and I found the exact causes.

**The black rectangle.** Toki's pill was built as a special "see-through" window (so the desktop showed around its rounded shape). It turns out Windows has a rule: a see-through window and the "hide me from screen recording" feature **don't work together** - Microsoft's own documentation says so. When both are on at once, Windows sometimes paints the whole window solid black. That's the black rectangle you saw. It only showed up while recording, which is exactly the situation that triggers the conflict.

**The slowness.** That same see-through window couldn't use your graphics card - it was being drawn the slow way, by the main processor, over and over for every little animation. On top of that, the part of Toki that watches for your clicks was sharing a lane with the part that draws the window, so when drawing got slow, your **mouse felt laggy everywhere**, not just in Toki.

## What I did

I rebuilt Toki's window from the ground up as a **normal, solid window** - but with nicely rounded corners (Windows draws those for us now, smoothly). Because it's a normal window:

- The **black-rectangle conflict is gone** - the "hide from recording" feature now works properly alongside it. I confirmed Toki is still invisible to screen capture.
- It's drawn by your **graphics card**, so it's fast and light. I measured it: Toki uses about half the processor power it did before when idle, and it's now smooth.
- I moved the click-watcher onto its **own separate lane**, so it can never make your mouse laggy again.
- I made every animation **stop completely when Toki is hidden**, so it uses basically nothing when you're not looking at it.

The one small cost: the pill's corners are a touch less round than before, and it lost a soft outer shadow. A fair trade for "never turns into a black box and never slows your PC."

## How I checked

I photographed the new window (clean, rounded, solid), measured its processor use before and after (it dropped), confirmed it's still hidden from screen recording, and ran every screen dozens of times with zero crashes. Toki also stays open on its own now and only closes when you actually click away from it.

## What's next

Back to the real feature work: **turning recordings into a written transcript** (your voice and theirs, kept separate, echoes removed) - the groundwork for the actual meeting notes.
