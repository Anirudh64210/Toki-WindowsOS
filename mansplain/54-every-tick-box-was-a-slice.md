# Every tick box was a slice

*2026-08-24*

You sent four screenshots and one question: it is a small, simple canvas you type on, so why is
anything wrong with it.

Fair question. Everything in those screenshots was really wrong, and here is the uncomfortable part:
the test suite had just passed 2,071 checks, and not one of them could see any of it.

## The tick boxes were being cut in half

Look at your screenshots again and the tick boxes are not boxes. They are little arcs, like the top
of a box with the bottom missing. The numbered lists were worse: "1." came out as "l." and "2." came
out as something that is not a character at all.

Here is what was happening. A tick box in a note is a real, clickable control that sits inside the
line of text. To put it in the line, Toki wraps it in a little container. Somebody (me, earlier) sized
that container to exactly the height of a capital letter, with a very sensible-sounding reason: a tick
box should sit on the same band as the capitals next to it, so the line looks even.

The reasoning was right. The mechanism was a knife. That container does not just *position* what is
inside it, it *cuts off* anything that does not fit. A capital letter is about 11 pixels tall. A tick
box is 24. So thirteen pixels of every tick box on your screen were being thrown away, and the top of
every digit with them.

One line of code was the entire bug.

## Why no test caught it

There *was* a test for this. It measured where the middle of each marker sat relative to the text, and
it said "0.10 pixels off" - which was completely accurate. The middle of the tick box was exactly where
it should be. It was just that most of the tick box was not there any more.

The test asked "is it centred?" and never asked "is it all there?". Those are two different questions
and I had only ever written one of them down.

So there is a new check now, and I proved it works the only way that means anything: I put the bug back
in, ran the tests, and watched the new check fail with "13.0px of it is outside its box" while the old
one carried on cheerfully reporting 0.10px. Then I took the bug back out.

## The "/" that stayed behind

In one screenshot there is a stray "/" sitting in the note on its own line. In another there is a whole
"/sdfsdfsdfsd".

When you type "/" a menu of commands opens. Choose one and the "/" you typed is supposed to disappear,
because it was you talking to the menu, not you writing.

It did disappear - if you pressed Enter. If you *clicked* the command with your mouse, it stayed. There
were two ways to choose a command and only one of them cleaned up after itself. And clicking is how a
person picks a template, so the templates were the ones that broke.

Now there is one route both go through, and one place the cleanup happens. Same class of fix as the
cursor bug last night: not "remember to do this in both places", but "make it impossible to do it in
only one".

## The templates gave you nowhere to write

Pick a template and you got Summary, Key points, Action items, stacked up with big empty gaps between
them and nothing to type on.

The gaps were not accidental, they were the bug. Between each heading Toki was inserting a *blank
line*, and blank lines in a note are deliberately drawn at about a third of a normal line's height so
that when you press Enter twice you get a small breath, not a huge hole. Three of those squashed slivers
in a row, plus the space a heading naturally keeps above itself, and you got roughly seventy pixels of
nothing that you could not even click into properly.

Templates now put a real, full-height, writeable line under each heading, Action items starts with an
actual tick box, and the cursor is already sitting on the first line you would want to type on.

(A small thing worth knowing: a "line with nothing on it" cannot be written down in a .md file - an
empty line in a file *is* a blank line, there is no difference. So those lines exist while you are
writing and quietly become ordinary blank lines when the note saves. By then you have either typed in
them or you did not want them.)

## The writing started miles from the edge of the page

Your third complaint. On a wide window with the list tucked away, the first letter of every line sat a
couple of hundred pixels in from the panel, floating.

The note page was set to "centre this column of text". That is a normal, even good, choice for a
document - it is what this file would look like on a website. But the rest of the window does not do it:
Templates and Settings both start hard against the left with the same margin, and the panel edge itself
obviously stays where it is. So the note was the one page that drifted, and it read as detached rather
than as centred.

It starts at the left now, same edge, same margin as everything else.

## And the strange block across the bottom

The writing area was a fixed 560 pixels tall. Below it went a thin line and a Delete button. On any
window taller than that, the effect was that your page stopped in the middle at a rule, with dead space
underneath - which is exactly what you described.

The canvas now takes the whole page, however tall the window is, and the line and Delete are pushed to
the bottom where a footer belongs. I also turned off the note's own separate scrolling, because it was a
scrolling box inside a scrolling page and which one moved depended on where your mouse happened to be.

## One more thing I found and did not touch

While measuring line spacing I found that the setting that is supposed to give notes their line spacing
has never actually done anything. Windows offers two ways of stacking lines, and the one a note uses
ignores that setting completely and just uses whatever the font asks for.

So the note's line spacing is, and always has been, the typeface's own - a bit tighter than what the code
says it is aiming for. Nothing on your screen has ever been wrong because of it.

I made the bulleted and numbered and tick-box lines match that real spacing exactly, so a mixed list sits
on one rhythm (before this, a tick-box row was noticeably taller than the bullet above it). But I have
*not* changed the spacing of the whole document, because that would re-space every note you have ever
written, and that is your call to make deliberately rather than something I do to you while fixing a list.
Say the word and it is a one-line change.

## How I checked

Not by reading the code and deciding it should work now. The render harness takes photographs, so I made
it take the photographs that were missing: a note with a template just inserted, the note page wide with
the list collapsed, and a blank note. The old harness only ever shot the note in a narrow three-column
window - and in a narrow pane, a centred column and a left-aligned one look identical. It could not have
caught this.

A page only tests as wide as you photograph it.
