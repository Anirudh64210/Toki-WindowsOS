using System.Collections.Generic;
using Toki.Llm;
using Toki.Storage;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Attributing a to-do to the person who volunteered for it.
///
/// This runs with no model at all: the to-do already has an evidence link to the transcript line it came
/// from, and once that line knows who spoke it, a first-person commitment names its own owner.
///
/// The tests are mostly refusals, for the usual reason - putting the WRONG person's name against a
/// commitment is worse than leaving it blank, because an unowned to-do is obviously unfinished whereas a
/// wrongly-owned one looks finished and is quietly false.
/// </summary>
public sealed class ActionOwnerTests
{
    static TranscriptSegment Said(string who, string text, int start) =>
        new(who == "You" ? Speaker.You : Speaker.Them, start, start + 3000, text)
        { Tag = who is "You" or "Them" ? null : who };

    static ActionItem Todo(string text) => new() { Text = text };

    [Fact]
    public void Someone_who_volunteers_owns_the_task()
    {
        var transcript = new List<TranscriptSegment>
        {
            Said("Devi", "Okay, I'll put the onboarding proposal together and send it round on Friday.", 0),
        };
        var actions = new List<ActionItem> { Todo("Put the onboarding proposal together and send it round") };

        var owners = ActionOwners.Infer(actions, transcript);

        Assert.Single(owners);
        Assert.Equal("Devi", owners[0].Owner);
    }

    [Fact]
    public void Reporting_somebody_elses_task_names_nobody()
    {
        // Devi is describing Craig's work, not taking it on. Attributing it to Devi would be a lie that
        // reads as a fact.
        var transcript = new List<TranscriptSegment>
        {
            Said("Devi", "Craig will send the onboarding proposal round on Friday.", 0),
        };
        var actions = new List<ActionItem> { Todo("Send the onboarding proposal round") };

        Assert.Empty(ActionOwners.Infer(actions, transcript));
    }

    [Fact]
    public void A_task_assigned_to_you_names_nobody_from_the_speaker()
    {
        // "You will need to update the deck" - the speaker is assigning, not volunteering.
        var transcript = new List<TranscriptSegment>
        {
            Said("Devi", "You will need to update the deck before the review.", 0),
        };
        var actions = new List<ActionItem> { Todo("Update the deck before the review") };

        Assert.Empty(ActionOwners.Infer(actions, transcript));
    }

    [Fact]
    public void The_local_user_can_own_a_task_they_volunteered_for()
    {
        var transcript = new List<TranscriptSegment>
        {
            Said("You", "Sure, I'll chase the invoices this week.", 0),
        };
        var actions = new List<ActionItem> { Todo("Chase the invoices this week") };

        var owners = ActionOwners.Infer(actions, transcript);

        Assert.Single(owners);
        Assert.Equal("You", owners[0].Owner);
    }

    [Fact]
    public void An_untagged_remote_line_stays_unowned()
    {
        // Diarisation has not run, so all we know is "somebody remote". "Them" is not an owner.
        var transcript = new List<TranscriptSegment>
        {
            Said("Them", "Right, I'll chase the invoices this week.", 0),
        };
        var actions = new List<ActionItem> { Todo("Chase the invoices this week") };

        Assert.Empty(ActionOwners.Infer(actions, transcript));
    }

    [Fact]
    public void An_owner_the_model_already_found_is_never_overwritten()
    {
        var transcript = new List<TranscriptSegment>
        {
            Said("Devi", "I'll chase the invoices this week.", 0),
        };
        var actions = new List<ActionItem> { new() { Text = "Chase the invoices this week", Owner = "Craig" } };

        Assert.Empty(ActionOwners.Infer(actions, transcript));
    }

    [Fact]
    public void A_task_with_no_confident_source_line_stays_unowned()
    {
        // Nothing in the transcript resembles the to-do, so there is no line to read an owner from.
        var transcript = new List<TranscriptSegment>
        {
            Said("Devi", "Anyway, the weather has been miserable all week.", 0),
        };
        var actions = new List<ActionItem> { Todo("Renegotiate the hosting contract before renewal") };

        Assert.Empty(ActionOwners.Infer(actions, transcript));
    }

    [Fact]
    public void A_statement_of_fact_is_not_a_commitment()
    {
        var transcript = new List<TranscriptSegment>
        {
            Said("Devi", "The onboarding proposal went round on Friday and everyone signed off.", 0),
        };
        var actions = new List<ActionItem> { Todo("Onboarding proposal went round Friday") };

        Assert.Empty(ActionOwners.Infer(actions, transcript));
    }

    [Fact]
    public void An_empty_meeting_is_not_an_error()
    {
        Assert.Empty(ActionOwners.Infer([], []));
        Assert.Empty(ActionOwners.Infer([Todo("something")], []));
    }
}
