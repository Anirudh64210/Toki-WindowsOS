# 35 — Sticky notes, done properly (and the bullet bug)

*2026-08-06*

You tested what we built yesterday and came back with one bug and one "actually, no, do it differently".
Both turned out to be worth more than the features they were about.

---

## The bug: bullets breeding

You said the bullets and lists "get incremented — bullets are created by themselves."

That was real, and I want to be honest about how I found it, because I nearly didn't. My first instinct
was to read the code and reason about it, and I did that for a while and got nowhere. So I stopped and
wrote a test that *pretends to be your fingers*: it types characters into the real note editor, presses
Enter, clicks the toolbar buttons, ticks boxes — and then reads back what would be saved to disk.

It failed on the first run. Three different ways.

Here's what was happening. When you type `- ` at the start of a line, that's how you write a bullet in
Markdown. But the editor also had its *own* idea of bullets, using a component Windows gives you for
lists. So the line ended up with two bullets: the one Windows was drawing, and the `- ` still sitting in
your text. Save it, and you get `- - two`. Open it again, save again, `- - - two`. Every round trip added
one. That's your "bullets create themselves".

The other two were the same root cause wearing different clothes. The Windows list component *owned* those
lines, which meant the editor couldn't reach into them properly: pressing the "Body" button couldn't get a
line back out of a list, and a blank line between numbered items secretly split them into two lists so the
numbering restarted at 1.

I didn't patch these. I removed the cause. Every line in the editor is now just a plain line that knows
what kind of line it is, with the bullet or number or tick box drawn as a little badge in the margin that
is *not part of your text*. It can't leak into what gets saved, because it was never text to begin with.
Numbering is recalculated from position every time anything changes, so it can't go stale.

And now typing `- ` does what you'd expect from any notes app: it turns the line into a bullet and eats
the two characters. Same for `1. `, `# `, and `[] ` for a tick box.

The test that caught it is permanent — `--notetest`, 13 checks, including saving and reloading the same
note ten times in a row and demanding that nothing changes.

---

## The wrong idea: notes steering the summary

Yesterday I built the thing where what you'd typed got fed into the AI so the summary would emphasise
what you cared about. You've asked me to take it out, and you're right.

Here's the reason, put the way it landed for me: **a summary is something you check against the room.**
You read it and think "yes, that's what happened." If it has quietly rearranged itself around your private
jottings, it can't do that job any more — and worse, you can't tell that it has. It looks the same.

So: the summary is written from the transcript and nothing else. Your notes sit next to it, untouched, and
touching nothing.

---

## Sticky notes, rebuilt on something that can carry them

You said the sticky notes coming into the Library list as their own thing was "confusing as f", and that
they don't even open. Correct on both counts. Gone.

Underneath, they were also built wrong. They lived in a small file keyed to a calendar event, which meant
a note could only ever belong to the one meeting it was born on, and the file never cleaned itself up.
They're now proper records in the same database as everything else — a real row from the first keystroke,
so a crash mid-sentence costs nothing.

**The bit you specifically asked about: how does Toki know which note goes with which meeting?**

It doesn't guess. Ever. You told me not to depend on titles and you were right to — titles change,
meetings get renamed, and "Weekly sync" is a meeting everybody on earth has. Any guess here fails in one
of the two worst ways: it buries your notes, or it shows you somebody else's.

So there's a small list button on the prep card. It offers "Start a new note" first, then your recent
notes with when you last touched each one. You pick. Toki remembers *that specific note*, by its internal
id — not by its name, not by the meeting's name. Which means one note can happily run across a whole year
of a weekly 1:1, and renaming a meeting can never lose it.

**Where it lives now.** Open a meeting in the Library and there's a **"My sticky note"** button right under
the title. Click it and the note opens *inside* that meeting's note — after everything Toki wrote, just
before the transcript, exactly where you described it. You can type in it, tick things off, delete it. It
saves itself as you type; there's no Save button anywhere else in Toki and I didn't want to invent one here.

**What it deliberately cannot do**, and there's a test for each of these, because "it doesn't leak" is only
true if something actually checks:

- It can't appear as a row in the Library list.
- It can't come back as an answer when you Ask — it's your private page.
- It can't influence the summary.
- Deleting a meeting deletes the *connection*, not your writing. You might still want it next week.

---

## Two smaller things

**Your meeting-is-starting nudge is back.** Yesterday we made the prep card hover-only. Testing it, I
think that removal quietly took something else with it: the card appearing was also the only thing telling
you a meeting was about to start. It's back, but polite — it opens once, and only if Toki isn't busy and
you're not in something fullscreen. Otherwise it just sits there armed, one hover away.

**To-dos now have the ↳ arrow too.** You asked about this last time — why some lines link to the
transcript and some don't. Part of the answer was a genuine gap on my side: I'd only wired the arrow into
the summary bullets, and never into the action items, which are the lines you'd *most* want to check. Fixed.

(The other part of the answer stands: a line with no arrow means Toki isn't confident enough to point at a
specific moment, and I'd rather show you nothing than send you to the wrong line.)

---

## Where it stands

1,759 tests green. All the self-tests pass against the packaged app, not just the development one. I've
looked at the Library and the notch on screen and they render right.

None of it has met a real meeting yet. That's your bit.

One thing I got wrong that's worth writing down: the critique I wrote yesterday listed eleven things about
the note editor and **did not contain the bug you found the next day**. I'd reviewed the part I'd just
written tests for — the part I already understood — instead of the part with no tests at all. That's
backwards, and I've noted it at the bottom of that document so it stays noted.
