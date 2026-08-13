using System.Collections.Generic;
using System.Linq;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Name correction, built from two REAL meetings recorded 2026-08-11. Every case below is a spelling
/// Toki actually produced, not an invented one - which is the only reason to trust the thresholds.
/// </summary>
public sealed class NameCorrectorTests
{
    static TranscriptSegment You(string t) => new(Speaker.You, 0, 1000, t);
    static TranscriptSegment Them(string t) => new(Speaker.Them, 2000, 3000, t);

    static string[] TextOf((IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<NameCorrector.Fix> Fixes) r) =>
        r.Segments.Select(s => s.Text).ToArray();

    // ── the case the owner reported ──

    [Fact]
    public void Greg_becomes_Craig_because_the_library_has_seen_Craig_far_more()
    {
        // Within one meeting it is 1-1 and undecidable. Across the library it is not close.
        var corpus = new Dictionary<string, int> { ["Craig"] = 6, ["Greg"] = 1 };
        var result = NameCorrector.Correct(
            [You("did Craig mention this"), Them("we'll propose it to Greg")],
            corpus: corpus);

        Assert.Equal("we'll propose it to Craig", TextOf(result)[1]);
        Assert.Contains(result.Fixes, f => f.From == "Greg" && f.To == "Craig");
    }

    [Fact]
    public void A_dead_tie_is_left_alone_rather_than_guessed()
    {
        // One occurrence each and nothing else to go on: a coin flip is not an answer.
        var result = NameCorrector.Correct([You("did Craig mention this"), Them("propose it to Greg")]);
        Assert.Equal("propose it to Greg", TextOf(result)[1]);
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void The_users_own_name_from_Settings_is_ground_truth()
    {
        // "Anirud" was the owner's own name, mis-heard. Settings knows how it is spelled.
        var result = NameCorrector.Correct(
            [Them("so Anirud, Bindu, just going through the list")],
            trusted: ["Anirudh"]);

        Assert.Contains("Anirudh", TextOf(result)[0]);
        Assert.Contains(result.Fixes, f => f.From == "Anirud" && f.To == "Anirudh");
    }

    [Fact]
    public void A_calendar_attendee_beats_a_more_frequent_mishearing()
    {
        // Trust beats counting: the attendee list was typed by a human.
        var corpus = new Dictionary<string, int> { ["Abashik"] = 9, ["Abhishek"] = 1 };
        var result = NameCorrector.Correct(
            [Them("Abashik, you can do a sample doc")],
            trusted: ["Abhishek"], corpus: corpus);

        Assert.Contains("Abhishek", TextOf(result)[0]);
    }

    [Theory]
    [InlineData("Abashik", "Abhishek")]
    [InlineData("Panita", "Punita")]
    [InlineData("Davey", "Davy")]
    public void Real_mishearings_collapse_onto_the_spelling_the_library_knows(string heard, string right)
    {
        var corpus = new Dictionary<string, int> { [right] = 5, [heard] = 1 };
        var result = NameCorrector.Correct([Them($"so {heard} can you check that")], corpus: corpus);
        Assert.Contains(right, TextOf(result)[0]);
    }

    [Fact]
    public void A_mishearing_too_far_from_the_real_name_is_deliberately_left_alone()
    {
        // "Yashtren" for "Yashina" is a real error from the 2026-08-11 meeting, and this does NOT fix
        // it: four edits apart with different phonetic keys. Widening the thresholds far enough to
        // catch it would also start merging names that are genuinely different people, and the cost of
        // those two mistakes is not remotely equal - one is a missed fix, the other rewrites what
        // somebody said into somebody else's name. Pinning the name in Settings is the way out.
        var corpus = new Dictionary<string, int> { ["Yashina"] = 5, ["Yashtren"] = 1 };
        var result = NameCorrector.Correct([Them("so Yashtren can you check that")], corpus: corpus);
        Assert.Contains("Yashtren", TextOf(result)[0]);

        // ...and pinning it does fix it.
        var pinned = NameCorrector.Correct(
            [Them("so Yashtren can you check that")], trusted: ["Yashina"], corpus: corpus);
        Assert.Contains("Yashina", TextOf(pinned)[0]);
    }

    // ── the guards: what it must NEVER do ──

    [Fact]
    public void An_ordinary_word_is_never_turned_into_a_name()
    {
        // "Great" and "Craig" sound alike. Rewriting the first would be the worst thing this code
        // could do, so ordinary words are excluded before any matching happens.
        var corpus = new Dictionary<string, int> { ["Craig"] = 20 };
        var result = NameCorrector.Correct([Them("that is Great news for us")], corpus: corpus);
        Assert.Equal("that is Great news for us", TextOf(result)[0]);
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void A_word_opening_a_sentence_is_not_treated_as_a_name()
    {
        // Capitalised for grammar, not because it is a name.
        var corpus = new Dictionary<string, int> { ["Craig"] = 20 };
        var result = NameCorrector.Correct([Them("Greg. And that is all.")], corpus: corpus);
        Assert.StartsWith("Greg.", TextOf(result)[0]);
    }

    [Fact]
    public void Two_real_people_who_sound_alike_are_both_left_alone()
    {
        // If BOTH spellings are trusted they are two actual people, not one mis-heard one.
        var result = NameCorrector.Correct(
            [Them("Craig and Greg both joined")],
            trusted: ["Craig", "Greg"]);
        Assert.Equal("Craig and Greg both joined", TextOf(result)[0]);
        Assert.Empty(result.Fixes);
    }

    [Fact]
    public void Genuinely_different_names_are_not_merged()
    {
        var corpus = new Dictionary<string, int> { ["Priyanka"] = 9, ["Prajwal"] = 1 };
        var result = NameCorrector.Correct([Them("Priyanka and Prajwal are both here")], corpus: corpus);
        Assert.Equal("Priyanka and Prajwal are both here", TextOf(result)[0]);
    }

    [Fact]
    public void Lowercase_words_are_never_touched()
    {
        var corpus = new Dictionary<string, int> { ["Craig"] = 20 };
        var result = NameCorrector.Correct([Them("we had a greg of a time")], corpus: corpus);
        Assert.Equal("we had a greg of a time", TextOf(result)[0]);
    }

    [Fact]
    public void Punctuation_and_spacing_survive_a_rewrite()
    {
        var corpus = new Dictionary<string, int> { ["Craig"] = 6, ["Greg"] = 1 };
        var result = NameCorrector.Correct([Them("Yes, Greg, it's working.")], corpus: corpus);
        Assert.Equal("Yes, Craig, it's working.", TextOf(result)[0]);
    }

    [Fact]
    public void Nothing_to_do_means_the_original_objects_come_straight_back()
    {
        var segs = new[] { Them("nothing here needs fixing at all") };
        var result = NameCorrector.Correct(segs);
        Assert.Same(segs[0], result.Segments[0]);
    }

    [Fact]
    public void Empty_input_is_not_a_crash()
    {
        var result = NameCorrector.Correct([]);
        Assert.Empty(result.Segments);
        Assert.Empty(result.Fixes);
    }

    // ── the phonetic key itself ──

    [Theory]
    [InlineData("Craig", "Greg")]
    [InlineData("Punita", "Panita")]
    [InlineData("Davey", "Davy")]
    public void Names_that_sound_alike_share_a_phonetic_key(string a, string b)
    {
        Assert.Equal(NameCorrector.Phonetic(a), NameCorrector.Phonetic(b));
    }

    [Theory]
    [InlineData("Priyanka", "Prajwal")]
    [InlineData("Craig", "Bindu")]
    public void Names_that_do_not_sound_alike_do_not(string a, string b)
    {
        Assert.NotEqual(NameCorrector.Phonetic(a), NameCorrector.Phonetic(b));
    }

    // ── the pinned-names box (Settings) ──
    //
    // `PinnedNames` was read by NameLexicon from the day T2 shipped, but nothing could write it until
    // the Settings box was added. These cover the shapes a person actually types into a multi-line box:
    // one per line, commas, a trailing separator, stray blanks.

    [Theory]
    [InlineData("Craig\nAbhishek\nYashina", 3)]
    [InlineData("Craig, Abhishek, Yashina", 3)]
    [InlineData("Craig,\n Abhishek ,\nYashina,", 3)]      // trailing comma, stray spaces
    [InlineData("  Craig  ", 1)]
    [InlineData("", 0)]
    [InlineData("Craig; Abhishek", 2)]
    public void Pinned_names_are_parsed_the_way_people_type_them(string typed, int expected)
    {
        var settings = Toki.Llm.AppSettings.Current;
        var before = settings.PinnedNames;
        var beforeName = settings.UserName;
        try
        {
            // In-memory only. Save() is never called, so the real settings file - which holds the DPAPI
            // API key and the Google refresh token - is not touched by a test run.
            settings.PinnedNames = typed;
            settings.UserName = null;
            Assert.Equal(expected, NameLexicon.Trusted().Count);
        }
        finally { settings.PinnedNames = before; settings.UserName = beforeName; }
    }

    [Fact]
    public void A_pinned_name_and_a_calendar_attendee_are_equally_trusted_and_deduped()
    {
        var settings = Toki.Llm.AppSettings.Current;
        var before = settings.PinnedNames;
        var beforeName = settings.UserName;
        try
        {
            settings.PinnedNames = "Craig\nAbhishek";
            settings.UserName = null;
            // "craig" from a calendar invite is the same person as "Craig" typed by hand.
            var trusted = NameLexicon.Trusted(["craig", "Devi"]);
            Assert.Equal(3, trusted.Count);
            Assert.Contains("Abhishek", trusted);
            Assert.Contains("Devi", trusted);
        }
        finally { settings.PinnedNames = before; settings.UserName = beforeName; }
    }
}
