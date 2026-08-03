using System.Text.RegularExpressions;

namespace Toki.Llm;

/// <summary>
/// Tier 0.7 of the dialogue brain — the USER_FACT / IDENTITY turns:
///
/// 1. The user STATES a fact about themself ("call me Anirudh", "my name is …", "I work at …") →
///    capture it, remember it (persisted via <see cref="UserProfile"/>), and acknowledge naturally.
/// 2. The user asks about THEMSELF ("what is my name", "who am I", "what do you know about me") →
///    answer from the profile — NEVER a notes search. ("What is my name" being searched against the
///    meeting notes and answered with three citations was a real, screenshot-verified failure: the
///    old pipeline had no concept of a fact the user just told it.)
///
/// Same discipline as SmallTalk/Persona: anchored whole-utterance patterns with the fuzzy wake-name
/// stripped, so "did we discuss my name in the sync?" still falls through to the notes Q&amp;A.
/// Deterministic, ~0ms, no model.
/// </summary>
public static class UserFacts
{
    // "call me X" / "my name is X" / "i'm called X" — allowing the connective lead the live
    // screenshot produced ("BUT YOU call me anirudh"). The captured value is 1–3 word-ish tokens.
    static readonly Regex NameRx = new(
        @"^(?:(?:but|and|no|hey|well|actually|so|btw|please|listen)[,\s]+)*" +
        @"(?:(?:you\s+(?:(?:can|could|should|may|will|always)\s+)?)?call\s+me|my\s+name(?:'s|\s+is)|i\s*am\s+called|i'?m\s+called|the\s+name(?:'s|\s+is))\s+" +
        @"([a-z][a-z'\-]*(?:\s+[a-z][a-z'\-]*){0,2})\s*[.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "i'm X" / "i am X" — only trusted when the value survives the not-a-name guard below
    // ("i'm tired" is a state, not a name).
    static readonly Regex IAmRx = new(
        @"^(?:(?:but|and|no|hey|well|actually|so)[,\s]+)*i'?\s*a?m\s+([a-z][a-z'\-]*(?:\s+[a-z][a-z'\-]*)?)\s*[.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Words that make a captured "name" value NOT a name: states/feelings, time words ("call me
    // later"), and common chatter. If the FIRST captured token is one of these, no capture.
    static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // "call me <time/manner>"
        "later","back","tomorrow","tonight","today","again","anytime","sometime","soon","now","asap",
        "when","whenever","after","before","maybe","please","once","twice","daily","early",
        // "i'm <state/feeling/answer>"
        "tired","hungry","good","fine","great","okay","ok","well","bad","sad","happy","angry","bored",
        "busy","done","ready","sorry","sure","confused","lost","here","home","curious","serious",
        "excited","sick","cold","hot","late","stuck","impressed","disappointed","listening","joking",
        "kidding","testing","waiting","leaving","going","trying","asking","wondering","thinking",
        "not","just","still","also","very","really","so","too","quite","the","a","an","your","his","her",
    };

    // "i work at/for X" · "my role/job/title is X" — durable facts worth keeping.
    static readonly Regex WorkRx = new(
        @"^(?:(?:but|and|so|btw|actually)[,\s]+)*i\s+work\s+(?:at|for|in)\s+(.{2,60}?)\s*[.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RoleRx = new(
        @"^(?:(?:but|and|so|btw|actually)[,\s]+)*my\s+(role|job|title|position)\s+is\s+(.{2,60}?)\s*[.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── identity questions, whole-utterance ──
    static readonly Regex WhoAmIRx = new(
        @"^(?:(?:so|and|ok|okay|hey)[,\s]+)*(?:" +
        @"what(?:'s|\s+is)\s+my\s+name|do\s+you\s+(?:know|remember)\s+(?:my\s+name|who\s+i\s+am|me)|" +
        @"who\s+am\s+i|say\s+my\s+name|what\s+do\s+you\s+call\s+me|what\s+am\s+i\s+called" +
        @")\s*[.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex AboutMeRx = new(
        @"^(?:(?:so|and|ok|okay|hey)[,\s]+)*what\s+do\s+you\s+(?:know|remember)\s+about\s+me\s*[.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>If this turn STATES a fact about the user, capture + persist it and return the
    /// acknowledgment to show. Null → not a fact statement.</summary>
    public static string? TryCapture(string? input, ConversationContext? ctx = null)
    {
        var t = Strip(input);
        if (t.Length == 0) return null;

        var m = NameRx.Match(t);
        if (m.Success && IsNameish(m.Groups[1].Value))
        {
            var name = TitleCase(m.Groups[1].Value);
            UserProfile.SetName(name);
            return $"Got it — {name} it is. Nice to meet you properly!";
        }

        m = IAmRx.Match(t);
        if (m.Success && IsNameish(m.Groups[1].Value))
        {
            var name = TitleCase(m.Groups[1].Value);
            UserProfile.SetName(name);
            return $"Nice to meet you, {name} — I'll remember that.";
        }

        m = WorkRx.Match(t);
        if (m.Success)
        {
            var place = m.Groups[1].Value.Trim();
            var fact = $"Works at {place}";
            UserProfile.AddFact(fact);
            ctx?.SessionFacts.Add(fact);
            return $"Noted — you work at {place}.";
        }

        m = RoleRx.Match(t);
        if (m.Success)
        {
            var role = m.Groups[2].Value.Trim();
            var fact = $"Role: {role}";
            UserProfile.AddFact(fact);
            ctx?.SessionFacts.Add(fact);
            return $"Noted — {role}. Good to know.";
        }

        return null;
    }

    /// <summary>If this turn asks about the USER's identity, answer from the profile. Null → not an
    /// identity question. Never touches the notes.</summary>
    public static string? TryIdentity(string? input)
    {
        var t = Strip(input);
        if (t.Length == 0) return null;

        if (WhoAmIRx.IsMatch(t))
        {
            var name = UserProfile.DisplayName;
            if (string.IsNullOrEmpty(name))
                return "I don't know your name yet — tell me with “call me …” and I'll remember it.";
            return UserProfile.NameWasSetByUser
                ? $"You're {name}."
                : $"Going by this PC's account, you're {name} — say “call me …” if you'd rather I use something else.";
        }

        if (AboutMeRx.IsMatch(t))
        {
            var name = UserProfile.DisplayName;
            var facts = UserProfile.Facts;
            if (string.IsNullOrEmpty(name) && facts.Count == 0)
                return "Not much yet — tell me things like “call me …” or “I work at …” and I'll remember them.";
            var bits = new List<string>();
            if (!string.IsNullOrEmpty(name)) bits.Add($"you're {name}");
            bits.AddRange(facts.Select(f => f.StartsWith("Works at", StringComparison.OrdinalIgnoreCase)
                ? char.ToLowerInvariant(f[0]) + f[1..] : f.ToLowerInvariant()));
            return $"Here's what I've got: {string.Join("; ", bits)}. Everything stays on this PC.";
        }

        return null;
    }

    static string Strip(string? input)
    {
        var t = (input ?? "").Trim();
        if (t.Length == 0) return "";
        t = WakeName.StripLeading(t);
        t = WakeName.StripTrailing(t);
        return t.Trim();
    }

    static bool IsNameish(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;
        // The guard is on the FIRST token ("call me later today"); any listed token vetoes.
        foreach (var tok in tokens)
            if (NotNames.Contains(tok)) return false;
        return tokens[0].Length >= 2;
    }

    static string TitleCase(string value) =>
        string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + (w.Length > 1 ? w[1..] : "")));
}
