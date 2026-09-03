using System.Collections.Generic;
using System.Linq;
using Toki.Llm;
using Toki.Storage;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// The grounding metric asks one question: does the note state a name, number or date that the
/// meeting never contained?
///
/// Every test below is a MUTATION PROBE against a false positive this metric actually produced on
/// the owner's real 112-note Library on 2026-09-02. It read 67% precision on its first run and 96.2%
/// once these four were fixed - which is to say almost everything it originally "found" was its own
/// bug. That is the whole lesson: an instrument that cries wolf is worse than no instrument, because
/// it sends you off fixing a writer that was not broken. Any change to Grounding must keep these
/// passing or it is measuring itself again.
/// </summary>
public sealed class GroundingTests
{
    static IReadOnlyList<TranscriptSegment> Say(params string[] lines) =>
        lines.Select((t, i) => new TranscriptSegment(Speaker.You, i * 1000, i * 1000 + 900, t)).ToList();

    static NoteSet Note(string summary, params string[] bullets) => new()
    {
        FromModel = true,
        Tldr = summary,
        Sections = [new NoteSection { Heading = "Key points", Bullets = [.. bullets] }],
    };

    /// <summary>FALSE POSITIVE 1: a capital at the start of a bullet is grammar, not a proper noun.
    /// This one alone accounted for most of the original 67% reading - the metric was reporting
    /// "Identified", "Agreed" and "Rotate" as invented people in almost every note.</summary>
    [Fact]
    public void A_capitalised_first_word_of_a_bullet_is_not_a_name()
    {
        var r = Grounding.Score(
            Note("The team looked at the tracker.", "Identified a gap in the tracker", "Agreed to revisit it"),
            Say("We looked at the tracker and found a gap. Let us revisit it."));

        Assert.DoesNotContain(r.Unsupported, e => e.Text is "Identified" or "Agreed");
    }

    /// <summary>FALSE POSITIVE 2: people SAY "seventy percent" and Toki WRITES "70%". A digits-only
    /// comparison called a perfectly grounded number an invention.</summary>
    [Fact]
    public void A_number_spoken_in_words_counts_as_present()
    {
        var r = Grounding.Score(
            Note("Budget is at 70% of the quarterly allocation."),
            Say("On budget, we are at seventy percent of the quarterly allocation."));

        Assert.DoesNotContain(r.Unsupported, e => e.Text.Contains("70"));
    }

    /// <summary>FALSE POSITIVE 3: a model that numbers its own bullets is not claiming the meeting
    /// mentioned the number 2.</summary>
    [Fact]
    public void A_list_marker_is_not_a_numeric_claim()
    {
        var r = Grounding.Score(
            Note("Two things came up.", "1. Search is slow", "2. Onboarding needs work"),
            Say("Search is slow and onboarding needs work."));

        Assert.DoesNotContain(r.Unsupported, e => e.Kind == Grounding.Kind.Number);
    }

    /// <summary>FALSE POSITIVE 4: "Still open from last time" is built from OTHER meetings' action
    /// items. Its names are grounded in a transcript this scorer is not holding, so counting them
    /// reported three correctly carried-over tasks as invented people (meeting 98).</summary>
    [Fact]
    public void The_carry_forward_section_is_not_scored_against_this_transcript()
    {
        var notes = Note("We reviewed the design issues.", "Cutoff buttons identified as a design issue");
        notes.Sections.Insert(0, new NoteSection
        {
            Heading = OpenFromLastTime.Heading,
            Bullets = ["set up meeting with Surabi from documentation team (Kirtana)"],
        });

        var r = Grounding.Score(notes, Say("The cutoff buttons are a design issue we should fix."));

        Assert.DoesNotContain(r.Unsupported, e => e.Text is "Surabi" or "Kirtana");
    }

    /// <summary>And the metric must still CATCH the thing it exists for. A person who was never in
    /// the room is the failure users never forgive.</summary>
    [Fact]
    public void A_person_who_was_never_in_the_transcript_is_caught()
    {
        var r = Grounding.Score(
            Note("The migration was handled by Priya.", "Ownership passed to Priyanka Deshmukh"),
            Say("The migration finished on Friday. Nobody was named."));

        Assert.Contains(r.Unsupported, e => e.Kind == Grounding.Kind.Name);
        Assert.True(r.Precision < 1.0, "an invented person must lower precision");
    }

    /// <summary>A speaker named by diarisation is legitimately nameable even though the word never
    /// occurs in anything anyone said.</summary>
    [Fact]
    public void A_diarised_speaker_name_counts_as_present()
    {
        var transcript = new List<TranscriptSegment>
        {
            new TranscriptSegment(Speaker.Them, 0, 2000, "I will take the migration.") { Tag = "Craig" },
        };
        var r = Grounding.Score(Note("Craig took the migration."), transcript);

        Assert.DoesNotContain(r.Unsupported, e => e.Text == "Craig");
    }

    // ── the name gate ──

    /// <summary>The gate's whole job: a colleague who was never in the meeting does not ship. This is
    /// the failure that makes a note untrustworthy rather than merely imperfect.</summary>
    [Fact]
    public void A_line_naming_someone_absent_is_dropped()
    {
        var notes = Note("Priya finished the migration.",
            "Priya finished the migration on Friday",
            "Priyanka Deshmukh will own the rollout");

        var dropped = Grounding.PruneUngroundedNames(notes,
            Say("Priya finished the migration on Friday.", "Priya will confirm the tables."));

        Assert.Equal(1, dropped);
        var kept = notes.EffectiveSections().SelectMany(s => s.Bullets).ToList();
        Assert.Contains(kept, b => b.Contains("Priya finished"));
        Assert.DoesNotContain(kept, b => b.Contains("Priyanka"));
    }

    /// <summary>MUTATION PROBE (Real_tasks_are_never_dropped_by_the_name_filter, and the 2026-08
    /// NameCorrector incident): when the transcript carries NO names at all, an absent name is not
    /// evidence of invention - it is evidence the recogniser caught nothing. Pruning there deletes
    /// real tasks. Below the floor, say nothing.</summary>
    [Fact]
    public void Nothing_is_pruned_when_the_transcript_names_nobody()
    {
        var notes = Note("We agreed the plan.", "Ask Priyanka to make the folder");

        var dropped = Grounding.PruneUngroundedNames(notes, Say("we agreed the plan and moved on."));

        Assert.Equal(0, dropped);
        Assert.Single(notes.EffectiveSections().SelectMany(s => s.Bullets));
    }

    /// <summary>Numbers and dates get paraphrased legitimately ("about a hundred", "early next
    /// week"). The gate is names only, or it would delete good content to satisfy a string match.</summary>
    [Fact]
    public void The_gate_does_not_prune_numbers_or_dates()
    {
        var notes = Note("Craig sized the work.", "Roughly 100 accounts affected by Tuesday");

        var dropped = Grounding.PruneUngroundedNames(notes,
            Say("Craig said about a hundred accounts are affected, some time early next week."));

        Assert.Equal(0, dropped);
    }

    /// <summary>Carry-forward items come from other meetings and must survive.</summary>
    [Fact]
    public void The_gate_never_touches_the_carry_forward()
    {
        var notes = Note("We reviewed the design.", "Craig confirmed the cutoff buttons");
        notes.Sections.Insert(0, new NoteSection
        {
            Heading = OpenFromLastTime.Heading,
            Bullets = ["set up meeting with Surabi from documentation team (Kirtana)"],
        });

        Grounding.PruneUngroundedNames(notes, Say("Craig confirmed the cutoff buttons are wrong."));

        Assert.Contains(notes.Sections, s => s.Heading == OpenFromLastTime.Heading && s.Bullets.Count == 1);
    }

    /// <summary>Recall exists so precision cannot be gamed by writing less. An empty note must not
    /// score perfectly.</summary>
    [Fact]
    public void An_empty_note_has_perfect_precision_and_terrible_recall()
    {
        var r = Grounding.Score(Note(""),
            Say("Priya finished the migration on Friday.", "Priya confirmed the migration on Friday."));

        Assert.Equal(1.0, r.Precision);
        Assert.True(r.Recall < 0.5, "a note that says nothing must be visibly incomplete, not perfect.");
    }
}
