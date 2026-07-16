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
