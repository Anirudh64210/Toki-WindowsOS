using System;
using System.Collections.Generic;
using System.Linq;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Regression tests from the 2026-08-18 five-critic audit of the processing pipeline. Each test is
/// one confirmed finding, written so the fix can never silently regress. The theme across all of
/// them: a layer that guesses must prefer silence to a confident wrong answer.
/// </summary>
public sealed class ProcessingAuditTests
{
    static TranscriptSegment You(int ms, string t) => new(Speaker.You, ms, ms + 900, t);
    static TranscriptSegment Them(int ms, string t) => new(Speaker.Them, ms, ms + 900, t);

    // ── echo dedup: deletion direction ──

    [Fact]
    public void A_long_real_sentence_is_not_deleted_because_a_short_backchannel_shares_its_words()
    {
        // "yeah okay sounds good" from Them contains only words that also occur in the long real
        // You sentence. The old containment divided by the SHORTER set and scored this 1.0 - and
        // deleted the thirty-word line as an "echo" of the four-word one.
        var you = You(1000, "yeah okay sounds good so the plan is we take the vendor list sort it by cost and then walk the top three through security review before anyone signs");
        var merged = TranscriptMerger.Merge([you], [Them(1200, "yeah okay sounds good")]);
        Assert.Contains(merged, s => s.Speaker == Speaker.You && s.Text.Contains("security review"));
    }

    // (An exact duplicate of TranscriptMergerTests.FuzzyMatchDropsNearDuplicateLongLine lived here
    // until the 2026-08-18 test audit; the accept-side of the echo rule is asserted there, once.)

    // ── self-correction: sentence boundaries and repetitions ──

    [Fact]
    public void An_apology_opening_the_next_sentence_is_not_a_self_repair()
    {
        // "X. Sorry, X" is two sentences flanking an apology. The old pattern matched across the
        // period and produced "I couldn't hear you there was noise." - deleting a word, the
        // boundary, and the apology.
        var (text, repairs) = SelfCorrection.Clean("I couldn't hear you there. Sorry, there was noise.");
        Assert.Empty(repairs);
        Assert.Contains("there. Sorry, there", text);
    }

    [Fact]
    public void Saying_the_same_word_twice_around_a_cue_is_repetition_not_repair()
    {
        var (text, repairs) = SelfCorrection.Clean("send it to Craig, sorry, Craig can take it");
        Assert.Empty(repairs);
        Assert.Contains("Craig, sorry, Craig", text);
    }

    // ── name corrector: short names, established people, compounds ──

    [Fact]
    public void Two_real_short_names_differing_only_in_a_vowel_never_merge()
    {
        // Tim and Tom share a vowel-free phonetic key. A company with both must keep both, no
        // matter how lopsided the corpus counts are.
        var corpus = new Dictionary<string, int> { ["Tim"] = 50, ["Tom"] = 10 };
        var result = NameCorrector.Correct(
            [Them(1000, "ask Tom and then ask Tim about it")], corpus: corpus);
        Assert.Contains("Tom", result.Segments[0].Text);
        Assert.Contains("Tim", result.Segments[0].Text);
    }

    [Fact]
    public void An_established_spelling_is_a_person_the_corpus_vote_cannot_erase()
    {
        // Bryan appears 10 times across the library: he is a colleague, not a mishearing of the
        // more frequent Brian. Only a human (trusted name) may override that.
        var corpus = new Dictionary<string, int> { ["Brian"] = 50, ["Bryan"] = 10 };
        var result = NameCorrector.Correct(
            [Them(1000, "and Bryan said he would check")], corpus: corpus);
        Assert.Contains("Bryan", result.Segments[0].Text);
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void A_rare_spelling_still_collapses_onto_the_established_one()
    {
        var corpus = new Dictionary<string, int> { ["Craig"] = 6, ["Greg"] = 1 };
        var result = NameCorrector.Correct([Them(1000, "propose it to Greg today")], corpus: corpus);
        Assert.Contains("Craig", result.Segments[0].Text);
    }

    [Fact]
    public void An_attendee_whose_name_is_an_ordinary_word_is_not_a_correction_magnet()
    {
        // A calendar attendee named "May" must not pull short names toward it with trusted reach.
        var result = NameCorrector.Correct(
            [Them(1000, "I think Mae can pick that up")], trusted: ["May"]);
        Assert.Contains("Mae", result.Segments[0].Text);
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void A_hyphenated_name_is_never_partially_rewritten()
    {
        // Detection refuses compounds; replacement must too. The old tokenizer split "Jean-Luc"
        // and rewrote the halves independently.
        var corpus = new Dictionary<string, int> { ["Luke"] = 9, ["Luc"] = 1 };
        var result = NameCorrector.Correct(
            [Them(1000, "and Jean-Luc agreed to lead it")], corpus: corpus);
        Assert.Contains("Jean-Luc", result.Segments[0].Text);
    }

    [Fact]
    public void A_name_ending_a_question_still_counts_as_evidence()
    {
        // "Greg?" used to be discarded by the letters-only test, so names that end sentences lost
        // every frequency vote. Two mid-sentence "Greg" mentions here come only from questions.
        var corpus = new Dictionary<string, int> { ["Craig"] = 6, ["Greg"] = 1 };
        var result = NameCorrector.Correct(
            [Them(1000, "have you asked Greg? maybe ask Greg?")], corpus: corpus);
        Assert.Contains(result.Fixes, f => f.To == "Craig");
    }

    // ── diarisation: the attendee prior must never split ──

    [Fact]
    public void One_voice_is_never_shredded_to_match_the_invite_count()
    {
        // Five invitees, one person actually talking: six near-identical embeddings. The old stop
        // rule quit merging at expected=4 clusters, splitting a single human four ways.
        var one = new float[] { 1f, 0f, 0f };
        var vectors = Enumerable.Range(0, 6)
            .Select(i => new float[] { 1f, 0.001f * i, 0f }).ToList();
        var labels = SpeakerDiariser.Cluster(vectors, expected: 4, threshold: 0.80);
        Assert.Single(labels.Distinct());
        GC.KeepAlive(one);
    }

    [Fact]
    public void Two_distinct_voices_stay_two_even_when_more_were_invited()
    {
        var a = new float[] { 1f, 0f, 0f };
        var b = new float[] { 0f, 1f, 0f };   // orthogonal: maximally far apart
        var vectors = new List<float[]> { a, b, a, b };
        var labels = SpeakerDiariser.Cluster(vectors, expected: 4, threshold: 0.80);
        Assert.Equal(2, labels.Distinct().Count());
    }

    // ── speaker naming: the roster matches how people actually talk ──

    [Fact]
    public void A_calendar_display_name_vouches_for_its_first_name()
    {
        // The invite says "Priya Devi"; the room says "Priya". Whole-string matching meant the
        // roster - the namer's strongest trust signal - never matched anything on a real invite.
        var turns = new List<NamedTurn>
        {
            new(-1, "Over to you, Priya."),
            new(2, "Thanks. My update is short: the migration finished last night and the checks passed."),
        };
        var labels = SpeakerNamer.Name(turns, roster: ["Priya Devi"], ownName: "Sid Kumar");
        Assert.Contains(labels, l => l.Name == "Priya" && l.Cluster == 2);
    }

    [Fact]
    public void The_users_own_first_name_is_never_pinned_on_a_remote_voice()
    {
        var turns = new List<NamedTurn>
        {
            new(1, "Are you there, Sid?"),
            new(2, "Yes, I can hear you now, go ahead with the update."),
        };
        var labels = SpeakerNamer.Name(turns, roster: null, ownName: "Sid Kumar");
        Assert.DoesNotContain(labels, l => l.Name.Equals("Sid", StringComparison.OrdinalIgnoreCase));
    }
}
