using System.Text;
using System.Text.RegularExpressions;

namespace Toki.Llm;

/// <summary>
/// The GENERAL conversation path of the dialogue brain: turns that aren't about the meetings
/// ("what's a mutex?", "I'm stressed about tomorrow", "help me phrase this email") are answered by
/// the model from its OWN knowledge — warm, brief, honest — with the recent conversation and the
/// user profile in context. NO retrieval, NO citations: forcing these turns through notes search is
/// exactly what made Toki feel like a first-gen chatbot (deny-or-fabricate).
///
/// Works identically over both backends (local Qwen3-4B or the BYO API model): the prompt is plain
/// system+user text, and the multi-turn memory is carried as a short transcript inside the user
/// prompt — the pattern that works with a single-exchange chat-template call.
/// </summary>
public static class GeneralChat
{
    const int MaxHistoryTurns = 6;      // last N turns carried into the prompt
    const int MaxTurnChars = 300;       // each carried turn trimmed to this

    /// <summary>System prompt: persona + profile + honesty rules. Small instruct models follow an
    /// exemplar better than abstract rules, so it ends with one Q→A pair in the target voice.</summary>
    public static string BuildSystem(bool concise)
    {
        var sb = new StringBuilder();
        sb.Append("You are Toki, a small, friendly AI assistant that lives on the user's PC and takes their meeting notes. ")
          .Append("Right now the user is just chatting or asking a general question — not asking about their meeting notes — ")
          .Append("so answer from your own knowledge, like a warm, sharp colleague.\n");
        sb.Append(concise
            ? "Keep it to 1–2 short sentences — your answer appears in a tiny panel read at a glance.\n"
            : "Keep it brief — 1–3 short sentences unless they clearly want more. Use contractions and plain words.\n");
        sb.Append("You can reason, explain, do quick math, and give a considered opinion. ")
          .Append("Be honest: if you're not sure, say so plainly — never invent specifics. ")
          .Append("You're an on-device assistant and everything stays on their PC. ")
          .Append("If they ask who you are or what you do, say it plainly: you're Toki, their meeting assistant — ")
          .Append("you take notes in the meetings they okay and answer questions from them, all on this PC, ")
          .Append("and you're happy to chat about anything else. ")
          .Append("If they ask about their meetings or tasks, tell them to name the meeting, person, or topic and you'll check their notes.\n");

        var name = UserProfile.DisplayName;
        if (!string.IsNullOrEmpty(name)) sb.Append("The user's name is ").Append(name).Append(". ");
        var facts = UserProfile.Facts;
        if (facts.Count > 0) sb.Append("About them: ").Append(string.Join("; ", facts)).Append(". ");
        sb.Append('\n');

        sb.Append("Plain text only — NO emojis, NO markdown, NO speaker labels. Do NOT prefix your reply with your name. ")
          .Append("Do NOT state today's date unless they ask for it. ")
          .Append("Never write \"As an AI\", \"Great question\", or \"Based on\". Never ask more than one question back. Respond in English only.\n");
        sb.Append("Example:\nQ: what's a good name for a puppy?\nA: Depends on the pup! For something classic go Biscuit or Luna; for chaos energy, Turbo.");
        return sb.ToString();
    }

    /// <summary>User prompt: today's date, the recent conversation (so follow-ups work), then the turn.</summary>
    public static string BuildUser(string question, ConversationContext? ctx)
    {
        var sb = new StringBuilder();
        sb.Append("Today is ").Append(DateTimeOffset.Now.ToString("dddd, MMMM d, yyyy")).Append('.').AppendLine();
        if (ctx?.SessionFacts is { Count: > 0 } sf)
            sb.Append("The user told you this session: ").Append(string.Join("; ", sf)).Append('.').AppendLine();
        if (ctx?.Turns is { Count: > 0 } turns)
        {
            sb.AppendLine().AppendLine("Recent conversation:");
            foreach (var (role, text) in turns.TakeLast(MaxHistoryTurns))
            {
                var t = (text ?? "").Trim();
                if (t.Length == 0) continue;
                if (t.Length > MaxTurnChars) t = t[..MaxTurnChars].TrimEnd() + "…";
                sb.Append(role == "user" ? "User: " : "Toki: ").AppendLine(t);
            }
        }
        sb.AppendLine().Append("User: ").AppendLine(question.Trim());
        sb.Append("Reply:");   // NOT "Reply as Toki:" — that made the model prepend a literal "Toki:" label
        return sb.ToString();
    }

    /// <summary>Token budget for a general turn: chat is short by design.</summary>
    public static int MaxTokens(bool concise) => concise ? 120 : 260;

    // A leading speaker label the small model sometimes prepends ("Toki: …", "Assistant: …").
    static readonly Regex LeadLabel = new(@"^\s*(toki|assistant|ai|bot|reply)\s*[:\-]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Emoji / pictographs: astral-plane surrogate pairs (🚀 etc.) + the common BMP symbol blocks + the
    // variation selector and ZWJ used to compose them. The notch/answer voice is plain text (BuildSystem
    // bans these; this enforces it in code, since a small model still slips one in — CLAUDE.md lesson 8).
    static readonly Regex Emoji = new(
        @"[\uD800-\uDBFF][\uDC00-\uDFFF]|[←-⇿⌀-➿☀-⛿⬀-⯿️‍]",
        RegexOptions.Compiled);

    /// <summary>Strip artifacts a small model slips into a reply — a leading speaker label ("Toki:") and
    /// any emojis — so the plain-text voice is enforced in code, not just asked for in the prompt.</summary>
    public static string Sanitize(string text)
    {
        var t = (text ?? "").Trim();
        t = LeadLabel.Replace(t, "").Trim();
        t = Emoji.Replace(t, "");
        t = Regex.Replace(t, @"[ \t]{2,}", " ");   // tidy any double space a removed emoji left behind
        return t.Trim();
    }
}
