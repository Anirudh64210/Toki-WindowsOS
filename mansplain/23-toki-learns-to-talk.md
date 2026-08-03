# 23 — Toki learns to talk

You said it plainly: "it's just not smart — it looks like the first version of ChatGPT." You were
right, and this time the fix wasn't another bug hunt. It was a rebuild of how Toki *decides what
you meant*.

## The real problem

Under the hood, old Toki had exactly one move: **turn whatever you said into a search of your
meeting notes.** That's why:

- **"What is my name?"** → it *searched your meetings for your name* and answered "I don't have
  your name in the meeting notes" — with three meeting citations underneath, as if that were a
  researched fact.
- **"But you call me anirudh"** → it searched the notes for "Anirudh" and told you it found no
  mention. You were *teaching it something*, and it had nowhere in its head to put it.
- The mic mis-heard **"switch to light theme"** as "switch to *like* theme" → no command matched →
  so it searched the notes → found nothing relevant → and the AI, ordered to "connect what's
  related", **made something up**: it tied your theme request to an attendance-tracking meeting.
  That's the worst kind of failure — confident nonsense.

A search box with a chat skin. The AI model inside was never allowed to simply talk, think, or
remember.

## What changed — Toki now has a brain that sorts your words first

Every time you say something, Toki now decides *what kind of thing it is*, and sends it to the
right place:

- **A command** ("open the library") → does it. And a *garbled* command now gets a friendly
  check-in — "Sounds like a theme switch — say 'switch to light theme' or 'switch to dark theme'" —
  instead of an invented answer.
- **Chit-chat** ("good morning", "I love you") → warm reply, as before.
- **Something about you** — this is new. "Call me Anirudh" → *remembered*, permanently (the
  greeting uses it too). "What's my name?", "who am I?", "what do you know about me?" → answered
  from that memory, instantly, never a search.
- **A question about your meetings** → everything we built before: exact dates, task lists, honest
  misses, citations. Untouched.
- **Everything else** — also new, and the big one. "What's the capital of France?", "tell me a
  joke", "I'm stressed about tomorrow" → the AI finally answers *as itself*, from its own
  knowledge, remembering what you said earlier in the conversation. No more "I couldn't find that
  in your meetings."

Two honesty rules were also bolted on: if an answer admits "the notes don't cover this", it can no
longer show source citations under itself (a miss must look like a miss), and Toki will never again
stuff *unrelated* meetings into the AI just to have something to say — that's exactly where the
made-up answers came from.

## Being honest about the ceiling

The AI living on your laptop is small — about a hundred times smaller than the big cloud models.
It will chat competently, but it won't dazzle. Here's the thing though: most of what felt "dumb"
wasn't the model's size — it was the plumbing never letting it talk. That's fixed. And if you ever
want the big-model feeling, your own API key (already supported in Settings) plugs straight into
this same new brain: same memory, same honesty, much fancier conversation.

428 automated checks pass, including every old fix. The one thing no test can measure — whether
Toki now feels like *someone* — is yours to judge. The test script is at the end of
`docs/ask-toki-brain.md`.
