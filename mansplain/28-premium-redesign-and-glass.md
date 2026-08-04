# 28. A premium coat of paint (and a real glass notch)

**What we set out to do.** The owner's verdict was blunt: the app looked "AI-slop" — the checkbox, the
fonts, the spacing were off. So we did a proper design pass, and built the glassy notch the owner had
been asking for.

## Why it looked cheap (and the fix)

The tell was inconsistency: the app was using **18 different font sizes** (premium apps use ~6), three
different page paddings on neighbouring screens, and a checkmark that was being stretched out of shape. We
set a **tight type scale** and **consistent spacing** — change it once, every screen gets better.

Then the specific ugly things:
- **The checkbox** — redesigned: a crisp, correctly-shaped tick, **green** when done (green means "done",
  freeing the coral colour to mean only "record"), with a satisfying little pop when you check it.
- **The bare Windows checkbox in Settings** → a proper **sliding toggle switch.**
- **The "Google Meet ✓" tick** in the meeting composer → a clean toggle chip that **fills green** when on.
  The whole composer was flattened from a box-inside-a-box into one clean strip.
- **Notes** — the section headers are now **colour-coded** (decisions green, actions blue, questions
  purple), and text is left-aligned instead of justified (which made ugly "rivers" in a narrow column).
- **"Meeting" → "Calendar"** on the home card (it opens your calendar; it doesn't start a meeting).

## The glass notch — the hard one

The owner wanted the notch to become a **real frosted-glass panel** — blurring whatever's behind it, not
just a colour tint. That turned out to be a genuine fight with Windows, and we solved it piece by piece:
- **Real blur**, using the same Windows acrylic effect the OS itself uses — so text behind the notch
  dissolves into frost. It's **theme-aware** (dark frost in dark mode, white in light) and switches
  instantly when you flip the theme.
- **One consistent size** — turning glass on used to make the notch look bigger; now it's identical on and
  off.
- **A flush top** — Windows insisted on rounding the *top* corners (which must hang flush from the screen
  edge). The trick: we extend the notch a few pixels *above* the screen so that rounding hides off-screen,
  leaving a straight top and a rounded bottom.
- No stray outline, and it never interferes with ghost mode.

It's off by default (the solid notch stays Toki's signature), and it's a switch in **Settings →
Appearance**. Honest limit: true blur can only have Windows' standard small corner radius — a bigger,
rounder blurred corner isn't something Windows can do.

## What's next

The big remaining piece is the **hover-expand rows** (like Wispr Flow and Vercel): hover a meeting and it
smoothly grows to reveal Join / Prep / Edit, then retracts when you move away. That, plus finishing the
polish sweep on the last few screens.
