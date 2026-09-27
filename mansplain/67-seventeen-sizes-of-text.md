# 67 - Seventeen sizes of text

You said the tiny text was the giveaway:

> the fully small text is the worst obvious tell for AI generated slop man, i told you, subtext
> within headings and buttons on the window needs to be removed

So before changing anything I counted. Toki was using **seventeen different text sizes**. Thirty
pieces of text were 11 pixels or smaller. Three were 9 pixels, which on your screen works out to
about eleven actual dots.

That is the whole problem in one number. Seventeen sizes is not a scale, it is the absence of one.
A reader works out what matters by comparing sizes, and nobody can rank seventeen things. So the
page has no shape, and everything competes.

There are **five sizes** now, plus one for the big serif note title. Small print for dates and
counts, normal for reading, slightly bigger and bolder for labels, bigger again for section
headings, biggest for page titles. One hundred and fifty six sizes were changed to fit. There is
now an automatic check that fails the build if anyone adds a size that is not on the list, or any
text smaller than the floor. It caught two mistakes the moment I switched it on.

## The captions under headings

You were right that these read as generated. What made it obvious: **you had already banned them.**
Back in August you told me a settings page that explains each of its own rows reads like something
generated rather than a product, and that a row whose label does not do the job should be renamed,
not annotated. I wrote that down in the notch's settings file. I never applied it to the big window,
which had one under every single section.

Removing them forces the rename you were implying. "AI summaries", explained underneath by "On-device
by default, or bring your own key", is now just **"How notes get written"** - which says the same
thing, in the space that was already there, in one line instead of two.

One of them was not a caption at all. "One per line, or comma separated" is advice about typing, so
it now sits inside the box you type in and disappears the moment you start.

## The notes had it backwards

You said the summary looked bold and everything else looked small and weird. It did, and the reason
is that the summary was set BIGGER and HEAVIER than the section headings underneath it. So one
paragraph of content was outranking the page's own headings - the thing whose job is to organise it.

The old reasoning was good: someone who reads nothing else should still leave with the point. What
delivers that is **where** the summary sits, not how fat it is. It is the first thing under the
title, in full ink, with space beneath it. That is enough. It is normal size and normal weight now,
and the headings are the biggest thing after the title again.

## The transcript symbol

Still there, and you were right to keep asking. It was a small amber circle with a symbol inside it,
next to the word TRANSCRIPT in bold capitals in orange. Three different ways of shouting, for the one
section people open least.

Same story as the captions: this file's own notes already said not to do this. A comment a few lines
away says headers get no icon and no colour, and that the icon badges were "the cheap emoji look".
That one heading had simply never been converted.

## The calendar

Three different label styles in one scrolling list: a grey capitals week label, a **bold orange** day
header, and grey capitals day headers. And the week label said "THIS WEEK" eight pixels below a
subtitle that already said "This week".

One day style now. Today stands out by being darker rather than a different colour, because there is
already a red line across today marking it. The week label only appears where it actually separates
one week from the next.

In the new-meeting card, "Fill it in and press Create" is gone. It was narrating a form you are
looking at, above a button that says Create.

## The send button

The little paper plane was sitting off-centre in its circle. There was already a correction in there
- someone (me) had measured it by hand and nudged it one pixel. You still saw it.

The reason a hand-measured nudge could not fix it: a letter or symbol from a font carries invisible
padding, and it is not the same on the left and right. So "centre it" centres the padding too. It is
drawn as a shape now instead of typed as a character. A shape has no padding, so the middle of it is
genuinely the middle. This is the third button to get that treatment, so it is now the rule.

## The resizing, and my camera lying to me

You said the resizing still had not stopped. I filmed it at 60 frames a second and measured a jump of
87 pixels in a single frame - which looked like a smoking gun.

**It was my camera.** The screen recorder was dropping frames. The animation was fine.

So I made the app log the card's exact size on every frame it draws, and that showed the real
problem. The animation does run its full length smoothly - but the **first 70 milliseconds** were
running at roughly a quarter speed while the rest ran perfectly. And because of the curve it uses,
the card does about three quarters of its total movement in exactly those first 70 milliseconds. So
the stuttering part was the part you see, and the smooth part was the part where almost nothing
moves.

Both causes were mine, from the quality work last round. Toki is now drawn as live artwork rather
than a cached picture, which looks much better but means he is redrawn every frame - including
during the one moment the app is busiest. And the card's rounded shape was being rebuilt on frames
where its edge had moved by a hundredth of a pixel.

Toki now holds still for the third of a second a page change takes, and the shape only rebuilds when
it has actually moved. Measured the same way: those first frames went from an 11ms freeze plus a
31ms stall to a steady 8-9ms each.

## The model "failure" that was a switch

A self-test started failing with "model unavailable?". I told you it was probably memory pressure,
and gave you a real number to back it up.

**That was wrong, and you were right to push back.** The truth was that your notes setting was on
"Heuristic", so Toki was never asking the model at all. The number I quoted was real; the conclusion
I drew from it was not. A measurement is not a diagnosis.

The reason it took so long to find is a genuine bug, now fixed: two places in the code returned
"no notes" in complete silence. So "you told me not to use the model" and "the model is broken"
looked identical from the outside, and the test filled the gap with a guess. Both now say what they
actually found, and the test reports the setting instead of speculating.

To get your on-device notes back: **Settings, "How notes get written", pick On-device.** I have not
touched that file myself - it holds your encrypted API key and it is the one file I am not allowed
near.
