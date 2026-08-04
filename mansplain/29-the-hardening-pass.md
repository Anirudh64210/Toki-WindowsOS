# 29 — The hardening pass (nothing new to look at, everything sturdier)

This session added no new screens. Instead, Toki got the kind of once-over a product needs before
strangers pay for it: a deep inspection of every part of the code, done by a small fleet of AI
auditors working in parallel — and, crucially, every problem one auditor claimed was re-checked by a
second, skeptical one against the actual code before we believed it. 67 real issues survived that
filter. All the serious ones are now fixed, and everything was verified by actually running the app.

## The scariest one, in plain words

Toki's one sacred promise is: **the mic only runs when you can see it running.** We found a family of
ways to accidentally break that. If you were recording a meeting and, say, held the talk key to ask
Toki a question, or pressed Esc to get the notch out of the way — the recording *bar* disappeared but
the recording itself kept going, invisibly, with no way to stop it, and the meeting could end up lost.

That's fixed at the root: the question "is Toki recording?" is now answered by the recording itself,
not by whatever the little window happens to be showing. Closing, hiding, Esc-ing, asking questions —
none of it can hide a live recording anymore. The bar stays until *you* stop it.

## Delete now means delete

The Library's delete button showed a message promising the audio would be "removed for good" — but it
only removed the entry from the list. The audio files stayed on disk. Three separate auditors caught
this independently. Now deleting a meeting removes the note, the audio, and the Markdown copy. Gone
means gone.

## Crashes stopped being able to eat meetings

Two changes work together here. First, while recording, the audio files are made "safe to open" every
few seconds — so even if the PC dies mid-meeting, the recording up to that moment is playable.
Second, every time Toki starts, it quietly checks for recordings that never became meetings (from a
crash, or quitting mid-recording), and turns them into proper Library entries. When we turned this on,
it immediately found recordings from three weeks ago sitting invisibly on this very PC — cleaned up
the silent ones and rescued a real meeting into the Library. That's the feature proving itself on the
first run.

## A bouquet of smaller fixes

- Unplugging your headset mid-meeting used to silently kill your side of the recording. Now Toki
  switches to whatever mic Windows switches to, mid-file, without missing a beat.
- If your Windows username contains certain letter combos (really!), meeting detection could be
  entirely dead. And the new Microsoft Teams was being recorded one-sided. Both fixed.
- The "record this meeting?" prompt now politely waits if you're presenting or gaming fullscreen —
  it appears the moment you come back to the desktop instead of popping over slide 3.
- A rare crash-during-save could wipe your API key and Google connection. Files are now written the
  careful way, so a crash can't half-write them.
- The calendar could silently show empty weeks if you had lots of events (it only ever asked Google
  for the first 25). It now asks for all of them.
- The notes-writing AI got two new standing orders from our research into what makes Granola-class
  notes good: transcripts contain mis-heard words, so read through them sensibly but *never* "fix" a
  person's name — and when you write down a decision, write down the *why* that was said with it.
- And if your API key is ever missing, notes now fall back to the on-device AI instead of quietly
  dropping to the basic non-AI notes. (Found because this very PC was in that state.)

## How we know it all works

All 472 automated tests pass. Every one of the app's built-in self-checks passes on this PC —
including the one that loads the real language model and writes real notes. Every screen was rendered
in both light and dark themes and eyeballed. And the double-clickable app was rebuilt fresh and
launched to watch it start, warm up, and run its new recovery sweep for real.

Also gathered along the way: a full competitive picture of every major AI notetaker in 2026. The short
version — the whole market copied Toki's way of capturing audio, but nobody processes on-device, the
big players are being sued over consent and training, and everyone charges monthly. Toki's spot on the
map (local, consent-first, pay-once) is genuinely empty. The ideas worth borrowing next are written up
in the backlog for Sid to pick from.
