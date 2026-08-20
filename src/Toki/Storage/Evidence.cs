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
    public static int? BestSegment(string? bullet, IReadOnlyList<TranscriptSegment>? transcript) =>
        BestRange(bullet, transcript)?.First;

    /// <summary>
    /// The inclusive range of consecutive transcript lines a bullet came from, trimmed so both ends
    /// actually contain evidence words. The trim keeps the clip's from/to times honest: they bound the
    /// words that earned the link, not a scoring-window artefact.
    /// </summary>
    public static (int First, int Last)? BestRange(string? bullet, IReadOnlyList<TranscriptSegment>? transcript)
    {
        if (string.IsNullOrWhiteSpace(bullet) || transcript is null || transcript.Count == 0) return null;

        var want = Words(bullet);
        if (want.Count < MinShared) return null;   // too generic to attribute honestly

        var segWords = new List<HashSet<string>>(transcript.Count);
        foreach (var s in transcript) segWords.Add(Words(s.Text));

        var bestIdx = -1;
        var bestWidth = 0;
        var bestScore = 0.0;

        for (var i = 0; i < transcript.Count; i++)
        {
            var window = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var w = 0; w < MaxWindow && i + w < transcript.Count; w++)
            {
                window.UnionWith(segWords[i + w]);
                if (window.Count == 0) continue;

                var shared = want.Count(t => window.Contains(t));
                if (shared < MinShared) continue;

                // Penalise wider windows very slightly, so the tightest line that explains the bullet
                // wins over a sprawl that happens to contain the same words.
                var score = (double)shared / want.Count - w * 0.01;
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

    static HashSet<string> Words(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return set;

        foreach (var raw in text.Split(
                     new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '"', '(', ')', '[', ']', '—', '–', '/', '|' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.Trim('\'', '’', '-', '*', '#').ToLowerInvariant();
            if (w.Length < 3 || Stop.Contains(w)) continue;
            // Crude singular/plural fold so "blockers" matches "blocker" - cheap and safe enough for
            // a relevance score (it never changes what the user sees, only which line is offered).
            if (w.Length > 4 && w.EndsWith("s") && !w.EndsWith("ss")) w = w[..^1];
            set.Add(w);
        }
        return set;
    }
}
