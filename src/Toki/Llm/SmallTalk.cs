using System.Text.RegularExpressions;

namespace Toki.Llm;

/// <summary>
/// Tier 0.5: greetings, thanks, identity, and goodbyes get a short CANNED reply and are NEVER
/// sent to notes retrieval. Anchored whole-utterance, so it only fires on PURE small-talk; a real
/// question that merely contains a greeting word falls through to the Q&amp;A.
/// ("What can you do" is handled as the Help COMMAND upstream, not here.)
/// </summary>
public static class SmallTalk
{
    static readonly (Regex Rx, string[] Replies)[] Table =
    {
        (Whole(@"(hi|hey|hello|yo|hiya|heya|howdy|sup)(\s+there)?|what'?s\s+up|good\s+(morning|afternoon|evening|day)|greetings"),
            new[] { "Hey! What do you want to know from your meetings?", "Hi there - ask me anything about your meetings.", "Hey! Ask away." }),
        (Whole(@"thanks?(\s+(a\s+lot|so\s+much|very\s+much|a\s+ton|man|toki))?|thank\s+you(\s+(so\s+much|very\s+much|a\s+lot|a\s+ton))?|thx|ty|appreciate\s+(it|that|you)|cheers|nice|cool|great|awesome|perfect|good\s+job|well\s+done|got\s+it"),
            new[] { "Anytime.", "You got it.", "Happy to help." }),
        // Acknowledgements + bare backchannels ("okay", "ok cool", "sounds good", "yeah", "yep",
        // "mm-hmm"). Whole-utterance only; otherwise they fall through to retrieval and dig up an
        // unrelated meeting. The bare affirmations/negations matter DOUBLY here: a mis-heard short
        // utterance (Parakeet loves to emit "yeah"/"okay" on a garbled 1-word clip) must get a light
        // canned reply, NEVER a confident answer grounded on whatever note is on screen.
        (Whole(@"(yeah|yep|yup|yea|ya|mm+-?hmm+|uh-?huh|mhm+|ok(ay)?|alright(y)?|k+|cool|nice|sweet|great|awesome|perfect|gotcha|right|sure|word|fair\s+enough|sounds\s+good|makes\s+sense|no\s+worries)(?:[\s,]+(yeah|yep|ok(ay)?|cool|nice|sweet|great|awesome|perfect|thanks?|thank\s+you|then|man|got\s+it|makes\s+sense|no\s+worries|sounds\s+good|for\s+now))*"),
            new[] { "Cool.", "Sounds good.", "Anytime.", "You got it." }),
        // Bare yes/no/nah/nope on their own (outside a pending calendar confirm, which is handled
        // upstream): not a question, so acknowledge lightly rather than answering from the notes.
        (Whole(@"yes|no|nah|nope|not\s+really|never\s*mind"),
            new[] { "Okay!", "No worries.", "Got it." }),
        // A dismissal that clarifies a prior throwaway ("no I just meant ok cool thanks") - ONLY when the
        // tail is pure acknowledgement, never a meeting reference ("I meant the pricing meeting" is Q&A).
        (Whole(@"(no,?\s+)?i\s+(just\s+)?(meant|mean|said)\s+(that\s+|to\s+say\s+)?(?:(ok(ay)?|cool|nice|sweet|great|thanks?|thank\s+you|good|nothing|no\s+worries|then|man)[\s,]*)+"),
            new[] { "No worries!", "All good.", "Gotcha." }),
        (Whole(@"how\s+(are|r)\s+(you|u|ya)|how'?s\s+it\s+going|how\s+are\s+things|you\s+(good|okay|ok)|how\s+do\s+you\s+do|what'?s\s+good"),
            new[] { "Doing great - ready to dig through your meetings. What's up?", "All good! What do you need from your notes?" }),
        (Whole(@"(who|what)\s+are\s+you|what'?s\s+your\s+name|introduce\s+yourself|what\s+is\s+toki|tell\s+me\s+about\s+yourself"),
            new[] { "I'm Toki - your on-device meeting assistant. I answer from your own meetings, and everything stays on this PC." }),
        (Whole(@"(good\s*)?bye|goodbye|see\s+(ya|you)(\s+later)?|later|good\s*night|that'?s\s+(all|it)|nothing|forget\s+it"),
            new[] { "See you.", "Anytime - I'm here.", "Okay!" }),
        (Whole(@"are\s+you\s+there|you\s+there|are\s+you\s+listening|can\s+you\s+hear\s+me"),
            new[] { "I'm here - what do you need?" }),
        // Affection gets a warm canned reply and never touches the notes.
        (Whole(@"i\s+(really\s+|so\s+)?(love|luv|adore|like|miss)\s+(you|u|ya)(\s+(so\s+much|too|a\s+lot|man|buddy))?|love\s+(you|u|ya)|(you('re|\s+are)|ur)\s+(the\s+best|my\s+(favorite|favourite)|awesome|amazing|adorable|cute|sweet|lovely|wonderful|so\s+(cute|sweet|helpful))|best\s+(assistant|app)\s+ever|i\s+appreciate\s+you|will\s+you\s+marry\s+me|marry\s+me"),
            new[] { "Aw - right back at you. Now, what can I dig up from your meetings?", "That's sweet of you. I'm here whenever you need your meetings." }),
    };

    static Regex Whole(string body) => new($@"^(?:{body})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Random.Shared: TryReply runs on the threadpool from BOTH ask surfaces (notch + Library)
    // concurrently, and a shared System.Random instance corrupts under concurrent Next().

    /// <summary>A short canned reply for pure small-talk, or null to fall through to the notes Q&amp;A.</summary>
    public static string? TryReply(string? input)
    {
        var t0 = (input ?? "").Trim();
        if (t0.Length == 0) return null;
        var t = WakeName.StripLeading(t0);
        // Try the patterns both as-is (keeps "what is toki") and with a trailing address stripped
        // ("good morning, toki"). The original text is never mutated for the Q&A path.
        foreach (var cand in new[] { t, WakeName.StripTrailing(t) })
        {
            var c = cand.Trim('.', '!', '?', ',', ' ');
            if (c.Length == 0) return "I'm here - what do you need?";   // a bare "toki"
            foreach (var (rx, replies) in Table)
                if (rx.IsMatch(c)) return replies[Random.Shared.Next(replies.Length)];
        }
        return null;
    }
}
