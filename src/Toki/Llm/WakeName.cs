using System.Text.RegularExpressions;

namespace Toki.Llm;

/// <summary>
/// The one closed phonetic alias list for ASR mishearings of "Toki" (doki, toky, tokyo, ...),
/// shared by <see cref="CommandRouter"/>, <see cref="SmallTalk"/> and <see cref="Persona"/>.
/// The pattern is ONLY ever used to strip a leading/trailing ADDRESS ("hey doki,
/// …" / "…, toky."). It never rewrites the question that goes to retrieval - so a real question
/// about e.g. Tokyo is untouched on the Q&amp;A path.
/// </summary>
public static class WakeName
{
    // t/d onset + o (+a slip) + k/c/ck/kk + i/y/ie/ee/ey/e/u endings covers the observed ASR
    // mishearing family (toki, doky, docie, toku, ...) plus the real-word "tokyo".
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
