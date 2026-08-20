using System.Text;

namespace Toki.Transcription;

/// <summary>
/// Fixes mis-heard proper nouns post-ASR, with the user's own history as the authority: sound-alike
/// names cluster (phonetic key + edit distance) and collapse to a TRUSTED spelling (human-typed) or
/// the library's most frequent one; an undecided cluster is left alone - silence beats a wrong guess.
/// Decode-time hotwords are not an option: the shipped Parakeet archive has no bpe.vocab, and
/// post-ASR correction also repairs meetings that are already saved.
/// </summary>
public static class NameCorrector
{
    // Distance budgets must SCALE WITH LENGTH: a flat edit budget on a short word is a licence to
    // rewrite anything (ordinary words became pinned names). The rules live in SoundAlike as
    // three explicit branches (homophone, near-spelling, trusted long-name).

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

        // Trusted names are bare names, read literally: Candidates skips the first word of a
        // sentence, and a bare name is always the first word.
        var trustedSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in trusted ?? [])
            foreach (var word in (t ?? "").Split(new[] { ' ', ',', ';', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var w = word.Trim();
                // The Common filter applies to trusted names too: an attendee named "May" or "Will"
                // must not become a magnet with the trusted reach; a missed fix is the cheap side.
                if (w.Length >= 3 && w.Length <= 24 && w.All(char.IsLetter) && !Common.Contains(w)) trustedSet[w] = w;
            }

        // Every proper-noun candidate in THIS transcript, with how often it appears here.
        var local = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in segments)
            foreach (var w in Candidates(s.Text))
                local[w] = local.TryGetValue(w, out var n) ? n + 1 : 1;

        // The pool must include names this meeting never said correctly - the point is repairing
        // a spelling that only the Settings page or the rest of the library knows.
        var pool = new HashSet<string>(local.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var t in trustedSet.Keys) pool.Add(t);
        if (corpus is not null)
            foreach (var w in corpus.Keys)
                if (w.Length >= 3 && w.Length <= 24 && w.All(char.IsLetter) && !Common.Contains(w)) pool.Add(w);

        var canonical = BuildMap(pool, trustedSet, local, corpus);
        if (canonical.Count == 0) return (segments, []);

        // Trusted-winner corrections may also fire sentence-initial (a sentence often OPENS with a
        // name when addressing someone); corpus-voted ones don't - there the capital is just grammar.
        var towardTrusted = new HashSet<string>(
            canonical.Where(kv => trustedSet.ContainsKey(kv.Value)).Select(kv => kv.Key),
            StringComparer.OrdinalIgnoreCase);

        var fixes = new List<Fix>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outp = new List<TranscriptSegment>(segments.Count);
        foreach (var s in segments)
        {
            var rewritten = Rewrite(s.Text, canonical, local, towardTrusted, out var used);
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

            // 1. A trusted spelling wins outright. Two trusted spellings in one cluster means two
            //    real people who sound alike - nothing is touched.
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

            var winnerTrusted = trusted.ContainsKey(winner);
            foreach (var m in members)
            {
                if (m.Equals(winner, StringComparison.OrdinalIgnoreCase)) continue;

                // Union-find is transitive but the distance budgets are not: every rewrite must
                // pass the pair test against the winner DIRECTLY, never through an intermediate.
                if (!SoundAlike(m, winner, keys, winnerTrusted)) continue;

                // An ESTABLISHED spelling (seen 3+ times in the library) is a person, not a
                // mishearing: the corpus vote may not erase it, only a trusted winner can.
                if (!winnerTrusted && corpus is not null && corpus.TryGetValue(m, out var seen) && seen >= 3)
                    continue;

                map[m] = winner;
            }
        }

        return map;
    }

    static bool SoundAlike(string a, string b, Dictionary<string, string> keys, bool lenient)
    {
        var ka = keys[a]; var kb = keys[b];
        if (ka.Length == 0 || kb.Length == 0) return false;

        // Must begin with the same SOUND (phonetic first char: Craig/Greg both open "K").
        // Opening vowels are one class - recognisers routinely swap a name's opening vowel
        // (Abhishek/Ubishek); consonant openings stay strict.
        if (ka[0] != kb[0] && !(IsVowel(ka[0]) && IsVowel(kb[0]))) return false;

        var minLen = Math.Min(a.Length, b.Length);
        var keyDist = Distance(ka, kb);
        var rawDist = Distance(a.ToLowerInvariant(), b.ToLowerInvariant());

        // Very short names need their VOWELS to agree: the phonetic key drops non-initial vowels,
        // collapsing Tim/Tom, Dan/Don, Ray/Roy to one key. At 4 letters or less with no
        // human-vouched target, the vowel sequences must match exactly.
        if (a.Length <= 4 && b.Length <= 4 && !lenient && !SameVowels(a, b)) return false;

        // 1. Homophones: identical phonetic key, up to 3 spelling edits (Craig/Greg, Punita/Panita).
        if (keyDist == 0 && rawDist <= 3) return true;

        // 2. Near-spellings: one phonetic edit, tight raw budget - never on very short words,
        //    where one key character is most of the sound (Anirud/Anirudh; Abashik/Abhishek).
        if (keyDist == 1 && minLen >= 4 && rawDist <= (minLen >= 6 ? 3 : 2)) return true;

        // 3. Trusted reach: a human-typed name may pull in a rougher mishearing, but ONLY a long
        //    one sharing its opening sounds (Yashtren/Yashina) - the length floor is what stops
        //    the same leniency rewriting unrelated short names.
        if (lenient && keyDist <= 2 && rawDist <= 5 && minLen >= 6
            && ka.Length >= 2 && kb.Length >= 2 && ka[1] == kb[1]) return true;

        return false;
    }

    /// <summary>The vowel letters of each word, in order, compared exactly. "tim"/"tom" differ
    /// (i vs o); "greg"/"gregg" agree (e vs e).</summary>
    static bool SameVowels(string a, string b)
    {
        static string Vowels(string w) => new(w.ToLowerInvariant().Where(c => c is 'a' or 'e' or 'i' or 'o' or 'u').ToArray());
        return Vowels(a) == Vowels(b);
    }

    // ── the words we are willing to touch ──

    /// <summary>How often each proper-noun candidate appears across a body of text - what makes
    /// the user's whole library the authority on a name.</summary>
    public static Dictionary<string, int> CountNames(IEnumerable<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
            foreach (var w in Candidates(line))
                counts[w] = counts.TryGetValue(w, out var n) ? n + 1 : 1;
        return counts;
    }

    /// <summary>
    /// Could this word be somebody's name? Capitalised, letters only, sane length, not a common
    /// word. Shared with <see cref="SpeakerNamer"/> so there is ONE list of words that can never
    /// be a person - two copies would drift.
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
            // Strip terminal punctuation BEFORE the letters-only test, or names that habitually
            // end sentences never reach the corpus and lose every frequency vote.
            var word = raw.Trim('\'', '"', '(', ')', '.', '?', '!', ',', ';', ':');
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
        // Sentence adverbs and discourse markers: they open a sentence followed by a comma,
        // exactly the shape of addressing someone by name.
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
        // Everyday words a recogniser stray-capitalises. Deliberately EXCLUDES words that are also
        // common first names (Bill, Mark, Grace, Ray, Dawn): a missed fix is cheaper than
        // deleting a real person.
        "Get", "Got", "Give", "Gave", "Take", "Took", "Make", "Made", "Making",
        "Work", "Works", "Working", "Worked",
        "Day", "Days", "Date", "Dates", "Time", "Times", "Week", "Weeks", "Month", "Months",
        "Year", "Years", "Today", "Tomorrow", "Tonight", "Yesterday",
        "Morning", "Evening", "Afternoon", "Night", "Hour", "Hours", "Minute", "Minutes",
        "Mail", "Email", "Call", "Called", "Calls", "Chat", "Message", "Messages",
        "Open", "Opened", "Close", "Closed", "Start", "Started", "Stop", "Stopped",
        "End", "Ended", "Send", "Sent", "Pay", "Paid", "Lead", "Leads",
        "Middle", "High", "Low", "Top", "Bottom", "Back", "Front", "Side",
        "Home", "Office", "Room", "Line", "Lines", "List", "Lists", "Page", "Pages",
        "Note", "Notes", "Point", "Case", "Cases", "Part", "Parts", "Plan", "Plans", "Play",
        "Sales", "Sale", "Space", "Drive", "Data", "File", "Files", "Form", "Forms",
        "Link", "Site", "App", "Apps", "Code", "Test", "Tests", "Check", "Checked",
        "Community", "Company", "Companies", "Client", "Clients", "Customer", "Customers",
        "People", "Person", "Group", "Groups", "Member", "Members", "Manager", "Managers",
        "Report", "Reports", "Update", "Updates", "Status", "Number", "Numbers", "Order", "Orders",
        "Question", "Questions", "Answer", "Answers", "Thing", "Things", "Stuff",
        "Way", "Ways", "Kind", "Sort", "Type", "Types", "Name", "Names", "Word", "Words",
        "Talk", "Look", "Looks", "Wait", "Move", "Turn", "Run", "Runs", "Help",
        "Need", "Needs", "Want", "Wants", "Know", "Knows", "Think", "Thinks",
        "Say", "Says", "Said", "Tell", "Told", "Ask", "Asked", "Feel", "Felt",
        "Find", "Found", "Keep", "Kept", "Put", "Puts", "Try", "Tried", "Use", "Used",
        "Show", "Shows", "Mean", "Means", "Leave",
        "Bro", "Guy", "Guys", "Man", "Boss", "Sir", "Pop", "Hell", "God", "Damn",
        "Cool", "Nice", "Fine", "Portal", "Service", "Services", "Diet",
        "Doc", "Docs", "Document", "Documents", "Slide", "Slides", "Deck",
        "Video", "Audio", "Screen", "Screens", "Share", "Shared", "Record", "Recording", "Half",
    };

    // ── rewriting ──

    /// <summary>
    /// Apply the map, but only to tokens that could actually BE a matched name: replacement is
    /// gated by the same conditions as detection. The token must be capitalised (a lowercase word
    /// was never evidence of a name), and a sentence-initial token is rewritten only when the word
    /// also appears mid-sentence here, or the correction lands on a TRUSTED name (being addressed).
    /// </summary>
    static string Rewrite(string text, Dictionary<string, string> map,
        Dictionary<string, int> midSentence, HashSet<string> towardTrusted,
        out List<(string From, string To)> used)
    {
        var applied = new List<(string From, string To)>();
        used = applied;
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        var word = new StringBuilder();
        var changed = false;
        var sentenceStart = true;

        void Flush()
        {
            if (word.Length == 0) return;
            var token = word.ToString();
            word.Clear();
            var atStart = sentenceStart;
            sentenceStart = false;

            // A hyphenated compound ("Jean-Luc") was rejected as a detection candidate, so it is
            // exempt from rewriting too - never rewrite the halves independently.
            if (token.Contains('-')) { sb.Append(token); return; }

            // A possessive ("Greg's") is corrected on its stem and keeps its suffix.
            var suffix = "";
            var w = token;
            if (w.EndsWith("'s", StringComparison.OrdinalIgnoreCase)) { suffix = w[^2..]; w = w[..^2]; }
            else if (w.EndsWith("'")) { suffix = "'"; w = w[..^1]; }
            w = w.Trim('\'');
            if (w.Length == 0 || !w.All(char.IsLetter)) { sb.Append(token); return; }

            var eligible = char.IsUpper(w[0])
                && (!atStart || midSentence.ContainsKey(w) || towardTrusted.Contains(w));
            if (eligible && map.TryGetValue(w, out var to) && !w.Equals(to, StringComparison.Ordinal))
            {
                sb.Append(to).Append(suffix);
                applied.Add((w, to));
                changed = true;
            }
            else sb.Append(token);
        }

        foreach (var ch in text)
        {
            if (char.IsLetter(ch) || ch is '\'' or '-') word.Append(ch);
            else
            {
                Flush();
                sb.Append(ch);
                if (ch is '.' or '?' or '!') sentenceStart = true;
            }
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
