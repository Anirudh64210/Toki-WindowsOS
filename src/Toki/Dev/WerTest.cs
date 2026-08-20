using System.IO;
using System.Text.RegularExpressions;
using Toki.Transcription;

namespace Toki.Dev;

/// <summary>
/// --wertest &lt;audio.wav&gt; &lt;reference.txt&gt; [maxWerPercent]: word error rate of the REAL pipeline
/// against a reference transcript, pass/fail vs the threshold (default 20%). The reference is what
/// was actually said, written clean-transcript style; # lines are ignored except "#names: A, B, C",
/// which adds a per-name recall report. Both sides are normalized identically so formatting cannot
/// masquerade as recognition error. Script + reference live in docs/wer-eval.md.
/// </summary>
public static class WerTest
{
    public static int Run(string[] args)
    {
        var i = Array.IndexOf(args, "--wertest");
        var wav = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var refPath = i >= 0 && i + 2 < args.Length ? args[i + 2] : null;
        var threshold = i >= 0 && i + 3 < args.Length && double.TryParse(args[i + 3], out var th) ? th : 20.0;

        if (wav is null || refPath is null || !File.Exists(wav) || !File.Exists(refPath))
        {
            Console.WriteLine("[--wertest] usage: --wertest <audio.wav> <reference.txt> [maxWerPercent]");
            Console.WriteLine("            reference = what was said, written like a clean transcript (see docs/wer-eval.md)");
            return 2;
        }
        if (!TranscriptionService.ModelReady)
        {
            Console.WriteLine("[--wertest] Parakeet model not installed - run --asrtest once to fetch it.");
            return 2;
        }

        // The PIPELINE, not just the model: gate, levelling and readability are all measured.
        // Name correction is skipped - it depends on the user's real library, off-limits to tests.
        Console.WriteLine($"[--wertest] decoding {Path.GetFileName(wav)}...");
        var samples = AudioLoader.To16kMono(wav);
        using var transcriber = new Transcriber();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var segments = transcriber.Transcribe(samples, Speaker.You);
        sw.Stop();
        var (tidied, _) = Readability.Clean(segments);
        var hypText = string.Join(" ", tidied.Select(s => s.Text));

        var refLines = File.ReadAllLines(refPath);
        var names = refLines.FirstOrDefault(l => l.StartsWith("#names:", StringComparison.OrdinalIgnoreCase))
            ?["#names:".Length..].Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList() ?? [];
        var refText = string.Join(" ", refLines.Where(l => !l.TrimStart().StartsWith('#')));

        var hyp = Normalize(hypText);
        var reference = Normalize(refText);
        if (reference.Length == 0) { Console.WriteLine("[--wertest] reference file has no content lines"); return 2; }

        var (dist, subs, dels, ins) = Align(reference, hyp);
        var wer = 100.0 * dist / reference.Length;

        var audioSec = samples.Length / (double)AudioLoader.TargetRate;
        Console.WriteLine($"  audio: {audioSec:F0}s, decoded in {sw.Elapsed.TotalSeconds:F1}s ({audioSec / Math.Max(0.001, sw.Elapsed.TotalSeconds):F1}x real-time)");
        Console.WriteLine($"  reference: {reference.Length} words   hypothesis: {hyp.Length} words");
        Console.WriteLine($"  WER: {wer:F1}%   (sub {subs}, del {dels}, ins {ins})");

        if (names.Count > 0)
        {
            Console.WriteLine("  names (said vs heard):");
            var hypJoined = " " + string.Join(" ", hyp) + " ";
            var refJoined = " " + string.Join(" ", reference) + " ";
            foreach (var name in names)
            {
                var key = " " + Normalize(name).FirstOrDefault() + " ";
                var said = Regex.Matches(refJoined, Regex.Escape(key)).Count;
                var heard = Regex.Matches(hypJoined, Regex.Escape(key)).Count;
                Console.WriteLine($"    {name,-16} {heard}/{said}");
            }
        }

        var pass = wer <= threshold;
        Console.WriteLine(pass
            ? $"[--wertest] PASS - WER {wer:F1}% within the {threshold:F0}% budget."
            : $"[--wertest] FAIL - WER {wer:F1}% exceeds the {threshold:F0}% budget.");
        return pass ? 0 : 1;
    }

    /// <summary>Both sides through the SAME funnel: lowercase, punctuation to spaces, fillers out.
    /// "%" and ":" survive as-is inside tokens ("50%", "3:30") because the reference is written in
    /// the same clean-transcript style the pipeline now outputs.</summary>
    static string[] Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9%:']+", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("uh" or "um" or "uhh" or "umm" or "uhm" or "erm"))
            .Select(w => w.Trim('\''))
            .Where(w => w.Length > 0)
            .ToArray();

    /// <summary>Word-level Levenshtein with operation counts (the standard WER decomposition).</summary>
    static (int Dist, int Subs, int Dels, int Ins) Align(string[] rf, string[] hy)
    {
        var d = new int[rf.Length + 1, hy.Length + 1];
        for (var r = 0; r <= rf.Length; r++) d[r, 0] = r;
        for (var h = 0; h <= hy.Length; h++) d[0, h] = h;
        for (var r = 1; r <= rf.Length; r++)
            for (var h = 1; h <= hy.Length; h++)
            {
                var cost = rf[r - 1] == hy[h - 1] ? 0 : 1;
                d[r, h] = Math.Min(Math.Min(d[r - 1, h] + 1, d[r, h - 1] + 1), d[r - 1, h - 1] + cost);
            }

        // Walk back to attribute the distance to substitutions / deletions / insertions.
        int subs = 0, dels = 0, ins = 0;
        {
            int r = rf.Length, h = hy.Length;
            while (r > 0 || h > 0)
            {
                if (r > 0 && h > 0 && d[r, h] == d[r - 1, h - 1] + (rf[r - 1] == hy[h - 1] ? 0 : 1))
                {
                    if (rf[r - 1] != hy[h - 1]) subs++;
                    r--; h--;
                }
                else if (r > 0 && d[r, h] == d[r - 1, h] + 1) { dels++; r--; }
                else { ins++; h--; }
            }
        }
        return (d[rf.Length, hy.Length], subs, dels, ins);
    }
}
