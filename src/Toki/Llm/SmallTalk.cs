using System.Text.RegularExpressions;

namespace Toki.Llm;

/// <summary>
/// Tier 0.5: greetings, thanks, "how are you", identity, and goodbyes are answered with a short, warm,
/// CANNED reply and NEVER sent to notes retrieval. This is what stops "Good morning" from returning a
/// rambling meeting summary, and "who are you" from searching the notes — a meeting assistant shouldn't
/// pretend everything is a document question. Anchored whole-utterance, so it only fires on PURE
/// small-talk; a real question that merely contains a greeting word falls through to the Q&amp;A.
/// (Capability — "what can you do" — is handled as the Help COMMAND upstream, not here.)
/// </summary>
public static class SmallTalk
{
    static readonly (Regex Rx, string[] Replies)[] Table =
    {
        (Whole(@"(hi|hey|hello|yo|hiya|heya|howdy|sup)(\s+there)?|what'?s\s+up|good\s+(morning|afternoon|evening|day)|greetings"),
            new[] { "Hey! What do you want to know from your meetings?", "Hi there — ask me anything about your meetings.", "Hey! Ask away." }),
        (Whole(@"thanks?(\s+(a\s+lot|so\s+much|very\s+much|a\s+ton|man|toki))?|thank\s+you(\s+(so\s+much|very\s+much|a\s+lot|a\s+ton))?|thx|ty|appreciate\s+(it|that|you)|cheers|nice|cool|great|awesome|perfect|good\s+job|well\s+done|got\s+it"),
            new[] { "Anytime.", "You got it.", "Happy to help." }),
        (Whole(@"how\s+(are|r)\s+(you|u|ya)|how'?s\s+it\s+going|how\s+are\s+things|you\s+(good|okay|ok)|how\s+do\s+you\s+do|what'?s\s+good"),
            new[] { "Doing great — ready to dig through your meetings. What's up?", "All good! What do you need from your notes?" }),
        (Whole(@"(who|what)\s+are\s+you|what'?s\s+your\s+name|introduce\s+yourself|what\s+is\s+toki|tell\s+me\s+about\s+yourself"),
            new[] { "I'm Toki — your on-device meeting assistant. I answer from your own meetings, and everything stays on this PC." }),
        (Whole(@"(good\s*)?bye|goodbye|see\s+(ya|you)(\s+later)?|later|good\s*night|that'?s\s+(all|it)|nothing|forget\s+it"),
            new[] { "See you.", "Anytime — I'm here.", "Okay!" }),
        (Whole(@"are\s+you\s+there|you\s+there|are\s+you\s+listening|can\s+you\s+hear\s+me"),
            new[] { "I'm here — what do you need?" }),
        // ── affection ("I love you, Toki" — a real live utterance). Without this tier it fell through
        //    to notes retrieval and the model INVENTED a grounded-sounding ramble — the worst failure
        //    class. Affection gets a warm canned reply and never touches the notes. ──
        (Whole(@"i\s+(really\s+|so\s+)?(love|luv|adore|like|miss)\s+(you|u|ya)(\s+(so\s+much|too|a\s+lot|man|buddy))?|love\s+(you|u|ya)|(you('re|\s+are)|ur)\s+(the\s+best|my\s+(favorite|favourite)|awesome|amazing|adorable|cute|sweet|lovely|wonderful|so\s+(cute|sweet|helpful))|best\s+(assistant|app)\s+ever|i\s+appreciate\s+you|will\s+you\s+marry\s+me|marry\s+me"),
            new[] { "Aw — right back at you. Now, what can I dig up from your meetings?", "That's sweet of you. I'm here whenever you need your meetings." }),
    };

    static Regex Whole(string body) => new($@"^(?:{body})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Random Rng = new();

    /// <summary>A short canned reply for pure small-talk, or null to fall through to the notes Q&amp;A.</summary>
    public static string? TryReply(string? input)
    {
        var t0 = (input ?? "").Trim();
        if (t0.Length == 0) return null;
        // Strip a LEADING "(hey) toki" address ("toki, hi" / "hey doki, thanks") — via the shared
        // FUZZY wake-name (WakeName), because push-to-talk ASR renders "Toki" as Doki/Toky/Docie/….
        var t = WakeName.StripLeading(t0);
        // Try the whole-utterance patterns BOTH as-is (keeps "what is toki") and with a trailing
        // address stripped (keeps "good morning, toki" AND "I love you, Toky") — so the wake-name as
        // a subject vs. an address both work. The original text is never mutated for the Q&A path.
        foreach (var cand in new[] { t, WakeName.StripTrailing(t) })
        {
            var c = cand.Trim('.', '!', '?', ',', ' ');
            if (c.Length == 0) return "I'm here — what do you need?";   // a bare "toki"
            foreach (var (rx, replies) in Table)
                if (rx.IsMatch(c)) return replies[Rng.Next(replies.Length)];
        }
        return null;
    }
}
