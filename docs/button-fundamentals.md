# Buttons: the box is the button, and the label sits in the middle

*2026-08-12. Fundamentals pass, ahead of the full design + haptics revamp.*

The owner's report, which turned out to describe two separate defects:

> "all the buttons on the notch, even though they have a button activate UI on hover over the button,
> like the background turns grey right, the pressable button area is still the text or the symbol, every
> clickable area inside the button should be responsive to click on that button. and the symmetry for the
> text on the button is still weirdly not symmetric, and not centered"

Both are now measured, not eyeballed. Run `Toki.exe --hittest`.

---

## Defect 1: the hover highlight was a lie about where the button was

### The mechanism

A `TextBlock` with a click handler paints **only its glyphs**. That is all WPF has to hit-test, so the
gaps between letters and the space above and below them are dead. Put that TextBlock inside a parent that
draws a hover fill, and you get the worst possible combination:

- **hover** is triggered by the text, but **painted** across the whole box
- **clicking** still only works on the text

The box lights up, so the box looks like the button. It isn't. That is exactly the experience described,
and it is invisible in a screenshot - the pixels are identical whether the button works or not.

### The one-word version

```csharp
Background = Brushes.Transparent   // hit-testable
Background = null                  // NOT hit-testable  (and null is what "unset" means)
```

### What was actually broken

`--hittest` walks the real visual tree of every notch state, and asks WPF itself what would receive a
click at nine points inside each control (centre, four edge midpoints, four quarter-points). First run:

```
[--hittest] checked 172 clickable controls
RESULT: 44 problem(s).
```

| Where | What | Why |
|---|---|---|
| Calendar action strip | `Edit`, `Copy link`, `Open in Google`, `Delete` | bare `TextBlock`s with click handlers - only the glyphs responded |
| Calendar | the copy-link glyph | a 12px icon with no box at all |
| About | all 11 credit rows | `Margin="-6,2,-6,0"` bled the row past its parent, which clipped it - 6px each side looked pressable and was dead |
| Prep card, notes | every tick box | 18x18, below the 24px floor a pointer needs |
| Settings | the glass toggle | 36x21 |

### The fixes

- **`Views/HitTarget.cs`** - one shared helper that wraps content in a transparent, rounded, hand-cursored
  box with a 26px floor. It also *clears the cursor on the content*, so the inner element can never become
  a second, smaller "button" sitting inside the real one.
- **No negative side margins on a clickable row.** They put part of the target outside the parent's clip.
- **Tick box and switch keep their look, gain their hit area** - an 18px box inside a transparent 24px pad,
  a 21px track inside a 26px pad. Nothing moves; the target grows.

---

## Defect 2: every label sat 1.7px low

### Why "centred" text isn't centred

WPF centres a text's **line box**. A line box is taller than the letters: it reserves room for descenders
(g, y, p) and internal leading, and that reserve is **not symmetric**. Centre the box and the letters land
below the middle, because empty descender space is being counted as if it were ink.

The eye reads the middle of the **cap-height band** as the middle of a word. That is what must be centred.

### What was there before

```xml
TextBlock.LineHeight="{TemplateBinding FontSize}"    <!-- clamp the line box -->
<TranslateTransform Y="-1"/>                          <!-- ...then nudge it -->
```

Two problems. Clamping the line box **fights** the font instead of measuring it, and a hard-coded 1px is
only ever right at one font size - a correction that should scale with the em was frozen at whatever
looked acceptable on one button. Measured across the real notch, every pill label still sat **1.7px low**,
and the calendar's smaller buttons sat 1.5-1.6px low. Different sizes, different errors: the signature of
a constant where a ratio belongs.

### The fix: read the font's own metrics

`Theme/OpticalCenter.cs`, all in fractions of an em, all from the actual typeface:

```
line box height   = FontFamily.LineSpacing
baseline from top = FontFamily.Baseline
cap height        = GlyphTypeface.CapsHeight

optical middle of the letters = Baseline - CapsHeight/2
middle of the line box        = LineSpacing / 2
error (positive = too low)    = (Baseline - CapsHeight/2) - LineSpacing/2

nudge = -error * FontSize
```

Scales with type size, adapts to any font - including whatever the coming revamp switches to - and needs
no magic numbers. Nudges under a third of a pixel are skipped, because sub-pixel shifts only blur glyphs.

Verified two ways: `--hittest` measures the same thing independently and fails above 1.5px, and the
`buttons.png` render draws a true-centre line through each button so it can be checked by eye.

---

## The result

```
[--hittest] checked 170 clickable controls across every notch state
RESULT: every clickable box is clickable edge to edge, and every label is centred.
```

---

## Three things the test taught about testing

**The first version of the test was wrong in three ways, and each was instructive.**

1. **It probed the literal corners.** Toki's pills have a 13px corner radius on a 32px button, so a point
   3px in from a corner is genuinely outside the shape and *should* miss. Every healthy button was
   reported broken. A test that does not know the shape of what it measures produces confident nonsense.
2. **It measured centring on composite rows.** A meeting card has a date badge, a title and two chips; its
   first TextBlock is the day number, and comparing that to the row's middle produced "label sits 30.3px
   HIGH" for every row. Centring is only a question when the control **is** a label - now enforced by
   requiring exactly one TextBlock.
3. **It judged things that were not on screen.** Elements inside a collapsed or clipped container still
   report a size, and hit-testing them says something about the container, not the button. It also has to
   *force* the calendar's hover strip open, or the very controls it was written for are never examined.

**A screenshot cannot catch either defect.** Both look perfect in a PNG. That is the whole argument for a
test that interrogates the live visual tree rather than its picture.

---

## Round two: what the first pass still had wrong

The owner tested and said it was not fixed. They were right about the test, if not about every button.
Four more defects, three of them in the test itself:

**1. The test lied about its coverage.** Its header said *"across every notch state and Library page"*
while `Surfaces()` only ever built notch states. **The Library window - where most of the work happens -
was never audited at all.** A test that overstates coverage is worse than one that admits a gap: it turns
"not measured" into "measured and fine". Now covered: 170 controls became **216**.

**2. It hit-tested collapsed elements.** `VisualTreeHelper.HitTest(visual, point)` - the simple overload -
walks raw geometry and cheerfully returns elements that are `Visibility.Collapsed`. It reported the
Library's entire Ask page as "fully blocked" by a page whose own `IsVisible` was `False`. The filtered
overload, skipping `IsVisible: false` and `IsHitTestVisible: false`, answers the question actually being
asked: *where does a click go?*

**3. It measured the label instead of the content.** A button with an icon beside its text ("▪ Stop") is
correctly centred when the icon-plus-label *group* is centred. Measuring the label alone reported a
healthy button as 6.5px off. Vertical centring is about the letters; horizontal is about the whole
content.

**4. And one real rendering defect.** The correction is ~0.77px at 12pt - a **sub-pixel** vertical offset.
A render transform is applied after layout rounding and is not itself rounded, so it drops every glyph off
the device-pixel grid. The text goes **soft**, and soft text reads as subtly wrong without looking
obviously wrong. It is now snapped to a whole device pixel, computed per element from
`VisualTreeHelper.GetDpi` - Toki is per-monitor DPI aware, and a 4K panel at 150% needs a different
number from an external monitor at 100%.

### Ground truth, measured in pixels

Font metrics alone are an argument. When they disagreed with the owner's eyes, the tie-break was to
render the button and count pixels - find the coral fill's bounding box, find the white ink inside it,
compare the centres:

```
MeetingPrompt "Join & take notes"   pill centre 140.0   ink centre 139.5   ->  0.25px high
RecordingOpen "Stop"                pill centre  91.5   ink centre  91.5   ->  0.00px
```

*When a measurement and a person disagree, go and measure the thing the person is actually looking at.*

## Standing rules

- **Whatever shows the hand cursor is a box, and the whole box responds.**
- **`Transparent` is not `null`.** Unset means null means invisible to hit testing.
- **No negative side margins on anything clickable.**
- **24px is the floor**; the design targets are 32x34 and 34x34.
- **Never centre by nudging.** If a label looks low, compute the offset from font metrics.
- **One shared control, not per-site styling** - a revamp is coming, and one definition is one edit.

## Round three: it was never an input bug

The owner, after the click fix landed: *"the hit area works for the grey part of the buttons, but the
hover activate for the grey area is still only for the glyphs"*.

For a real WPF button those two cannot differ - hover and click share one hit test. So the report was
either impossible or pointing at something outside the input system. Rather than reason about templates
for a fourth time, the live window was instrumented: log any pointer position that is **inside a button's
box while that button's `IsMouseOver` is false**. Silent when healthy.

The owner exercised Settings repeatedly, toggling the theme four times. **Not one hover miss was
recorded.** `IsMouseOver` was true across every button's full box the entire time.

So the highlight WAS firing everywhere. It simply could not be seen:

| | overlay | over the notch (#000) | contrast |
|---|---|---|---|
| **dark** | white @ **12%** (`#1FFFFFFF`) | RGB ~31 | **1.15:1** |
| light | black @ 8% (`#14000000`) | on a white card | adequate |

The notch is deliberately pitch black. A 12% white wash on it is invisible - and the glyphs, being the
only high-contrast thing nearby, were the only place the eye could register any change at all. Hence
"hover only works on the text". **A visual bug wearing the costume of an input bug.**

Now `#38FFFFFF` (22% white) for hover and `#57FFFFFF` (34%) for press, with light bumped to 12% / 20%.

### Why this took four rounds

Every offline harness said the buttons were fine, and every one of them was right *about the thing it
measured* - the hit areas genuinely were fine. The defect lived in a dimension none of them looked at:
whether the feedback was **visible**. Three lessons, in order of how much they cost:

1. **A model of the app is not the app.** Four harnesses in a row - plain host, no reveal transform,
   mid-animation, wrong hit-test overload - each confidently cleared a bug the owner could see.
2. **Instrument the real thing early.** One `PreviewMouseMove` handler on the live window settled in a
   single test what four rounds of static reasoning could not.
3. **"Doesn't work" can mean "I can't see that it worked."** The input layer was correct from the start.
   Nobody thought to check the contrast ratio, because everybody was busy checking hit rectangles.

---

## Postscript: a choice from a set belongs in a list (2026-08-13)

Two controls fixed with the same reasoning, both reported by the owner as "impractical":

**The calendar's meeting length CYCLED** `30 -> 60 -> 90 -> 30` on each click. **15 and 45 minutes were
unreachable no matter how many times you pressed it**, and reaching a length you wanted meant clicking
through lengths you did not. Now a dropdown - 15 / 30 / 45 / 1 hour / 1 h 30 m / 2 hours - with a chevron,
so it reads as a choice rather than a toggle.

**The note editor's three Title / Heading / Body chips** became one "Aa" size picker. Three chips spent
the toolbar's whole width and still read as three unrelated buttons rather than one property with three
values.

**Rule: if a control has more than about three values, or any value it cannot reach, it is a list - not a
button that advances.** A cycling control also hides its own options, which is how four of six meeting
lengths went missing in plain sight for weeks.
