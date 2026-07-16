using System.Text;

namespace Toki.Transcription;

/// <summary>
/// Merges the "you" (mic) and "them" (loopback) transcripts into one timeline and
/// removes echo. The mic physically hears the far end through the speakers, so a
/// remote utterance can appear on BOTH streams. The clean copy is on "them"
/// (loopback), so we drop the "you" copy that duplicates a "them" line within a
/// small time window (brief §8, lesson 6).
///
/// Guard: for short lines (&lt;3 significant words) require an EXACT normalized
/// match — fuzzy-matching interjections like "yeah"/"okay" would wrongly delete
/// real things the user said.
/// </summary>
public static class TranscriptMerger
{
    const int EchoWindowMs = 4000;      // ±4s
    const double FuzzyThreshold = 0.80; // token overlap for longer lines

    public static IReadOnlyList<TranscriptSegment> Merge(
        IEnumerable<TranscriptSegment> you, IEnumerable<TranscriptSegment> them)
    {
        var themList = them.ToList();
        var kept = new List<TranscriptSegment>(themList);

        foreach (var y in you)
        {
            if (!IsEchoOfThem(y, themList))
                kept.Add(y);
        }

        return kept.OrderBy(s => s.StartMs).ThenBy(s => s.Speaker).ToList();
    }

    static bool IsEchoOfThem(TranscriptSegment you, List<TranscriptSegment> them)
    {
        var yn = Normalize(you.Text);
        if (yn.Length == 0) return false;
        var yWords = yn.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var t in them)
        {
            if (Math.Abs(t.StartMs - you.StartMs) > EchoWindowMs) continue;
            var tn = Normalize(t.Text);
            if (tn.Length == 0) continue;

            var tWords = tn.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (yWords.Length < 3 || tWords.Length < 3)
            {
                if (yn == tn) return true; // exact only when either line is short
            }
            else if (yn == tn || Containment(yWords, tWords) >= FuzzyThreshold)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Shared words as a fraction of the SHORTER line — catches echo where
    /// one transcript picked up extra/misheard words around the same phrase.</summary>
    static double Containment(string[] a, string[] b)
    {
        var aset = new HashSet<string>(a);
        var bset = new HashSet<string>(b);
        var shared = aset.Count(bset.Contains);
        var min = Math.Min(aset.Count, bset.Count);
        return min == 0 ? 0 : (double)shared / min;
    }

    /// <summary>Lowercase, strip punctuation, collapse whitespace.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastSpace = false;
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastSpace = false; }
            else if (!lastSpace) { sb.Append(' '); lastSpace = true; }
        }
        return sb.ToString().Trim();
    }
}
