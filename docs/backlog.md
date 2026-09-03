# Toki - backlog & "what's next" (living doc)

## 🚨 ROOT CAUSE FOUND AND FIXED: long meetings were never written by the model (2026-09-03)

**This is very likely the bulk of "the note quality is mediocre at best".** It was arithmetic.

`MaxChunkChars` was `(ctx - 800) * 3`. `LlmRuntime` sets `ContextSize = 3072` on the partial-offload
branch (VRAM 3.9GB to 7GB), which is the owner's RTX 3050 at 4.0GB, so a map window was 6,816
characters. Numbered transcript lines tokenise at nearer 2.6 chars/token, not 3, so that window was
~2,600 tokens against a 3,072 context that also had to hold ~500 tokens of instructions and up to
760 of generated answer.

Every window threw `LLamaDecodeError: llama_decode failed: 'NoKvSlot'`. `TextPass` swallowed it with
a bare `catch { return null; }`. Every window counted as failed. The meeting fell to the heuristic
floor, which writes raw transcript lines as to-dos.

**Short meetings fit in one pass and were fine. Long ones were never written by the model at all.**

Meeting 28 (20 minutes), before: no title, no summary, and to-dos reading "Um, I will ask Pooja to
give you access to this uh". After: a real title, a real summary, and bullets carrying names, dates
and numbers, with task-shaped actions and a due date.

**Fixed:** `ReserveTokens = 1600` and `ChunkCharsPerToken = 2.6` (window 6,816 to 4,347 chars);
model-call exceptions logged; the fall to the floor logged distinctly from "the meeting was quiet";
`ContextBudgetTests` asserts the budget fits at both context sizes and fails at both against the old
formula. Cost: meeting 28 went 91s to 255s, which is the right trade.

**What this means for everything measured before today.** Every note-quality number taken on this
machine over a long meeting was measuring the FLOOR, not the pipeline. The 96.2% precision / 66.1%
recall baseline, and the prompt tuning done against it, need re-reading in that light. Re-measuring
is the next task.

## 🔎 OPEN, MEASURED, NOT YET DIAGNOSED: a third of extracted points fail their own citation

Observed across every run today, and it predates all of this session's changes, so it is not caused
by them. Per-window keep rates from the map step, same pipeline, same day:

- **clean meeting (48):** 25/26, 29/31, 22/23, 21/22, 19/21. Almost nothing dropped.
- **noisy meeting (28):** 4/10, 15/30, 12/25, 7/13, 3/9. **A third to two thirds dropped**, and
  essentially all of it as `unsupported` rather than `uncited` or `out-of-range`.

`unsupported` means the model cited a real line inside the window, but fewer than half that line's
content words (plus its two neighbours) appear in the point it wrote. So the FORMAT is fine and the
model is pointing somewhere real; the words are not where it says they are.

Meeting 28 is a 20-minute call in 1,140 segments, so about one second per segment: heavy turn-merging
and short fragments. Two hypotheses, both cheap to test and NOT yet tested:

1. **The neighbour window is too narrow for fragmented speech.** `ValidateDigest` accepts the cited
   line plus one either side. On a transcript averaging a second per line, a single spoken sentence
   can span five or six lines, so a faithful point drawn from that sentence cites the middle and
   fails against a 3-line window. If so the fix is to scale the neighbourhood by SEGMENT DENSITY
   (lines per minute), not to loosen `CitationOverlap`, which would let real invention through.
2. **The model genuinely paraphrases harder on fragmented input**, having less coherent text to quote.

Test 1 first: it is a two-line change and `--linktest` plus a re-run of meeting 28 will show it. Do
NOT lower `CitationOverlap` to make the number look better; the gate is the only thing standing
between a digest and invention, and this is the failure mode it exists to catch.

## ⚠️ STOP TUNING AGAINST n=2: the instrument's noise floor is about 12 points

Measured 2026-09-03, and it invalidates a diagnosis I made earlier the same day. Meeting 28, run
three times through near-identical code, returned **62%, 38% and 46% recall**. Qwen is sampled at
temperature 0.7, so a single meeting's score moves about 12 points run to run for free.

I read the 62% to 38% step as a real regression, attributed it to the per-window digest budget, and
changed the code. The change is defensible on its own reasoning and it is **NOT measured as an
improvement**: pooled recall was 28.2% before it and 28.2% after (it lifted meeting 28 and lowered
meeting 48 by about the same). Kept, honestly labelled, not claimed as a win.

**The rule this buys:** `--regentest 2` cannot resolve a difference smaller than roughly 15 points.
Before treating a change as an improvement, either run n >= 10 meetings, or run the same small set
several times and compare distributions rather than single numbers. A pipeline change validated on
one meeting's score is a coin flip with extra steps.

`--linktest` does this correctly already, with a fixed seed and a paired McNemar test on
disagreements. `--regentest` cannot, because every run re-samples the model. Treat it as a detector
of STRUCTURAL faults (zero bullets, notes with no facts, a meeting falling to the floor), which is
what it actually found today, and never as a fine-grained quality meter.

**DONE 2026-09-03: the extraction budget was per-window, not per-meeting (kept on reasoning, not measured).**

Exposed by the context raise above. The map prompt asked for a flat "5 to 12 lines" however much
transcript the window held, so the budget belonged to the CHUNKING rather than to the meeting.
Windows grew 49%, meeting 28 went from seven windows to four, kept the same budget each, and its
recall fell 62% to 38% while running 100 seconds faster: extracting less, sooner.

`DigestLines` now scales at about one point per 450 characters, clamped 6 to 20 (below 6 a window
says nothing useful, above 20 the model pads and the citation gate discards the padding). The map
token cap scales with it, capped at 700 to stay under the 760 `ReserveTokens` is sized against.

**HONEST LABEL: this one is not measured as an improvement.** Pooled recall was 28.2% before it and
28.2% after. The 62% to 38% drop that prompted it turned out to be inside the run-to-run noise (see
the section above). The reasoning stands on its own - an extraction budget should describe the
meeting, not the chunking - so it is kept, but nobody should cite it as a measured gain.

**The shape of all three note-quality fixes today, worth remembering:** each was a constant that
described the MACHINERY rather than the WORK. Chars-per-token assumed 3 for text that runs at 2.6;
the re-reduce loop summarised summaries because digests did not fit a window size; the digest budget
counted lines per window instead of per minute. **None were prompt problems**, which is why round
after round of prompt tuning never moved the quality.

**DONE 2026-09-03. Re-reduction was where notes died; it no longer exists.**

Verified 2026-09-03 on the two longest meetings, after both fixes above. Provenance recovered to 6/7
bullets and 6/7 actions, and meeting 48 kept 3/3 action sources THROUGH a re-reduction, which is the
union fix working. But:

| meeting | re-reduced? | recall | facts claimed | bullets |
|---|---|---|---|---|
| 28 | no | 15% to **62%** | 4 to 5 | 7 |
| 48 | **yes** | 46% to **4%** | 25 to **2** | **0** |

Meeting 48's map step went perfectly: 25/26, 19/21, 29/31, 22/23, 21/22 lines kept across seven
windows. Then `notes: digests still 6645 chars, reducing again (round 1)` fired, the digests were
summarised again, and the final note came out with **no bullets at all**.

**Re-reduction summarises summaries, which is the exact failure this pipeline was built to avoid.**
The map step exists so the writer reads evidence rather than a paraphrase; the re-reduce loop
reintroduces the paraphrase-of-a-paraphrase for any meeting whose digests overflow. And the NoKvSlot
fix, by shrinking the window, made this loop fire MORE often, so one failure was partly traded for a
milder one.

**Candidates 1 and 2 are both DONE. Candidate 3 is not needed for now.**

1. **`ContextSize` raised 3072 to 4096 on the partial-offload branch, MEASURED first.** On the
   owner's RTX 3050 with 20 layers and 3,370 MiB free: KV cache 228 to 304 MiB, everything else
   unchanged, total VRAM 1,931 to 2,007 MiB, leaving 1,363 MiB headroom. **76 MiB buys a 49% bigger
   map window** (4,347 to 6,489 chars). If a machine ever fails to load, drop this back rather than
   lowering `ReserveTokens`: the reserve is what stops NoKvSlot, the context is only how much fits.
2. **Re-summarising replaced by selection (`TrimToFit`).** Lines carrying hard entities outrank lines
   carrying none; a verified window outranks an uncited one; length breaks ties. Reading order is
   restored afterwards, because a writer handed evidence out of sequence invents transitions to
   explain the jumps. Six tests, mutation-probed by scoring every line equally.
3. Batching the final pass over digest groups is unnecessary while 1 and 2 hold. Revisit only if a
   very long meeting still trims away most of its evidence.

Note that 4096 alone would NOT have fixed meeting 48: its digests were 6,645 chars against a 6,489
budget, so it would still have overflowed, barely. The context raise reduces how often trimming
happens; the trim is what makes overflowing survivable.

Do NOT tune the final prompt against this. The prompt was never the problem; the input it received
was a summary of a summary.

**REGRESSION THE FIX CAUSED, then fixed: provenance coverage collapsed 67% to 18%.**

Smaller windows mean the joined digests more often exceed the budget, which triggers the re-reduce
loop, and that loop called `provenance.Clear()`. Three of six meetings came back with no recorded
source at all. The original reasoning was that a digest of digests cannot be traced without guessing.
Right about guessing, WRONG about the alternative: a re-digest covers a KNOWN set of first-round
windows, so the UNION of their ranges is observed, not inferred. Coarser and still true.

`GroupWindows` now carries that union through re-reduction. One rule keeps it honest: a window that
came back uncited contributes its TEXT to the group but never widens the RANGE, so an unverified
window cannot manufacture a claim about lines nothing checked. Five tests in
`ReReduceProvenanceTests`, mutation-probed by letting unverified windows widen the range.

The diagnostic ALSO lied about this: it printed "likely a single-pass meeting" for the empty
coverage, when the real cause was re-reduction. Message corrected. A diagnostic that names the wrong
cause is worse than one that says nothing.

**MEASUREMENT NOTE: read the POOLED numbers, not the mean of percentages.** The macro mean let a
meeting with 2 entities count as much as one with 26, and a single 2/2 becoming 1/2 swung it eight
points. Pooled, the context fix is a clear gain over the broken pipeline:

| | old stored notes | pre-fix pipeline | post-fix |
|---|---|---|---|
| facts the note claims | 54 | 12 | 18 |
| pooled precision | 90.7% | 91.7% | 88.9% |
| pooled recall | 33.3% | 11.7% | 21.7% |

Notes with ZERO checkable facts fell from 3 of 6 to 1 of 6. `--regentest` now prints pooled ratios
beside the macro mean, and `n/a (0)` instead of 100% for an empty denominator.

**THE LIMIT OF THIS METRIC, and it matters:** the old stored notes still score higher on recall than
the fixed pipeline. That is not evidence they are better notes. Grounding measures FAITHFULNESS, and
a note that copies the transcript verbatim scores near-perfectly on it while being useless. The
heuristic floor, which is the worst note Toki can produce, scores WELL here by construction. Do not
tune the writer against this number alone; the gate is `--evidencetest` on real meetings and the
owner reading them.

**FOUND WHILE FIXING THE ABOVE, not yet changed: the bullet budget measures the wrong thing.**

```
var bulletBudget = content.Length > 9000 ? 9 : content.Length > 4000 ? 7 : 5;
```

Its own comment says "a cap that does not scale claims a 90-minute meeting holds no more than a
3-minute one". But `content` is the DIGEST text the final pass sees, and the re-reduce loop caps that
at `MaxChunkChars` - 4,347 chars on a 3072 context, 6,489 on 4096. So the `> 9000` branch is
effectively unreachable and **the real maximum is 7 bullets**, and a 90-minute meeting and a
20-minute meeting both arrive at the final pass with a context-capped digest and earn the same room.
The scaling the code intends is defeated by the context cap.

The fix is to budget from the TRANSCRIPT (length or duration), which is the actual size of the
meeting, rather than from the digest. That restores the stated intent rather than changing it.

**Deliberately NOT done in the same change as the context fix.** One variable at a time: the
regentest running now measures the context fix alone. If the budget is changed at the same time,
neither result can be attributed. Do it next, and gate it on the numbers: facts-checked should RISE
without precision falling. If precision falls, the extra room is being filled with invention and the
change reverts.

**Still open, and now more attractive:** the owner has a 4GB card, so `ContextSize` is 3072 and
windows are small. A larger context would mean fewer map rounds and better notes. Worth checking
whether 3072 is actually forced by VRAM at 20 layers, or whether it was set defensively.

## 🎨 OWNER CALL OPEN: accent drift between the Figma file and Theme.xaml (2026-09-03)

Found while cleaning the repo, not while looking for it. The root file `css code for all
the design layers.txt` (a 46,687-line Figma "copy as CSS" dump of node `288:4`) moved to
`docs/design/figma-layer-css.txt`, and `docs/design/README.md` now explains what it is,
what it must never be used for, and what was extracted from it.

The dump CONFIRMS the constitution on the things that matter: `rgba(33, 30, 30, 0.5)` 72
times, the 1px white-10% rim 38 times, the 99/24/16/12 radius ladder by real usage, and
the 90/50/25 white-opacity text ladder. None of that has drifted.

Four of the six accents HAVE drifted:

| Meaning | Figma | Theme.xaml |
|---|---|---|
| amber | `#F5A05C` | `#F5A05C`, matches |
| green | `#45C98A` | `#3DD68C` |
| blue | `#4DB8F5` | `#5AA9F9` |
| pink | `#EA72B0` | `#F09CC7` |
| purple | `#B477E8` | `#C08AF0` |

All four shipped values are slightly lighter and more saturated than the file, and they
drift in the same direction, which looks like a deliberate hand-tune rather than a slip.

**Not changed, deliberately.** Accent colour is a visible product decision and the rule is
no spreading a change for consistency without approval. Two questions for the owner:

1. Which wins, the file or the tuned values? "The Figma file is the boss" argues for the
   file; the consistent direction of the drift argues someone tuned these on purpose.
2. `Theme.xaml` carries TWO accent families (`Amber #E89B3C` alongside `AccentAmber
   #F5A05C`, and a plain `Green`/`Blue` beside them). Which is authoritative on which
   surface is not answerable from the tokens alone. Worth resolving whichever way (1) goes,
   because a second family is how the first one silently comes back.

Cheap either way: these are token values in one file, and `--themetest` walks every notch
view in both themes afterwards.

## 🎙️ APPROVED DESIGN, NOT YET BUILT: the recording bar (2026-09-03)

Owner saw it and said "I like the design, we'll work on it later." Build from the artifact, not from
this summary: **https://claude.ai/code/artifact/8d0da9ff-9ebb-4c18-a0b2-bb31cbec42b6** (both bars live
at true size, hover the proposed one).

**The thesis:** the bar is on screen for the whole meeting and today it shows a clock, fourteen grey
dots, and three round controls of EQUAL weight - one of which stops the recording and two of which are
rarely touched. It should answer three questions and offer one action.

**To build:**
1. **Stop becomes a labelled pill**, not a red disc among grey ones. Costs ~16px of width and turns a
   glyph into a word; that is the right trade for the only destructive control on the bar.
2. **Ghost and expand recede** to opacity 0 until pointer or keyboard focus reaches the bar.
3. **The waveform says WHO is talking** - two tones plus a small label. This is the strongest idea in
   the set: Toki captures the mic and the meeting as separate streams and already knows which is
   louder, and then draws one anonymous grey wave. No competitor can copy it, because they join the
   call as a bot on a single stream. `LevelMeter` already carries both levels.
4. **A breathing live dot**, 6px, StopRed, 2.4s cycle, stopped under reduced motion.

**Already shipped from this pass:** tabular figures on the clock, and even optical insets (18 left /
16 right, chevron overhang 6 to 2).

**Tried and reverted, do not retry:** spanning the waveform across the full row to centre it on the
card. Rendered, it ran underneath the ghost and stop buttons - the wave stretches to its span. A few
px off centre beats colliding with the controls.

**Two owner calls still open:** whether the wide Stop pill is worth the extra width versus keeping the
disc and only fading the other two; and whether the 38px speaker label earns its space or the wave's
colour alone should carry it.

## ✅ FIXED 2026-09-03: Toki flickered opening a panel from a peek

**FIXED, and verified on film.** `CollapsedView` is literally just a `TokiBlob` - the peek IS Toki -
so the cross-fade was photographing him and dissolving the picture at the card's centre while the
real bubble sprang in at the panel's gutter. The duplicate was the OUTGOING SNAPSHOT, not the
timing, which is why three rounds of delaying the bubble never touched it. A mascot-only peek is now
never snapshotted; there is nothing to carry across anyway, since the card behind it is bare.

Filming the fix immediately showed a NEW fault it had caused: ~10 frames with no Toki at all,
because the 170ms delay was still waiting for a duplicate that no longer existed. Back to 70ms.

History kept below, because the three failed attempts are each a real bug that was fixed on the way. Recording it is the only way to make
progress on this - `dist\Toki.exe --noghost --state collapsed,home,collapsed,home`, then ffmpeg
gdigrab at 60fps and tile the frames. Ghost mode hides the notch from every screen recorder, so
`--noghost` is mandatory.

**What the frames show (60fps, verified):** the peek's Toki sits at the card's RIGHT, then in one
frame he is gone and a different Toki springs in at the LEFT, dim and oversized.

**Why there are two:** a peek draws its OWN small Toki inside its content, because the floating
bubble is hidden in that state (CLAUDE.md: "peeks carry their own tiny one"). So the peek's Toki is
still on screen inside the OUTGOING page (now a cross-fade snapshot) while `MascotZone`'s bubble
springs in at the panel's gutter. Different positions, same frames.

**Attempts so far, all landed and none sufficient:**
1. Deferred `RevealMascot` to Render priority so his clock starts with the window. Real bug (the
   cold first open), not this one.
2. Made the arming synchronous - opacity and scale set in the same frame - so he can never be
   painted at final values before the entrance runs. Real bug, not this one.
3. Delayed the bubble to 170ms so the peek's Toki has faded before it starts. Still reported.

**What to check next, in order:**
- Is he being REPLACED or MOVED? If `PlaceMascot`/`MascotShift.X` is gliding him from the peek spot
  to the gutter at the same time as a fresh spring, two motions are fighting over one element.
- The window recentres when it resizes, which shifts every window-anchored child instantly.
  `AnimateSize` compensates via `MascotShift.X`, but the compensation and `PlaceMascot`'s own X may
  be double-counting on the peek-to-panel path specifically.
- Consider not drawing a mascot inside peek CONTENT at all, and instead moving the one bubble. One
  element that travels cannot flicker; two that hand over always can.

## 🧊 MOTION + GLASS - shipped and open (corrected 2026-09-03)

**Corrected 2026-09-03.** This section previously claimed real DWM acrylic on the Library and grain on
the notch card as SHIPPED. Both were reverted the same day and the claim was left standing. Verified
against the code before rewriting: `WindowBackdrop` is never called, and `CardMaterial` carries no
grain.

**Shipped:** one animated path for every notch transition (the allowlist is gone; `SnapResize` is the
only opt-out); the cross-fade snapshot, with mascot-only peeks excluded; the clip re-origined inside
the resize; the settle moved onto the animation's own `Completed`; `GlideTopGap`; reduce-motion
(`Motion.Reduced/Ms/Rise/Stagger`); the Library's shadow casters hidden during open/close; the
recording flip at 240/220; `WarmFirstOpen` before the first summon; the mascot spring armed
synchronously and started at Render priority; `BottomBreath` 14 to 6; the settings toggle on a
transform; `NotesView.Cascade`; the three-stage loading bar with an elapsed readout after 20s.

**Reverted, with the reason, do not retry blind:**
- **Real DWM acrylic on the Library.** It worked. It was reverted because translucency lets the
  desktop tint the page, and on a LIGHT theme a white page over a dark desktop reads as grey. The
  owner caught it immediately. No scrim opacity makes a reading surface both glassy and stable.
  `Theme/WindowBackdrop.cs` is kept, uncalled, for the notch case where it may still be right.
- **Grain on the notch card**, now removed TWICE. `CardPlateOpaque` does the anti-banding work grain
  was reintroduced for. The earlier finding stands: it reads as texture.
- **An eased gradient-stop distribution.** Aimed at the notch plate, it silently changed `CardRim`
  too, because it edited a SHARED builder. Enumerate the callers before editing one.

**OPEN, in order:**

1. **The recording bar's visual redesign.** Now the top motion item: it has an owner-approved design
   and is the only one of these the owner has actually asked for twice. See the recording-bar section
   at the top of this file.
2. **Interruptibility, the rest of it.** The morph now starts its zoom from the live on-screen scale
   (`inFlight`/`liveScale` in `PinContentForMorph`, guarded by `InterruptibleMorphTests`), which was
   the visible half. The window-size and clip animations still restart from a fixed duration rather
   than carrying velocity. Smaller than it was, no longer the largest gap.
3. **A captured blurred backdrop for the notch** (Part H6 of the research): GDI `BitBlt` behind the
   window, downscale, blur, `ImageBrush`. This is now the ONLY route to real glass, since the DWM one
   is closed on reading surfaces. Judge it against the painted recipe before committing.
4. **`prefers-reduced-transparency`.** Moot while there is neither acrylic nor grain to drop. Revisit
   only if item 3 lands.

## 🔬 NOTE QUALITY (2026-09-02) - the current front

Full findings and the evidence for every line below: `docs/note-quality-research.md`.
Shipped today: `--groundtest` (the instrument), the thin-input gate, the name gate, and the extractive
citing map step. **Baseline to beat: 96.2% precision, 66.1% recall over 77 stored notes.**

**NEXT, in order:**

1. **Regenerate the Library and re-measure.** Every number above describes notes written by the OLD
   pipeline; nothing shipped retroactively improves them. Needs a persisting rewrite path -
   `--renotes` prints and discards today.

   **MEASURED 2026-09-03** with `--regentest 6` over the six longest stored meetings (read-only,
   nothing was written to the Library):

   | | old notes | current pipeline |
   |---|---|---|
   | mean precision | 93.3% | **100.0%** |
   | mean recall | 30.4% | 34.4% |

   better 3, worse 0, unchanged 3 on precision.

   **Read this carefully; it is not a clean win.** Precision reaching 100% on all six is a real
   result and consistent with the citation gate plus the name gate doing their job: every hard fact
   in the new notes was actually said. But **recall is noisy and two meetings got materially worse**:
   meeting 48 fell 46% to 12% and meeting 45 fell 38% to 0%, while 28 rose 15% to 62% and 27 rose 50%
   to 100%. A mean of +4pp across six meetings with that spread is not evidence of a recall
   improvement.

   Part of the drop is intended: the brevity pass was tightened (bullet cap 22 to 15) and a terser
   note has lower entity recall by construction. Part may not be. **Meeting 45 returning 0% recall is
   the thing to look at next** - a note that carries none of the meeting's hard entities is a
   different failure from a short one.

   Still open: the actual persisting rewrite. Regenerating the owner's real Library rewrites their
   notes and is an OWNER DECISION, not something to run unilaterally. `--regentest` deliberately
   saves nothing.
2. **Wire provenance through to the note. DONE 2026-09-03.** Both joins were missing and both are
   now closed. `LlmNoteWriter.AttachProvenance` attributes each finished bullet and action to the
   digest window it was rewritten from, and `MeetingStore.ReplaceNotes` now writes
   `src_first`/`src_last` the way `Save` always did (regenerating a note used to strip provenance
   off every action item silently).

   Three details worth keeping:
   - **Attribution runs LAST**, after the name gate and `Brevity.Tighten`. `NoteSection.Sources` is
     index-aligned with `Bullets`, and both of those steps can drop a line; attributing earlier
     leaves every source pointing one bullet to the left, which is a silent and plausible-looking
     fault. `Sources_stay_index_aligned_with_bullets` pins it.
   - **Matching is against the DIGEST, not the transcript.** The digest is short, already verified
     against the lines it cites, and in the speaker's own words, so it is a far easier match than
     the whole meeting. `Evidence.BestRange` then narrows WITHIN the recorded span.
   - **Silent when unsure.** A bullet under 50% overlap, or one two windows explain within 0.15 of
     each other, keeps a null source and falls back to text matching. A wrong evidence link is
     worse than none.

   13 tests in `ProvenanceWiringTests`, 5 of which were verified to FAIL against the unfixed code,
   including one that drives the real `WriteAsync` end to end with a scripted model so deleting the
   call site cannot pass.
3. **Evidence-gated sections.** Model returns line ids per section type; CODE decides a section exists
   above a threshold, so an absent section is an absence of evidence rather than a judgement. Lands
   after the format is settled.
4. **The note format.** Outcome line first (not a "Summary" heading), commitments second and anchored,
   topic-derived headings, "For you" above the body, "Risks / open questions", decisions and questions
   as earned inline marks rather than standing sections. Ban "discussed" as a lint rule shaped like
   `NoEmDashTests`. **Owner call on the shape before building.**
5. **Grammar-constrain the classification passes only**, never the prose pass.
   **MEASURED 2026-09-03: the map citation format is NOT worth constraining.** Compliance is already
   effectively perfect. Across a live `--regentest` run over the six longest stored meetings, the log
   line "window N came back uncited" fired **zero** times, so no window ever failed the format badly
   enough to lose its provenance. Per-window keep rates were 26/29, 18/22, 19/20, 19/23, 20/21,
   18/20, and the drops are almost entirely `unsupported`, meaning the cited line does not actually
   contain those words. **A grammar cannot fix that**: it constrains the SHAPE of the output, not its
   truth. Out-of-range citations were 0 to 1 per meeting.

   The remaining candidates are the passes that pick from a fixed set: template classification, and
   any future section-type labelling. The prose pass stays unconstrained, as originally decided. Do
   not spend the work on the map format.
6. **Microphone health check at meeting start.** Toki has measured this failure (mic 38dB below the
   meeting stream, half the transcript invented) and has better instruments than the products shipping
   the check.
7. **The measure.** The note column has no `MaxWidth`: about 140 characters in a wide window, 6 to 8
   words in the Library pane. Owner decision 2026-08-24 stands; the number to come back to is 600.

**Owner decisions parked here, NOT to be taken unilaterally:** auto-start recording with a cancellable
countdown (converts the ask-first rule to opt-out, CLAUDE.md rule 3); the note format in item 4; and
deleting the two seeded fake notes (ids 1 and 2) that `--seed` wrote into the real Library before it
was guarded.

## 🧭 OWNER RE-PRIORITISATION (2026-08-25) - THIS is the current order

Supersedes the 2026-08-19 open list below for ordering. Priority: (1) transcription/speaker quality,
(2) assistant & Ask, (3) UI polish, then blocked/rated, dismissed, MCP + debating (PRE-launch),
post-launch. Ratings and the live-transcription diagnosis are the owner's asks from this session.

### 1) Transcription & speaker quality  [TOP]

- **Live transcription preview - BUILT 2026-08-25 (streaming, roadmap #1).** Implemented option (b):
  a streaming zipformer-transducer (20M, k2-fsa, via sherpa-onnx - no new native lib) as the LIVE-preview
  decoder emitting words ~1s as spoken, with a pulsing "Live" readiness cue in the recording view. The
  SAVED transcript stays the whole-file Parakeet pass. Verified offline via `--streamtest`; the LIVE
  feel awaits the owner's real-meeting test. Original diagnosis kept below for the record.
  DIAGNOSIS: The live transcript
  appears in ~15-21s CHUNKS on Windows (fast on the Mac build). Cause: `LiveTranscriber` decodes a
  WINDOW only once `WinSec=15s` (+2x`SearchSec`=6s) of audio has piled up - so the FIRST line takes
  ~21s, then one every ~15s. Parakeet is an OFFLINE/windowed recogniser, not streaming, so there are
  no partial words. The Mac build is fast because it uses a truly streaming decode.
  RESEARCH VERDICT (2026-08-25, agent-sourced): live transcript IS a real product signal - Otter,
  Granola (it DOES show a live panel now), and open-source Meetily all show it - but NOT universal:
  Fireflies and Fathom deliberately show NO live transcript, so a fast POST-meeting transcript is a
  defensible choice too. Meetily does live in ~5s VAD-gated chunks (whisper.cpp / Parakeet-ONNX);
  whisper.cpp `stream` uses a 5s window / 0.5s step. THREE implementation options, cheapest first:
  (a) SHORTEN the existing offline window (WinSec 15 -> ~5s, `TOKI_LIVE_WINSEC`) + a LocalAgreement-2
  commit (only show words two consecutive windows agree on) so the preview doesn't flicker/rewrite -
  least new code, some extra CPU from overlap, closes most of the gap; (b) BEST for Toki's stack - a
  small STREAMING Zipformer-transducer via sherpa-onnx (already in-process, ~60-80MB model, true
  emit-as-you-go, ~160ms-1s latency, cheap CPU) as a live-preview-only path; (c) `nvidia/
  parakeet-unified-en-0.6b` (ONE model, offline+streaming, selectable 160ms-1.12s latency) IF sherpa
  supports its buffered-streaming cache. In ALL cases the FINAL saved transcript stays the whole-file
  Parakeet pass (accuracy floor + the "one whole-file call per stream" law); the live view is a
  disposable preview. RECOMMENDED: (a) as the quick win to validate the UX, then (b) for the
  instant-like-Mac feel. Gate the live decode behind the existing Silero VAD (Meetily credits VAD with
  ~70% of its efficiency).
- **Speaker diarisation (T8) - MOSTLY BUILT 2026-08-25 (roadmap #2).** Was already implemented
  (`SpeakerResolver`, transient) but (a) the models were NEVER downloaded by the app (only self-tests),
  so fresh users had no diarisation, and (b) it ran AFTER notes. Fixed: models now download at launch
  (segmentation + CampPlus); diarisation runs BEFORE notes (`TagSpeakers`) so the writer sees distinct
  labels (real names or "Speaker 1/2") not everyone "Them"; owners inferred after (`InferOwners`).
  Measured ~5s on a real meeting (RTF 0.005). Voiceprints TRANSIENT (no saved acoustic records);
  CampPlus already credited. LIVE flow (a fresh recording naming speakers) needs the owner's test.
  Remaining polish: the naming UX (rename "Speaker 1" -> a real name via SpeakerChips) and whether a
  note already written with "Speaker 1" should update on rename (regenerate covers it for now).
- **Warm mic + pre-roll ring buffer - DROPPED (research-decided 2026-08-25).** Wispr Flow (category
  leader) does NOT keep an always-on mic; it solves first-word clipping with a READINESS CUE ("start
  speaking when you hear the ping / see the bars move") + a fast wired mic. Genuine warm-mic exists only
  as an opt-in community workaround for macOS/Bluetooth's 2-5s wake; on Windows WASAPI startup is ~100ms
  (10x smaller), so the problem barely exists. KEEP the hard "NO always-on mic" rule. Instead do the
  rule-preserving fix = **gate the "Listening" indicator on the mic's FIRST real audio frame** (not the
  keypress), optionally with a tiny ready cue - literally Wispr's mechanism. A keypress-opened ring
  buffer can't recover audio spoken before readiness, so it adds little over the indicator gate on
  Windows and isn't worth the complexity unless testing shows clipping past the cue.
- **#3 items - ALREADY BUILT (confirmed 2026-08-25), backlog was stale:** **T4 action-item owners** now
  handled by #2's diarise-before-notes reorder (`InferOwners`); **pinned names** = `AppSettings.PinnedNames`
  + a Library Settings box (`LibPinnedBox`/`LoadPinnedSettings`), merged by `NameLexicon.Trusted` into the
  name-corrector AND the diarisation roster; **"Re-clean saved transcripts"** = the `LibRecleanButton` ->
  `OnLibReclean` -> `TranscriptCleanup.RunAll` (progress + status, notes/to-dos untouched); **underline
  corrected words** = `NoteRenderer.MarkCorrections` renders a dotted underline + a hover tooltip with the
  original ("Self-corrected from ..." / "Also transcribed as ..."). Nothing to build; owner can verify in
  the running app.
- **Multilingual Whisper opt-in** (~500MB, Hinglish); **add Whisper base.en to the ONBOARDING download
  list** (today new users get it via the launch background fetch, degrading to Parakeet until it lands).
- **Owner WER baseline recording** (real mic, `docs/wer-eval.md`); **rebuild meetings 92/93** from WAVs.
- **Five-critic ASR deferrals** (live-window seams at VAD boundaries, device-change gaps, drift
  accounting, handover-names-backchannel, inherited clusters, evidence-cap-keeps-head) - see the full
  list lower in this file.
- **Owed (licence, NOT parked):** CampPlus (Apache-2.0) attribution row in AboutView + notices now that
  diarisation ships.

### 2) Assistant & Ask

- **Fuzzy content & person-name matching** ("acton items", "Ana"/"Anna"), **nested attribution**
  ("what did Ana say Bob should do"), **multi-intent** utterances ("recap Monday and what's due Fri").
- **BYO-API path surfaced better** in-product (the real "sharp brain" lever); a small fallback model
  for tiny machines.
- **P10 dialogue-brain live-verify** - owner still owes a verdict on conversational feel.

### 3) UI polish

- **#39 revamp remainders:** shared empty-state control; layout-stability/text-trim sweep;
  **hover-expand "section pills"** (the revamp's signature).
- **Instant-hover holdouts** (AiSummaries SegButton, Calendar rows, About credit rows); **SpeakerChips
  rename popup** still pre-glass.
- **notch Home has no primary action** (wants a layout proposal); **focus/error/disabled/long-data
  states never audited** (biggest untested hole).
- Small nits: dead button styles, `TokiCheckOpen`'s undrawn outline, manual-note bullet superscript,
  `NotesReadyPeekMs` tightness, recording-stop snap, faint light-theme loader plate first-moments,
  `--themetest` gradient sampling, green-toggle/pink-sliver colour double-meaning.

### 4) Blocked / to-rate - do we still need these for a well-rounded product?

- **On-device meaning-search (embeddings) - RATING: MEDIUM-LOW, not required for launch.** What it is:
  semantic search over meetings by MEANING (vector embeddings) instead of only keyword (FTS5/BM25), so
  Ask finds a meeting even when the user's phrasing differs from the transcript wording ("what did we
  conclude about budget" when the transcript says "we agreed on the finances"). STATUS UPDATE: the
  embedder (bge-small) already SHIPS via LLamaSharp (not the old ONNX conflict), but it is NOT WIRED
  into retrieval - only self-tests touch it. Why it can wait: meeting search is mostly by names/topics/
  decisions that appear LITERALLY in the transcript, so keyword + the query-understanding layer covers
  most cases; and the recent Ask failures were transcription/routing bugs, never recall misses. VERDICT:
  a nice-to-have that a clean high-quality v1 can ship WITHOUT. Owner chose to add it (2026-08-25), but
  **EMPIRICAL RE-TEST (2026-08-25, `--embedconflicttest`) CONFIRMS THE BLOCKER IS REAL AND CURRENT:** the
  bge embedder runs fine ALONE, but loading it in-process ALONGSIDE Qwen (llama.cpp) - exactly the
  Ask-time scenario - HANGS (the embed never returns; sat minutes until killed). No crash, a HANG. So
  wiring it in-process would hang the Ask feature. It is NOT the old ONNX-conflict story (it's a
  two-llama.cpp-models coexistence hang). Proper fix = the OUT-OF-PROCESS CPU embedder (separate process +
  IPC) - a real milestone, not a quick wire. **DECIDED 2026-08-25: DEFER to post-launch (owner).**
  Research confirmed an Ask assistant is table-stakes (Toki has one) and semantic search is the recall
  failure users notice (Granola markets against keyword-only; Natively proves on-device RAG via SQLite +
  `sqlite-vec`), so it is worth building EVENTUALLY - but v1 ships fine on keyword + the guards. Parked.
  Model download deliberately NOT in production (unused 36MB until solved). `--embedconflicttest` kept as
  the diagnostic that proved the in-process hang.
- **RAM-tiering - SLICE DONE 2026-08-25.** The load-on-demand slice is built: `App.PrewarmLlm` (>=32GB
  prewarms + keeps the LLM resident; everyone else loads on demand for a lighter startup), distinct from
  `LowMemoryMode` (<8GB). Verified in the startup log on the 11.4GB box. The rest of RAM-tiering below is
  the marginal part, still parked. ORIGINAL RATING: LOW-MEDIUM overall; ONE slice worth doing. Today RAM is a single 8GB binary
  switch (a 12GB and a 64GB box behave identically). Full tiering (bigger context on 32GB+, etc.) is
  marginal polish. The ONE piece that is a real felt win: **load-on-demand model residency** (auto by
  RAM tier) - on a ~12GB box Toki prewarms + holds the 2.5GB LLM and feels heavy at startup; loading it
  on demand (first note of the session) would fix that. Do that slice; skip the rest for now.

### 5) MCP + debating features (PRE-LAUNCH, still deciding)

- **Local read-only MCP server** (localhost, Settings-gated) - lets Claude/other on-device AI read the
  meeting library. Safe, additive, honors every constraint; the low-risk half.
- **MCP client** (Toki reaches OUT to Notion/Linear/etc. on explicit per-send approval) - the outward-
  sending half; owner-gated, human-in-the-loop, start with ONE connector.
- **Follow-up email draft** from a note (one local-LLM pass + copy) - "can be there as an option, idk".
- **Link-sharing** for summaries / to-dos (share a meeting's summary or action items via a link).
  NOTE: sharing is an OUTWARD-sending feature like the MCP client - same constitution tension (crosses
  "nothing leaves the laptop"), same rule: opt-in, explicit, user approves every share.

### 6) Post-launch (parked until traction says so)

- **Toki 2.0 student-notetaker niche** - a class notetaker (homework/assignment due dates, test
  notices, lesson topics). Same engine, mostly prompts + a Class template + student framing. Build
  AFTER the general v1 launch, only if the Lecture gate shows students concentrating there.

### Building NOW

- **Mac version** - in active development (was parked; `HANDOFF-MAC.md` at repo root). The fast live
  transcription the owner sees is the Mac build's streaming decode (see item 1).

### Still undecided (explain, then owner picks)

- **Topic Trackers** - a SAVED search (an FTS5 query) pinned with a live badge/count, e.g. a "Pricing"
  tracker that shows how many meetings mention pricing and jumps to them, or an "Action items due this
  week" tracker. Cheap on the existing FTS index. Owner: "idk about topic trackers" - not parked, not
  picked. Decide: worth a shelf of saved queries, or clutter?

### 🗑 DISMISSED by the owner (2026-08-25) - do NOT build

- **Drag-reorder in the Library** - dead, not pursued. (Already removed from the code; this just
  confirms it stays gone.)
- **Bold / Italic in the note editor** - trashed. The numeric size dial replaced it; not coming back.
- **Per-app "always record"** - not needed. Toki already DETECTS a call and offers to record it, so a
  per-app always-on rule adds nothing but a consent-posture headache.

### Library virtualization - NOT dismissed (re-clarified 2026-08-25: now LOW-RISK, medium priority)

The owner's earlier "forget it" was conditional on the drag-reorder risk - but drag-reorder is already
GONE from the code, so that risk is moot and this is now a safe optimization. What it is: the Library's
`Cards` is a plain non-virtualizing `StackPanel` that builds a card for EVERY meeting (LibraryView.xaml
`Cards.Children.Add(Card(m))` in a foreach), so open/search/sort rebuild them all - measured 424ms at
54 meetings, ~343ms of it arranging off-screen cards, growing LINEARLY to ~3.9s at 500. Virtualization
= build only the ~6 visible rows (+buffer) and recycle containers on scroll, so the Library stays
instant at any count. NOT a launch blocker (dozens of meetings is fine); NEEDED within the first months
of heavy use before the count climbs, or the Library gets slower every week. Cheap alternative if
preferred: render first N cards + load more on scroll (paging), no container recycling. The "five other
`Cards.Children`-by-index sites" that made this risky are simple reads now that drag-reorder is gone.

---

## 📋 THE OPEN LIST as of 2026-08-19 - SUPERSEDED for ORDER by the 2026-08-25 section above (kept for the item detail)

Everything below was surfaced during the 2026-08-18/19 design-quality rounds and is NOT done.
Grouped by what unblocks it. This section supersedes older "next up" lists further down for
anything they disagree on.

### Waiting on the owner's word (posed as questions, never started)

- **DONE 2026-08-25: Short-utterance transcription for Ask ("Hi Toki" -> "Yeah").** Built the
  **Whisper base.en** decoder for the Ask path (owner chose it) - runs through the existing sherpa-onnx
  runtime as ONNX (no new native lib), robust on short clips because whisper pads to its 30s window.
  Parakeet stays the meeting engine. Downloads in the background at launch; credited MIT in AboutView +
  notices. Optional follow-up: add whisper to the ONBOARDING download list too (today new users get it
  via the launch background fetch, degrading to Parakeet until it lands). See
  [[ask-voice-hotkey-flicker-listening]].

- **Warm mic + pre-roll ring buffer for Ask-by-voice?** (2026-08-25) The single biggest listening
  win in the push-to-talk research: keep the WASAPI mic stream open continuously with a ~300-500ms
  lookback ring buffer, so key-down has ZERO init latency and the first word is never clipped. It
  directly contradicts the hard rule **"Voice: push-to-talk ONLY, NO always-on mic (owner decision)"**
  - a continuously-open mic IS an always-on mic, even if nothing is recorded until the key is held.
  NOT built. If the owner keeps the rule, the no-privacy-cost fallback is to gate the "Listening"
  indicator on the mic's first real audio buffer (so the user doesn't speak into a still-initialising
  mic). Owner to decide. See memory [[ask-voice-hotkey-flicker-listening]].

- **Delete the two dead button styles?** `ChipButton` and `SquareButton` in Theme.xaml have zero
  consumers since the glass wave. One-commit cleanup if yes.
- **`TokiCheckOpen` promises an amber outline it never draws.** Either draw the outline or drop
  the promise from the style - owner to pick which.
- **The remaining instant-hover holdouts** (pre-glass Border-with-mouse-handler rows):
  AiSummariesSection's SegButton, the CalendarRows clickables (hover-reveal rows, action/copy
  links, copy icon), and the AboutView credit rows. Sweeping them into the glass hover grammar
  is mechanical; owner said "say the word".
- **SpeakerChips rename popup still wears the pre-glass look** - same sweep, same word.
- **Library accent bars: keep blue/purple kind-coding or go one uniform colour?** Coding shipped
  2026-08-19 (blue = recorded, purple = manual note); collapsing to a single colour is a
  one-line change if the owner prefers.
- **Manual-note bullet dots sit at cap-height** (look superscript). Pre-existing, flagged by two
  audits, deliberately left; fix on request.
- **`NotesReadyPeekMs` = 2000ms** - the "Notes ready" click window before Toki flies to the
  peek. Flagged as tight; it is one constant if the owner wants it longer.
- **The somersault flight tuning** - bigger / slower / bouncier on request after the owner sees
  it live.
- **Recording-stop snap**: on the in-place path, stopping snaps RecordingOpen (48px) to the
  13px sliver with no animation. Noticed by the behaviour audit; if the owner ever reports "a
  blink when I hit stop", this is it - can be given a proper shrink morph.
- **Rebuild meetings 92/93** from their intact WAVs with the fixed audio engine (owner decision
  carried from the ASR-fix day).
- **Multilingual Whisper opt-in** (~500MB model choice in Settings) for Hinglish meetings.
- **The WER baseline recording**: owner records the script in `docs/wer-eval.md` on their real
  mic so the accuracy gate measures reality instead of TTS audio.
- **Outlook live sign-in** still needs the Azure app registration (owner does the portal step,
  guided - exactly as Google was).
- **Topic Trackers** - still undecided ("idk"), still neither parked nor picked.

### Owed to the owner's EYES (built and tested, but no harness can picture them)

- The evidence play/stop chips on a note with real recordings (glyphs are now drawn paths).
- The chat-history delete cross (now the house Path glyph) inside the history popup.
- The warm flip-to-peek on a GENUINE recording start (only reachable mid-meeting; guarded by
  code tests, never filmed).
- The Y/T speaker discs on the Library note page (probe-verified, below the harness fold).
- The full notes-ready flight and the ghost-frame-free summon, live, in both themes.

### Small debts seen in passing (assistant can do without a decision, just not started)

- **`--themetest` coverage narrowed by the glass**: its background-finder only accepts opaque
  solid brushes, so labels sitting on gradient plates are no longer contrast-measured (worst
  cases were hand-checked). Teach it gradient sampling.
- **Loader sliver plate looks faint in light theme** for its first moments (film observation,
  possibly just the shimmer phase - verify, fix if real).
- **The `--state` comma-path dev seam skips an explicit `hidden` hop** mid-path (TryGo rejects
  Home -> Hidden from the seam) - tooling nit, matters only for filming transitions.
- **ScrollThumb kept its instant hover swap deliberately** (drag-flicker risk) - revisit only
  with a fade that provably does not flicker during drag.
- **CollapsedView / RecordingBarView peek Grids use MouseLeftButtonUp** - documented-deliberate
  exception to "a clickable thing is a Button" (whole-peek hit areas); revisit only if the rule
  is ever made absolute.
- **design-web mirror drift**: StopRed was ported by hand 2026-08-19; the mirror still drifts
  whenever Theme.xaml changes - port by hand per `.design-sync/NOTES.md`, and a fuller re-sync
  is pending since the glass rounds.

### Still open from before these rounds (unchanged status)

- **Tasks #93/#94** - the Figma revamp remainders (the open front in the task list).
- **P10 dialogue brain live-verify** - the owner still owes a verdict on conversational feel.
- **Attribution row for CampPlus** (Apache-2.0) still owed on the Built-with screen.
- **The five-critic audit deferrals** (listed further down in this file).
- **On-device meaning-search (embeddings)** - still blocked by the native-runtime conflict.
- **The M8 ship path** - still parked until the product is final.
- **Mac version** - `HANDOFF-MAC.md` written 2026-08-19 at the repo root; starts whenever the
  owner opens that front.

## ✅ The 2026-08-13 batch - BUILT

The owner picked from the pending list and everything picked is done. **1,907 tests green.**

**Quality bugs from the real meetings:** template misfit (`TemplateGate` - a specialised template now
has to be earned by the transcript), self-corrections ("educative, oh, executive"), corrections shown
in the transcript with the original on hover, and "Re-clean saved transcripts" in Settings.

**UI:** the new-meeting composer reshaped like Google's and Outlook's (day/time pickers, prefilled,
labelled guests field, Meet as a switch); the manual-note page rebuilt as a document (720px measure,
no form rule, footer Delete); a numeric text size 1-12 replacing the parked Bold/Italic.

**Features:** "Open from last time" carries a recurring meeting's unfinished to-dos into the next
note. Evidence audio plays the moment behind a note line and can export the clip. Outlook joins Google
as a second, simultaneous calendar connection.

**Positioning copy** for the moats: `docs/positioning.md`.

### PARKED by the owner, 2026-08-13 - do not start without a fresh go

- **The whole M8 ship path** - "waits until the product is finalised". Signed installer, Velopack,
  licence gate, onboarding download, Google scope verification.
- **Bold / Italic in the note editor** - replaced by the numeric size dial. The collision with the S8
  per-Run FontWeight fix is written up in ARCHITECTURE.
- **Per-app "always record"** - "we dont need that".
- **Follow-up email draft** from a note.
- **Import / transcribe an existing audio file.**
- **Localhost-only MCP server** over the SQLite.
- **All performance and resource work** - RAM tiers, load-on-demand model residency.

### Undecided

- **Topic Trackers** (a saved FTS5 query with a badge). Owner: "idk about topic trackers." Not
  parked, not picked.

### Needs the owner before it can be finished

- **Outlook is BUILT but cannot sign in yet.** It needs an Azure app registration, exactly as Google
  needed an OAuth client. Ten minutes, free: `docs/outlook-setup.md`. Until then the button correctly
  says "Outlook sign-in isn't set up in this build yet".
- **Attribution rows** for the CampPlus speaker-embedding model (Apache-2.0) are still owed in
  `THIRD-PARTY-NOTICES.md` and `AboutView` now that diarisation ships. This is a licence obligation
  and it is the one part of the parked M8 work that is not actually parked.

---


*Plain-language capture of everything decided or discussed but **not yet built**, so any of it can be
picked up later without re-deriving it. Updated 2026-08-06. The canonical short roadmap lives at the
top of `CLAUDE.md`; this doc is the fuller version with the reasoning.*

---

## 🔴 T1-T8 "the first real meetings" - IN PROGRESS 2026-08-11

Two real recordings found what 1,800 tests could not. **Full analysis: `docs/real-meeting-findings.md`.**

**Done:** T1 transcript turn-merging + backchannel de-interleaving · T2 `NameCorrector` (corpus-wide
phonetic clustering, trusted-name override, conservative guards) · T3 summary never falls back to raw
transcript (and no longer poisons the title) · T7 a bare name is not a to-do, near-dupes collapse.

**Update 2026-08-12.** T2 is now WIRED (both the `Process` path and the LIVE path, which skips `Process`
and would otherwise have silently done nothing). T5's two constant costs are fixed and measured. T8 is
measured and blocked on two owner decisions. Details: `docs/ui-performance.md`,
`docs/real-meeting-findings.md`.

**T5 UI, decided by measurement, not opinion:**
- ✅ `Select()` no longer rebuilds the list to move a highlight: **424 ms → 0.4 ms**.
- ✅ Search debounced 180 ms: typing "pricing" costs 1 rebuild, not 7.
- ✅ New `--uitest` asserts budgets against the real database.
- ⬜ **Virtualization, now justified**: a build is 424 ms at 54 meetings and ~343 ms of that is WPF
  arranging cards nobody can see; it grows linearly (≈3.9 s at 500 meetings) while the visible portion
  stays at ~6 rows. Approach: `ItemsControl` subclass overriding `PrepareContainerForItemOverride` so the
  existing code-built `Card(m)` is reused, plus `VirtualizingStackPanel` with recycling. **Main risk:
  drag-reorder walks `Cards.Children` BY INDEX**: indices become positions in the realised window, not
  the list. Five other `Cards.Children` sites also need rework (see the table in `docs/ui-performance.md`).

**T8 diarisation, blocked on two owner decisions:**
1. **Persistent voiceprints?** Owner leaned toward "only people you name" (the Circleback model, plus an
   Otter-style "Rematch" over old meetings). Not yet confirmed explicitly, and it stores biometric data
   for colleagues who did not consent. Transient-only is the zero-exposure alternative.
2. **When does it run?** Now that it is ~50 s/hour rather than ~7 min, running it BEFORE the notes is
   affordable and is what would stop a roll-call becoming to-dos.

Everything else for T8 is measured and designed: ASR-guided clustering (10x, published as
Turn-to-Diarize), average linkage, calendar count as prior, conservative default, user-settable count.

**Still to do, in the order they matter:**
1. **T8 diarisation** - 11 people collapse into "Them". sherpa-onnx 1.13.4 already ships the whole
   toolkit in its .NET binding (`OfflineSpeakerDiarization`, pyannote segmentation,
   `SpeakerEmbeddingExtractor`, `FastClusteringConfig`, `SpeakerEmbeddingManager` for voice enrollment).
   Blocking unknowns: model licences for commercial redistribution, model sizes, CPU cost on a 30-60 min
   WAV, and the naming UX. Under research.
2. **T4 action-item owners** - all four of meeting 75's to-dos belonged to the owner and none said so.
3. **T5 UI rebuilds** - `Select()` rebuilds all 52 cards to move one highlight; `OnSearch` re-queries and
   rebuilds per keystroke; `Cards` is a non-virtualizing `StackPanel`. Fix the first two, MEASURE, then
   decide on virtualization (it is the riskiest: drag-reorder depends on child indices).
4. **Wire T1/T2 into the save path** - both are built and tested but not yet called when a meeting saves.
5. **Settings: pinned names** ("Names & terms Toki should get right") - the escape hatch for what
   corpus frequency cannot decide, e.g. "Yashtren".
6. **"Re-clean saved transcripts"** button - owner chose OFFER, never automatic. Re-merges turns and
   re-runs name correction over saved meetings; summaries are NOT rewritten.
7. **Underline corrected words** in the transcript, original on hover - owner chose marked-in-transcript
   only, notes stay clean.

**Known and deliberately unfixed:** "Yashtren" -> "Yashina" (4 edits, different phonetic keys - widening
the thresholds would start merging genuinely different people; the pinned list is the answer). Template
misfit: both meetings were classified "Decision log" when neither made a decision, so Rationale reads as
invented justification. Self-corrections ("educative... oh, executive") are ignored.

---

## ✅ S8 "polish pass" - BUILT 2026-08-06

**1,774 tests green.** Em dashes gone project-wide (3,089 across 188 files; 3 functional exceptions
guarded by `NoEmDashTests`); one type ramp with **Settings as the reference** (`PageTitle` etc., SemiBold
everywhere, Bold only for micro-elements); Templates left-aligned like every other page; hit targets fixed
at the STYLE level (`PillButton`'s hover was an opacity change, invisible on transparent buttons, so those
had no hover at all); new shared `RowButton`; Copy is one split pill; nav rows light on hover.
Detail: README "S8", ARCHITECTURE "S8 decisions".

**And one real bug:** the "mosquito text" the owner reported was **losing text**, not just shrinking it -
a blank line drawn at `FontSize=6` stayed tagged Blank after being typed into, and Blank blocks are
written as nothing. Now covered by four checks in `--notetest`.

**Left open on purpose:**
1. **The Library window's "Built with" row** still uses the Border-on-negative-margins pattern that the
   notch's "All other settings" was just converted away from. Not flagged by the owner, so left alone;
   switch it to `RowButton` next time that page is touched.
2. **EN dashes remain** in numeric/time ranges ("9:41 - 10:11", "2-4 sentences"). Deliberate - that's what
   the character is for - but the owner may want them flattened to hyphens too.
3. The type sweep covered TITLES and section headers. Body/caption sizes were left as they are; if the
   window still reads inconsistent, that's the next layer.

---

## ✅ S1–S7 "sticky notes, done properly" - BUILT 2026-08-06 (awaiting real-meeting bug-testing)

**1,759 tests green**, published to `dist`. This batch came straight out of the owner's first test of
N1–N7. Detail: README "S1–S7", ARCHITECTURE "S1–S7 decisions".

**Closed by this batch:** #58 (the pre-meeting nudge is back, but only surfaces when the notch is idle
and nothing is fullscreen), #59 (sticky notes are real database rows now, not a JSON file that never
pruned), #61 (action items carry ↳ evidence links).

**What came out of it that ISN'T finished:**
1. **Sticky notes aren't in the Markdown mirror** - every other note is readable in Obsidian/Notepad from
   `%APPDATA%\Toki\Notes`; sticky notes live only in `toki.db`. That was a deliberate call (a second
   write path is a second thing to drift), but it does mean one class of hand-written text isn't
   plain-text-readable. Options: mirror them under `Notes/Sticky/`, or fold them into the meeting's own
   `.md` under a heading. Needs an owner decision about which.
2. **Sticky notes are unreachable except through their meeting.** If you write one for a meeting you
   never record, the only way back to it is the prep card's picker. There is no "all my sticky notes"
   view. Probably fine; worth watching whether notes get lost.
3. **The ↳ silence is still ambiguous** - a line with no arrow could mean "no confident match" or
   "nothing to match against". The floor is doing its job (a wrong link is worse than none), but the user
   asked about this directly, and a one-line explanation somewhere near the TRANSCRIPT header would
   answer it cheaply.
4. **Audio playback for evidence links** (task #60) - still text-jump only. A player (seek/scrub/waveform)
   is its own milestone.
5. **Re-writing a note with a different template** is now safe (the summary never depended on typed
   notes), so the old N6-era caveat is gone.
6. **`LibraryWindow.ShowMenu` and `Views/NotePicker` are two implementations of the same themed
   dropdown.** The notch pair was unified; the Library one wasn't, to keep this change's blast radius
   small. Worth merging next time either is touched.

---

## 🎯 THE BUILD AS SPECIFIED (owner regroup, 2026-08-05) - kept for the reasoning

The owner reviewed the whole pending list and picked. **Built in this order (N1→N7):**

| # | What | Why it's here |
|---|---|---|
| **N1** | **The note editor** - one reusable editable surface: text, 3 sizes, to-do checkboxes, lists, spacing, template insertion. **No font family, no colour** (owner: "nothing outside normal text… just size, templates, spacing, freedom"). Markdown-backed. | Everything below is this one component in a different frame. Build it once. |
| **N2** | **Manual notes in the Library** use N1 - templates, to-dos, action items, all insertable by hand. Plus a "Custom note" entry point. | Today a manual note is a bare `TextBox`. |
| **N3** | **The prep card becomes editable** - keeps its generated content, but the user can add to-dos and free text on top. "Like a blank Notion page, our version." | This *is* the "manual/custom notes as a separate section" answer - resolved, see below. |
| **N4** | **Prep card surfacing: auto-surface OFF → hover-only** + a **"Prep card" button in the recording state**: hover the peeking Toki → recording page drops → Prep card → stays open while hovered → Back returns to recording. | OWNER DECISION. Resolves the long-pending auto-vs-hover question. |
| **N5** | **Save-or-discard the scratchpad** into the Library after the meeting, classified as manually created, chosen **on the preview card in the note list itself**. | No hollow entries; the user decides. |
| **N6** | **Typed-notes augmentation** (was research item C#14) - the LLM merges what you typed during the meeting with the transcript, so your fragments steer the emphasis. | The highest-leverage note-quality item in the research. |
| **N7** | **Evidence-linked notes** (was C#15) - every bullet jumps to the transcript line that produced it, highlighted. **Text jump only**; the audio player is deliberately deferred to its own milestone. | Cheapest way to close the perceived-quality gap with cloud LLMs. |

**Owner decisions this settles (removing them from "pending"):**
- **Manual/custom notes - section vs filter: ANSWERED.** Neither, as originally framed. The editable
  prep card / sticky note covers the need, *plus* a dedicated custom-note button. The "Manual notes"
  filter already shipped in H2 and stays.
- **Prep card auto vs hover: ANSWERED - hover-only**, with the in-recording button as the second door.

**Everything else stays parked**, explicitly: the whole **M8 ship path** (revisit "once we get closer to
the actual complete and confident build"), the rest of section B's owner decisions, the remaining
research features, the #39 UI passes, the assistant-brain limits, and the infra work below.

---

## ⭐ Next up (updated 2026-08-05, after the v1-iter3 premium-UI pass)

**UI revamp #39 - passes ①, ⑤ and the premium redesigns are DONE.** ① motion tokens + ease-out morph +
message fade-up + notch jump-pill + 3-dot thinking indicator (v1-iter2); ⑤ the jump-pill in the **Library**
chat (v1-iter3). Also shipped in v1-iter3: settings consolidation (notch = UI/visibility only), the
compact recording page, the Luma-style Ask landing (**without** the example-question chips - owner removed
them), the in-house themed sort popup, the calendar/COMING-UP/PrepCard premium redesign, the new mascot +
7-size app icon, and ghost mode re-asserted on every notch state.

**Remaining #39 passes:** ② shared **empty-state** control (mascot + line + one action) across
Library/Ask/Calendar-disconnected/no-speech; ③ **layout-stability / text-trim sweep** (`TextTrimming` on
single-line, `MinHeight` reservations, `UseLayoutRounding`); ④ **hover-expand "section pills"** (rows grow
on hover → inline actions - the revamp's signature). None of these are blockers; they're the last coat of
polish.

**The real next milestone is the M8 SHIP PATH** (below): signed installer, Velopack auto-update, licence-
key gate, onboarding download polish, Google sensitive-scope verification. Everything else on this list is
optional; that list is what stands between Toki and being sold.

**Parked (owner, 2026-08-04) - ASR model quality** on short push-to-talk. Full resume plan lives in the
`asr-and-notch-reply-quality` memory: clipped-start fix, VAD/padding, phonetic post-correction for domain
words, a relevance floor so a garbled input can't retrieve a random meeting, or a stronger ASR model. The
"voice returns nothing" root cause this session was a MUTED Windows mic, not a bug (`--mictest` now
diagnoses device/peak in one command).

**Attribution screen - DONE 2026-08-04** (was "next" below; shipped as M8.0, see README). The M8 SHIP PATH
(installer / auto-update / key gate / onboarding download / Google scope verification) is still the road to
actually selling.

---

## Attribution / "Built with" screen - DONE (kept for the reasoning)

### The licence obligation that unblocked charging money
A real credits screen (in Settings/About) listing the on-device models and their licences. This is a
**legal obligation, not a nicety** - the app cannot be sold until it ships:

| Model | Role | Licence |
|---|---|---|
| Parakeet TDT 0.6b v2 | transcription (ASR) | **CC-BY-4.0** (requires attribution) |
| Qwen3-4B-Instruct-2507 | notes + Ask answers (LLM) | Apache-2.0 |
| Whisper small.en | ASR fallback | MIT |
| bge-small | embeddings (deferred/blocked, but shipped in-tree) | MIT |

Scope: a scrollable "Built with" section reachable from Settings/About, each entry with name, what it
does, licence, and link. CC-BY-4.0 (Parakeet) specifically **requires** visible credit. Keep it in the
product's design language (dividers-not-boxes, calm). Small, self-contained, unblocks charging money.

---

## 🚢 Ship path (M8) - the grind to actually distribute & sell

- **Signed installer** (.exe) - code-signing cert so Windows/SmartScreen doesn't scare users.
- **Velopack auto-update** from GitHub Releases - a meeting must never wait on a download; update in
  the background.
- **License-key gate** - Gumroad or Lemon-Squeezy one-time $20 key (no subscription; Hard Constraint #1).
- **Onboarding polish** - first-run model download UI with progress (models downloaded at onboarding,
  warmed every launch - a meeting never waits on a download).

## ✨ Features

- **M7 Calendar** - ✅ **foundation DONE (2026-08-03, live-verified):** Google OAuth (loopback + PKCE),
  DPAPI-encrypted refresh token, raw-HTTP `Calendar/` module (list/insert/delete), `CalendarService`
  facade, Home + Settings connect/disconnect, `--caltest`. See `docs/google-setup.md`, `ARCHITECTURE.md`
  "Google Calendar foundation", the `calendar-foundation` memory. **Surfaces still to build (sit on the
  facade). **UI DONE (M7.1, 2026-08-03, render + live verified):** connected Home "COMING UP" glance;
  Calendar page (`PillState.Calendar`) agenda-by-day week view + now-line + week nav + Join; **create**
  (one-box natural language via `QuickEventParser`), **edit** (`events.patch`), **delete** (undo for solo /
  confirm for guested). See `mansplain/25`, README M7.1, ARCHITECTURE "Google Calendar UI", the
  `calendar-foundation` memory.
  **Prep cards DONE (M7.2, 2026-08-03, render + logic verified):** ~5-min-before auto-surfaced prep card
  (LAST TIME recap + STILL OPEN items w/ owners + AGENDA), deterministic/no-LLM, honest first-time state,
  restrained `PrepWatcher`; `--preptest`, `--state PrepCard`, `TOKI_PREP_LEAD`. See README M7.2,
  ARCHITECTURE "Pre-meeting prep cards", `mansplain/26`.
  **Voice-create DONE (M7.3, 2026-08-03):** "schedule a call with Ana tomorrow at 3" on push-to-talk →
  confirm-gated create (`Llm/CalendarCommand.cs`, two tiers in `NotesQa.AskStreamAsync`,
  `ConversationContext.PendingCreate`); `--voicecreatetest`, 472 tests. Also fixed the shared parser so a
  bare "at 3" = 3 PM. See README M7.3, ARCHITECTURE, `mansplain/27`.
  **Still to build:** (1) **owner confirm** the prep-card **auto-surface vs hover-only** choice (currently
  auto; one-line switch); (2) optionally let an imminent COMING-UP row on Home open its prep card directly.
  Outlook is post-v1 (UI provider-neutral - `CalendarService.ProviderName` is the seam). Ship-side: Google
  sensitive-scope **verification** (privacy policy + demo video) before selling → M8.
- **Speaker diarisation** - real named speakers in transcripts instead of just YOU/THEM. (Task
  name-tags already done; transcript-level diarisation is the remaining, bigger piece - pairs well with
  the on-device-embeddings unblock.)

## 🎛 Polish / UX

- ✅ **Library filter/sort menu - DONE.** Newest / Oldest / Longest / Manual-notes-only shipped in H2;
  v1-iter3 replaced the native Windows `ContextMenu` with the **in-house themed popup** (Toki surface,
  hairlines, pink check on the active mode) so it stops looking like a system control.
- **Manual/custom notes** - owner decision still pending: a separate left-panel section vs. a "Manual
  notes" filter in the sort menu above.
- ✅ **Notch glassy/blur toggle - DONE (M9.0, 2026-08-03).** Settings → Appearance → "Glassy notch": a real
  DWM acrylic blur-behind (theme-aware frost, one size, flush top, rounded bottom, off by default). See the
  `redesign-and-glass` memory + ARCHITECTURE "glassy-notch blur" for the full interop.

- **⭐ Premium redesign - pass 2 (mostly done; one piece left).** Pass 1 shipped (type ramp, green
  `TokiCheck`, `TokiSwitch`, `ChipButton`, colour-coded note headers, flattened composer, "Calendar"
  rename, one-accent icons, glass notch). Pass 2 **shipped in v1-iter3**: the calendar/COMING-UP/PrepCard
  redesign (accent bars, derived attendee avatars, amber countdowns, Join), the compact recording page,
  the Luma-style Ask landing, the themed sort popup, the type/spacing sweep, and the motion-consistency
  pass (everything now draws from `Theme/Motion.cs`). **Still to build:** the Wispr/Vercel **hover-expand
  "section pills"** (rows grow on hover → reveal Join/Prep/Edit inline; Toki already has the hover engine
  in `PillWindow`). Full spec is in the Fable design audit; summary in the `redesign-and-glass` memory.

---

## 🧠 Discussed today (2026-08-03) - captured so we don't lose it

### 1. Graduated adaptive-resource scaling (RAM tiers) - **idea, not built**
Today's finding (verified in code): Toki's resource adaptation is **coarse**, not graduated.
- **RAM** drives a single binary switch: `App.LowMemoryMode = totalGB < 8.0` (one model resident at a
  time + no LLM prewarm below 8 GB; both warm above). A 12 GB and a 64 GB machine behave **identically** -
  RAM past 8 GB is unused.
- **VRAM** drives 3 GPU-offload tiers (`LlmRuntime.Resolve`): ≥7 GB full offload, ≥3.9 GB partial (20
  layers), else CPU. Context size follows the VRAM tier, **not** RAM. Model is always Qwen3-4B.

Proposed enhancement - a genuinely graduated policy:
- **RAM tiers** instead of one cutoff: e.g. **16 GB+** → larger context window / keep more meeting
  history in the prompt; **8–16** → today's normal; **6–8** → low-memory; **under ~6 GB** →
  heuristic-only floor (a 4 GB machine physically can't hold the 2.5 GB LLM + 630 MB ASR + Windows).
- Optionally a **smaller answer-model** (1–2B) on tiny machines so they still get AI notes, not just the
  heuristic floor.
- Bigger context / longer answers when there's headroom.

Honest caveats: 4 GB *total* RAM is the heuristic-only floor regardless; the biggest speed lever stays
**GPU/VRAM**, not RAM. (See the `dev-pc-specs` memory for the exact current thresholds. Dev box = 12 GB
RAM / RTX 3050 4 GB → "normal mode", partial GPU 20 layers.)

**⭐ TODO - automatic load-on-demand for the AI model (decided 2026-08-03).** Real finding on the owner's
12 GB machine: Toki **prewarms the ~2.4 GB Qwen model at launch and keeps it resident**, which on a 12 GB
box (≈11.3 GB usable, often only ~1–3 GB free with Chrome/Slack/etc.) leaves things tight and feels heavy
at startup. Measured clean (no leak - closing Toki returned the full ~2.4 GB). **Decision: make model
residency AUTOMATIC by RAM tier, not a manual toggle** - a modest-RAM machine (≈≤16 GB) uses **load-on-
demand** (model loads only when notes/Ask need it, then can unload after idle; costs ~10 s on the first
note per session), while a high-RAM machine (32 GB+) keeps today's **prewarm** for instant notes. Zero
config, fits Toki's "just works" ethos. Keep an optional Settings override - "Keep AI model loaded:
**Auto** / Always / On-demand" - defaulting to Auto, for power users only. This is the concrete first
slice of the RAM-tiers work above; the prewarm switch lives near `App.LowMemoryMode`.

### 2. BYO-API key as the "sharp brain" lever - **partially exists; not surfaced**
The P10 dialogue brain (`docs/ask-toki-brain.md`) routes turns and builds plain system+user prompts, so
the **same brain drives either backend** - local Qwen3-4B or the user's BYO API model - with identical
routing, memory, and honesty rails; only the generator swaps. Consequence discussed today: the honest
ceiling for *general conversation & reasoning* on a 4-bit 4B on-device model is "competent but modest";
the **API key is the real upgrade** for users who want frontier-model sharpness. Not-yet-built ideas:
- Surface/encourage the BYO-API path better in-product (esp. for the general/reasoning turns), while
  keeping on-device the private, $0-forever **default**.
- Consider a small local fallback model for tiny machines (ties into RAM tiers above).
- Keep Hard Constraint #1 intact: on-device is free forever; the API key is the user's own cost.

### 3. P10 dialogue brain - verification & tuning still open
- **Live-verify pending:** the owner still needs to judge the *conversational feel* (pacing, warmth,
  phrasing) of the on-device general-chat path - that's the one thing tests can't prove (live-script
  step 10 in `docs/ask-toki-brain.md`). 428/428 automated tests are green.
- **Tuning knobs left** (only if the live run shows problems): if general answers ramble →
  `GeneralChat.MaxTokens` + a second exemplar in `GeneralChat.BuildSystem`; if a grounded turn ever
  fabricates → the retrieval relevance floor + `LooksLikeMiss` detector, both in `NotesQa`.

---

## ⛔ Blocked

- **On-device meaning-search (embeddings / hybrid / reranker)** - blocked by a native backend conflict
  (sherpa-onnx vs. the embedder). Fix = an **out-of-process CPU embedder**. See ARCHITECTURE "Why
  on-device embeddings are deferred". None of the recent assistant failures were paraphrase misses -
  they were routing/state/precision bugs, now fixed deterministically - so this stays deferred.

## ⏸ Parked (owner-deprioritized)

- **Live transcription preview** (streaming Zipformer, preview-only, never saved).

---

## ✅ Done this session (2026-08-02→03) - for context

Voice (Ask-bar mic + push-to-talk notch), mascot redesign + animation vocabulary, and the four-part
**assistant intelligence** arc - all Fable-owned, all verified (build + tests + render + republished
`dist`), all documented:
- **P8** (`docs/ask-toki-redesign.md`) - routing/screen-grounding/persona/tone fixes.
- **P9** (`docs/ask-toki-reasoning.md`) - the Analyze route, self-reference, the command set.
- **P9.1** (`docs/ask-toki-refinements.md`) - wake-name fuzzing, honesty, identification, title recall,
  the clipped-notch + friendly-app-names.
- **P10** (`docs/ask-toki-brain.md`) - the **dialogue-brain rebuild**: turn-type routing, user
  profile/memory, RAG-vs-parametric, anti-fabrication rails. 428 tests green.

---

## 🛡️ Hardening pass - DONE (H1, 2026-08-04)

The full-codebase audit + fix + verify pass (see README H1 / ARCHITECTURE "The hardening pass"). All
14 high- and 21 medium-severity findings fixed, 472 tests green, dist republished & smoke-verified.

**Deliberately deferred from that audit (refactor-only, no user-visible effect, regression risk in
delicate code):**
- NotesQa's three near-identical tokenize pipelines (TitleTerms / ContentQuery / ProbeTerms) - consolidate
  only when next touching retrieval.
- Token-patch streaming plumbing duplicated between the notch (App.HandleNotchText) and the Library Ask.
- The state→view factory duplicated between PillWindow and RenderHarness.

## 🧭 Research-derived opportunities (2026-08-04, competitor + UX research - owner picks)

Full research in the session notes; ranked by leverage-per-effort, all within the hard constraints:

1. **Typed-notes augmentation (the Granola model)** - a scratchpad in the recording view; at meeting
   end the LLM merges your fragments (steering emphasis) with transcript-derived expansion. The single
   most-praised note UX in the market; Fathom just copied it. Manual-notes plumbing is a head start.
2. **Evidence-linked notes** - each note bullet carries a tap-to-transcript timestamp (and play-the-WAV).
   Research says users forgive a small model's phrasing when they can check the source in one click -
   the cheapest way to close the perceived-quality gap with cloud LLMs. Toki already keeps the WAVs
   (competitors' top complaint is no way to verify).
3. **"Open from last time" in notes for recurring meetings** - feed the prep card's STILL-OPEN query into
   note generation. Circleback's stickiest feature, deterministic here.
4. **Per-app "always record"** - surface the choice on the meeting prompt ("Always for Zoom"). Hard
   Constraint #3 explicitly allows it; closes the market's known "missed meeting is gone" failure mode.
   OWNER DECISION - it changes the consent posture per app.
5. **Speaker names v1 without diarisation** - map "them" to calendar attendee names for 1:1s (Notion's
   July-2026 approach). The #1 universal complaint category-wide; cheap first step before real diarisation.
6. **Suggested follow-up questions in Ask** (2–3 tappable, Fireflies pattern) + **Topic Trackers**
   (a saved FTS5 query with a badge) - both nearly free on the existing architecture.
7. **Follow-up email draft** from a note (one local-LLM pass + copy button) - reviewers use it constantly.
8. **Import/transcribe an audio file** - the whole-file ASR path exists; it's a UI affordance.
9. **Consent-responsibility notice** - botless recorders shift disclosure onto the user; a first-run
   note + optional "remind me to announce recording" line on the record prompt is the good-citizen
   pattern (Granola/Otter are in litigation over exactly this). Belongs in M8 onboarding.
10. **Tray recording indicator** - ghost mode hides the notch from capture, so the tray icon should
    reflect recording state too.
11. **Local(host)-only MCP server over the SQLite** - 2026's power-user tell (everyone shipped one);
    zero upkeep, data stays local, lets Claude/ChatGPT desktop query your meetings.
12. **Market the existing moats**: the Markdown mirror (Obsidian-ready files = loud unmet demand),
    ghost mode ("invisible in screen shares"), and offline-forever notes.

**Explicitly not worth copying** (research-confirmed): meeting bots, engagement/sentiment scoring,
viral sharing mechanics, video recording, CRM stacks, voice agents, 24/7 ambient capture, 100+
cloud languages, team sync.

---

## H2 - DONE (2026-08-04): task-scoping fix, Library sort, security pass

Assistant due-date scoping, Library sort menu, and a full security threat-model pass - see README H2 /
ARCHITECTURE. 520 tests green, dist republished.

**Deliberately deferred from the security audit (documented decisions, not oversights):**
- **RegisterHotKey instead of the WH_KEYBOARD_LL push-to-talk hook** - owner chose hold-to-talk, which
  needs key-UP that RegisterHotKey can't give. The hook callback already branches only on the chord and
  never stores other keys. Keep the hook; document it on the privacy/about screen when that copy is written.
- **DPAPI optionalEntropy parameter** - would fail to decrypt every EXISTING stored secret (wiping the
  user's API key + Google refresh token) for marginal gain (same-user malware can call our code path
  regardless). Skip unless we add a migration.
- **Move bulk data (DB/WAVs/Notes/log) from %APPDATA% (roaming) to %LOCALAPPDATA%** - stops meeting
  content silently syncing across an org's roaming profiles, but needs a one-time migration of existing
  users' data. Owner-gated.
- **WER/crash-dump scrubbing** - a full-heap .dmp could contain the in-memory access token / decrypted
  key. Low likelihood; revisit with the signed-installer/packaging work (M8).
- **Assistant answer citations "jump to transcript moment"** and the full `--braintest` fixture-DB golden
  harness - the targeted regression tests cover the fix; the big harness is a larger investment for when
  the brain is next reworked.

**Still open from H1's research list** (unchanged - owner picks): typed-notes augmentation, evidence-linked
notes, per-app always-record, speaker names from calendar attendees, follow-up email draft, audio import,
local MCP server, tray recording indicator, consent notice in onboarding.

---

## H3 - DONE (2026-08-04): assistant brain rework + 1,155-case prompt-test suite

Stress-test-driven rework of the Ask brain - see README H3 / ARCHITECTURE. 1,636 tests green, dist
republished. The golden set lives in `tests/Toki.Tests/Brain/` (fixture + frozen clock + RouteOf).

**Known assistant limitations left as future work (surfaced by the taxonomy, deliberately not fixed):**
- **General ASR/typo tolerance beyond the wake name.** Toki fuzzes "Toki"→Doki/Toky but does NOT
  fuzzy-match content words: "acton items" won't hit "action items", "meating" won't hit "meeting",
  "due"↔"do" isn't merged (too risky - "do" is ubiquitous). A phonetic/edit-distance normalization pass
  before parsing would close this; it's a real feature with real false-positive risk. Homophone table is
  in the research (scratchpad/taxonomy-spec.md §15.2).
- **Fuzzy PERSON-name matching.** "Anna" vs "Ana", "Sean/Shawn" - the person filter matches known owner
  names but doesn't yet do phonetic/edit-distance matching or a "did you mean?" clarify.
- **Nested attribution** ("what did Ana say Bob should do") - parsed as a single person scope, not two roles.
- **Multi-intent in one utterance** ("recap Monday and what's due Friday") - answers the first, doesn't split.
- **"tips for running a standup"-style ambiguity** - a meeting word inside a general how-to grounds on the
  corpus (defensible either way; left grounded).
- **Speaker-attribution honesty** (who said what) is coarse until named diarisation ships (YOU/THEM only).
- Deeper generation-side answer-quality (faithfulness scoring, citation-moment links) is a separate,
  model-in-the-loop eval - the deterministic layer is what this suite locks down.

## Deferred from the five-critic audit (2026-08-18)

24 confirmed findings were fixed the same day (see README + ARCHITECTURE). These were confirmed or
plausible but deliberately deferred, each because the fix is invasive relative to its harm:

- **Live window seams cut at quiet points, not VAD boundaries.** Roughly every 15s of CONTINUOUS
  speech risks one garbled token where a word straddles two windows. Proper fix: consume only up to
  the last completed VAD region per window and hold the tail for the next one (needs a consumed-samples
  contract between LiveTranscriber and Transcriber, plus forward-progress guarantees). Medium effort,
  small recurring quality gain.
- **Device-change gaps insert no silence.** A Bluetooth mic re-pair mid-meeting shifts the "you"
  timeline earlier by the gap length; if it exceeds ~4s the echo de-dup window and merge ordering are
  wrong from then on. Fix: track the gap and write zeros into WAV + live tap on rebuild. Also: the
  loopback capture thread has no rebuild at all - if it dies, "them" ends silently.
- **Live-pipe drift accounting.** Buffer overflow (post-fix: only during extreme stalls) still shifts
  timestamps with no detection. A fed-vs-decoded seconds comparison per pipe could log and correct it.
- **Handover names the backchannel.** "Over to you, Priya" → cluster of whoever says "yep" first gets
  named Priya. Needs interjection-aware NextOther. Related: a turn-initial vocative credits the NEXT
  speaker where it usually names the PREVIOUS one.
- **Inherited clusters feed the namer.** A sub-0.6s "Priya here." inherits the previous turn's cluster
  and its self-intro then names the WRONG voice. Fix: mark inherited turns; exclude them from votes.
- **Evidence 45s cap keeps the head of the range.** A bullet whose matched words sit in the tail of a
  long merged range plays a clip that never reaches them. Fix: anchor the cap around the best-matching
  line, not the range start.
- **No live Library refresh when the LLM title lands.** During the up-to-300s wait an open Library
  shows the placeholder title; MarkDirty only applies on next open.
- **Lecture gate dominance** merges all remote voices into "Them", so any remote-dominated call with
  one teaching phrase can pass as Lecture (cue count keeps this rare).
- **Attendee named an ordinary word ("May", "Will")** is now never auto-corrected toward (the
  correction-magnet fix) - a deliberate miss; pinning can't override it currently.


## Design taste audit, 2026-08-24 (MOSTLY APPLIED same day - see below)

Full findings: `docs/design-audit-2026-08-24.md`. Every notch state and Library page run against
`.claude/skills/design-taste/SKILL.md`. The owner asked for the findings to be applied; what shipped
and what did not is in ARCHITECTURE under "Acting on the taste audit". Still OPEN from this list:

- ~~**Green Settings toggles and the pink Summarizing sliver**~~ DONE 2026-08-26: both moved to brand
  Blue (= Toki-active chrome), off the reserved content colours.
- **notch Home has three actions in two treatments and no primary.** Real; layout redesign, not a
  refinement. PROPOSAL OUT 2026-08-26 as an artifact (unified-quiet-nav [recommended] / take-note-primary
  / library-primary) - awaiting owner pick, then build in WPF.
- **Focus / error / disabled / long-data states** - AUDITED 2026-08-26 (Fix 5). DISABLED gap fixed
  (shared button styles dim + Arrow cursor on IsEnabled=False). FOCUS: app is deliberately mouse/hotkey-
  driven (Focusable=False; notch never focuses) - added `TokiFocus` ring + made Settings toggles
  keyboard-operable; **full button keyboard-nav is a bigger a11y project still OPEN** (needs tab-order
  design + testing). LONG-DATA + ERROR were already handled (trimming/measuretest; FallbackBanner/
  ShowError/voice-hints/AskView.Failed). Also DONE this batch: greeting demoted, chips uniform, Library
  window renamed.

The findings as originally reported:

- The greeting is the loudest element on notch Home and hub Ask while carrying no information (plus
  an exclamation mark the skill forbids and the hub version does not have).
- Two narrating sentences to delete: "Your meeting assistant." (hub Ask) and "How Toki looks and what
  it shows in a share." (hub Settings).
- Summarizing, the longest wait in the product, names no stage and gives no estimate.
- One-meaning-one-colour has two exceptions: green Settings toggles (green means decisions) and the
  pink Summarizing sliver (pink means questions).
- Two left edges on hub Templates; ragged suggestion chips on hub Ask.
- hub Templates section chips are constants, so they are labels wearing badge clothing, and the nine
  template blocks are unranked.
- notch Home has three actions in two treatments and no primary.

## Ask cannot see hand-written notes (found AND FIXED 2026-08-24)

The Ask assistant is recorded-meetings-only, by three query filters, and this is invisible to the
user because the Library search box behaves differently:

- `MeetingStore.SearchRelevant` joins `AND m.kind = 'Meeting'`, so manual-note text IS in the FTS
  index (`FtsSelect` includes `m.body`) and is filtered out on read.
- `TitleMatchIds` and `MeetingTimes` are `WHERE kind='Meeting'` too, so a hand-written note cannot be
  found by title, is never counted by "how many meetings", and never appears in a date range.
- The FTS-unavailable fallback re-applies the same rule in C#, and the router's evidence probe uses
  the same filtered calls - so a question whose only answer is in a manual note is classified as
  GENERAL and answered from the model's own knowledge with no citation, rather than "I could not
  find it". That is the worst part: it is silently wrong, not visibly empty.
- Opening a manual note in the Library and asking "summarise this" cannot work either: `VisibleMeeting`
  requires `Kind: MeetingKind.Meeting`.
- Sticky notes are excluded structurally (own tables, never in `FtsSelect`) and that is deliberate.
- The Library search box uses a plain `LIKE` over `m.body` with NO kind filter, so the user can find a
  note by typing its text and then be told by Ask that it does not exist.

FIXED the same day. Content search and title search are open to notes; the temporal/counting queries
are deliberately still meeting-only, so "how many meetings" stays right. `BuildContext` renders a
note's body (without that, a retrieved note would have been cited empty) and `VisibleMeeting` accepts
a viewed note so "summarise this" works. Sticky notes stay out. See ARCHITECTURE, "Ask reaches
hand-written notes", and the `--dbtest` guard.

## Toki 2.0: student-notetaker niche (POST-LAUNCH idea, 2026-08-24)

Owner's direction: launch Toki as a GENERAL notetaker; if traction concentrates on students, niche
it down later into a class notetaker that listens to online (or in-person) classes and extracts
homework notices, assignment due dates, test/exam announcements, lesson topics/definitions/readings,
plus the usual transcription. Captured now, to build AFTER the v1 general launch, only if traction
says so.

**Feasibility: high, no engine rewrite.** The engine (ASR, on-device LLM, diarisation, SQLite +
Markdown, evidence-audio, calendar) is domain-neutral - a class is a meeting with one dominant
speaker. Two mechanisms already anticipate this: the **Lecture gate** (detects lecture-shaped audio)
and **TemplateGate** (a specialised template must be EARNED by the transcript). Student mode rides
the same build, no fork.

**What actually changes (mostly prompts + framing, not machinery):**
1. **Extraction schema + prompts** - the real work, the good kind. Add a "Class/Lecture" template:
   homework/assignments (structurally identical to an action item with a due date, already modelled),
   test/exam notices (item + date + scope), lesson topics/concepts/definitions/readings (new
   sections). The **"a template is a claim"** guard is critical here - a hallucinated "test Friday" is
   worse than none.
2. **UI framing** - a Homework/Tests view; auto-offer to push assignment/test due dates into the
   calendar (create/edit events already exist).
3. **Capture - the one genuine decision, not just tuning:** live classes in Zoom/Teams/Meet work
   TODAY (a class is a "call"); in-person via laptop mic works (the "you" mic path exists); a
   PRE-RECORDED lecture playing in a browser tab does NOT - Hard Constraint #3 taps only a detected
   call's process tree, never tab/system audio, so this needs a deliberate "record this" affordance or
   a browser-as-source decision.

**Why the constraints fit students BETTER:** $0-forever + on-device + offline (flaky campus wifi),
one-time $20 (student budget), all-local/no-account (no consent/FERPA headache). Diarisation matters
less for a single-teacher lecture, so student mode is technically SIMPLER in that respect.

**Clean path:** ship general; the Lecture gate already gives free signal on how much class-taking
happens; if it concentrates there, add the Class template (TemplateGate activates it on
lecture-shaped transcripts) + student framing. Same engine, same build.

## MCP integration - CLIENT side wanted (owner ask, 2026-08-25)

Owner wants to build the MCP CLIENT side. MCP (Model Context Protocol) is a standard plug between an
AI assistant and a tool/data source; there are always two ends and Toki can be either or BOTH at once
(they are independent subsystems - see the coexistence note below).

**Direction the owner asked for - Toki as MCP CLIENT (Toki reaches OUT):** Toki's own Ask/notes flow
connects to the user's other services (Notion, Linear/Jira, Google Tasks, Slack, calendar) to push
things out or pull context in. Concretely: after a meeting, "Send these 3 action items to your Notion
tasks?" and on the user's click, it does; or Ask answers "is this deadline already on my calendar?"
by querying it.

- **Constitution tension - this is the one real decision.** A client crosses "nothing leaves the
  laptop": it sends the user's data to the user's own accounts, on the user's click. Defensible the
  same way the BYO API key is, but it is the FIRST outward-sending feature, so it is an owner-gated,
  opt-in, human-in-the-loop feature - Toki PROPOSES, the user APPROVES every send, never silent sync.
- **Cost/upkeep:** every connector is an OAuth integration to build and maintain (real surface area,
  the kind of upkeep the constitution otherwise avoids). Start with ONE connector, prove the pattern,
  add more only on demand. Natural first target: whatever the owner already lives in.
- **Data still stays local except the explicit push:** the transcript/recordings never go out; only
  the specific action items / query the user approves do.

**Coexistence with Toki as MCP SERVER - YES, both at once, no conflict.** Client and server are
opposite ends of the same protocol and share no state. Toki can run a tiny LOCAL, read-only MCP
server (localhost only) so Claude Desktop / Claude Code / other on-device AI apps can query the
meeting library ("what did I commit to in yesterday's standup?", "list this week's action items
assigned to me") - and simultaneously act as a client that pushes items out. Recommended split:
- **Server = the clean, purely-additive half.** Read-only, localhost, nothing leaves the laptop,
  ~zero upkeep, gated behind a Settings toggle ("Let AI assistants on this PC read my Toki notes").
  Honors every hard constraint. This is the low-risk win and arguably should ship FIRST or alongside.
- **Server tools it would expose:** search_meetings, get_transcript, list_action_items,
  get_notes_for(date). All read-only.
- Building the client does NOT block or complicate the server; do them as two independent tracks.

**Build order suggestion (owner decides):** (1) local read-only MCP server - safe, additive, tiny;
(2) MCP client with ONE approved connector + explicit per-send approval UI. Both are post-launch (M8
ship path comes first), but the server half is small enough it could ride earlier if wanted.

## Spec-driven self-building loop (PARKED idea, 2026-07-31)

How to build a system where Sid writes one detailed brief (goals, guardrails, rules, a checkable
"done") and Claude prompts itself to build and iterate, interrupting only at real decisions. Full
write-up in `docs/self-driving-build-system.md`: the oracle (tests / self-test flags as the pass/fail
engine), the three layers (Brief / Loop / Guardrails), the Claude Code mechanisms (`/loop`, custom
slash command, `/schedule`, Workflows, headless SDK), and a starter kit to build when we revisit
(`BRIEF.md` + `TASKS.md` + a `/build-next` command). Not built; deliberately NOT put in CLAUDE.md
(constitution stays rules-only).
