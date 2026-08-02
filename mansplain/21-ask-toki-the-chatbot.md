# 21 — Ask Toki, the chatbot

*Plain-language story of Phase 2: asking questions of your own meetings.*

## What it does

Open **Ask** and type a question about your meetings — *"what did we decide about the survey forms?"*, *"why was the launch delayed?"*, *"what are my open tasks?"* — and Toki answers using **only your saved notes**, all on your laptop.

## Why it feels fast now

Two changes:

1. **It streams.** Instead of waiting for the whole answer to finish, the words appear as Toki writes them — like watching someone type. Same as ChatGPT. You start reading immediately.

2. **It finds the right notes first.** Toki keeps a private search index of everything in your library (titles, summaries, action items, and the full transcripts). When you ask something, it instantly finds the two or three most relevant meetings and only reads those to the model — less to chew on, so a quicker, sharper answer. Every answer shows little chips of the meetings it used; click one to jump to it.

## Two kinds of questions

- **"What are my tasks?"** — this one is answered **straight from your data**, not the model. It's instant and exact, because a list of tasks should never be re-written or half-remembered by an AI. And it's smart about scope: ask *"tasks in the Postgres meeting"* and you get just that meeting's items; ask *"my open tasks"* and you get everything still open.

- **Everything else** — questions that need actual thinking (summaries, "what did we decide", "who was worried about X") go to the AI, which reads the relevant notes and writes you an answer grounded in them. If your notes don't cover it, it says so instead of guessing.

## Not one endless conversation anymore

There's now a **+ New chat** button to start fresh, and a **History** button that keeps your past conversations (named after your first question) so you can reopen or delete them. They're saved on your laptop, so they're still there after you close Toki.

## Still local, still free

None of this talks to a server. It uses the on-device model (or your own API key if you set one). No accounts, no cloud, no monthly cost.
