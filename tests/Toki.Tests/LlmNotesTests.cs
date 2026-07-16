using Toki.Llm;
using Toki.Transcription;

namespace Toki.Tests;

public class LlmNotesTests
{
    [Theory]
    [InlineData("これは日本語です", true)]       // Japanese
    [InlineData("会议记录", true)]                // Chinese
    [InlineData("회의록", true)]                  // Korean
    [InlineData("!!!!!!", true)]                  // repeat run
    [InlineData("aaaaaaa done", true)]            // repeat run mid-string
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Ship on Thursday.", false)]      // clean English
    [InlineData("We'll confirm the font tomorrow!", false)]
    public void DegenerationValidator(string text, bool degenerate)
        => Assert.Equal(degenerate, TextValidator.IsDegenerate(text));

    // A fake model that returns canned outputs per prompt substring.
    sealed class FakeLlm(Func<string, string> reply) : ILlm
    {
        public bool Ready => true;
        public Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct = default)
            => Task.FromResult(reply(user));
    }

    static readonly TranscriptSegment[] Transcript =
    [
        new(Speaker.Them, 0, 2000, "What analytics platform do we use?"),
        new(Speaker.You, 2100, 4000, "Let's ship Thursday."),
    ];

    [Fact]
    public async Task ThreePassProducesCleanNotes()
    {
        var llm = new FakeLlm(user =>
            user.Contains("decisions") ? "- Ship on Thursday\n- Keep email capture optional"
            : user.Contains("action items") ? "- Wire the webhook | owner: You | due: This week"
            : user.Contains("summary") ? "The team agreed to ship Thursday; the webhook is the last blocker."
            : "NONE");

        var notes = await new LlmNoteWriter(llm).WriteAsync(Transcript);
        Assert.Contains(notes.Decisions, d => d.Contains("Thursday"));
        Assert.Single(notes.Actions, a => a.Text.Contains("webhook") && a.Owner == "You" && a.Due == "This week");
        Assert.Contains("Thursday", notes.Tldr);
        Assert.NotEmpty(notes.Questions); // from heuristic
    }

    [Fact]
    public async Task DegenerateOutputFallsBackToHeuristic()
    {
        // Model spews CJK for every pass → each section falls back to heuristic.
        var llm = new FakeLlm(_ => "会议 会议 会议 会议");
        var notes = await new LlmNoteWriter(llm).WriteAsync(Transcript);
        Assert.DoesNotContain("会议", notes.Tldr);
        Assert.All(notes.Decisions, d => Assert.False(TextValidator.HasNonLatinScript(d)));
    }

    [Fact]
    public async Task NotReadyModelUsesHeuristic()
    {
        var notes = await new LlmNoteWriter(new NotReadyLlm()).WriteAsync(Transcript);
        Assert.NotNull(notes); // heuristic result, no throw
    }

    sealed class NotReadyLlm : ILlm
    {
        public bool Ready => false;
        public Task<string> CompleteAsync(string s, string u, int m, CancellationToken ct = default)
            => throw new InvalidOperationException("should not be called");
    }
}
