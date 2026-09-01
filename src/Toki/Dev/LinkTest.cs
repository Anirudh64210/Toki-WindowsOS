using System;
using System.Collections.Generic;
using System.Linq;
using Toki.Storage;
using Toki.Transcription;

namespace Toki.Dev;

/// <summary>
/// --linktest [cases]: how OFTEN does an evidence link land on the right transcript line, and does
/// rarity weighting make it land right more often?
///
/// THE PROBLEM THIS SOLVES. You cannot judge a matching change by looking at a few links, because
/// you have no independent answer to compare against: whatever the matcher says looks plausible.
/// Information retrieval solves it with a ground truth set plus precision@1, and where nobody has
/// hand-labelled anything, with SYNTHETIC queries whose correct answer is known by construction.
///
/// HOW THE GROUND TRUTH IS BUILT. Take a real transcript line, and make a "bullet" out of it the
/// way a summary does: keep only some of its content words, drop the rest, and mix in a word or two
/// from ELSEWHERE in the same meeting, because a summary generalises and borrows vocabulary from
/// its surroundings. The line it came from is the correct answer, and no human had to say so.
///
/// WHAT THE NUMBERS MEAN, HONESTLY. The absolute precision is FLATTERED, because a synthetic bullet
/// is built from its source line's own words, which is the friendliest possible case for word
/// overlap. So the absolute number is not a quality claim about Toki. What carries signal is the
/// PAIRED comparison: both scorers see identical cases, so their disagreements are the evidence.
/// That is McNemar's test - it looks only at the cases where the two disagree, because the cases
/// they both get right or both get wrong say nothing about which is better.
///
/// READ-ONLY. It never writes to the store.
/// </summary>
static class LinkTest
{
    public static int Run(string[] args)
    {
        App.Headless = true;
        var i = Array.IndexOf(args, "--linktest");
        var want = i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var n) ? n : 300;

        // TWO CONDITIONS, both reported, decided before looking at either. An easy benchmark hits a
        // ceiling where every method scores the same and nothing can be learned; a hard one is the
        // case that actually matters, because a real bullet is abstractive and keeps few of its
        // source's words. Reporting only the flattering one, or only the one that agrees with you,
        // is how a measurement becomes an argument.
        var conditions = new[] { ("EASY  keeps half the words, 1 borrowed", 0.5, 1),
                                 ("HARD  keeps a third, 3 borrowed", 0.33, 3) };
        foreach (var (label, keepFrac, distractors) in conditions)
        {
            var cs = BuildCases(want, keepFrac, distractors, out var ms);
            if (cs.Count == 0) return Fail("not enough transcript in the library to build cases");
            Console.WriteLine($"[--linktest] {label}: {cs.Count} synthetic bullets from {ms} meeting(s)");
            RunCondition(cs);
            Console.WriteLine();
        }

        Console.WriteLine("REMEMBER: synthetic bullets are built from their source line's own words, which");
        Console.WriteLine("flatters word overlap. The absolute precision is not a quality claim about Toki.");
        Console.WriteLine("The final gate is --evidencetest on real meetings, and the owner's eyes.");
        return 0;
    }

    static void RunCondition(List<(string Bullet, IReadOnlyList<TranscriptSegment> T, int Truth)> cases)
    {
        var flat = Score(cases, weighted: false, provenance: false);
        var rare = Score(cases, weighted: true, provenance: false);
        var prov = Score(cases, weighted: false, provenance: true);

        Report("flat  (searches the whole transcript)", flat, cases.Count);
        Report("rarity-weighted", rare, cases.Count);
        Report("PROVENANCE (search confined to the chunk)", prov, cases.Count);

        Paired("rarity weighting vs flat", flat, rare, cases.Count);
        Paired("PROVENANCE vs flat", flat, prov, cases.Count);
    }

    static void Paired(string what, Result baseline, Result candidate, int count)
    {
        var onlyFlat = 0; var onlyRare = 0; var both = 0; var neither = 0;
        for (var k = 0; k < count; k++)
        {
            var a = baseline.Hit[k]; var b = candidate.Hit[k];
            if (a && b) both++;
            else if (a) onlyFlat++;
            else if (b) onlyRare++;
            else neither++;
        }

        Console.WriteLine();
        Console.WriteLine($"PAIRED - {what} (identical cases, only disagreements count):");
        Console.WriteLine($"  both right {both}   both wrong {neither}   only baseline {onlyFlat}   only candidate {onlyRare}");

        var disagree = onlyFlat + onlyRare;
        if (disagree == 0)
        {
            Console.WriteLine("  the two never disagree on this sample: the change is a no-op here.");
        }
        else
        {
            // McNemar with continuity correction. 3.84 is p<0.05 and 6.63 is p<0.01 at 1 degree of
            // freedom; below that the difference is inside the noise and must not be called a win.
            var chi = Math.Pow(Math.Abs(onlyFlat - onlyRare) - 1, 2) / disagree;
            var verdict = chi >= 6.63 ? "significant at p<0.01"
                        : chi >= 3.84 ? "significant at p<0.05"
                        : "NOT significant - this is noise, do not ship it as a win";
            Console.WriteLine($"  McNemar chi-square {chi:0.00} on {disagree} disagreements: {verdict}");
            Console.WriteLine($"  net {(onlyRare - onlyFlat >= 0 ? "+" : "")}{onlyRare - onlyFlat} cases for the candidate");
        }

    }

    sealed class Result
    {
        public bool[] Hit = [];
        public int Right, Wrong, Refused;
    }

    /// <summary>
    /// <paramref name="provenance"/> simulates what the note writer now records: the chunk of
    /// transcript the model was reading. The chunk is reconstructed here the way the writer builds
    /// them, as a band of lines around the true source, so the comparison is honest about what
    /// provenance actually buys - a smaller haystack, not a free answer.
    /// </summary>
    static Result Score(List<(string Bullet, IReadOnlyList<TranscriptSegment> T, int Truth)> cases,
        bool weighted, bool provenance)
    {
        var before = Evidence.WeightByRarity;
        Evidence.WeightByRarity = weighted;
        var r = new Result { Hit = new bool[cases.Count] };
        try
        {
            for (var k = 0; k < cases.Count; k++)
            {
                var (bullet, t, truth) = cases[k];
                SourceSpan? hint = null;
                if (provenance)
                {
                    // A writer chunk holds roughly 40 lines of this transcript; the true line sits
                    // somewhere inside it, not at its centre, so offset it deterministically.
                    const int chunk = 40;
                    var start = Math.Max(0, (truth / chunk) * chunk);
                    hint = new SourceSpan { First = start, Last = Math.Min(t.Count - 1, start + chunk - 1) };
                }
                var range = Evidence.BestRange(bullet, t, hint);
                if (range is null) { r.Refused++; continue; }
                // A hit means the returned window CONTAINS the line the bullet was made from.
                if (truth >= range.Value.First && truth <= range.Value.Last) { r.Right++; r.Hit[k] = true; }
                else r.Wrong++;
            }
        }
        finally { Evidence.WeightByRarity = before; }
        return r;
    }

    static void Report(string name, Result r, int total) =>
        Console.WriteLine($"  {name,-38} right {r.Right,4}  wrong {r.Wrong,4}  refused {r.Refused,4}   " +
                          $"precision@1 {(total - r.Refused == 0 ? 0 : r.Right / (double)(total - r.Refused)):P1}");

    /// <summary>
    /// Build cases from the real library. Deterministic seed: a benchmark that changes between runs
    /// cannot tell a real improvement from a different sample.
    /// </summary>
    static List<(string, IReadOnlyList<TranscriptSegment>, int)> BuildCases(
        int want, double keepFraction, int distractors, out int meetings)
    {
        var rnd = new Random(20260823);
        var cases = new List<(string, IReadOnlyList<TranscriptSegment>, int)>();
        meetings = 0;

        foreach (var summary in App.Store.List(null))
        {
            if (cases.Count >= want) break;
            var m = App.Store.Get(summary.Id);
            if (m is null || m.Transcript.Count < 12) continue;
            meetings++;

            // Lines long enough that a summary of them could exist at all.
            var usable = Enumerable.Range(0, m.Transcript.Count)
                .Where(i => Content(m.Transcript[i].Text).Count >= 6)
                .OrderBy(_ => rnd.Next())
                .Take(Math.Max(2, want / 12))
                .ToList();

            foreach (var idx in usable)
            {
                if (cases.Count >= want) break;
                var bullet = Paraphrase(m.Transcript, idx, rnd, keepFraction, distractors);
                if (bullet is null) continue;
                cases.Add((bullet, m.Transcript, idx));
            }
        }
        return cases;
    }

    /// <summary>
    /// A stand-in for what the note writer does to a line: keep about half its content words, and
    /// borrow one from elsewhere in the meeting. Deliberately lossy - a bullet that kept every word
    /// would make every matcher look perfect.
    /// </summary>
    static string? Paraphrase(IReadOnlyList<TranscriptSegment> t, int idx, Random rnd,
        double keepFraction, int distractors)
    {
        var words = Content(t[idx].Text);
        if (words.Count < 6) return null;
        var keep = words.OrderBy(_ => rnd.Next())
            .Take(Math.Max(3, (int)Math.Round(words.Count * keepFraction))).ToList();

        // Words from DIFFERENT lines, as a summary borrows the meeting's vocabulary. These are the
        // distractors: they are what pulls a weak matcher to the wrong place, and they are the
        // whole point of the hard condition.
        var added = 0;
        for (var tries = 0; tries < 40 && added < distractors; tries++)
        {
            var other = rnd.Next(t.Count);
            if (other == idx) continue;
            var pool = Content(t[other].Text);
            if (pool.Count == 0) continue;
            keep.Add(pool[rnd.Next(pool.Count)]);
            added++;
        }
        return string.Join(" ", keep.OrderBy(_ => rnd.Next()));
    }

    static List<string> Content(string s) =>
        (s ?? "").ToLowerInvariant()
        .Split(new[] { ' ', ',', '.', '?', '!', ':', ';', '-', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
        .Where(w => w.Length > 3)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    static int Fail(string why) { Console.WriteLine($"[--linktest] {why}"); return 1; }
}
