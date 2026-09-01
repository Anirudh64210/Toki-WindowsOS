using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// The readability pass, tested against lines from REAL saved meetings (2026-08-17). The bar for
/// every rule: it may only ever change how something reads, never what was said - and where a
/// conversion is not certain, the words stay words.
/// </summary>
public sealed class ReadabilityTests
{
    static string Clean(string s) => Readability.Clean(s).Text;

    // ── dates, times, years, percentages: the conversions ──

    [Theory]
    [InlineData("your last working date is uh july twenty second", "your last working date is July 22nd")]
    [InlineData("It is twenty first, august twenty first.", "It is 21st, August 21st.")]
    [InlineData("the migration finished on august twenty first at three thirty pm",
                "the migration finished on August 21st at 3:30 pm")]
    [InlineData("we validated fifty percent of the tables", "we validated 50% of the tables")]
    [InlineData("the contract runs to march twenty twenty seven", "the contract runs to March 2027")]
    [InlineData("that shipped back in nineteen ninety five", "that shipped back in 1995")]
    [InlineData("decide before the fifteenth of october", "decide before the 15th of October")]
    [InlineData("call me at eleven a.m. tomorrow", "call me at 11 a.m. tomorrow")]
    [InlineData("a hundred percent agreed", "100% agreed")]
    public void Spoken_dates_times_and_numbers_become_digits(string heard, string expected) =>
        Assert.Equal(expected, Clean(heard));

    // ── what must NOT convert ──

    [Theory]
    [InlineData("I have two questions about this")]           // a count, not a date - words are correct
    [InlineData("the third option is cheaper")]               // small ordinal in prose stays words
    [InlineData("you may second that motion")]                // "may" the verb, not May the month
    [InlineData("the top twenty first-time buyers")]          // "twenty first-time" is not 21st
    public void Ordinary_spoken_numbers_are_left_alone(string line) =>
        Assert.Equal(line, Clean(line));

    [Fact]
    public void A_sentence_ending_after_pm_keeps_its_full_stop() =>
        Assert.Equal("we close at 3 pm.", Clean("we close at three pm."));

    // ── fillers ──

    [Fact]
    public void A_sentence_opening_filler_hands_its_capital_to_the_next_word() =>
        Assert.Equal("Hi sorry.", Clean("Uh hi sorry."));

    [Fact]
    public void Mid_sentence_fillers_vanish_without_a_seam() =>
        Assert.Equal("so I just am not sure", Clean("so um, I uh just am not sure"));

    [Fact]
    public void A_backchannel_answer_is_not_a_filler()
    {
        // "Mm-hmm." is agreement - an ANSWER - and stays. Only pure hesitation goes.
        Assert.Equal("Mm-hmm.", Clean("Mm-hmm."));
    }

    [Fact]
    public void A_line_that_is_nothing_but_hesitation_is_kept_rather_than_emptied()
    {
        var seg = new TranscriptSegment(Speaker.Them, 0, 500, "Um.");
        var (segments, _) = Readability.Clean([seg]);
        Assert.Equal("Um.", segments[0].Text);   // somebody made a sound; the record keeps it
    }

    [Fact]
    public void Umbrella_is_not_a_filler() =>
        Assert.Equal("bring the umbrella", Clean("bring the umbrella"));

    // ── extended fillers (2026-08-25): er/eh/ah interjections + parenthetical discourse markers ──

    [Theory]
    [InlineData("so er, I think that works", "so I think that works")]     // MUTATION PROBE: "er" must go
    [InlineData("Ah, right, that makes sense", "Right, that makes sense")]  // head interjection hands the capital
    public void Interjection_family_is_extended_past_um_and_uh(string heard, string expected) =>
        Assert.Equal(expected, Clean(heard));

    [Theory]
    [InlineData("it was, you know, huge", "it was huge")]                   // MUTATION PROBE: parenthetical removed, no seam
    [InlineData("we could, I mean, ship it today", "we could ship it today")]
    [InlineData("it was, like, twice as fast", "it was twice as fast")]
    [InlineData("You know, we should just ship it", "We should just ship it")] // head form, capital handed over
    [InlineData("let's just ship it, you know.", "let's just ship it.")]     // tail form, keeps the stop
    public void Comma_parenthetical_discourse_fillers_are_dropped(string heard, string expected) =>
        Assert.Equal(expected, Clean(heard));

    [Theory]
    [InlineData("do you know what time it works")]      // "you know" is literal here - no commas, untouched
    [InlineData("tell me what you mean by that")]       // "mean" the verb
    [InlineData("build a tool like this one")]          // "like" the preposition
    [InlineData("I really like the new design")]        // "like" the verb
    [InlineData("sort of the whole point")]             // "sort of" mid-phrase, no commas
    public void Discourse_words_in_their_literal_use_are_never_touched(string line) =>
        Assert.Equal(line, Clean(line));

    // ── stutters ──

    [Theory]
    [InlineData("but let's say like even if if they are not responding",
                "but let's say like even if they are not responding")]     // real line, meeting 93
    [InlineData("I think these are the new new ones", "I think these are the new ones")]
    [InlineData("I I think that works", "I think that works")]
    public void Stuttered_doubles_collapse(string heard, string expected) =>
        Assert.Equal(expected, Clean(heard));

    [Theory]
    [InlineData("the work he had had was finished")]    // legitimate double: past perfect
    [InlineData("I said that that was fine")]           // legitimate double: conjunction + pronoun
    public void Legitimate_english_doubles_survive(string line) =>
        Assert.Equal(line, Clean(line));

    // ── the receipts ──

    [Fact]
    public void Number_conversions_are_recorded_so_the_transcript_can_show_its_working()
    {
        var (_, tidies) = Readability.Clean("due august twenty first");
        Assert.Contains(tidies, t => t.From == "august twenty first" && t.To == "August 21st");
    }

    [Fact]
    public void Filler_removal_is_not_recorded_as_a_correction()
    {
        var (_, tidies) = Readability.Clean("so um, fine");
        Assert.Empty(tidies);
    }
}

/// <summary>Found BY the WER harness on its first run: the synthesized eval script came back with
/// "Abhishek" as "Ubishek", and the first-sound gate (A != U) blocked even a pinned name from
/// repairing it. Opening vowels are one sound class now.</summary>
public sealed class VowelOpeningTests
{
    [Fact]
    public void A_name_misheard_with_a_different_opening_vowel_is_repaired_by_its_pinned_spelling()
    {
        var seg = new TranscriptSegment(Speaker.Them, 0, 900, "and Ubishek patched it the same evening");
        var result = NameCorrector.Correct([seg], trusted: ["Abhishek"]);
        Assert.Contains("Abhishek", result.Segments[0].Text);
    }

    [Fact]
    public void Consonant_openings_stay_strict()
    {
        // The vowel class must not loosen consonants: Kirtana still never becomes Craig.
        var result = NameCorrector.Correct(
            [new TranscriptSegment(Speaker.Them, 0, 900, "so Kirtana can you check that")],
            trusted: ["Craig"]);
        Assert.Contains("Kirtana", result.Segments[0].Text);
    }
}
