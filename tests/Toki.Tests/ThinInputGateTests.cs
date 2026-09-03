using System.Collections.Generic;
using System.Linq;
using Toki.Llm;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// MUTATION PROBE (meeting 70, 2026-08-05): the whole transcript was one 90-word utterance in which
/// somebody said "I propose we do a sort of two-month rotation". The note asserted three "Options
/// considered" (two of them never said), a firm "Decision" that "begins immediately", and a
/// three-bullet "Rationale". Nobody decided anything and nobody else spoke.
///
/// That is a MISSING NULL CASE, not a hallucination: the note format had no legal way to say "there
/// was not enough here", so the model filled the shape it was handed. Asking the model to judge its
/// own sufficiency does not fix it - a model asked "include this only if warranted" says yes almost
/// regardless. So the gate is arithmetic and runs before the model is consulted.
///
/// These tests pin the CALIBRATION, because a threshold picked by taste rather than measurement is
/// how the first attempt at this gate swallowed every note in the test suite.
/// </summary>
public sealed class ThinInputGateTests
{
    static IReadOnlyList<TranscriptSegment> Say(params string[] lines) =>
        lines.Select((t, i) => new TranscriptSegment(Speaker.You, i * 1000, i * 1000 + 900, t)).ToList();

    /// <summary>The real failing meeting, verbatim. 56 substantive words, measured. It must not reach
    /// the writer.</summary>
    [Fact]
    public void The_rotation_meeting_falls_under_the_floor()
    {
        var t = Say(
            "able to go deeper, uh increase the objectivity with which my reports can manage their groups, " +
            "allow me more time to focus on new markets and allow me to shift into more of a question ask " +
            "remote than generating content and answering questions in the meetings. And but to avoid adding " +
            "three net new meetings to stakeholders' calendars, I propose we do a sort of two-month rotation. " +
            "So month one, development quality go, month two, security and UX would go.");

        var n = LlmNoteWriter.SubstantiveWords(t);
        Assert.True(n < LlmNoteWriter.ThinInputWords,
            $"the rotation meeting scored {n} substantive words but the floor is {LlmNoteWriter.ThinInputWords} - " +
            "it would reach the writer again and invent a Decision.");
    }

    /// <summary>A meeting of ordinary length must NOT be gated. The real Library's median is 210
    /// substantive words; a gate that catches that is a gate that deletes the product.</summary>
    [Fact]
    public void An_ordinary_meeting_clears_the_floor()
    {
        var t = Say(
            "The database migration finished on August twenty first at three thirty pm, about two days ahead of schedule.",
            "We validated fifty percent of the tables on Friday and the rest over the weekend.",
            "Craig found one issue with the invoice table, but Abhishek patched it the same evening.",
            "On budget we are at seventy percent of the quarterly allocation, which leaves room for the vendor renewal.",
            "The contract runs to March twenty twenty seven, so nothing is urgent, but let us decide before October.",
            "Sita is taking over the reporting dashboard from next Monday and Deepika will shadow her for the first week.");

        Assert.True(LlmNoteWriter.SubstantiveWords(t) >= LlmNoteWriter.ThinInputWords,
            "a normal meeting must reach the note writer.");
    }

    /// <summary>Greetings and audio checks are not substance. This is the difference between counting
    /// WORDS and counting CONTENT - the junk notes in the real Library ("Mm-hmm", "Hello hello hello")
    /// all scored between 1 and 14.</summary>
    [Fact]
    public void Greetings_and_backchannels_do_not_count_as_substance()
    {
        var chatter = Say(
            "Hi hello, can you hear me?",
            "Yeah I can hear you, thanks.",
            "Okay so, um, yeah, right.",
            "Mm-hmm. Yes. Okay, sure, thanks.");

        Assert.True(LlmNoteWriter.SubstantiveWords(chatter) < 12,
            "a call that is entirely greetings must score near zero substantive words.");
    }

    /// <summary>The floor is not free-floating: it must sit ABOVE the known failure and well BELOW a
    /// real meeting. If someone retunes it, this is the constraint they have to keep.</summary>
    [Fact]
    public void The_floor_sits_between_the_known_failure_and_a_real_meeting()
    {
        Assert.InRange(LlmNoteWriter.ThinInputWords, 57, 150);
    }
}
