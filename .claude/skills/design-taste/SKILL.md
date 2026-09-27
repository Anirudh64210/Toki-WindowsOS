---
name: design-taste
description: The review instrument that separates a considered interface from generated "AI slop" - the visual tells, why each happens, the principles that replace them, how a senior designer writes UI copy, and an ordered audit procedure. Load before any design review, before shipping a screen, and when something looks generic but you cannot say why.
---

# Design taste

A review instrument for telling a considered interface from a generated one. Use it on a
screenshot, a build, or your own work before you show it to anyone. Every item is meant to be
checkable by looking, not by arguing.

The thesis in one line: generic UI is what you get when no hierarchy decision was made and the
interface is narrating itself because nobody trusted the labels. A model reaches for the
statistical centre of its training data, so absent a real decision it produces the average of every
SaaS dashboard ever shipped. Taste is the set of decisions the average cannot contain.

## 1. The tells

Each is stated so you can spot it in a static screenshot: what you see, why it happened, what
replaces it.

**Every element has the same visual weight.** Three headings, four labels, six values, all the same
size, colour and weight. Nobody decided what the screen is FOR, so nothing could be demoted.
Emphasis was skipped because de-emphasis is the harder half. Pick the one thing the user came for,
and emphasise it by de-emphasising everything else.

**Gradients that mean nothing.** A card header filled with a gradient with no data behind the
colour change. Gradient is the default decoration of the training distribution. A gradient must
encode something (depth, a range, a state) or be deleted.

**Icons that repeat their own label.** A trash can next to "Delete". The icon was ornament, not a
second channel. An icon either REPLACES a label (dense toolbar, with a tooltip) or adds what the
word cannot carry. Never both saying the same thing.

**Captions explaining controls that explain themselves.** "Toggle this switch to enable dark mode"
under a switch labelled "Dark mode". Nobody trusted the label, so the interface narrates itself.
This is the most reliable AI tell in copy. Fix the label, delete the caption. Helper text is for
what the label cannot hold: a format, a consequence, a cost.

**Emoji as iconography.** Free, always render, require no icon decision, and carry a platform's art
direction rather than yours. One icon set, one stroke width, one radius, one optical size.

**Everything centred.** Centring is the safe default when no reading order was designed, and it
destroys the left edge the eye scans down. Left-align text. Centre only short display text and
single-object states. Right-align numeric columns.

**A uniform grid of identical cards.** Six cards, icon on top, bold heading, exactly two lines of
grey text. The grid removed the need to rank the content, so nothing is important. Rank them, or
make it a list with dividers.

**Pills everywhere, and radii that do not nest.** Reserve the full pill for tags and status chips so
the shape still means something. Nest radii concentrically (see symmetry-and-spacing).

**Shadows on things that do not float.** Shadow means "above the plane and probably temporary":
menus, popovers, dragged items, toasts. Separate static regions with spacing first, a background
tint second, a hairline third. Shadow is the last option, not the first.

**Three or more competing CTA colours.** Each feature was coloured by whoever added it, so
importance is now negotiated by saturation. One accent per context, everything else neutral. If a
colour appears once, ask what it means; if it means nothing, remove it.

**Microcopy that congratulates, apologises, or explains the obvious.** Tone substituted for
information. Say what happened and what to do about it.

**Empty states that describe emptiness.** The empty state is the FIRST screen most users see, not
an error condition. Name the thing, say how to make one, offer the action.

**Badges and chips carrying no information.** A badge earns its place if its value CHANGES and the
change matters. A constant is a label, and probably belongs in the text.

**All-caps grey section labels above every block.** Labels compensating for a layout that does not
group visually. Group with space instead; a label is a last resort. If the space between groups
exceeds the space inside them, most headings become unnecessary.

**Stat rows nobody measured.** "10,000+ users, 99.9% uptime, 24/7 support" is filler shaped like
proof. Cut it, or use one number you can stand behind.

**Animation on high-frequency actions.** A menu opened sixty times a day playing a 400ms spring.
Interaction feedback under 200ms. Animate the rare and the novel, not the constant.

**Missing states.** No focus ring, no loading state, no error state, no disabled state. The happy
path was designed and the other four were not. This is the tell that survives a screenshot review
and fails a real user.

## 2. The principles that replace them

- **Hierarchy comes from type and space, not lines and boxes.** Before a border, try more space.
  Before a card, try a divider. Before a divider, try nothing. Boxes multiply; space does not.
- **One accent per context.** One thing is coloured because it is the action.
- **Removing is a design act.** A review that produces only additions was not a review.
- **If a signal can be quieter, make it quieter.** Lower every emphasis one step and see what
  actually breaks. Most do not.
- **Content-first density.** More real content per screen, less chrome around it.
- **Honesty.** A glow behind a button does not make the feature better, and a skeleton loader that
  fakes work is a lie.
- **Detail is the whole thing.** Care and accuracy in the process show respect to the user.
- **Opinions beat flexibility.** A screen that serves every use equally serves none distinctly.

## 3. How a senior designer writes UI text

1. The label says what the control DOES, as a verb where it is an action.
2. No narration. Delete any sentence describing the interface itself.
3. No exclamation marks. None.
4. No apology, no congratulation.
5. Errors say what happened and what to do next, beside where it happened. Never "invalid".
6. Preserve the user's work on every failure path.
7. Specific beats friendly. "Transcribing, about 40 seconds left" beats "Hang tight".
8. Numbers, dates and units are concrete.
9. Sentence case except proper nouns. Title Case On Every Label reads as a template.
10. Cut until it breaks, then add one word back. Most strings survive losing 40% of their words.

## 4. Audit procedure

Run in order. Each pass has one decision. The squint test invalidates half the later work if you
run it last.

1. **Squint.** Blur the screen or step back two metres. Name the single element that still reads.
   If two compete, or nothing stands out, hierarchy is undecided. Fix by demoting, not promoting.
2. **Hierarchy.** List every text element with size, weight and colour. Fewer than three distinct
   levels is flat; more than five is noise. Merge to three or four, used consistently.
3. **Delete.** Element by element: if this were gone, what could the user not do or know? Delete
   everything with no answer. If you removed nothing, you did not run the test.
4. **One accent.** Count the distinct saturated colours. Keep one plus semantic states, and check
   the accent lands on the primary action and nothing else.
5. **Alignment and spacing.** Draw the left edges; compare between-group space with within-group
   space. The numbers are in the symmetry-and-spacing skill.
6. **Copy.** Read every string aloud in a flat voice. Anything that sounds like a person describing
   an interface gets cut or rewritten as a label.
7. **States.** Tab through, force an error, empty the data, switch themes.
8. **The twin test.** Cover the logo. Could this be any other product? If yes, name the one thing
   specific to this product's content or job, and make it more prominent.

## 5. When taste yields

Taste is a tiebreaker, not an authority. It loses immediately to:

- **The design file.** If a frame exists, replicate it. Disagreements go back to its author as a
  question, not into the build as a "fix".
- **The owner.** Their eyes are the final gate on anything visual.
- **Accessibility and contrast.** Contrast minimums, focus visibility and hit-target size are
  floors. If a quiet treatment fails contrast, change the background, not the requirement.
- **Platform convention.** Users spend most of their time in other apps.
- **The user's data.** A layout elegant with three items and broken with three hundred is not
  designed yet. Test the long name, the empty list, the 500-row table.
- **Working over refined.** A control wired to nothing is worse than no control.

## Sources

- https://rauno.me/craft/interaction-design
- https://emilkowal.ski/ui/great-animations
- https://www.vitsoe.com/us/about/good-design
- https://medium.com/refactoring-ui/7-practical-tips-for-cheating-at-design-40c736799886
- https://developer.apple.com/design/human-interface-guidelines
- https://www.nngroup.com/articles/error-message-guidelines/
- https://www.figma.com/blog/karri-saarinens-10-rules-for-crafting-products-that-stand-out/
- https://linear.app/method
- https://www.raycast.com/blog/a-fresh-look-and-feel
- https://www.developersdigest.tech/blog/ai-design-slop-and-how-to-spot-it
- https://superdesign.dev/blog/why-ai-design-looks-generic
- https://www.holistics.io/blog/data-ink-ratio/

## The numbers, when "it looks cheap" needs a value rather than a verdict

`references/premium-notetaker-numbers.md` (added 2026-09-27) carries the measured layer under this
skill: Granola's and Wispr Flow's real type, spacing and colour-restraint specs; the published light
-theme rules (white is the CARD not the canvas, tinted not black-alpha ramps, the separation order,
the hairline band); typography numbers with sources; NN/g's reading-length data; the chip-versus-text
decision table; and the CHI 2011 retrieval study that settles whether a meetings Library earns its
place.

**Read section 9 before proposing any colour.** Toki's card is `Glass` at 85%, so every value in
every design system - all of which assume opaque surfaces - has to clear a contrast case those
systems never faced. Three recommended values were adopted, measured and rejected on exactly that.
