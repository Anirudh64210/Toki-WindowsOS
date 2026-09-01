using System;
using System.Collections.Generic;
using System.Linq;
using Toki.Transcription;

namespace Toki.Storage;

/// <summary>
/// Links a note line back to the transcript moment that produced it. Deterministic word overlap,
/// no model. A link to the WRONG line is worse than no link, so below the match floor return null
/// and show nothing.
/// </summary>
public static class Evidence
{
    /// <summary>How many consecutive transcript lines a single bullet may be matched against - a
    /// summarised point often spans a sentence that got split across two or three utterances.</summary>
    const int MaxWindow = 3;

    /// <summary>Share of the bullet's content words that must appear in the window.</summary>
    const double Floor = 0.34;

    /// <summary>And at least this many distinct content words, so two stray matches can't carry it.</summary>
    const int MinShared = 2;

    // Deliberately small: words that carry no evidence either way. Anything domain-specific stays in,
    // because that's exactly what identifies the right line.
    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "if", "then", "than", "that", "this", "these", "those",
        "is", "are", "was", "were", "be", "been", "being", "am",
        "do", "does", "did", "done", "have", "has", "had", "having",
        "will", "would", "shall", "should", "can", "could", "may", "might", "must",
        "to", "of", "in", "on", "at", "by", "for", "with", "from", "as", "into", "about", "over",
        "we", "you", "they", "he", "she", "it", "i", "me", "us", "them", "him", "her",
        "our", "your", "their", "his", "its", "my",
        "so", "not", "no", "yes", "just", "also", "very", "really", "there", "here",
        "up", "out", "down", "off", "back", "now", "get", "got", "go", "going", "one", "all",
    };

    /// <summary>The index of the transcript line a note bullet most likely came from, or null when
    /// nothing matches well enough to be worth pointing at.</summary>
    /// <summary>
    /// Weight a shared word by how RARE it is in this meeting instead of counting every word once.
    ///
    /// MEASURED 2026-08-23 and left OFF. The argument for it was good: a bullet and a line can share
    /// "deck" while discussing different decks, so a word carried by two lines should count for more
    /// than one said constantly. --linktest scored both settings on 400 identical synthetic cases,
    /// twice. Easy condition: identical, zero disagreements. Hard condition: flat 82.9%, weighted
    /// 81.9%, nine disagreements, McNemar chi-square 1.78, which is noise. It is kept as the control
    /// arm of that benchmark, not as a feature waiting to be switched on: turn it on only if a
    /// measurement says to.
    /// </summary>
    public static bool WeightByRarity { get; set; }

    public static int? BestSegment(string? bullet, IReadOnlyList<TranscriptSegment>? transcript,
        SourceSpan? source = null) =>
        BestRange(bullet, transcript, source)?.First;

    /// <summary>
    /// The inclusive range of consecutive transcript lines a bullet came from, trimmed so both ends
    /// actually contain evidence words. The trim keeps the clip's from/to times honest: they bound the
    /// words that earned the link, not a scoring-window artefact.
    /// </summary>
    /// <param name="source">Where the note writer says this line came from, when it recorded it.
    /// The search is then confined to those lines: provenance beats recovery, because the range is
    /// what the model was actually reading rather than what happens to share words. Null means no
    /// record, and the whole transcript is searched as before.</param>
    public static (int First, int Last)? BestRange(string? bullet, IReadOnlyList<TranscriptSegment>? transcript,
        SourceSpan? source = null)
    {
        if (string.IsNullOrWhiteSpace(bullet) || transcript is null || transcript.Count == 0) return null;

        // Confine to the recorded window, with one line of slack either side: a chunk boundary can
        // fall mid-thought, so the sentence that explains a bullet can sit just outside it.
        var lo = 0;
        var hi = transcript.Count - 1;
        if (source is not null)
        {
            lo = Math.Clamp(Math.Min(source.First, source.Last) - 1, 0, transcript.Count - 1);
            hi = Math.Clamp(Math.Max(source.First, source.Last) + 1, 0, transcript.Count - 1);
            if (hi < lo) { lo = 0; hi = transcript.Count - 1; }
        }

        var want = Words(bullet);
        if (want.Count < MinShared) return null;   // too generic to attribute honestly

        var segWords = new List<HashSet<string>>(transcript.Count);
        foreach (var s in transcript) segWords.Add(Words(s.Text));

        // HOW RARE IS EACH WORD IN THIS MEETING? Counting every shared word equally is what made
        // links land on the wrong moment: a bullet and a line can share "deck" and "send" while
        // talking about different decks twenty minutes apart, and a meeting says its own common
        // words constantly. A word carried by one or two lines is evidence; a word in half the
        // transcript is not. This is the same reasoning as the name corrector counting across the
        // corpus rather than the sample.
        Dictionary<string, int>? lineCount = null;
        if (WeightByRarity)
        {
            lineCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in segWords)
                foreach (var w in set)
                    lineCount[w] = lineCount.TryGetValue(w, out var c) ? c + 1 : 1;
        }

        double Weight(string word)
        {
            if (lineCount is null) return 1;   // flat scoring: every shared word counts once
            var n = lineCount.TryGetValue(word, out var c) ? c : 0;
            if (n == 0) return 0;
            // Inverse document frequency, flattened: a word on one line is worth about 3x a word
            // spread over a tenth of the meeting, never more, so one rare hit cannot carry a link
            // on its own (MinShared still applies).
            return Math.Min(3.0, Math.Log((double)transcript.Count / n) + 0.5);
        }

        var wantWeight = want.Sum(Weight);
        if (wantWeight <= 0) return null;   // nothing in the bullet appears in this meeting at all

        var bestIdx = -1;
        var bestWidth = 0;
        var bestScore = 0.0;

        for (var i = lo; i <= hi; i++)
        {
            var window = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var w = 0; w < MaxWindow && i + w <= hi; w++)
            {
                window.UnionWith(segWords[i + w]);
                if (window.Count == 0) continue;

                var matched = want.Where(window.Contains).ToList();
                if (matched.Count < MinShared) continue;

                // Penalise wider windows very slightly, so the tightest line that explains the bullet
                // wins over a sprawl that happens to contain the same words.
                var score = matched.Sum(Weight) / wantWeight - w * 0.01;
                if (score > bestScore) { bestScore = score; bestIdx = i; bestWidth = w; }
            }
        }

        if (bestScore < Floor) return null;

        // Trim to the lines that actually hold evidence words.
        var first = bestIdx;
        var last = bestIdx + bestWidth;
        while (first < last && !segWords[first].Overlaps(want)) first++;
        while (last > first && !segWords[last].Overlaps(want)) last--;
        if (!segWords[first].Overlaps(want)) return null;   // no single line carries a word: refuse

        return (first, last);
    }

    // SUPERSEDED 2026-08-25 and removed: DisjointRanges forced every note point's clip onto a
    // DIFFERENT set of transcript lines so no two clips could overlap. That "clean-cut by no-overlap"
    // was the wrong model - it is a greedy assignment, so when several points came out of one run-on
    // utterance the FIRST point's clip took the whole stretch and the rest got scraps or nothing.
    // The right model is precision, not non-overlap: each point resolves on its own (BestRange) and
    // its clip narrows to the phrase that carries it (EvidenceAudio.Window). Two points that genuinely
    // share words may overlap a little; none swallows the others.

    static readonly char[] WordSeparators =
        { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '"', '(', ')', '[', ']', '—', '–', '/', '|' };

    static HashSet<string> Words(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return set;
        foreach (var raw in text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
            if (FoldWord(raw) is { } w) set.Add(w);
        return set;
    }

    /// <summary>The content words of a line, for callers that need to align a phrase to audio (the
    /// evidence-clip narrower). Same tokenisation the matcher uses, so a word counts as evidence in
    /// exactly the cases the link would.</summary>
    internal static HashSet<string> ContentWords(string? text) => Words(text);

    /// <summary>Normalise one raw token to its evidence form, or null if it carries no evidence (a
    /// stopword or a fragment). ONE fold definition, shared by the matcher and the clip narrower.</summary>
    internal static string? FoldWord(string raw)
    {
        var w = raw.Trim('\'', '’', '-', '*', '#').ToLowerInvariant();
        if (w.Length < 3 || Stop.Contains(w)) return null;
        // Crude singular/plural fold so "blockers" matches "blocker" - cheap and safe enough for a
        // relevance score (it never changes what the user sees, only which line is offered).
        if (w.Length > 4 && w.EndsWith("s") && !w.EndsWith("ss")) w = w[..^1];
        return w;
    }
}
