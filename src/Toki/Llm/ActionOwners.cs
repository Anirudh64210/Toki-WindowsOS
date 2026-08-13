using System.Text.RegularExpressions;
using Toki.Storage;
using Toki.Transcription;

namespace Toki.Llm;

/// <summary>
/// Works out who owns a to-do, once the speakers have been told apart. No model call.
///
/// The insight that makes this free: every to-do already has an **evidence link** - the transcript line
/// it came from, computed by <see cref="Evidence"/>. The moment that line knows who spoke it, the owner
/// of a to-do somebody volunteered for is sitting right there. No second LLM pass, no extra wait, and it
/// is deterministic, which matters for something that puts a person's name against a commitment.
///
/// **The rule that keeps it honest: only a FIRST-PERSON commitment names its speaker.**
///
///     "I'll send the deck by Friday"      said by Devi  ->  owner: Devi     (they volunteered)
///     "Craig will send the deck"          said by Devi  ->  owner: nobody   (Devi is reporting)
///
/// Getting that backwards would attribute other people's work to whoever happened to mention it, which is
/// worse than an unowned to-do - the same asymmetry that governs the name corrections and the evidence
/// links themselves. When the line is not a volunteer, this leaves the owner blank and says nothing.
/// </summary>
public static class ActionOwners
{
    // "I'll", "I will", "I can", "I'm going to", "let me", "I'll take", "I've got", "I would".
    // Anchored loosely: the commitment usually opens the sentence but can follow "okay, so".
    static readonly Regex FirstPerson = new(
        @"(?i)\b(?:I'?ll|I will|I can|I could|I'?m going to|I am going to|I'?ve got|I'?d|let me|I shall|on it|I'?ll take|leave it with me)\b",
        RegexOptions.CultureInvariant);

    // Someone ELSE is named as the doer, so the speaker is reporting rather than volunteering.
    static readonly Regex ThirdPerson = new(
        @"(?i)\b(?:he|she|they|you)\s+(?:will|can|should|is going to|are going to|needs? to)\b",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Fill in missing owners from the transcript. Returns the to-dos that CHANGED, by index, so the
    /// caller can write just those - an untouched to-do must not be rewritten, because the user may have
    /// edited it in the meantime.
    /// </summary>
    public static IReadOnlyList<(int Index, string Owner)> Infer(
        IReadOnlyList<ActionItem> actions, IReadOnlyList<TranscriptSegment> transcript)
    {
        var found = new List<(int, string)>();
        if (actions.Count == 0 || transcript.Count == 0) return found;

        for (var i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            if (!string.IsNullOrWhiteSpace(a.Owner)) continue;         // the model already answered

            var idx = Evidence.BestSegment(a.Text, transcript);
            if (idx is not int line) continue;                          // no confident source line

            var seg = transcript[line];
            var text = seg.Text ?? "";
            if (!FirstPerson.IsMatch(text)) continue;                   // not a volunteer
            if (ThirdPerson.IsMatch(text)) continue;                    // naming somebody else

            // "Them" is not an answer. A tag ("Devi", "Speaker 2") or "You" is.
            if (seg.Tag is null && seg.Speaker == Speaker.Them) continue;

            found.Add((i, seg.Label));
        }
        return found;
    }
}
