# The production pass

*2026-08-19, late night*

You asked for four things before your field test: notes that always come out right, a loader
that never stutters, notes text you can actually drag-copy, an up-to-date credits page - and a
codebase cleaned to the standard a senior team would ship. Here is what changed.

## The stuttering loader

While Toki writes your notes, the AI grabs every core of your processor - and the part of
Windows that draws the screen was left fighting for scraps, so the loader animation hiccuped.
The AI is now told to leave two cores alone, always. Your notes arrive a moment later at most;
the screen never pays for them.

## The over-eager loader

The loader sliver and the "Notes ready" pill sit at the top-centre of the screen - exactly
where your cursor passes on the way to browser tabs. They used to drop open if the cursor
lingered there even briefly. Now they open only when you CLICK them, the same rule the
recording pill already follows. Nothing opens over your work uninvited.

## Drag-copy in the notes screen

The notch's notes were built from many separate text boxes, so a selection died at the end of
every line. The notch now shows the exact same "one document" the Library shows - one drag can
sweep from the summary through the to-dos into the transcript, and Ctrl+C takes all of it. Same
notes, same behaviour, everywhere.

## If the fallback ever fires again

The rule-based safety-net note writer (the one that made tonight's 92-question mess) is now
capped: at most ten questions, ten key points, twelve to-dos, and one-word grunts like "What?"
are dropped. Even the worst case reads like a note now, not a strainer over the transcript.

## Credits are complete

Three models Toki ships were missing from the "Built with" page: the tiny one that tells speech
from silence, and the two that tell speakers apart. All three are now credited in the app and
in the notices file - that is a licence obligation, not decoration. The credit rows are also
real buttons now.

## The cleanup

Six helpers went through every source file and stripped about 2,300 lines of build-diary
comments - the dated war stories, the "this used to be" notes, the leftover duplicates. Every
hard-won rule survived, compressed to a line or two where the next engineer will see it. Not
one line of actual code changed. Afterwards: all 2,054 tests green, and every visual self-test
passes on the freshly built app.

## Getting it to you

Your running Toki still uses the old build, and I never close your app - you might be
mid-meeting. The new build is staged and a watcher is waiting: the moment you quit Toki, it
drops the new build into `dist` and you just relaunch. If you relaunch before it finishes, it
will say so and we retry.
