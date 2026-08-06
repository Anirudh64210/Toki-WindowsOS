using Toki.Llm;
using Xunit;

namespace Toki.Tests;

/// <summary>Small-talk must intercept pure greetings/thanks/identity (never a notes search), but must
/// NOT swallow real questions that merely contain a greeting word.</summary>
public sealed class SmallTalkTests
{
    [Theory]
    [InlineData("hi")]
    [InlineData("hey toki")]
    [InlineData("good morning")]
    [InlineData("good morning, toki")]
    [InlineData("hello there")]
    [InlineData("how are you")]
    [InlineData("how's it going")]
    [InlineData("thanks")]
    [InlineData("thank you so much")]
    [InlineData("who are you")]
    [InlineData("what are you")]
    [InlineData("what is toki")]
    [InlineData("bye")]
    [InlineData("see you later")]
    [InlineData("toki")]
    public void Intercepts_small_talk(string input)
        => Assert.NotNull(SmallTalk.TryReply(input));

    // Real questions - even ones containing a greeting word - must fall through to the notes Q&A (null).
    [Theory]
    [InlineData("what did we decide in the standup")]
    [InlineData("good morning what's on my plate today")]
    [InlineData("who owns the pricing follow-up")]
    [InlineData("what are the action items")]
    [InlineData("summarize my last meeting")]
    [InlineData("thanks for the notes, what did sarah say")]
    [InlineData("how many meetings did i have")]
    public void Falls_through_real_questions(string input)
        => Assert.Null(SmallTalk.TryReply(input));

    // ── P9.1: AFFECTION ("I love you, Toki") must get a warm canned reply, never notes retrieval -
    //    the live run sent it to the notes engine, which INVENTED a grounded-sounding ramble. And the
    //    address must match the FUZZY wake-name (the ASR heard "Toky"). ──
    [Theory]
    [InlineData("I love you, Toky.")]
    [InlineData("i love you, toki")]
    [InlineData("i love you")]
    [InlineData("love you toki")]
    [InlineData("i miss you")]
    [InlineData("i really love you toki")]
    [InlineData("you're the best")]
    [InlineData("you are amazing")]
    [InlineData("you're so cute")]
    [InlineData("i appreciate you")]
    [InlineData("will you marry me")]
    public void Intercepts_affection(string input)
        => Assert.NotNull(SmallTalk.TryReply(input));

    // Fuzzy wake-name variants of pure small-talk still intercept.
    [Theory]
    [InlineData("hey doki")]
    [InlineData("doky")]
    [InlineData("good morning, toku")]
    [InlineData("thanks docie")]
    public void Fuzzy_wake_name_small_talk(string input)
        => Assert.NotNull(SmallTalk.TryReply(input));

    // Affection words about WORK content (not about Toki) must still fall through to the Q&A.
    [Theory]
    [InlineData("i love the new dashboard design")]
    [InlineData("what did we say about the features people love")]
    [InlineData("tell me about tokyo")]     // a real topic that HAPPENS to sound like the wake-name
    public void Affection_words_about_content_fall_through(string input)
        => Assert.Null(SmallTalk.TryReply(input));
}
