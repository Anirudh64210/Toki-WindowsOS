# 31 — A thousand questions (and teaching Toki to answer them right)

You caught Toki giving a bad answer: you asked "what tasks did I have on August 2nd" and it dumped every
overdue task instead of the tasks from that day. You were right, and you asked for the fix to be done
properly — not just patch that one question, but figure out *every* kind of question a person could ask a
meeting assistant, build a big test for all of them, and fix whatever's broken. Only the "brain" part.

So that's what this round did.

## First, the research

I had two deep research passes study how the best assistants (Granola, Fireflies, Otter, Fathom,
Circleback) handle questions, and map out the entire space of things people say: asking about a time
("what happened last week"), a task ("what's due Friday"), a person ("what did Ana say"), a topic ("what
did we decide about pricing"), a pattern ("what keeps coming up"), a command ("open my library"),
chit-chat ("thanks!"), a privacy worry ("are you sending my data to the cloud?"), a general question
("what does OKR mean?"), and the tricky ones — vague questions, questions about things that never
happened, even attempts to trick it. Every category, with the *correct* answer for each, written down.

## Then, a thousand-plus tests

From that map I built **1,155 automated question-tests** (Toki's total test count went from about 520 to
over 1,600). Each one asks Toki a question and checks it does the right thing — sends it to the right
place, understands the time range, and stays honest. The clever part: almost all of Toki's understanding
happens *before* the AI model runs — deciding "is this a command, a task question, chit-chat, or a real
meeting question?" is plain logic. That means I could test a thousand questions in about five seconds,
without the slow AI model — so this test runs every single time, forever, and will catch it the instant a
future change breaks something.

## What the tests found and fixed (your bug and more)

- **Your bug.** "What tasks did I have on August 2nd" now correctly means *the tasks that came out of that
  day's meetings* — not a dump of everything overdue. Toki now tells apart "what's *due* this week" (a
  deadline question) from "what did I have *on* that day" (a what-happened question). Exactly the
  distinction you'd expect.
- **A privacy bug I didn't expect to find, and it mattered.** When I tested "are you sending my data to
  the cloud?", "are you always listening?", "is this private?" — Toki was *searching your meeting notes*
  for the answer instead of just telling you the truth. That's a real problem: the one time a wrong answer
  is unacceptable is when someone asks whether their private meetings are safe. Now those questions get a
  precise, honest answer every time: everything stays on your PC, nothing's uploaded, there's no
  always-on microphone, and the only way anything leaves the machine is if *you* add your own API key.
- **Toki no longer gets confused by coincidences.** Ask "write me a haiku about Mondays" and it used to
  latch onto your "Monday standup" meeting. Now it knows a general request from a question about your
  meetings.
- **It understands more natural phrasing:** "what's on my plate", "what does Ana owe me", "how many calls
  last month", "start a new chat", "call me Sam" / "I go by Sam" — all recognised now.

## How I know it's right

All 1,636 tests pass. Every built-in self-check passes. Your exact reported question is now a permanent
test that proves the fix. And the double-clickable app was rebuilt and confirmed running. I didn't touch
anything outside the assistant's brain, as you asked.
