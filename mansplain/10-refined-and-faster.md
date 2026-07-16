# Step 10 — Making the notes better, cleaner, and faster

*July 16, 2026*

## What we set out to do

You tested Toki on real meetings and it worked — but you spotted things to fix: the notes had a redundant "Decisions" list that just repeated the to-dos, an "Open Questions" bit you didn't want, and the very first thing you saw when a meeting ended was an ugly rough draft that then reloaded a few seconds later. It was also slow. This step polishes all of that.

## What we did

1. **Rewrote the notes to look like Granola.** Now every meeting gives you three clean things: a labelled **Summary**, a few **Key points** (the actual topics and decisions), and a **to-do list**. The old repetitive "Decisions" list and the "Open Questions" are gone.

2. **Better titles.** Toki writes a short, specific title for each meeting from the summary — like a calendar event — instead of grabbing the first line of what was said.

3. **No more ugly first draft.** When a meeting ends, Toki now keeps showing its little "thinking" animation until the *real* notes are ready, then shows them once. You never see the rough version flash by anymore.

4. **Made the AI much faster.** The AI notes went from about a minute down to **around 13 seconds**. Two things did it: Toki now figures out exactly how much of your graphics card it can safely use and puts part of the work there, and it warms the AI up the moment the app starts, so a finished meeting never waits for it to load. Crucially, it only uses the graphics card by a safe amount — never enough to risk a crash, even while your video call is still using it.

5. **A proper Library window.** There's now a full window (from the Home screen, the tray, or the little "maximise" button) that shows every meeting and note side-by-side, with a **"Back to Toki"** button to pop back to the little pill. Your handwritten notes are editable there with a real **Save** button.

6. **Lots of small nice touches.** Slim, rounded scrollbars instead of the chunky Windows ones; nicer rounded checkboxes for to-dos; a gentle fade when you switch between notes; and a **Copy** button for the transcript.

## The honest part about speed

You asked to get the whole thing under 30 seconds. I dug into where the time actually goes and found it's **not** the AI notes — it's the **speech-to-text** (turning the recording into words). That runs at about four times real-time on the processor, and the two audio tracks (you and the other person) are done one after another.

I tried the obvious fix — doing both tracks at the same time — and it **scrambled the transcript** (the engine isn't safe to run twice at once). Your near-perfect transcription matters more than a few seconds, so I put that back the safe way. I also found the speech engine we ship can't use the graphics card at all.

So for now, a one-minute meeting takes about **35 seconds** end to end, mostly the transcription. The real way to beat 30 seconds is to **transcribe while the meeting is happening** — then when you hit stop, only the ~13 seconds of note-writing is left. That's the "live" feature we parked; it turns out to be the actual answer, and we'll come back to it.

## One bug I hit and fixed

While adding the fancy new scrollbars, I made a mistake in the styling that made the **whole window go black**. Tracked it down to a rule that isn't allowed in that spot, fixed it, and both windows draw perfectly again.

## What's next

Your call — either come back to make it genuinely fast with live transcription, or move on to the calendar and a proper installer.
