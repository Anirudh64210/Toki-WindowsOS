# The notch became glass

You asked for the notch to have the glassiness of Apple's Control Center, and for smooth borders on
every state, page and button, including Toki's bubble. Here is what happened, in plain language.

## The honest answer about "real" glass first

Real Apple glass blurs whatever is behind it. We researched whether Windows can do that for our
notch, and the answer is no, for a reason specific to how the notch is built: the invisible window
that holds the card is bigger than the card (Toki's bubble lives in the extra space, and the shadow
needs room too). Windows can only blur a window's entire rectangle, so "real" blur would frost a
patch of empty desktop around the card, not just the card.

But here is the thing the research found: Apple's own glass is 82-95% solid paint. The blur is the
last few percent. What makes it read as glass is the layering, and layering we can do perfectly.

## What the card is now made of

Instead of one flat colour, the card is now a stack, like a real material:

- A plate that is very slightly lighter at the top, because light comes from above.
- A whisper of grain across the surface, at 2.5%. You cannot see the grain itself, but it is the
  single biggest reason something reads as a material instead of flat paint.
- A soft glow near the top, like light caught in the glass.
- A bright edge along the top of the rim that fades as it goes down the sides, the way a real pane
  catches the light on its top edge. In light mode the rim ends on a dark lower hairline instead, so
  a white card still has an edge against a white app.
- Two shadows underneath, a tight one and a soft wide one, instead of one flat one.

The buttons and pills sitting on the card got the same treatment one level lighter, so they read as
glass resting on glass, which is exactly how Control Center gets its depth. The pills on the
"record?" prompt finally got real shadows; the code had a comment promising them, and no shadows.

## The smooth borders

Two real causes were found and fixed:

- Windows was drawing our 1-pixel edges "between" the screen's actual pixels at 125% or 150% display
  scaling, which smears a crisp line into a soft grey one. Every edge on a rounded surface now lands
  exactly on whole screen pixels, whatever your scaling.
- Apple's corners are not ordinary rounded corners; they blend into the straight edge gradually (the
  "squircle"). We built the true version of that curve for the card. Honesty from the measurements:
  at our corner size the difference is about a third of a pixel, so you will not consciously see it.
  It is there, it costs nothing, and the crisp edges above are what your eyes will actually notice.

## Every button now answers your hover properly

Hovering used to dim some buttons, and on the glassy surfaces the old highlight greyed them out like
frost. Now every button brightens, smoothly, the way Control Center does, and the label never dims
with it. The toggle switch got a hover state for the first time ever, and ticked checkboxes got
theirs back (they had silently lost it).

The Library window, the menus, the little popup pickers, the confirm dialog: all of them now wear
the same glass, from one shared definition, so they cannot drift apart again.

## How it was checked

All 2,015 tests pass, the contrast checker passed every page in both light and dark themes, all 290
clickable things are still clickable edge to edge, the frame-rate test shows the new layers cost
nothing measurable, and the finished app was rebuilt and repackaged with the tests run on the actual
packaged file. Your eyes are the last gate, as always: open the notch, hover a few buttons, flip the
theme, and see if it feels like glass to you.
