using Toki.Transcription;

namespace Toki.Tests;

public class TranscriptMergerTests
{
    static TranscriptSegment You(int ms, string t) => new(Speaker.You, ms, ms + 1500, t);
    static TranscriptSegment Them(int ms, string t) => new(Speaker.Them, ms, ms + 1500, t);

    [Fact]
    public void MergesIntoOneTimelineOrderedByTime()
    {
        var merged = TranscriptMerger.Merge(
            [You(3000, "sounds good to me")],
            [Them(1000, "what do you think")]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(Speaker.Them, merged[0].Speaker);
        Assert.Equal(Speaker.You, merged[1].Speaker);
    }

    [Fact]
    public void DropsYouLineThatEchoesThemWithinWindow()
    {
        // Remote said it (clean on THEM); mic also picked it up (echo on YOU).
        var merged = TranscriptMerger.Merge(
            [You(1200, "brand font and the make webhook url")],
            [Them(1000, "brand font and the make webhook url")]);
        Assert.Single(merged);
        Assert.Equal(Speaker.Them, merged[0].Speaker);
    }

    [Fact]
    public void KeepsYouLineOutsideTheEchoWindow()
    {
        var merged = TranscriptMerger.Merge(
            [You(8000, "brand font and the make webhook url")],
            [Them(1000, "brand font and the make webhook url")]);
        Assert.Equal(2, merged.Count); // 7s apart - not an echo
    }

    [Fact]
    public void ShortInterjectionRequiresExactMatch_NotDeleted()
    {
        // "yeah" on YOU is a real interjection; must NOT be deleted just because
        // "yeah exactly right" is on THEM nearby.
        var merged = TranscriptMerger.Merge(
            [You(1100, "yeah")],
            [Them(1000, "yeah exactly right lets do that")]);
        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, s => s.Speaker == Speaker.You && s.Text == "yeah");
    }

    [Fact]
    public void ShortInterjectionExactMatchIsDropped()
    {
        var merged = TranscriptMerger.Merge(
            [You(1100, "okay!")],
            [Them(1000, "Okay.")]);
        Assert.Single(merged);
        Assert.Equal(Speaker.Them, merged[0].Speaker);
    }

    [Fact]
    public void FuzzyMatchDropsNearDuplicateLongLine()
    {
        var merged = TranscriptMerger.Merge(
            [You(1200, "so what is left before we actually ship this")],
            [Them(1000, "what is left before we ship this")]);
        Assert.Single(merged); // strong word overlap within window -> echo
    }

    [Fact]
    public void NormalizeStripsPunctuationAndCase()
    {
        Assert.Equal("hello there", TranscriptMerger.Normalize("  Hello, There!! "));
    }

    [Fact]
    public void EmptyStreamsProduceEmptyTranscript()
    {
        Assert.Empty(TranscriptMerger.Merge([], []));
    }

    // ── turn joining (the 182-segments-in-8-minutes problem, 2026-08-11) ──
    //
    // Parakeet emits a segment per breath group, so one sentence arrives as five. Unjoined, the note is
    // a wall of two-word paragraphs and the note writer gets a transcript chopped at every pause.

    static TranscriptSegment At(Speaker who, int start, int end, string t) => new(who, start, end, t);

    [Fact]
    public void Consecutive_lines_from_one_person_become_one_turn()
    {
        var merged = TranscriptMerger.Merge(
            [At(Speaker.You, 0, 1000, "so basically it's just like an Excel sheet that"),
             At(Speaker.You, 1200, 2200, "we have and there's another guy"),
             At(Speaker.You, 2400, 3400, "who deals with the attendance link")],
            []);

        var one = Assert.Single(merged);
        Assert.Equal("so basically it's just like an Excel sheet that we have and there's another guy "
                   + "who deals with the attendance link", one.Text);
        Assert.Equal(0, one.StartMs);
        Assert.Equal(3400, one.EndMs);   // the turn spans first start to last end
    }

    [Fact]
    public void A_real_pause_still_starts_a_new_turn()
    {
        var merged = TranscriptMerger.Merge(
            [At(Speaker.You, 0, 1000, "let me think about that"),
             At(Speaker.You, 9000, 10000, "okay here is what I would do")],
            []);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void The_other_speaker_always_breaks_a_turn()
    {
        var merged = TranscriptMerger.Merge(
            [At(Speaker.You, 0, 1000, "what do you think")],
            [At(Speaker.Them, 1200, 2200, "sounds good")]);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void A_finished_sentence_keeps_its_full_stop_and_capitalises_the_next()
    {
        var merged = TranscriptMerger.Merge(
            [At(Speaker.You, 0, 1000, "That is done."),
             At(Speaker.You, 1200, 2200, "now for the other thing")],
            []);
        Assert.Equal("That is done. Now for the other thing", Assert.Single(merged).Text);
    }

    [Fact]
    public void One_person_talking_for_ages_is_still_split_into_readable_turns()
    {
        var segs = Enumerable.Range(0, 40)
            .Select(i => At(Speaker.You, i * 1000, i * 1000 + 800, "some words that go on and on here"))
            .ToList();
        var merged = TranscriptMerger.Merge(segs, []);

        Assert.True(merged.Count > 1, "a monologue must not become one giant paragraph");
        Assert.All(merged, s => Assert.True(s.Text.Length <= 700));
    }

    // ── de-interleaving ──

    [Fact]
    public void A_backchannel_stops_splitting_someone_elses_sentence()
    {
        // The real thing, from the 2026-08-11 meeting: "idea." landed between the two halves of a
        // question because the streams are merged by start time.
        var merged = TranscriptMerger.Merge(
            [At(Speaker.You, 0, 1000, "did Craig ask"),
             At(Speaker.You, 1400, 2400, "you to do this?")],
            [At(Speaker.Them, 1100, 1300, "idea.")]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(Speaker.You, merged[0].Speaker);
        Assert.Equal("did Craig ask you to do this?", merged[0].Text);
        Assert.Equal("idea.", merged[1].Text);
    }

    [Fact]
    public void A_real_reply_is_never_moved_out_of_the_way()
    {
        // Long enough to be a turn, not a backchannel - the order must stand.
        var merged = TranscriptMerger.Merge(
            [At(Speaker.You, 0, 1000, "so what should we do"),
             At(Speaker.You, 1400, 2400, "about the pricing")],
            [At(Speaker.Them, 1100, 1300, "I think we should hold it where it is")]);

        Assert.Equal(3, merged.Count);
        Assert.Equal(Speaker.Them, merged[1].Speaker);
    }
}
