using System.Text.RegularExpressions;
using Toki.Transcription;

namespace Toki.Llm;

/// <summary>
/// The transcript's veto over the model's template choice.
///
/// WHY THIS EXISTS. A specialised template is not a layout, it is a CLAIM about the meeting.
/// "Decision log" asserts that a decision was made, and then asks the model to fill in
/// "Options considered", "Decision" and "Rationale". Both of the first real meetings were
/// classified "Decision log" and neither made a decision, so Rationale came out as invented
/// justification for a decision that never happened. The note did not look wrong - it looked
/// authoritative, which is worse.
///
/// The model proposes; the transcript disposes. Every non-General template must clear a
/// deterministic evidence gate computed from the raw transcript. Failing the gate is not an
/// error and is never surfaced: it falls back to General ("Key points"), which asserts nothing
/// about the meeting and therefore cannot be wrong about it.
///
/// The gates are deliberately loose about what counts as evidence and strict about needing SOME.
/// A missed specialisation costs a slightly plainer note. A false one puts words in people's
/// mouths - the same asymmetry that sets <see cref="NameCorrector"/>'s thresholds.
/// </summary>
public static class TemplateGate
{
    /// <summary>Return the model's pick if the transcript supports it, else General.</summary>
    public static NoteTemplate Verify(NoteTemplate picked, IReadOnlyList<TranscriptSegment> transcript)
        => HasEvidence(picked.Name, transcript) ? picked : NoteTemplates.General;

    /// <summary>Does this transcript contain what the named template claims about it?</summary>
    public static bool HasEvidence(string? templateName, IReadOnlyList<TranscriptSegment> transcript)
    {
        // General claims nothing, so nothing can contradict it. Same for a name we don't gate.
        if (string.IsNullOrWhiteSpace(templateName)) return true;
        if (transcript is null || transcript.Count == 0) return false;

        var text = " " + string.Join(" ", transcript.Select(s => s.Text ?? "")).ToLowerInvariant() + " ";

        return templateName.Trim().ToLowerInvariant() switch
        {
            "general" => true,
            "decision log" => Count(text, DecisionStrong) >= 1 || Count(text, DecisionWeak) >= 2,
            "interview" => Count(text, Interview) >= 2,
            "sales call" => Count(text, Sales) >= 2,
            "standup" => Count(text, Standup) >= 2,
            "brainstorm" => Count(text, Brainstorm) >= 2,
            "1:1" => Voices(transcript) == 2,
            "lecture" => Dominance(transcript) >= 0.80 && Count(text, Lecture) >= 1,
            _ => true,
        };
    }

    // ── the cues ──
    //
    // Each is written as one alternation so a single Regex pass answers "how many DIFFERENT kinds of
    // evidence are here". Counting distinct alternatives rather than total matches matters: someone
    // saying "let's go with" four times about four different lunch orders is one kind of evidence, not
    // four, and it should not on its own promote a meeting to a decision log.

    static readonly Regex DecisionStrong = Make(
        @"we(?:'ve| have)? decided|decision is|the decision|we decided|it(?:'s| is) decided|"
        + @"we(?:'re| are) going with|we(?:'ll| will) go with|we(?:'ve| have) agreed|"
        + @"final(?:ly)? (?:decided|agreed|call)|sign(?:ed)? off on|that(?:'s| is) (?:the )?decision");

    static readonly Regex DecisionWeak = Make(
        @"let(?:'s| us) go with|settled on|we agreed|agreed to|going to go with|"
        + @"approved|the call (?:is|was)|option (?:a|b|one|two)|either way we|"
        + @"pros and cons|weigh(?:ing)? (?:up )?the options");

    static readonly Regex Interview = Make(
        @"tell me about yourself|walk me through your|your (?:resume|cv|background|experience)|"
        + @"years of experience|why (?:do you want|are you interested)|the candidate|this role|"
        + @"do you have any questions for (?:me|us)|previous (?:role|job|company)|"
        + @"what (?:attracted|drew) you");

    static readonly Regex Sales = Make(
        @"pricing|price point|the quote|our contract|the contract|budget for|proposal|"
        + @"discount|free trial|per (?:seat|user|month)|renewal|"
        + @"procurement|purchase order|sign(?:ing)? (?:the|a) deal|onboarding fee");

    static readonly Regex Standup = Make(
        @"\bblocked\b|\bblockers?\b|yesterday i|today i(?:'m| am| will)?|what i(?:'m| am) working on|"
        + @"standup|stand-up|no updates? from|my update|over to you|round the room|"
        + @"i(?:'ll| will) pick (?:that|it) up");

    static readonly Regex Brainstorm = Make(
        @"brainstorm|what if we|we could (?:try|do|build)|another idea|throwing (?:it |this )?out there|"
        + @"just spitballing|off the top of my head|crazy idea|park(?:ing)? that idea|"
        + @"build(?:ing)? on that|any other ideas");

    static readonly Regex Lecture = Make(
        @"today(?:'s)? (?:lecture|class|session|topic)|as you can see (?:on|in) the|"
        + @"the next slide|any questions (?:so far|at the end)|"
        + @"this is (?:defined|called)|by definition|for (?:your )?homework|the reading|"
        + @"let me explain|to summarise this section|to summarize this section");

    static Regex Make(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>How many DIFFERENT cues in this family the transcript contains.</summary>
    static int Count(string text, Regex cues) =>
        cues.Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct().Count();

    /// <summary>Distinct people, as far as Toki can honestly tell (stream, or diarised tag).</summary>
    static int Voices(IReadOnlyList<TranscriptSegment> transcript) =>
        transcript.Select(s => s.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>Share of the spoken words that came from the single most talkative person.</summary>
    static double Dominance(IReadOnlyList<TranscriptSegment> transcript)
    {
        var byVoice = transcript
            .GroupBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => (double)g.Sum(s => Words(s.Text)))
            .ToList();
        var total = byVoice.Sum();
        return total <= 0 ? 0 : byVoice.Max() / total;
    }

    static int Words(string? s) =>
        string.IsNullOrWhiteSpace(s) ? 0 : s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
