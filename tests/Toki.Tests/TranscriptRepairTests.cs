using Toki.Llm;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// The two repairs added after the first real meetings exposed them: a template that claimed
/// something the meeting never did, and a self-correction the recogniser kept both halves of.
///
/// Both are judgement calls with an ASYMMETRIC cost, so the tests are weighted the same way: a
/// handful check that the good case works, and most of them check that the bad case is refused.
/// </summary>
public class TemplateGateTests
{
    static IReadOnlyList<TranscriptSegment> T(params string[] lines) =>
        lines.Select((l, i) => new TranscriptSegment(
            i % 2 == 0 ? Speaker.You : Speaker.Them, i * 1000, i * 1000 + 900, l)).ToList();

    static IReadOnlyList<TranscriptSegment> Tagged(params (string Tag, string Text)[] lines) =>
        lines.Select((l, i) => new TranscriptSegment(Speaker.Them, i * 1000, i * 1000 + 900, l.Text)
        { Tag = l.Tag }).ToList();

    // ── the defect this was built for ──

    [Fact]
    public void A_meeting_with_no_decision_is_not_a_decision_log()
    {
        var t = T("So how did the reports go last week?",
                  "Fine, mostly. The numbers took longer than I expected.",
                  "Okay. Let's talk again on Thursday.");
        Assert.False(TemplateGate.HasEvidence("Decision log", t));
    }

    [Fact]
    public void Discussing_options_without_choosing_is_not_a_decision()
    {
        var t = T("We could keep it monthly, or we could move it to quarterly.",
                  "Both have downsides. I want to think about it.",
                  "Sure, no rush.");
        Assert.False(TemplateGate.HasEvidence("Decision log", t));
    }

    [Fact]
    public void An_explicit_decision_earns_the_decision_log()
    {
        var t = T("We went round on this for a while.",
                  "Right, so we decided to move the review to Thursdays.",
                  "Agreed.");
        Assert.True(TemplateGate.HasEvidence("Decision log", t));
    }

    [Fact]
    public void Verify_falls_back_to_general_rather_than_failing()
    {
        var t = T("Morning. How was the weekend?", "Quiet, thanks.");
        var picked = NoteTemplates.ByName("Decision log");
        Assert.Equal("General", TemplateGate.Verify(picked, t).Name);
    }

    [Fact]
    public void Verify_keeps_a_pick_the_transcript_supports()
    {
        var t = T("Two options on the table.",
                  "We've decided to go with the second one, it is cheaper to run.");
        var picked = NoteTemplates.ByName("Decision log");
        Assert.Equal("Decision log", TemplateGate.Verify(picked, t).Name);
    }

    // ── General is the safe floor and is never gated ──

    [Fact]
    public void General_always_passes()
    {
        Assert.True(TemplateGate.HasEvidence("General", T("Anything at all.")));
    }

    [Fact]
    public void An_empty_transcript_supports_nothing()
    {
        Assert.False(TemplateGate.HasEvidence("Decision log", T()));
    }

    // ── the other specialised templates ──

    [Fact]
    public void A_sales_call_needs_commercial_talk()
    {
        Assert.False(TemplateGate.HasEvidence("Sales call",
            T("Thanks for making time.", "No problem, good to meet you.")));
        Assert.True(TemplateGate.HasEvidence("Sales call",
            T("Our pricing is per seat.", "And what does the contract look like?")));
    }

    [Fact]
    public void An_interview_needs_interview_talk()
    {
        Assert.False(TemplateGate.HasEvidence("Interview",
            T("How's the project going?", "Slowly, but it's going.")));
        Assert.True(TemplateGate.HasEvidence("Interview",
            T("Tell me about yourself.", "Walk me through your background, if you would.")));
    }

    [Fact]
    public void A_standup_needs_status_reporting()
    {
        Assert.False(TemplateGate.HasEvidence("Standup",
            T("Did you see the email?", "Not yet.")));
        Assert.True(TemplateGate.HasEvidence("Standup",
            T("I'm blocked on the migration.", "Yesterday I finished the import, today I'm on tests.")));
    }

    [Fact]
    public void A_one_to_one_needs_exactly_two_voices()
    {
        // An UNTAGGED transcript cannot support the claim: before diarisation every remote person
        // is one "Them" label, so two labels is what an 8-person all-hands looks like too. The
        // gate used to accept that (it cleared every meeting there is); now the claim needs the
        // speakers actually told apart.
        Assert.False(TemplateGate.HasEvidence("1:1", T("Line one.", "Line two.")));
        Assert.True(TemplateGate.HasEvidence("1:1", Tagged(
            ("Ana", "Line one."), ("Ben", "Line two."))));
        Assert.False(TemplateGate.HasEvidence("1:1", Tagged(
            ("Ana", "Line one."), ("Ben", "Line two."), ("Cal", "Line three."))));
    }

    [Fact]
    public void A_lecture_needs_one_dominant_voice()
    {
        // A real back-and-forth, even with teaching words in it, is not a lecture.
        var conversation = T(
            "Let me explain how the pricing works, because it changed last quarter and it matters here.",
            "Right, so is that per seat or per account? I never got a straight answer on that one.");
        Assert.False(TemplateGate.HasEvidence("Lecture", conversation));
    }

    [Fact]
    public void An_unknown_template_name_is_not_blocked()
    {
        // Gating is a safety net over a known list, not a gatekeeper over every future template.
        Assert.True(TemplateGate.HasEvidence("Retro", T("Anything.")));
    }
}

public class SelfCorrectionTests
{
    // ── the defect this was built for ──

    [Fact]
    public void The_discarded_attempt_is_dropped()
    {
        var (text, repairs) = SelfCorrection.Clean("It was in the educative, oh, executive summary.");
        Assert.Equal("It was in the executive summary.", text);
        Assert.Single(repairs);
        Assert.Equal("educative", repairs[0].From);
        Assert.Equal("executive", repairs[0].To);
    }

    [Fact]
    public void I_mean_works_the_same_way()
    {
        var (text, _) = SelfCorrection.Clean("Send it to Marcus, I mean, Markus.");
        Assert.Equal("Send it to Markus.", text);
    }

    // ── and the reason it has to be narrow ──

    [Fact]
    public void Ordinary_speech_around_a_cue_is_left_alone()
    {
        // "shipping" and "Tuesday" are two different words; "I mean" is doing ordinary work here and
        // cutting the word before it would delete what was said.
        const string said = "We're shipping, I mean, Tuesday at the earliest.";
        var (text, repairs) = SelfCorrection.Clean(said);
        Assert.Equal(said, text);
        Assert.Empty(repairs);
    }

    [Fact]
    public void A_sentence_with_no_cue_is_untouched()
    {
        const string said = "The executive summary covers the educative angle too.";
        var (text, repairs) = SelfCorrection.Clean(said);
        Assert.Equal(said, text);
        Assert.Empty(repairs);
    }

    [Fact]
    public void Short_words_are_never_repaired()
    {
        // "do"/"due", "no"/"know" - too small to judge, and the cost of being wrong is the same.
        const string said = "Is it due, sorry, do we know yet?";
        var (text, _) = SelfCorrection.Clean(said);
        Assert.Equal(said, text);
    }

    [Fact]
    public void Cleaning_is_idempotent()
    {
        var once = SelfCorrection.Clean("It was in the educative, oh, executive summary.").Text;
        var twice = SelfCorrection.Clean(once).Text;
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Empty_and_null_are_safe()
    {
        Assert.Equal("", SelfCorrection.Clean((string?)null).Text);
        // Whitespace comes back untouched rather than trimmed: nothing qualified, so nothing changes.
        Assert.Equal("   ", SelfCorrection.Clean("   ").Text);
        Assert.Empty(SelfCorrection.Clean("   ").Repairs);
    }

    [Fact]
    public void A_transcript_keeps_every_segment_and_its_timings()
    {
        var input = new List<TranscriptSegment>
        {
            new(Speaker.You, 0, 900, "Hello there."),
            new(Speaker.Them, 1000, 1900, "The educative, oh, executive team signed off."),
        };
        var (segments, repairs) = SelfCorrection.Clean(input);

        Assert.Equal(2, segments.Count);
        Assert.Equal("Hello there.", segments[0].Text);
        Assert.Equal(1000, segments[1].StartMs);
        Assert.Equal(Speaker.Them, segments[1].Speaker);
        Assert.DoesNotContain("educative", segments[1].Text);
        Assert.Single(repairs);
    }

    [Fact]
    public void An_unchanged_segment_is_the_same_object()
    {
        // Cheap, but it is also the property that makes re-cleaning a whole library safe to run twice.
        var seg = new TranscriptSegment(Speaker.You, 0, 900, "Nothing to repair here.");
        var (segments, _) = SelfCorrection.Clean(new[] { seg });
        Assert.Same(seg, segments[0]);
    }
}
