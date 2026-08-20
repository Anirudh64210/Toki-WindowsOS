# Measuring transcription accuracy (`--wertest`)

Until 2026-08-18 Toki had no accuracy number. This is the five-minute procedure that produces
one, comparable across machines and across code changes. Run it after ANY change to the audio
path, the gate, the recogniser settings, or the readability pass.

## The procedure

1. Open any voice call app (or just Sound Recorder pointed at your mic).
2. Read **the script below** aloud at a normal meeting pace - do not over-articulate; the point
   is to measure real conditions. If you stumble, keep going (the scoring drops "uh"s).
3. Save the recording as a WAV (or record a meeting of yourself reading it and grab `you.wav`
   from its folder under `%APPDATA%\Toki\Recordings`).
4. Run:

```
dist\Toki.exe --wertest path\to\recording.wav docs\wer-reference.txt
```

It prints WER (word error rate), the substitution/deletion/insertion split, decode speed, and a
per-name scorecard. Exit code is pass/fail against a 20% budget (override: add a number, e.g.
`... docs\wer-reference.txt 15`).

## What the numbers mean

- **WER below 10%** on this script is production-grade for on-device.
- **Substitutions** are mishearings; **deletions** are usually the gate or levelling eating
  speech; **insertions** are usually hallucination - each points at a different layer.
- **The names row is the one that matters most.** A 6% WER with 0/3 on "Abhishek" is a worse
  product than 9% with 3/3.

## The script (read this aloud)

> Hi everyone, thanks for joining. Quick agenda before we start: the migration status, the
> budget, and then handover planning. Priya, can you go first?
>
> Sure. The database migration finished on August twenty first at three thirty pm, about two
> days ahead of schedule. We validated fifty percent of the tables on Friday and the rest over
> the weekend. Craig found one issue with the invoice table, but Abhishek patched it the same
> evening.
>
> Great. On budget: we are at seventy percent of the quarterly allocation, which leaves some
> room for the vendor renewal in September. The contract runs to March twenty twenty seven, so
> nothing is urgent, but let's decide before the fifteenth of October.
>
> Last thing. Sita is taking over the reporting dashboard from next Monday. Deepika will shadow
> her for the first week. Any questions? No? Alright, I will send the notes today. Thanks all.

## Why the script looks like this

Names that stress the recogniser (Priya, Craig, Abhishek, Sita, Deepika - two of them were
mangled in real meetings), spoken dates and times the readability pass must convert, percentages,
a spoken year, a handover (the speaker-naming shape), and normal connective tissue. Roughly 150
scored words - two minutes at meeting pace.

Keep `docs/wer-reference.txt` exactly in sync with the script. If you change one, change both.
