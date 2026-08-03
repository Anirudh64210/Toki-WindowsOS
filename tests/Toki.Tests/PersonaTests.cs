using Toki.Llm;
using Xunit;

namespace Toki.Tests;

/// <summary>Tier 0.6 — questions about TOKI ITSELF must get a warm canned reply, never retrieval
/// ("why are you so rude?" was literally searched against the notes — screenshot bug #4). And real
/// notes questions that merely mention "you"/"screen"/"rude" must fall through.</summary>
public sealed class PersonaTests
{
    // ── intercepts: meta/persona questions ──
    [Theory]
    [InlineData("why are you so rude?")]
    [InlineData("why are you so rude")]
    [InlineData("you're being rude")]
    [InlineData("are you mad at me")]
    [InlineData("why are you so dumb")]
    [InlineData("you're useless")]
    [InlineData("that's not what I asked")]
    [InlineData("wrong answer")]
    [InlineData("do you see what's on my screen right now?")]
    [InlineData("can you see my screen")]
    [InlineData("do you know what I'm looking at")]
    [InlineData("what am i looking at right now")]
    [InlineData("how do you work")]
    [InlineData("what else can you do")]
    [InlineData("is my data private")]
    [InlineData("do you send my data anywhere")]
    [InlineData("are you an AI?")]
    [InlineData("are you chatgpt")]
    [InlineData("sorry about that")]
    [InlineData("hey toki why are you so rude")]
    public void Intercepts_meta_questions(string input)
        => Assert.NotNull(Persona.TryReply(input));

    // ── falls through: real notes questions, even ones containing meta-ish words ──
    [Theory]
    [InlineData("what did we decide about the survey forms")]
    [InlineData("who was rude in the standup yesterday")]
    [InlineData("what did mac say about the screen design")]
    [InlineData("summarize my last meeting")]
    [InlineData("what are my open tasks")]
    [InlineData("why was the launch delayed")]
    [InlineData("what did you note down about pricing")]
    public void Falls_through_notes_questions(string input)
        => Assert.Null(Persona.TryReply(input));

    // ── screen-awareness answers concretely and honestly ──
    [Fact]
    public void ScreenQuestion_NamesTheOpenNote()
    {
        var reply = Persona.TryReply("do you see what's on my screen right now?", "MR Rate Target and Quality Balance");
        Assert.NotNull(reply);
        Assert.Contains("MR Rate Target and Quality Balance", reply);
    }

    [Fact]
    public void ScreenQuestion_HonestWhenNothingOpen()
    {
        var reply = Persona.TryReply("can you see my screen", null);
        Assert.NotNull(reply);
        Assert.Contains("nothing", reply, StringComparison.OrdinalIgnoreCase);
    }

    // A complaint must never be answered from the notes ("no mention of rudeness in the meeting
    // notes…") — the reply is an apology-shaped sentence, not a retrieval result.
    [Fact]
    public void RudenessComplaint_GetsAnApology_NotASearch()
    {
        var reply = Persona.TryReply("why are you so rude?");
        Assert.NotNull(reply);
        Assert.Contains("sorry", reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("notes", reply, StringComparison.OrdinalIgnoreCase);
    }
}
