# Measuring evidence links (`--linktest`)

An evidence link is the click that takes a note bullet back to the moment it came from. Getting one
wrong is worse than offering none, because a reader sent confidently to the wrong minute stops
trusting the feature. This is the instrument that says whether a change to the matcher is a real
improvement or noise.

```
dist\Toki.exe --linktest [n]        # n = synthetic bullets per condition, default 200
```

It reads the real Library (READ ONLY, nothing is written) and needs no LLM, so it runs in seconds and
can run while something else is using the GPU.

## What it actually does

It builds synthetic bullets from real stored transcripts: take a line, keep a fraction of its words,
borrow a few words from elsewhere in the meeting, and remember which line it came from. That gives
ground truth, which real notes cannot: nobody has labelled which line each real bullet came from.

Two difficulty conditions, because one number hides the answer:

- **EASY** keeps half the words and borrows 1. A bullet that quotes its source.
- **HARD** keeps a third and borrows 3. A bullet that has been genuinely reworded, which is what a
  good note looks like.

Three matchers are scored on the identical cases: flat (search the whole transcript),
rarity-weighted, and provenance (search confined to the recorded window). Then it runs a **paired
McNemar test** on the disagreements. That is the part that matters. Comparing two precision
percentages across a few hundred cases cannot tell a 1-point improvement from sampling noise;
counting only the cases where the two matchers DISAGREE can.

The seed is fixed (`20260823`) so the sample does not change between runs. A benchmark that
resamples cannot tell a better matcher from a luckier draw.

## The reading, 2026-09-03

`--linktest 300`, 300 bullets per condition drawn from 16 meetings.

| Condition | Flat | Rarity-weighted | Provenance | Paired verdict (provenance vs flat) |
|---|---|---|---|---|
| EASY | 96.7% | 96.7% | 98.0% | McNemar 2.25 on 4 disagreements: **not significant** |
| HARD | 85.6% | 85.3% | 88.5% | McNemar 4.17 on 6 disagreements: **significant, p<0.05** |

**What to conclude, and what not to.**

Confining the search to a recorded window is worth having, but only on the HARD condition. On easy
bullets flat matching already gets 290 of 300 right and provenance is noise. Since a real note
rewords rather than quotes, HARD is the condition that describes the product.

Refusals rise with provenance (1 to 4 on HARD). That is the intended trade, not a regression: a
search confined to the right part of the meeting that finds nothing stays silent, where a flat search
would reach across the whole hour and return something plausible.

**Rarity weighting is a no-op on this sample.** It never disagrees with flat on EASY, and is net -1
on HARD. It stays behind `Evidence.WeightByRarity` and should not be switched on as a "win".

**This corrected an earlier claim.** The backlog and `note-quality-research.md` had recorded +4.6pp
for provenance. That number no longer matched the library and has been replaced everywhere with the
table above.

## The caveat that matters most

`--linktest` SIMULATES provenance with a 40-line chunk hint centred on the true line's chunk. The
shipped implementation records the range `ValidateDigest` narrowed to the lines its surviving points
actually cover, which is TIGHTER than a whole chunk. So the real effect should be at least what this
table shows.

That is an expectation, not a measurement. Measuring it properly needs stored notes that carry real
recorded sources, which only exist for notes written after 2026-09-03. Until the Library is
regenerated, this table is the best available number, and it should be described as what it is.

Related instruments:

- **`--evidencetest [id]`** cuts every evidence clip in a stored meeting, re-transcribes it, and
  reports whether the audio says the line the link claims. That is the gate on REAL meetings, where
  `--linktest` is the gate on a controlled sample. It found segments with an `int.MinValue` end.
- **`--groundtest`** measures whether the note's hard facts were said at all, which is a different
  question from whether the link points to the right place.
- **`--regentest [n]`** rewrites stored meetings with the current pipeline and now also reports what
  fraction of bullets and actions carry a recorded source, which is the coverage number this table
  cannot supply.

## When to run it

After ANY change to `Evidence` scoring, the overlap floors, the map prompt's citation format,
`ValidateDigest`, or `LlmNoteWriter.AttachProvenance`. Read the PAIRED verdict, never the raw
percentages: a matcher that refuses more can raise its precision while helping nobody.
