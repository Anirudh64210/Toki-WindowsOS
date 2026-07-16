# Step 8 — Toki remembers, and pulls out the useful bits

*July 15, 2026*

## What we set out to do

Now that Toki can turn a meeting into a transcript, two things: **save every meeting** so you can find it again later, and take a **first pass at the useful parts** — the decisions, the to-dos, the open questions — without needing the big AI yet.

## What we did

1. **A local memory.** Every meeting is now saved into a small database on your computer (nothing in the cloud). Each one also gets written out as a plain text file you can open, search, or back up yourself.

2. **A first-pass notes writer that needs no AI.** Toki reads the transcript and sorts it: sentences where people **agreed** on something become **Decisions**; sentences about who's doing what become **To-dos** (it even guesses the owner and a due date — "Priya… tomorrow"); anything phrased as a question becomes an **Open question**; and the first couple of sentences become a short summary. It's simple pattern-matching, but it's instant, always works, and never needs the internet. (The smarter AI-written version comes next and sits on top of this.)

3. **The Library.** A searchable list of every meeting and note. Each card shows the date, a one-line summary, and little tags like "2 open" (to-dos) and "2 people". Click one to read its notes; there's a trash button to delete (with a confirm). You can search **everything**, or narrow it — `you: pricing` finds only things *you* said, `them: webhook` finds only what *they* said.

4. **Your own notes.** A **"+ New note"** button lets you jot a note by hand — a title and free text — that lives right alongside your meeting notes and is searchable the same way. Good for prepping before a call.

## How I proved it works

Two automatic tests: one that saves a meeting, searches for it (including the you:/them: filters), ticks off a to-do, and deletes it — all correct. Another that feeds in a planted conversation and checks Toki correctly pulls out the decision, the to-dos with their owners, and the questions — and removes an echoed line. Then I loaded three sample meetings and photographed the real Library, the notes page, and the note editor — all clean and correct.

## What's next

**The smart summaries.** Right now the notes are pulled out by simple rules. Next, Toki gets a proper on-device AI (a language model that also runs entirely on your machine) to write the notes the way a person would — a real summary, cleanly grouped decisions and to-dos. After that, the live preview so you can watch words appear while you talk.
