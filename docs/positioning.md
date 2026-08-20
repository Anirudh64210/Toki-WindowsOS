# What Toki has that the others don't

*Positioning notes for the landing page and the store listing. Written 2026-08-13. This is a writing
task, not a build task - everything below already exists in the product.*

The temptation with a notetaker is to sell the notes. Everybody sells the notes, the notes are broadly
similar, and a buyer cannot tell them apart from a screenshot. Toki's advantages are all structural -
they come from the decision to run entirely on the user's machine - and structural advantages are the
ones a competitor cannot copy in a sprint.

Three of them, in the order they are likely to close a sale.

---

## 1. The files are yours, in a folder, as Markdown

Every meeting is mirrored to a plain `.md` file in `%APPDATA%\Toki\Notes\`. Point Obsidian at it and
your meetings are in your vault. Search them with the tools you already use. Back them up with
whatever backs up the rest of your work.

**Why it lands:** the loudest recurring request under every Granola thread is an Obsidian export, and
it is a genuinely hard thing to retrofit onto a product whose notes live in a database on someone
else's server. Toki did not build an export. Toki never had anywhere else to put them.

**The line:** *Your notes are files. In a folder. On your computer.*

## 2. Invisible in a screen share

Ghost mode hides the Toki overlay from screen capture at the OS level. Not "we ask nicely" - the
window is excluded, so it does not appear in a Teams share, a Zoom share, a Loom, or OBS.

**Why it lands:** the awkward reality of a notetaker is that everyone can see you are running one.
This is the only feature on this page that people react to physically the first time they see it.

**The line:** *You can see Toki. The meeting can't.*

## 3. It works on a plane, and it costs nothing to keep

Transcription and notes run on the machine. No account, no server, no subscription, no per-minute
charge, no usage tier that runs out on the 23rd. A one-time $20.

**Why it lands:** every competitor here is a monthly bill that grows with use, and every one of them
stops working when their servers do. Toki has no servers to stop.

**The line:** *One payment. No account. Nothing leaves your laptop.*

---

## The fourth thing, which is newer

**You can hear it, not just read it.** Every note bullet links to the transcript moment behind it, and
now plays the few seconds of audio it came from - because Toki still has the recording, on the disk,
where the user put it.

This is worth its own paragraph because it inverts the usual objection. A small on-device model
phrases things less elegantly than a frontier cloud model; people forgive that completely once they
can check the claim in one click. Granola cannot offer this at all - it deletes the audio by design.
Otter can, but only by hosting your recording in its cloud and handing you a link it controls.

**The line:** *Don't trust the summary. Press play.*

---

## What to be honest about

Marketing that oversells gets found out in the first review, and these are all defensible when stated
plainly:

- **English-first.** The on-device recogniser is an English model. Say so. "Excellent English" beats
  "100+ languages" that are all mediocre, and the people who need 100 languages were never going to be
  happy here.
- **The notes are good, not magic.** A 4-billion-parameter model running on a laptop writes clear,
  accurate, slightly plain notes. Anyone who wants frontier-model phrasing can add their own API key
  and pay for it directly - which is a feature, not an apology.
- **You need a reasonably modern PC.** It runs on the machine, so the machine matters.

## What NOT to say

- Do not claim Toki is more accurate than the cloud tools. It probably is not, and the claim is
  checkable in ten minutes.
- Do not lean on "AI". Every competitor says AI. Nobody else can say "your notes are files in a
  folder".
- Do not imply Toki handles consent or compliance for the user. Toki records what they ask it to
  record; announcing it is theirs to do.
