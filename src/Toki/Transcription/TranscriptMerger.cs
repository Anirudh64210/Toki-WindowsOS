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
/// match - fuzzy-matching interjections like "yeah"/"okay" would wrongly delete
/// real things the user said.
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
    /// Move a short backchannel out of the middle of someone else's sentence.
    ///
    /// The two streams are transcribed separately and merged by start time, so a listener's "yeah" that
    /// lands mid-sentence is inserted BETWEEN two halves of the speaker's thought:
    ///     You:  "did Craig mention ask"
    ///     Them: "idea."
    ///     You:  "you to like do this?"
    /// Nothing is wrong with the timestamps - that really is when it was said - but it makes the
    /// transcript read as gibberish and it stops the two halves ever being joined. So a very short
    /// interjection that sits inside a continuing sentence is shifted to just after it. Its timestamp is
    /// untouched, only its position, and only when the speaker demonstrably carries straight on.
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
    /// Join consecutive lines from the same person into one turn.
    ///
    /// Parakeet emits a segment per breath group, which gave 182 segments for an 8-minute meeting - one
    /// every 2.6 seconds. Rendered, that is a wall of two-word paragraphs each with its own speaker
    /// label; read by the note writer, it is a transcript chopped at every pause. Joining them costs
    /// nothing (the timestamps of the first and last are kept, so evidence links still resolve) and
    /// makes every downstream consumer better at once.
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
                    outp[^1] = prev with { EndMs = s.EndMs, Text = Stitch(prev.Text, text) };
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
    /// Same person, for the purposes of joining and de-interleaving.
    ///
    /// This MUST NOT be a plain <c>a.Speaker == b.Speaker</c>. That enum only says which stream the audio
    /// came from, so every remote participant answers to "Them" - and two different people talking back
    /// to back with a short gap (the single most ordinary thing that happens in a group call) would be
    /// welded into one turn attributed to whoever happened to be first. Once diarisation has run, the
    /// resolved identity is the thing that decides.
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

    /// <summary>Shared words as a fraction of the SHORTER line - catches echo where
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
