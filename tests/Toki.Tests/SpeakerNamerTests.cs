using System.Collections.Generic;
using System.Linq;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Naming the voices WITHOUT a voiceprint.
///
/// The whole feature rests on one bet: a meeting tells you who is in it, if you read it. These tests are
/// the evidence for that bet, and equally the evidence for the refusals - most of what follows is about
/// what Toki must NOT conclude, because inventing a colleague is far worse than leaving "Speaker 2".
/// </summary>
public sealed class SpeakerNamerTests
{
    // Cluster 0 and 1 are two remote voices; -1 is the local user, who is already known.
    static NamedTurn Me(string t) => new(-1, t);
    static NamedTurn A(string t) => new(0, t);
    static NamedTurn B(string t) => new(1, t);

    static string? NameOf(IReadOnlyList<SpeakerLabel> labels, int cluster) =>
        labels.FirstOrDefault(l => l.Cluster == cluster)?.Name;

    // ── the three signals ──

    [Fact]
    public void Someone_who_introduces_themselves_is_named()
    {
        var labels = SpeakerNamer.Name([
            Me("right, shall we get going"),
            A("Hi everyone, I'm Priya, I look after onboarding."),
        ]);

        Assert.Equal("Priya", NameOf(labels, 0));
    }

    [Fact]
    public void A_self_introduction_is_trusted_even_for_a_name_nobody_supplied()
    {
        // No calendar, no pinned list, said exactly once. Still fine: the speaker is telling you.
        var labels = SpeakerNamer.Name([A("My name is Aditi and I'm covering for Sam this week.")]);
        Assert.Equal("Aditi", NameOf(labels, 0));
    }

    [Fact]
    public void Handing_over_names_the_person_who_speaks_next()
    {
        var labels = SpeakerNamer.Name([
            Me("that's the update from my side. Over to you, Priya"),
            A("Thanks. So the onboarding numbers are up."),
        ], roster: ["Priya"]);

        Assert.Equal("Priya", NameOf(labels, 0));
    }

    [Fact]
    public void Thanking_someone_names_the_person_who_just_spoke()
    {
        var labels = SpeakerNamer.Name([
            A("so that's why the deadline slipped by about a week."),
            Me("Thanks, Priya. That makes sense."),
        ], roster: ["Priya"]);

        Assert.Equal("Priya", NameOf(labels, 0));
    }

    [Fact]
    public void Addressing_someone_by_name_names_the_person_who_answers()
    {
        var labels = SpeakerNamer.Name([
            Me("Priya, can you take us through the numbers?"),
            A("Sure. We're at about four hundred signups."),
        ], roster: ["Priya"]);

        Assert.Equal("Priya", NameOf(labels, 0));
    }

    [Fact]
    public void Two_speakers_are_named_independently()
    {
        var labels = SpeakerNamer.Name([
            Me("let's start. Priya, want to kick off?"),
            A("Sure. Onboarding is looking healthy."),
            Me("great. Over to you, Marcus"),
            B("Right, so on the billing side we've had two incidents."),
        ], roster: ["Priya", "Marcus"]);

        Assert.Equal("Priya", NameOf(labels, 0));
        Assert.Equal("Marcus", NameOf(labels, 1));
    }

    // ── the refusals: everything below is a name Toki must NOT attach ──

    [Fact]
    public void The_users_own_name_is_never_given_to_a_remote_voice()
    {
        // In a one-to-one the other person says your name constantly, and every one of those is
        // about YOU. Attaching it would label the remote speaker with the listener's name.
        var labels = SpeakerNamer.Name([
            Me("so where did we land on pricing?"),
            A("Thanks, Sid. I think we land on the middle tier."),
        ], roster: ["Sid"], ownName: "Sid");

        Assert.Null(NameOf(labels, 0));
    }

    [Fact]
    public void An_ordinary_capitalised_word_is_never_a_person()
    {
        // "Thanks, Great work" and friends: the recogniser capitalises mid-sentence constantly.
        var labels = SpeakerNamer.Name([
            A("we shipped it on Tuesday."),
            Me("Thanks, Great. Then we're done."),
        ]);

        Assert.Empty(labels);
    }

    [Fact]
    public void A_name_nobody_vouched_for_must_be_said_more_than_once()
    {
        // One capitalised word said once is likelier a mishearing than a colleague.
        var once = SpeakerNamer.Name([
            A("the migration finished overnight."),
            Me("Thanks, Yashtren."),
        ]);
        Assert.Empty(once);

        // The same evidence, but the name is on the calendar: now it is a person.
        var vouched = SpeakerNamer.Name([
            A("the migration finished overnight."),
            Me("Thanks, Yashtren."),
        ], roster: ["Yashtren"]);
        Assert.Equal("Yashtren", NameOf(vouched, 0));
    }

    [Fact]
    public void One_name_cannot_be_given_to_two_different_voices()
    {
        // If two clusters both look like Priya, one of them is wrong. The weaker keeps its number.
        var labels = SpeakerNamer.Name([
            Me("Priya, go ahead"),
            A("sure, so the first thing is onboarding."),
            Me("thanks Priya"),
            Me("Priya, anything else?"),
            B("no I think that's everything."),
        ], roster: ["Priya"]);

        Assert.Single(labels);
        Assert.Equal("Priya", labels[0].Name);
    }

    [Fact]
    public void The_local_user_is_never_given_a_discovered_name()
    {
        // The mic stream is already known to be the user. Nothing said about them changes that.
        var labels = SpeakerNamer.Name([
            A("over to you, Priya"),
            Me("thanks, so from my side we're on track."),
        ], roster: ["Priya"]);

        Assert.DoesNotContain(labels, l => l.Cluster < 0);
    }

    [Fact]
    public void Weak_or_absent_evidence_leaves_the_speaker_numbered()
    {
        var labels = SpeakerNamer.Name([
            Me("morning"),
            A("morning, all good?"),
            Me("yeah fine"),
        ], roster: ["Priya", "Marcus"]);

        Assert.Empty(labels);
        Assert.Equal("Speaker 1", SpeakerNamer.Fallback(0));
        Assert.Equal("Speaker 2", SpeakerNamer.Fallback(1));
    }

    [Fact]
    public void An_empty_transcript_is_not_an_error()
    {
        Assert.Empty(SpeakerNamer.Name([]));
    }

    // ── the roll-call, which is what broke the notes in the first place ──

    [Fact]
    public void A_roll_call_names_the_people_who_answer_it_not_everyone_mentioned()
    {
        // The 11-person meeting on 2026-08-11 read a register out loud and eleven names became to-dos.
        // Being SAID is not evidence about a voice; answering is.
        var labels = SpeakerNamer.Name([
            Me("let me run through attendance. Priya, Marcus, Aditi, Sam, Rahul."),
            A("Priya, present."),
            Me("thanks. Marcus, are you there?"),
            B("yes, present."),
        ], roster: ["Priya", "Marcus", "Aditi", "Sam", "Rahul"]);

        // Only the two who actually spoke get a voice attached; the rest are just words in a line.
        Assert.Equal("Marcus", NameOf(labels, 1));
        Assert.DoesNotContain(labels, l => l.Name is "Aditi" or "Sam" or "Rahul");
    }

    // ── what a REAL meeting did to the first version (2026-08-12, meeting 77) ──
    //
    // Every string below is one Toki actually produced. The first run named two participants "Please"
    // and "Otherwise", which is the precise failure this whole layer exists to avoid, and no stopword
    // list would have been complete enough to catch them.

    [Fact]
    public void Please_is_not_a_person()
    {
        var labels = SpeakerNamer.Name([
            new(0, "Please, please break out right away, everyone."),
            new(1, "Okay, will do."),
        ]);

        Assert.DoesNotContain(labels, l => l.Name == "Please");
    }

    [Fact]
    public void Otherwise_is_not_a_person()
    {
        var labels = SpeakerNamer.Name([
            new(0, "Otherwise, folks can break out."),
            new(1, "Sounds good, thanks."),
        ]);

        Assert.DoesNotContain(labels, l => l.Name == "Otherwise");
    }

    [Fact]
    public void Addressing_someone_names_who_ANSWERS_not_the_asker_who_keeps_talking()
    {
        // From meeting 76 (11 people). The chair said "Yashtren, are you there?" and then carried
        // straight on, so "the next turn" was still the chair - and the chair was labelled Yashtren.
        // Addressing somebody names the person who answers, which is the next DIFFERENT speaker.
        var labels = SpeakerNamer.Name([
            A("If you're not a team lead, you gotta roll."),
            A("Yashtren, are you there?"),
            A("Can you just double-check that these are all team leads?"),
            B("Yes, on it now."),
        ], roster: ["Yashtren"]);

        Assert.Null(NameOf(labels, 0));               // the chair is NOT Yashtren
        Assert.Equal("Yashtren", NameOf(labels, 1));  // the person who answered is
    }

    [Fact]
    public void An_answer_far_later_in_the_conversation_is_not_an_answer_to_this()
    {
        // Somebody is addressed, nobody replies, and five turns later a different person speaks about
        // something else. That is not an answer, and attaching the name to them would be a guess.
        var labels = SpeakerNamer.Name([
            new(-1, "Priya, can you take this one?"),
            new(-1, "anyway, moving on."),
            new(-1, "so the second thing is billing."),
            new(-1, "and the third is onboarding."),
            new(-1, "right, that's everything."),
            A("sorry, I just joined - what did I miss?"),
        ], roster: ["Priya"]);

        Assert.Empty(labels);
    }

    [Fact]
    public void A_word_said_in_lowercase_elsewhere_is_never_a_name()
    {
        // The general rule that replaced guessing at a word list: a real name essentially never turns
        // up in lowercase, and an ordinary word always does. Here "Please" opens a sentence in exactly
        // the shape of a vocative, and "please" appears mid-sentence two lines later.
        var labels = SpeakerNamer.Name([
            new(0, "Please, contact your team leads."),
            new(1, "Right."),
            new(0, "If you are unsure, please contact your team leads."),
        ]);

        Assert.Empty(labels);
    }

    [Fact]
    public void But_a_rostered_name_survives_even_if_it_looks_like_a_word()
    {
        // Some people are called Grace, Hope, Mercy, Sunny. A human putting the name on the calendar
        // outranks any heuristic Toki has - the lowercase rule must not overrule an actual attendee.
        var labels = SpeakerNamer.Name([
            new(-1, "Grace, do you want to take this one?"),
            new(0, "Sure. So the numbers are up on last month."),
            new(-1, "we should do that with grace and not rush it"),
        ], roster: ["Grace"]);

        Assert.Equal("Grace", NameOf(labels, 0));
    }

    // ── the promise: this is text, and only text ──

    [Fact]
    public void Naming_is_pure_text_and_keeps_no_state_between_calls()
    {
        // The same conversation, twice, from a cold start each time: nothing was learned in between.
        // This is the transient-by-construction guarantee - there is nowhere for a voiceprint to live.
        NamedTurn[] turns = [Me("over to you, Priya"), A("thanks, so the numbers.")];

        var first = SpeakerNamer.Name(turns, roster: ["Priya"]);
        var second = SpeakerNamer.Name([A("thanks, so the numbers.")], roster: ["Priya"]);

        Assert.Equal("Priya", NameOf(first, 0));
        Assert.Empty(second);   // no memory of the handover that named this voice a moment ago
    }
}
