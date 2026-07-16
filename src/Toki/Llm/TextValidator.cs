using System.Text.RegularExpressions;

namespace Toki.Llm;

/// <summary>
/// Guards against the ways a 4-bit local model degenerates (lesson 8): drifting
/// into CJK/kana/Hangul, or collapsing into repeated characters like "!!!!!!".
/// Never show the user degenerate output — reject, retry once, then fall back to
/// the heuristic notes.
/// </summary>
public static partial class TextValidator
{
    public static bool IsDegenerate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (HasNonLatinScript(text)) return true;
        if (RepeatRun().IsMatch(text)) return true;   // 6+ of the same char in a row
        return false;
    }

    /// <summary>True if the text contains CJK, kana, or Hangul — Qwen drifting off English.</summary>
    public static bool HasNonLatinScript(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= (char)0x3040 and <= (char)0x30FF) return true; // hiragana/katakana
            if (ch is >= (char)0x3400 and <= (char)0x9FFF) return true; // CJK ideographs
            if (ch is >= (char)0xAC00 and <= (char)0xD7A3) return true; // hangul syllables
            if (ch is >= (char)0xF900 and <= (char)0xFAFF) return true; // CJK compat
        }
        return false;
    }

    [GeneratedRegex(@"(.)\1{5,}")]
    private static partial Regex RepeatRun();
}
