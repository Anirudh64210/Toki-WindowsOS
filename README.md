# Toki for Windows

A meeting assistant that lives quietly at the edge of your screen. It notices when you're in a meeting, offers to record it, transcribes it, writes clean structured notes, and remembers every meeting in a searchable local Library - **entirely on your machine**. One-time $20, no subscription, nothing ever leaves the laptop.

- **Full project brief:** [`WINDOWS-BUILD-PROMPT.md`](WINDOWS-BUILD-PROMPT.md)
- **Decisions & rationale:** [`ARCHITECTURE.md`](ARCHITECTURE.md)
- **Plain-language story of the build:** [`mansplain/`](mansplain/)
- **Design reference:** [`Toki-ProductDesign/`](Toki-ProductDesign/) (macOS notch design - Windows adapts it to a bottom-center pill)

## Building

Requires the .NET 8 SDK on Windows 10 2004+ (build 19041).

```
dotnet build Toki.sln     # build everything
dotnet test Toki.sln      # run unit tests
dotnet run --project src/Toki   # run the app
```

Note: unit tests/CI can never verify audio, GPU, or model paths - those are verified by the self-test flags (`--mictest`, `--asrtest`, …) run on a real PC, milestone by milestone.

---

# Build log

One entry per milestone: what was built, how it was verified, what broke.

## The morph had four causes, and film was the only way to find them (2026-09-03, latest)

The owner reported the notch "glitch resizing" through four rounds. Each round I fixed a real cause,
each time they said it was still there, and each time they were right - another was live underneath.
Recorded in order, because the sequence is the lesson:

1. **The page was laid out at the UNION width.** `PinContentForMorph` arranged the incoming page at
   the wider of the two sizes and clipped it, so its heading sat hard against the card edge with the
   text cut off; then `Completed` released to `NaN` and the whole page RE-LAID-OUT in one frame. Now
   it is arranged at the target, so it is correct on the first frame and identical on the last.
2. **The old page was destroyed instantly.** `Host.Children.Clear()` meant the card spent the morph
   showing the NEW page cropped to a rectangle that was still the old size. Fixing the layout could
   not help, because the CROP was the artifact. There is now a frozen raster of the outgoing page,
   taken before anything changes, dissolving over the incoming one - a real cross-fade.
3. **The clip's origin was relative to the union box.** While morphing, the card is centred in the
   union window, so the clip carries a left inset of half the slack. The instant the window shrank to
   target that inset was nonsense and cropped the card - and `SettleCorners` corrected it a frame
   LATER. Every morph ended with one frame shifted. The clip is re-origined in the same dispatcher run
   as the resize now.
4. **The settle was a second clock.** A `DispatcherTimer` at `ms + 20`: a deliberate gap that made the
   window resize a separate, later event, and being wall-clock it drifted under load - exactly when
   notes are generating. It hangs off the clip animation's `Completed` now, so the last frame of the
   motion and the resize are the same moment. The shadow also stopped fading in afterwards; it starts
   at 65% through so it is home when the card lands.

Plus the cross-fade became a HAND-OFF rather than a dissolve between equals - the outgoing page exits
at 35% on the depart curve and the incoming one waits 22% before starting, because with both fading
together there were 3-4 frames at 60fps where two pages were equally readable.

**MASCOT FLICKER, fixed.** `CollapsedView` is literally just a `TokiBlob` - the peek IS Toki - so the
cross-fade was photographing him and dissolving that picture at the card's centre while the real
bubble sprang in at the panel's gutter. The duplicate was the outgoing SNAPSHOT, not the timing, which
is why three rounds of delaying the bubble never touched it. A mascot-only peek is never snapshotted.

**INTERRUPTIBILITY.** Every animation now starts from the presentation value. The clip always did; the
zoom did not (unconditional `From`, so interrupting snapped back and re-zoomed) and the outgoing
snapshot captured only `Host.Children[0]`, throwing away the half-faded old page an interrupt was
looking at. Five mutation probes in `InterruptibleMorphTests`, including one asserting nothing
disables hit-testing during a morph - a transition that swallows clicks is not interruptible however
its values are computed.

**REVERTED: the glass work from earlier today.** The owner reported the light theme turning grey and
was right on all three counts. Library DWM acrylic is OFF - a translucent window lets the desktop tint
the page, and a reading surface has to hold its own colour. `CardPlateOpaque` is back to its two
original stops. And the eased `t^1.6` stop distribution is gone entirely: it applied to every Down/Up
gradient with more than two stops, which silently included `CardRim` - a wider change than intended.
Grain is out too, for the second time in this codebase's history; `CardMaterial` now says so.

**LOADER.** The sliver is a stage bar: it fills a third at a time through transcribing, identifying
speakers and writing, so it answers "is this getting anywhere?" without being opened - which matters,
because tucked is how it is actually seen. The shimmer stays on top as the within-stage heartbeat.
Elapsed time appears after 20s; before that a timer is noise, after it a wait with no number reads as
a hang.

**SETTINGS ROW.** The hover fill began exactly where the label did, because the row dropped its
padding to 0 to satisfy the ONE GUTTER rule. Fixed by moving the inset down a level: the page pads 4
and every row carries the remaining 12 itself. Text still lands on the same gutter; the button box is
now 12px wider than its label on each side. A negative margin was tried and `--hittest` failed it, as
it had in August - the scroll area clips what hangs past its content. That warning is blunter now.

**RECORDING BAR.** Tabular figures on the clock (proportional digits changed its width every second
and nudged the waveform for the whole meeting) and even optical insets. A full revamp is designed and
owner-approved but NOT built: `docs/backlog.md`, and the artifact linked there.

**METHOD.** ffmpeg is installed and `--noghost` makes the notch visible to a screen recorder (ghost
mode hides it from every capture, which cost a round to discover). Filming a transition at 60fps and
tiling the frames found all four morph causes and the mascot duplicate, and immediately caught two
faults the fixes themselves introduced. Reasoning about WPF animation did not.

2144/2144 green; six harnesses pass; `--notetest` and `--llmtest` pass on the packaged dist.

## One animated path, real glass on the Library, and the ghost-mode claim overturned (2026-09-03)

**THE REGRESSION, AND THE RULE IT BOUGHT.** The 2026-09-02 fix below widened `IsHoverPair` so every
transition animated. That was the wrong shape: it left TWO paths doing one job, and they drifted
within a day. `deferPeekBg` still read `peek && hoverMorph`, so a newly-animated collapse called
`SetSurface(peek)` on frame one - stripping the card's glass and the mascot instantly - and then
shrank an empty box for 240ms. The owner saw it as "glitch changes", and it was worse than the snap
it replaced.

The fix was not to correct the second path. It was to delete it: `hoverMorph` is now simply
`!fromHidden`, one branch handles every visible-to-visible transition, and `SnapResize` is the single
explicit opt-out. **Two code paths that must agree will eventually disagree.** `GlideTopGap` moved
into it, so the 10px `TopGapFor` change on MeetingPrompt to RecordingBar rides the morph curve.

**DEAD GLASS, PROPERLY.** `BottomBreath` was 14 and applied to EVERY auto-height panel, not just the
recording bar. DPI rounding costs one or two pixels; content is top-anchored, so fourteen read as the
card being slightly too big for its contents on every page. Now 6, and 2 for the fixed-height
recording row.

**THE GHOST-MODE CLAIM WAS WRONG.** `apple-glass-material-system.md` recorded that blur-behind is
permanently dead on the notch because of `WDA_EXCLUDEFROMCAPTURE`. Researched and superseded:
Microsoft's `SetWindowDisplayAffinity` reference lists exactly one caveat (DWM must be composing),
there is no report anywhere of a conflict, and stealth-overlay apps ship content protection and
acrylic together routinely - one project *requires* the exclusion for its blur, because a window that
captures the screen behind itself without excluding itself samples its own output and greys out.

The notch's real blockers are two, and both hold: `AllowsTransparency=True` makes it a per-pixel
layered window, which bypasses the frame surface DWM paints a backdrop into; and it is never
activated, and Microsoft documents that acrylic and mica fall back to a solid colour on deactivation.
A backdrop there would render its flat fallback essentially always.

**THE FINDING THAT REFRAMED IT.** Apple's own `SwiftUI.Material` docs: *"A material blurs a background
that's part of your app, but not what appears behind your app on the screen."* The glass being
admired mostly is not sampling the desktop. The gap was a RECIPE gap, not a blur gap.

So: **the Library window gets real DWM acrylic** (`DWMWA_SYSTEMBACKDROP_TYPE 38` =
`DWMSBT_TRANSIENTWINDOW 3`, Win11 22621+, with `Background=Transparent` AND
`CompositionTarget.BackgroundColor=Transparent` - setting only the first leaves WPF clearing to
opaque black first). It has neither blocker, and it is the honest A/B for judging the notch's fake
version. Confirmed applied on the owner's machine. `InkScrim` keeps body text legible over it.
Pattern adapted from WPF-UI's `WindowBackdrop` (MIT, credited); no dependency taken.

**And the notch got the recipe:**
- **3% tiled grain**, generated once, frozen, `ViewportUnits=Absolute` so it does NOT scale when the
  notch morphs (scaled grain is an instant tell). Noise is a named layer in Microsoft's published
  acrylic recipe: a smooth fill carries no high-frequency information and reads as synthetic, and
  grain also dithers away the 8-bit banding that makes large dark gradients look cheap.
- **Eased light falloff**: `CardPlateOpaque` goes from two stops to four, spaced on a `t^1.6` curve so
  they bunch toward the top. Evenly spaced stops read as "a gradient was applied"; eased stops read as
  a lit surface. Two-stop brushes are unaffected (0 and 1 are fixed points).

Both are plain Brushes, so the no-Effect-in-an-animated-subtree rule holds and they cost nothing per
frame or per resize.

**REDUCE MOTION.** Toki honoured none of it. `Motion.Reduced` reads `SystemParameters.ClientAreaAnimation`
live; `Ms()`, `Rise` and `Stagger()` collapse spatial travel while keeping opacity, because reduced
motion means gentler, not absent - a hard cut loses the cause and effect that motion was carrying.
Wired into the Library open, the mascot spring, the recording flip, the gap glide, the notes cascade
and the calendar accordion.

**Also in this pass:** the Library's two `DropShadowEffect` casters now leave for the ride and fade
back on settle (they sit inside the subtree `PlayOpen`/`PlayClose` scale and fade, so every frame was
a software repaint of a 22px blur); the recording-bar flip drops 520ms/480ms to 240ms/220ms and the
fall 12px to 6px, because it fires on every recording start and played inside a 14px clipped box;
`WarmFirstOpen` runs BEFORE the first summon so the first drop-down is not the cold one;
`RevealMascot` defers to `Render` priority so Toki's spring clock starts with the window rather than
before `Show()`; the settings toggle animates a `TranslateTransform` instead of `Margin`; and
`NotesView.Cascade` staggers the note body at 45ms per item, capped at 300ms (live transcript rows
are deliberately excluded - they already arrive staggered in time).

2139/2139 green; `--revealtest --hittest --measuretest --themetest --notetest --fpstest` all pass;
`--notetest` and `--llmtest` pass on the packaged dist. Plain-language story:
`mansplain/57-smoothness-and-honest-notes.md`. Research: `docs/note-quality-research.md`.

## The map step stops paraphrasing and starts citing (2026-09-02)

Stage 3. The map step was where invention entered: it emitted paraphrased dash bullets, so by the time
the writer ran the transcript was gone and it had nothing left to check itself against. A 40-minute
meeting's note was written from a paraphrase of a paraphrase, and a name the map step misheard became
a fact.

Transcript lines are now numbered into each chunk, the map step must CITE the line every point came
from, and `ValidateDigest` drops any point whose citation does not hold up: outside the window,
uncited, or whose content words do not appear at the cited line or its neighbours (threshold 0.5,
neighbours included because turn-merging can split a point across two lines). Citations are stripped
before the writer sees them, and the surviving points narrow the provenance range to what they
actually cover.

**Both numbers below are measured on meeting 48, a real 26-minute recording.**

First run dropped **2 of 4 windows entirely**. The diagnostics said `uncited 31, out-of-range 0,
unsupported 0` - the model was ignoring the citation format, not inventing. Two fixes:

- **Format-first prompting took citation compliance from 0% to about 94%** (32/34 and 23/25). The
  rules used to come before the shape; a small model follows the last simple pattern it was shown far
  better than a paragraph of conditions. The required shape and a two-line example now lead.
- **A verification step must never be able to delete more than it verifies.** An uncited window now
  falls back to the raw digest without provenance. Losing the content is far worse than losing the
  citation, and an uncited digest is exactly what the pipeline used before, so the fallback is a
  return to old behaviour rather than a new failure. This is the rule to remember.

Diagnostics name their counts rather than reporting a bare verdict: a window that lost everything to
UNCITED is a prompt problem, one that lost everything to UNSUPPORTED is invention or a threshold set
too high, and those need opposite fixes. "Dropped" alone could not tell them apart, which is why the
first run was a mystery for one build.

Nine mutation probes in `DigestCitationTests` pin the balance: strict enough that a made-up point
cannot cite its way in, loose enough that tidying spoken words is still allowed. 2139/2139 green;
UI harnesses pass; `--notetest` and `--llmtest` pass on the packaged dist. Next steps are in
`docs/backlog.md` under "NOTE QUALITY (2026-09-02)".

## Measuring a note, and the gate that stops it inventing people (2026-09-02)

Note quality could only be argued about before this, never measured. Three things landed.

**`--groundtest`, the instrument.** Scores every stored note against its own transcript for hard
entities (names, numbers, dates, money) by exact string match, no model involved. Precision is what
the note claims and nobody said; recall ships beside it because **precision alone is gamed by writing
less**, and without it every future "improvement" would just be the model getting terser. Report goes
to `%TEMP%\toki-groundtest.txt` so runs can be diffed.

Its first reading of the real 112-note Library was **67%** and almost all of it was the instrument
being wrong. Four false-positive classes, each now a mutation probe in `GroundingTests`:

1. A capital at the start of a bullet is grammar, not a proper noun. It was reporting "Identified",
   "Agreed" and "Rotate" as invented people in nearly every note.
2. People SAY "seventy percent" and Toki WRITES "70%". A digits-only comparison called a perfectly
   grounded number an invention.
3. A model that numbers its own bullets is not claiming the meeting mentioned the number 2.
4. "Still open from last time" is built from OTHER meetings' action items, so its names are grounded
   in a transcript the scorer is not holding. This one reported three correctly carried-over tasks as
   invented people.

**True baseline: 96.2% precision, 66.1% recall, 10 of 77 notes carrying an unsupported entity.** The
writer was never as bad as the first reading claimed. Recorded here so the next change has something
to beat.

**The thin-input gate.** The worst note this engine ever wrote came from one 90-word utterance in
which somebody said "I propose we do a sort of two-month rotation": it asserted three Options
considered, a firm Decision, and a three-bullet Rationale. That is a MISSING NULL CASE, not a
hallucination - the format had no legal way to say "there was not enough here". Below
`ThinInputWords` (70) substantive words the writer is never consulted and the note is the honest
minimal one. MEASURED against the real Library: median meeting 210 substantive words, that failure
56, the junk ("Mm-hmm", "Hello hello hello") 1 to 14. The first attempt guessed 120 and swallowed
every note test, which is how the calibration got done properly. It lives at the CALL SITE, not in
`WriteAsync`: the writer's job is to write from what it is handed, and judging a meeting too thin to
write about is the caller's call.

**The name gate.** A person who was never in the meeting is the failure users never forgive, and a
note that invents a colleague is not slightly wrong, it is untrustworthy in a way that poisons the
true lines beside it. `Grounding.PruneUngroundedNames` drops any written line naming someone absent
from the transcript (or its diarisation tags), running last on the assembled note so a name cannot
sneak in via the map digest, the final pass or the retry. Two deliberate limits: **names only**,
because numbers and dates get paraphrased legitimately ("about a hundred", "early next week"); and it
**does nothing at all when the transcript names nobody**, because then an absent name is evidence the
recogniser caught nothing, not evidence of invention. That second rule is the existing
`Real_tasks_are_never_dropped_by_the_name_filter` guard catching this change in the act, and it is the
same shape as Toki's evidence rule: below the floor, say nothing.

2130/2130 green; all five UI harnesses pass; `--notetest` and `--llmtest` pass on the packaged dist.
Findings behind all of it: `docs/note-quality-research.md`.

## The three transitions nobody animated, and the 8-minute note cliff (2026-09-02)

Two owner complaints, two root causes, both found by audit rather than guesswork.

**Motion.** `PillWindow.OnStateChanged` decided whether a transition animated at all by asking
`IsHoverPair(old, next)`. Three of the seven workflow steps failed that test and fell through to an
in-place branch with NO size choreography: the card teleported in one frame. They were exactly the
three the user clicks - accepting a meeting (169x40 -> 34x14), pressing Stop (272x64 -> 220x13), and
opening the notes. Meanwhile the two most polished moments in the app (the drop-down, the 560ms
notes-ready flight) are the two nobody asks for. The clip-morph machinery was always generic; only
the gate was narrow. The in-place branch now rides `AnimateSize` like a hover expand, and animates
by DEFAULT - `SnapResize` stays the explicit opt-out. Fixed alongside:

- **The union-width flash.** `PinContentForMorph`'s completion nulled `Root.Clip` at `t = ms`, but
  `AnimateSize`'s settle timer does not resize the window until `t = ms + 20`. Root stretches, so for
  1-2 frames the card repainted at the UNION width - collapsing Home flashed back to 344px, fully
  plated, every single time. It now holds the target rect until the window has actually resized.
- **~36% of every close was a dead frame.** `ConcealUp` clipped at the WINDOW height while Root is
  `CardPadB` shorter, so the retract started 24px above the card. Same bug in the handoff flight.
- **Content no longer pops.** `ShowNotesReady` / `RestoreTuckedView` swapped children at full opacity
  and then animated the box around them. Both now fade in under the morph (`FadeHostIn`).
- Toki's bubble fades out on Stop instead of vanishing in one frame; the linear (un-eased) opacity
  animations got their curves; `Motion`'s easings are cached and frozen instead of allocating a new
  unfrozen Freezable on every property read.

**Notes.** The quality ceiling was arithmetic, not prompt wording. On a 4GB card `LlmRuntime` picks
`ContextSize = 3072`, so `MaxChunkChars()` = `(3072 - 800) * 3` = 6,816 chars, which is about EIGHT
MINUTES of speech. Every longer meeting was map-reduced into "2 to 5 short dash bullets" per window
and the entire note was then written from those - a 40-minute meeting's note came from at most 25
bullets, then capped at 5 bullets per section. The model was writing from a summary of a summary.

- The map step now returns **5 to 12** bullets and is told this digest is all the writer will ever
  see of that part, keeping names, numbers, dates and quantities verbatim (220 -> 420 tokens).
- The per-section bullet cap **scales with the meeting** (5 / 7 / 9) instead of being 5 forever.
- The final pass gets **760 tokens**, not 480. The truncated-tail guard existed precisely because
  480 landed inside ACTION ITEMS, the last thing written, so to-dos were the content most often lost.
- **The writer is finally told what the meeting WAS.** Date, length, the app, the calendar event
  title, who was invited, and which voices diarisation actually heard. All of it already existed in
  `CurrentPrep` and was used only for name repair. A model that cannot tell a 4-minute catch-up from
  a 90-minute review writes both the same way. Facts only, never a hint to go find something.

**Two instruments were blind.** `--llmtest` and `--renotes` printed `notes.Decisions`, which the model
path never populates - they have always printed `KEY POINTS (0)` for a model-written note. They print
`Sections` now. You could not measure a note-quality change before this.

**`--seed` was writing into the real Library.** It built `new MeetingStore()` with no path, i.e. the
real `%APPDATA%\Toki	oki.db`, unguarded and not `#if DEBUG`. Two hand-written C# string literals
(ids 1 and 2) are sitting in the owner's 112-note store, and they read better than real output, so
anyone judging quality by browsing the Library was over-rating the product. It now uses a temp db.

Also: `Disc_labels_pin_their_own_line_metrics` had been passing only because the test assembly was
stale. Its regex embedded a literal CRLF while `PillParts.cs` is LF-only, so it could never match. The
pattern is `
?
` now and the guard is real again. 2115/2115 green; `--revealtest`, `--hittest`,
`--measuretest`, `--themetest`, `--notetest` all pass, and `--notetest` + `--llmtest` pass on the
packaged dist.

Skills: Emil Kowalski's `apple-design`, `animate`, `review-animations`, `improve-animations`,
`animation-vocabulary` and `design-eng` are vendored (MIT) under `.claude/skills/emil-*` as the
source-of-numbers layer beneath the house `motion-and-feel`, which still wins on conflict.

## UI polish: rename, colour collisions, and the never-audited states (Fix 5) (2026-08-26)

A batch of visual-polish work, added the `apple-design-motion` skill (MIT) as the source-of-numbers
under `motion-and-feel`, and audited/fixed the design-audit's "missing states" hole.

- **Library WINDOW renamed** "Toki" -> "Library" (the in-app nav brand + "Back to Toki" kept).
- **Greeting quieted** (hub Ask): 19px SemiBold -> 14px Medium TextSoft, so the mascot leads, not an
  info-less line (the comment already said "demote, never promote").
- **Suggestion chips**: ragged `WrapPanel` -> a uniform 2-col `UniformGrid`, equal-width chips.
- **Colour double-meaning removed**: the Settings toggle-on and the loader shimmer both moved OFF the
  reserved content colours (green=decisions, pink=questions) to brand **Blue** = "Toki active" chrome.
- **Motion audit** (against the new Apple-numbers skill): `Motion.cs` already well-aligned and
  deliberately SNAPPIER than Apple's midpoints - that IS the "premium speed"; no value changes.

**Fix 5 - the states the harness never photographs:**
- **Disabled** was a real gap (a disabled button looked live) -> shared button styles now dim to 0.38 +
  drop the hand cursor when `IsEnabled=False`.
- **Focus**: the app is deliberately mouse/hotkey-driven (`Focusable=False`; the notch is never focused).
  Added a keyboard-focus RING (`TokiFocus`, blue) and made the **Settings toggles keyboard-operable**
  (Space toggles) - a real win for the focusable Library window. Full keyboard-nav of every button is a
  bigger a11y project, flagged not blind-done.
- **Long-data**: already handled (ellipsis + max-width/height throughout; `--measuretest` = no clipping).
- **Error**: already handled (fallback banner, calendar `ShowError`, voice hints, Ask `Failed`).

Objective UI gates all pass: `--hittest`, `--themetest`, `--measuretest`. 2115 tests; dist republished,
launched. Home layout (Fix 1) is out for the owner's pick as an artifact, not yet built.

## Load-on-demand LLM on mid-RAM machines [roadmap #5 slice] (2026-08-25)

The owner noticed Toki felt heavy at startup on a 12GB box: it prewarmed and kept the 2.5GB notes LLM
resident on every machine >=8GB. The RAM-tier slice: only a HIGH-RAM box (>=32GB) prewarms + keeps the
LLM resident for instant notes; everyone else (the common 8-16GB laptop) now LOADS ON DEMAND - the LLM
loads when a meeting's notes or an Ask first needs it (~10s once per session, under the loader), so idle
Toki stays light and startup isn't heavy. New `App.PrewarmLlm` (>=32GB), distinct from `LowMemoryMode`
(<8GB, one-model-at-a-time, unchanged). `EnsureLocal` already loaded lazily, so skipping the prewarm IS
the slice - no other change. Verified in the startup log: "load-on-demand: LLM loads when first needed"
on the 11.4GB dev box (was "normal mode"). 2115 tests, `--notetest`, launched. This is the ONE RAM
piece worth doing; the rest of RAM-tiering (bigger context on 32GB+) is marginal and stays parked.

## Speaker diarisation: now downloaded, and run BEFORE the notes [roadmap #2] (2026-08-25)

The owner's complaint (meeting 118): two clear speakers, but nobody could be named and every to-do went
to "me". Two root causes fixed:

- **The diarisation models were never downloaded by the app** - only the self-tests fetched them, so a
  fresh install never had diarisation at all and every remote voice stayed "Them". They now download in
  the background at launch (pyannote segmentation + CampPlus embedding), same as the other models, once
  Parakeet is present.
- **Diarisation ran AFTER the notes** (an old decision from when it took ~7 min). It is now measured at
  ~5s on a real meeting (RTF 0.005), so it runs BEFORE the notes (owner decision): the writer sees real
  names / "Speaker 1/2" labels instead of everyone "Them", which is what lets it attribute to-dos to the
  right person and recognise a roll-call as introductions, not tasks. The loader shows an "Identifying
  speakers" stage. `ResolveSpeakers` split into `TagSpeakers` (before notes) + `InferOwners` (after).

Unchanged and re-confirmed: **transient by design - no acoustic record of anyone's voice is ever saved**
(clusters live and die inside `SpeakerResolver`; only renameable text lands in the DB). CampPlus is
already credited (About + notices). Verified: `--diartest` split a real meeting into 2 speakers in 5.5s;
2115 tests; the LIVE flow (a fresh recording naming its speakers) needs the owner's real-meeting test.
Historical meetings stay as they were until re-transcribed. dist republished, launched.

## Live transcription now streams (fixes the ~21s Windows lag) [roadmap #1] (2026-08-25)

The owner: the live transcript shows up in ~15-21s chunks on Windows (instant on the Mac build).
Diagnosed: `LiveTranscriber` decodes an offline Parakeet WINDOW only once ~15s (+6s search) of audio
piles up, so the first line takes ~21s. Researched how the good tools do it (agents): Meetily does live
in ~5s chunks; the confident pattern is a STREAMING model for the preview while the whole-file pass
stays the saved record. Owner chose the proper implementation, not the cheap window-shorten.

Built: a **streaming zipformer-transducer (20M, k2-fsa, via the existing sherpa-onnx runtime - no new
native lib)** as the LIVE-PREVIEW decoder. It emits words as they are spoken (~1s), proven by the new
`--streamtest` (feed a WAV in 1s chunks, watch the partial grow: "THE YELLOW LAMPS" -> "...WOULD LIGHT
UP HERE" -> finalized). New `StreamingTranscriber`; `LiveTranscriber` taps the same resampled audio to
it and raises `PreviewPartial`/`PreviewFinalized`; `RecordingOpenView` shows a per-speaker in-progress
line that commits when the utterance ends, under a pulsing **"Live" readiness cue**. The streaming
output is disposable and PREVIEW-ONLY - the SAVED transcript is still the accurate whole-file Parakeet
pass (the "one whole-file call per stream" law holds). Downloads in the background at launch; falls
back to the Parakeet window until it lands. Credited (Apache-2.0, k2-fsa) in About + notices.

Verified offline: `--streamtest` shows incremental partials + finalized lines on real speech; the
expanded recording view still renders; 2115 tests. The LIVE feel needs a real-meeting test (audio paths
always do). dist republished, `--llmtest` + `--notetest`, Toki launched.

## The loader peek's "two inner lines" - the ACTUAL cause this time (2026-08-25)

The owner, rightly frustrated after two wrong fixes: inside the squircle of the peeked loader there
are two curved lines cutting in like false edges. My earlier passes chased the SHIMMER poking out
(capping the track width) - a real thing, but NOT what the owner was seeing. The real cause, found by
rendering the tucked sliver crisply and zooming the rounded ends: `CardMaterial`'s inner top/bottom
edge-light RINGS are drawn at the full-card radius (23), but a 13px sliver's plate clamps its corner
to ~6.5 - so each ring's radius-23 corner arc sweeps ACROSS the short sliver as a crescent "edge",
one at each rounded end. The material even had a `Radius` setter for exactly this ("a ring at the
wrong radius cuts across the corner arcs"), but nothing set it for the sliver.

Fix: `PillWindow.ApplyCornerGeometry` now sets `Material.Radius` to the plate's CLAMPED radius minus
the rim, every time it lays the corners out - so the edge-lights stay concentric with the real
corner at any height. Full cards are unchanged (still 23); only slivers/short cards shrink the ring.

Verified by render: the `Summarizing_sliver_*` shots show clean rounded ends now, versus the two
crescents before; `--themetest` + `--revealtest` pass; 2115 tests; dist republished, `--llmtest` +
`--notetest`, Toki launched.

Also undid a bad earlier idea in the same breath: an earlier pass had capped the shimmer TRACK to a
fixed 188px centred (chasing a shimmer-poke-out that was never the real bug). On a sliver wider than
220 that left the outer bar un-swept, so the loading highlight appeared to start from the MIDDLE. The
track is full-width again (Margin 16), and the sweep range is now computed from the track's REAL width
(`StartShimmer` + `OnTrackSizeChanged`: `-70 -> trackWidth`), so the highlight travels the whole bar
edge to edge at any width - no magic numbers.

## Ask-by-voice now transcribes with Whisper (fixes "Hi Toki" -> "Yeah") (2026-08-25)

The real fix for the owner's "Hi Toki records as Yeah": Parakeet is excellent on whole meetings but
collapses 1-2 word push-to-talk clips, because there's too little audio to lock onto. Whisper pads
every clip to its 30s window internally, so it stays accurate on short phrases - so the ASK-BY-VOICE
path now uses **Whisper base.en**, while Parakeet stays the meeting engine.

- Runs through the SAME sherpa-onnx runtime already shipping (whisper as ONNX) - no new native
  library, no Microsoft.ML.OnnxRuntime conflict. New `WhisperTranscriber`; `TranscribeFile` prefers
  whisper and falls back to Parakeet only until whisper finishes downloading.
- Downloads in the BACKGROUND at launch (base.en, ~200MB archive), never mid-meeting; warms at launch
  beside Parakeet. Existing setups fetch it automatically; the Ask feature degrades to Parakeet in the
  meantime, so it never breaks.
- Guarded against Whisper's one known failure: trained on YouTube captions, it emits stock phrases
  ("you", "Thanks for watching", music glyphs) on near-silence - those whole-output hallucinations are
  rejected as "nothing said", while real greetings ("thank you", "bye") pass through.
- Credited (MIT, OpenAI) on the "Built with" screen and in THIRD-PARTY-NOTICES - it ships now, so the
  licence obligation is met.

Verified end-to-end: `--voicetest` decodes the model's own test clips cleanly through the Ask path
(Whisper, not Parakeet); 2115 tests (new whisper-noise + earlier guard probes); build clean; dist
republished (folder), `--llmtest` + `--notetest`, Toki launched. Test it: hold Ctrl+Alt+Space and say
"Hi Toki" - it should transcribe right and greet you, not dump a task.

## Ask-by-voice: a garbled clip no longer gets a confident wrong answer (2026-08-25)

The owner: saying "Hi Toki" records as "Yeah" and then the assistant dumps a random task from the
notes (screenshot). Diagnosed two stacked failures: (1) Parakeet mangles a ~1s clip ("Hi Toki" ->
"Yeah") - a known weakness of the model on 1-2 word utterances; (2) "Yeah" then slipped past the
greeting guard (it wasn't in the small-talk list) and, because the notch was open over a meeting's
notes, got GROUNDED on that on-screen note and summarised it - a confident answer to a non-question.

Fixed the second (the symptom the owner sees): bare backchannels/affirmations ("yeah", "yep",
"mm-hmm", "yes", "no", "nah") now intercept as small-talk and get a light canned reply, NEVER a
notes answer. So a mis-heard 1-word clip degrades to "Cool." instead of a wrong, grounded-sounding
task. Also made the Ask silence-trim TRAILING-ONLY (was head+tail): the Parakeet empty-decode bug is
about trailing silence, and trimming the head risked eating a soft first word (the "Hi" in "Hi
Toki") - leading silence is harmless to the recogniser, so it stays.

NOT fixed, and honest about it: the transcription itself. "Hi Toki" -> "Yeah" is Parakeet's
short-clip weakness, and the two real fixes are both milestone-sized - a whisper.cpp fallback for the
Ask path (whisper's language model handles short phrases far better; needs a new native lib + model
download + attribution) OR the warm-mic pre-roll ring buffer (blocked by the "no always-on mic"
rule). Put to the owner as a decision (backlog).

Verified: 2103 tests (new SmallTalk + silence-trim probes); build clean; dist republished (folder),
`--llmtest` + `--notetest`, Toki launched.

## Ask-by-voice: hotkey no longer leaks, notch stops flickering, better listening (2026-08-25)

The owner: holding Ctrl+Alt+Space fires Claude's search bar every split second; Toki is "very bad at
listening and replying"; and the notch FLICKERS while the answer generates (screenshot showed a
cut-off answer under three stacked "I didn't catch that" lines). Researched push-to-talk capture and
compact streaming-answer UX (agents: Wispr Flow, superwhisper, Talon, Raycast, MS Copilot CUX, plus
the sherpa/whisper/NeMo issue trackers). Fixes:

- **The chord no longer leaks.** `PushToTalkHook` returned `CallNextHookEx` unconditionally, so
  Ctrl+Alt+Space (and its auto-repeat) reached whatever app had that binding. It now SWALLOWS the
  chorded Space and its repeats (returns 1) while Ctrl+Alt are held; a BARE space is never swallowed,
  so typing is safe and a missed key-up can never lock the spacebar.
- **The notch stops flickering while answering.** The 70ms stream flush called `GrowNotch` every
  patch, re-morphing the window ~14x/sec (the exact anti-pattern the research names). Now the answer
  text mutates IN PLACE and scrolls; the window only grows on a coarse ~280ms throttle and settles
  once at the end - size changes on state changes, not per token.
- **Listening is better on real speech.** Parakeet TDT returns an EMPTY decode when fed trailing
  silence (NVIDIA-NeMo #15757) and a push-to-talk clip always ends in silence - a top cause of
  "I didn't catch that" on speech that WAS there. The Ask path now trims the quiet head/tail (125ms
  keep-pad so no consonant is clipped) before the recognizer, isolated from the shared meeting decode.
- **Errors escalate instead of stacking.** A repeated identical Toki line now PULSES in place instead
  of walling three copies; a second empty capture in a row switches copy - points at a muted mic and
  offers "type it in the Library" - rather than repeating the same hint (research: never a 3rd
  identical attempt).

PROPOSED, owner's call: the single biggest listening win in the research is a warm mic + pre-roll ring
buffer (no clipped first word, no per-press init latency). That means keeping the mic stream open
continuously, which CROSSES the hard "NO always-on mic" rule - so it is NOT built; flagged for an
owner decision (see backlog).

Verified: 2092 tests (4 new silence-trim mutation probes); build clean; dist republished (folder),
`--llmtest` + `--notetest`, Toki launched. Test it: hold Ctrl+Alt+Space (Claude's bar should stay
shut), speak a question (answer should stream without the notch flickering), and try a silent hold
(one line, then an escalated one - no stack).

## Tighter deliverables, and more filler out of the transcript (2026-08-25)

The owner: the notes are still too long and filler-heavy - "the to-do, the summary, key points should be
small, straight to the point, clean". Two changes, after researching how the best tools do it (Wispr
Flow, Otter, Granola - agents dispatched per the standing rule):

**Deliverables are now Tight (owner's pick).** The summary was the ONE block the concise pass never
touched, so it stayed a 3-4 sentence corporate paragraph. It now runs through the same pass: hedges out,
capped to a 2-sentence outcome-first lead. Bullets dropped from a 22-word backstop to 15 and actions
from 14 to 11, and the writer prompt now asks for 6-12 word bullets, outcome-first, no reported-speech
frame. Same pass runs on the LLM note and the heuristic floor, so the two stay the same shape.

**The transcript loses more filler, safely.** Research verdict: Otter strips disfluencies delete-only
(no verbatim mode - a common complaint); Wispr goes further and even re-tones, and reviewers catch it
"improving" what people said; Granola keeps the transcript verbatim as auditable ground truth and cleans
only the notes. Toki's model IS Granola's (the transcript is the ground truth our evidence clips are cut
from), so we took the safe floor everyone shares: the readability pass now also drops the er/eh/ah
interjection family and comma-parenthetical discourse fillers ("it was, you know, huge" -> "it was
huge"). Delete-only, never a rephrase - the literal uses ("do you know what time", "a tool like this",
"I really like it") have no comma and are never touched, so evidence links still match. Genuinely
rambling speech and side-talk that was really said still stays; that is faithful, not a bug.

Verified: 2088 tests (14 new, each a mutation probe that fails against the old code); `--llmtest` shows
the tight 2-sentence summary + terse actions; dist republished (folder), `--llmtest` + `--notetest`,
Toki launched. Existing saved notes keep their old text until re-transcribed; new meetings and any
re-transcribe use the tight style.

## The loader shimmer can no longer poke past the sliver's rounded ends (2026-08-25)

The owner: the "Tidying up your notes" loading bar keeps showing its shimmer sticking out past the
rounded edges of the sliver, a bug we thought was fixed. Root cause found by rendering the tucked
sliver faithfully (new `Summarizing_sliver_*` shots in `--render`): the shimmer's track used
`Margin="16,0"`, which stretches it to the CONTENT width. During the tuck morph `PillWindow` lays the
content out at a UNION width WIDER than the card while the card's clip animates over it, so the track
- and the bar sweeping inside it - reached past the card's rounded end and stayed hidden only while
`Root.Clip` kept exact pace. It didn't always, so a 3px nub of shimmer poked out of the rounded ends
intermittently: why it "kept coming back". Fixed structurally: the track is now `MaxWidth="188"` +
`HorizontalAlignment="Center"`, so its OWN `ClipToBounds` is always inside the 220 card no matter how
wide the content is laid out or where the morph clip is - the fragile timing dependency is gone.

Verified: the new `union` render variant (content 264, card 220 centred) reproduces the old escape
condition and now shows the bar fully contained at every sweep position; `--revealtest` passes; full
suite green; dist republished (folder), `--llmtest` + `--notetest`, Toki launched.

## The note scrollbar is always reserved, so the summary stops reflowing (2026-08-25)

The owner: with the transcript collapsed there is no scrollbar and the text is wider; expand the
transcript and the scrollbar appears, narrows the column, and the summary reflows. Cause: the note
ScrollViewers used `VerticalScrollBarVisibility="Auto"`, which adds/removes the bar's 10px of width as
content overflows. Fixed: they are `Visible` now (notch `NotesView` scroll, the Library standalone note
box, and the sticky-note outer scroll), so the slim bar's width is ALWAYS reserved and the column width
never changes. The bar's track is transparent, so a not-needed scrollbar is invisible - only the thumb
shows when there is something to scroll.

Verified: 2074 tests, `--hittest`, `--libshot` (reserved bar is subtle, summary width stable); dist
republished (folder), `--llmtest` + `--notetest` 53/53, Toki launched.

## Speaker naming for two-party calls, editable-looking tags, retitle on re-transcribe, one ghost icon (2026-08-25)

Four owner findings:

1. **The other speaker could not be named.** Meeting 118 was a two-person call (You: 20 lines, Them: 19)
   but diarisation left every `speaker_tag` null, so `SpeakerChips.Build` showed only the "You" chip and
   the other person was unnameable - and the notes read as if the owner was the only participant. Fixed:
   the chip row now offers a "Them" chip whenever there are untagged non-You lines, and `RenameSpeaker`
   was taught that a stream label ("You"/"Them") also names the UNTAGGED lines of that stream (the
   `speaker` column is text). Name "Them", regenerate, and the model can attribute to that person.
2. **The name tags did not look editable.** They were transparent `PillButton`s that vanished into the
   light theme. Now `ChipButton` (visible hairline rim) + a fill, so a tag reads as a tappable field.
3. **Re-transcribe did not retitle.** `RegenerateNotesAsync` now replaces the floor placeholder title
   with the model's title (only when the current title IS the placeholder - a user-typed title is kept).
4. **Two ghost icons.** The recording bar had an eye; Settings has a crossed-out video camera. The bar
   now uses the SAME camera-with-strike, so the ghost control is one design across every state.

Verified: 2074 tests green; `--hittest`; `--render` (recording bar camera icon confirmed); dist
republished (folder), `--llmtest` + `--notetest` 53/53. The speaker chips live in the Library's expanded
transcript and the action rows are below the harness fold, so those two are for the owner to eyeball.

## Notes visualisation revamp, researched against Granola + Apple (2026-08-25)

The owner: the note layout looks cheap - emoji-ish section icons, bullets not on the text line, to-do
tags (owner/due) as tan pills sitting left of the text with a gap, and "look at Granola like I asked."
This time I sent two research agents (Granola note UI via Mobbin screenshots + web; Apple Notes/Bear/
Craft typography), and they converged hard. What changed, per the findings:

- **No section icons.** The round icon chips ("emoji") are gone. Headers are now sentence case,
  semibold, full-ink, body+1 size, separated by whitespace (24 above / 8 below) - differentiated by
  WEIGHT, not colour or an icon (exactly what Granola and Apple Notes do).
- **Real dot bullets, hanging indent.** Dropped WPF's `List` (its marker floats off the text line -
  that was the "bullet not on the same line" bug). Each bullet is now a hanging-indent paragraph with
  a drawn 4.5px disc in a fixed marker column; the dot and text sit on ONE line and wrapped lines
  align under the text.
- **To-do tags are inline muted prose, not pills.** The tan owner/due chips are gone; owner (only when
  it is a named person, not "You") and due date now trail the task as dimmed text - Granola writes
  dates in the prose, no chips, no colour. The checkbox hangs in its own column, centred on the text
  line.
- **Monochrome body.** One ink for headers/text, muted for markers and metadata; the only colour left
  in the body is the action checkbox. No rails, no chips, no dividers.
- **Alignment: LEFT, not justified.** The owner asked for justified, but BOTH references are
  left-aligned, and justified in a ~300-520px column with no WPF hyphenation makes rivers and hurts
  reading - so I implemented left-aligned and flagged it; flipping to justified is a one-line change if
  the owner still wants it after seeing this.

Verified: 2074 tests green; `--hittest`, `--themetest` (both themes), `--libshot` (headers/bullets/
spacing confirmed clean); dist republished (folder), `--llmtest` + `--notetest` 53/53. The action-row
render is below the harness fold, so the checkbox/inline-meta is for the owner to eyeball in the app.

## Evidence clips get their own moment, and the note page gets one gutter (2026-08-25)

The owner: the first to-do's clip covers ALL the to-dos, the last bleeds into the next part, the play
button is bulky, the jump arrow is ugly, and the note layout is misaligned. Four things:

1. **Evidence clips are per-point now, not "disjoint".** Last session's `DisjointRanges` forced every
   clip onto different transcript lines so none overlapped - a greedy assignment that gave the FIRST
   point the whole run-on stretch and the rest scraps. Removed. Each point resolves on its own, its
   clip is capped at 12s (was 45s), and the clip NARROWS to the phrase inside the utterance that
   carries the point (proportional word timing, since we store per-utterance not per-word times). Two
   points that share words may overlap a little; none swallows the others. HONEST LIMIT, measured with
   `--evidencetest`: when several to-dos are spoken in one continuous breath (one 25s utterance),
   sub-second cuts are a guess - the proper fix is storing the recogniser's per-WORD timestamps
   (which it emits and we currently discard); left as a TODO. Points on separate utterances already
   cut cleanly (meeting 117: 5/5).
2. **The play control is quiet** - a bare 16px triangle, dim at rest, brightening on hover (was a 20px
   filled disc with a rim, a bead in the sentence).
3. **The jump "↳" is gone.** One evidence affordance per line, not two.
4. **The note template got one gutter.** The section heading and its rows now share a left edge and
   group by SPACE (heading-above 24, heading-below 8, rows 8) instead of a decorative accent rail that
   put the heading and its content on two different left edges. Action checkboxes sit on the text's
   line (centre-aligned, was top-aligned). Applies to both the notch and the Library (one renderer).

Verified: 2074 unit tests green (2 new evidence-precision probes, incl. one that fails if narrowing is
removed); `--evidencetest` (per-point, containment check); `--hittest`, `--themetest`, `--measuretest`;
`--render`/`--libshot` for the layout; dist republished (folder) and `--llmtest` + `--notetest` 53/53.

## The transcript leaked into the summary (2026-08-25)

The owner opened a recent meeting and the summary and to-dos were the first lines of the
conversation, pasted in verbatim. Root cause was a stacked failure: the on-device model FAILED to
load that session (`The type initializer for 'LLama.Native.NativeApi' threw` - the Vulkan/llama.cpp
native backend crashed, and .NET caches that failure for the whole process, so every meeting after
it silently fell back), and the heuristic FLOOR that then ran was itself leaky - its "summary" was
just the opening ~140 characters of speech, and it accepted any sentence containing an action word
as a to-do, which the brevity pass then truncated into fragments. Prior fixes had hardened the
MODEL path against exactly this; nobody had applied the same rule to the floor, and the floor is
what runs when the model dies.

Four things, each verified:
1. **The floor is honest now.** No raw-transcript summary (empty, like the model path - the renderer
   drops it); a to-do must be SHAPED like a task (a commitment or an imperative opener), not merely
   contain a task word. Two mutation probes in `NoteExtractorTests` fail against the old code.
2. **The trigger + a real retry.** `LlmService` now distinguishes a native poisoning (needs a
   restart) from a transient failure, and `RetryLocal` / `App.RegenerateNotesAsync` clear the stuck
   flag and re-attempt, so a session isn't locked to floor quality.
3. **Two honest banners on the notes** (`NoteRenderer.FallbackBanner`): "Basic notes" with **Show
   transcript** + **Try again with the on-device model** when the floor ran, and a privacy-clean
   "Private, on this device" nudge when the API model was chosen but no key is saved. The retry
   regenerates in place.
4. **A ghost toggle on the recording bar** (an eye / eye-off puck), so a meeting started with ghost
   mode off can be hidden mid-recording without leaving the bar.

Then the banner kept saying "restart and try again" - so the model was not just crashing once, it
could not load AT ALL in the packaged build. The root cause turned out to be PACKAGING, not the
model: the single-file dist launches (WPF works) but the on-device model never loads in it
(`NativeApi` type-init throws: "the native library cannot be correctly loaded"). LLamaSharp finds its
GPU backend under `runtimes/win-x64/native/{vulkan,...}/` relative to the app base dir, and a
single-file app's base dir is the TEMP EXTRACTION folder, which does not reproduce that per-backend
tree. The dev build loads Qwen and writes notes in ~29s from identical source. Fix: **publish the dist
as a FOLDER, not single-file** (`-p:PublishSingleFile=false`), which keeps the `runtimes/` tree beside
the exe where LLamaSharp looks. This is also what the M8 installer (Velopack) ships. Every prior
single-file dist has silently run the heuristic floor for notes; the AI notes seen before were from a
non-single-file build.

Verified: 2075 unit tests green; `--hittest` (321 controls, the new puck included), `--themetest`
(banner contrast both themes), `--render` (eye glyph + bar layout); dist republished as a FOLDER and
`--llmtest` (Qwen loads, real notes) + `--notetest` 53/53 + `--hittest` all pass on it. CLAUDE.md's
publish command still says single-file - owner's call to change (see the packaging memory).

## Every marker on the canvas was a slice (2026-08-24, latest)

The owner sent four screenshots of the note canvas and one sentence: it is a small simple canvas to
type on, so why is anything wrong with it. Everything they photographed was real, and every one of
them was invisible to a suite that had just gone 2,071 green.

**The tick boxes and the numbers were being CUT IN HALF.** A marker lives in an `InlineUIContainer`
inside a `Border`, and that Border was given a Height of exactly the text's cap height (11px at a
16px body) so the marker would sit on the same optical band as the capitals beside it. The
arithmetic was right and the picture was wrong: an `InlineUIContainer` CLIPS its child to that box,
so a 24px tick box lost thirteen of its twenty four pixels and every "1." lost the top of the digit
and read as "l.". The alignment self-test measured where the marker's CENTRE sat and reported 0.10px
off, correctly, the entire time. Centred and visible are two different questions and only one of them
was being asked. The Border now sizes to what it holds, a `RenderTransform` (ink, not layout) drops it
onto the cap band, and each marked line is pinned to the body text's own line height so a taller
marker overflows its line instead of stretching it. `--notetest` now asks the second question too:
put the Height back and it fails at "13.0px of it is outside its box" while the offset check still
passes.

**Picking a command with the mouse left the "/" in the note.** The keyboard path took the typed
"/todo" back out before running the command; the row's own Click handler did not. So Enter came out
clean and a click left "/" or a whole "/standup template" sitting in the writing. There is now one
commit path in `SlashMenu.Run` that both go through, and a self-test that chooses a command the way a
pointer does.

**A template handed over nowhere to write.** The skeleton was headings separated by blank lines, and
a blank line is deliberately drawn at a third of a row, so what arrived was a stack of titles with
unclickable slivers between them and about seventy pixels of nothing in each gap. Templates are now
built as BLOCKS rather than through Markdown (Markdown cannot hold an empty body line, which is what
collapsed them), every heading is followed by a real full-height line, Action items opens with a tick
box, and the caret lands on the first line you can type on.

**The writing started two hundred pixels in from the panel.** The note page was `Stretch` +
`MaxWidth`, which is WPF for "centred up to this measure" - fine in a 300px pane, and on a wide window
with the list put away it left the first letter of every line adrift from the page's own edge. It is
left-aligned now, at the same left edge and the same padding as Templates and Settings.

**And the block across the middle of the page** was a 560px-tall editor followed by a hairline and a
Delete row, so on any taller window the writing appeared to stop at a rule with blank space under it.
The canvas takes the whole page now (a star row with a MinHeight bound to the viewport), the footer is
pushed to the bottom, and the editor no longer scrolls itself inside the page's scroll.

Found along the way: `LineStackingStrategy.MaxHeight` (a FlowDocument's default) ignores `LineHeight`
entirely, so the document's documented 1.5em leading has never actually been in force - the real
leading is the typeface's own ~1.33em. Marked lines are pinned to THAT, so lists and paragraphs share
one rhythm. Raising the whole document to 1.5em would re-space every note ever written and is the
owner's call, not a side effect of fixing a list.

Verified by rendering, not by reasoning: `--render` now photographs a note with a template just
inserted, and `--libshot` photographs the note page wide with the list collapsed and blank - the two
shapes the old narrow three-column shot could never show. 2,071 unit tests and `--notetest` 50/50,
`--hittest`, `--measuretest`, `--stickytest`, `--uitest`, `--themetest` all green.

## The onboarding card kept space it never used (2026-08-24, latest)

The notch sizes an auto-height page as `max(designHeight, measuredContent)`, so the design height is a
FLOOR, not a size. Onboarding's floor was 250 while its three steps really measure 203, 227 and 152 -
so every step was padded out, worst on the last one at 98px of dead card. Floor is 130 now and the
measure wins.

No test could see it, because every check around panel sizing asked whether a page was CLIPPED and
none asked whether it was PADDED. `--measuretest` now checks the floor against real content for every
auto-height page and names the dead pixels; reverting the number fails it.

Also written this session: a full technical audit of the Windows build for the macOS port, in the Mac
repo. Every subsystem, every state, every paid-for lesson, with the Windows-specific mechanisms
separated from the portable logic.

## Ask can read your own notes, and the audit got acted on (2026-08-24)

**Manual notes are retrievable now.** They always were indexed - their text sits in the FTS table -
and three queries filtered them out on the way back. The worst part was not that Ask could not find
them: the router used those same filtered queries to decide whether any evidence existed, so a
question whose only answer was in a hand-written note got classified as general knowledge and
answered from the model's own head with no citation. A confident answer about something else, not an
empty one.

Content search and title search are open now. Counting is not: "my last meeting", "how many meetings
did I have" and "meetings last week" still see only real meetings, because a hand-written note is not
a meeting and an answer the user can check against their own calendar has to stay right.

The trap on the way was `BuildContext`, which renders a summary, sections and actions - none of which
a manual note has, all of it being in `Body`. Opening retrieval without fixing that would have
retrieved a note and cited it as empty, which is worse than not finding it. Notes render their body
now, labelled "(your own note)" so the assistant says "in your note" rather than implying somebody
said it out loud. `VisibleMeeting` also required a real meeting, so opening your own note and asking
"summarise this" had grounded on nothing; it accepts any viewed note now. Sticky notes stay excluded,
deliberately. Guarded in `--dbtest`, both directions.

**And the taste audit got applied.** The Ask greeting dropped from 30px to 19px (it was the largest
type in the window and the only thing on it carrying no information). "Your meeting assistant." and
"How Toki looks and what it shows in a share." are deleted; the one real fact in the second moved onto
the row it describes. The notch greeting lost its exclamation mark, and "New Chat" became "New chat".
The Templates page lost its cards - they were putting every template's text 16px right of the page
title, which is two left edges in one column - and its constant grey chips became plain text, with a
"Default" tag on the one that actually is. And the loader says what it is doing ("Transcribing", then
"Writing your notes") instead of one unchanging friendly phrase across a wait that can run minutes.

Four findings were deliberately left: the icon-plus-label buttons on Home (platform convention beats
the rule), the green toggles and pink progress sliver (two colours with two meanings - a real finding,
but the call is the owner's), the ragged Ask chips (content-sized pills that wrap are normal), and
Home's three-actions-two-treatments (a real finding whose fix is a redesign of the front door, not a
refinement). All of it, applied and not, is in `docs/design-audit-2026-08-24.md` and `docs/backlog.md`.

## Second pass: measured against Notes, Notion and Notepad (2026-08-24)

First, the reason none of the above appeared to work: the fixes went into the dev build and the
owner's shortcut launches `dist\Toki.exe`, which was still the 23:09 publish. They restarted, saw the
identical bug, and were right to. `dist` is now published from this work and `dist/Toki.exe
--notetest` passes 51/51 - the packaged artifact, not the dev one.

Then the real question: why does it still not feel like Apple Notes or Notion. Audited against the
numbers measured from Notion's production CSS that were already sitting in the block-editor skill.
Four things, all measurable, none of them opinions:

- **The markers did not share a left edge.** Every marker was right-aligned in its gutter, so a
  bullet's ink started at 24.0px, a number's at 19.6px and a tick box's at 8.0px. Sixteen pixels of
  raggedness down the left of every mixed list, while the headings and paragraphs beside them all
  started at 0. Right-alignment is right for numbers alone and had been applied to all three. One
  left edge now, for markers, headings, paragraphs and all.
- **The marker column was 1.75em**, against Notion's measured 24px at a 16px body. It is 1.5em now.
- **The leading was 1.33, not the 1.5 the code claimed** - see the entry above for why nobody
  noticed. Every line kind now pins its own line height: 1.5em for body and lists, 1.3 for headings.
  This re-spaces every existing note, on purpose.
- **A heading owned space below it as well as above.** Notion's rule is that a heading owns the space
  above and nothing owns the space below; ours added 0.62em underneath, which is why a template
  opened seventy-pixel holes. Zero now.

And **a blank line is a full line again**. It was drawn at 9/13 of a row so that Enter-Enter gave "a
beat, not an empty row" - a considered decision that turned out to be a gap you cannot predict and a
target you can barely click, and it is not what Notepad, Notes, Notion or Word do. Superseded and
dated in ARCHITECTURE.

Then a third round from the same session, all measured rather than guessed:

- **The caret was drawn inside the W of "Write".** The editor draws its first character at x=5 (a
  RichTextBox keeps a content offset of its own) and the hint was pinned at x=1 - so the placeholder
  sat four pixels LEFT of the cursor. The hint's position is read from the document now, not
  hard-coded, so it survives the text-size dial. It is also drawn at the body size; it had been stuck
  at 13px while the body moved to 16, so the placeholder was visibly smaller than the writing it
  promised.
- **The note filled half the pane.** The 600px reading measure (66 characters, Bringhurst) was correct
  typography and not what the owner wanted: on a wide window it read as a page that had stopped early.
  OWNER DECISION: the note fills the pane. The argument for a cap, and the number, are kept in the code
  at the point where the width is decided.
- **The title sat 5px left of its own body**, for the same content-offset reason. The title now reads
  the origin from the editor after layout, so a note has ONE left edge from its title to its last
  bullet.

A note on method, because it cost a round: a build error was masked by grepping for `error CS` when the
failure was `error MC3000` (a XAML comment cannot contain a double hyphen). The rule was already in
memory - grep for `error`, not `error CS` - and I broke it. The tests then "passed" against a stale
binary.

2,071 unit tests, `--notetest` 53/53 (four new checks: pitch, left edge, caret clearance, hint size,
all mutation-probed), `--hittest`, `--measuretest`, `--stickytest`, `--uitest`, `--themetest` green, in
`dist` as well as dev.

**Also reported, not changed** (both in `docs/backlog.md`): a taste audit of every notch state and
Library page (`docs/design-audit-2026-08-24.md`), and the finding that the Ask assistant is
recorded-meetings-only - hand-written notes are in the FTS index but filtered out on read by
`AND m.kind = 'Meeting'` in three queries, while the Library search box has no such filter.

## The caret that forgot its column (2026-08-23)

CORRECTION, same day: the first fix for this reached into WPF's internals by reflection to clear the
cached column. It passed every check in development and did NOTHING in the shipped build. The app
publishes SELF-CONTAINED, so it carries its own PresentationFramework, the private field was not
found there, and the catch swallowed the failure silently. Running the harness on the PACKAGED exe
is what caught it: 39 checks in the dev build, 38 and one failure in dist, deterministically, with
"Down lost the caret's column: 67.3px -> 5.0px".

It now uses public API only: nudge the caret one character with the real editing commands and back,
which makes WPF recompute the column from where the caret actually is, then re-assert the position
if the round trip landed elsewhere. No internals, and it passes in BOTH builds. Lesson 13 in
CLAUDE.md exists for exactly this, and the reflection version was written and verified against the
dev build alone.


Researched Notion's editor end to end (the block model, every key, the markdown input rules, the
slash menu, the published measurements) and the research found a bug in the editor we shipped this
morning.

WPF caches the x position a vertical arrow key should aim for, and does NOT clear that cache when
code sets `CaretPosition`. This is dotnet/wpf#3757, open, and the triage note on it says the fix is
"very unlikely" to land. Toki moves the caret in code constantly: every Enter, every paste, every
slash command, every click seam. So the first Down or Up after any of those snapped the caret to the
left edge of the line instead of leaving it under the text where the reader put it.

MEASURED, in the live editor: with the caret 67.3px into a line, Down landed at **5.0px**. It now
lands at 65.6px, which is the nearest character to where it started.

The fix is one function. `SetCaret` is now the ONLY way the editor moves the caret in code, all
eight sites go through it, and it clears the stale cache after every move. The cache is internal to
WPF, so reaching it needs reflection, and the whole call is wrapped: if a future .NET moves the
field, the caret still works, it just forgets its column again.

The mutation probe is the test's own numbers. Reverting `SetCaret` to a plain assignment fails
`--notetest` with "Down lost the caret's column: 67.3px -> 5.0px", so the check cannot pass
vacuously, and it carries a floor on the starting x so it cannot pass at zero either.

Verified: `--notetest` 39 of 39 (one new check), full suite green (2,071), `--stickytest`,
`--measuretest` and `--hittest` all pass.

**Found by the same research and NOT changed, because each is a product decision for the owner:**
Tab currently types a literal tab into the note (`AcceptsTab="True"`), which is neither indenting
nor nothing; Enter and Backspace over a selection hand off to WPF, whose split copies formatting but
not markers, the exact fault the editor's own comment says it was built to avoid; and the repair
pass runs on every text change including mid-IME-composition, which is how CJK input gets eaten. The
IME one cannot be verified on this machine without an IME, so it is written down rather than guessed
at.

## The note canvas (2026-08-23)

The owner: the manual note is "the least intuitive feature", the option names are bad, the template
picker covers the text, and a bullet and its text are "not even on the same line". Researched
Notion's block editor and Notability's canvas thinking, then rebuilt.

**The permanent toolbar is gone.** It sat above every note, cost a row of a 356px panel, and named
things badly ("Steps", "Style", "Aa 10"), which is an interface explaining its own capabilities
instead of getting out of the way. Notion's rule is that a blank line is a blank line with a faint
hint, and the menu exists the instant you ask for it and not before. So: **type "/" at the start of
a line** and the commands appear AT THE CARET, filtered as you type, with arrows to move, Enter or
click to run, Escape or space to cancel. Mid-sentence a slash stays a slash, because a menu that
opens while somebody types "and/or" is the interface fighting the writing.

Names now say what they do: "Steps" became "Numbered list", "Style" split into the three block names
it was hiding (Text, Heading, Subheading), and every template is its own entry. Text size left the
writing surface entirely: it is a page preference, not a per-line command.

**The template picker was anchored to the whole toolbar**, so it opened across the full width and
landed on top of the writing. It is a slash entry now, opening at the caret.

**Markers sit on the text's line.** The bullet was the character "•" at a hardcoded 12px, whose
ink rides high in its own box and did not scale with the note, and the marker container used
BaselineAlignment.Center, which centres on the LINE BOX including the room a font reserves for
descenders. The bullet is now a drawn ellipse sized in ems, the number matches the body size, and
the container is baseline-aligned inside a box of exactly the cap height.

MEASURED, in the live editor: `--notetest` now reports how far each marker's ink centre sits from
the middle of the text's cap band. Bullet, number and tick box all read **0.05px**. The first
tolerance was 1.5px and the mutation probe walked straight through it at -0.83px, so the bar is
0.6px, where reverting the alignment fails all three.

**Click anywhere on the page to write.** A page of paper has no edge past which a pen stops working.

The sticky note inherits all of it, and in jotting mode the slash menu offers bullets only.

Verified: 33 editor checks (was 23), full suite green (2,071), six UI self-tests pass.

## Provenance: the note remembers what it read (2026-08-23)

The deeper fix for evidence links, and the first change in this area to ship on numbers rather than
on an argument.

**The problem with matching text.** Recovering where a bullet came from means finding the line that
shares the most words with it, and shared words are exactly what makes a wrong line look right. A
meeting that says "send the pricing deck" twice offers two equally good answers.

**The fix.** The note writer reads a long meeting in windows and maps each to a digest, so it always
knew which lines it was reading; it just threw that away. Now each digest keeps its line range, each
written bullet is matched to the digest it came from, and that range is stored with the note. The
evidence link then searches those lines instead of the whole meeting.

**Measured with --linktest**, 400 synthetic bullets per condition, paired on identical cases:
easy 95.9% to 97.7% (+7, chi 5.14, p<0.05), hard 82.9% to 87.5% (+16, chi 14.06, p<0.01), and
ZERO cases made worse in either condition. Provenance is a smaller haystack, not a different guess.

For contrast, rarity weighting - the intuitively obvious fix, where a rare word should count for
more than a common one - was measured the same way and came out as noise, trending slightly the
wrong way. It is implemented, off, and kept only as the control arm of the benchmark. The intuition
was mine and the data disagreed.

Old notes are unaffected: the stored range is null on everything written before today, which reads
as "no record" and falls back to the previous behaviour.

## The clip that played the wrong half-second (2026-08-23)

The owner reported that the audio beside a to-do plays the wrong moment, and that the timeline
points are inaccurate. Three causes were possible and reasoning could not separate them, so the
first thing built was the instrument, not a fix.

**`--evidencetest [id]`** cuts every evidence clip in a stored meeting, runs it back through the
REAL recogniser, and compares what it heard with the transcript line the link claims. Read-only.
On meeting 108: 8 note lines, 8 linked, only 3 played the words they claimed, median match 37%.

**What it found, in one line of output:** `raw span 169243ms..-2147483648ms`. Some stored segments
carry an `EndMs` of `int.MinValue`. C# casts a NaN double to int as `int.MinValue`, silently, and
the recogniser occasionally returns a NaN token time. The evidence window then collapsed to its
500ms floor, so a ten-word line played half a second of itself.

Fixed at the source: token times and durations that are not real numbers are ignored rather than
propagated, the millisecond conversion is clamped instead of cast, and an end can never precede its
own start. Separately, `TranscriptMerger` took the newer segment's end unconditionally, so a short
interjection nested inside a longer turn SHRANK the merged line while its text grew; it now takes
the later of the two.

**What research says about the rest.** WhisperX exists because Whisper's timestamps are segment
level, roughly plus or minus 500ms, and it recovers word timings with a forced-alignment pass at
about plus or minus 50ms. We do not need that: Parakeet TDT through sherpa-onnx already returns
per-token timestamps AND durations, verified 1:1 with the token array on real audio. Our timing
problem was never the model. Granola keeps no audio at all, so it has no equivalent feature; Otter
keeps word timings and syncs the playhead to the transcript rather than matching summary text.

**Known and NOT yet changed:** the remaining low scores are long matched ranges (15s, 33s) where
the clip cap plays only the opening, and the link itself is bag-of-words overlap, which weights a
common word the same as a rare one. Both need a decision rather than a guess, and the owner asked
for research before implementation on this one. Also, meetings already stored keep their poisoned
timestamps; only new transcripts get the fix.

## Taste, spacing, and the letter in the circle (2026-08-23)

**Two skills, researched and written down.** `.claude/skills/symmetry-and-spacing` (the scale,
vertical rhythm, one gutter, optical centring, symmetry defects, concentric radii, and how to
assert each in code) and `.claude/skills/design-taste` (the tells of generated UI, the principles
that replace them, how to write UI copy, and an ordered audit). Both are mirrored to the Mac repo
and registered in both constitutions. Both indict decisions we shipped: the settings captions were
the "interface narrating itself" tell, and a rename chip on the user's own name was a control that
looked like a choice and was not.

**The initial in its disc, on the fourth attempt, measured.** Three plausible theories failed in a
row: an inherited paragraph line height, then a pinned one, then a Path whose layout box included
the empty space above the glyph, because BuildGeometry lays a glyph out from the TEXT origin and
Stretch.None measures from there. Translating the geometry by its own negative bounds makes the
layout box exactly the ink. `--stickytest` now MEASURES it: geometry bounds transformed into the
disc, centres compared. Reads 0.00 by 0.00; with the fix removed it reads 1.61px low. The first
version of that check measured the Path's RenderSize rather than the ink and passed on broken code,
which the mutation probe caught.

**The local speaker is not a guess.** The transcript prints the user's own name from Settings
instead of "You", and their chip is quieter than the voices Toki had to work out. Renaming the
wrong one of two identical chips is what pushed the owner's own lines back to "Speaker 1". Still
renameable, and never an oval: chips are the design's 13px rounded rectangle, guarded by a test.

**One gutter in the notch settings.** Both row labels and the page share a 16px inset. The first
attempt bled the row button outward on a negative margin and the hit-target harness failed it: a
scroll area clips what hangs past its content, so the row went dead at both edges. The run after
that passed against a STALE binary, because the build had failed on an XML comment containing a
double hyphen. Both traps are in our own lessons file.

**The window opens like the notch now.** The page switch ran AFTER the open animation started, so
the opening frames were spent building a view. Page first, one layout pass, then animate. The curve
is the notch's own ArriveStrong over Morph (280ms) instead of stock CubicEase over 230ms, and the
scale starts at 0.96 rather than 0.9, because on a 960px window a 10% jump is nearly 100px of
travel and reads as a lurch. The close keeps its character at 70% of the enter.

**Sticky notes lost the document toolbar** (headings, text size, numbered steps, template picker).
A scratchpad gets bullets.

Verified: suite green (2,067), all seven UI self-tests pass, packaged exe re-tested after publish.

## The note page stops shouting (2026-08-23)

**The transcript is a disclosure now.** It was an amber section with a three-line preview and a
"show full transcript" pill, which meant the longest and least-read thing on the page was always
half-open. It is now ONE full-width button: the section's amber icon and label on the left, the
line count and a chevron on the right, collapsed by default, expanding in place. The research bar
for a collapsed row is that it answers "what will I find if I click this?" without opening, which
the count does. Evidence links still open it on their way to a line, and the expansion is still
chunked so a thousand-paragraph meeting cannot freeze the window.

**The sticky note has an explicit Save.** Typing already autosaved on a debounce, but an autosave
is a safety net against losing text, not an answer to "is my note kept?" - the only feedback was a
status word that faded. Save and Delete now sit together in the sticky's header, and the entry
point reads "Add sticky note" when there is none.

**Settings subtext came back, small.** The previous round removed every caption; the ask was
concise, not zero. Five section titles carry one short line at 11.5px (Appearance, pinned names,
re-clean, Calendars, AI summaries). Captions inside rows stay gone - a toggle labelled "Dark theme"
explains itself.

**The notch Settings rows are one list.** The switch row and the "All other settings" row now share
the same 12px inset and 10px vertical padding with the hairline inset to match, so they read as two
rows of one list rather than two unrelated decisions.

**Speaker rename: two real defects.** The rename field was focused synchronously right after the
popup opened, and a Popup builds its child and its own window when it opens, so the focus can fail
silently and every keystroke goes to the read-only document behind it. Focus now happens on the
popup's Opened event at input priority. And renaming "You" opened on the literal word although Toki
has the user's name in Settings, so it now pre-fills with it. `--stickytest` grew five checks over
the rename popup (real button, opens, editable, pre-filled, focused).

HONEST NOTE: the mutation probe for the focus fix did NOT fail against the old code, so the harness
cannot tell the two apart and the diagnosis is unproven. The hazard is real and worth fixing; it may
not be the owner's bug. Awaiting a description of the exact symptom.

Verified: suite green (2,065), all six UI self-tests pass, packaged exe re-tested.

## Shorter notes, quieter chrome (2026-08-23)

A batch from the owner's field test.

**The mascot stopped disappearing on white.** The body faded to near-white at the feet and carried a
pale cyan crown wash, both straight from the design; against a light surface the character lost its
top and bottom edges. It is now ONE blue that only darkens (#01A4FF to #0E82D6), in both themes,
with the crown wash cut to a tenth of its alpha. The per-theme body values are gone: a character
that needs a different palette to survive a light background was drawn wrong, not themed wrong.

**The loader is only the bar now.** The pink highlight ran through a dim track, and the track's
rounded caps read as a second colour sitting either side of the bar. The track is deleted (and its
token with it); the bar runs in a bare clip.

**Settings lost its narration.** Every caption under every row, and every one-line explainer under
every section title, is gone from both the notch page and the Library page: a page that explains
each of its own controls reads as generated copy. "Toki 1.0 - all on this PC" is now "Toki 1.0", and
"all on this PC" is gone from the Library header, the notch library, the record prompt and the Ask
landing (now just "Your meeting assistant"). The Library scope label only appears when a filter is
actually on.

**An edited title updates the card you are looking at.** `MarkDirty` only set a flag for the next
open, so renaming a manual note left the old title on the list card. An open window now refreshes
its list in place (list only, never the detail pane, which is the editor the edit came from).

**Initial discs are centred by measurement.** `DiscLabel` carried a fixed half-pixel lift, which is
right at exactly one font size, so the Y/T discs in the notch sat low while the Library's looked
fine. The lift now comes from the typeface, through the same `OpticalCenter.OffsetFor` the buttons
use.

**Notes got shorter, in code rather than in the prompt.** See ARCHITECTURE; the short version is
that the prompt has always asked for concision and a model at temperature writes reported speech
anyway, so a deterministic pass now strips the frame ("Craig mentioned that"), the hedges
("basically", "I think", "in order to"), and the owner's name from an action item whose row already
prints it, then caps length at a clause boundary or leaves the line whole.

**Carry-forward stopped guessing.** "Open from previous meetings" matched on ANY one shared title
word within 45 days, so everything containing "sync" or "review" inherited everything else's
unfinished work. Generic title words are now dropped before matching, and one coincidence is not a
match when both titles have more than one distinctive word.

Verified: full suite green (2,065, up 11), `--hittest --themetest --measuretest --notetest
--stickytest --revealtest` all pass, renders checked in both themes, packaged exe re-tested after
publish. Two mutation probes verified failing against the unfixed code.

Also fixed: `TaskSchedulingTests` anchored its week on Sunday while the product anchors on Monday,
so it failed one day in seven. It surfaced because this session ran on a Sunday.

## Grain out, light in (2026-08-20)

The owner compared the notch against Figma node 293:1875 and asked why our card is ~95% opaque
where the frame says `rgba(33,30,30,0.5)`. It is the glass-vs-ghost tradeoff: the frame's 50% only
reads because Figma blurs what is behind it (plus a `#333` color-dodge layer), and blur-behind is
dead on the layered notch window. Our plate bakes that stack's RESULT instead. Answer recorded,
no change to the plate.

What did change is what sat on top of it, and it took two goes. The material's 64px tiled noise,
at 2.5%, repeated about five times across a 344px card and read as texture rather than glass.
Grain out. In its place went a soft diagonal light field, on the reasoning that a blur destroys
high spatial frequencies and keeps low ones, so grain is the one thing a blurred backdrop cannot
leave behind. The owner then saw "strands of lines that go sideways, very faintly": a broad
gradient at under 6% alpha has only a handful of quantisation steps across 344px, so it BANDS, and
the grain had been dithering those bands away all along. Removing one exposed the other.

**The card is now deliberately plain**: the plate gradient, the fixed 48px top sheen, and the two
inner edge rings. No fill of its own. Owner's call, and the right one - the material's depth comes
from the rim, the sheen and the stacked shadows, none of which band.

Verified: `--render` in both themes, `--themetest --hittest --revealtest` pass, full suite green
(2,054), and the packaged exe re-tested after publish.

## The production pass (2026-08-19 late night)

Four quality fixes plus a codebase-wide cleanup, ahead of the owner's multi-day field test.

**Smooth loader.** The notch's loader hitched while notes were being written: llama.cpp's default
takes every CPU core, which starves WPF's render thread. The LLM now leaves two cores free
(`Threads`/`BatchThreads` in `LlamaLlm`) - the UI and the model no longer compete.

**Click, never hover.** The tucked loader sliver and the "Notes ready" pill used to open on a
hover dwell, and they sit exactly in the band a cursor crosses reaching for browser tabs - so
they kept dropping open over the owner's work. Both now open ONLY on a click, the same rule the
recording peek follows. The behaviour test was rewritten to guard the new spec.

**One selectable document in the notch.** The notes-ready screen rendered each line as its own
TextBox, so a drag-select died at every line. The notch now embeds the same single FlowDocument
the Library renders (scroll-disabled, the page's ScrollViewer stays the one scroll, evidence
links scroll the host page) - a single drag now spans summary to transcript, and the old
panel-of-TextBoxes path is deleted.

**A floor with a ceiling.** The heuristic fallback now caps itself (10 questions / 10 key points /
12 to-dos, sub-4-word grunts dropped, owner/due-carrying tasks kept first) so even the worst-case
note reads as a note, not a sieve. Guarded by a probe verified failing against uncapped code.

**Attribution complete.** "Built with" and THIRD-PARTY-NOTICES gained the three models that ship
but were uncredited: Silero VAD v5 (MIT), pyannote segmentation-3.0 (MIT), 3D-Speaker CAM++
(Apache-2.0). The credit rows are now real Buttons (RowButton), per the box-is-the-button rule.

**The cleanup.** Six parallel agents stripped ~2,300 lines of session-diary comments across all
~100 source files - dated war stories reduced to their 1-3 line invariants, commented-out code
and doubled `<summary>` blocks removed, every code line byte-identical. Verified: full suite
green (2,054), and `--hittest --themetest --measuretest --notetest --stickytest --revealtest`
all pass on the built exe. Dead-code candidates were flagged for review, not deleted (list in
ARCHITECTURE).

## The note that matched no header (2026-08-19 night)

The 9 PM meeting (17 min, id 101) came out as garbage notes: the "summary" was the meeting's
opening lines verbatim, plus a 92-item dump of every question sentence. The log had the cause in
one line: `1230 chars of model output matched no header - falling back`. The model DID write the
note, but formatted every line as a bullet ("- SUMMARY: ..."), and the parser - which correctly
refuses to treat bullets as headers, so a bullet reading "Next steps" can't hijack the actions
section - matched nothing, threw the whole answer away, and shipped the heuristic floor. One
unlucky sample, zero retries, and nothing logged about WHAT the model wrote.

Three fixes in `LlmNoteWriter.FinalNote`: (1) a rescue parse - when the strict parse matches
NOTHING, lines that are bullets but exactly match a known header get un-bulleted and parsed
again (when strictness has already yielded nothing, there is nothing left for it to protect);
(2) the final pass now retries once on a parse miss, with a hard format reminder appended - at
sampling temperature the second answer is a genuinely different one; (3) a parse failure now logs
an excerpt of the raw output, so the next one is diagnosable instead of a mystery character
count. Verified with two MUTATION PROBES (both confirmed failing against the unfixed code), the
full suite (2053 green), and `--renotes 101` against the real meeting on the real model.

## The ghost frame, and Toki learns to fly (2026-08-19 afternoon)

The corner flicker survived the previous round because its deepest cause was not ours: it was
WINDOWS replaying a stale picture. `ConcealUp` called `Hide()` the instant the animation CLOCK
finished - one frame before the fully-shut clip was actually composed - and a layered window
retains its last composed frame, so the OS kept a ~13px card stub and presented it on the next
`Show()` until the reveal's first real frame arrived. That stub is the corner the owner kept
seeing, in both themes, because the surface replays whatever it held. The fix pins the leaving
elements, waits two real `CompositionTarget.Rendering` frames (a dispatcher-priority hop was
filmed STILL flashing - priorities order UI work, not the render thread's presentation), and only
then hides. A second, smaller cause fell in the same films: the expand transition painted the new
surface and THEN stalled ~100ms measuring the next view, showing a bare card with neither state's
content; transitions now measure everything before mutating a single pixel. `--revealtest` grew a
"the hide composed an empty frame" check so a retained-surface stub can never ship silently again.

And the notes-ready hand-off became the owner's requested character beat: when the 2-second
"Notes ready" announce goes unclicked, Toki's bubble dissolves under him, he hops out, does one
mid-air somersault while shrinking 44 to 28px, and flies 560ms up to exactly where the peeking
Toki rests - the pill concealing beneath him, the landing done as a silent in-place bare-ify plus
cross-fade (the normal collapse morph was filmed resurrecting the white pill behind him).
Clicking the landed Toki opens the notes.

**Verified:** 2,051 tests green (7 new), `--revealtest --hittest --measuretest --themetest` on
Debug and on the republished `dist\Toki.exe`, and contact-sheet films of the summon (empty edge,
then only the mascot's own drop) and the full flight (hop, somersault, arc, clean landing).

## The flicker on film, and the loader learns to tuck (2026-08-19 late night)

The owner filmed the notch and the film did what three rounds of reasoning could not. The white
flicker before the mascot appears turned out to be the meeting prompt's white pill and close puck
rendering ALONE over the desktop for a few frames - no plate, no Toki - because
`Collapsed -> MeetingPrompt` was not on the hover-pair list (so it took the instant-snap path
instead of the bloom morph), and because a rogue `GrowToContent` raced every transition
(AppState notifies the view-refresh handlers before the window, so a refresh-triggered morph ran
first and left the real transition a no-op). The peek now blooms into EVERY target, and grows are
gated on a shown-state stamp.

The "loader never minimises" complaint exposed a real regression: the squircle settle rebuilt the
resting clip from the arranged box, and for a tucked 13px sliver that box is the ~50px bubble
floor - the settle silently UN-CLIPPED every tucked sliver back to a full card, and Toki plus his
"Tidying up.." say-bubble live outside the card clip anyway, so they stood on screen through the
whole transcription. Both fixed (clip clamp; mascot hidden while tucked). The flow is now the
owner's spec: transcribing = a bare shimmering line at the top edge; a deliberate 360ms dwell
(not a graze) pops it down; it re-tucks 540ms after the cursor leaves, leave-driven. When notes
are ready, Toki pops up beside a "Notes ready" pill for 2 seconds (one constant,
`NotesReadyPeekMs`) - click opens the note, ignore it and everything folds back to the quiet
peek with the note one summon away.

Also: outline glyphs (the recording bar's chevron) now centre sub-pixel - at 125% an 11px glyph
in a 26px puck left 9.375 device pixels a side and layout rounding split them 9/10, the leftward
lean. And the Y/T speaker discs were being sabotaged by WPF inheritance: `Paragraph.LineHeight`
(19.5px) reaches the TextBlock inside the 16px avatar and inflates its line box, pinning the
capital to the disc's top - a standalone probe reproduced the owner's screenshot exactly. A
shared `PillParts.DiscLabel` (line height pinned to font size, optical lift, sub-pixel) now
centres both avatar implementations.

**Verified:** 2,044 tests green (9 new, fail-first proven), self-tests on Debug and the
republished `dist\Toki.exe`, and frame captures of every fixed behaviour: the bloom with no white
frame, the sliver that ignores a graze and tucks when left, the 2s announce and its fold to the
peek. New dev seam: `--state a,b` walks a state path for filming transitions.

## The details pass: cut-off buttons, the real Apple red, and five behaviour bugs (2026-08-19)

The owner came back from using the glass build with a screenshot roll: action buttons clipped or
off-centre, a "lipstick red" stop button, a notch corner glitching before the mascot shows, the
transcribing loader floating mid-screen and refusing to minimise, the peeking Toki's eyes cut off,
and note-list accent bars in random colours. Two audit agents (one visual with fresh renders, one
read-only on the code) produced root causes; two build waves fixed all thirteen; a post-fix audit
re-verified each on the final tree.

**The button fixes:**

- The recording bar's chevron puck is 26px but sat in a 24px row - WPF layout-clipped its bottom
  2px flat and cropped the shadow rings. The row got the 2px back (pill still exactly 50px), and
  `--hittest` now FAILS on any clickable control that layout is slicing, so this class of bug
  cannot ship again.
- The stop button painted a frozen `#FF114D` (and the theme's `StopRed #FF2D55` - Apple's PINK -
  had zero consumers). `StopRed` is now Apple's system red, `#FF453A` dark / `#FF3B30` light,
  consumed as a live theme resource, and every crimson-pink constant is gone.
- The evidence play/stop glyphs were FONT characters ("▶", "■") whose baseline and side bearings
  drift with DPI - that was the off-centre triangle. Both are now authored Path geometry with
  optical centring, and the chip wears the house glass grammar (rim + fading hover) instead of an
  instant brush swap. The chat-history "✕" got the same font-to-path cure; the Ask send glyph got
  a 1px render-transform nudge (a margin nudge measures as a no-op - layout re-centres it).
- Small glyphs opt out of layout rounding (`PillParts.SubPixel`), so a 10px square in a 24px disc
  centres sub-pixel at 125% scaling instead of snapping up-left.
- Library note cards: the left accent bar was coloured by `meeting id % 6` - six colours at
  random. It is now coded by KIND: blue for recorded meetings, purple for manual notes.

**The behaviour fixes:**

- The mascot glitch was two bugs. Collapsing to a peek left the bubble's dark disc visible for
  240ms beside the already-arrived peek Toki, then removed it in ONE frame - the "corner popping
  off". The disc now fades out over 180ms. And the FIRST expand stalled 50-150ms building views
  and rasterising the mascot cache, eating the head of the animation - the first-open views and
  the mascot bitmap are now warmed at launch, off-screen, at idle priority.
- The transcribing loader is now flush with the top edge like recording (it wore the default 12px
  hover gap), and its auto-tuck timer re-arms when the cursor is over it instead of silently dying
  - the "it doesn't really minimise" bug - and tucks on the 2s beat instead of 9s.
- The peeking Toki's eyes were measurably 3px above the card's clip. It is smaller now (27px) and
  sits lower - eyes fully visible with air above. The flip lost its two-bounce jitter: one soft
  520ms turn with a single gentle overshoot, ~540ms total, readable fun that stays out of the
  workflow's way.

**Verified:** 2,035 tests green (20 new, every one watched to fail against the unfixed code);
live burst-capture A/B for the mascot pop (dissolves over 4 frames instead of vanishing in 1),
the eyes, the flush loader and the 2s tuck; full self-test battery on the republished
`dist\Toki.exe`. Not verifiable without a real meeting: the warm flip-to-peek on a genuine
recording start - the owner's eyes are the gate there.

## The notch became Apple glass (2026-08-18)

The owner asked for the glassiness of Apple's Control Center on the notch, and smooth borders on
every state, page, button and Toki's bubble, nothing excluded. Research first (two agents), then
three build waves.

**The research verdict:** true blur-behind is not achievable for this window. The supported Windows
11 backdrop (`DWMWA_SYSTEMBACKDROP_TYPE`) cannot combine with a per-pixel-alpha layered window, and
the undocumented acrylic path blurs the window's full rectangle - including the mascot gutter and
shadow pads - which is exactly why the existing `ApplyBlur` interop was already permanently off.
Apple's own materials run their tint plate at 82-95% opacity, so past the blur the look is LAYERING:
a gradient plate, grain, a lit rim, inner edge light, stacked shadows. That is buildable, and it is
what got built.

**Built, wave by wave:**

- **The material system** (`Theme/CardMaterial.cs`, a `Gradients` table in `ThemeManager`): the card
  is now a ~95% opaque warm plate, slightly lighter at the top, under a radial top sheen, a
  fixed-seed noise grain (2.5% dark / 2% light - the single biggest "material, not paint" trick), a
  2px inner top highlight, a 2px inner bottom shade, and a 1-device-pixel rim that is a GRADIENT:
  brightest at the crown, fading down the sides (the cheap form of Liquid Glass's specular edge). In
  light mode the rim ends on a dark bottom hairline so a white card separates from white apps. Two
  shadow casters (key + ambient) replace the single one; both still hide during morphs. The Glassy
  notch setting now only chooses ~95% vs 100% plate opacity; both modes get the full stack.
- **Glass on glass**: capsules, pills, pucks and popups carry the inner step (`GlassFill` +
  `GlassRim`) plus real soft ring shadows - the MeetingPrompt pills had a comment PROMISING a shadow
  and none existed. Hover across every shared button style is now a fading brighten (130ms in, 90ms
  out) as a sibling of the content, so labels never dim and translucent fills no longer grey out.
  The switch got its first hover state ever; ticked checkboxes got theirs back.
- **The Library and every popup** (sidebar, note card, search, sort, prompt capsule, menus, pickers,
  confirm dialog) wear the same material via a shared `GlassSurface`/`GlassCard` helper; the Ask
  bloom is themed for light mode instead of hardcoded dark teal; two captured-brush theme bugs died
  on the way.
- **Corner geometry** (`Theme/Squircle.cs`): the card's resting clip and rim are now a true
  G2-continuous squircle (Figma 60% smoothing parameterisation), swapped in only at rest so the morph
  machinery is untouched. Honest finding, measured by pixel diff: at radius 24 the difference from a
  circular arc peaks at ~0.35px and is invisible at 1:1 - the mechanism is right, the visible wins
  came from the rim and DPI work. Every hairline on a rounded edge in the notch and views now lands
  on whole device pixels at any DPI scale (125%/150% included), which is where the "rough borders"
  actually came from.

**Verified:** 2,015 tests green, `--themetest` both themes, `--revealtest` 9/9, `--hittest` 290
controls, `--measuretest`, `--notetest`, `--stickytest`, `--uitest` in budget, `--fpstest` at
baseline (the two extra fills and casters cost nothing measurable; a true A/B with the squircle
forced off showed no frame cost), renders in both themes read back and described per wave.
`dist\Toki.exe` republished from the final tree with the battery run ON the packaged exe. The
owner's eyes remain the final gate.

**Flagged for the owner, not changed:** `ChipButton` and `SquareButton` are dead styles with zero
consumers; `TokiCheckOpen` still promises an amber outline it does not draw; a handful of code-built
rows (calendar rows, About credits, the AI-mode segment) are still Borders with mouse handlers and
instant hovers; the SpeakerChips rename popup still wears the old look.

## One waveform, and it actually hears you (2026-08-18)

The owner's verdict on the two waveforms: they differ between Ask Toki and recording and "it needs to
be uniform", the recording bar is "just so damn ugly", the chevrons-up-down button is "sooooo ugly",
and our waveform is "one of the worst i've seen".

Two decorative waveforms existed and **neither one heard anything**. `LiveWave` on the recording bar
ran a travelling sine on a 1500ms clock; Ask ran seven pink bars on staggered per-bar animations.
Both looped at the same amplitude whether the room was talking or the mic was unplugged. In a product
whose whole story is not inventing what it heard, that is the same lie in miniature.

**Built:**

- **`Controls/VoiceWave`** - THE waveform, one control at both sites. 3px bars, 3px gaps, full caps,
  centre-weighted envelope (a cosine dome with a fixed deterministic jitter, normalised so no bar can
  grow out of its row). Only `BarCount` and `Height` differ per site: 14 bars / 20px on the recording
  bar, 9 bars / 22px in Ask. One 30Hz `DispatcherTimer` for the whole row, ScaleY only, nothing
  allocated per tick, stops dead when hidden or parked.
- **`Audio/LevelMeter`** - the honest source. Every capture buffer reports its RMS (a sum of squares
  on the buffer already in hand, no allocation). Two static meters: `Recording`, which the mic AND
  the per-process loopback both feed, so the bar shows max(you, them) and the other person talking
  moves it; and `Voice`, which only push-to-talk feeds. It self-decays, so a stopped capture settles
  the row to resting dots with no teardown wiring to forget.
- **`LevelScale`** - the level-to-height mapping: `sqrt(clamp((rms - noise) / (ref - noise), 0, 1))`,
  where the noise floor tracks the 10th percentile of ~3s of history and the reference tracks the
  95th. Both ends move because a fixed pair cannot serve both mics that exist: the owner's mic
  records ~38dB below the meeting stream, so a floor set for a normal mic renders that one
  permanently flat, and a floor set for that one makes room tone dance on a normal mic. Silence and
  steady room tone both read 0, which is the point.
- **The recording bar** - the chevrons-up-down glyph on a transparent disc became **one** chevron on
  a real glass puck (`PillParts.GlassPuck`, `GlassFill` + `GlassRim`), which rotates 180 degrees over
  200ms on the in-place curve (`Motion.Move`, new). The frame draws the stacked chevrons; the owner
  overruled that detail and the owner outranks the frame. The timer went **amber** (one meaning one
  colour: amber is time everywhere else in Toki).

**Verified:** 2,015 unit tests green (22 new, each verified to FAIL against a deliberately broken
version - 12 mutation probes, all killed). New **`--wavetest`** self-test flag drives the REAL control
on the REAL clock with a synthetic level and prints the bar heights at each phase; `--hittest`,
`--measuretest`, `--revealtest`, `--themetest` all pass, `--fpstest` unchanged across two runs.
`--render` PNGs in both themes show the resting-dots state at both sites (no audio flows in the
harness, so resting is the honest picture there).

**Found by the new self-test, not by reasoning:** the first decay constant left the row visibly above
its dots 1.5 seconds after the room went quiet, and the jitter plus wobble peaked bars at 1.21x the
control's height, growing them silently over whatever sat underneath.

## The tests were put on trial (2026-08-18)

The owner suspected the ~1,991-test headline contained filler written to hit a number. Two-pronged
audit: a static review of every test file, and MUTATION PROBES - deliberately breaking product
code to see whether anything notices. The verdict, honestly:

**The suite is largely real, but the headline is inflated ~35%.** Honest count of independent
behavioral assertions: ~1,300. The gap is one self-documented mechanism: two generated theories
(RobustnessPerturbationTests, 749 cases; WakeNameGoldenTests, 88) multiply seed utterances by
casing variants - and since every router entry point is case-insensitive (QueryUnderstanding
lowercases first), the UPPER/lower/Capitalize variants re-enter the identical code path. Real
property, padded count. Nothing looked like tests invented purely to hit a mark.

**Found and fixed:** one genuinely tautological assert (BehaviorGoldenTests - a haystack that was
always "x" or "", so it could never fail; it had been passing on its second assert for months),
three cannot-fail cases (Enum.IsDefined on a switch result - now assert the routing property that
matters), one exact cross-file duplicate (removed).

**The mutation probes were the sharp edge.** Breaking small-talk routing: 40 tests failed - that
coverage is real. Two ROUTING-PRECEDENCE mutations survived, but honestly: overlap probes proved
both swaps were behaviorally inert (the canned predicates are engineered disjoint), so the suite
was RIGHT to stay green - the BrainFixture precedence-copy risk stays theoretical for now. But two
probes survived that should not have, both against fixes from THIS morning: deleting the
recording-clock null-guard (the fix that stops meetings being deleted as accidental taps) and
breaking RenameSpeaker's to-do-owner update left all 1,991 green. Both now have tests, and both
tests were verified to KILL their mutation before being trusted. 1,993 green, product code
byte-identical to the shipped build.

The meta-lesson joins the constitution's older ones: a fix without a test that dies when the fix
is removed is coverage theatre - and the freshest code is where the holes are.

## Toki can measure itself now, and transcripts read like a product (2026-08-18)

The owner asked for production-grade quality with transcription and summary quality as priority
one. Rated against the market first (scorecard in the session log): the two highest-impact gaps
were "we have no accuracy number at all" and "verbatim transcripts read amateur next to Otter's".
Both closed:

1. **`--wertest <wav> <ref.txt>` - the accuracy scoreboard.** Decodes through the REAL pipeline
   (gate, levelling, readability) and prints WER with the substitution/deletion/insertion split,
   decode speed, and a per-name recall table - names get their own score because they are where
   meetings care most and recognisers are weakest. `docs/wer-eval.md` carries a two-minute
   scripted reading (names, dates, times, percentages, a spoken year, a handover) and its exact
   reference; the owner can produce a comparable number on any machine in five minutes.
   Verified end to end by synthesizing the script with Windows TTS: 58s of audio, 6.5x real-time,
   WER 9.2% - and the name table immediately caught Abhishek/Sita/Deepika all missed (heard as
   "Ubishek"/"Deepaka") while Priya and Craig scored clean. The instrument found a bug on its
   FIRST run: the name corrector's first-sound gate demanded identical opening phonemes, so a
   pinned "Abhishek" could never repair "Ubishek" (A vs U). Opening vowels are one sound class
   now; consonants stay strict (Kirtana still never becomes Craig).
2. **The readability pass (`Readability.cs`)** - deterministic, no model, runs LAST in the repair
   chain on both the live path and Re-clean. Spoken dates/times/years/percentages become digits
   ("your last working date is uh july twenty second" - a real saved line - now reads "your last
   working date is July 22nd"); uh/um-family fillers are dropped (Mm-hmm stays - it is an
   answer); stuttered function-word doubles collapse ("even if if", another real line). Counts
   and small ordinals stay words ("two questions", "the third option"), "may" the verb never
   becomes May the month, "twenty first-time buyers" is left alone, and a line that was pure
   hesitation is kept rather than emptied. Number conversions are recorded as "tidy" fixes so
   the transcript can show its working on hover; removals are not (nothing to hover, nothing
   was added).

1,991 tests green (28 new). Multilingual/Hinglish (the remaining big accuracy gap - Parakeet is
English-only and the owner's meetings mix Hindi) needs a model-choice decision and is parked for
the owner: the honest option is offering multilingual Whisper as a Settings choice.

## Five critics walked the whole pipeline (2026-08-18)

The owner asked for a system-wide adversarial audit of the processing engine. Five independent
review agents each took one layer - audio capture/live ASR, transcript post-processing, the note
writer, diarisation/evidence, storage/orchestration - with orders to report only provable failure
scenarios. They returned ~45 findings; each was verified against the code before anything was
touched, and 24 confirmed defects were fixed (1,963 tests green, 14 new). The worst, by layer:

- **Stop-flow**: any mid-meeting detour through Ask or the prep card RESET the recording clock, so
  a 40-minute meeting whose user tapped push-to-talk near the end measured 3 seconds and was
  deleted by the accidental-tap rule - audio and all. The clock now belongs to the session, not
  the state machine. Also: a deleted meeting could be resurrected by the recovery sweep when its
  folder delete failed (tombstone marker now), the sweep could double-save a meeting recorded
  while it ran (claim-by-rename + fresh DB check now), and everything the user did during the
  up-to-300s LLM wait - ticking a to-do, retitling - was silently overwritten (their edits win now).
- **Note writer**: a map-phase timeout was indistinguishable from "the meeting had no substance",
  so a real meeting could be stamped with the canned nothing-happened note; the owner validator
  checked names against the model's own digests (a hallucinated name validates itself - it now
  checks the transcript, as a phrase, so "Craig Portal" can't be welded from two real words); the
  "1:1" template gate passed every meeting ever (two labels is not two people); truncated final
  action lines shipped half-parsed; meetings past ~10 windows overflowed the reduce pass.
- **Transcript**: the echo de-dup could delete a thirty-word real sentence because a four-word
  backchannel shared its words (deletion direction was backwards); "X. Sorry, X" across a sentence
  boundary was eaten as a self-repair; Tim/Tom-style short names merged into whichever the library
  saw more (vowels must now agree, and an established spelling is a person the corpus vote cannot
  erase); the re-clean button let one bad meeting outvote the library AND erased the corrections
  audit trail (corpus snapshotted once minus the meeting's own counts; corrections now append).
- **Diarisation**: the attendee-count prior FORCED splits - five invitees with one speaker
  shredded that voice into four "speakers" (the count may extend merging, never prevent it); the
  roster held "Priya Devi" while the room says "Priya", so roster trust never fired on a real
  calendar invite (first names indexed now); renaming a speaker left their to-dos owned by
  "Speaker 2"; clicking a second evidence clip killed its own playback.
- **Audio**: a failed live decode window was deleted as if transcribed (lost seconds are now
  counted, and past 8s the whole-file pass runs instead); the startup recovery sweep could hold
  the shared recognizer for minutes while a live meeting's buffers overflowed (deferred while
  recording, plus round-robin window decode); an Extensible-float device format was encoded as
  int32 under a float header after a device change (full-scale noise); a silent live window's
  gain hit x20 and re-armed the hallucination path (absolute noise floor now).

Deferred with reasons in `docs/backlog.md`: live window seams still cut at quiet points rather
than VAD boundaries, device-change gaps don't inject silence, the handover-names-the-backchannel
case, evidence's 45s cap keeping the head of a long range, and a live Library refresh when the
real title lands.

## The notes stopped inventing things (2026-08-18)

The owner asked what was wrong with the two newest recordings. Four real defects, all in the
note-quality pipeline, all fixed and covered by 13 new tests (1,949 green total):

1. **The name corrector was eating ordinary English words.** "wait for a day or two" saved as
   "wait for a Toki or two"; "I didn't get you" as "I didn't Craig you"; a real person named David
   became "Toki". Three stacked causes: flat distance budgets (5 edits allowed against a 3-letter
   word), a case-insensitive rewrite (one stray capitalised "Day" put every lowercase "day" at
   risk), and a too-short common-words list. Now: distance budgets scale with word length (three
   explicit branches in `SoundAlike`, each carrying its real example), the rewrite only touches
   capitalised tokens (sentence-initial only with mid-sentence evidence, or when correcting toward
   a trusted name - the "Abashik, you can..." vocative case), and ~180 everyday words joined the
   never-a-name list. Every regression test case is a rewrite from the real 2026-08-17 logs.
2. **The speech gate never ran on real meetings.** The Silero VAD gate built 2026-08-14 lived only
   on the stop-time whole-file path - and every real meeting uses the LIVE path, which fed raw 15s
   windows to Parakeet ungated and unlevelled. `TranscribeSlice` now levels and gates each live
   window with a persistent Silero session; the stop path also no longer falls through to an
   ungated whole-file decode when the gate finds zero speech (that fall-through was the exact
   hallucination case the gate exists to stop).
3. **"Still open from last time" attached strangers' to-dos.** It ran before the LLM title existed,
   so it matched on the placeholder (the meeting's first words), and its title match was substring,
   so "just" matched "AdJUSTing" and "even" matched "EVENt". Now: the real title is computed first,
   titles share ground on whole words only, and "last time" is capped at 45 days (a leftover seed
   meeting with 2023 due dates had been handing out homework for weeks).
4. **A prompt instruction leaked into a to-do.** Meeting 93 rendered a task due "(when no time
   stated)" - the model echoed the prompt's own drop-the-field instruction, and `ParseActions`
   stored whatever followed `due:`. Both fields are now validated in code: a due date must look
   like a time (digit, day, month, or relative word; instruction echoes rejected), and an owner
   must be "You" or a name that appears word-for-word in the transcript. A prompt is a request;
   the validator is the enforcement.

Also checked and NOT a bug: the loopback stream's 99% peak. A sample-by-sample scan of the newest
`them.wav` found zero saturated samples (max 0.985) - it is loud, not clipping.

Verified: full `dotnet test` (1,949), plus the failure-shaped tests written from the actual logged
corruption. The real verification is the owner's next meeting - the transcript should stop
containing words nobody said.

## Toki was sitting on his own pills (2026-08-15)

Your three screenshots were one bug. While the notch changes size it holds the window at the bigger of
the two sizes and centres the card in it, so Toki is nudged right to stay beside the card's moving
edge. **That nudge was never undone.** When the notch settled, the card went back to filling its
space and Toki stayed parked where the nudge left him - on top of the pill, over its own text.

It only happened when the notch got SMALLER, which is why it looked like three separate bugs: folding
to "Notes ready", to the recording bar, and to the loader sliver each left him further across.

**A second one underneath it:** the window is only as tall as the card, but Toki's bubble is a fixed
58px pinned to the window's top - so any short card cut the bottom off the circle. The loader's thin
sliver made it obvious: a 13px card left you two thirds of his head. The window now never gets shorter
than he is.

I photographed the real notch state by state afterwards to check, rather than assuming: Summarizing
and Notes both show a full circle with Toki beside the pill.

## One thing I could not answer

The frame test came back much worse than its old baseline, which would mean the new Toki costs real
smoothness. So I cached the two parts that deserve it - his body (three complex shapes) and the pen -
since both only ever MOVE, they never change shape. That is the textbook case for it, and I checked he
is still sharp.

**Then the measurement stopped making sense.** Three runs of the *same build* gave 10.5, 18.4 and 15.4.
The variation between runs is bigger than the thing I was trying to measure, so right now that test
cannot tell me whether there is a problem. Your PC is syncing this folder to OneDrive and has been
compiling for hours.

I would rather tell you that than pick whichever number looked best. It is written down as its own job
with the next three suspects named, to be measured when the machine is quiet.

**Verified:** 1,923 tests green; --hittest, --revealtest, --themetest, --measuretest, --stickytest all
clean.

## Replay setup starts at step one again (2026-08-15)

The notch keeps one copy of each screen and reuses it. Onboarding remembers which of its three steps
is showing in nothing but which one is visible - so the first time you ran it, it walked to "You're
all set" and *stayed there*, and every later replay handed you back that same finished page.

It rewinds on entry now, and re-reads your name from Settings on the way in, since seeing and
correcting it is half the point of replaying.

The general version, written into the notes: **a reused screen that holds a multi-step flow has to be
rewound when you arrive at it.** Reusing screens is right; leaving your position inside one lying
around is not.

**One thing I had to fix to test it.** Adding the check made three existing checks fail on a build
that was perfectly correct - they were sampling the opening animation a moment after it started, and
the extra work in the test was enough that on a busy machine it had already finished. The notch now
records those values at the instant it appears, and the test reads that recording. A test that fails
because the machine is busy is worse than no test.

**Verified:** 1,923 tests green; --hittest, --revealtest (now 9 checks), --themetest, --measuretest,
--stickytest all clean. Confirmed by putting the bug back: it reports "showing step 2".

## Toki on white (2026-08-14)

Your screenshot was right and the reason is slightly annoying: **Toki is drawn for a dark notch.** The
design fades his feet to near-white and washes his crown pale, which looks lovely on dark glass and
means that on the light theme's white bubble the top and bottom of him stop existing.

I measured before changing anything, and our colours were faithful to the design - it really is that
pale at the ends. It gets away with it on Figma's white artboard; it does not get away with it sitting
in a white puck in a real product.

So three colours now have a light-theme value: his feet go a proper blue instead of near-white, the
wash over his crown drops from 86% to 35%, and the sparkles go from pale glitter-gold to a deeper
amber that you can actually see. **The saturated middle of him is untouched** - same character, he
just keeps his edges on a pale background.

**Four more from your list:**

- **The bubble is a full circle again** during recording. It is 58px and a recording pill is 48, so
  centring it pushed the top of the circle above the window's own edge, where it was cut flat.
- **Toki writes while recording and holds the note while transcribing** - I had those backwards.
- **"Connect a calendar" glides** to the calendar rows now instead of jumping, a beat after the page
  lands.
- **The app icon is the new Toki**, at all seven sizes, so Task Manager and the taskbar match. It is
  built from the light-theme colours on purpose - the dark ones vanish on a light taskbar. Windows
  caches pinned icons, so a pinned shortcut may need unpinning and repinning once.

**Verified:** 1,923 tests green; --hittest, --revealtest, --themetest, --measuretest, --stickytest
all clean, and the exe's embedded icon extracted and checked.

## The smile is a bend, not a swap (2026-08-14)

You caught me breaking the rule we set. The happy face was hiding the eyes and showing a *different*
shape - which is exactly the swappable-accessories thing the Grok lesson exists to stop. You said it
plainly: "remember the dynamic lesson from grok bot, use that."

**The eyes are one shape now.** Each eye is a single outline of thirteen points. Open, they form an
exact oval. Happy, the same thirteen points sit in a caret - the top rises to a peak, the sides swing
out into arms, the bottom lifts. Toki's eye BENDS into the smile and unbends out of it. Nothing
appears and nothing is hidden.

That also fixed "it can only have ^ ^": the leftover ovals were the eyes' fake drop shadows, a second
copy that hiding the eye did not hide. Both copies are gone - one shape, one outline.

**And Toki is centred in his bubble** - the board reserved space for his props only on the right, so
he sat slightly left.

## Home and Settings

- **The Library is a proper button again**, bottom right, with the icon *and* the word, on the same
  fill every other secondary action uses - so its shade and size aren't a one-off any more.
- **"Connect a calendar"** - four words, not a sentence - and it now opens the **Library's** Settings
  scrolled to the calendar rows. You were right that it was pointing at a page that had no way to
  connect anything.
- **The empty space under the notch's Settings is gone.** That page was pinned to a fixed height from
  back when it held everything; it measures its content now.

**Verified:** 1,923 tests green; --hittest, --revealtest, --themetest, --measuretest, --stickytest
all clean.

**Still open:** the notch font (task #16 - I think it's because a transparent window can't use
ClearType, but I want to prove that before changing type across the app), and the mascot's outer edge
is still slightly crisper than the design's blur.

## Toki is the real Toki now (2026-08-14)

You were right about the shape, and it could not have been fixed by nudging what was there: the old
Toki was four overlapping circles, which can only ever make a fat semicircle. So I pulled the actual
vector out of the Figma file instead.

| | before | now |
|---|---|---|
| body | 4 circles, too tall | the design's real path: crown bump, a lobe each side, **two feet** |
| colour | pale on top, dark at the bottom | **blue on top fading to near-white at the bottom** - the design's own, and it was upside down |
| eyes | wide and far apart | tall, narrow, close together - the design's exact size and spacing |
| happy | squashed pills | **^ ^ arcs** |
| reading | **did not exist** | **the note in hand** - a little card with two lines, exactly as you asked |
| writing | a chunky hand-drawn barrel | the design's own slim navy fountain pen |

Two of those came from measuring rather than looking: I sampled the reference image to get the pale
glow at the crown right, and found the pen was being rotated 26 degrees off its resting pose, which
had been throwing it clean off the body.

**Toki now reads while transcribing and writes while writing.** Those were the wrong way round -
recording showed a pen, summarising showed a thinking bubble - and the note-in-hand character existed
in the design and not in the app at all.

## Home says what it does

- **"Start a meeting" is now "Calendar"** with a calendar icon. It always opened the calendar page.
- **The whole bottom row is gone** - "+ Google", "Library >", "Connect a calendar". You were right
  that "+ Google" was wrong on its own terms: we support Outlook too, and naming one provider on the
  home screen is a bug, not a shortcut.
- **UPCOMING sits under the two buttons**, and with nothing connected it shows one quiet line:
  "Connect to Google or Outlook calendar in Settings".
- **The Library is an icon** beside Settings now, where a redundant calendar icon used to be.

## The notch's Settings

"Appearance" is gone, and "Glassy notch" and "All other settings" now start at the same place - the
first was a plain row at inset 0, the second a button whose style pads 12, which is why one sat
further forward than the other.

**Verified:** 1,923 tests green; --hittest, --revealtest, --themetest, --measuretest, --stickytest
all clean.

## Frame 34 (2026-08-14)

Your recording settled it. Pulled apart at 30 frames a second:

```
frame 33   the peek
frame 34   THE WHOLE SETTINGS PANEL - full size, fully solid
frame 35   blank
frame 36+  the panel fading in properly
```

One frame of the finished panel, then nothing, then the real opening. That is the flicker.

**What caused it:** the new screen was being put into the notch while the notch was already on your
display, at full brightness, and only hidden a few instructions later - after the window had been
resized. Resizing a window lets Windows repaint it, and that repaint caught the screen in the state
it was in: finished, full size, nothing hiding it yet.

Three changes:

1. The content is hidden **before** it is put in, not after the resize. There is no longer any moment
   where a finished screen exists on your display without having been faded in.
2. The mask that shapes the card goes on **before** the window grows, so the worst thing a mid-resize
   repaint can catch is the small rectangle the card is about to grow out of.
3. **Toki now leaves with the card.** Your second screenshot showed him fully formed beside a
   shrinking stub of a panel: the mask that hides the card never covered him, so through the whole
   close he just hung there. He fades out with it now.

**Measured after**, on the real app with the capture synchronised to the app's own log: the panel
comes up over seven frames as a smooth grow-and-fade, and there is no full-size frame anywhere.

**What I got wrong for three rounds:** I kept reasoning from the code about what the frames must
contain. The recording answered it in one pass. When a visual bug survives a fix, get the frames
before touching the code again.

## The pop, actually found this time (2026-08-14)

You were right that it was still there, and right that there was history. Both of my earlier fixes
were real bugs; neither was this one.

So I stopped reasoning about it and photographed it: 90 screenshots of the top of your screen while
the app woke up, laid out as a contact sheet. The frames showed the card appearing at **full size and
completely empty in a single frame**, then the content fading in, then Toki arriving. No growth at
all - so it was never a stale image or a late animation.

One line of logging then said exactly why:

```
morph  52x42 -> 344x211   (150ms)
morph 344x211 -> 344x211  (300ms)     <- 19 milliseconds later
```

**Two openings were running for one hover, 19 milliseconds apart.** The first is the real one - the
card growing from the little peek to the full panel. The second is a duplicate that starts from the
first one's *destination*, so it has nothing to animate - and starting it cancels the real growth
that was underway. The card gets 19ms of its glide and then snaps open.

The underlying mistake: the notch recorded its new size at the *start* of an opening rather than at
the end, so anything that interrupted an opening thought the card was already there. It now reads the
card's true current size, so an interrupted opening carries on from where it got to.

And that is exactly why it was the first time only: two openings collide only when the screen is
being built for the first time (it grows as it lays out). Every time after, the screen is cached,
only one opening runs, and it looks fine.

Measured after the fix: both morphs read `52x42 -> 344x211`, and the frames show the card growing
over five or six frames instead of appearing in one.

**Both log lines stay in.** This took three attempts and a 90-frame burst to find, and it would have
been one line of log the first time.

**Verified:** 1,923 tests green; --hittest, --revealtest, --themetest, --measuretest, --stickytest,
--fpstest all clean.

## The flash was the notch's LAST frame, not its first (2026-08-14)

You said the fix had not worked, and gave the detail that solved it: it is the FIRST hover only, and
every hover after that is fine.

**Closing the notch was leaving a picture of an open notch behind.** The close animation finished by
un-clipping the card back to full size and then hiding the window - so the image Windows keeps for
that window was a fully open notch with Toki beside it. On the next open, Windows shows you that old
picture until the first new frame is ready.

And that is exactly why only the first time: on the first open, the screen you are opening is being
built for the first time, so the gap is long enough to see. After that it is built already, the new
frame arrives in a fraction of a frame, and the old picture is never on screen long enough to notice.

Now the notch hides first and is put back to its closed state afterwards, so the last thing it ever
draws is nothing.

**A second bug fell out of it:** Toki's entrance was only ever playing ONCE per launch. Nothing ever
put him away, so after the first open he just appeared with the card instead of springing in. Your
"every time after the first has no issues" was half right - no flash, but also no animation.

**Proven, not claimed.** I put the old line back and the check reports all three faults, including
Toki at full size and full opacity on the second open. Then I put the fix back.

**The bubble is dark in dark theme now.** It was a near-white disc - on a dark desktop with a dark
notch it was the brightest thing on screen. It is the same warm dark glass as the card now, one step
lighter so it still reads as its own object beside it. Checked in both themes.

**And no icon in the Library's title bar** - just "Toki" beside the minimise/maximise/close buttons.
Screenshotted the real title bar to confirm. One thing I could not check: your taskbar is
auto-hidden, so I could not photograph the taskbar icon. It should be untouched (that icon comes from
the exe, not from the window), but please glance at it.

**Verified:** 1,923 tests green; --hittest, --revealtest, --themetest, --measuretest, --stickytest
all clean.

## The flicker on summon, and a sweep for its whole family (2026-08-14)

You reported that opening Toki from the top edge shows the bubble Toki for a moment before the real
one renders, and that it had happened before. Found, fixed, and guarded.

**What it was.** Toki springs in 90 milliseconds after the card so the two read as separate objects.
But a delayed animation does not hold its starting value while it waits - until its clock starts, it
shows the element's normal value, and Toki's normal value is his FINISHED state. So every summon
painted a full-size Toki next to a card that had not opened yet, then snapped him small and sprang
him back in. The delay was showing you the ending first.

**Two more of the same.** Every delayed animation in the app was checked. The thinking dots and the
sparkles had milder versions of it (visible at full size for a moment, then snapping small). Fixed.
Everything else was already correct.

**And a new check, `--revealtest`,** because nothing could ever have caught this: it is not a hit
target, not a colour, not a measured size, and it cannot be screenshotted - the overlay is invisible
to screen capture and the whole thing is over in a tenth of a second. It drives the real notch window
from hidden into a panel and reads the values at the moment it appears. With the fix removed it
reports exactly what you saw; with the fix in, it passes.

**The wider sweep you asked for, from the app's own click log:**

- **Three more clickable things that were not buttons** - the "Latest" jump pill in both Ask views,
  and the "Built with - open source and licences" row in Settings. Pill-shaped, hand cursor, no press
  feedback. That is the fifth, sixth and seventh time this pattern has shipped.
- **A whole screen was outside the button audit.** The log had caught a real dead-hover on the
  calendar composer's Create button, and the audit could never have found it because the composer is
  a mode rather than a page. It and the month picker are covered now: 259 controls became 311.

## Your two screenshots: one Toki per surface

- **The hover box behind the Ask page's Toki is gone.** That was mine from an hour earlier - making
  him a real button (he had no press feedback at all) brought a hover fill with it. There is a new
  bare style now: he still presses, he just has no box. Drawing a grey rectangle behind a soft
  character puts a box around a shape that is deliberately not a box.
- **The sidebar mascot is gone entirely** - the panel just says "Toki" now. There were two Tokis in
  that window; a masthead identifies the app, it is not a control.
- **No icon in the title bar** beside the minimise/maximise/close buttons - just "Toki". The taskbar
  and alt-tab icon is deliberately untouched.

**Verified:** 1,923 tests green; --hittest (311 controls), --revealtest, --themetest, --measuretest,
--stickytest and --uitest all clean.

## Toki watches you (2026-08-14)

You said Toki only blinks and never looks to the side. The reason was a number: the little
autonomous motions fired **once every 30-60 seconds**, and only one in four was a glance. So a
sideways look happened about once every three minutes, which for the notch mascot - on screen for a
few seconds at a time - means never.

There are four layers of life now, from constant to rare:

- **Toki follows your pointer.** Whenever your cursor is anywhere near, its eyes track it. This is
  the one that does most of the work: a character that tracks you is paying attention, one that does
  not is a drawing.
- **When nobody is near, the eyes dart on their own** every second or two, sometimes back to centre,
  sometimes with a blink - the way real eyes actually move.
- **The body is never perfectly still** - a slow float and lean on 4-6 second cycles, small enough
  that you only notice when it is switched off.
- **The bigger motions** (look around, nod, wobble, perk up) now come every 9-20 seconds instead of
  every 30-60.

Its eyes are composed rather than owned: a mood points them somewhere (writing = down on the page),
and your cursor pulls them from there - so a writing Toki glances up at you and then returns to the
page, not to dead centre.

**The two moments you named:**

- **Sparkles in first-run setup** now turn as well as pulse, each on its own rhythm, and there is a
  fourth on the opposite side so they surround Toki instead of sitting in a neat arc above it.
- **Writing while transcribing** was a metronome - a perfectly even 260ms tick, which the eye reads
  as mechanical no matter how smooth it is. It is now a 2.2 second phrase: a few quick strokes, a
  pause where the hand lifts between words, a few more, then a rest. The body moves on the same
  phrase, which is what makes pen and body read as one thing.

**Your Ask-page line is fixed.** That tinted band at the bottom of the page was a plain rectangle, so
it had a hard vertical edge beside the sidebar and a square corner against the window's round one. It
now fades out at both sides. Only the curved corner, as you asked.

Also: the big Toki on the Ask page was a clickable box that was not a button, so poking it gave you
no press feedback at all - the "haptics" half of your note. It is a real button now.

**Cost of all this:** 0.25 CPU-seconds per 20 idle seconds, and the notch still drops 1.2 frames per
transition, unchanged.

**Verified:** 1,923 tests green; --hittest, --themetest, --measuretest, --stickytest, --uitest,
--notetest and --fpstest all clean.

## Toki starts acting instead of holding props (2026-08-14)

Toki used to change mood by making a pen appear and a bubble vanish - nothing moved between the two.
Now the props grow out of the body and shrink back into it, and, more importantly, **the body acts
too**: Toki leans and sways when it is thinking, dips in time with its own pen when it is writing,
bounces when it is pleased, and in every mood its **eyes look at whatever it is doing**. Before this
the silhouette was identical in all four moods and the prop was doing all the acting.

The mascot bubble's drop shadow is also gone - not cosmetically, but because it sat inside the
subtree that animates, so every frame of every entrance was being re-rendered in software. That was
the diagnosed cause of the mascot "rendering hard". Same look, none of the cost.

And the notch's glass finally has its top-edge highlight. The element for it has existed since the
redesign, painted transparent, doing nothing.

**The test for all this had never worked.** --render-anim renders a view twice, 550ms apart, so the
pair proves an animation moves. Its two files were byte-identical every run since the day it was
written: with no window attached, WPF's animation clock never ticks, so both frames captured the same
un-animated values. The one check that existed to confirm an animation runs could not have detected
one that did not. It has a real off-screen host window now, and the pair shows what it claims.

**Verified:** 1,923 tests green; --hittest, --themetest, --measuretest and --stickytest clean.

**Not yet pictured:** the notch's actual chrome. --render wraps every view in a stand-in pill, so the
glass, the rim, the new highlight and the mascot bubble have never appeared in a PNG - which is
exactly the layer you keep saying looks wrong. Next up.

## Levelling the quiet stream, and the order that matters (2026-08-14)

The second half: lift a quiet stream to the level Parakeet expects. Two mistakes in the first
version, both caught by measuring.

**The true peak vetoed the gain.** Capping the lift so the loudest sample cannot clip sounds
obviously right and is not - one cough held a thirteen-minute recording to **x1.7** when its speech
needed ten times that. The ceiling is now the 99.9th percentile, and the same file gets **x9.4**.

**The gate was running on un-levelled audio.** Gain was applied after the regions were picked, so
Silero was handed the very signal it was least able to read, on exactly the stream this exists for.
Levelling happens first now. Speech found on the quiet mic went from **17% to 46%**.

| | gain chosen | speech found | words |
|---|---|---|---|
| the quiet mic | **x9.4** | 17% -> 46% | 73 |
| the good stream | **x1.0** - left alone | 72% | 1595 (was 1596) |

The level is the 95th percentile of frame energy, not a mean: a mean over a track that is three
quarters silence reports a level nobody ever spoke at.

**One thing this cannot fix.** Your microphone is running about 38dB below the meeting audio, and a
mic that quiet is barely capturing you. Post-processing cannot invent what was never recorded - the
real fix is the input level on that device. Worth checking your Windows sound settings.

**Verified:** 1,923 tests green; `--hittest` and `--themetest` clean.

## The speech gate: stop transcribing silence (2026-08-14)

Built on the diagnosis below. Toki now decodes only where somebody is actually speaking, using
Silero VAD through the sherpa-onnx runtime it already ships - so no second inference runtime, and
the 629KB model is fetched at launch on the background warm thread, never mid-meeting. If it is
missing or fails, the old path runs unchanged.

**On the quiet stream that was producing nonsense:**

| | before | after |
|---|---|---|
| decode | 159s (5.0x real-time) | **30s (26.8x real-time)** |
| worst line | "Vacuum kinna bital pin button up okay I will laugh okay okay" | "Okay I will laugh." / "Okay, okay." |

**The first tuning was wrong and the A/B caught it.** With Silero's defaults the gate looked good on
the broken stream and quietly damaged the good one - a word-level diff showed **187 words of real
speech thrown away**, all of them short: "yep", "go ahead craig", "craig you're not audible". The
two errors are not symmetric - a false negative is a word you said and will never see, a false
positive is a second of quiet the recogniser ignores anyway - so the gate was retuned to err toward
keeping audio:

| the good stream | ungated | gated (v1) | gated (retuned) |
|---|---|---|---|
| words | 1580 | 1549 (**-31**) | 1596 (**+16**) |
| audio decoded | 100% | 70% | 72% |

This also replaces the old 3-minute windowing, which cut at "the least loud frame near here" - still
mid-word in continuous speech. Cuts now land on the model's own speech boundaries.

No ground-truth transcript exists, so none of this is a WER claim; what is measured is word
retention against the previous behaviour, decode time, and specific hallucinations disappearing.
`--asrtest` now writes the full transcript to `--out` so any future change here can be diffed, and
`TOKI_ASR_NOGATE=1` forces the old path for comparison.

**Verified:** 1,923 tests green.

## Why the transcripts are bad: half of every one is invented (2026-08-14)

Talat runs the same Parakeet and the same Qwen3-4B and gets cleaner results, so the gap is ours. It
is, and it is not subtle. Measured on today's real recording, a 13-minute two-stream meeting:

| | you.wav (mic) | them.wav (meeting) |
|---|---|---|
| median RMS | 0.0008 | 0.0635 |
| speech-ish seconds | 3% | 66% |
| near-silent seconds | 73% | 32% |
| **segments from ~13 min** | **10** | **242** |
| sample | "Oh my god, singy cocky and the beef in the table." | "Yeah, I pinged you in the team space itself." |

Same model, same code, same settings, same meeting. **The mic track is about eighty times quieter
than the meeting track and three quarters silence, and we hand it to the model at the same gain with
no voice-activity gate.** Parakeet's features are log-mel; fed that, it does not return nothing, it
returns confident nonsense. The meeting side, which is well levelled, produces a genuinely good
transcript from the very same pipeline.

That also explains the LLM half of the complaint. The note writer, diarisation, echo de-dup, evidence
links and the Ask index all sit downstream of a transcript that is half fabricated - no prompt change
survives that input. Lesson 1 of this project, in the wild: garbage in, garbage out.

**The fix needs no new dependency.** The sherpa-onnx build already shipping here exports `SileroVad`
and friends, so a real VAD is one ~2MB model file away with no second ONNX runtime. Gate with the
VAD first, then normalise per-stream gain - not the reverse, since normalising a 73%-silent track
just amplifies its noise floor. Written up in ARCHITECTURE and tracked as task 13, along with four
further levers (we hand-chunk every meeting over 6 minutes, no window overlap, greedy decoding, int8
models) which are plausible and, unlike this one, still unproven.

**Also captured, not dropped:** the notch and mascot revamp the owner asked for (tasks 11 and 12),
including the one genuinely actionable principle from the references - the Grok Bot icon morphs one
shape between states rather than swapping props, which is exactly what Toki's mascot does not do.

## The invisible button, and what the sweep found next (2026-08-14)

"Back to Toki" measured **1.02:1** against the sidebar card in both themes - invisible, not dim. My
fault: when the sidebar became a glass card I gave the button `GlassCapsule`, and the Glass tokens
are tuned to sit on the NOTCH; over the sidebar's own fill they land within a rounding error of it.
New opaque `SidebarButton` token, which steps clearly both ways.

The text sweep could never have caught it - the label measured 10:1. So a button-FILL check was
written, and then **deleted**: on the real app it reported seventeen cases of which one was the bug,
because the design has buttons defined by a rim rather than a fill, and buttons on gradients where
"the surface behind" cannot be resolved by walking the tree. Real faults and correct controls both
sat between 1.01 and 1.15:1. A gate that cries wolf is worse than no gate. The reasoning is left in
the code where the check would go.

What did survive is **the Library window joining `--themetest`**, which was worth more than the
check that prompted it. It first reported sixteen labels that were merely mid-fade (effective
opacity is part of the test now), and then one real fault: the list row's date number is the only
label in the row not going through a Style, so it captured its brush and stayed dark-theme white on
a light-theme card. Third occurrence of that class this session.

**Verified:** 1,923 tests green; `--themetest`, `--hittest`, `--uitest`, `--measuretest`,
`--copytest` and `--notetest` all clean.

## The Library window, slice two: the reading pane (2026-08-14)

The frame does not draw sections as cards, which is what the screenshot suggests at a glance - it
draws the coloured circle badge and label (which already existed), then a **3px vertical bar in the
same accent at 12%** down the left of that section's content. The bar is what makes a section read
as a group; without it a coloured heading is the only thing holding its bullets together.

Built as ONE FlowDocument `Section` per group rather than a border on each paragraph, because a
border per block leaves a gap at every paragraph margin and comes out as a dotted line. The
transcript rides the same rail, so its expand/collapse insertions now write to the Section.

`--copytest` is what made this safe: restructuring a read-only FlowDocument can break selection
across the new containers, which no screenshot would show. It still captures summary, bullet, to-do
and transcript in one select-all.

**Verified:** 1,923 tests green; `--hittest`, `--uitest`, `--notetest`, `--copytest`, `--themetest`,
`--measuretest` and `--stickytest` all clean.

## The Library window, slice one (2026-08-14)

The 1440px frames for the desktop window had never been built. This slice does the shell.

- **The sidebar is a floating card**, 240 wide, inset, radius 18, glass with a rim and a shadow -
  not a flush rail. The seam between it and the main pane is gone with it.
- **The nav rows were Borders with mouse handlers**, which breaks the standing "a clickable thing is
  a Button" rule - and survived because `--hittest` only audits ButtonBase, so the app's most-used
  control was breaking it where the test could not see. Real Buttons now, at the frame's sizes.
- **Every list row is a filled capsule** (radius 16), not just the selected one, so the list reads
  as a list. Type went to the frame's 16/12/12 from 13/9.5/10.5.
- **The search field got a magnifier and a placeholder** and became the frame's 44-tall glass pill;
  the sort control became a real 44x44 button.
- **The header is one line**: "Library  56 | all on this PC".

**Verified:** 1,923 tests green; `--hittest` (254 controls), `--uitest`, `--themetest` and
`--measuretest` clean; rendered and checked in both themes.

## The calendar composer, rebuilt (2026-08-14)

Four faults reported, all with the same shape: a layout decision never checked against the panel it
lives in.

- **Two hairlines under the header** - the header drew one and the composer's top border drew
  another 12px below it. Now one.
- **Cancel and Create fell off the bottom.** The composer was docked ABOVE the agenda and shared the
  panel's fixed 430px with it. Making a meeting is a MODE now: it takes the whole panel, the agenda
  hides, the header reads "New meeting", the fields scroll, and the buttons are pinned to the bottom.
- **Six boxes at four different heights** - a filled 38, a filled 36, an outlined chip, another
  filled box, a bare switch. They are all the same kind of thing and now look like it: one card of
  46px rows split by hairlines, label left, value right, the same shape as the Library settings page.
- **Two competing sentences under the title** - a permanent hint plus a separate parse preview. The
  hint is the empty state now; the preview takes the line once it exists.

The half-width field card took two wrong fixes to diagnose: the composer was a bare child of the
root DockPanel with no `Dock` set, which silently means `Dock=Left`, so it got only its desired
width. It shares the filling slot with the agenda now.

Adding the composer to `--themetest` failed it within a minute: the parse preview captured its brush
(`FindResource("Green")`), so a preview rendered under one theme kept that colour into the other -
light-theme green on the dark card at 1.98:1. Same bug as yesterday's pill labels, in much older
code, found by a test written an hour earlier that had never seen this view.

**Verified:** 1,923 tests green; `--themetest`, `--hittest`, `--measuretest`, `--notetest`,
`--stickytest` and `--uitest` all clean; composer rendered and checked in both themes.

## Text you can actually read, in both themes (2026-08-14)

Owner: "fix the text colors, it kinda blends in with the background, both for white and black
themes". Measured rather than eyeballed - every text token composited over every surface and scored
with the WCAG contrast ratio. Three faults, and the one that mattered was not a text colour at all.

- **The card's opacity was the real cause.** At the frame's 50%, the card is half whatever is behind
  it, so over a bright window the warm dark glass became mid-grey and white-90% text fell to
  **2.96:1**. That is why both themes were reported: each fails against the opposite wallpaper. The
  card is now 85% - still visibly glass, but text holds 8.7:1 over any desktop.
- **The light theme's accents had never been checked**: AccentAmber 2.42:1, AccentTeal 2.74:1,
  TextDim 2.94:1, TextMid 3.83:1.
- **Coral, Pink and Purple had no light value at all**, documented as "constant brushes that don't
  invert". Pink measured **1.66:1** on a light chip: invisible, not dim.

Secondary text went 50% -> 65%, tertiary 25% -> 45%, and every accent was re-derived by walking its
HLS lightness until it cleared 4.5:1 on the glass card over black, grey and white, hue and
saturation preserved so "one meaning, one colour" is untouched. New `CoralText` token: white on the
dark theme's coral measured 2.73:1, so a filled coral button gets a near-black label in dark and a
white one in light.

**Enforced from now on.** `ContrastTests` reads the palette out of `ThemeManager.cs` and fails the
build if any text colour drops under 4.5:1 on any surface in either theme (3:1 for decoration),
including against the translucent card over three wallpapers. It also asserts the parse found the
palette, because a check that reads nothing passes perfectly while measuring nothing.

**Then the light theme still had white labels** on "Start a meeting", "Take notes" and "Record this
meeting". The palette was right; those pills are built in code and had CAPTURED the brush
(`(Brush)Resources["TextHi"]`) instead of referencing it. A theme switch replaces the resource, so a
captured brush is a snapshot that is correct when the view is built and wrong forever after. Fixed
with `SetResourceReference` via a new `PillParts.Themed()` helper, plus `LiveWave`, `NotesReadyView`
and the live-transcript rows.

`--themetest` had passed through all of this, because it checked one Border that somebody had wired
up correctly - the mechanism, not the product. It now walks every notch view and every label in it,
in both themes, and fails on anything under 3:1 against what is painted behind it. The order is the
whole trick: the views are built ONCE in dark and the theme is switched under them, because building
a fresh view per theme passes even with the bug present. Validated by putting the bug back on
purpose - it named all three labels at 1.26:1.

**Verified:** 1,923 tests green (7 new); `--themetest`, `--hittest`, `--measuretest`, `--notetest`
and `--stickytest` clean; `--render` in both themes checked by eye.

## The morph rework, and the glitch before the drop (2026-08-14)

Two owner reports: "why are we struggling to render clean smooth designs on the notch", and a
"little glitch that happens and then toki pops down" when summoning from the top edge.

**Measured before touching anything.** New `--fpstest` flag: the same animated mascot in three
windows, frames counted. On the dev PC (120Hz, render tier 2) transparency costs ~1% and the
mascot's vector art ~2% - so `AllowsTransparency` was not the villain, and caching the mascot would
have bought nothing and made it look worse at 44px. What does cost is animating the WINDOW's own
bounds: 56-76 dropped frames every four seconds, worst frames 39-48ms against an 8.3ms budget,
because each frame is a Win32 move plus a layered-surface reallocation on the UI thread, mid-layout.

**The morph no longer animates the window.** `AnimateSize` sets it once to the union of where it is
and where it is going, holds it there, and animates a rounded clip on the card instead - centred
horizontally, top-anchored, with Toki's bubble tracking the clip's left edge on the same curve. The
content is laid out once at the union size and never re-laid-out. One further resize settles onto
the target after the motion ends. **Two window resizes per transition instead of about 120.** The
reveal-from-hidden path already worked this way; the morph and the in-place path now match it.

**The glitch was a one-frame sideways jump.** Setting WPF's `Width` only marks layout dirty, so
calling `PlaceTopCenter` straight afterwards centres the window for its OLD width and corrects a
frame later. `SizeWindowNow` / `MoveTopCenterFor` compute the position from the TARGET size instead,
needing neither a readback nor a layout pass. Two other versions were tried and measured first: one
reintroduced the jump, the other fixed it with a synchronous `UpdateLayout` that cost about four
extra dropped frames per transition. Both are documented in the code so they do not get re-invented.

**Also fixed:** Toki's bubble was a hardcoded pale disc and vanished into a light-theme notch; it is
now a themed pair (`MascotBubble` / `MascotBubbleRim`).

**An honest correction.** An earlier claim in this log of a "36-54% improvement" from the rework is
WITHDRAWN. It came from an interleaved A/B that a later standalone measurement of the same build
contradicted by 3x. The live-notch number is dominated by the per-transition VIEW REBUILD, not the
morph, so it works as a regression alarm and not as a benchmark. The architectural argument stands
on its own: 120 window resizes per transition became two. See ARCHITECTURE.md for the full account,
including the three hypotheses for the disagreement that were tested and all turned out wrong.

**Next real lever:** the view swap. Every state change clears `Host` and lays out a fresh page.

**Verified:** 1,915 tests green; `--hittest`, `--measuretest`, `--notetest`, `--stickytest` and
`--uitest` all clean.

## The five menu-bar pill states, from the Figma CSS export (2026-08-14)

The owner supplied 22 screenshots plus `css code for all the design layers.txt` (moved 2026-09-03
to `docs/design/figma-layer-css.txt`, see `docs/design/README.md`) - the raw Figma export of the
whole "Redesign by SKz" board, 46,687 lines covering **23 screens**: the notch
(dashboard, three onboarding steps, live transcription, notes, ask, settings, meetings list), six
menu-bar states, and the full 1440px Library window. This entry builds the six menu-bar frames; the
rest is tracked as the open front.

**Built, to the frame's own numbers:**

- **`PillParts`** - the shared vocabulary all six frames are made of: the 40px glass action pill, the
  24px round puck, and the lucide icon set (video, notepad-text, chevrons-up-down, chevron-right, x)
  drawn as **paths**, not near-miss Segoe glyphs. Two new theme styles, `GlassPill` and `RoundPuck`,
  carry radius 24 / the white-10% rim / the frame's `0 4px 8px` shadow and the standard press-compress.
- **`LiveWave`** - the 14-bar waveform: 2.4px wide, 2px gaps, radius 99, white 90%, the frame's exact
  resting heights (5,9,14,7,11,16,6,12,15,8,5,10,13,6 = 59.6px). Staggered ScaleY only, stops dead
  when hidden.
- **Record this meeting** (`MeetingPromptView`) - one 169x40 pill replacing the old three-button
  prompt, with the frame's hover-revealed 20px close puck instead of a "Not now" button. A calendar
  match adds a second "Join call" pill. The app Toki detected moved into the tooltip.
- **The recording bar** (`RecordingOpenView`) - timer, waveform, the frame's `#FF114D` stop disc and
  a chevrons-up-down that opens the **"Live Transcription (Expanded)"** frame in place: live lines
  with YOU/THEM capsules or a named speaker's tinted initial, and the frame's top fade (an
  OpacityMask, so nothing forces a software render). The peek before it is unchanged - the owner
  asked to keep the tiny Toki and have the bar appear when you open it.
- **Tidying up your notes** (`SummarizingView`) - the 220x50 pill, still tucking to its 13px sliver.
- **Notes ready** (`NotesReadyView`) - new. A finished note used to tuck into a blank 13px strip,
  which looked identical to the strip that means "busy"; it now tucks into the frame's
  "Notes ready >" pill. Not a PillState - the state stays `Notes`, so the note, its scroll position
  and its Keep-or-Discard choice survive the tuck.
- **The idle pair** - Home's two actions rebuilt from the same `PillParts.ActionPill`, so the pills
  on Home and the standalone frame can never drift apart.

**Three supporting changes.** `IWidthAware` lets one view own its card width (the recording bar is
two frames wide, 272 and 344). `DrawsOwnCapsules` makes the window stop drawing its glass card for
states whose view draws free-floating pills - one card cannot express two capsules with air between
them. And `--render` now honours both, because it was drawing a 288px card behind a 169px pill: a
picture of a layout the app does not have.

**Deliberately not built: the pause button.** The frame draws one on the recording bar. Nothing in
the audio layer can pause a capture, and a control wired to nothing is worse than no control.

**Verified:** 1,915 tests green; `--hittest` (261 clickable controls), `--measuretest`, `--notetest`
(23), `--stickytest` (4) and `--uitest` all clean; `--render` PNGs checked by eye for all five
states plus the expanded transcript, which got its own shot (`RecordingOpen-expanded`) because the
state loop could not reach it and a state nothing renders is a state nobody looks at.

**One trap for next time:** `src/Toki/bin/Debug/` holds **two** output folders - a live
`net8.0-windows10.0.19041.0/` and a stale `net8.0-windows/` left over from an older target
framework, last written 15 July. Picking the first `Toki.exe` a recursive search returns runs the
July binary, which hangs on startup and looks exactly like a regression in whatever you just wrote.

## One Toki, clean air, flush summon (2026-08-14)

The owner's check found four things, all fixed and republished (23:49):

- **ONE mascot per surface, everywhere.** Panels show the floating bubble Toki; the in-view blobs
  that doubled him are hidden (Ask/push-to-talk - the reported "two Tokis", RecordingOpen,
  Summarizing, Onboarding) and the Calendar's two empty-state blobs became a quiet calendar glyph.
  Blobs are hidden not deleted - the poke/animation wiring stays, and a hidden TokiBlob pauses
  itself.
- **Clean spacing**: the bubble no longer overlaps the card by 4px; CardPadL 56 -> 68 gives a
  crisp 10px air gap (58px bubble + 10 + card).
- **The transcribing mood reaches the bar**: the recording BAR is a peek (floating Toki hidden),
  so its own tiny Toki now carries the Writing mood - the pen scribbles while a meeting is being
  taken down, matching the open panel.
- **The edge-summon peek is flush with the top again** (TopGap 0 for Collapsed) - the hover-summon
  drops out of the screen edge; a 12px gap there read "too low", not "hovering".

1,915 tests green; hittest/uitest/measuretest clean.

## Claude Design + the Ask/Settings redesign lands in the app (2026-08-14, late)

Two halves. FIRST, at the owner's ask, Toki's design system went to claude.ai/design: a hand-authored
React mirror (`design-web/` - 15 components, the exact theme hexes, press-feel, mascot, notch shell)
was converted, render-verified (15/15) and uploaded to the "Toki Design System" project, with a
design-language guide distilled from the Figma file so the design agent knows the rules. The mirror's
cloud then got de-weirded: each SVG lobe had its OWN gradient (per-lobe light = visible seams); one
userSpaceOnUse gradient across the fused shape restored the smooth WPF texture.

SECOND, the owner picked the direction (the frames, not the agent's button-fixated takes) and the ASK
and SETTINGS pages were rebuilt IN THE APP from the Figma frames (348:5438, 353:9154):

- **Ask**: a teal-blue ambient bloom rises from the bottom (the one page with atmosphere - it is a
  conversation, not a document); the hero glow went blue with the mascot; the FOUR suggestion chips
  are back as real Buttons (the frame draws them; the old veto was about the oval look); the prompt
  bar is a full capsule (r26) ending in a round send disc; History gets its glyph and New Chat is
  the white pill.
- **Settings**: one centred 640px column titled "Settings · Toki 1.0"; the name field spans the
  column; APPEARANCE moved up under the name as the frame's CARD of rows - one rounded surface,
  hairline-split rows (Dark theme / Glassy notch / Show Toki in screen recordings), switch on the
  right. The frame's Recording/Permissions sections are NOT shipped - no auto-record mode or audio
  retention exists yet, and a control wired to nothing is worse than no control.
- Gotcha recorded: WPF has no `VerticalAlignment="Baseline"` - it parses to a runtime XAML crash
  that took --uitest down; siblings baseline-align by Bottom + margin.

1,915 tests green; hittest (253 controls) and uitest (0.2ms/43ms/137ms) clean; published to
`dist\Toki.exe` (23:32) and running. --libshot hung once against the live dist instance - killed,
noted, harnesses covered the rest.

## Round four: the motion-and-feel skill, and the research that fixed the "hard" rendering (2026-08-14)

The owner's verdict on round three: the animation was poorly designed, no haptic feel, Toki too low
and too small to notice, the rendering "very hard and ugly". Per their ask, this round started with
RESEARCH (Apple HIG springs, Material 3 motion tokens, IBM Carbon, Emil Kowalski's standards,
Duolingo's mascot practice, and the WPF rendering docs), the findings were written into a project
skill - `.claude/skills/motion-and-feel/SKILL.md` - and then the skill was applied:

- **The jank had a NAME.** WPF renders any `DropShadowEffect` subtree in SOFTWARE, re-rendered on
  every animated frame. TokiBlob carried THREE effects (body + both eyes) inside a subtree that
  breathes and blinks constantly - the mascot was software-rendered ~60x a second, which is exactly
  the "hard and ugly". All three are gone: the body's lift is now a cheap gradient ellipse, the
  eyes sit on a static darker pill offset 1.5px (the blink transform moved UP to the grid so the
  seat closes with the eye - a seat that stayed open would flash dark mid-blink). Same story on the
  notch: the shadow caster now HIDES during every size morph and reveal and fades back on settle,
  instead of software-re-rendering its blur on every frame of the animation.
- **The researched curves replaced the stock eases.** `Theme/Motion.cs` gained a real cubic-bezier
  easing (`BezierEase`, browser-style Newton solve) and three named curves: `ArriveStrong`
  (0.23,1,0.32,1 - entering content), `Depart` (0.3,0,0.8,0.15 - exits), and `Drawer`
  (0.32,0.72,0,1 - the iOS-sheet/Dynamic-Island settle, now THE curve for the notch morph and
  reveal). Gotcha worth recording: `EasingFunctionBase` defaults to `EasingMode.EaseOut`, which
  MIRRORS a curve that already encodes its whole shape - the class pins `EaseIn` (raw passthrough).
- **Haptic feel, centrally.** Every shared button style (PillButton, ChipButton, RowButton,
  SquareButton, IconButton) now compresses on press (0.97; rows 0.985; icons 0.94) in 90ms and
  springs back in 200ms with a whisper of overshoot - the whole box, from the template, never
  per-site.
- **Toki is bigger, higher, and placed PER STATE.** The bubble grew 44 -> 58px (blob 44x40), and
  `PlaceMascot` now glides him (composition translate, Drawer curve) to where each Figma frame
  draws him: vertically centred on a compact pill, bottom-left overlapping the corner on the
  expanded recording panel, level with the header on tall panels.
- **He's also more alive**: breathing retuned to the researched calm (3.5s cycle at <=2% amplitude,
  was 3s at 4.5%), blinks are asymmetric (close 60ms, open 190ms decelerating), and a fidget layer
  fires every 30-60s while idle - a sideways glance (new Gaze transform), a nod, a wobble, or a
  double-blink - never during a working mood.
- **Recording sits AT the top.** `TopGap` is state-aware: 2px for the recording bar/panel (a status
  strip, not a floating panel), 12px for everything else.
- **The Library reading pane got the frame's minute details** (348:2933): title 26 Bold; a meta row
  with "N open" (amber) and "N people" status capsules; action items with the redesign's CIRCULAR
  teal checks, real strikethrough on done, owner/due as capsule chips BELOW the text; transcript
  lines with derived initial-avatars (StableHash colours, full name on hover - the name run stays,
  because two people can share an initial); and the toolbar is ONE rounded icon group - copy (menu +
  green-check flash) and a red trash - instead of two text pills.

1,915 tests green; uitest 0.5ms/51ms/156ms, notetest 23, hittest, stickytest 4, measuretest all
clean. NOT yet in `dist\` - the owner's Toki was running from dist (a live lock), so the publish
waits for them to quit it. Still open in #93/#94: the compact pill states, transcribing live-lines,
Settings segmented controls, Ask window suggestion chips (now allowed), and section collapse
chevrons (deferred - FlowDocument blocks have no Visibility; needs a rebuild-on-toggle).

## Revamp round three: the duplicates, the bubble, the moods, the Library window (2026-08-14)

The owner's meticulous pass on round two, all taken:

- **Toki no longer appears twice.** The Home card's greeting-row blob is gone (a hidden host keeps
  the pop/blink wiring); the ONE Toki is the floating one beside the card.
- **Toki sits in his circle** - the round translucent bubble every frame draws him in (42px, soft
  white glass, gentle shadow), not a bare cloud on the wallpaper.
- **The mood system is real**: `TokiBlob.SetMood` - THINKING (a dots bubble at his shoulder, dots
  pulsing in sequence), WRITING (a pen scribbling at his side), HAPPY (^^ squinted eyes + pulsing
  sparkles, blink timer suspended so a scheduled blink can't pop the smile open). Wired to the notch:
  thinking while notes are written, writing while recording, happy when notes land.
- **The Library WINDOW is on the redesign** (frame 349:6743): the sidebar's logo row (small blue
  cloud + "Toki" wordmark) over icon nav; list rows with the DATE OUTSIDE the card (16 Black over
  8 Bold), a per-meeting accent bar inside, meta, snippet, chips; and the reading pane's colored
  section headers - a round tinted icon chip + label per meaning, with the design's mapping
  (decisions green, actions teal, open questions pink, transcript amber). Selection paints the inner
  card only, so the date column never highlights; `_cardFills` carries that and is cleared per load.

1,915 tests green, hit/measure/uitest all clean - the Library list stays under its 250ms budget with
the new rows (48ms). Remaining, specced in #93/#94: the compact recording/prompt/ready pills, the
transcribing live-lines view, Settings segmented controls, and the Ask window.

## The revamp goes structural: the notch HOVERS (2026-08-13, late night)

The owner's correction on slice one was that the redesign is not a restyle - the fundamentals
changed. The biggest: **the panel no longer fuses to the top edge. It hovers.** Landed:

- **The floating card.** 12px below the top edge, rounded on all four corners (r24), a soft shadow
  under it - cast by a separate near-invisible border, never an Effect on the card itself, which
  would push every glyph through an intermediate surface and cost ClearType.
- **Toki floats beside it.** The mascot is his own element in a zone left of the card (the frames
  put him at the panel's top-left, outside it), hidden during the peek, which carries its own Toki.
- **One conversion point for the new geometry.** The window is bigger than the card (mascot zone +
  shadow room); every size in PillWindow speaks CARD dimensions and WinW/WinH/CardW/CardH are the
  only place pads are applied - so the pads can never be added twice, and the card (not the window)
  sits on the screen's centreline.
- **Window-level acrylic retired.** The DWM accent fills the rectangular window, which would frost
  the empty pads around a floating card. The glass reading now comes from the card's own
  semi-transparent tint; the "Glassy notch" setting picks the more transparent tint.

1,915 tests green, all four harnesses clean. **The moving parts (reveal, tuck, hover) were verified
by harnesses only - the hovering window needs the owner's eyes on the real machine.** The remaining
states and animations are specced frame-by-frame in task #93.

## The design revamp begins: Figma "Redesign by SKz", slice one (2026-08-13, night)

The owner supplied the full redesign as a Figma file (node 288:4) - 16 notch states, the live panel,
and Ask/Library/Settings at 1440x900 - and asked for the revamp against it. Design properties were
pulled straight from the file (get_design_context), not eyeballed. **1,915 tests green, `--hittest`
and `--measuretest` clean.** This is SLICE ONE; the remaining states are specced in tasks #93/#94.

**The design system, measured from the file:** warm dark GLASS - rgba(33,30,30,.5) tint over a frost,
1px white-10% rim, corner radius 24, inner capsules rgba(33,30,30,.4) r12; text as WHITE OPACITIES
(90/50/25), never grey hexes; SF Pro Rounded -> Segoe UI Variable; per-event accent lines #F5A05C /
#32C7D3; a white Join pill with dark text; #FF2D55 for "Stop & summarize". All landed as theme tokens
(Glass/GlassCapsule/GlassBorder/TextHi/TextMid/TextLow/AccentAmber/AccentTeal/JoinPill/StopRed) with
contrast-checked light-theme counterparts.

**Landed in this slice:**
- **The notch is the glass sheet**: radius 24, glass tint + rim in both the acrylic and opaque paths
  (the old pitch-black slab and the untinted blur are both gone).
- **The mascot is the blue cloud** - body gradient EAF6FF->3D9BEA, WHITE eyes (the redesign inverts
  them), blue blush and glow. App icon regenerated from it (7 sizes).
- **Home is the Default frame**: greeting 14/TextHi with calendar+settings icons right, COMING UP as
  the content, twin glass action buttons ("Start a meeting" / "Take Notes"), the conditional connect
  row and account footer beneath.
- **COMING UP rows are the design's capsules**: bare date column (16 Black over 8 Bold) outside a
  r12 glass capsule with the 3px accent line, 12-Semibold title, 10 TextLow time, countdown, and the
  white Join pill on imminent meetings (which now actually opens the link).

**Still to build** (fully specced with screenshots in the session scratchpad + tasks):
prompt/recording/ready compact pills, transcribing-expanded with avatar-chip live lines, the
Meetings/Meeting-Notes panels with colored collapsible sections, Settings segmented controls, the
mascot's expression states, and the three full-window redesigns.

## Round two of owner testing: the sticky, the clips, the home card (2026-08-13, evening)

Research-first this time, as asked - one agent pass over Otter, Fathom, Fireflies, Grain, Descript's
edit-point patent, Granola, Fantastical, Krisp and Loom, plus the WPF nesting literature - then built.
**1,915 tests green, `--stickytest` 4/4 (with a new structural check that the OLD build fails),
`--notetest` 23/23, `--hittest` and `--measuretest` clean.** Full session notes:
[`docs/notes-2026-08-13.md`](docs/notes-2026-08-13.md).

- **The sticky note works.** It was nested inside the read-only note document, whose text editor
  starves any nested editor of keyboard, caret and selection at class level - three separately
  documented mechanisms, which is why point-fixes (focus-after-layout) kept "passing" while the owner
  could not type a letter. The note page now SPLITS at the sticky: notes document, real editor,
  transcript document - three siblings under one scroll. The trade (no drag-selection THROUGH the
  sticky) is honest: selection across it only ever worked while typing in it did not.
- **Clips play the moment, not the word "That".** Evidence scored a 3-line window but returned only
  its first line - which could carry zero matched words. `BestRange` returns the span, trimmed at
  both ends to lines that hold actual evidence. Padding went from 1500ms of somebody else's sentence
  to 300ms lead / 700ms tail, with the cut SNAPPED into the silence before the same stream's next
  line (Descript's rule; transducer ASR end-times run early, so the tail matters more than the lead).
- **Play buttons are now guaranteed-present**: same stream, monotonic timestamps, a 1.5s minimum
  moment, and the WAV header proves the file covers the window - all checked at render, so a button
  that appears is a button that works.
- **The home card has a hierarchy instead of a grid.** One hero (Take notes), the calendar as a quiet
  pill, COMING UP as the content, and ONE "Connect a calendar ▾" row that offers Google or Outlook
  and disappears entirely once connected - the Granola/Loom/Fantastical pattern, and the end of the
  "weirdly symmetric" 2x2.

## The owner's four corrections, taken properly (2026-08-13, later)

The owner opened the build and found four things wrong, with reference screenshots from Outlook. All
four were right, and one exposed a rule this repo had already written down twice and ignored a third
time. **1,907 tests green**, `--notetest` 23/23, `--measuretest` clean, `--hittest` clean.

- **The day "picker" was a list of 14 days.** A meeting three weeks out was unreachable, and a flat
  list answers none of what a person is weighing ("is that a Friday?"). New `Views/DayPicker.cs`: a
  real month calendar in Toki's language - month + year, arrows, locale-aware weekday header, always
  six rows so paging never jumps, today ringed, chosen day filled, dimmed neighbours, a Today jump.
- **The time list ran 7am-9pm**, which quietly asserted nobody meets outside office hours - a 6am
  call with another timezone was unpickable. Now the whole day, and the picker opens SCROLLED TO the
  current value, which is what makes 48 rows cost nothing.
- **The third field is the END TIME, not a duration.** Outlook shows `6:00 PM - 6:30 PM`; nobody
  should do the arithmetic. The row now reads `2pm - 2:30 PM`, and the end picker's rows are
  "2:30 PM (30 min)" - Google's annotation, so time-thinkers and length-thinkers both get their
  answer. The When row went to two lines; three fields in 360px was clipping "tomorrow" mid-word.
- **The audio play button did not play. It had NEVER worked in the product** - it was a Border with a
  MouseLeftButtonUp handler inside a read-only RichTextBox, whose selection machinery eats the
  mouse-up. `NotePicker`'s own comment warns about exactly this. It is now a real Button (Click is
  raised from capture logic, which is why the ticks beside it always worked), drawn as a small filled
  pill instead of a bare triangle.
- **Style and Size are two toolbar controls now**, like every editor on earth - a Style dropdown
  (Title/Heading/Body, opens on the current line's style) and an "Aa 4" size chip that shows its
  number. The merged two-section dropdown is gone.
- **Connect Google AND Outlook live on the notch Home** - side by side when nothing is connected;
  once connected they shrink to small round provider marks beside the account address, with a compact
  "+ Outlook" pill to add the second. In Settings the unconfigured Outlook button is no longer
  DISABLED (indistinguishable from broken) - it explains the one-time Microsoft registration and
  names the guide.

**Two things the harnesses caught on the way out**, both worth their write-up: `--notetest` started
failing at 19px because the editor now loads the OWNER'S saved text size - the first real use of the
size dial broke a test that inherited ambient settings; it now pins the size. And `--measuretest`
reported a 4px clip that was really the harness measuring TWO DIFFERENT LAYOUTS: showing the view
fired `RefreshGoogle`, which read the real (disconnected) state and flipped the debug-pinned connected
card back to the CTA. `DebugConnected` now pins. The measure harness also learned to NAME the element
that differs instead of leaving a whole view to bisect by hand.

## Quality bugs, a document-shaped note editor, and a second calendar (2026-08-13)

**1,907 tests green.** The owner picked a batch off the pending list; this is all of it.

**Note quality - the three defects two real meetings left behind**

- **A template is a CLAIM, and it now has to be earned.** Both real meetings were classified
  "Decision log" and neither made a decision, so "Rationale" came out as invented justification for
  something that never happened. New `Llm/TemplateGate.cs`: the model still proposes, but the
  TRANSCRIPT has to contain evidence for a specialised template or it falls back to General ("Key
  points"), which asserts nothing and therefore cannot be wrong. Decision language, interview
  language, commercial language, standup reporting; 1:1 needs exactly two voices; a lecture needs one
  voice carrying 80% of the words. Deliberately loose about what counts and strict about needing
  some, because a missed specialisation costs a plainer note while a false one puts words in people's
  mouths.
- **Spoken self-corrections kept both halves.** "The educative, oh, executive summary" reached the
  notes with a word nobody said. `Transcription/SelfCorrection.cs` drops the discarded attempt, but
  ONLY when a cue ("sorry", "I mean", "oh") sits between two words that clearly sound like the same
  word - so "we're shipping, I mean, Tuesday" is left completely alone.
- **Toki's own edits are now visible.** Names it unified and repairs it dropped are stored with the
  meeting (`corrections_json`) and drawn in the transcript with a dotted underline and the original on
  hover. Notes stay clean, per the owner's rule. The wording is honest at meeting level - "also
  transcribed as" - because Toki records the PAIR it changed, not which instance.
- **"Re-clean saved transcripts"**, in Settings. Re-runs turn-joining, name correction and
  self-correction over meetings you already have. Offered, never automatic, and it does not touch a
  single summary, to-do or tick box - which is exactly why it is safe to offer.

**The note editor and the manual-note page**

- **A size dial, 1 to 12**, replacing the parked Bold/Italic. It scales the whole ramp together
  rather than setting a point size on a selection, for two reasons that are the same reason
  Bold/Italic is parked: Markdown cannot record "this word is 14pt", and per-Run formatting on this
  surface is what caused the S8 mosquito-text data loss. Remembered across notes, because somebody who
  wants bigger text wants it everywhere. `--notetest` is now **23 checks**.
- **The manual-note page reads as a document.** It ran the full width of the pane (a 900px line is
  genuinely hard to read), drew a form-style rule under the title, and ended in a Delete button. Now:
  a capped 720px measure, centred, with the date under the title where a document keeps its quiet
  facts, and Delete in a footer past a generous gap.
- **List markers are set RIGHT in their gutter.** Left-aligned, a tick box nearly filled the gutter
  while a four-pixel bullet floated 20px from its own words. Right alignment with one fixed gap gives
  every marker the same relationship to its text and still lines the text column up.
- **Blank lines were nearly twice the gap they asked for.** WPF's default `LineStackingStrategy`
  takes the LARGER of the requested line height and the font's natural height, so a 9px beat silently
  became 17px and every section drifted apart.

**The new-meeting composer, reshaped like Google's and Outlook's**

Day and Time still take typed words ("tomorrow", "2pm") and now each carries a picker, so nobody has
to guess what the box accepts. Both are prefilled with today and the next half hour - a blank form
asks a question the app could have answered. Guests became a labelled field that is always there
rather than a surprise behind an "Add people" toggle, and the Meet link became a labelled switch,
because a chip that fills in when active cannot say whether it describes a state or offers an action.

**"Open from last time"**

A recurring meeting's note now opens with what the last occurrence left unfinished
(`Llm/OpenFromLastTime.cs`). Deterministic, no second model pass, no added wait. It refuses to fire on
a one-off, refuses to repeat a to-do this meeting already produced, and refuses to resurrect anything
ticked.

**Evidence audio - hear the moment, and share the clip**

Researched first, as asked. **Granola keeps no audio at all** and markets that as the privacy
feature, so its evidence links can only ever reach the transcript. **Otter keeps audio in its cloud**
and shares a snippet as a hosted LINK it continues to control. Toki is the only one holding the audio
on the user's own disk - a real advantage, and a real liability, because a local file has no revoke.

So: playback is free and silent, plays a WINDOW with a run-up rather than the meeting, and export is a
separate deliberate act behind a right-click that warns once, in plain words, that the clip carries
other people's voices. Playing and exporting share one code path, so "share this" can never quietly
widen. No play button at all when the recording is gone. The text jump is KEPT beside it - reading is
instant and works in an open office; they answer the same question at different costs.

**Outlook, alongside Google rather than instead of it**

`MicrosoftAuth` (loopback + PKCE, no client secret - Microsoft registers this as a public client) and
`OutlookClient` (Graph). Both connections can be on at once and the agenda is merged; every event
carries its `Provider`, so an edit or delete goes back to the account that owns it. Three Graph
gotchas handled and commented: `calendarView` rather than `events` (or a daily standup appears once),
timestamps arrive with NO offset (parsed as local, a 2pm meeting shows at 7:30), and Microsoft rotates
the refresh token on EVERY refresh (store it or the connection dies overnight). Setup:
[`docs/outlook-setup.md`](docs/outlook-setup.md) - **needs an Azure app registration before the button
can do anything**, exactly like the Google one.

## Pinned names + the migration that retried forever (2026-08-13)

**Built**

- **Settings → "People Toki should spell right".** `AppSettings.PinnedNames` had been READ by
  `NameLexicon` since T2, but nothing could ever write it - the escape hatch existed with no handle on
  it, so the only way to fix a name the library could not settle by frequency was hand-editing JSON.
  Now a multi-line box beside the name field, with the same Save/Cancel/confirm pattern. A name typed
  here is trusted: correction reaches further towards it, and the speaker namer will attach it to a
  voice on evidence that would not be enough for a name nobody vouched for.

**Fixed**

- **The sticky migration logged an error on every single launch.** `prep-scratch.json` was read, then
  migrated, then renamed - and that is not atomic. Two launches in quick succession genuinely overlapped
  (the migration runs on a background thread 3s after start), so both read the same file and both wrote
  the same note. The owner's log showed `migrated 1 prep-card note(s)` **three times in under a second**.
- **And an unreadable file retried forever.** The old code left a file it could not parse in place "to
  be retried next launch" - but a file that cannot be parsed never will, so it logged
  `'t' is an invalid start of a property name` on every launch with no path to success.

Both fixed by **claiming the file with a rename before reading it** - the one operation a filesystem
makes atomic. Whoever wins the rename owns the migration; everyone else finds nothing and leaves. An
unparseable file is parked as `.unreadable` (text intact, never deleted) instead of retried, and an
unexpected failure puts the file back so the next launch really can try again.

1,844 tests green.

## Button fundamentals - the box is the button (2026-08-12)

Owner: *"the pressable button area is still the text or the symbol"* and *"the symmetry for the text on
the button is still weirdly not symmetric, and not centered"*. Two separate defects, both now measured
rather than eyeballed. Full write-up: [`docs/button-fundamentals.md`](docs/button-fundamentals.md).

**Built**

- **New `--hittest`.** Walks the real visual tree of every notch state and asks WPF what would receive a
  click at nine points inside each control. **A screenshot cannot catch either of these defects** - the
  pixels are identical whether a button works or not.
- **`Views/HitTarget.cs`** - one shared wrapper that makes anything clickable a proper box.
- **`Theme/OpticalCenter.cs`** - centres a label on its cap height, computed from the font's real
  metrics, replacing a hard-coded 1px nudge.

**What was broken**

`44 problems` on the first run. The Calendar action strip (`Edit`, `Copy link`, `Open in Google`,
`Delete`) were bare TextBlocks with click handlers, so only the glyphs responded while the hover fill lit
the whole row - the highlight was a lie about where the button was. All 11 About credit rows had negative
side margins that put 6px of each row outside the parent's clip. Tick boxes were 18px, the switch 21px.
And every pill label sat **1.7px low**, because a fixed 1px correction cannot be right at more than one
font size.

**Result**

```
[--hittest] checked 170 clickable controls across every notch state
RESULT: every clickable box is clickable edge to edge, and every label is centred.
```

Deliberately fundamentals only - hit targets, centring, clipping - because a full design and haptics
revamp is coming and styling done now would be thrown away.

## T5 + T8 - the speakers, and the list (2026-08-12)

**Built**

- **T8: speakers get names, and Toki keeps nobody's voice.** `them.wav` is split into voices and each one
  is named *from the conversation itself* - a self-introduction ("I'm Priya"), a handover ("over to you,
  Priya"), a thank-you ("thanks, Priya"), or being addressed by a name that is on the calendar. Nothing
  about anybody's voice is stored: reopen the meeting tomorrow and Toki has no way to recognise them.
  Full write-up: [`docs/speaker-naming.md`](docs/speaker-naming.md).
- **Rename once, applies everywhere.** A row of people sits above the transcript; click one, type the
  right name, and every line that voice said updates. A rename is a text update, not a retrained model.
- **The note arrives in two waves, and says so.** Summary and to-dos land at the usual speed; the names
  and to-do owners follow a few seconds later, in place. While that is pending the two sections show a
  pulsing "telling the speakers apart" / "working out who owns these" - because a note that silently
  rewrites itself while you read it is unsettling in a way a slower note is not. Owner's call: *"I don't
  like the idea of increasing the wait time"*.
- **To-do owners come free, with no second model pass** (`Llm/ActionOwners.cs`). Every to-do already had
  an evidence link to the line it came from; once that line knows who spoke it, a to-do somebody
  volunteered for names its own owner. Only first-person commitments count - *"I'll send the deck"* names
  the speaker, *"Craig will send the deck"* names nobody. **This closes T4 as a side effect of T8.**
- **T5: the Library list is virtualized** (`Views/MeetingList.cs`) and **drag-reorder was removed** at the
  owner's request - which is what made virtualization safe, since walking the cards by index is the one
  thing it silently breaks.

**Verified by running** (not by reading the code)

```
--uitest        build the whole list   424.2 ms -> 46.1 ms   all three budgets PASS
--speakertest   meeting 75 (a real 1:1)   2 voices, correctly separated,
                                          remote person named "Devi" from "Thank you, Devi."
                meeting 76 (11 people)    3 voices - under-split, no calendar connected
1,826 tests green.
```

**What broke, and what it taught**

- **The first real run invented participants called "Please" and "Otherwise"**, from lines like *"Please,
  please break out right away, everyone."* Both are perfect vocative shapes; neither is a person. A
  hand-written list of not-names is never finished, so the fix is a general rule: **a word said in
  lowercase anywhere in the same meeting is not a name.** Real names essentially never appear lowercase.
  That caught two of the three. "Otherwise" survived because it never happened to be said lowercase in
  that transcript - so the weakest signal (a bare vocative) now requires a name a human supplied.
- **The clustering threshold turned one announcer into six speakers.** Moved 0.55 -> 0.80. No single
  value serves both a one-to-one and an eleven-person call, so the question is which way to be wrong:
  under-splitting is coarse, over-splitting invents colleagues who were never in the room.
- **A consequence worth knowing:** because diarisation runs *after* the notes (owner's call - nothing may
  delay the notes), the note writer does not see the names on a fresh meeting. They appear on a rewrite.

## M0 - Scaffold (2026-07-15)

**Built**
- Installed the .NET 8 SDK (8.0.423) - the machine had no .NET tooling.
- Solution `Toki.sln` with two projects:
  - `src/Toki` - the WPF app (`net8.0-windows`), currently the default template window (placeholder until M1).
  - `tests/Toki.Tests` - xUnit test project referencing the app.
- `CLAUDE.md` (condensed from the brief), `ARCHITECTURE.md`, `mansplain/`, `.gitignore`.
- Received `Toki-ProductDesign/` (PNG exports of the macOS design) - the UI reference for M1's pill.

**Verified**
- `dotnet build Toki.sln` - build succeeded, 0 warnings, 0 errors.
- `dotnet test Toki.sln` - 1 placeholder test discovered and passed, proving the test harness runs.

**What broke**
- The xUnit template targets `net8.0`, but a WPF app targets `net8.0-windows`, so the test project couldn't reference the app. Fixed by retargeting the test project to `net8.0-windows` with `UseWPF` enabled. Every future test project must do the same.

**Not done in M0**
- CI (build + test on push) needs a GitHub remote; will be added when the repo is pushed. Local `dotnet build` + `dotnet test` are the gate until then.

## M1 - Pill shell (2026-07-15)

**Built**
- `AppState` state machine (`src/Toki/State/`) - single owner of the pill's state, explicit transition table, wall-clock recording start. 12 unit tests.
- `PillWindow` - one borderless, topmost, transparent window fixed at bottom-center; an inner shell morphs width/height between states with eased animations (transparent areas are click-through). Sizes taken from `Toki-ProductDesign/`.
- Placeholder views for all 9 visible states (meeting prompt, recording bar, expanded recording, summarizing, notes, no-speech, home, library, settings) styled to the design: dark surfaces, pill buttons, coral record button, pink Toki blob, animated waveform.
- Tray icon (pink blob, generated `Assets/toki.ico`) with menu: open, demo states, library, settings, quit. Left-click toggles the pill.
- Global hotkey **Ctrl+Alt+Space** toggles the pill.
- Dev aids: `Toki.exe --state <name>` opens directly in a state; every state change is logged to `%TEMP%\toki-m1.log`.

**Verified**
- 12/12 state-machine unit tests pass.
- Sid drove the real app: hotkey toggles, home ⇄ library ⇄ settings, meeting prompt → dismiss, Take Notes → recording → summarizing → notes → close. The state log recorded every transition matching the clicks.

**What broke**
- The tray-icon package restored against .NET Framework under bare `net8.0-windows`; retargeted both projects to `net8.0-windows10.0.19041.0` (needed for M2 loopback audio anyway). A restore warning remains but the modern build is used and works.
- Verification detour: GDI screenshots cannot capture transparent (layered) windows, so my captures showed no pill while the app was on screen and fine. Lesson recorded - verify via the state log + a human looking at the screen.

## M1.1 - UI revision to match templates (2026-07-15)

Feedback from Sid: the pill was too big, buttons were full ovals, and Toki wasn't alive.

- **Ambient state added** - Toki is always quietly present as a small pill (112×30) at bottom-center with the pink character peeking over the top; hover lifts it out (the signature moment). No more "only appears on hotkey."
- **Toki is animated** - the character now breathes (squash/stretch) and blinks at natural irregular intervals, everywhere it appears. Summarizing shows a spinning sparkle, cycling status with animated dots, a paper stack, and Toki bobbing.
- **Buttons fixed** - rounded rectangles (13px), not capsules/ovals.
- **Per-monitor DPI manifest** - the app was being bitmap-stretched (big + blurry) on scaled monitors; now it renders crisply at each monitor's real DPI. This was the main cause of "huge."
- **Self-verification** - added `Toki.exe --render <dir>`, which renders every view to a PNG via `RenderTargetBitmap`. This captures the visual tree directly, so I can finally see the UI despite the transparent window that defeats normal screenshots.

Verified by rendering all nine views and comparing against `Toki-ProductDesign/`.

## M1.2 - Interaction + alive loading (2026-07-15)

Owner decisions (asked directly): idle shows **nothing**; Ctrl+Alt+Space **toggles** open/closed; closing also via **Esc, click-outside, or the X button**.

- **Idle is Hidden** - nothing on screen until a meeting, the hotkey, or the tray icon. (The always-on ambient pill was dropped per the owner.)
- **Ctrl+Alt+Space toggles** Home ↔ Hidden, force-applied so the user can never get stuck.
- **Click-outside / Esc dismiss** panels (Home/Library/Settings/Notes/NoSpeech) back to Hidden; recording/summarizing/prompt are deliberately immune to a stray click.
- **Loading state is now clearly alive** - spinning sparkle, cycling status with animated dots, a soft pulsing glow behind Toki, Toki bobbing, and a sheet lifting off the paper stack, on top of the character's own breathing + blinking.

**Verified**
- 13/13 unit tests pass.
- Live animation proven by capturing the running window twice ~450ms apart with `PrintWindow(PW_RENDERFULLCONTENT)` (which *can* grab the transparent window): the two frames differ - sparkle angle and dots changed.
- Shell size confirmed 340×164 via a post-layout `ActualWidth/Height` log (the PrintWindow grab renders transparent pixels as black, so size can't be eyeballed from it).

## M2 - Audio spike (2026-07-15) - the de-risk milestone

**Built**
- `MicCapture` - WASAPI shared-mode capture of the default mic ("you" stream) to a WAV, with peak metering.
- `ProcessLoopbackCapture` - the hard part: per-process-tree loopback via `ActivateAudioInterfaceAsync` + `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK` with `INCLUDE_TARGET_PROCESS_TREE` ("them" stream). Hand-written COM interop (no NAudio support for this), event-driven capture to a WAV. This is what will tap a browser meeting, where Meet's audio lives in a renderer child process.
- Self-test harness: a WinExe console attach (`ConsoleHelper`) + `--mictest`, `--loopbacktest <pid|name>`, and a `--playtone` helper. `--out <dir>` and `--seconds <n>` options.

**Verified (on this PC, via self-tests)**
- `--mictest` → recorded a real 48kHz WAV from the Realtek mic array, peak 24.6% (heard the room). Playable.
- `--loopbacktest` → one Toki instance played a 440Hz tone; another tapped **that process** and captured peak **20.5%** - matching the tone's 0.2 gain exactly (proves it's the real tone, not noise).
- **Selectivity proof:** with the tone playing, tapping the tone process gave 20.5% while simultaneously tapping a *silent* app (Slack) gave **0.0%**. Toki captures only the target's audio, never system-wide.

**Still to verify with Sid (by ear, brief's bar):** record a real 2-person browser Google Meet and listen to `you.wav` + `them.wav`. Commands:
```
Toki.exe --mictest --out <folder>                 # 10s of your mic
Toki.exe --loopbacktest chrome --out <folder>     # 15s of the meeting app (while it plays)
```

## M2.1 - Robustness, double-click app, ghost mode (2026-07-15)

Owner asks: it must never crash, it should be a double-click app for testing, deployment should feel like Wispr Flow, and it needs a Cluely-style undetectable recording overlay.

- **Never crashes** - global handlers for UI, background-task, and AppDomain exceptions; UI exceptions are logged and swallowed so the app keeps running. Tray-icon and ghost-mode setup are individually guarded.
- **Single instance** - a named mutex; a second launch exits quietly instead of fighting over the hotkey and stacking pills.
- **Opens on launch** - double-click now shows Home immediately (was: nothing until hotkey), then normal rules apply.
- **Ghost mode** - `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` + tool-window style: the overlay is visible to the user but **invisible to screen recording and screen-share**, so Toki never leaks onto the call it's taking notes on. Also kept out of alt-tab.
- **Double-click app** - self-contained single-file `dist\Toki.exe` (75 MB, bundles the .NET runtime → no "unable to run" prompt, runs on any Win10 2004+ PC). Shortcuts placed on the Desktop and in the project folder.

**Verified**
- 13/13 tests pass. Stress test launched all 9 states - 0 crashes.
- Ghost mode: a `PrintWindow(PW_RENDERFULLCONTENT)` capture (how recorders grab windows) comes back fully black while the app is visibly on screen.
- Double-click: launching the shortcut opens Home; a second launch detects the running instance and exits (stays at 1 process).

**Deployment note (future):** real install is a signed Velopack installer + launch-at-login (Wispr-Flow style), landing in M8. `dist\Toki.exe` is the test build.

## M3 - Meeting detection + real recording (2026-07-15)

**Built**
- `MicUsageReader` - reads which apps hold the mic *now* from the capability-access registry (HKCU **and** HKLM; `LastUsedTimeStop == 0`), decoding the `#`-encoded exe paths.
- `MeetingDetector` - denylist (dictation/recorders/audio tools/AI IDEs/Toki itself), debounce (2 polls ≈ 5s), 10-min per-app cooldown on dismiss, prompt-once-per-mic-session, and auto-stop when the recorded app frees the mic for 3 polls (≈ 7.5s). Poll logic is pure and unit-tested.
- `ProcessResolver` + `ProcessTree` - map a detected app to the **root** of its process tree (Toolhelp snapshot), so `INCLUDE_TARGET_PROCESS_TREE` reaches a browser's audio-rendering child.
- `RecordingSession` - starts the mic ("you") always and the per-process loopback ("them") when the app resolves, writing WAVs to `%APPDATA%\Toki\Recordings\<timestamp>\`. Falls back to mic-only if the tap fails - never crashes a meeting.
- Wired into the pill: detection → "Chrome is in a call - record it?" prompt → Record starts real capture → Stop/auto-stop → Summarizing → Notes (or the empty state if silent). `--detecttest`, `--recordtest`.

**Verified**
- 21/21 unit tests pass (8 new detector tests: denylist, debounce, reset, cooldown, can-prompt gate, auto-stop, presence-reset, no-prompt-while-recording). The debounce/cooldown tests caught a real re-prompt-spam bug, now fixed.
- `--detecttest` reads the live mic set and applies the denylist correctly.
- `--recordtest` drives the real `RecordingSession`: mic-only and mic+loopback both write playable WAVs to `%APPDATA%\Toki\Recordings\`.

**Robustness/UX fixes this round**
- Replaced focus-based click-away (which flickered the pill shut) with global low-level mouse/Esc hooks + a 500ms grace period - dismiss only on a real click outside the pill, no focus-stealing (keeps ghost feel).

**Still to verify with Sid (by ear):** join a real Google Meet, accept the prompt, talk, hang up → watch auto-stop, then play `you.wav`/`them.wav` from the recordings folder.

## M3.1 - Never-hang + animation redo (2026-07-15)

Owner feedback: Toki must **never hang** (always alive, responds only when summoned), and the animations should be buttery/cute like the Mac app - the loading screen especially should feel alive, not just glowing papers.

**Never hang**
- **Root cause fixed:** `StopRecording` used to block the UI thread up to 3s joining the capture worker (which also stalled the input hook → whole app frozen). Capture now starts *and* stops on background threads; the UI advances instantly.
- **Non-invasive input:** dropped the global keyboard hook (it saw every keystroke); the remaining mouse hook does zero UI work - it reads a **cached** shell rectangle, so it can never lag system input.
- **Relaunch surfaces the running instance** (named event) instead of silently exiting, so double-clicking a running Toki brings it up. Tray **Restart Toki** added.

**Animation redo**
- The loader is now a **cycling character scene**: Toki (breathing/blinking/bobbing, soft glow) beside a motif that changes through the stages - pulsing **rings** (Listening back) → a **document with a sweeping highlight** (Reading it over) → **checkboxes ticking** one by one (Finding to-dos) → a **pen drawing an underline** (Writing it up) - with a filling progress bar. Clean, rounded, spaced.
- Morph tuned buttery (gentle 340ms ease with a hint of spring).

**Verified**
- 21/21 tests, 0 crashes across all states.
- Live-captured the loader at 4 moments (via a dev `--noghost` flag) - the four stages visibly cycle with distinct animated motifs.
- Published app launches, single instance, relaunch re-surfaces it.

## M3.2 - Black-window + slowness fix (2026-07-15)

Owner reported: ghost mode works (recording doesn't capture Toki 👍), but the PC gets slow/buggy and a **black rectangle** pops up. Screenshot analysed.

**Root cause (confirmed via Microsoft docs/issue tracker):** `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` is **incompatible with `AllowsTransparency=True`** (layered) WPF windows - the combination produces the black rectangle, and the transparent window also forced slow **software** rendering. Separately, the click-away mouse hook ran on the **UI thread**, so when rendering stalled, *all* mouse input across Windows lagged.

**Fixes**
- **Pill is now a non-layered, hardware-accelerated window** with native **DWM rounded corners** + subtle border. Opaque, GPU-composited, sized to the current state's content and anchored bottom-center. Ghost mode (`WDA_EXCLUDEFROMCAPTURE`) works *reliably* on non-layered windows - no more black rectangle.
- **Mouse hook moved to its own dedicated thread** with its own message loop, so UI load can never lag system-wide input. Callback does pure arithmetic against a cached window rect.
- **Animations pause when hidden** - `TokiBlob`, `Waveform`, and the loader stop when off-screen (cached views were animating forever in the background). Fixed a blink-timer handler leak too.
- **Loader only animates the active stage** (previous stages' motifs were still animating invisibly).

**Verified**
- Ghost still works: a `PrintWindow` capture of the running window comes back blank (excluded).
- CPU: idle Home 3.8% → 1.8%; summarizing 6.5% → 5.3% (now GPU-composited). Hidden ≈ 0 (animations paused).
- 21/21 tests, 0 crashes. Home stays open with no interaction (click-away only fires on a real outside click).
- New window renders as a clean rounded opaque pill.

**Trade-off:** dropped the layered window's soft outer shadow and very-large corner radius; DWM gives clean anti-aliased ~8px corners. Stability + ghost reliability were the priority.

## M3.3 - UI fixes: rounded corners + content fit (2026-07-15)

Owner reported: sharp corners (not rounded), Library/Notes content cut off, record button pushed off-screen.

- **Sharp corners fixed** - DWM corner rounding doesn't apply to a borderless (`WindowStyle=None`) window, so the pill had square corners. Replaced with a `SetWindowRgn(CreateRoundRectRgn)` region at radius 22 (re-applied on every resize), which also brings back the larger pill radius the design wants.
- **Content cut-off fixed** - when the window became size-to-content, the content host stopped stretching the view to fill it, so views rendered at their natural (larger) size and clipped (Stop/Star buttons, right-edge text, bottom items). Switched the host to a `Grid` that stretches the view to the exact window size; text now wraps and tall panels (Notes) scroll internally. Also clamp window height to the work area so it never runs under the taskbar.
- **Verified** with DPI-aware window captures (the machine is at 125% scaling - my earlier non-DPI-aware captures were grabbing only the top-left corner, which faked "cut off"). Home, RecordingOpen, and Notes all render fully with rounded corners; the layout log confirmed `view == host` size. 21/21 tests, 0 crashes.

## M3.4 - DPI-proof positioning (2026-07-15)

Owner (with screenshots): the recording bar floated in the **middle of the screen** and Notes ran off the **bottom** - on their 125%-scaled display.

- **Root cause:** the window was positioned with WPF's logical `Left`/`Top`, which mis-scale on a per-monitor-DPI-aware app at 125%+ - so nothing landed where intended.
- **Fix:** position in **physical pixels via Win32** - read the real work area (`SystemParametersInfo(SPI_GETWORKAREA)`), the window's physical size (`GetWindowRect`), and place it with `SetWindowPos`. Fully dynamic/self-adjusting for any resolution or DPI. Window height is clamped to the physical work area so tall panels never run under the taskbar (they scroll internally).
- Removed the window-`Top` slide animation (it fought the physical placement); entrance is now the content pop+fade.

**Verified** (position logs + DPI-aware captures, screen 1920×1080, work-area bottom 1020): recording bar bottom-edge at 1002, Notes bottom-edge at 1002 - both sit just above the taskbar, centered. Recording bar and summarizing render correctly with rounded corners. 21/21 tests, 0 crashes.

## M4 - Transcription (2026-07-15)

On-device speech-to-text with **Parakeet TDT 0.6b v2** via **sherpa-onnx**.

**Built**
- `AudioLoader` - any WAV → 16kHz mono float32 (NAudio downmix + WDL resample), for the 48kHz-float "you" and 44.1kHz-16bit "them" streams.
- `ModelManager` - downloads + extracts the ~630MB Parakeet int8 bundle to `%APPDATA%\Toki\Models\` with progress (onboarding-time, never mid-meeting - lesson 2).
- `Transcriber` - sherpa-onnx `OfflineRecognizer` (`nemo_transducer`, greedy), whole-file per stream, one fresh stream per file (no leaked state - lesson 5), token timestamps grouped into segments. Empty result = silence, returns nothing (lesson 4/7).
- `TranscriptMerger` - merges you/them into one timeline and removes echo (drop a "you" line that duplicates a "them" line within ±4s; exact-match only for short interjections - lesson 6). 8 unit tests.
- `TranscriptionService` - orchestrates both streams → merge; owns the heavy recognizer (created once, warmed at launch); degrades gracefully if the model isn't installed.
- Wired into the flow: Stop → loader animation while transcription runs **off the UI thread** → Notes shows the real you/them transcript (or the empty state if silent). `--asrtest`, `--transcribetest`.

**Verified (real audio)**
- `--asrtest` on a 22.8s speech clip: transcribed near-perfectly, **tail matched the end** (no mid-file truncation), **2.3× real-time on CPU**.
- `--transcribetest` on two separate speech files: both transcribed accurately and merged with correct **You/Them** attribution.
- 29/29 unit tests pass.

**Deferred:** live streaming preview (streaming zipformer) and the whisper.cpp error-only fallback are stubbed (fallback logs + skips for now) - both are follow-ups; the core accurate transcription works.

## M5 - Storage + heuristic notes + Library (2026-07-15)

**Built**
- `MeetingStore` - SQLite at `%APPDATA%\Toki\toki.db` (meetings, transcript, decisions, questions, action_items; manual notes share the `meetings` table via `kind`). Save/Get/List/Search/SetActionDone/UpdateTitleAndBody/Delete (cascade). `you:`/`them:` search filters.
- `NoteExtractor` - deterministic "notes floor" (no model): TL;DR, decisions, action items (with owner + due heuristics), open questions; heuristic title with validation + `<app> - <date>` fallback (lesson 9). 7 unit tests.
- `MarkdownMirror` - every meeting also written to `%APPDATA%\Toki\Notes\*.md` (user-inspectable, export-ready).
- Wired: on transcription, notes are extracted and the meeting saved to DB + Markdown, then the Notes screen shows the structured notes (TL;DR / Decisions / checkable Action items / Open questions / Transcript).
- **Library** with real data: searchable meeting cards (date, tldr, open-actions/people tags), open → notes, delete-with-confirm, **+ New note**.
- **Manual notes**: editable title + freeform body, auto-saved, searchable alongside meetings (brief's NEW feature). `--dbtest`, `--notestest`, `--seed`.

**Verified**
- `--dbtest`: insert/search(you:/them:)/update/delete round-trip + cascade.
- `--notestest`: planted transcript → echo removed, decisions/actions(+owner/due)/questions extracted, clean title.
- Live DPI-aware captures of Library (3 seeded items), Notes (full structured notes), and the manual-note editor - all render correctly. 36/36 tests.

## M6 - LLM notes (2026-07-15)

On-device AI-written notes with **Qwen3-4B-Instruct-2507** (Q4_K_M GGUF) via **LLamaSharp**.

**Built**
- `LlamaLlm` (ILlm) - loads the ~2.5GB GGUF once, Qwen3 chat template, `DefaultSamplingPipeline` with Qwen's official params (temp 0.7 / topP 0.8 / topK 20 / presence 0.5; never greedy - lesson 8).
- `LlmNoteWriter` - three focused passes (decisions → to-dos → **TL;DR last**, from the reduced sections); every output validated (`TextValidator`: rejects CJK-drift / repetition), retried once, else falls back to the heuristic per section. 12 unit tests.
- `LlmService` + `AppSettings` - model selection (On-device / Heuristic-only / BYO API key, DPAPI-encrypted), **GPU off by default** (CPU is crash-proof; GPU is opt-in for 6GB+ cards). `OpenAiLlm` for the BYO path.
- Wired: after a recording, heuristic notes show instantly, then the LLM notes **upgrade in place** in the background. `--llmtest`. Functional Settings screen.

**Verified**
- `--llmtest`: Qwen wrote a correct, coherent, English summary + decisions + action items (with owners & due dates) from a test transcript - **62s on CPU**, no degeneration.
- Caught + fixed two real bugs: a GPU **VRAM OOM crash** on a 4GB card (→ default CPU, context 4096, GPU opt-in) and an **async deadlock** when blocking on inference from the UI thread (→ run off-thread). 48/48 tests. App launches clean with the native LLM backend configured.

## M6.1 - Refinements: notes shape, speed & polish (2026-07-16)

Owner testing on real meetings drove a batch of refinements to the notes, the engine speed, and the UI.

**Notes - Granola-style, one pass**
- Restructured the notes to **Summary → Key points → Action items** (labelled). Dropped the old "Decisions" section (it just echoed the to-dos) and "Open Questions". The `decisions` DB column is repurposed to store Key points (no migration).
- `LlmNoteWriter` now does **one structured generation** (Key points + Action items + Summary, summary written last so it's drawn from the details) plus a tiny title pass - down from four passes. Output token budget trimmed.
- **Concise, context-aware titles** via a dedicated title pass from the summary (e.g. "Finalize Branding and Webhook Integration"), with a validated heuristic fallback.
- Owner/due placeholder junk (`->`, `-`, `TBD`) is now stripped in the prompt, the parser, **and** the renderer, so old rows are clean too.

**Speed - 65s → ~13s for the AI notes**
- **VRAM-auto GPU offload** (`LlmRuntime`): reads the true GPU memory from the driver registry and offloads a *safe* number of layers (20 on a 4 GB card, ctx 3072), or full offload on a big card, or clean CPU otherwise. The Vulkan backend is loaded **only** when we actually offload - enabling it at 0 layers was the pathological slow path (was ~65s of CPU/GPU thrash; clean CPU is ~46s).
- Both engines are now **warmed at launch**, so a meeting-stop never waits on model load.
- Settings GPU toggle is now **automatic** (on = auto/safe, off = force CPU) instead of forcing an unsafe full offload.

**No more "bad first draft" flash**
- Toki now stays on the summarizing animation until the good AI notes are ready, then reveals them once - instead of flashing the rough heuristic draft (raw transcript lines) and swapping it out ~10s later. A 150s timeout guards against a stuck generation.

**UI polish (buttery pass)**
- New resizable **Library window** as the maximise target and the Home/tray Library button: master–detail, every meeting + manual note, with a **"‹ Back to Toki"** button.
- **Custom slim rounded scrollbars** and **custom rounded checkboxes** (coral fill + tick) app-wide; soft cross-fade when switching notes.
- **Copy transcript** and **maximise** buttons on the pill Notes view; removed a dead "search this transcript" box.
- **Manual notes** are fully editable in the window with an explicit **Save**.

**Transcription - findings (kept sequential on purpose)**
- Measured Parakeet at **~4× real-time on CPU**; the two tracks run **sequentially**. Parallelising them corrupted the transcript (sherpa-onnx isn't safe to decode two at once - non-deterministic output), so it was reverted: correctness over ~15% wall-clock. More CPU threads don't help (flat past 6). The shipped engine is **CPU-only** (no GPU execution provider in the package).
- Net: total summarize time is ~35s on a 1-min two-party meeting, dominated by transcription. The clean path to <30s is **live transcription during the meeting** (parked, task #14).

**Verified**
- `--llmtest`: new structure + concise title, **~27s cold / ~13s warm**, no degeneration. 49/49 tests.
- Live DPI-aware captures of the Library window, manual-note editor, and pill Notes view - all render correctly after fixing a WPF crash (a `Template` set inside a `ControlTemplate` trigger blanked the window black).

## M6.2 - Reliability on a small PC (2026-07-16)

Triggered by a real 30-min meeting that saved **"nothing was said."** Root-caused from the logs + the actual 784 MB recording to a **6 GB RAM** machine trying to hold both heavy models (2.5 GB LLM + 630 MB ASR) at once.

- **Manual start now captures the other side.** Recording from Home/hotkey was mic-only, so a muted user saved silence. `BeginRecording` now resolves the meeting app on the mic (`ResolveMeetingTarget`) and taps it. Auto-stop stays armed only for prompt-accepted meetings.
- **Long meetings can't crash ASR.** `Transcriber` windows long audio into ~3-min slices cut at quiet points (whole-file kept for normal meetings). Verified end-to-end on the exact 35-min file that used to throw.
- **Loader OOM fixed.** `AudioLoader` preallocates one exact array instead of a doubling `List` + `ToArray`.
- **Low-memory mode (`App.LowMemoryMode`, <8 GB).** Only one heavy model resident at a time (release LLM before transcribe, release ASR before notes, no LLM prewarm, release LLM after notes). This is the fix for the crash *and* the first-launch clunkiness.
- **Gentle warm** on a below-normal-priority thread; **junk-tap guard** (sub-3s, <5 words → discarded, no hollow Library entry).

Docs: `mansplain/11-fits-your-pc.md`.

## M6.3 - Calmer recording, sane dismissal, honest "glass" (2026-07-16)

- **Recording chip** - removed the fake looping waveform; the recording pill is now a small calm chip (breathing dot + time + stop, 138×46) instead of a wide animated bar.
- **Click-outside dismissal** - `DismissOnClickOutside` reduced to `{Home, NoSpeech}`; Notes/Library/Settings (and recording/summarizing) no longer vanish when you click into your real work.
- **"Glass" pill** - first attempt kept it opaque (mistakenly thought translucency broke ghost mode). Corrected in M6.4.

Still open: further speed (live transcription remains the real answer).

## M6.4 - Real translucent glass, crisp icons, design-matched Library (2026-07-17)

Owner pointed out (correctly) that Cluely / "cheating daddy" have transparent windows *and* stay hidden from screen capture - so the M6.3 "opaque only" conclusion was wrong.

- **Genuinely translucent pill** - `PillWindow` is now `AllowsTransparency=True` with a translucent Border fill + top sheen. Verified: `--noghost` shows a see-through pill; **ghost-on shows it absent from capture** (ghost still works, via `WDA_EXCLUDEFROMCAPTURE` re-applied on every show). No DWM accent acrylic (it caused sharp black corners); corners are clean because outside the Border the window is truly transparent.
- **Crisp icons** - all buttons use **Segoe Fluent Icons**; copy / open-window / close are now distinct (were ambiguous unicode glyphs). `IconButton` restyled to a clean floating glyph with a subtle hover highlight.
- **Library cards** - date badge + meta + snippet + "N open / N people" chips, matching `Toki-ProductDesign/⑥`.
- **Buttery Library open** - the window scales up from its bottom-centre (toward the pill) with a fade, so it reads as the pill extending into a window instead of a hard pop.
- **Drag-to-reorder notes** - pick up a card and the others slide to make space (iPhone-home-screen style); the order persists via a new `sort_order` column (safe migration, seeded to the current newest-first order). New notes land at the top.

## M6.5 - Monotone notes + morph/drag polish (2026-07-17)

Owner feedback on the first pass: coloured section headers + green tick were "ugly, not clean," the Library "force opened," and a dropped card could sit awkwardly between two rows.

- **Notes go monotone** - SUMMARY / KEY POINTS / ACTION ITEMS / TRANSCRIPT are quiet dim-grey labels (no coral/green/blue on text, no tick icon); chips are neutral grey (YOU a shade brighter than THEM). Apple-clean, generous consistent spacing. Removed the "ON-DEVICE" badge.
- **Morph grows from the pill** - the Library window's content now scales up out of the pill's *exact* on-screen rectangle (not the screen centre), so it reads as the pill extending into a window.
- **Drag settles cleanly** - on release the card animates into its target slot before committing (it can no longer sit between rows), and an off-by-one in the drop target was fixed.

## M6.6 - Transcript integrity + calmer recording + UI fixes (2026-07-27)

Owner tested real 20-min meetings and hit two real bugs plus a batch of UI issues.

**Transcript was shattered per word (the big one).** Notes came out garbled ("a udi o", "min ute", "D eep ik a"); the transcript broke every 1–2 sub-word pieces. Root-caused meticulously from the actual recordings + logs: `GroupIntoSegments` splits on a time-gap (>0.8s), and the split was allowed to fall **mid-word**. Parakeet emits words as sub-word pieces, and on hesitant/pausey real-meeting speech the gap landed inside words. Verified the tokenizer directly (`--asrtest` debug dump): this Parakeet build marks a new word with a **leading space** on the token (`" R","as","h","m","i"` = "Rashmi"), *not* the `▁` marker the old `Detokenize` assumed (its `▁`→space replace was a silent no-op - the spaces were already literal). Fix: only break at a **word boundary** (token with a leading space / `▁`), on either a real pause **or** end-of-sentence punctuation. Verified on the exact meeting that shattered: **912 fragments → 82 clean per-sentence segments**, whole words throughout, tail matches audio end.

**A muted-mic side is skipped now.** Logs showed meetings where the user's mic was muted the whole call (`you peak 0%`) yet we still spent ~half the total time transcribing pure silence. `TranscriptionService` now skips a near-silent stream (linear amplitude scan, early-exit) - roughly halves processing whenever one side is muted. (The remaining ~4×-real-time floor on a *fully-active* meeting is why live transcription is the next real speed win - parked below.)

**UI**
- **Recording is out of the way (WisprFlow-style).** While recording, the pill collapses to just the **alive Toki character** (breathing/blinking) with a small pulsing dot - no timer or button sitting on screen. Click it to reveal a compact **timer + Stop**. The expanded view's **fake placeholder transcript** (shown during real recordings - a lie) was removed.
- **Softer record button.** The harsh fire red-orange (`#F94A26`) → a truer, calmer coral (`#FF6F61`) across record/stop/accents.
- **Adaptive greeting.** "Welcome back, Sid!" (hardcoded) → the actual user (settings name, else Windows account name).
- **No em dash** in the "X is in a call. Record it?" prompt.
- **Library opens instantly.** It felt like a ~2s freeze. Profiled it: the cost was rebuilding content on every open - above all **rendering a 1140-segment transcript synchronously** into a non-virtualizing panel. Fixes: stream the transcript in on background priority (render ~30ms), stop rebuilding twice per open, and keep the window warm + skip the rebuild unless data changed (like WisprFlow keeping its window alive). Measured **~940ms → ~80ms**.

Verified: `--asrtest` on the real 20-min recording (82 clean segments), `--render` of every view, 49/49 unit tests.

## M6.7 - UI/UX overhaul: hover-reveal pill, smooth morphs, cleaner Library (2026-07-27)

A focused pass on how Toki *feels*, driven by live owner testing. Everything below was verified by running the app (view renders, mouse-driven hover cycle in the log, maximise capture) - not just described.

**Toki is now an always-present, hover-reveal pill (WisprFlow-style)**
- **Launch = the tiny alive Toki pill** (56×44, breathing/blinking), not a static "Welcome back" panel. **Hover it → Home reveals** ("welcome back" + the page); **move away → it collapses back**. Ctrl+Alt+Space toggles the pill on/off.
- **While recording**, same idea: the tiny Toki sits out of the way; **hover → it grows to the timer + a `Stop` button**; leave → shrinks back. No click needed.
- **The morph is smooth, not a burst:** the pill *animates its size* (grows over ~300ms, shrinks faster ~190ms so closing feels snappy), staying anchored bottom-centre, content fading in.
- **Hover detection is cursor-position polling, not WPF `MouseEnter`/`Leave`** - those fire phantom leave/enter while the window animates+repositions under the pointer, which caused an open/close flicker loop. Polling the real cursor vs the pill's real rect gives clean hysteresis (on it → open; off it → closed) with a dwell (~260ms, so a graze won't trigger it) and a quick close (~120ms).
- **Toki blinks + pops** the moment you hover the pill, and when Home/recording controls appear - alive and glad to see you.
- Fixed: the expanded recording pill sometimes clipped the `Stop` button (height was exactly tight) - gave it headroom so it always shows. Fixed: Home wouldn't collapse back (the state machine disallowed Home→Collapsed) - added the transition.

**Library window**
- **Opens instantly** (~80ms): created once and kept warm (pre-built off-screen at launch), hidden not destroyed between opens, and rebuilt only when data changed. The transcript is streamed in on background priority instead of built all at once. See ARCHITECTURE.
- **Maximise is clean now:** pinned to the monitor work area (`WM_GETMINMAXINFO`) so it snaps correctly with no jump; the content is a **fixed-width readable column** so widening/maximising doesn't re-wrap every transcript line (that reflow was the lag that made the content "sit then snap"); dark window border, not white.
- **Monotone selection** (soft white hairline, not the coral/red box). The maximise-from-transcript button now hides the pill first, then opens the warm window - consistent with Home→Library.

**Notes & Home**
- **Manual notes auto-save** in the Library window (debounced) - the static red **Save**/**Cancel** are gone; a quiet "Saved" flashes and fades; the editor left-aligns instead of floating centre.
- **One `Take Notes` button** on Home (the duplicate "Meeting" is gone - both just recorded).

## M7 (UI) - The virtual notch: Toki hangs from the top edge (2026-07-28)

A design pivot (owner-driven): instead of a bottom pill (which shadowed WisprFlow), Toki is a **virtual notch** at the top-centre edge - closer to the original macOS Toki, which "hangs from the notch". Built in three phases; the whole state machine, hover-morph, blink and ghost mode transferred unchanged, just re-anchored to the top.

**The notch**
- Anchored **flush to the top-centre edge**, square top corners, **rounded only on the bottom** - reads as carved into the top, not a floating pill. Grows **downward** (blooms out of the edge) and **tucks back up** into it on hide.
- **Idle is nothing.** It appears only when needed.

**How it comes and goes**
- **Reach the top-centre edge → it summons** (Phase 2). Tuned against accidental triggers (top-centre is crowded with browser tabs / window controls): a **narrow centre band**, the **absolute top pixel row**, a short **dwell**, and a **velocity gate** (a fast reach for a tab won't trip it). Suppressed while **dragging a window** (don't fight Snap Layouts) and in **fullscreen** (`SHQueryUserNotificationState`).
- **Peek ≠ open.** Reaching the edge shows only **Toki peeking** (the character alone - no dark box). Opening the welcome window is deliberate: **click Toki**, or **dwell noticeably** ("waiting for it"). A brief hover just shows the peek and tucks away.
- **Hotkey / launch** also summon it; **Home stays until Esc/click-away** (an explicit summon never vanishes).
- **Recording** shows a **minimal eyes-peek** (tiny, clear of browser tabs) - hover reveals timer + Stop.
- **Results** (notes / "nothing was said") **drop for a glance (~2s) then tuck themselves away** so they never sit on your work - unless you hover to keep reading.
- **Multi-monitor:** appears on the screen your cursor is on.

Env `TOKI_NO_EDGE` disables edge-summon (a Settings toggle will follow). Settings notch trimmed (no dead space); the Settings page content is still placeholder wiring - flagged for the ship pass.

## M7.1 (UI) - Context-aware snap-bar: Toki first, tuck-not-dismiss (2026-07-28)

Refined the notch into a proper snap-bar that remembers where you were and never squats on your work.

**Toki first, then the page**
- **Launch / reach-the-edge → Toki peeks** (the character alone, no panel) and greets for ~3s. **Click Toki** (or rest on it) → it **blooms open into your remembered page** - Home by default, or whatever you were last on.
- **Context-aware.** Toki remembers the current page (`_context`). Clicking into your real work **tucks the page to a thin sliver (keeps context)** instead of resetting to Home; re-summoning restores that page. A tucked sliver fully hides after ~9s, still remembering the page.

**Persistent tucked sliver (no rebuild)**
- Any page (Home / Notes / loader / "nothing was said") **tucks to a sliver ~2.5s after you stop hovering** - not gone, a little hangs from the edge. **Hover the sliver → the SAME page slides back down** (context preserved, no reload).
- **Loader** shows ~2.8s then tucks regardless of hover (it's a transient "working" flash). **Results arrive as a quiet sliver** (a peek), never a panel dropped in your face - hover to open.

**Recording = upside-down Toki**
- Toki **flips and falls** to hang **upside-down**, only its **eyes peeking** over the edge, blinking - minimal, alive, clear of browser tabs.

**Two real bugs fixed**
- **Timers were starved during model warm-up.** The tuck timers ran at `DispatcherTimer` default *Background* priority, so they got starved while the ASR/LLM models load at launch - exactly when Home / the loader show. That's why pages during warm-up never tucked but Notes (later) did. Bumped to **Input priority**. (This was the "works in the demo, not in real use" bug.)
- **Accidental-tap guard strengthened.** A ~5-word mis-click ("Met the t I mean") was saved as a meeting because the guard required <3s **and** <5 words. Now drops anything with **<12 words or <4s** - a mis-click is never a meeting.

Sizes trimmed smaller across the board (Notes no longer half-screen). Timer/animation knobs are all constants at the top of `PillWindow`.

## M7.2 (UI) - Pitch-black notch, close-off that *sticks*, tiny recording peek (2026-07-28)

Owner testing in a real Chrome session exposed that M7.1's "tuck to a sliver" left Toki squatting on the screen, and - worse - it kept **re-summoning itself**. Rebuilt the resting behaviour around one principle: **one element at the top, and once it's gone it stays gone until you deliberately ask for it back.**

- **Pitch-black notch.** The frosted-translucent surface read as a washed-out grey pill and slivers were nearly invisible. The notch is now a **fully-opaque pitch-black slab** (no white sheen, no translucency) - a real "hole carved out of the top edge," like the Mac notch / Dynamic Island.
- **Close-off, not a lingering sliver.** A page now **closes off completely** (→ `Hidden`, context remembered) when you move on, instead of leaving a strip hanging in your way. The **loader is the one exception** - it keeps a clean sliver as its "I'm working" heartbeat.
- **Summon that sticks (the "context-aware close-off doesn't work" bug).** The log showed `Home → Hidden → Collapsed → Home` looping every ~2s: the top-centre summon zone sits exactly on **Chrome's tab strip**, so the instant Toki closed off, the cursor (still up there) re-triggered it. Fix: the edge-summon **re-arms only after the cursor drops ≥150 px into the page body**, the arm is consumed on each summon and reset on every close-off, and the dwell went 220 → 300 ms. Now you can close Toki and keep working up top - it stays gone; you bring it back with a deliberate reach up (or `Ctrl+Alt+Space` / tray).
- **"Nothing was said" no longer loops.** Clicking *Okay* hid it, but the remembered page was still `NoSpeech`, so hovering re-opened it forever. A **result is a one-shot announcement** - dismissing it now resets the remembered page to Home.
- **Loader is a clean sliver.** It used to show a clipped slice of Toki's head + text when tucked (ugly). The loader view's top row is now exactly the sliver height, carrying only a **soft coral shimmer heartbeat** - Toki's body never peeks through. Redesigned smaller and cuter.
- **Recording peek shrunk** to ~34 px (was 52) so the upside-down eyes never clash with browser tabs or the snipping-tool toolbar.
- **`Ctrl+Alt+Space` summons the small Toki**, not the full welcome window (Toki-first, like the edge-summon).

## M8 (Notes) - Map-reduce, adaptive, honest notes (2026-07-28)

Two real meetings exposed that the notes were fundamentally broken, and the logs told the true story:

| Meeting | Transcription | Notes |
|---|---|---|
| 25-min | **602 s (10 min!)** whole-file at stop | LLM overran its ~3 k-token context → threw → silent heuristic fallback (TL;DR = the first three sentences, "to-do" = 19 keyword-matched lines) |
| 5-min "nothing" | 38 s | LLM ran and **manufactured corporate filler** out of a screen-share logistics exchange |

**Findings:** the 6–7-minute wait is *transcription*, not notes (see M8.1 plan). The bad notes are two separate failures - **context overflow on long meetings** (→ garbage fallback) and a **rigid template that forces KEY POINTS / ACTION ITEMS on every meeting** (→ invented filler).

**What changed - `LlmNoteWriter` is now map-reduce and adaptive:**
- **Map-reduce.** The transcript is split at speaker/segment boundaries into context-safe windows (`MaxChunkChars` sized from `LlmRuntime.ContextSize`). Each window is **mapped** to a compact factual digest; the digests are **reduced** into the final note. Summarising summaries never overflows - a 2-hour meeting works as well as a 5-minute one. (One-window meetings skip the map and read raw for best fidelity.)
- **Adaptive + honest.** The reduce step judges the meeting's substance and emits **only the sections that genuinely apply** (empty sections are already dropped by both renderers, so this is adaptive-by-omission - no schema change). A meeting with no real content gets a one-line honest note, never filler. The `NOTHING` escape is deliberately narrow (**greetings / small-talk / connection-checks only**) so a real-but-informal working session isn't wrongly dismissed.
- **Title from the whole-meeting summary**, not the opening line.
- **Verification:** new **`--renotes <meetingId>`** self-test re-runs the writer on a stored meeting's real transcript and prints the result. Confirmed against the two failing meetings - the 25-min one now yields *"Refine Weekend Attendance Tracking"* + a real whole-meeting summary/points/actions (80 s); the 5-min one yields an honest *"nothing substantive"* line (7 s). App notes timeout raised 150 → 300 s (map-reduce makes several short calls).

## M8.1 (ASR) - Pausey speech no longer shatters the transcript (2026-07-28)

A presenter reading slowly ("And… professors… Brown… University.") produced **one word per line** in the transcript. The M6.6 fix stopped mid-*word* shatter, but a >0.8 s pause *between* words still forced a new segment. Fix: a pause break now only fires once the line already has **≥ 8 words** (`MinWordsForPauseBreak`); sentence ends (`. ? !`) still break freely. Verified by re-transcribing the real clip → clean full-sentence lines. (This also fixed a knock-on notes false-negative: a shattered transcript looked like noise and got a wrongly-empty note.)

## Live transcription - the next milestone (Phase 2, planned)

The 25-min meeting spent **10 minutes transcribing after Stop** because Toki does one whole-file Parakeet pass at the end (and the sherpa-onnx build is CPU-only - no GPU ASR). Owner-approved plan: **incremental Parakeet during the meeting** - a voice-activity detector splits the live audio at natural pauses and each utterance is transcribed in the background, so **Stop → transcript is near-instant**. One recognizer feeds both a live preview and the saved transcript (memory-friendly for low-RAM PCs). The notes' "map" passes can then run *during* the meeting on capable machines for near-instant notes. This is the real fix for the multi-minute wait; it touches the audio pipeline so it needs a real meeting to verify by ear.

## M9 (BYO API key) - Optional cloud-speed notes, done securely (2026-07-29)

On-device notes keep every meeting private but are slow on a modest PC (see `mansplain/16`). This milestone adds an **optional** path: bring your own API key from a cloud provider you already pay for, and notes come back in seconds. It stays true to the $0-upkeep rule - **it's your key and your cost; Toki never pays and never takes a cut.**

**What shipped:**
- **Works with any provider, not just one.** Toki speaks the common chat-completions format, so the same setting works for **Claude, OpenAI, Groq, OpenRouter**, or a **local model** - and any OpenAI-compatible router. Claude works through Anthropic's compatibility endpoint with no special build.
- **Dead-simple, provider-first setup.** In **Settings → My API key** you: pick your **provider** from a dropdown (the web address is filled in for you), paste your **key**, then pick a **model** from a list Toki fetches live from that provider. Switching provider reloads the right models - nothing gets stuck. A searchable dropdown handles providers with hundreds of models.
- **Your key is protected, hard.** It's **encrypted on your PC** (Windows DPAPI, tied to your user account), stored only under `%APPDATA%\Toki\` - **never** in the project, never in plaintext, never in a log, and scrubbed out of any error message. It travels only over an encrypted (https) connection.
- **Honest connection status.** One clean line with a status dot: it only turns **green (with a gentle pulse)** after **Test connection** actually reaches your provider and gets a reply - it never claims to be working just because a key is present. One-tap **Remove key** wipes it and returns you to the free on-device model.

**Findings/fixes along the way:** an early version quietly built everything around Claude (auto-targeting it, defaulting the model to a Claude one) - corrected to be fully provider-neutral. A first attempt used an iOS-style scroll-wheel picker - replaced with cleaner dropdowns. And a self-inflicted one worth recording: the screenshot/render tool was reading the *live* settings and firing the model-fetch, which corrupted the settings file (and cost a saved key); the render harness is now fully offline/read-only (`App.Headless`), and the rule "never mutate real settings to take a screenshot" is written into project memory. Verified: 82/82 tests pass, no key material anywhere in the repo, clean 130 MB packaged build.

## M9.1 (Recording) - Record-a-demo toggle; Library un-ghosted (2026-07-31)

Toki's notch runs **ghost mode** - it's excluded from **every** screen recorder (Snipping Tool, Xbox Game Bar, **OBS**) and from screen-share. That's the privacy win (Toki never shows up in a meeting you're sharing), but it also means you can't record a demo of Toki. Two changes came out of the owner trying to record a walkthrough:

- **New "Show Toki in screen recordings & sharing" toggle** - Settings → **Recording & sharing**. **Off by default**, so Toki stays hidden and private; turn it on to record a demo, then turn it back off. Takes effect **immediately, no restart**. (The `--noghost` dev flag still works.) Under the hood it flips `App.GhostDisabled`, which swaps the window between `WDA_EXCLUDEFROMCAPTURE` and `WDA_NONE` live.
- **The Library is no longer ghosted.** Testing revealed the Library window was *also* excluded from capture - invisible in a recording **even after** turning the toggle on. It had been silently copied from the pill "for consistency," which the owner never asked for. Per owner decision it was **removed entirely**: the Library now records and screen-shares like any ordinary window. Trade-off (owner-accepted): if you screen-share with the Library open, its meeting list is visible to others. The pill's ghost is deliberately kept pill-only.
- **Still open (parked):** with the toggle on the notch *is* capturable, but Snipping Tool pins its own control bar to the top-centre of the recorded screen - right where the notch sits - so they overlap regardless of how many monitors you have. The clean fix for a top-centre notch is a recorder whose controls live in their own window (e.g. OBS on a second screen, capturing the notch's screen). Tracked for a later pass.

Verified: build clean, **82/82 tests pass**, new build launches cleanly (ghost affinity applied with no error in the log).

## M10 (Look, feel & templated notes) - 2026-08-02

A broad refinement pass across appearance, motion, and note quality.

- **Live light/dark theme.** A sun/moon toggle in Settings flips the *whole* app - notch and Library - between the signature dark and a clean, warm Apple-white, instantly, no restart. `ThemeManager` swaps a small palette of brushes that every screen references dynamically. (Two dead ends recorded in ARCHITECTURE - poking Color resources, and mutating brushes, which WPF freezes; the fix is to *replace* the brush.)
- **The notch is a tinted mirror.** The black and white notch are now semi-transparent, so the desktop colours behind them bleed through faintly - see-through, not blurred. Kept fairly opaque so text stays readable and it never washes out to grey.
- **Templated notes (8 shapes).** Toki auto-detects the meeting type - Standup, 1:1, Interview, Brainstorm, Lecture, Sales call, Decision log, or General - and writes the note in that shape (a standup gets Updates/Blockers; an interview gets Strengths/Concerns/Recommendation). Tasks are name-tagged to whoever they were actually given to (or "You"), never guessed; summaries are tuned to cut filler. Notes store flexible sections; older notes still render.
- **Smoothness.** Notch open/close is GPU-composited (an animated clip, no per-frame relayout); the Library grows out of the notch and retracts back to it; every scroller glides; maximise eases in.
- **Cloud mascot.** Toki is now the pink cloud from the product design (three humps + eyes), not a single oval.
- **Cleaner UI.** Theme + screen-recording controls are single header icons (shown once, not repeated under each notes-mode page); the recording icon is a camera crossed out when Toki is hidden. Deleting a note uses Toki's own rounded dialog, not the Windows box.
- **Housekeeping.** Removed the dead `Waveform` control and a never-firing notes-update event chain. The dev PC was found to be 11.4 GB RAM + RTX 3050 (not the old 6 GB) - noted for on-device headroom.

Verified: **83/83 tests pass** (incl. new template-detection and live-theme-swap self-tests), clean 130 MB build.

## P1 (Library → hub) - 2026-08-02

The Library window became Toki's **home** - a Claude-desktop-style hub. (Phase 1 of a larger plan; the chatbot is Phase 2.)

- **Sidebar** - the Toki cloud mascot up top (click it → a cute reaction that varies each poke), nav for **Ask · Library · Templates**, and **‹ Back to Toki** at the bottom.
- **Ask** (the landing) - a greeting that types itself in and rotates each open, a centered search bar with a **mic**, and quick suggestion chips. *(Phase 1: a query routes into the Library search; conversational answering over your notes + voice input arrive with the Phase 2 chatbot.)*
- **Library** - the meetings list + note detail, with a **drag-reorder toggle** (off by default, so cards never move by accident) and a **per-note template picker** (change the shape → Toki re-writes the note in it, off the UI thread).
- **Templates** - all 8 shapes with the sections each produces.
- Smooth page fades; theme-reactive throughout.

Verified: **83/83 tests pass**; hub pages render clean via a new `--libshot` dev flag. Fixed at the pre-commit review: the template-rewrite ran the model on the UI thread (would freeze the window) - now offloaded to `Task.Run`; `LlmService.CanWrite` no longer force-loads the model.

## P1.1 (Library note polish) - 2026-08-02

Copy/selection and layout fixes for the open note.

- **Whole note is selectable + copyable.** The note is now one rich-text document, so a single drag from top to bottom selects everything (summary → bullets → to-dos → transcript) and Ctrl+C copies it all - not one bullet at a time. **Copy** is a split button: the button copies the *entire* note; the **▾** offers everything / summary+to-dos / transcript / reveal the note's file. Proven by a headless `--copytest` (a mouse drag can't be screenshot-verified, so the test selects the whole doc and checks every part is captured).
- **Clean note layout.** Content is left-aligned and fills the pane at any window size (no centered column with a big left gap); the scrollbar sits at the window's right edge. Top toolbar: **Template** on the left, **Copy · Delete** on the right, above the title. **Delete** is a clear red, not a greyed-out look. Action-item name tags are bright (blue/amber) and readable on both themes.
- **Buttons truly centered.** Segoe UI Variable sat labels ~1px low; corrected in the button template (measured against a true-center line via the `--libshot` `buttons.png` probe). The "‹ Back to Toki" chevron uses a properly-centered icon glyph.
- **Resizable panels.** VS Code-style seams - invisible until hover, then a thin pink line - between the sidebar/list/note; drag to resize. Clicking **Library** while already on Library **collapses the note list** (animated); click again to bring it back.
- **Outlined inputs.** The Library search box and the Settings API-key / Address fields get a hairline outline so they no longer blend into the surface behind them.

Note: `NotesQa.cs` (retrieval for the Phase 2 chatbot) is new and untracked - add it when committing.

## P2 (Ask chatbot - fast + smart) - 2026-08-02

"Ask Toki" became a real assistant over your notes.

- **Answers stream in.** Words appear as the model writes them (instead of waiting for the whole reply) - the big perceived-speed win. Works for both the on-device model and a BYO API key.
- **Better, grounded retrieval.** A local **FTS5 full-text index** (BM25-ranked over title, summary, sections, transcript, and tasks) finds the most relevant meetings; the answer cites them as chips you can click to open. Falls back to keyword/recent if needed. Verified against the real library with a `--asktest` dev flag.
- **Instant task answers.** "What are my open tasks" is answered **straight from the database** (exact, no model, no hallucination). It's **scope-aware**: naming a meeting ("action items in the Postgres meeting") lists just that meeting's items; a bare "my open tasks" lists everything still open.
- **New chat + history.** A **+ New chat** button starts fresh; a **History** list reopens past conversations (auto-titled from the first question, deletable) - no more being stuck in one endless thread. Conversations persist locally in `chats.json`.
- A tighter prompt (context first, question last, today's date, short-answer constraint) keeps replies concise and on-topic.

Verified: **87/87 tests pass** (incl. new FTS retrieval + open-tasks tests); `--llmtest` confirms the model still generates cleanly through the new streaming path. *(Voice input in the Ask bar is the next slice.)*

## P2.1 (Ask - understand the question first) - 2026-08-02

The chatbot was giving confidently wrong answers to questions about **time** - because it searched by keyword and let the AI guess dates. Fixed by understanding the question *before* answering (like Granola/Hyprnote/Notion do):

- **Dates & recency are now handled by the database, not the AI.** "My most recent meeting" is a date sort; "meeting on July 30" / "last week" / "in March" are date filters (parsed offline by Microsoft's date recogniser). The AI is *told* the right meeting and only writes the answer - it can no longer pick the wrong one.
- **Instant, exact answers** for "how many meetings…", "what meetings did I have this week", and task lists - straight from the database.
- **Smarter task scoping** - "action items in the July 21 meeting" now lists *that* meeting's items (by date), not all 25.
- **Intent is judged on your question, not note titles** - so a meeting named "…Follow-Up" no longer triggers the task list.
- **Conversation memory** - follow-ups work: "before that" shifts the week, "what about the action items?" / "summarize it" stay on the meeting you were just discussing.

Verified: **107/107 tests pass**; new `--querytest` / `--followuptest` / `--asktest` dev flags prove the routing and follow-ups on the real library.

**Deferred - on-device meaning-search (embeddings):** planned as the next quality layer, but both ways to run a second small model on-device conflict with an engine Toki already ships (a native `onnxruntime.dll` version clash with the transcription engine; and the embedder *hangs* on the GPU/Vulkan backend that the notes AI needs). It was **not** forced in - that would risk transcription. The fix (run the embedder in an isolated CPU-only helper process) is scoped for the next iteration; the code is present but dormant and unused. Keyword search stays active meanwhile - the bugs above were about time/intent, which are now fixed. See ARCHITECTURE.md for the technical detail.

## P3 (UI polish - notch behaviour + Library refinements) - 2026-08-02

A pass focused entirely on how it *feels*, driven by a short set of design rules (dividers-not-boxes, selection-isn't-an-outline, motion is brief, professional colour, one clean scroll from the top). No new features.

**Notch behaviour**
- **Dropdowns no longer shut the notch.** When the model/provider list drops *below* the notch, it now counts as part of the notch - you can scroll and click an option without it retracting mid-pick. Clicking the dropdown again closes it (fixed a Windows toggle quirk).
- **Cleaner open.** The reveal is clipped to zero height *before* the window shows, removing a one-frame flash on hover-to-open.

**Library**
- **Airy note list** - rows are open (no boxed tiles); only the selected note carries a soft fill, hover shows a subtle one. The bright divider line is gone (a faint grey seam appears only while you drag to resize).
- **Normal top-to-bottom scroll** - the whole note (actions, title, body) is one scroll filling the pane, so the scrollbar runs the full height from the very top.
- **Small "＋ New note" and a book "My notes" filter** by the Library title; the filter shows only your hand-written notes.
- **Title & date are selectable/copyable**, and a freshly opened note starts at the top.

**Settings**
- **Connection status redesigned** - dropped the pulsing dot + status card; one quiet line under *Test connection* (green "Connected" when verified), and the button itself confirms.

**Colour** - Delete is a cleaner crimson shown as red *text* on the neutral button (no loud red pill); the red fill is reserved for the confirm dialog.

Verified by rendering the Library (`--libshot`) and **107/107 tests pass**. Known-open: a small transient glitch to the left of the mascot during hover-to-open - being tracked; needs a live capture to pin down.

## P4 (Ask by voice - on-device) - 2026-08-02

The Ask bar's mic is live. Click it, speak, click again - Toki transcribes your question **on-device** (the same Parakeet speech model already used for meetings, warmed at launch) and drops the text into the box for you to glance over and send. Nothing leaves the machine; no extra download. It's user-initiated and mic-only - not always-listening (that's the later "Hey Toki" carve-out).

- **Auto-stops when you finish** (Google-/Safari-style): once it's heard you speak, it ends on a generous ~1.8 s silence, transcribes, and fills the box - no need to tap stop (you still can). Gives up gracefully if nothing's said, and hard-caps a single utterance.
- **Live feedback**: the input box shows a pulsing coral **"Listening…"**, then a pink **"Transcribing…"** loader while it thinks - the mic glows coral throughout.
- Fills the box for review rather than auto-sending, so a mis-hear never fires the wrong question.
- Degrades kindly: a clear hint if the speech model isn't installed or the mic can't be reached.

Verified end-to-end with a new `--voicetest <wav>` dev flag (synth a clip → `TranscribeFile` → text); the live mic capture, auto-stop, and loader are confirmed by ear/eye. 107/107 tests still pass.

Also this pass: the Library note list renders **edge-to-edge on one background** (no inset box / padding strip), the note toolbar's **Template** picker works inside the scroll (rebuilt as a real button + themed menu), and the VS Code pane resizer was removed.

## P5 (Mascot redesign + press-to-talk hotkey) - 2026-08-02

**Toki looks like Toki now.** The flat, sharp cloud was redrawn as the soft, dimensional character from the product design - a glossy light crown fading to a deep pink base, cheek blush, glossy eyes, a gentle lift-shadow. It's one control, so it upgraded everywhere at once (the notch peek, Home, the Library sidebar).

**Toki feels alive.** The corner mascot plays a *different* reaction every time you poke it - squish, hop, wobble, wink, spin, big-eyes, nod - and there's a springy "drop-in" hello. Idle breathing and blinking continue underneath.

**Hotkey = press-to-talk.** `Ctrl+Alt+Space` now opens Ask and starts listening straight away - a silent, always-reliable way to ask by voice. There is **no always-on mic and no wake word**: the owner chose press-to-talk only, so Toki still only listens when you ask it to (Hard Constraint #3 stays fully intact). The "Hey Toki" wake word was researched (sherpa-onnx keyword spotting) and deliberately dropped.

107/107 tests pass; mascot verified by render, voice/hotkey by ear.

## P6 (Push-to-talk on the notch) - 2026-08-02

`Ctrl+Alt+Space` is now **hold-to-talk**, Wispr-Flow style: **hold** it and Toki drops down from the top into a listening notch (Toki + a pink waveform); **speak** while holding; **release** and it thinks, then the answer streams onto the notch. The mic runs **only while you hold the key** - there's no always-on listening and no wake word, so Toki still only listens when you ask it to.

- New `PushToTalkHook` (a low-level keyboard hook) sees the key *release* that `RegisterHotKey` can't, so hold/release works.
- New `AskView` notch flow: listening (waveform) → thinking (loader) → answer (selectable, scrolls); the notch grows to fit each phase. Esc / × / a 30 s timeout dismiss it.
- Reuses the on-device Parakeet transcription and the Ask engine - nothing new leaves the machine.
- Full polished Q&A-on-the-notch is a future refinement; this is the working first cut.

107/107 tests pass; the listening notch verified by render, the live hold→speak→answer flow by ear.

## P7 (Toki becomes an assistant) - 2026-08-02

The push-to-talk notch is now a proper quick-access assistant, built on deep research into how the best assistants (Claude, ChatGPT voice, Perplexity, Raycast, Granola, Apple App Intents, Copilot/Dia) behave.

- **Quick-view answers.** The notch answers in 1–3 short, personal sentences - "a sharp colleague who was in the room," not a report. Brevity is enforced in code (≤280 chars, cut on a sentence boundary); when an answer is genuinely long, Toki gives the one-line takeaway and a **"See full answer in Library →"** button that jumps there. The Library keeps giving fuller answers.
- **Assistant commands.** "Take me to the library", "open settings", "new note", "go home", "close", "help" - Toki just does them. A deterministic router runs first, with a question-veto so "what did we decide in the *settings* review?" is still *answered*, never mis-fired as a command. (35 adversarial tests.)
- **Screen-aware.** Ask "what were the action items?" while looking at a meeting's notes and Toki answers about *that* meeting - because it reads its *own* window state (which note is open), never your screen. No screenshots, no OCR, nothing leaves the machine - a privacy story Copilot/Recall can't tell.
- **Session memory.** The notch remembers the thread while it's open (follow-ups like "and who owns them?"), and starts fresh when you close it.

142/142 tests pass.

## P7.1 (Assistant polish) - 2026-08-02

Refinements to the notch assistant from real use:

- **Words render in patches**, not letter-by-letter - both the notch and the Library Ask buffer tokens and flush in ~70 ms chunks.
- **No ugly cut-back.** A long answer used to stream out in full then snap down to a short one. Now the notch stops generating at a clean sentence boundary once it's past the glance budget, so what you see is what you get - with the "See full answer in Library" button for the rest.
- **Adjustable notch.** The answer panel starts as small as its content and grows to fit as words arrive, then scrolls past a cap.
- **Conversation thread.** The notch now keeps the whole session as a scrollable thread of questions and answers (until you close it), with follow-up memory.
- **Natural commands.** "Pull up my library", "jump to settings", "let me see my notes" - not just one hard-coded phrase. (Questions still never mis-fire - 155 tests.)
- **Screen-aware actually works** - "what were the action items?" grounds on the meeting you're viewing.
- **The Library always pops up** - from closed, minimized, or behind - and a "full answer" hand-off opens a fresh conversation there.

## P8 (Ask Toki redesign - the "it's dumb" fixes) - 2026-08-02

Five verified live-session failures were root-caused in the code and fixed deterministically - the full story (root causes, research, and the owner live-test script) is in [`docs/ask-toki-redesign.md`](docs/ask-toki-redesign.md).

- **A person question isn't a meeting list.** "What was discussed about Kai Sinat in my meetings?" now *searches for Kai Sinat* (and says honestly when nothing's there) instead of dumping "your 10 most recent meetings". Retrieval also queries only the words that carry meaning, so filler like "discussed"/"meetings" can't drag in random notes.
- **"This meeting" means the note on your screen - reliably.** The "what's open right now" state is now *derived* from the real window state instead of a cached value that went stale (that's what answered about the wrong meeting). The notch also shows a small "About: <note title>" line whenever it answers about the open note, so you can *see* what it grounded on.
- **"What did Mac have to do?" filters by Mac.** Names are recognised against the owners that actually exist in your action items (first names and "Mac's" included), on every task question - with an honest "no items for Mac here; those are owned by Sid" when that's the truth.
- **Toki answers for itself.** "Why are you so rude?", "do you see my screen?", "is my data private?" get short, warm, in-character replies - never a search of your notes ("there's no mention of rudeness in the meeting notes" is gone). The screen one names the open note honestly.
- **Tighter voice.** The sampler now matches the Qwen3 model card exactly, prompts carry an example answer in the target "sharp colleague" voice, and quick answers stop cleanly at the end of the answer instead of overrunning and being trimmed.

228/228 tests pass - every screenshot bug is now a permanent regression test. Final tone check is the owner's live run (script in the doc).

## P9 (Ask Toki - the reasoning route, "me", and the full command set) - 2026-08-02

Full story in [`docs/ask-toki-reasoning.md`](docs/ask-toki-reasoning.md).

- **"Assigned to me" now knows who "me" is.** Self-questions filter to the items owned by *you* ("You" in the notes), with an honest "nothing here is yours - these belong to Lily and Mac" when that's the truth. Multi-owner items ("Lily, Mac") now match each named person too.
- **Thinking questions get a thinking pipeline.** "What should I focus on?", "themes across my meetings", "how is X progressing?" now read up to six related meetings (not three), with a conclusion-first reasoning prompt and four times the answer budget - instead of being forced through the one-glance lookup pipe.
- **A whole command vocabulary.** "Start taking notes", "switch to the api/on-device/heuristic model", "switch to white/dark theme", "ghost mode on/off", "open templates", "open the assistant" - and "take me to the library" now lands on the actual notes list. All question-vetoed, all instant.

300/300 tests pass.

## P9.1 (Ask Toki refinements - the wake-name cluster) - 2026-08-03

Full story in [`docs/ask-toki-refinements.md`](docs/ask-toki-refinements.md).

- **Toki recognises its own name however the mic mangles it** (Doki/Toky/Docie/Toku/Tokyo…) - so spoken commands and "I love you, Toky" stop falling into notes retrieval (where the model *invented* answers).
- **"Which meeting was it?" is answered from the database** - oldest/first/most-recent (+ title) exactly; a date with several meetings lists them all instead of silently picking one; a topic that matches a meeting *title* ("the meeting regarding Amazon") is found deterministically.
- **Friendly app names + no more clipped notch** - "Slack is in a call. Record it?", measured *after* the text is set so the prompt always fits.

389/389 tests pass.

## P10 (The dialogue brain - Toki learns to talk) - 2026-08-03

The owner's verdict after live use: still "not smart - like the first version of ChatGPT". The root cause was architectural - **every** utterance was force-fit into meeting-notes retrieval ("what is my name" was *searched in the notes* and answered with citations; a misheard "switch to like theme" was "answered" with a fabricated link to an unrelated meeting). P10 rebuilds the orchestration into a real dialogue manager - full design, research and live-test script in [`docs/ask-toki-brain.md`](docs/ask-toki-brain.md).

- **Toki knows who you are.** "Call me Anirudh" is remembered (persisted - the greeting uses it too); "what's my name / who am I / what do you know about me" answer from that memory, never a search.
- **Toki can just talk.** Turns that aren't about your meetings - chit-chat, world knowledge, "I'm stressed about tomorrow" - go to the model's *own* knowledge with the conversation as context. No retrieval, no citations, no "not in your notes".
- **Meetings stay grounded.** Everything P8/P9/P9.1 built is preserved as the meeting branch, entered when the turn is actually about the meetings (wording cues, dates, screen, or real evidence in your notes).
- **Zero fabrication.** A retrieval with no matching content is an honest miss (never "analyse 5 random meetings"); an answer that admits a miss carries **no** source chips; a garbled command gets "did you mean the light theme?" instead of an invented notes answer.

428/428 tests pass. What tests can't judge - how *natural* the conversation feels - is the owner's live run (script in the doc).

## M7.0 (Google Calendar - the foundation) - 2026-08-03

The plumbing that connects Toki to Google Calendar, built to carry the calendar features that come next
(notch preview, prep cards, meeting creator). Owner setup guide: [`docs/google-setup.md`](docs/google-setup.md); decisions in `ARCHITECTURE.md`; plain-language story in [`mansplain/24-connecting-your-calendar.md`](mansplain/24-connecting-your-calendar.md).

- **Sign in, direct to Google.** Standard installed-app OAuth: **loopback redirect + PKCE**, the browser
  talks straight to Google, tokens land on the user's PC. No Toki server - hard constraint #1 intact.
- **Least privilege.** One scope pair - `openid email` + `calendar.events` (read **and** create, so the
  meeting-creator needs no second consent). No Gmail/Drive/contacts.
- **Secure at rest.** Only the long-lived refresh token is stored, **DPAPI-encrypted** (per-Windows-user,
  reusing the API-key pattern); access tokens live in memory and refresh lazily. Tokens are never logged.
  Verified: `settings.json` holds an opaque DPAPI blob, zero plaintext token.
- **Lean by choice.** No Google SDK - three REST endpoints hand-rolled over the shared `HttpClient` in the
  `ApiLlm` house style, so the single-file publish stays small.
- **New `Calendar/` module** - `GoogleAuth` (PKCE/loopback/exchange/refresh/revoke), `CalendarClient`
  (events list/insert/delete), `CalendarModels`, `CalendarService` (the facade every UI surface uses).
  Wired into the Home "Connect Google" button and a Settings "Google Calendar" connect/disconnect section.
- **Proven live**, not just compiled - `--caltest connect | list | insert | disconnect` ran against a real
  Google account: signed in, read the next 7 days, created+deleted an event, auto-refreshed the token. One
  real bug caught and fixed (an un-encoded `+` in the `timeMin`/`timeMax` timestamps → HTTP 400 → now
  `EscapeDataString`d).

446/446 tests pass (+18 calendar: PKCE correctness against the RFC 7636 vector, event-JSON parsing across
timed/all-day/cancelled/Meet-link shapes, id_token email decode). Ship-side follow-up: Google's free
**sensitive-scope verification** (privacy-policy page + demo video) before selling, to drop the
"unverified app" warning - queued with the M8 attribution/licensing work.

## M7.1 (Calendar UI - the surfaces) - 2026-08-03

The visible calendar, built on the foundation's `CalendarService` facade. Design matches
`Toki-ProductDesign/③ Home` exactly; plain-language story in
[`mansplain/25-calendar-in-the-notch.md`](mansplain/25-calendar-in-the-notch.md); decisions in `ARCHITECTURE.md`.

- **Home redesign (connected):** a two-up **Meeting** + **Take Notes** row, a **"COMING UP"** list (date
  badge · title · time · **Join**, with an amber "in N min" treatment for imminent meetings), and an
  account footer with a live green dot. Not-connected keeps the Connect-Google CTA.
- **Calendar page** (`PillState.Calendar`, 392×472): an **agenda-by-day** week view (the right form for a
  narrow surface - not a cramped 7-col grid), today picked out in amber with a live **"now" line**, week
  nav (this week + 3, no fake past), Join, and a per-event **…** menu (Join · Edit · Open in Google · Delete).
- **Create - natural language, one box:** type "Design sync tomorrow 2pm with ana@work.com"; a live green
  preview shows the parsed time; chips for duration / Google Meet (default on) / people. Backed by the
  already-shipped `Microsoft.Recognizers` (future-preferred) in `QuickEventParser`.
- **Edit** reopens the same composer prefilled (Save mode) via a new `events.patch` (`sendUpdates=all`).
- **Delete - safe but fast:** instant + an **Undo** bar for solo events; a one-tap **confirm** when guests
  would get a cancellation email (that one isn't reversible).
- **Provider-neutral by construction** - views touch only `CalendarService` + the models, so Outlook
  drops in later with no UI rewrite (`CalendarService.ProviderName` is the one naming seam).
- New: `Calendar/UpcomingCache` (stale-while-revalidate, shared by Home + Calendar), `Views/CalendarRows`
  (shared row builders), `Views/CalendarView`, `Views/NotchMenu`, `Calendar/QuickEventParser`,
  `Dev/CalendarSamples`. Self-tests: `--caltest update`, `--caltest parse`.
- **Verified:** all screens render-checked (connected Home, week view, create composer with live preview);
  create / **edit** / delete / parse run live against a real Google account (edit = insert→patch→verify→delete PASS).

450/450 tests pass (+4 natural-language parser: title/time/email extraction, 30-min default, no-time, past-rolls-forward).

## M7.2 (Pre-meeting prep cards) - 2026-08-03

The calendar earns its keep: ~5 min before a real meeting, Toki auto-surfaces a prep card fusing the
calendar (what's next) with the notes store (what happened last time + what's still open). Design matches
`Toki-ProductDesign/④ Assistant - prep card`; story in [`mansplain/26-prep-cards.md`](mansplain/26-prep-cards.md).

- **Content (100% deterministic - no LLM, instant, zero extra RAM):** title + amber "in N min", **LAST
  TIME (N days ago)** recap (the matched meeting's TL;DR), **STILL OPEN** action items with amber owner
  tags (tickable - writes through to the store), **AGENDA** parsed from the calendar invite's description.
- **Matching is honest:** a `CalEvent` links to a prior meeting by **title/topic** (`TitleMatchIds` →
  `SearchRelevant` + a shared-title confidence bar) - never by attendee (notes don't store emails). No
  confident match → an honest **first-time card** ("First time with this one - I'll take notes if you
  record"), never an invented recap.
- **Trigger (`PrepWatcher`):** a 30 s calendar poll raises the card in a 5-min lead window
  (`TOKI_PREP_LEAD` override), **restrained** - real meetings only (link or other guests), **once per
  meeting-instance ever**, only while the notch is idle (Hidden/Collapsed), never during a fullscreen
  app/presentation, "Later" suppresses it, and it auto-retires if you walk away. Never records on its own
  - "Join and record" is the human click (Hard Constraint #3).
- New: `Calendar/PrepBuilder` (deterministic assembly + matching + agenda parse), `Calendar/PrepWatcher`
  (the trigger), `Views/PrepCardView`, `Dev/PrepSamples`; `CalEvent` gained `Description`;
  `PillState.PrepCard` (auto-height 356×306). Self-test `--preptest`; demo `--state PrepCard`.
- **Verified:** matched + first-time cards render-checked in light **and** dark (near-exact to design ④);
  `--preptest` proves title-match / open-item pull / unrelated-event rejection / agenda parse; live
  `--state PrepCard` runs without crashing (the ctor-grow recursion guard from the Home fix applied here too).

450/450 tests pass. Owner to confirm live: the auto-surface-vs-hover choice, and the real T-5 pop-up +
"Join and record" against a live meeting.

## M7.3 (Voice-create a meeting) - 2026-08-03

Say "schedule a call with Ana tomorrow at 3" on push-to-talk → Toki confirms, then creates it. Story in
[`mansplain/27-voice-create.md`](mansplain/27-voice-create.md); routing detail in ARCHITECTURE.

- **A new command-tier intent in the dialogue brain** (`Llm/CalendarCommand.cs`), slotted into
  `NotesQa.AskStreamAsync` **before** retrieval/general-chat (a create is never answered from the notes)
  and after the safety/identity tiers. Deterministic detection (no LLM): a tight scheduling-verb + event-
  noun / "put … on my calendar" / "remind me to" shape, with the **question-shape veto reused from
  `CommandRouter`** so "when is my call with Ana" / "what meetings do I have" stay READ turns.
- **Confirm before booking (the point):** turn 1 never creates - it reads back "Create 'Call with Ana' -
  tomorrow, 3:00–3:30 PM …?" and holds a single-shot `ConversationContext.PendingCreate`; "yes" creates
  once, "no" cancels, and a **stray "yes" later can never create** (pending is cleared up front). No time
  → asks "when?". Not connected → honest, books nothing. A mishearing costs one "no".
- **Honest about people:** a spoken "with Ana" is a name, not an email - it goes in the title (+ a
  description note), never a fabricated invite. Reuses the shared `QuickEventParser` + `CalendarService`.
- **Parser fix (helps the typed composer too):** a bare hour like "at 3" now resolves to the **business-
  hours reading (3 PM)**, not 3 AM; explicit "9am"/"8pm" are respected.
- **No UI changes** - the confirm/result flow rides the existing Ask streaming path on both the notch and
  the Library. Self-test `--voicecreatetest` (detection matrix + the confirm gate, no mic/model/network).

472/472 tests pass (+22: detection positives/negatives, title/time/person extraction, and the full
confirm-gate matrix incl. stray-yes and no-time). Owner to confirm live: hold-to-talk → speak → "yes" →
event on the real calendar.

## M9.0 (Premium redesign - pass 1 + glassy notch) - 2026-08-03

First pass of a taste-driven redesign (owner: the UI read "AI-slop"), researched via Mobbin + premium
references (Wispr Flow, Linear, Vercel, Cron…). Story in `mansplain/`, decisions in ARCHITECTURE.

- **Design tokens** (`Theme.xaml`): a restrained type ramp (was 18 ad-hoc sizes → named `Title/BodyStrong/
  Secondary/Caption/Micro`; section headers SemiBold not heavy Bold). Reused across the touched screens.
- **Redesigned checkbox** (`TokiCheck`): 18px, correct 12px tick (no more stretch-distortion), **green** fill
  when done (frees coral for record only), spring-pop on check. Plus **`TokiSwitch`** - a real toggle
  replacing the bare Win32 checkbox in Settings.
- **Notes**: colour-coded section headers (decisions green / actions blue / questions purple, per design ⑤)
  and left-aligned text (justify made rivers in a narrow column).
- **Calendar composer**: flattened (was box-in-box-in-box → a section under a hairline), clean outlined
  **chip toggles** (`ChipButton`) - the "Google Meet ✓" text-slop is now a green-filling toggle.
- **Home**: "Meeting" → **"Calendar"** (it opens the calendar, not starts a meeting); one-accent icons
  (coral mic on Take Notes, monochrome calendar).
- **Fixes**: typing in the create composer no longer auto-tucks the notch (idle-tuck now respects focused
  input); a bare "at 3" resolves to 3 PM (business-hours reading) in the shared parser.
- **Glassy notch toggle** (Settings → Appearance): a **real DWM acrylic blur-behind** that frosts whatever's
  behind the notch (not a tint), theme-aware (dark/white frost, re-tints on live theme swap), one consistent
  size (glass = opaque size, no growth), flush square top (window extends above the screen edge so DWM's
  top-corner rounding hides off-screen), rounded bottom, no rim. Off by default. See ARCHITECTURE for the
  full interop (why true blur can't coexist with a 22px corner, and every constraint solved).

472/472 tests pass. **Still to build (redesign pass 2):** the Wispr/Vercel hover-expand "section pills"
(rows growing on hover to reveal actions), the remaining type/spacing sweep (Library + small states), and
the motion-consistency pass.

## M9.1 (Calendar page revamp) - 2026-08-04

The Calendar page read too big and busy (three arrows, week paging) and flickered on refresh.

- **One continuous agenda** replaces week paging - the upcoming cache already holds ~31 days, so the
  two week arrows were artificial. Now a single scroll with **week bands** ("This week" / "Next week" /
  date range) separating it; only the **back** arrow remains.
- **Scroll-synced subtitle** (header updates to the week you're viewing + its count) and a **Today pill**
  that fades in only once you scroll away.
- **Smooth**: a render-key skips the rebuild when nothing changed (the 60s tick + multi-source refresh
  no longer re-stagger identical rows), and the entrance animation runs **once**. Fixed-height page with a
  real internal scroller (an earlier auto-height attempt fought the ScrollViewer → no scroll); smaller at
  360×430. Coral **now-line dot**, mascot empty state ("＋ Plan something") that reacts on hover.
- **Theme fix**: the render-key carries the theme and the page listens for theme swaps, so the code-built
  rows recolour on a light↔dark toggle (they were staying black-on-dark before).

## M8.0 (Attribution - the "Built with" screen) - 2026-08-04

The licence-obligation screen - Toki cannot be sold without crediting the open models it ships (Parakeet is
**CC-BY-4.0**). First real step onto the ship runway.

- **`AboutView`** (`PillState.About`), reached from **Settings → About → "Built with - open source &
  licences"**. Scrollable credits list, design-system native (type ramp, dividers, amber/neutral chips).
  *(v1-iter3 moved the entry point to the **Library window's Settings page** when the notch Settings became
  UI/visibility only - the screen itself is unchanged.)*
- **MODELS**: Parakeet TDT 0.6b v2 (NVIDIA · **CC-BY-4.0**, with an "int8 quantised" note to satisfy the
  modification clause), Qwen3-4B-Instruct-2507 (Alibaba · **Apache-2.0**), bge-small-en-v1.5 (BAAI · MIT).
- **ENGINES & LIBRARIES**: sherpa-onnx (Apache-2.0), ONNX Runtime, LLamaSharp · llama.cpp, NAudio, SQLite,
  H.NotifyIcon, SharpCompress, Recognizers-Text (all MIT). Each row opens its upstream source.
- Attribution-required licences (CC-BY, Apache) get **amber chips**; MIT stays neutral. **Whisper is
  deliberately omitted** - it's a stubbed error-fallback, not shipped, so crediting it would be false.
- Repo **`THIRD-PARTY-NOTICES.md`** holds the full, authoritative attributions + licence links.

472/472 tests pass. **Still to build (M8):** signed installer + auto-update, licence-key gate, onboarding
download polish.

## H1 (The hardening pass - audit, fix, verify) - 2026-08-04

With the feature set settled, the whole app went through a production-readiness pass: a deep
multi-agent audit of every subsystem (each finding adversarially re-verified against the code before
it counted), plus market research across the top consumer notetakers to calibrate quality. 67 real
issues confirmed; every high- and medium-severity one fixed, and all but three deliberate deferrals
of the lows.

**Built / fixed (highlights)**
- **A recording can never run invisibly.** Recording truth now lives in the audio session itself, and
  every way of dismissing the notch keeps a live recording's bar on screen. Push-to-talk or Esc during
  a meeting previously could leave the mic running with no indicator and lose the meeting.
- **Delete really deletes.** Removing a meeting from the Library now also removes its audio files and
  Markdown mirror - which the confirm dialog had promised all along.
- **Crash recovery.** WAVs stay playable through a crash (headers finalized every few seconds), and on
  the next launch Toki finds any recording that never became a meeting and turns it into one. Verified
  live: it recovered a real meeting from a 3-week-old orphaned folder on this PC.
- **Unplugging your headset mid-meeting no longer kills the recording** - capture rebuilds on the new
  default mic and keeps writing the same file, converting formats on the fly.
- **Detection fixes:** the denylist no longer matches install paths (a Windows username containing
  "obs" used to disable detection entirely), and new Teams now resolves to its real process, so its
  meetings capture both sides.
- **Stability:** the low-memory model-release race that could crash the app mid-answer; API streaming
  no longer dies at 60 seconds on slow local models; calendar requests can't silently truncate the
  agenda (real pagination) or vanish on timeouts; settings/chat files are written atomically (a crash
  mid-write used to wipe the API key and Google connection).
- **Security/privacy:** calendar "Join" links only open real web URLs; the OAuth listener shrugs off
  bogus local requests; the SharpCompress CVE was patched; the diagnostic log is size-capped; orphaned
  voice snippets are swept.
- **Quality:** meeting prompts wait until you leave fullscreen/presentation mode; the notes prompts now
  read past speech-recognition errors without inventing names and pair every decision with its stated
  reason; if the API key is missing, notes fall back to the on-device model instead of plain heuristics.

**Verified**
- 472/472 unit tests green. Self-tests all pass on this PC: `--dbtest`, `--notestest`, `--querytest`
  (10/10), `--themetest`, `--copytest`, `--caltest parse`, `--preptest`, `--voicecreatetest`,
  `--detecttest`, and `--llmtest` - real notes from the real Qwen model through the new fallback path.
- All 17 views rendered clean in both themes (`--render` / `--theme light`).
- `dist` republished (132 MB, healthy), smoke-launched, warm-up + state machine verified in the log -
  including watching the recovery sweep clean 4 dead folders and save 1 recovered meeting, live.

## H2 (Assistant scoping fix, Library sort, security hardening) - 2026-08-04

Owner-reported: asking "what tasks do I have for this week" listed every open task. Root-caused, fixed,
and hardened alongside a Library sort menu and a security/testing pass - all research-validated.

**Assistant answer quality**
- **Task time-queries now scope by DUE DATE, not meeting date.** "What's due this week / Friday /
  overdue" was scoping to *meetings held* in the window - which on a recent library is almost every
  meeting, so it dumped everything. Now it resolves each task's free-text due ("Friday", "next week")
  against its meeting's date and buckets open tasks into **Overdue / Due-this-window / undated-from-this-
  window**, with the window echoed in the answer ("2 tasks this week:"). This is the market-validated
  behavior (task managers scope by due date; the undated fallback is disclosed).
- "What's due Friday", "what's overdue", "anything due tomorrow" now route to the task list (they used
  to fall through to a generic lookup).
- Bare weekday deadlines are future-biased ("due Friday" set on Monday means *this* Friday, not last).
- 15 new tests (unit + end-to-end through the store seam) lock the behavior in.

**Library sort/filter** - a menu on the list: Newest / Oldest / Longest / Shortest / Most open tasks /
Manual (drag order). Persisted; drag-reorder auto-selects Manual so it never renumbers a sorted view.

**Security & testing** (from a full threat-model audit)
- **Single-instance guard hardened** against local named-object squatting: the Mutex/EventWaitHandle now
  use per-user (SID-salted) names with a current-user-only ACL, and "already exists" is verified against
  a real running Toki before deferring - a squatter can no longer DoS startup or drive the UI.
- **One shell choke point** (`SafeShell`): every "open link / reveal file" goes through it; only http/https
  URLs (and validated in-app paths) ever reach Windows - a poisoned calendar join-link can't launch
  `file:`/UNC/`ms-msdt:`.
- **One FTS query builder** (`ToFtsQuery`) so no search text can ever reach a MATCH clause un-sanitized.
- **Path-containment guard** on the Markdown mirror (a title can never escape the Notes folder).
- **Prompt-injection delimiters** around meeting notes fed to the model ("treat as data, not instructions").
- OAuth loopback socket set `ExclusiveAddressUse`; confirmed no TLS-validation bypass and no
  `BinaryFormatter` anywhere.
- **NuGet vulnerability auditing turned on - and it immediately earned its keep:** it surfaced a
  previously-invisible **high-severity advisory in the bundled native SQLite** (`SQLitePCLRaw` 2.1.6),
  now patched to 2.1.12 (verified: the DB round-trip still passes). A critical advisory in
  `NuGet.CommandLine` (a build tool pulled in by the date parser, never shipped in the app) is suppressed
  with a documented reason so real runtime advisories still surface.
- 33 new security/pagination tests.

**Verified** - 520/520 unit tests; self-tests all pass (`--dbtest`, `--notestest`, `--querytest` 13/13,
`--voicecreatetest`); single-instance guard runtime-verified (2 launches → 1 process); Library sort
render-verified; `dist` republished (132 MB) and smoke-launched.

## H3 (Assistant brain - 1000+ prompt tests + rework) - 2026-08-04

Owner asked for a stress-test-driven rework of the smart assistant: research the full space of questions
users ask, build a large prompt-test suite covering every category, and fix what it surfaces - LLM/brain
only. Two deep-research passes produced an exhaustive question taxonomy (temporal, task, person, topic,
analytical, counting, follow-ups, meeting-id, commands, small-talk/persona/privacy, general knowledge,
ambiguous, adversarial - with market-validated disambiguation rules and an ASR-homophone table).

**The suite: 1,155 deterministic prompt-test cases** (total project tests: 520 → **1,636**), CI-safe (no
model/audio/GPU). Built on a seeded 7-meeting fixture + a frozen clock + a `RouteOf` classifier that runs
the real routing predicates in pipeline order, so a question's turn-type, intent, scope, and honesty
behavior are all asserted without loading the model. Categories: turn-type routing, temporal windows,
task provenance-vs-due-date, end-to-end honesty behavior, person/topic/counting/identity, follow-up
memory, wake-name ASR variants, and a route-invariance perturbation overlay (casing/punctuation/address).

**Reworks the suite drove (brain only):**
- **"What tasks did I have on August 2nd" no longer dumps every overdue task.** A specific past date (or
  past-tense recall) now means PROVENANCE - the tasks that came out of that day's meetings - while
  "due this week / Friday / overdue" stays a DEADLINE view. This is the market-validated split
  (`TaskScheduling.ModeFor`), and it's the exact case the owner caught.
- **Privacy questions are now answered accurately and never sent to retrieval.** "Are you sending my data
  to the cloud?", "are you always listening?", "is this private?", "do you train on my data?" were
  falling through to the notes search (which could give a wrong or fabricated answer) - a real trust bug.
  They now get a precise, honest canned answer: everything's local, nothing's uploaded, no always-on mic,
  and the BYO API key is the only opt-in path off-device.
- **General-knowledge questions stop falsely grounding** on an incidental word match ("write me a haiku
  about mondays" no longer latches onto a "Monday standup"): a definitional/how-to/creative shape is
  answered parametrically.
- **Broader, more natural phrasing coverage:** counting now includes calls/syncs/notes; "what's on my
  plate", "what does Ana owe me", "on his plate", deadline words route to the task list; "start a new
  chat", "i go by Sam" recognised; recurring/common analytical words no longer leak into retrieval.

**Verified** - 1,636/1,636 tests green; self-tests all pass (`--querytest` 13/13, `--dbtest`, `--notestest`,
`--voicecreatetest`); the exact reported bug proven fixed end-to-end; `dist` republished (132 MB) and
smoke-launched. No non-brain code touched.

## v1-iter2 (Owner-driven fix + feature + polish sweep) - 2026-08-04

A long live-testing session with the owner: fixing the bugs they hit as they used the app, adding the
features they asked for, and starting the UI revamp. Each item built → `dotnet test` green → `dist`
republished → owner verified on the real PC. **1,657 tests green** at the end.

**UI / notch stability (the "ugly to use" regressions)**
- **Scroll-up glitch fixed** - the Ask thread (notch + Library) force-scrolled to the bottom on every
  streaming token, yanking you back down when you scrolled up. Rebuilt as **stick-to-bottom**: it
  auto-follows only while you're already parked at the end; a new turn re-pins. Threshold widened to
  ~120px (research §3a; 8px was hair-trigger).
- **Text cut-off + open/close stability fixed** - root-caused with a new headless `--measuretest`
  (measures each auto-height view's true arranged height, no RenderTargetBitmap so it runs in CI/sandbox
  where the render harness hangs). The content measured fine; the last line just sat flush against the
  rounded bottom with ~0px slack → looked clipped, and DPI rounding tipped it into a real clip. Fix: a
  **14px bottom breathing pad** on auto-height pages + a **clip-generation guard** so a late-firing
  reveal/conceal animation can't clobber the current clip or hide a just-reopened notch (the "open/close
  a million times → corruption" bug).

**Onboarding + name**
- **First-run onboarding** (`PillState.Onboarding`, notch-native, 3 steps: name → privacy-as-pitch → all
  set → Home). Gated on a new `OnboardingComplete` flag so it fires once for every install - which also
  corrects a stale stored name. `AboutView`-style wiring.
- **Name moved to a Library-window Settings page** (owner decision - it's passive, not something you
  change often): a 4th "Settings" nav tab with a name field, **Confirm/Cancel**, a green **"✓ Saved"**
  reinforcement, and **live propagation** (fixed the bug where the Home "Welcome back" greeting cached its
  text at construction and never refreshed - it now re-reads the name every time Home is shown). Removed
  the notch Settings name field.

**Assistant / voice**
- **Misheard command → clarify** - a command-shaped mis-hear ("start reporting" for "start recording")
  now gets a short clarification instead of a bad Q&A answer (`CommandRouter.TryNearMiss` broadened to
  recording verbs). An earlier auto-**re-listen** (a second mic) was tried and **removed** - it grabbed
  the audio device and starved the next push-to-talk, so voice never answered (the endless "I didn't
  catch that" loop). Clarify-only now; hold-to-retry.
- **Multi-turn meeting creation** - the voice/text calendar create is now real slot-filling: the draft
  PERSISTS across turns and absorbs corrections (`CalendarCommand.ApplyUpdate`: day, time, a range like
  "6 to 7pm tomorrow", title, people) until a final "yes" creates it. "yes but make it 4pm" is a change,
  not a create; chit-chat/questions release the draft.
- **Reply hygiene** - fixed **blank notch answers** (a concise `"\n\n"` stop treated the model's leading
  newline as end-of-output → empty stream; removed it) and the **"Toki: Hey Anirudh! … 🚀"** artifact
  (`GeneralChat` ended its prompt with "Reply as Toki:" → the model prepended a label, parroted the date,
  and never banned emojis). Now: prompt bans labels/emojis/date-echo, ends "Reply:", plus a code
  `GeneralChat.Sanitize()` strips a leading label + emojis on every answer path.

**Calendar**
- **Structured create form** - split the one natural-language box into labeled **Title / Day / Time**
  fields (Day/Time still parse "tomorrow"/"2pm" live), keeping the Duration/Meet/People chips.
- **Copy Meet link** - the agenda row's copy action now copies the joinable **Google Meet** link (not the
  pointless calendar-page link) and only appears when the event has one.
- **"A meeting's on now - Join?"** - the record prompt is now "both smart": a calendar meeting with a Meet
  link happening now shows **Join call** (opens the link + records) + Record; an ad-hoc detected call
  shows **Join & take notes**. Dismissing re-surfaces once more after the 10-min snooze if the call is
  still going.

**UI revamp #39 (started)**
- **Central motion tokens** (`Theme/Motion.cs`) - one place for durations + easings; the notch morph
  switched to **ease-out** (arriving motion decelerates - Glass/Material/Fluent); new messages **fade-up**
  in (standard "arrive" motion).
- **"↓ Latest" jump pill** in the notch conversation (appears when you scroll up mid-stream) + a **3-dot
  thinking indicator** replacing the sliding bar.
- **Push-to-talk fix** - the mic now ends only when **Space** (the talk key) is released; relaxing Ctrl/Alt
  while still holding Space no longer cuts you off mid-sentence (the "waveform disappears while listening"
  bug).

**Also:** the muted-mic diagnosis (the "voice returns nothing" root cause was the owner's Windows mic being
muted/blocked - `--mictest` now dumps the Communications-vs-Console default device + all devices + peak so
this is one command to diagnose). ASR model quality on short push-to-talk is **parked** (owner's call) with
a full resume plan in the `asr-and-notch-reply-quality` memory.

**Deferred to next passes of #39:** shared empty-state control, layout-stability/text-trim sweep, the
hover-expand "section pills", and the jump-pill in the Library chat. **Still open:** the ASR quality pass
and the M8 ship path (installer, auto-update, key gate).

## v1-iter3 (Premium UI pass + settings move + fixes) - 2026-08-04

Owner-driven, verified on the real PC. **1,682 tests green.** Done via parent + partitioned Fable agents.
- **Task-count per date fixed** - "how many tasks on Aug 2" counted one meeting, not all; count/quantity words ("many/much/count/number/total" + phrasing words) leaked into the task *topic* → topic-scoped to one meeting. Stripped them; date-scoped counts span all meetings that day (14 new tests).
- **Acknowledgements → small-talk** - "okay cool", "ok cool thanks", "no i just meant okay cool thanks" no longer retrieve a random meeting (SmallTalk broadened, tests added).
- **Settings consolidated in the Library** - the AI-summaries + API-key block moved to a reusable `AiSummariesSection` UserControl on the Library Settings page; the **notch Settings is now UI/visibility only** (theme, glassy, ghost) + a row that opens the Library page. Notch Settings size 360→208.
- **Recording page redesigned** - compact (~93×268 vs ~136×288): one tight row (Toki + dot + timer · Stop chip), hairline, live line. **Ghost mode fixed on every notch state** - acrylic/region re-composition was shedding `WDA_EXCLUDEFROMCAPTURE`; now re-asserted after every composition write + at the end of every state change.
- **Ask landing** - Luma-style hero (mascot on a soft pink radial glow, typed greeting, one identity line, staggered Motion entrance). The example-question pills that shipped mid-pass were **removed** at the owner's call ("flat out ugly and never encourage") - the landing is now hero + one line + the prompt bar, nothing else. Assistant self-awareness sharpened (who/what-are-you in the prompts). Copy trimmed across Library/Settings/Templates.
- **Library sort menu** restyled to the in-house themed popup (pink check on the active mode) - no more native Windows menu. Library **jump-to-latest pill** added (was notch-only).
- **Calendar/COMING UP/prep premium redesign** - accent bars per event, overlapping attendee **avatar** stacks (initials, on-device), amber "in N min" countdown when imminent, Join affordances; PrepCard reshaped into the "Coming Up" card (title · amber relative time · invited/accepted avatars · Context inset · full-width Join). Deterministic, honest first-time.
- **New mascot** - the `TokiBlob` vector redrawn brighter/glossier with black eyes to match the owner's redesign (stays crisp + animated + theme-aware everywhere). `--makeicon` renders it to `Assets/toki.ico` as a **7-frame icon (16/24/32/48/64/128/256 px)**, replacing the old app/taskbar/shortcut picture. The first cut shipped a single 256px frame - Windows downscales that badly at 16–32px, so the taskbar/shortcut still looked wrong; a real multi-size ICONDIR fixed it. (Windows caches shortcut/taskbar icons: unpin & re-pin, or `ie4uinit.exe -show`, to see the change.)
- Central **Motion tokens** (`Theme/Motion.cs`, ease-out) now drive the notch morph, message arrival, calendar/prep motion, jump-pill.

**Follow-ups (2026-08-05).**
- **AI-summaries mode selector made roomy** - the On-device / Heuristic / My API key segmented control was
  still wearing notch dimensions after the move into the Library window (owner: *"tight and ugly and close
  to each other"*). Track padding 3→5 and radius 10→14, segments gained real side padding (14,11 vs 0,7)
  and a 2px gap so they no longer touch, type 11.5→12.5, a hover fill on the inactive options, and the
  selected chip now fades in on `Motion.Micro` instead of snapping.
- **"Replay first-run setup" in the tray menu** - right-click Toki in the hidden-icons tray to walk the
  onboarding pages (name → privacy → all set) again. Non-destructive: finishing only re-saves the name if
  the box isn't blank, so it doubles as a way to change your name.
- **Shortcut icon** - the icon *inside* the exe was already correct (verified by extracting the 32px frame
  from `dist/Toki.exe`); the stale picture was Windows' icon cache. Cleared via `ie4uinit.exe -show` + a
  re-stamp of both shortcuts' `IconLocation`. If a pinned taskbar icon is still stale, unpin/re-pin.
- **`design-handoff/` - every screen, exported for the design team.** `--render` gained the three
  first-run **Onboarding** frames and `--libshot` gained the Library **Settings** page, so the harnesses
  now cover the whole product; the 24 resulting PNGs are laid out in flow order under
  `design-handoff/ui-screens/` with `design-handoff/README.md` as the index (what each screen is, what
  the frames can't show, and a **privacy warning** - they render with real meeting data). Generated, not
  curated: re-run both flags after any UI change.

**State at the end of v1-iter3 (2026-08-05).** Built, tested (`dotnet test` → **1,682 / 1,682 green**),
published to `dist/Toki.exe` (self-contained single file) and owner-verified on the real PC. **Still open:**
the ASR quality pass (parked by the owner - resume plan in the `asr-and-notch-reply-quality` memory), the
remaining #39 passes (shared empty-state control, layout-stability/text-trim sweep, hover-expand section
pills), and the **M8 ship path** - signed installer, Velopack auto-update, licence-key gate, onboarding
download polish, Google sensitive-scope verification. Nothing in the product is blocked on those; they are
the road to actually distributing it. Full reasoning: `docs/backlog.md`.

## N1–N7 (Notes you can actually write in) - 2026-08-05

The owner reviewed the entire pending list and picked one theme: **hand-written notes should be as
capable as AI-written ones, and what you type should shape what Toki writes.** Seven milestones, each
built, tested and owner-verified before the next. **1,738 tests green** (56 new).

- **N1 - the note editor** (`Views/NoteEditor`). ONE editable surface, reused by the Library's manual
  notes, the prep card and the in-meeting scratchpad. Three text sizes, tickable to-do boxes, bullet
  and numbered lists, blank-line spacing, and one-tap insertion of any of the 8 note templates.
  **Deliberately no font family and no colour** (owner: *"nothing outside normal text… just size,
  templates, spacing, freedom"*). Bullets/numbers use WPF's native `List`, so Enter-makes-the-next-item
  and Tab-indent come from the same machinery Word uses; a to-do repeats itself on Enter and ends on an
  empty one. A **`Compact` toolbar** (glyphs, sizes behind one "Aa") exists because the full labelled
  bar wraps to two rows at the notch's 356px and eats the card.
- **N2 - the Library's manual notes use it.** They were a bare `TextBox` until now. Autosave and the
  "Saved" flash kept; ticking a box counts as an edit.
- **N3 - the prep card became writable.** Not a blank sheet beside it: **Edit turns the card itself into
  the note**, with the recap as text, the still-open items as real tick boxes and the agenda as bullets
  (`PrepBuilder.ToMarkdown`). Once edited it stays your version - your words outrank Toki's.
- **N4 - the prep card stopped ambushing you.** `PrepWatcher` now only **arms** a card; it never seizes
  the notch. You reach it by hovering the peek before the meeting, or via a **"Prep card" button on the
  recording page** during it (stays open while hovered, ‹ Back returns to recording). Also fixed:
  `JoinAndRecord` was *discarding* the card on join, so during the very meeting it had been prepared for
  there was no card at all.
- **N5 - save-or-discard, on the note list itself.** When a recording stops, anything typed on that
  meeting's card is offered at the top of the Library as **Keep / Discard** - never a modal, never saved
  behind your back. Discard is two-step ("Sure?") because it's text typed by hand.
- **N6 - typed-notes augmentation** (the Granola model). What you typed goes into the note-writing prompt
  *ahead of* the transcript, as a constraint: cover every point, expand with transcript detail, lead with
  them, never contradict. Tick state survives as `[task]` / `[done]` so "still outstanding" reaches
  ACTION ITEMS. **Your notes survive every failure path** - no model, model failure, or a
  "nothing substantive was discussed" verdict all still emit a "Your notes" section, with your unticked
  boxes as action items owned by You. The typed block is capped so it can never be what overruns context.
- **N7 - evidence-linked notes.** Every traceable bullet carries a small **↳** that jumps to the
  transcript line it came from and highlights it (`Storage/Evidence.cs`, deterministic word overlap, no
  model). A bullet that can't be traced gets **nothing** - a link to the wrong line is worse than no
  link. Computed at render time, so it works on notes written before the feature existed and costs
  nothing on disk. Clicking a link past the preview fills the lazy transcript in first, then scrolls.

**Storage decisions.** Hand-written notes are **Markdown** (`Storage/NoteMarkdown.cs`) - the same format
as the existing `%APPDATA%\Toki\Notes` mirror, so they stay openable in Obsidian or Notepad instead of
becoming a proprietary blob; 23 round-trip tests guard ticks, spacing and list renumbering. The prep
scratchpad lives **outside** the meeting database (`Storage/PrepScratch.cs`, atomic writes) because it
exists *before* the meeting record does and must survive a crash mid-meeting.

**Still open, deliberately:** the prep card's auto-surface is gone by owner decision and logged for
revisit (it doubled as the "a meeting is starting" nudge); prep notes for meetings that were never
recorded remain invisible; and re-writing a note with a different template loses the typed steering,
because a meeting record doesn't store which calendar event it came from. All three are tracked.

## S1–S7 (Sticky notes, and a real fix for the note editor) - 2026-08-06

The first owner test of N1–N7 found one bug and one wrong idea. Both are fixed here, and the sticky note
is rebuilt on storage that can actually carry it.

**The bug: "bullets are created by themselves."** Real, reproducible, and a genuine feedback loop. The
owner's note is the proof - this is what was actually on disk after seven opens:

```
- •	•	•	•	•	•	•	Confirm launch blockers & owners
```

**Root cause:** the editor built bullets from WPF's native `List`, and read a line's text back with
`TextRange.Text` - which, for a paragraph inside a list, *includes the list's own marker*, serialised as
`"•\t"`. So reading gave back `"•\tConfirm launch blockers"`, which was saved as
`"- •\tConfirm launch blockers"`, re-read as `"•\t•\tConfirm…"`, and so on. **One more bullet on every
single save.** It hit bullets and numbered items only - everything in a native `List` - which is exactly
why the to-dos in that note are clean and the agenda is not.

Two further defects from the same `List` structure, found by the same test run:

- A paragraph inside a `ListItem` is not a child of `Document.Blocks`, so inserting the next line after it
  threw, and the **Body** button could never lift a line back *out* of a list.
- A marker the user *typed* (`- `) stayed in the text and was written on top of the kind's own marker
  (`- - two`), and a blank line silently split one list in two and restarted the numbering.

Fixed by construction, not by patching: **every line is now a flat paragraph** carrying its
`NoteBlockKind`, with bullet/number/tick markers held in an `InlineUIContainer` that is *not* part of the
text. Every line is addressable, every kind change is a local edit, a marker can never leak into the text,
and numbering is recomputed from position on every change. Typing `- `, `1. `, `# ` or `[] ` now promotes
the line and **eats the characters**, the way every notes app does - which is also what stops the marker
surviving into storage. Enter, Backspace-at-line-start and the toolbar toggles are all ours and uniform.
`NoteMarkdown.TryMarker` is now the single definition of "this text starts with a marker", shared by the
parser, the writer (which strips exactly one, the precise inverse) and the editor.

Verified by a new **`--notetest`** that drives the real control the way fingers do - types markers, presses
Enter, clicks toolbar buttons, ticks boxes - and reads the Markdown back. 16 checks, including *ten*
save/reload cycles that must change nothing and an explicit assertion that **no marker glyph or tab ever
reaches the saved text**. A pure round-trip test could never have caught this: the leak was in WPF's
`TextRange`, not in the text format, and 23 green format tests stayed green throughout.

**Notes already damaged by it heal on open.** `NoteMarkdown.Heal` strips leaked marker glyphs from a
line's text. The tab is what makes that safe: it's how WPF serialises a marker and not something a person
types at the start of a line, so `"•\t"` is stripped and a hand-typed `"• "` is left alone. Added
**`--dumpnote <text>`**, a read-only dump of what a note *actually* contains - "what's on screen" and
"what's saved" are different questions, and guessing at the second one is what cost the time here.

**The wrong idea: typed notes steering the summary (N6, removed).** Owner's call, and the reasoning is
worth keeping: the summary's job is to say what was said in the meeting. A summary that reshapes itself
around your private jottings is one you can no longer check the room against. `LlmNoteWriter` now takes
the transcript and nothing else.

**Sticky notes, rebuilt.** They were a JSON file keyed by calendar event id; that file never pruned, and a
note could only ever belong to the one event it was born on. They are now real rows in `toki.db`
(`sticky_notes` + two link tables), with the old file migrated once and renamed rather than deleted.

- **Linking is explicit, never by title.** Meeting titles change, and "Weekly sync" is every meeting
  anybody has. Toki never guesses; the prep card has a picker ("Start a new note", or any recent note with
  when it was last touched) and the choice is remembered by id. One note can run across many meetings - a
  weekly 1:1 keeps one page.
- **It lives inside the meeting's note, not beside it.** The separate Keep/Discard card in the Library
  list is gone (owner: a row you can't open, sitting among meetings it isn't one of). A **"My sticky note"**
  button sits under the meeting title; the note opens after everything Toki wrote and **before** the
  transcript, fully editable in place, auto-saving, with a two-step Delete.
- **It touches nothing else.** Not in the meetings table (so it can't appear as a Library row), not in the
  FTS index (so Ask can't surface it), not an input to the note writer. Deleting a meeting drops the
  *link* and keeps the writing - you may still want it next week.
- **The pre-meeting nudge is back** (S5): the prep card surfaces itself once, ~5 min out, but **only** when
  the notch is idle and nothing is fullscreen. Otherwise it stays armed, one hover away. Removing it a day
  earlier had also removed the only "your meeting is starting" signal.

**Action items are tagged too** (S7): the ↳ evidence link now appears on to-dos, not just section bullets -
a task is the line you most want to check the source of. One `Link()` builder serves both, so they cannot
drift apart.

Also fixed en route: the note editor's own dropdown used `Border` + `MouseLeftButtonUp` rows, which are
silently swallowed in a `StaysOpen=false` popup - every item was dead. Both notch pickers now share
`Views/NotePicker.cs`, built on real `Button`s.

**Verified:** 1,759 tests green (18 new sticky-note tests covering same-title collisions, renames, delete
cascades, migration, and the "can't leak into search or the list" guarantees); `--notetest` 13/13;
`--dbtest`, `--preptest`, `--measuretest` all pass; Library and notch renders checked by eye; run against
the packaged `dist/Toki.exe`, not just the debug build.

## S8 - the polish pass (em dashes, type, hit targets) - 2026-08-06

Owner-driven cleanup after S1-S7, plus one bug that turned out to be deleting text.

**"Mosquito sized text" - and it was losing your writing.** Clicking the empty line between two sections
and typing gave 6px text. The cause: a blank spacing line was rendered at `FontSize = 6`. But the line was
*still tagged blank*, and `NoteMarkdown.Write` emits **nothing** for a blank line - so the sentence just
typed was **discarded on the next save**. Three linked faults, all fixed:

- Sizing a line with `FontSize`. `LineHeight` now does the shrinking and the font stays at body size, so
  the first character typed on a blank line is already normal.
- A blank line with text in it was still "blank". It now becomes a body line the moment there's text,
  which is what stops the text being thrown away.
- WPF stamps the caret's formatting onto typed Runs, so a character born in a tiny line carried
  `FontSize=6` explicitly and outranked the paragraph. Restyling now clears per-Run font size and weight,
  and clears the stale short `LineHeight` when a line changes kind.

`--notetest` is now **20 checks**, four of them on exactly this.

**No em dashes anywhere** (owner: they look ugly in Toki's surfaces). 3,089 replaced across 188 files -
source, tests, and every markdown doc. Three survive because they're *functional*, each named in
`NoEmDashTests` rather than blanket-exempted: a word separator in `Evidence`, a model-emitted "field is
empty" marker in `LlmNoteWriter`, and the character `GeneralChat.PlainDashes` exists to remove. The model
is a source too, so prompts ask AND `PlainDashes` enforces - at the note writer, at every reply, and
**per streamed token**, because an answer is read as it streams and a late cleanup would still have been
seen. EN dashes stay in ranges ("9:41 - 10:11", "2-4 sentences"): that's the character's job.

**One type ramp, and Settings is the reference.** Pages had drifted (Templates 22 Bold, Settings 22
SemiBold, note titles 22 Bold, the manual-note title 20 Bold) - and a Bold title beside a SemiBold one
reads as a different typeface even when the family is identical. Named styles now: `PageTitle`,
`PageSubtitle`, `SectionTitle`, `PanelTitle` (the notch's smaller scale), `NoteTitle`. Nothing is Bold
except deliberate micro-elements (date badges, YOU/THEM chips). Templates also stopped being the one
centred page - every page starts at the same left edge now.

**The box is the button, never the text.** Owner: the notch buttons were "impractical", you had to aim at
the label. The root cause was in the styles, not the layouts: `PillButton`'s hover was an *opacity*
change, which is invisible on the many buttons that are deliberately `Background="Transparent"` - so
those had no hover at all. The template now carries a separate hover **overlay layer** that draws the
rounded box exactly where the button can be clicked. Floors added too (`PillButton` 32x34, `IconButton`
34x34, editor toolbar chips 32x30), plus the WPF trap that an *unset* Background is `null` and a
null-background Border is invisible to hit testing.

Also: the sidebar nav rows now light up on hover (they were always full-width targets, but nothing was
drawn at rest, so an inactive row read as a floating word); the notch's "Library" link is a real pill;
"All other settings" is a real `RowButton` sitting inside the card instead of a Border on negative margins
whose hover box spilled to the walls, with the chevron non-hit-testable so it can't look like a separate
control; and Library's Copy is one rounded split button with a hairline seam instead of two pills a pixel
apart.

**Verified:** 1,774 tests green; `--notetest` 20/20; `--measuretest` reports no clipping from the larger
buttons; Library, Templates, Settings, Home and the notch Settings all re-rendered and checked by eye
(including a `hub-nav-hover.png` frame, since hover is otherwise invisible to a headless screenshot);
run against the packaged `dist/Toki.exe`.

## T1-T8 (The first real meetings) - 2026-08-11, IN PROGRESS

Two real recordings, back to back: an 8-minute 2-person call and a 6-minute 11-person team call. Full
error analysis in **[`docs/real-meeting-findings.md`](docs/real-meeting-findings.md)**. The headline is
that **every failure was invisible to the 1,800-test suite**, because the suite tested the layers and not
the artifact a person reads.

**Done**

- **T1 - the transcript was shredded.** 182 segments in 8 minutes, and short interjections spliced into
  the middle of sentences (`"did Craig mention ask"` / `"idea."` / `"you to like do this?"`). Cause:
  `TranscriptMerger` did echo-dedup then `OrderBy(StartMs)` and **nothing else** - no same-speaker
  merging existed. Consecutive lines from one person now join into a turn (1.5s gap, capped at 700 chars
  so a monologue still breaks into paragraphs), and short backchannels are moved out of the middle of
  someone else's sentence. Timestamps survive, so evidence links still resolve. One fix, three symptoms:
  readability, a note document down from 182 paragraphs to ~40, and better chunk boundaries for the writer.
- **T2 - names.** `Transcription/NameCorrector.cs`. Names that sound alike are clustered (compact
  Metaphone + edit distance) and collapse to one spelling: a **trusted** one first (the user's own name,
  a calendar attendee, a pinned term), else the **most frequent across the whole library**. Craig/Greg is
  a dead tie inside one meeting and not close across the library, which is exactly why the corpus is the
  authority. Guards: ordinary words are excluded before matching ("Great" can never become "Craig"),
  sentence-initial words are excluded, two trusted spellings in one cluster means two real people, and a
  dead tie is left alone. A trusted name may reach further because the user asserted it.
- **T3 - the Summary was raw transcript, twice.** The model returned good sections but skipped `SUMMARY`,
  and the writer substituted `NoteExtractor.Tldr` - the first ~140 characters of the transcript. Now it
  asks again with a summary-only prompt, and if that fails leaves the summary EMPTY so the renderer drops
  the section. This also fixes the title: "Evacuate Immediate Action Required" for a meeting about
  centralising team reports came from titling the raw-transcript summary.
- **T7 - a roll-call became eleven to-dos.** The chair read out a register and every name became a task.
  A task now needs an action in it; a bare name (or a pair) is a person. Near-duplicate tasks collapse.

**Chosen after research: post-ASR correction, not decode-time biasing.** Contextual biasing is the
principled fix and sherpa-onnx supports it for Parakeet TDT, but hotwords on a BPE model need a
`bpe.vocab` and the model archive Toki ships has none; it would also force beam search over greedy. Post
correction is deterministic, unit-testable, free at runtime, and repairs the meetings already saved.

**In progress**

- **T8 - diarisation.** 11 people collapsed into "Them" is what let a register be read as tasks. The
  two-stream design helps: `you.wav` means the local user is already perfectly separated, so only
  `them.wav` needs splitting. sherpa-onnx 1.13.4 already exposes `OfflineSpeakerDiarization`, pyannote
  segmentation, `SpeakerEmbeddingExtractor`, `FastClusteringConfig` and - the important one -
  `SpeakerEmbeddingManager` (Add/Search/Verify), the primitive for naming a voice once and recognising it
  in later meetings. No new dependency; two small model downloads. Licence/size/CPU cost under research.
- **T4** action-item owners (all four of meeting 75's to-dos were the owner's, and none said so).
- **T5** UI: `Select()` rebuilds all 52 cards to move one highlight; `OnSearch` re-queries and rebuilds
  per keystroke; `Cards` is a non-virtualizing `StackPanel`.
- Wiring T1/T2 into the save path, the Settings names list, a "Re-clean saved transcripts" button, and
  underlining corrected words in the transcript.

**Not yet addressed:** both meetings were classified "Decision log" when neither made a decision, so the
Rationale section reads as invented justification.

**Verified so far:** 1,807 tests green, all new ones written from the two real transcripts.

### T1-T8 progress (2026-08-12)

**T2 name correction is now WIRED and live.** Building it was not the whole job: there is a *live*
transcription path (Toki decodes during the meeting) which skips `TranscriptionService.Process`
entirely at stop time, and name correction only existed inside `Process`. Since the live path is the one
most meetings actually take, the feature would have looked correct in testing (via the fallback) and
done nothing in real use. Both routes now call it. The lexicon has two tiers: **trusted** (the user's own
name, calendar attendee display names, and a new `AppSettings.PinnedNames`) which wins outright and gets
a wider match because a human typed it, and **corpus** (every spelling counted across all transcripts,
titles, summaries, and action-item owners weighted 3x) which settles what one meeting cannot.

**T5 UI performance - the two constant costs are fixed, measured.** Full write-up with the
with/without/who/when/how of virtualization: **[`docs/ui-performance.md`](docs/ui-performance.md)**.

- `Select()` called `LoadList()` to *"refresh selection highlight"*, so every click on a note re-ran the
  SQL query and rebuilt ~800 visual elements to change one brush. Now walks the on-screen cards:
  **424 ms -> 0.4 ms**.
- `OnSearch` rebuilt per keystroke; typing "pricing" cost seven queries and seven rebuilds. Now debounced
  at 180 ms: **one** rebuild.
- New **`--uitest`** self-test measures all of it against the real database with pass/fail budgets, so
  "slow and clunky" is a number. Current: selection PASS (0.4 ms), note render PASS (139 ms), list build
  **FAIL** (424 ms at 54 meetings, of which ~343 ms is WPF laying out cards nobody can see).
  Virtualization is therefore justified by measurement rather than by principle; the risk is drag-reorder,
  which walks `Cards.Children` **by index**.

**T8 diarisation - measured, awaiting two owner decisions.** New **`--diartest`** benchmarks two routes on
real meeting audio. On the owner's PC, 8.4 minutes of `them.wav`:

| Route | Measured | Extrapolated to 60 min |
|---|---|---|
| Textbook (segmentation sweep + per-hop embeddings) | 59.6 s (RTF 0.119) | ~7 min |
| **ASR-guided (one embedding per utterance Toki already found)** | **7.1 s (RTF 0.014)** | **~50 s** |

The shortcut is published prior art, not a hack: **Turn-to-Diarize** (Google, ICASSP 2022) reports 31x
less compute at one hour *and lower* DER on 2 of 3 datasets, because a fixed sliding window straddles
speaker boundaries and creates confusion errors. It also sidesteps an unfixed
[AccessViolationException in sherpa's .NET diarisation](https://github.com/k2-fsa/sherpa-onnx/issues/3253)
on multi-chunk audio, which is uncatchable in .NET and would kill the process.

Two findings that changed the design:
- **Average linkage, not complete.** Complete linkage asks that *every* pair in a cluster be close, so one
  bad moment (network drop, mic distance) splits a person permanently. Measured: one remote speaker came
  out as 5 clusters at the default threshold and never fewer than 2 even at 0.95. Average linkage reaches
  the correct 1.
- **No single threshold serves both meetings.** 0.80 gets a 1:1 exactly right and collapses an 11-person
  call to 2; 0.55 gets the team call roughly right and shatters one person into 5. So: use the calendar
  attendee count when known (passing the true count worked perfectly), default conservative when not
  (under-splitting is today's behaviour; over-splitting invents people), and let the user set the number,
  which is cheap because re-clustering takes ~5 s.

GPU was investigated and rejected: the shipped NuGet is CPU-only, `Provider = "cuda"` silently falls back,
and a measured CUDA build on an RTX 3090 still gave RTF 0.24 because the bottleneck is thousands of tiny
sequential inferences.

## Sticky notes that accept typing, and a duration you can choose (2026-08-13)

**Fixed: "Add a sticky note" opened the panel and typing did nothing.**

Root cause: `Reveal()` called `Focus()` on the editor in the same breath as adding it to the panel -
before it was in a laid-out visual tree. `Focus()` on a control that is not yet laid out returns false
and does nothing, **silently**. Keyboard focus therefore stayed on the read-only `RichTextBox` that HOSTS
the sticky panel, and a read-only RichTextBox swallows every keystroke without a sound.

The editor itself was fine the whole time - which is exactly why `--notetest` passed all 20 checks while
the feature was unusable. **The defect was in the hosting, not the control.** New `--stickytest` drives
the sticky note where it actually lives, inside a real meeting document, and it reproduced the bug on the
first run. Focus now happens after layout, and a click anywhere in the panel puts the caret back in the
note (the host RichTextBox very much wants focus back).

**Fixed: the meeting-duration chip could not reach 15 or 45 minutes.** It CYCLED 30 -> 60 -> 90 -> 30 on
each click, so two of the six sensible lengths were unreachable no matter how many times you pressed it,
and you had to click through lengths you did not want. A duration is a choice from a set, and a set
belongs in a list you can see - the affordance every calendar uses. Now a dropdown: 15 / 30 / 45 / 1 hour
/ 1 h 30 m / 2 hours, with a chevron so it reads as a choice rather than a toggle.

**Same fix, same reasoning, in the note editor:** the three separate Title / Heading / Body chips became
one "Aa" size picker. Three chips spent the whole toolbar width and still read as three unrelated
buttons.

**Not done, deliberately: Bold / Italic.** They do not exist anywhere and `NoteMarkdown` has no inline
formatting, so it needs markdown round-trip plus tests - and it collides head-on with the S8 fix that
CLEARS per-Run FontWeight to stop the "mosquito text" data loss. Both previous bugs in this control were
data loss. Tracked rather than rushed at the end of a session.

1,846 tests green; `--notetest` 20/20; `--stickytest` 3/3; `--hittest` clean.
