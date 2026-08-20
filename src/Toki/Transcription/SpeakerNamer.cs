using System.Text.RegularExpressions;

namespace Toki.Transcription;

/// <summary>One voice cluster, the name we settled on, and what convinced us.</summary>
public sealed record SpeakerLabel(int Cluster, string Name, double Score, string Evidence);

/// <summary>One turn as the namer sees it: which voice, and what was said.</summary>
/// <param name="Cluster">The voice cluster. NEGATIVE means the local user (their own mic stream), who
/// is already known and is never given a discovered name.</param>
public readonly record struct NamedTurn(int Cluster, string Text);

/// <summary>
/// Puts names to the voices from the conversation TEXT alone - no audio, no stored voiceprint
/// (owner decision: enrollment stores biometric data colleagues never agreed to). Signals:
/// self-introduction (strongest, trusted even off-roster), handover (names the NEXT speaker),
/// thanks (names the PREVIOUS speaker). Guards, all one rule - a wrong name is worse than none:
/// an off-roster name must be said at least twice; the local user's own name is never given to a
/// remote voice; below <see cref="Floor"/> the cluster keeps "Speaker 2"; one name, one cluster.
/// </summary>
public static class SpeakerNamer
{
    /// <summary>Evidence needed before a name is attached at all. One handover or one thank-you clears
    /// it; a lone weak mention does not.</summary>
    public const double Floor = 3.0;

    const double SelfIntroScore = 6.0;
    const double HandoverScore = 3.0;
    const double ThanksScore = 3.0;

    // A first name as a recogniser leaves it: one capital then lowercase. The lookahead rejects
    // possessives ("Craig's" talks ABOUT a person, never introduces one). No apostrophes or
    // hyphens inside: allowing them let "I'm" itself match.
    const string NamePattern = @"([A-Z][a-z]{2,23})(?!['\u2019]s\b)";

    static readonly RegexOptions Opts = RegexOptions.CultureInvariant;

    // Trigger phrases match case-INSENSITIVELY via inline (?i:) groups while the name group stays
    // case-SENSITIVE - a fully case-insensitive pattern would let any lowercase word be a name.

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

        // The roster arrives as calendar DISPLAY names ("Priya Devi", sometimes an email), but
        // people are ADDRESSED by first name and the capture pattern yields one word - so index
        // each word of each roster name, skipping what an email's domain would contribute.
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in roster ?? [])
        {
            var display = (r ?? "").Split('@')[0];   // "priya.devi@x.com" → "priya.devi"
            foreach (var part in display.Split(new[] { ' ', '.', '_', ',', '-' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var w = Clean(part);
                if (w.Length >= 3) known.Add(w);
            }
        }
        var own = Clean(ownName ?? "");
        // The user's own name blocks its FIRST name too: "Sid" from "Sid Kumar" must never be
        // pinned on a remote voice just because someone said "you there, Sid?".
        var ownFirst = own.Split(' ').FirstOrDefault() ?? "";

        // How often each name is said anywhere. A name nobody supplied has to earn its place by being
        // repeated - one capitalised word is far more likely a mishearing than a colleague.
        var mentions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in turns)
            foreach (Match m in Regex.Matches(t.Text ?? "", @"\b" + NamePattern + @"\b", Opts))
                Count(mentions, Clean(m.Groups[1].Value));

        // The strongest guard: a word that appears in LOWERCASE anywhere in this meeting is a word,
        // not a person ("please", "otherwise", "great"); a real name essentially never does. A
        // hand-written not-a-name list is never finished - the transcript itself generalises.
        var seenLower = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in turns)
            foreach (Match m in Regex.Matches(t.Text ?? "", @"\b[a-z][a-z']{2,23}\b", Opts))
                seenLower.Add(m.Value);

        var votes = new Dictionary<(int Cluster, string Name), (double Score, string Why)>();

        // A self-introduction (`selfSaid`) is exempt from the say-it-twice rule: the speaker is
        // telling you their own name. Every other signal infers about someone ELSE and must clear it.
        void Vote(int cluster, string name, double score, string why, bool selfSaid = false, bool rosterOnly = false)
        {
            name = Clean(name);
            if (cluster < 0 || name.Length == 0) return;                  // the user is already known
            if (!NameCorrector.LooksLikeName(name)) return;               // "Great", "Thanks", "Monday"
            // The vocative shape ("Priya, can you...") is indistinguishable from a discourse marker
            // ("Otherwise, folks can break out"), so it only counts for a human-supplied name -
            // the residue the lowercase rule cannot catch.
            if (rosterOnly && !known.Contains(name)) return;
            if (own.Length > 0 && name.Equals(own, StringComparison.OrdinalIgnoreCase)) return;
            if (ownFirst.Length > 0 && name.Equals(ownFirst, StringComparison.OrdinalIgnoreCase)) return;
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

            // Addressing somebody names the person who ANSWERS - the next speaker who is not the
            // one talking. "The next turn" would credit the asker whenever they carry on talking.
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

        // Cluster id is the final tie-break so equal-score votes resolve the same way every run -
        // dictionary enumeration order must never decide who gets a name.
        foreach (var ((cluster, name), (score, why)) in votes
            .OrderByDescending(v => v.Value.Score)
            .ThenBy(v => v.Key.Name, StringComparer.Ordinal)
            .ThenBy(v => v.Key.Cluster))
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
