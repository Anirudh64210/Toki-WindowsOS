# Step 16 — Why notes take time (and how the fast apps cheat)

*July 28, 2026*

You asked a sharp question: apps like Granola, Fireflies, and tl;dv spit out notes in a few seconds — why does Toki take tens of seconds? And wouldn't writing notes *during* the meeting cause problems? Here's the honest answer.

## The one thing that explains almost everything: whose computer runs the AI

- **Granola / Fireflies / tl;dv run the AI in the cloud** — giant rented computers in a datacenter. Those machines write text at roughly **50–150 words-per-second**.
- **Toki runs the AI on *your laptop*** — your RTX 3050 writes at roughly **15–20 words-per-second**.

That's 5–10× slower, and it's basically the whole gap. It's not that they have a cleverer method — they're renting a much bigger engine, and charging you (or your company) for it. Toki's promise is the opposite: **free forever, and your meetings never leave your laptop.** The price of that promise is that the AI runs on the computer you already own, which is slower.

Think of it like printing a document: a home printer and an industrial press print the *same* document, but the press is 10× faster because it's a far bigger machine. Same words, different engine.

## Why "write notes during the meeting" is a trap (you were right)

The obvious idea — start summarizing while the meeting is still going — sounds faster, but it hurts quality, exactly as you guessed:

- **Missing context:** if Toki summarizes the first 10 minutes before the meeting resolves anything, it writes down tangents and half-decisions that got *reversed* later.
- **Over-capturing:** it turns every "we should maybe…" into an action item, because it can't yet see that the idea was dropped five minutes on.

So the good tools **don't** do that. They transcribe live, then at the end send the **whole transcript at once** to the AI and get one clean summary — with full context. That's exactly what Toki does too. **Toki's method is right; it's just running on a smaller engine.**

## So how do we make Toki's notes fast without breaking the promise?

Three honest paths, in order of how much they help:

1. **Bring your own key (fastest, opt-in).** If you *want* cloud speed, Toki lets you paste in your own OpenAI/Claude key (Settings → AI summaries). Then your notes come back in ~3–5 seconds — the same speed as Granola — because they're generated on the same kind of cloud engine. Toki stays free; you pay your provider a few pennies. Everyone who *doesn't* want that keeps the free, private, on-device default. Best of both worlds.

2. **Speculative decoding (makes the local engine faster, no quality loss).** A neat trick: pair the big local model with a tiny "draft" model that *guesses* the next few words cheaply; the big model then checks all the guesses in a single step instead of writing them one at a time. When the guesses are right (which they often are for ordinary phrasing), you get several words for the cost of one — realistically about **2× faster**, with **identical** output. It needs a second small model on board, so it's a real build, but it speeds up the free/local path for everyone.

3. **Keep tuning the on-device path.** Small wins (fewer AI round-trips, tighter prompts). Helps a little; won't ever reach cloud speed on a laptop.

## The bottom line

Toki isn't slow because it's doing something wrong — it's slow because it's doing the *right* thing (one clean, full-context summary) on *your* computer instead of a rented supercomputer, so that your meetings stay yours and cost you nothing per month. If you want Granola's speed, the bring-your-own-key option gives you exactly that, on your terms.
