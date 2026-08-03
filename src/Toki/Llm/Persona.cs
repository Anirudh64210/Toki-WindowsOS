using System.Text.RegularExpressions;

namespace Toki.Llm;

/// <summary>
/// Tier 0.6: questions ABOUT TOKI ITSELF — its mood ("why are you so rude?"), its abilities, whether
/// it can see the screen, privacy, complaints, apologies — get a short, warm, in-character CANNED
/// reply and are NEVER sent to notes retrieval. Without this tier, "why are you so rude?" was
/// literally searched against the meeting notes and answered "there's no mention of rudeness in the
/// meeting notes" (real screenshot). The shipping-assistant pattern (Dialogflow "long tail" design,
/// Copilot Studio out-of-scope guidance): identify the likely out-of-domain intents up front and give
/// each a prepared, personable answer instead of a retrieval dead-end.
///
/// Anchored whole-utterance, like <see cref="SmallTalk"/>, so a real notes question that merely
/// contains "you" ("what did you note down about pricing?" — arguably about notes) falls through.
/// Deterministic, ~0ms, no model.
/// </summary>
public static class Persona
{
    // Each entry: whole-utterance pattern → reply. {screen} is replaced with the screen-aware line.
    static readonly (Regex Rx, string Reply)[] Table =
    {
        // ── mood / rudeness complaints ("why are you so rude?" — the screenshot bug) ──
        (Whole(@"why\s+(are|r)\s+(you|u)\s+(so\s+|being\s+so\s+|being\s+)?(rude|mean|grumpy|cold|short|snappy|angry|harsh|blunt|sassy)(\s+to\s+me)?|(you('re|\s+are)|ur)\s+(so\s+|being\s+|really\s+)?(rude|mean|grumpy|cold|harsh|blunt)(\s+to\s+me)?|are\s+you\s+(mad|angry|annoyed|upset)(\s+(at|with)\s+me)?"),
            "Sorry if I came across short — never my intent. Ask me again and I'll do better."),

        // ── quality complaints / "you're dumb" / wrong answer feedback ──
        (Whole(@"why\s+(are|r)\s+(you|u)\s+(so\s+)?(dumb|stupid|bad|useless|slow|terrible|awful|wrong)|(you('re|\s+are)|ur)\s+(so\s+|really\s+)?(dumb|stupid|bad|useless|terrible|awful|wrong|not\s+(very\s+)?smart|not\s+helping)|that('s|\s+is|\s+was)\s+(wrong|not\s+right|incorrect|not\s+what\s+i\s+(asked|meant|wanted))|you\s+(got|have)\s+(it|that)\s+wrong|(that\s+)?(answer\s+)?(was|is)\s+(so\s+)?(bad|wrong|useless)|wrong\s+answer|try\s+again"),
            "Sorry about that — I clearly missed. Rephrase it, or name the meeting or person, and I'll take another shot."),

        // ── screen awareness ("do you see what's on my screen right now?" — the screenshot bug) ──
        (Whole(@"(do|can|could)\s+(you|u)\s+(see|tell|know|read)\s+(what('s|\s+is)\s+)?(on\s+)?(my\s+)?screen(\s+right\s+now)?|(do|can)\s+(you|u)\s+(see|know)\s+what\s+i('m|\s+am)\s+(looking\s+at|reading|viewing|doing)(\s+right\s+now)?|what('s|\s+is)\s+on\s+my\s+screen(\s+right\s+now)?|what\s+am\s+i\s+looking\s+at(\s+right\s+now)?|(do|can)\s+(you|u)\s+watch\s+my\s+screen"),
            "{screen}"),

        // ── capabilities / how it works (beyond the Help command) ──
        (Whole(@"how\s+do\s+(you|u)\s+work|how\s+smart\s+(are|r)\s+(you|u)|what\s+else\s+can\s+(you|u)\s+do|what\s+(do|can)\s+(you|u)\s+(know|remember)|how\s+do\s+(you|u)\s+know\s+(all\s+)?(this|that|things)"),
            "I listen to your meetings when you say yes, take the notes, and answer questions from them — all on this PC. Ask me about any meeting, task, or decision."),

        // ── privacy / data ──
        (Whole(@"(is|are)\s+(my\s+)?(data|notes?|meetings?|recordings?)\s+(private|safe|secure)|do\s+(you|u)\s+(send|upload|share|sell)\s+(my\s+)?(data|notes?|meetings?|recordings?)(\s+(anywhere|to\s+anyone|online|to\s+the\s+cloud))?|where\s+(is|are|do)\s+(my\s+)?(data|notes?|meetings?)\s*(stored|saved|kept|live|go)?|(is\s+this|are\s+you)\s+(all\s+)?(local|on.?device|offline)"),
            "Everything stays on this PC — your meetings live in a local database and nothing is uploaded anywhere."),

        // ── identity edge cases (SmallTalk owns "who are you"; these are the AI-flavoured ones) ──
        (Whole(@"are\s+(you|u)\s+(an?\s+)?(ai|robot|bot|human|real|person|chat\s*gpt|chatgpt|gpt|alexa|siri)( or .+)?|(are|r)\s+(you|u)\s+alive|do\s+(you|u)\s+have\s+feelings"),
            "I'm Toki — a little on-device assistant, not a person. But I was in your meetings, so ask away."),

        // ── user apologises / checks in on Toki ──
        (Whole(@"(i('m|\s+am)\s+)?sorry(\s+(about\s+that|toki))?|my\s+bad|apologies"),
            "No worries at all. What do you need?"),
    };

    static Regex Whole(string body) => new($@"^(?:{body})[\s.!?]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A short in-character reply for a question about Toki itself, or null to fall through to
    /// the notes Q&amp;A. <paramref name="screenTitle"/> = the title of the note visibly open right now
    /// (null if none) — so "do you see my screen?" can answer concretely and honestly.</summary>
    public static string? TryReply(string? input, string? screenTitle = null)
    {
        var t = (input ?? "").Trim();
        if (t.Length == 0) return null;
        // Strip a leading/trailing address, same as SmallTalk — via the shared FUZZY wake-name
        // (WakeName), so "why are you so rude, Doky?" is still a persona intercept, not retrieval.
        t = WakeName.StripLeading(t);
        t = WakeName.StripTrailing(t);
        if (t.Length == 0) return null;

        foreach (var (rx, reply) in Table)
            if (rx.IsMatch(t))
                return reply == "{screen}" ? ScreenReply(screenTitle) : reply;
        return null;
    }

    static string ScreenReply(string? screenTitle) =>
        screenTitle is { Length: > 0 }
            ? $"I can see you have “{screenTitle}” open — ask me anything about it."
            : "I don't watch your screen — I only know which note you have open in Toki, and nothing's open right now.";
}
