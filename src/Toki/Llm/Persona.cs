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

        // ── capabilities / how it works (beyond the Help command, which owns the exact "what can you
        //    do") — includes "what do you do", "what can you do for me", "what are you for" ──
        (Whole(@"how\s+do\s+(you|u)\s+work|how\s+smart\s+(are|r)\s+(you|u)|what\s+else\s+can\s+(you|u)\s+do|what\s+(do|can)\s+(you|u)\s+(know|remember)|how\s+do\s+(you|u)\s+know\s+(all\s+)?(this|that|things)|what\s+do\s+(you|u)\s+(actually\s+)?do|what\s+can\s+(you|u)\s+do\s+for\s+me|what\s+(are|r)\s+(you|u)\s+(for|good\s+for)|what('s|\s+is)\s+toki\s+for"),
            "I take the notes in the meetings you okay, and answer questions from them — all on this PC. Ask about any meeting, task, or decision, or just chat."),

        // ── privacy / data handling — MUST be complete and accurate (a soft or wrong answer here is a
        //    trust breach). Broad on purpose: any question about where data goes, cloud/upload, storage,
        //    training, or "is this private/safe/local" gets the same honest, full answer. ──
        (Whole(@".*(?:\b("
            + @"send(ing)?|upload(ing|ed)?|shar(e|ing)|sell(ing)?|leak|leav(e|es|ing)\s+(my|this|the)|go(es|ing)?\s+(to|anywhere)|"
            + @"stor(e|ing|ed|age)|sav(e|ing|ed)|keep(ing)?|kept|"
            + @"cloud|server|online|internet|external|third.?part(y|ies)|"
            + @"train(ing|ed)?\s+(on|your|my|the)|used?\s+(my|your|the)\s+data|"
            + @"private|secure|safe|confidential|encrypt|"
            + @"local(ly)?|on.?device|offline|"
            + @"who\s+(can|could)\s+(see|access|read|view)|can\s+(my|anyone|someone|my\s+boss)\s+(see|access|read)"
            + @")\b.*\b(data|notes?|meetings?|recordings?|audio|transcript?s?|conversations?|info|information|it|this|everything)\b"
            + @"|\b(is|are)\s+(this|it|toki|my\s+(data|notes?|meetings?|recordings?|stuff|audio)|my\s+stuff|everything)\s+(private|secure|safe|local|on.?device|offline|encrypted|confidential)\b"
            + @"|\bwhere\s+(is|are|does?|do)\s+(my\s+|the\s+|this\s+|all\s+(my|this)\s+)?(data|notes?|meetings?|recordings?|audio|stuff|everything|it)\b"
            + @"|\b(train(ing|ed)?|used?)\b.*\b(my|your)\s+(data|meetings?|notes?|recordings?|audio)\b"
            + @"|\b(my|your)\s+(data|meetings?|notes?|recordings?|audio)\b.*\b(train(ing|ed)?|uploaded?|sent|shared|sold)\b"
            + @"|\bdo(es)?\s+(you|toki|this|it)\s+(phone\s+home|call\s+home)\b"
            + @").*"),
            "Everything stays on this PC. Your meetings, notes and audio live in a local database on this "
            + "machine and nothing is ever uploaded — no cloud, no servers, no training on your data. The one "
            + "exception is if YOU add your own API key in Settings; only then does the text you ask about go to "
            + "that provider, and that's your choice."),

        // ── always-listening / microphone — the other half of the privacy story, also must be accurate ──
        (Whole(@"(?:.*\b(always|constantly|all\s+the\s+time|24.?7|secretly)\s+(listen|listening|record|recording|on)\b"
            + @"|\b(are|r)\s+(you|u)\s+(always\s+)?(listen(ing)?|record(ing)?)\b"
            + @"|\bdo\s+(you|u)\s+(always\s+)?(listen|record)\b"
            + @"|\bis\s+(the\s+|my\s+)?(mic|microphone)\s+(always\s+)?(on|listening|recording|live)\b"
            + @"|\bcan\s+(you|u)\s+hear\s+me\s+(all\s+the\s+time|always|right\s+now\s+even|when)\b).*"),
            "No — there's no always-on listening and no wake word. The mic only runs while you're recording a "
            + "meeting you chose to record, or while you hold the push-to-talk key. The rest of the time it's off."),

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
