# 36 - The polish pass (and the bug that was eating your sentences)

*2026-08-06*

You gave me a list of things that looked or felt wrong. One of them turned out not to be a looks problem
at all.

---

## "Mosquito sized text, what is that man"

You clicked on the empty line between two sections, typed, and got text the size of an insect.

Here's what was actually happening, and it's worse than what you saw.

Blank lines in a note are deliberately squashed - if an empty line took a full line's height, the gaps
between sections would be chasms. I did that squashing the lazy way: I set the **font size** of a blank
line to 6 pixels. So the line looked right, and anything you typed on it came out 6 pixels tall. That's
the part you saw.

The part you didn't see: that line was still filed as "blank". And when Toki saves a note, a blank line is
written as *nothing*. So the sentence you typed on it was **thrown away the next time the note saved.**

You reported a font size. It was deleting your writing.

Three separate fixes, because it was three mistakes stacked:

1. **Don't shrink a line by shrinking its font.** There's a separate setting for how tall a line is. Use
   that. The font stays normal size, so the first letter you type is already the right size.
2. **A blank line you've typed on isn't blank.** Obvious when you say it out loud. It now becomes a normal
   line the moment there's anything on it - which is the bit that stops the text being discarded.
3. **Windows quietly copies formatting onto what you type.** Even after fixing the line, the individual
   characters were carrying "6 pixels" stamped on them personally. Now that gets wiped whenever a line
   changes.

Four new checks cover exactly this, including one that types on a blank line and demands the text survive.

---

## No more em dashes

You said they make the project look ugly. Gone - 3,089 of them, across every file: the code, the notes it
writes, the documentation.

Three survive, and I want to be upfront about why, because "I removed all of them except some" deserves an
explanation:

- One is used as a *word separator* when Toki matches a note line back to the transcript. It's punctuation
  the machine reads, not text you see.
- One is how Toki recognises when the AI has left a field empty. If I removed it, you'd start seeing stray
  dashes in the owner/due labels.
- One is inside the function whose entire job is removing em dashes.

All three are listed by name in a test that fails the build if a fourth ever appears.

**The AI is also a source of them.** Telling a model "don't use em dashes" works most of the time, and the
one time it doesn't is the time you're reading. So Toki now strips them from every summary, every reply,
and - importantly - from each word *as it streams onto your screen*, because cleaning up at the end would
still have let you see one.

I left the short dashes in time ranges alone ("4:37 - 5:07 PM"). That's a different character with a real
job, and it's Toki's own formatting rather than the AI's. Say the word if you want those gone too.

---

## One font, and Settings decides

I got this backwards first and you caught it. You wanted the *Settings* page to be the standard; I'd made
Settings match Templates instead. Fixed.

The funny thing is that every page was already using the same font. What differed was the **weight** -
Templates was Bold, Settings was SemiBold, note titles were Bold - and a bold heading next to a semi-bold
one genuinely reads as a different typeface even when it isn't one. Everything is SemiBold now, except a
few tiny things where bold is doing real work (the date squares, the YOU/THEM labels).

They're named styles now rather than numbers typed into each page, so they can't quietly drift apart
again. Templates also stopped being the one page that started in the middle of the screen.

---

## Buttons you can actually hit

Your words: "impractical", "you have to click the text".

You were right, and the cause was one line in the shared button style. Toki showed "you're hovering me" by
making a button slightly see-through. That works on a button with a colour behind it - and does **nothing
at all** on the many buttons that are deliberately transparent. Those buttons had no hover feedback
whatsoever, so there was no way to know there was a box there at all. You just aimed at the word.

Buttons now draw a proper rounded box under the pointer, exactly the size of the area that responds. Every
button also has a minimum size, so a single "•" is still something you can hit without aiming.

Then the specific ones you pointed at:

- **The sidebar Library/Ask/Templates/Settings rows** were always full-width targets, but nothing was
  drawn until you clicked, so they looked like floating words. They light up now.
- **The Library link in the notch** was transparent text pretending to be a button. It's a real pill.
- **"All other settings"** was hanging past both edges of the card, so its highlight looked like a
  rectangle rather than a button, and the little arrow beside it looked like the thing to press. It's one
  proper button now, sitting inside the card, and the arrow can't be clicked separately because it's
  decoration.
- **Copy and Delete** in the Library: Copy was two pills a pixel apart pretending to be one. It's one
  rounded button with a hairline down the middle now.

---

## Where it stands

1,774 tests green. Everything runs against the packaged app, not just the development one. I added a
screenshot of a *hovered* nav row to the render harness, because hover is invisible to a normal screenshot
and I didn't want to just tell you it works.

Still no real meeting has touched any of this. That's yours.
