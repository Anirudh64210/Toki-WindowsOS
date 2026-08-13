using System.Text;

namespace Toki.Transcription;

/// <summary>
/// Fixes mis-heard proper nouns in a transcript by making the user's OWN history the authority.
///
/// ── The problem, from two real meetings (2026-08-11) ──
/// The same person came out as "Craig" on the mic stream and "Greg" on the loopback stream; "Abhishek"
/// and "Abashik"; "Yashina" and "Yashtren"; and the owner's own name as "Anirud". Speech recognisers are
/// weakest exactly where meetings care most - names - because a name is, by definition, not in the
/// language model's usual distribution.
///
/// ── Why this and not contextual biasing ──
/// The principled fix is to bias the DECODER (sherpa-onnx supports hotwords for NeMo transducers). It is
/// unavailable to us: hotwords on a BPE model need a bpe.vocab, and the Parakeet TDT int8 archive Toki
/// ships contains only encoder/decoder/joiner/tokens.txt. It would also force modified_beam_search over
/// greedy, which costs real time on a CPU for a 25-minute meeting. So the correction happens after
/// decoding, where it is deterministic, unit-testable, free at runtime, and - the deciding advantage -
/// able to repair meetings that are ALREADY saved.
///
/// ── How it decides ──
/// Names that sound alike are clustered (phonetic key + edit distance), and each cluster collapses to one
/// canonical spelling chosen in strict order:
///   1. A TRUSTED name - the user's own name from Settings, a calendar attendee, or one they pinned.
///      These are typed by a human and are simply correct.
///   2. Otherwise the most frequent spelling across the WHOLE library. One meeting saw Craig once and
///      Greg once, a tie; the library has seen Craig many times and Greg once, which is not.
/// A cluster that cannot be decided is left exactly as it was. Silence beats a confident wrong guess -
/// the same rule the evidence links follow.
/// </summary>
public static class NameCorrector
{
    /// <summary>How different two spellings of the same name may be. Deliberately tight: these are
    /// thresholds for REWRITING somebody's words, so a miss is much cheaper than a false hit.</summary>
    const int MaxKeyDistance = 1;    // between phonetic keys
    const int MaxRawDistance = 3;    // between the spellings themselves

    /// <summary>A name the user TYPED (their own name, an attendee, a pinned term) may reach further,
    /// because they have asserted it is right. The risk stays bounded: a looser match can only ever
    /// rewrite TOWARDS a spelling a human supplied, never towards another guess.</summary>
    const int TrustedKeyDistance = 2;
    const int TrustedRawDistance = 5;

    /// <summary>A correction that was made, so the transcript can show its working.</summary>
    public sealed record Fix(string From, string To);

    /// <summary>
    /// Correct the names in <paramref name="segments"/>.
    /// </summary>
    /// <param name="trusted">Names known to be right (user's own name, calendar attendees, pinned list).</param>
    /// <param name="corpus">How often each spelling appears across the user's whole library.</param>
    public static (IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<Fix> Fixes) Correct(
        IReadOnlyList<TranscriptSegment> segments,
        IEnumerable<string>? trusted = null,
        IReadOnlyDictionary<string, int>? corpus = null)
    {
        if (segments is null || segments.Count == 0) return (segments ?? [], []);

        // Trusted names are bare names ("Anirudh"), not prose - so they are read literally. Running them
        // through Candidates would drop every one of them, because Candidates skips the first word of a
        // sentence and a bare name is always the first word.
        var trustedSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in trusted ?? [])
            foreach (var word in (t ?? "").Split(new[] { ' ', ',', ';', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var w = word.Trim();
                if (w.Length >= 3 && w.Length <= 24 && w.All(char.IsLetter)) trustedSet[w] = w;
            }

        // Every proper-noun candidate in THIS transcript, with how often it appears here.
        var local = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in segments)
            foreach (var w in Candidates(s.Text))
                local[w] = local.TryGetValue(w, out var n) ? n + 1 : 1;

        // The pool has to include names this meeting never said correctly - otherwise "Anirud" has
        // nothing to be matched against, and the whole point is repairing a spelling that only the
        // Settings page or the rest of the library knows.
        var pool = new HashSet<string>(local.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var t in trustedSet.Keys) pool.Add(t);
        if (corpus is not null)
            foreach (var w in corpus.Keys)
                if (w.Length >= 3 && w.Length <= 24 && w.All(char.IsLetter) && !Common.Contains(w)) pool.Add(w);

        var canonical = BuildMap(pool, trustedSet, local, corpus);
        if (canonical.Count == 0) return (segments, []);

        var fixes = new List<Fix>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outp = new List<TranscriptSegment>(segments.Count);
        foreach (var s in segments)
        {
            var rewritten = Rewrite(s.Text, canonical, out var used);
            foreach (var (from, to) in used)
                if (seen.Add(from)) fixes.Add(new Fix(from, to));
            outp.Add(ReferenceEquals(rewritten, s.Text) ? s : s with { Text = rewritten });
        }
        return (outp, fixes);
    }

    /// <summary>Map every mis-spelling to the one spelling its cluster agreed on. Entries that map to
    /// themselves are dropped - there is nothing to do for them.</summary>
    static Dictionary<string, string> BuildMap(
        HashSet<string> pool,
        Dictionary<string, string> trusted,
        Dictionary<string, int> local,
        IReadOnlyDictionary<string, int>? corpus)
    {
        var words = pool.ToList();
        var keys = words.ToDictionary(w => w, Phonetic, StringComparer.OrdinalIgnoreCase);

        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in words) parent[w] = w;

        string Find(string w) => parent[w].Equals(w, StringComparison.OrdinalIgnoreCase) ? w : parent[w] = Find(parent[w]);
        void Union(string a, string b) { var (ra, rb) = (Find(a), Find(b)); if (!ra.Equals(rb, StringComparison.OrdinalIgnoreCase)) parent[ra] = rb; }

        for (var i = 0; i < words.Count; i++)
            for (var j = i + 1; j < words.Count; j++)
            {
                var lenient = trusted.ContainsKey(words[i]) || trusted.ContainsKey(words[j]);
                if (SoundAlike(words[i], words[j], keys, lenient)) Union(words[i], words[j]);
            }

        var clusters = words.GroupBy(Find, StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cluster in clusters)
        {
            var members = cluster.ToList();
            if (members.Count < 2) continue;

            // 1. A trusted spelling wins outright. Two trusted spellings in one cluster means they are
            //    genuinely different people who happen to sound alike (a real Greg AND a real Craig), so
            //    nothing is touched.
            var trustedHere = members.Where(trusted.ContainsKey).ToList();
            if (trustedHere.Count > 1) continue;

            string? winner = trustedHere.FirstOrDefault();
            if (winner is null)
            {
                // 2. The library's vote. Fall back to this meeting's own counts when there is no corpus
                //    (a first-ever meeting), and refuse to decide a dead tie.
                int Score(string w) =>
                    (corpus is not null && corpus.TryGetValue(w, out var c) ? c : 0) * 100
                    + (local.TryGetValue(w, out var l) ? l : 0);

                var ranked = members.OrderByDescending(Score).ThenBy(w => w, StringComparer.Ordinal).ToList();
                if (Score(ranked[0]) == Score(ranked[1])) continue;   // a coin flip is not an answer
                winner = ranked[0];
            }

            foreach (var m in members)
                if (!m.Equals(winner, StringComparison.OrdinalIgnoreCase))
                    map[m] = winner;
        }

        return map;
    }

    static bool SoundAlike(string a, string b, Dictionary<string, string> keys, bool lenient)
    {
        var maxRaw = lenient ? TrustedRawDistance : MaxRawDistance;
        var maxKey = lenient ? TrustedKeyDistance : MaxKeyDistance;

        if (Math.Abs(a.Length - b.Length) > maxRaw) return false;
        if (Distance(a.ToLowerInvariant(), b.ToLowerInvariant()) > maxRaw) return false;
        if (Distance(keys[a], keys[b]) > maxKey) return false;

        // They must also begin with the same SOUND, which stops a generous distance turning every short
        // name into a candidate for every other one. Note this is the phonetic first character, not the
        // written one: Craig and Greg both start "K", which is the entire point.
        var ka = keys[a]; var kb = keys[b];
        return ka.Length > 0 && kb.Length > 0 && ka[0] == kb[0];
    }

    // ── the words we are willing to touch ──

    /// <summary>
    /// Proper-noun candidates: a capitalised word, not at the start of a sentence, that isn't an
    /// ordinary English word.
    ///
    /// All three conditions matter. Capitalisation is the only signal the recogniser gives us that it
    /// heard a name; sentence-initial words are capitalised for grammar, not meaning; and the common-word
    /// list is what stops "Great" becoming "Craig" - the single most damaging thing this code could do.
    /// </summary>
    /// <summary>How often each proper-noun candidate appears across a body of text. This is what makes
    /// the user's whole library the authority on a name: within one meeting Craig and Greg were one
    /// occurrence each, and across the library it is not close.</summary>
    public static Dictionary<string, int> CountNames(IEnumerable<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
            foreach (var w in Candidates(line))
                counts[w] = counts.TryGetValue(w, out var n) ? n + 1 : 1;
        return counts;
    }

    /// <summary>
    /// Could this word be somebody's name? Capitalised, letters only, sane length, and not one of the
    /// ordinary words a recogniser capitalises mid-sentence. Shared with <see cref="SpeakerNamer"/> so
    /// there is ONE list of words that can never be a person - two copies would drift, and the drift
    /// would show up as "Great" becoming a participant.
    /// </summary>
    public static bool LooksLikeName(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        var w = word.Trim('\'', '"', '(', ')');
        return w.Length is >= 3 and <= 24
            && char.IsUpper(w[0])
            && w.All(c => char.IsLetter(c) || c is '\'' or '-')
            && !Common.Contains(w);
    }

    static IEnumerable<string> Candidates(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var sentenceStart = true;
        foreach (var raw in Tokenize(text))
        {
            var word = raw.Trim('\'', '"', '(', ')');
            var atStart = sentenceStart;
            sentenceStart = raw.Length > 0 && (raw[^1] is '.' or '?' or '!');

            if (word.Length < 3 || word.Length > 24) continue;
            if (!char.IsUpper(word[0])) continue;
            if (!word.All(char.IsLetter)) continue;
            if (atStart) continue;                       // "Please" opening a sentence is not a name
            if (Common.Contains(word)) continue;
            yield return word;
        }
    }

    static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetter(ch) || ch is '\'' or '-') sb.Append(ch);
            else
            {
                if (sb.Length > 0) { yield return sb.ToString() + (ch is '.' or '?' or '!' ? ch.ToString() : ""); sb.Clear(); }
                else if (ch is '.' or '?' or '!') yield return ch.ToString();
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>Capitalised words that are emphatically NOT names. Short and blunt on purpose: this list
    /// only has to cover words a recogniser capitalises mid-sentence.</summary>
    static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "The", "This", "That", "These", "Those", "There", "Then", "They", "Their", "Them",
        "And", "But", "For", "Not", "You", "Your", "Yes", "Okay", "OK", "All", "Any", "Are",
        "Can", "Could", "Should", "Would", "Will", "What", "When", "Where", "Which", "Who", "Why",
        "How", "Just", "Like", "Well", "Sure", "Thanks", "Thank", "Hello", "Hey", "Right", "Great",
        "Good", "Yeah", "Come", "Let", "Now", "One", "Two", "Some", "Also", "Even", "Ever",
        "Still", "Very", "Much", "More", "Most", "Team", "Teams", "Meeting", "Sheet", "Folder",
        // Sentence adverbs and discourse markers. These open a sentence followed by a comma, which is
        // exactly the shape of addressing someone by name - a real meeting produced participants called
        // "Please" and "Otherwise" before this block existed.
        "Please", "Otherwise", "However", "Therefore", "Meanwhile", "Anyway", "Actually", "Basically",
        "Honestly", "Obviously", "Finally", "First", "Second", "Third", "Next", "Again", "Alright",
        "Sorry", "Perfect", "Exactly", "Absolutely", "Certainly", "Definitely", "Maybe", "Instead",
        "Unfortunately", "Fortunately", "Similarly", "Additionally", "Overall", "Besides", "Moreover",
        "Furthermore", "Nevertheless", "Regardless", "Personally", "Hopefully", "Essentially",
        // People-shaped words that are not a person.
        "Everyone", "Everybody", "Anyone", "Anybody", "Someone", "Somebody", "Nobody", "Others",
        "Another", "Everything", "Something", "Nothing", "Anything", "Welcome", "Sounds", "Awesome",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
        "January", "February", "March", "April", "May", "June", "July",
        "August", "September", "October", "November", "December",
    };

    // ── rewriting ──

    static string Rewrite(string text, Dictionary<string, string> map, out List<(string From, string To)> used)
    {
        var applied = new List<(string From, string To)>();
        used = applied;
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        var word = new StringBuilder();
        var changed = false;

        void Flush()
        {
            if (word.Length == 0) return;
            var w = word.ToString();
            if (map.TryGetValue(w, out var to) && !w.Equals(to, StringComparison.Ordinal))
            {
                sb.Append(to);
                applied.Add((w, to));
                changed = true;
            }
            else sb.Append(w);
            word.Clear();
        }

        foreach (var ch in text)
        {
            if (char.IsLetter(ch)) word.Append(ch);
            else { Flush(); sb.Append(ch); }
        }
        Flush();

        return changed ? sb.ToString() : text;
    }

    // ── phonetics ──

    /// <summary>
    /// A compact Metaphone: enough to make Craig and Greg agree (both KRK), and Punita and Panita (PNT),
    /// without the size of the full Double Metaphone. Vowels survive only in first position, which is
    /// what makes it robust to the vowel confusion recognisers actually produce.
    /// </summary>
    public static string Phonetic(string word)
    {
        if (string.IsNullOrEmpty(word)) return "";
        var w = word.ToUpperInvariant();
        var sb = new StringBuilder(w.Length);

        for (var i = 0; i < w.Length; i++)
        {
            var c = w[i];
            if (!char.IsLetter(c)) continue;
            var next = i + 1 < w.Length ? w[i + 1] : '\0';
            var prev = i > 0 ? w[i - 1] : '\0';

            if (c == prev && c != 'C') continue;               // collapse doubles

            switch (c)
            {
                case 'A': case 'E': case 'I': case 'O': case 'U':
                    if (i == 0) sb.Append(c);
                    break;
                case 'B': if (!(i == w.Length - 1 && prev == 'M')) sb.Append('B'); break;
                case 'C':
                    if (next == 'H') { sb.Append('X'); i++; }
                    else if (next is 'I' or 'E' or 'Y') sb.Append('S');
                    else sb.Append('K');
                    break;
                case 'D': if (next == 'G') { sb.Append('J'); i++; } else sb.Append('T'); break;
                case 'G': if (next == 'H' && i + 2 >= w.Length) { i++; } else sb.Append('K'); break;
                case 'H': if (IsVowel(prev) && !IsVowel(next)) break; sb.Append('H'); break;
                case 'K': if (prev != 'C') sb.Append('K'); break;
                case 'P': if (next == 'H') { sb.Append('F'); i++; } else sb.Append('P'); break;
                case 'Q': sb.Append('K'); break;
                case 'S': if (next == 'H') { sb.Append('X'); i++; } else sb.Append('S'); break;
                case 'T':
                    if (next == 'H') { sb.Append('0'); i++; }
                    else sb.Append('T');
                    break;
                case 'V': sb.Append('F'); break;
                case 'W': case 'Y': if (IsVowel(next)) sb.Append(c); break;
                case 'X': sb.Append("KS"); break;
                case 'Z': sb.Append('S'); break;
                default: sb.Append(c); break;                  // F J L M N R
            }
        }
        return sb.ToString();
    }

    static bool IsVowel(char c) => c is 'A' or 'E' or 'I' or 'O' or 'U';

    /// <summary>Levenshtein, bounded by the shorter row so it stays cheap on the O(n^2) pairing above.</summary>
    static int Distance(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

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
