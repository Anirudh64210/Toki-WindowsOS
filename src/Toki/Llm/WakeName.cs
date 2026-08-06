using System.Text.RegularExpressions;

namespace Toki.Llm;

/// <summary>
/// The ONE place that knows what "Toki" sounds like to the ASR. Push-to-talk goes through Parakeet,
/// and a short made-up name is exactly what a general ASR model mis-hears: the owner's live session
/// produced "Doki", "Toky", "Docie", "Toku", "Tokyo", "Doky" - and every one of those fell straight
/// through the routers (which only knew the literal "toki") into notes retrieval, where the model
/// then hallucinated a grounded-sounding answer. The standard fix in shipping voice assistants is a
/// fuzzy/phonetic alias list for exactly this ("entity synonyms/aliases + phonetic similarity",
/// MindMeld/Webex robust-NLP guidance) - so this class is that closed alias list, shared by
/// <see cref="CommandRouter"/>, <see cref="SmallTalk"/> and <see cref="Persona"/>.
///
/// Scope discipline: the pattern is ONLY ever used to strip a leading/trailing ADDRESS ("hey doki,
/// …" / "…, toky."). It never rewrites the question that goes to retrieval - so a real question
/// about e.g. Tokyo is untouched on the Q&amp;A path.
/// </summary>
public static class WakeName
{
    // t/d onset + o (+a slip) + k/c/ck/kk + i/y/ie/ee/ey/e/u endings covers the observed ASR family:
    // toki, toky, tokie, tokey, tokee, toke, toku, tokki, tocki, doki, doky, dokie, dokey, docie,
    // dockie, doku … plus the two real-word mishearings seen live: "Tokyo" (and "toaki" via the o-a slip).
    public const string Pattern = @"(?:[td]oa?(?:ck|kk|k|c)(?:ie|ee|ey|i|y|e|u)|tokyo)";

    static readonly Regex Leading = new(
        $@"^(?:(?:hey|hi|ok|okay|yo|hello)\s+)?{Pattern}\b[\s,!.:;-]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex Trailing = new(
        $@"[\s,;-]*\b{Pattern}\b[\s.!?,]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Is this single token a plausible ASR rendering of "Toki"?</summary>
    public static bool IsWakeWord(string token) =>
        Regex.IsMatch(token ?? "", $@"^{Pattern}$", RegexOptions.IgnoreCase);

    /// <summary>Strip a leading address ("hey doki, …" → "…"). Leaves anything else alone.</summary>
    public static string StripLeading(string text) => Leading.Replace(text ?? "", "").Trim();

    /// <summary>Strip a trailing address ("i love you, toky." → "i love you"). Leaves anything else alone.</summary>
    public static string StripTrailing(string text) => Trailing.Replace(text ?? "", "").Trim();
}
