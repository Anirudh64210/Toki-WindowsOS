using System.Text.RegularExpressions;

namespace Toki.Transcription;

/// <summary>
/// Drops the discarded half of a spoken self-repair.
///
/// People correct themselves mid-sentence and the recogniser, faithfully, keeps both attempts:
/// "the educative, oh, executive summary". A reader has to do the repair again in their head every
/// time they read the line, and the note writer sees a word that was never really said - in one real
/// meeting "educative" reached the summary as if it were a topic.
///
/// The rule is narrow on purpose. A repair is only removed when a CUE ("sorry", "I mean", "oh") sits
/// between two words that clearly sound like the same word - "educative ... executive", not
/// "shipping, I mean, Tuesday". Otherwise "I mean" is just ordinary speech and cutting the word
/// before it would delete real content. Same asymmetry as everywhere else in the transcript pipeline:
/// a missed repair is a slightly clumsy line; a wrong one deletes what somebody said.
/// </summary>
public static class SelfCorrection
{
    /// <summary>One removed attempt, kept so the transcript can show it on hover.</summary>
    public sealed record Repair(string From, string To);

    /// <summary>The correction cues, longest first so "oh sorry" beats "sorry".</summary>
    const string Cues =
        @"oh(?:,?\s+sorry)?|i\s+mean(?:t)?|or\s+rather|rather|sorry|excuse\s+me|no,?\s+wait|well,?\s+no";

    // wrong-word , cue , right-word. The separators allow the comma, ellipsis or dash the recogniser
    // tends to place around a hesitation.
    static readonly Regex Pattern = new(
        @"\b(?<wrong>[A-Za-z][A-Za-z'’-]{3,})\b[\s,.…-]*\b(?<cue>" + Cues + @")\b[\s,.…-]*(?<right>[A-Za-z][A-Za-z'’-]{3,})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Clean one line. Returns the line unchanged when nothing qualifies.</summary>
    public static (string Text, IReadOnlyList<Repair> Repairs) Clean(string? text)
    {
        var repairs = new List<Repair>();
        if (string.IsNullOrWhiteSpace(text)) return (text ?? "", repairs);

        var result = Pattern.Replace(text, m =>
        {
            var wrong = m.Groups["wrong"].Value;
            var right = m.Groups["right"].Value;
            if (!SoundsLikeTheSameWord(wrong, right)) return m.Value;   // ordinary speech, leave it
            repairs.Add(new Repair(wrong, right));
            return right;
        });

        return (Tidy(result), repairs);
    }

    /// <summary>Clean a whole transcript. Non-fatal by construction - a segment that changes nothing
    /// is returned as-is, so this is safe to run over already-clean text.</summary>
    public static (IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<Repair> Repairs) Clean(
        IReadOnlyList<TranscriptSegment> segments)
    {
        var all = new List<Repair>();
        var output = new List<TranscriptSegment>(segments.Count);
        foreach (var s in segments)
        {
            var (text, repairs) = Clean(s.Text);
            all.AddRange(repairs);
            output.Add(repairs.Count == 0 ? s : s with { Text = text });
        }
        return (output, all);
    }

    /// <summary>
    /// Two attempts at the SAME word, as opposed to two different words either side of a filler.
    /// Requires a shared opening sound and a small edit distance relative to length - "educative" and
    /// "executive" differ by 3 of 9 characters and both open on E.
    /// </summary>
    static bool SoundsLikeTheSameWord(string a, string b)
    {
        if (a.Length < 4 || b.Length < 4) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        var ka = NameCorrector.Phonetic(a);
        var kb = NameCorrector.Phonetic(b);
        if (ka.Length == 0 || kb.Length == 0) return false;
        if (ka[0] != kb[0]) return false;                    // a repair restarts on the same sound

        if (ka == kb) return true;                           // homophones: "there"/"their"

        var longest = Math.Max(a.Length, b.Length);
        var budget = Math.Max(2, longest / 3);
        return Distance(a.ToLowerInvariant(), b.ToLowerInvariant()) <= budget;
    }

    /// <summary>Close the gaps a removal leaves behind: double spaces, a space before punctuation,
    /// and a doubled comma.</summary>
    static string Tidy(string s)
    {
        s = Regex.Replace(s, @"\s{2,}", " ");
        s = Regex.Replace(s, @"\s+([,.!?;:])", "$1");
        s = Regex.Replace(s, @",\s*,+", ",");
        return s.Trim();
    }

    static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
