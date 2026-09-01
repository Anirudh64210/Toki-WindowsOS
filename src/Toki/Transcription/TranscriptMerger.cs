using System.Text;

namespace Toki.Transcription;

/// <summary>
/// Merges the "you" (mic) and "them" (loopback) transcripts into one timeline and removes echo:
/// the mic hears the far end through the speakers, so a remote utterance can appear on BOTH
/// streams; the clean copy is "them", so the duplicating "you" copy within a small window is
/// dropped. Short lines (&lt;3 significant words) require an EXACT normalized match -
/// fuzzy-matching interjections would delete real things the user said.
/// </summary>
public static class TranscriptMerger
{
    const int EchoWindowMs = 4000;      // ±4s
    const double FuzzyThreshold = 0.80; // token overlap for longer lines

    /// <summary>Gap below which two consecutive lines from the same person are one thought. The ASR
    /// emits a segment per breath, so a single sentence arrives as five or six of them.</summary>
    const int SameSpeakerGapMs = 1500;

    /// <summary>A merged turn stops growing past this, so one person talking for two minutes still
    /// yields readable paragraphs (and the note writer still has chunk boundaries to split on).</summary>
    const int MaxTurnChars = 700;

    /// <summary>Words at or under this count, from the OTHER speaker, mid-sentence: a backchannel
    /// ("yeah", "okay", "right") rather than a turn.</summary>
    const int BackchannelWords = 3;

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

        var ordered = kept.OrderBy(s => s.StartMs).ThenBy(s => s.Speaker).ToList();
        return JoinTurns(Deinterleave(ordered));
    }

    /// <summary>
    /// Move a short backchannel out of the middle of someone else's sentence: streams merge by
    /// start time, so a listener's "yeah" lands BETWEEN two halves of a thought and stops them
    /// being joined. Only its position moves (never its timestamp), and only when the speaker
    /// demonstrably carries straight on.
    /// </summary>
    static List<TranscriptSegment> Deinterleave(List<TranscriptSegment> segs)
    {
        var outp = new List<TranscriptSegment>(segs.Count);
        for (var i = 0; i < segs.Count; i++)
        {
            // Is segs[i] a short interruption sandwiched between two close segments of one other speaker?
            if (i > 0 && i + 1 < segs.Count
                && WordCount(segs[i].Text) <= BackchannelWords
                && SameVoice(segs[i - 1], segs[i + 1])
                && !SameVoice(segs[i - 1], segs[i])
                && segs[i + 1].StartMs - segs[i - 1].EndMs <= SameSpeakerGapMs)
            {
                // Let the sentence finish, then the interjection. Keep scanning from the line after it.
                outp.Add(segs[i + 1]);
                outp.Add(segs[i]);
                i++;
                continue;
            }
            outp.Add(segs[i]);
        }
        return outp;
    }

    /// <summary>
    /// Join consecutive lines from the same person into one turn - Parakeet emits a segment per
    /// breath group. The first and last timestamps are kept, so evidence links still resolve.
    /// </summary>
    static List<TranscriptSegment> JoinTurns(List<TranscriptSegment> segs)
    {
        var outp = new List<TranscriptSegment>();
        foreach (var s in segs)
        {
            var text = (s.Text ?? "").Trim();
            if (text.Length == 0) continue;

            if (outp.Count > 0)
            {
                var prev = outp[^1];
                if (SameVoice(prev, s)
                    && s.StartMs - prev.EndMs <= SameSpeakerGapMs
                    && prev.Text.Length + text.Length + 1 <= MaxTurnChars)
                {
                    // An end never moves BACKWARDS. A short interjection nested inside a longer turn would
                    // otherwise shrink the merged line's span while its text grew, which is how a
                    // ten-word line ends up claiming half a second of audio.
                    outp[^1] = prev with { EndMs = Math.Max(prev.EndMs, s.EndMs), Text = Stitch(prev.Text, text) };
                    continue;
                }
            }
            outp.Add(s with { Text = text });
        }
        return outp;
    }

    /// <summary>Join two fragments into readable prose. The ASR ends most segments without punctuation
    /// because the sentence hasn't finished, so a plain space is right; if it DID end a sentence, keep
    /// the break and capitalise what follows.</summary>
    static string Stitch(string a, string b)
    {
        a = a.TrimEnd();
        b = b.TrimStart();
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;

        var ended = a[^1] is '.' or '?' or '!';
        if (ended && char.IsLower(b[0])) b = char.ToUpperInvariant(b[0]) + b[1..];
        return a + " " + b;
    }

    /// <summary>
    /// Same person, for joining and de-interleaving. MUST NOT be plain Speaker equality: the enum
    /// only says which stream, so every remote participant is "Them" and two people talking back
    /// to back would be welded into one turn. The diarised identity (Tag) decides.
    /// </summary>
    static bool SameVoice(TranscriptSegment a, TranscriptSegment b) =>
        a.Speaker == b.Speaker && string.Equals(a.Tag, b.Tag, StringComparison.OrdinalIgnoreCase);

    static int WordCount(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

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

    /// <summary>
    /// How much of the YOU line's vocabulary the THEM line accounts for. The direction is the
    /// point: an echo is the mic's degraded COPY of the loopback's words, so the you-line must be
    /// mostly contained in the them-line. A long line is never an echo of a short one.
    /// </summary>
    static double Containment(string[] you, string[] them)
    {
        var yset = new HashSet<string>(you);
        var tset = new HashSet<string>(them);
        if (yset.Count == 0 || tset.Count == 0) return 0;
        // A you-line substantially LONGER than the them-line is not a copy of it: a couple of
        // extra mis-heard words is what a real echo looks like, a paragraph is not.
        if (yset.Count > tset.Count + 2) return 0;
        var shared = yset.Count(tset.Contains);
        return (double)shared / Math.Min(yset.Count, tset.Count);
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
