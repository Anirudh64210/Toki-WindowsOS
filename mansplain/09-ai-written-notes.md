# Step 9 — Toki writes the notes like a person would

*July 15, 2026*

## What we set out to do

Up to now, Toki pulled out decisions and to-dos with simple pattern-matching. This step gives Toki a real **AI writer** — a language model that runs **entirely on your computer** — to write a proper summary and clean, well-grouped notes, the way a thoughtful colleague would.

## What we did

1. **Installed the AI writer.** Toki now uses **Qwen3-4B**, a compact language model, downloaded once (~2.5 GB) and kept on your machine. Nothing is sent anywhere.

2. **Three careful passes.** Instead of asking the AI to do everything at once (which small models fumble), Toki asks in stages: first "what did they decide?", then "who has to do what, and by when?", and finally "write a short summary from those" — the summary is written *last*, from the notes, so it's accurate.

3. **A safety net.** Tiny AI models sometimes glitch — drifting into Chinese characters, or repeating "!!!!!!". Toki checks every answer, throws away anything broken, tries once more, and if it's still bad, quietly falls back to the simple pattern-matched notes. **You never see garbage, and you always get notes.**

4. **Instant, then better.** Because the AI takes about a minute on the processor, Toki shows you the quick notes immediately when a meeting ends, then **silently swaps in the nicer AI-written version** a minute later — no waiting on a blank screen.

5. **You're in control.** A new Settings screen lets you choose: the on-device AI, the simple built-in version, or your own API key (stored encrypted). And there's a **"Use GPU" switch that's off by default** — so Toki runs on your processor and won't strain your graphics card unless you turn it on.

## Two real bugs I hit and fixed

- **The AI crashed the app** the first time, because it tried to load onto your graphics card, which only has 4 GB — not quite enough, and the low-level code crashed hard. Fix: run on the processor by default (which can't run out of graphics memory), and make the graphics card an opt-in choice for bigger cards.
- **The AI hung forever** once, because of a subtle timing knot — it was waiting for the main app while the main app waited for it. Fix: run the AI properly off to the side.

## How I proved it works

I fed Toki a test conversation and had the AI write the notes. It produced an accurate summary, five clean decisions, and three to-dos with the right owners and due dates ("Priya — tomorrow", "You — this week") — all in plain English, nothing broken. It took about a minute on the processor.

## What's next

The last piece you asked for: **live preview** — watching the words appear on screen while you're still talking, instead of only after you stop.
EOF
