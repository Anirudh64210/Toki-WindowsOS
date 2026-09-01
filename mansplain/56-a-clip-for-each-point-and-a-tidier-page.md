# 56 - A clip for each point, and a tidier page

You had four complaints about the notes: the little voice clips were wrong, the play button was
bulky, the jump arrow was ugly, and the layout was crooked. All four are fixed. One of them is only
mostly fixed, and I want to be straight with you about why.

## The voice clips

Last time you said the clips overlapped, so I made them never overlap - I forced each point's clip
onto different lines of the transcript. That was the wrong fix. It's like handing out slices greedily:
the first to-do grabbed the whole stretch, and everything after it got the leftovers. That's why the
first clip "covered all the to-dos."

The real goal isn't "never overlap," it's "each clip is exactly its own point" - and if two points
genuinely share a moment, a little overlap is fine (your Venn-diagram point was exactly right). So I
threw out the greedy approach. Now each point finds its own moment, a clip is capped at 12 seconds
instead of 45, and the clip zooms IN to just the phrase that point came from, not the whole paragraph
around it.

**The honest bit:** when several to-dos are rattled off in one long breath ("okay, I'll fix auth,
then the dark mode, then the demo, then feedback..."), Toki can't cut them apart perfectly. It knows
WHEN each sentence was said, but not when each WORD was said - and to split one breath into five
clean pieces you need the word-by-word timing. The good news: the speech engine actually produces
that timing, we just throw it away right now. Storing it is a proper next job. For normal meetings,
where points are said in separate sentences, the clips are already spot-on.

## The play button and the arrow

The play button was a chunky filled circle sitting in the middle of your sentences. It's now a small,
quiet triangle that only lights up when you hover it. The ugly "jump to transcript" arrow is gone
entirely - one quiet control per line, not two.

## The page

The section headings were sitting at the far left while their bullet points were pushed in behind a
colored bar, so the heading and its own content didn't line up - two edges fighting each other. I
removed the bar and lined everything up on one edge, with clean, consistent spacing. The to-do
checkboxes now sit on the same line as their words instead of floating above them. It's the same for
both the little notch and the big Library window, because they share one drawing routine.

Have a look, record a few meetings, and tell me what still feels off - especially whether you want me
to build the word-by-word timing next, which is what would make those rapid-fire to-do clips perfect.
