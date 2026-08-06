# 22 - Ask Toki gets smart

You showed me five screenshots where Toki's answers were, frankly, bad. Here's what was actually
going wrong, in plain words - and what changed.

## Why it was "dumb"

None of it was the AI model being stupid. Every failure was a small piece of *plumbing* sending your
question to the wrong place:

1. **"What was discussed about Kai Sinat in my meetings?"** - Toki saw the words "my meetings" and
   decided you wanted a *list of meetings*. The name Kai Sinat was thrown away without ever being
   searched. Like asking a librarian about an author and being handed the whole shelf.
2. **"What were the updates in this meeting?" answered about a different meeting** - Toki keeps a
   little sticky note for itself saying "the user is looking at note X". That sticky note went stale:
   it got wiped when the Library closed and never rewritten when it reopened, and at startup it was
   even set to a note you'd never looked at. So "this meeting" pointed at the wrong one.
3. **"What did Mac have as action items?" listed everyone's** - Toki simply had no concept of *who*
   in a task question. Names weren't looked at at all.
4. **"Why are you so rude?" → "there's no mention of rudeness in the meeting notes"** - Toki treated
   *every* sentence as a question about your notes. It had no way to notice a question was about
   *itself*.
5. **Answers read long and robotic** - the model was allowed to run past the answer and then got
   chopped, and it had no example of what a good short answer looks like.

## What changed

- **Names now win.** If your question contains a topic or a person, Toki searches for *that* - and
  if it's genuinely not in your notes, it says so in one honest sentence instead of guessing.
- **The sticky note is gone.** "What's on screen" is now *computed on the spot* from what's actually
  open and what you actually clicked - it can't go stale. And when Toki answers about the open note,
  the notch shows a small "About: …" line so you can see it grounded on the right one.
- **"Mac" means Mac.** Toki checks names against the real owners on your action items (first names
  and "Mac's" count too), and filters. If Mac has nothing in that meeting, it says so - and tells
  you who does.
- **Toki answers for itself.** Complaints, "do you see my screen?", "is my data private?" - short,
  warm, human replies. (And yes: it only knows which note is open in Toki. It never watches your
  screen, and everything stays on your PC.)
- **Shorter, more human answers.** The model's settings now match its maker's official
  recommendations, it's shown an example of the voice we want ("You settled on the $20 price - that
  was in Tuesday's pricing sync."), and quick answers stop at the end of the answer instead of
  rambling until they're cut off.

## What to know

All five fixes are locked in by automated tests (228 now pass), so these exact bugs can't quietly
come back. The one thing tests *can't* judge is whether the phrasing feels right to you - there's a
10-step try-this list in `docs/ask-toki-redesign.md` for your next session with the app.
