# The waveform that actually hears you

You said the waveforms differ between Ask Toki and recording and it needs to be uniform, the
recording bar is just so damn ugly, the vertical chevron button is sooooo ugly, and our waveform is
one of the worst you've seen. All four are fixed. Here is what was actually wrong, because one of the
answers is worse than "it looked bad".

## There were two of them, and neither one could hear

Toki had two completely separate waveforms written at different times by different passes. The one on
the recording bar was fourteen white bars running a slow wave along the row. The one in Ask Toki was
seven pink bars, each doing its own little bounce. That is why they never matched: they were never
the same thing, they just both happened to be bars.

But the real problem is the other one. **Neither of them was listening.** Both were animations on a
timer. They danced exactly the same whether you were mid-sentence, sitting in silence, or had the
microphone unplugged. The bars moving meant nothing at all.

That matters more here than it would in another app. Toki's whole promise is that it does not make
things up about what it heard - we spent two weeks this month proving the transcript wasn't inventing
words. A waveform pretending to hear an empty room is the same lie, just small enough that nobody
called it out. It is also the one lie you can catch in real time, by looking at it while nobody is
talking.

## Now there is one waveform, and it is wired to the microphone

There is now a single waveform used in both places. The only two things that change between them are
how many bars there are and how tall they are: fourteen short ones on the recording bar, nine taller
ones in Ask. Everything else - the width, the gaps, the rounded caps, the colour, the way it moves -
is literally the same code, so they cannot drift apart again.

And it is fed by the actual audio. While you're recording, it reads the loudest of your microphone
and the meeting's own sound, so when the other person is talking it moves for them too, not just for
you. While you're holding Ctrl+Alt+Space, it reads that microphone. When nobody is talking it settles
into a row of small dots that breathe gently, the same slow breath Toki himself has. Speak and the
bars snap up; stop and they relax back down over about half a second.

**Silence looks like silence.** That is the whole design.

## The tricky bit: your microphone is very quiet

We measured this back on the 14th - your microphone records about 38dB quieter than the meeting
audio. That's a huge difference. If I had picked one fixed "this counts as loud" threshold, either
your mic would show a permanently flat line, or a normal mic would have the bars dancing to the hum
of an empty room.

So the waveform watches the last three seconds and works out, for itself, where your quiet is and
where your loud is, then draws you somewhere between them. Room hum settles at "quiet" and stays
resting. Your voice, however faint, still fills the row.

## The ugly button

The recording bar's expand button was a stacked up-and-down arrow floating on an invisible disc,
which is why it read as a scratch rather than a control. It is now one chevron sitting on a proper
piece of glass, with the same rim as the rest of the bar, and it turns over when you open the
transcript so the arrow always points the way it will go. The timer is now amber, which is the colour
time is everywhere else in Toki.

The design file draws the stacked arrows. You overruled it. You outrank the design file.

## How I know it works

The maths behind the bars has 22 new tests, and for each one I deliberately broke the code it guards
and confirmed the test failed, so none of them are decoration.

More usefully, I built a new check (`--wavetest`) that runs the real waveform on the real clock,
pushes a fake voice level at it, and prints the actual height of every bar at every stage. It caught
two things I had reasoned my way past: the bars were still noticeably raised a second and a half
after the "voice" stopped, and at full volume the tallest bars were growing about 20% taller than the
box they live in, quietly overlapping whatever sat below. Both fixed.

**What I still can't check, and you can:** how it feels in a real room. Talk and the bars should jump
up fast; stop and they should sink back to dots within about a second. Open Ask Toki and the
recording bar side by side and they should look like the same object. And the chevron should read as
a real glass button and flip over when the transcript opens.
