using Toki.Llm;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// The owner and due fields of an action item are CLAIMS, and claims get checked in code - a prompt
/// is a request the model may ignore. Both validators exist because of real notes:
///
///   · meeting 93 (2026-08-17) rendered a to-do due "(when no time stated)" - the model echoed the
///     prompt's own instruction into the due slot and nothing stopped it;
///   · the summarisation-faithfulness literature and our own meetings agree that a mis-attributed
///     task is the compounding error - the wrong owner in the note becomes the wrong name in the
///     follow-up mail. An owner the transcript never mentions was invented.
/// </summary>
public sealed class ActionFieldValidationTests
{
    // ── due: has to look like a time ──

    [Theory]
    [InlineData("Friday")]
    [InlineData("Aug 21")]
    [InlineData("2026-08-21")]
    [InlineData("tomorrow")]
    [InlineData("next week")]
    [InlineData("end of day")]
    [InlineData("EOD")]
    [InlineData("by noon")]
    public void A_real_deadline_is_kept(string due) =>
        Assert.True(LlmNoteWriter.LooksLikeWhen(due));

    [Theory]
    [InlineData("when no time stated")]            // the meeting-93 leak, verbatim
    [InlineData("no time was stated")]
    [InlineData("not mentioned")]
    [InlineData("unknown")]
    [InlineData("unspecified")]
    [InlineData("soonish, depending on how the review with the status team goes")]  // prose, not a date
    public void An_instruction_echo_or_non_answer_is_dropped(string due) =>
        Assert.False(LlmNoteWriter.LooksLikeWhen(due));

    // ── owner: has to have been said ──

    const string Source = "You: I'll ask Sita about the portal.\nThem: Sita said Craig approved it.";

    [Fact]
    public void An_owner_who_was_actually_named_is_kept() =>
        Assert.Equal("Sita", LlmNoteWriter.ValidOwner("Sita", Source));

    [Fact]
    public void You_is_always_a_valid_owner() =>
        Assert.Equal("You", LlmNoteWriter.ValidOwner("you", Source));

    [Fact]
    public void An_owner_the_transcript_never_mentions_is_invented_and_dropped() =>
        Assert.Null(LlmNoteWriter.ValidOwner("David Kim", Source));

    [Fact]
    public void A_generic_role_word_is_not_a_person()
    {
        Assert.Null(LlmNoteWriter.ValidOwner("name", Source));
        Assert.Null(LlmNoteWriter.ValidOwner("the team", Source));
        Assert.Null(LlmNoteWriter.ValidOwner("someone", Source));
    }

    [Fact]
    public void A_short_name_cannot_claim_to_appear_inside_a_longer_word() =>
        // "Ani" is inside "anything"; a word-boundary match must not count that as being named.
        Assert.Null(LlmNoteWriter.ValidOwner("Ani", "Them: is there anything else for today?"));
}
