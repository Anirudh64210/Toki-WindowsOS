# The story of building Toki for Windows

This folder explains the build in plain language — no jargon, no code. One short file per step: what we set out to do, what broke, and how it was fixed.

Read them in order:

1. [Getting started](01-getting-started.md) — setting up the workshop and the empty app.
2. [The pill is alive](02-the-pill.md) — Toki's body: the shape-shifting pill, the tray icon, and the shortcut.
3. [Toki can hear](03-toki-can-hear.md) — the two ears (your mic + the meeting app only), and proof it records only the right app.
4. [Toki notices, and a real app you can open](04-noticing-and-recording.md) — double-click app, never-crash, ghost mode, and detecting a call to ask about recording.
5. [Never freezes, and properly alive](05-smooth-and-alive.md) — killing the freeze bug, re-launch handling, and the redone alive loading animation.
6. [The black window, and why the PC got slow](06-black-window-fixed.md) — the see-through-window vs ghost-mode conflict, and rebuilding the window to be solid, fast, and stable.
7. [Toki turns talking into text](07-toki-understands-speech.md) — on-device speech-to-text, both sides transcribed and labelled, echo removed.
8. [Toki remembers, and pulls out the useful bits](08-remembering-meetings.md) — local database, first-pass notes, the searchable Library, and manual notes.
9. [Toki writes the notes like a person would](09-ai-written-notes.md) — the on-device AI writer, three-pass notes, safety net, and the GPU control.
10. [Making the notes better, cleaner, and faster](10-refined-and-faster.md) — Granola-style notes, concise titles, no ugly first draft, a big speed-up, the Library window, and the honest story on transcription speed.
11. [Making Toki fit your PC](11-fits-your-pc.md) — the lost-meeting bug explained, capturing the other people on a manual start, long meetings that can't crash, and fitting two big models into a small memory.
12. [Real glass, calm recording, cleaner buttons](12-glass-and-polish.md) — the calm recording chip, click-outside that stops closing things, a genuinely translucent pill that's still invisible to recording, crisp icons, and a design-matched Library.
13. [Honest transcripts and quiet recording](13-honest-notes-and-quiet-recording.md) — the mid-word transcript bug found and fixed (900+ fragments → 82 clean sentences), the truth about the "6 minutes", skipping a muted mic, the tiny live-Toki recording pill, and small UI fixes.
14. [Toki moves to the top, like a notch](14-the-notch.md) — the pivot to a top-edge virtual notch: Toki peeks first then opens your page, closes off cleanly so it never squats on your work (and the summon that finally "sticks" past the browser tabs), flips upside-down while recording, and the fixes along the way.
15. [Notes that are actually smart (and honest)](15-smarter-notes.md) — reading a long meeting in pieces so the summary covers the *whole* thing, notes shaped to the meeting (no made-up filler for a nothing-chat), and the slow-speech transcript bug fixed.
16. [Why notes take time (and how the fast apps cheat)](16-why-notes-take-time.md) — the honest speed story: cloud apps rent a 10× bigger engine, on-device keeps your meetings yours but slower, why "summarize during the meeting" hurts quality, and the two real fixes (bring-your-own-key for cloud speed, speculative decoding for the local model).
17. [Bring your own AI key](17-your-own-ai-key.md) — the optional cloud-speed path: pick a provider, paste a key, pick a model; how the key is kept safe; the honest "https is the same, the address isn't" fix; one green connected light; and the false starts (Claude-as-default, the clunky wheel, the erased key).
18. [A fresh coat of paint, and notes that fit the meeting](18-look-and-notes.md) — a live light/dark switch, a see-through "tinted mirror" notch, notes shaped to the kind of meeting (standup, interview, decision…) with name-tagged tasks, the pink-cloud mascot, smoother motion everywhere, and tidier settings.
19. [Toki gets a home](19-the-hub.md) — the Library becomes a Claude-desktop-style hub: a sidebar (Ask / Library / Templates), a typing greeting + search on the front page, a clickable mascot, an on/off reorder switch, and a per-note template picker. The shell before the chatbot.
20. [Notes you can copy, and a tidy Library](20-copyable-notes-and-tidy-library.md) — every part of a note becomes selectable and copyable (nothing locked as a picture), a one-click "copy everything," and a cleaner Library.
21. [Ask Toki — the chatbot](21-ask-toki-the-chatbot.md) — the assistant arrives: ask questions about your meetings and get streamed answers pulled from your own notes.
22. [Ask Toki gets smart](22-ask-toki-gets-smart.md) — the assistant learns to route questions (facts vs. thinking vs. commands), ground to the meeting on screen, know people's names, and stop making things up.
23. [Toki learns to talk](23-toki-learns-to-talk.md) — the dialogue brain: Toki remembers who you are, can just chat, keeps meeting answers grounded, and never fabricates.
24. [Toki connects to your calendar](24-connecting-your-calendar.md) — the private, safe link to Google Calendar (the foundation): browser-direct sign-in, an encrypted "valet key," the smallest possible permission, and proof it reads/creates events live.
25. [Your week, in the notch](25-calendar-in-the-notch.md) — the calendar UI: the redesigned home card ("COMING UP" + Join), an agenda-style week view with a "now" line, one-box natural-language create, edit, and delete (instant + Undo for solo events, confirm for guested ones).
26. [Toki preps you before a meeting](26-prep-cards.md) — the prep card: ~5 min before a meeting, Toki surfaces last time's recap, still-open items (with owners), and the agenda, plus Join-and-record. Deterministic (no model), honest when it's a first-time meeting, and restrained (once per meeting, only when idle).
