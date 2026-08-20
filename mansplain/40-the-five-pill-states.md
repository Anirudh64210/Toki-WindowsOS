# 40 - The five pill states

*2026-08-14. What the Figma export turned out to contain, what got built from it, and the one
button I refused to draw.*

## What you actually handed over

Twenty-two screenshots and a text file called "css code for all the design layers". The text file
is not a slice of the design. It is the raw Figma export of the entire board, 46,687 lines, and
when you index it by screen name it comes to **23 screens**:

- the notch: dashboard, three onboarding steps, live transcription, meeting notes, ask, settings,
  meetings list
- six "menu bar" states: idle, idle-with-a-close-button, transcribing, notes ready, record this
  meeting, processing notes
- the whole Library window at desktop size: Ask empty, Ask expanded, Ask in conversation, the
  library list-and-detail in three sidebar states, and Settings twice

That is the whole app, redesigned. Roughly two thirds of it is already built and close. This round
did the six menu-bar frames, because you picked them and because they were the biggest visible gap.

## The good part: the export gives exact numbers

Not "roughly a rounded pill" but: 320 by 50, corner radius 24, padding 12 and 12 and 12 and 64,
fourteen waveform bars 2.4 pixels wide with 2 pixel gaps at heights 5, 9, 14, 7, 11, 16, 6, 12, 15,
8, 5, 10, 13, 6. The stop button is a 24 pixel disc in `#FF114D`, which is a slightly different red
from the one in our theme, and side by side you can see it.

So this round is replication, not interpretation. Every number in the code came out of that file.

## What you now have

**Record this meeting.** The old prompt asked a question ("Record this meeting?") and gave you three
buttons. The frame is one pill that says what it does. Declining moved to a small circle with a
cross that appears when you hover, which is the frame's own way of doing it. If a scheduled meeting
is on, a second "Join call" pill appears beside it.

**The recording bar.** You said keep the tiny peeking Toki and have the bar appear when you open
him, so that is exactly what happens. The bar is the timer, a waveform that breathes, the red stop
disc, and a small up-down chevron. Press the chevron and it opens into the taller frame with the
live transcript underneath: each line marked YOU or THEM, or with a coloured initial once Toki has
worked out who is who, and the oldest line fading out at the top.

**Tidying up your notes.** The working pill, at the frame's size. It still tucks itself away into a
thin strip almost immediately, because it should never sit on your screen while it works.

**Notes ready.** This one is new, and it fixes something small but real. A finished note used to
tuck away into a blank 13 pixel strip - which is *the same blank strip* that means "still working".
The one moment Toki has something worth showing looked identical to the moment he had nothing. Now
it tucks into a pill that says "Notes ready" with an arrow.

**The two action pills** on Home are rebuilt from the same definition as the standalone frame, so
they cannot drift apart later.

## The one button I did not draw

The frame puts a **pause** button on the recording bar. I left it out.

Nothing in Toki's audio layer can pause a capture. I could have drawn the button and had it do
nothing, or drawn it and disabled it, and either way it would be a promise the app cannot keep in
the middle of a meeting - which is the worst possible moment to discover it. Your own rule is that
a control wired to nothing is worse than no control.

If you want real pause, it is a proper piece of work on the microphone and the meeting-audio
capture, and a new way for a recording to go wrong. Say the word and I will scope it.

## Two things underneath that had to change

The window used to draw one glass card and put everything on it. Three of these frames are not that
shape - "record this meeting" is a pill with a *separate* mascot circle beside it, and "idle" is two
pills with a gap between them. You cannot express a gap with one card, and putting glass pills on a
glass card gives you glass on glass. So for those states the window now stops drawing its card and
lets the view supply the pills. Toki still floats beside them.

The other: the recording bar is two widths, 272 collapsed and 344 open, with no state change
between them. A view can now own its own width.

## The half hour I lost, so it does not happen again

Every self-test hung. No output, no log line, no CPU. I spent a while convinced I had broken
something in startup.

`src/Toki/bin/Debug/` contains **two** output folders. The live one, and a stale one from an older
target framework last written on 15 July. A recursive search for `Toki.exe` returns the July one
first, and that binary hangs on launch. I was testing a month-old build of a different app.

It is in the build log and in ARCHITECTURE now.

## Checked

1,915 tests green. The hit-target test walked 261 clickable controls and found every box clickable
edge to edge. The layout, note, sticky and Library-performance tests all pass. And I rendered PNGs
of all five states and looked at them, including the expanded transcript, which needed its own shot
because nothing else could reach it - a state nothing renders is a state nobody looks at.

Your eyes are still the last gate. The build is ready for you to publish and try.
