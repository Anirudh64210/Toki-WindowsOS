using System.Text.RegularExpressions;

namespace Toki.Transcription;

/// <summary>One voice cluster, the name we settled on, and what convinced us.</summary>
public sealed record SpeakerLabel(int Cluster, string Name, double Score, string Evidence);

/// <summary>One turn as the namer sees it: which voice, and what was said.</summary>
/// <param name="Cluster">The voice cluster. NEGATIVE means the local user (their own mic stream), who
/// is already known and is never given a discovered name.</param>
public readonly record struct NamedTurn(int Cluster, string Text);

/// <summary>
/// Puts names to the voices, WITHOUT storing anyone's voice.
///
/// Diarisation can say "these turns are the same person" but it can never say who that person is. The
/// obvious way to close the gap is voice enrollment: keep a fingerprint per person, recognise them next
/// time. Toki deliberately does not. Enrollment means storing biometric data for colleagues who never
/// agreed to it, and it is the one thing in this whole area with active litigation attached. Wispr Flow
/// shipped a notetaker in August 2026 that names speakers with no voiceprints at all, and states so as a
/// product promise; the signals they use are the ones below, and Toki already has every one of them.
///
/// So the name comes from the conversation itself:
///
///   • <b>Self-introduction</b> - "I'm Priya", "Priya here". The strongest signal there is: the speaker
///     is telling you. Trusted even for a name nobody supplied.
///   • <b>Handover</b> - a turn that ENDS by addressing someone ("over to you, Priya", "Priya, thoughts?")
///     names the person who speaks NEXT.
///   • <b>Thanks</b> - a turn that STARTS by addressing someone ("thanks, Priya", "good point Priya")
///     names the person who spoke BEFORE.
///
/// Three rules keep it from inventing people, all of them the same rule really - a wrong name is worse
/// than no name, exactly as with the evidence links:
///
///   1. A name from the roster (calendar attendees, pinned names) is trusted. A name that is NOT on the
///      roster has to be said at least twice before it counts, because a single capitalised word is more
///      likely a mishearing than a new colleague.
///   2. The local user's own name is never given to a remote voice. In a one-to-one the other person
///      says your name constantly, and every one of those is about you, not about them.
///   3. Below <see cref="Floor"/>, the cluster keeps its "Speaker 2" label. One name, one cluster: a
///      name already spent cannot be assigned again.
///
/// Everything here is text. No audio, no model, no stored fingerprint - which is what makes it testable
/// against a real transcript, and what makes it forget everyone the moment the meeting is over.
/// </summary>
public static class SpeakerNamer
{
    /// <summary>Evidence needed before a name is attached at all. One handover or one thank-you clears
    /// it; a lone weak mention does not.</summary>
    public const double Floor = 3.0;

    const double SelfIntroScore = 6.0;
    const double HandoverScore = 3.0;
    const double ThanksScore = 3.0;

    // A first name as it survives a recogniser: one capital then lowercase letters. The lookahead is
    // what stops "this is Craig's team" naming somebody "Craig" - a possessive is talking ABOUT a person,
    // never introducing one. Deliberately no apostrophes or hyphens inside: allowing them let "I'm"
    // itself match, and the names this has to catch in practice are first names.
    const string NamePattern = @"([A-Z][a-z]{2,23})(?!['\u2019]s\b)";

    static readonly RegexOptions Opts = RegexOptions.CultureInvariant;

    // The trigger phrases are matched case-INSENSITIVELY via inline (?i:) groups, while the name group
    // stays case-SENSITIVE. Both halves matter: a sentence can start "Over to you, Priya" as easily as
    // "over to you", and making the whole pattern case-insensitive would let any lowercase word be a name.
    // (Getting this wrong is why the first version of these tests failed.)

    // "I'm Priya" · "I am Priya" · "my name is Priya" · "Priya here" · "Priya, present" (a roll call).
    // "this is X" is deliberately absent - "this is Craig's account" is not an introduction.
    static readonly Regex[] SelfIntro =
    {
        new(@"(?i:\b(?:I'?m|I am|my name is))\s+" + NamePattern + @"\b", Opts),
        new(@"(?:^|[.!?]\s+)" + NamePattern + @"(?i:\s+here)\b", Opts),
        new(@"(?:^|[.!?]\s+)" + NamePattern + @"(?i:[,]?\s+(?:present|speaking))\b", Opts),
    };

    // Ends by handing the floor over. The NEXT speaker is the named one.
    static readonly Regex[] Handover =
    {
        new(@"(?i:\b(?:over to you|back to you|go ahead|take it away|all yours))[,\s]+" + NamePattern + @"[\s?.!]*$", Opts),
        new(@"(?i:\b(?:what do you think|any thoughts|your thoughts|thoughts|do you want to add anything))[,\s]+" + NamePattern + @"[\s?.!]*$", Opts),
        new(@",\s*" + NamePattern + @"\s*\?\s*$", Opts),
    };

    // Opens by answering or thanking someone. The PREVIOUS speaker is the named one.
    static readonly Regex[] Thanks =
    {
        new(@"^(?i:(?:thanks|thank you|cheers|nice one|good point|great point|agreed|exactly|fair enough))[,\s]+" + NamePattern + @"\b", Opts),
        new(@"^(?i:(?:thanks|thank you)\s+for\s+(?:that|those))[,\s]+" + NamePattern + @"\b", Opts),
    };

    // Addresses someone, at the start of a turn or of a sentence inside it. The NEXT speaker is the
    // named one ("Priya, can you take this?", "thanks. Marcus, are you there?"). Anchored to a sentence
    // boundary on purpose: mid-sentence, "I spoke to Marcus, and he said" is about Marcus, not to him.
    static readonly Regex Vocative = new(@"(?:^|[.!?]\s+)" + NamePattern + @",\s+\S", Opts);

    /// <summary>
    /// Work out who each voice cluster is. Clusters with no verdict are simply absent from the result -
    /// the caller keeps calling them "Speaker 2", which is honest.
    /// </summary>
    /// <param name="turns">Every turn in order, across both streams. Negative cluster = the local user.</param>
    /// <param name="roster">Names known to be in the room: calendar attendees, pinned names.</param>
    /// <param name="ownName">The user's own name, never assigned to a remote voice.</param>
    public static IReadOnlyList<SpeakerLabel> Name(
        IReadOnlyList<NamedTurn> turns,
        IEnumerable<string>? roster = null,
        string? ownName = null)
    {
        if (turns.Count == 0) return [];

        var known = new HashSet<string>(
            (roster ?? []).Select(Clean).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
        var own = Clean(ownName ?? "");

        // How often each name is said anywhere. A name nobody supplied has to earn its place by being
        // repeated - one capitalised word is far more likely a mishearing than a colleague.
        var mentions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in turns)
            foreach (Match m in Regex.Matches(t.Text ?? "", @"\b" + NamePattern + @"\b", Opts))
                Count(mentions, Clean(m.Groups[1].Value));

        // The strongest guard here, and the one that was missing when this first met a real meeting.
        //
        // A hand-written list of words-that-are-not-names is never finished. The first run against a
        // real recording produced participants called "Please" and "Otherwise", from lines like
        // "Please, please break out right away, everyone" - both perfectly good vocative shapes, neither
        // a person. Extending the list would have fixed those two and missed the next two.
        //
        // So use the transcript itself: collect every word that appears in LOWERCASE anywhere in this
        // meeting. "please", "otherwise", "great", "sorry" all do, constantly. A real name essentially
        // never does. It costs one pass and it generalises to words no list would have thought of.
        var seenLower = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in turns)
            foreach (Match m in Regex.Matches(t.Text ?? "", @"\b[a-z][a-z']{2,23}\b", Opts))
                seenLower.Add(m.Value);

        var votes = new Dictionary<(int Cluster, string Name), (double Score, string Why)>();

        // `selfSaid` marks a self-introduction, which is exempt from the say-it-twice rule: the speaker
        // is telling you their own name, and needing them to do it twice would miss the commonest case
        // in any meeting with a new face. Every other signal is an inference about someone ELSE, and
        // those still have to clear the bar.
        void Vote(int cluster, string name, double score, string why, bool selfSaid = false, bool rosterOnly = false)
        {
            name = Clean(name);
            if (cluster < 0 || name.Length == 0) return;                  // the user is already known
            if (!NameCorrector.LooksLikeName(name)) return;               // "Great", "Thanks", "Monday"
            // The vocative shape ("Priya, can you...") is indistinguishable from a discourse marker
            // ("Otherwise, folks can break out"), so it only counts for a name a human supplied. This
            // is the residue the lowercase rule cannot catch: a word that happens never to be said in
            // lowercase in this particular meeting. Measured, not assumed - "Otherwise" survived every
            // other guard on a real recording.
            if (rosterOnly && !known.Contains(name)) return;
            if (own.Length > 0 && name.Equals(own, StringComparison.OrdinalIgnoreCase)) return;
            // Said in lowercase elsewhere in this meeting? Then it is a word, not a person - unless a
            // human put it on the roster, in which case they know better than any heuristic.
            if (!known.Contains(name) && seenLower.Contains(name.ToLowerInvariant())) return;
            if (!selfSaid && !known.Contains(name) && mentions.GetValueOrDefault(name) < 2) return;

            var key = (cluster, name);
            var had = votes.GetValueOrDefault(key);
            votes[key] = (had.Score + score, had.Why ?? why);
        }

        for (var i = 0; i < turns.Count; i++)
        {
            var text = (turns[i].Text ?? "").Trim();
            if (text.Length == 0) continue;

            foreach (var rx in SelfIntro)
                foreach (Match m in rx.Matches(text))
                    Vote(turns[i].Cluster, m.Groups[1].Value, SelfIntroScore, $"introduced themselves: \"{Snippet(text)}\"", selfSaid: true);

            // Addressing somebody names the person who ANSWERS - which is the next speaker who is not
            // the one talking. Using "the next turn" instead attributes the name to the asker whenever
            // they carry on, and they usually do: measured on a real 11-person call, "Yashtren, are you
            // there?" labelled the chair Yashtren, because the chair's own next sentence came first.
            var next = NextOther(turns, i);
            var prev = PrevOther(turns, i);

            if (next is int nx)
            {
                foreach (var rx in Handover)
                    if (rx.Match(text) is { Success: true } m)
                        Vote(turns[nx].Cluster, m.Groups[1].Value, HandoverScore, $"handed over: \"{Snippet(text)}\"");

                if (Vocative.Match(text) is { Success: true } v)
                    Vote(turns[nx].Cluster, v.Groups[1].Value, HandoverScore, $"addressed: \"{Snippet(text)}\"", rosterOnly: true);
            }

            if (prev is int pv)
                foreach (var rx in Thanks)
                    if (rx.Match(text) is { Success: true } m)
                        Vote(turns[pv].Cluster, m.Groups[1].Value, ThanksScore, $"answered: \"{Snippet(text)}\"");
        }

        // Assign greedily, strongest evidence first. One name to one cluster, both ways: two clusters
        // that both look like "Priya" means one of them is wrong, and the weaker one keeps its number.
        var takenCluster = new HashSet<int>();
        var takenName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outp = new List<SpeakerLabel>();

        foreach (var ((cluster, name), (score, why)) in votes.OrderByDescending(v => v.Value.Score).ThenBy(v => v.Key.Name, StringComparer.Ordinal))
        {
            if (score < Floor) continue;
            if (!takenCluster.Add(cluster)) continue;
            if (!takenName.Add(name)) { takenCluster.Remove(cluster); continue; }
            outp.Add(new SpeakerLabel(cluster, name, score, why));
        }

        return outp.OrderBy(l => l.Cluster).ToList();
    }

    /// <summary>How far to look for the other person. An answer that arrives five turns later is not an
    /// answer to this, it is a different part of the conversation.</summary>
    const int Reach = 4;

    /// <summary>The next turn spoken by somebody OTHER than whoever is speaking at <paramref name="i"/>.</summary>
    static int? NextOther(IReadOnlyList<NamedTurn> turns, int i)
    {
        for (var j = i + 1; j < turns.Count && j <= i + Reach; j++)
            if (turns[j].Cluster != turns[i].Cluster) return j;
        return null;
    }

    /// <summary>The previous turn spoken by somebody OTHER than whoever is speaking at <paramref name="i"/>.</summary>
    static int? PrevOther(IReadOnlyList<NamedTurn> turns, int i)
    {
        for (var j = i - 1; j >= 0 && j >= i - Reach; j--)
            if (turns[j].Cluster != turns[i].Cluster) return j;
        return null;
    }

    /// <summary>The label a cluster shows when nothing named it. Numbered from 1 for humans.</summary>
    public static string Fallback(int cluster) => $"Speaker {cluster + 1}";

    static void Count(Dictionary<string, int> d, string key)
    {
        if (key.Length == 0) return;
        d[key] = d.GetValueOrDefault(key) + 1;
    }

    static string Clean(string s) => (s ?? "").Trim().Trim('\'', '"', ',', '.', '-');

    static string Snippet(string text) => text.Length <= 60 ? text : text[..57].TrimEnd() + "...";
}
