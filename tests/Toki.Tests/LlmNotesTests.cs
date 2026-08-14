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
        public async IAsyncEnumerable<string> StreamAsync(string system, string user, int maxTokens,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
            IReadOnlyList<string>? stop = null)
        { await Task.Yield(); yield return reply(user); }
    }

    static readonly TranscriptSegment[] Transcript =
    [
        new(Speaker.Them, 0, 2000, "What analytics platform do we use?"),
        new(Speaker.You, 2100, 4000, "Let's ship Thursday."),
    ];

    [Fact]
    public async Task CombinedPassProducesCleanNotes()
    {
        // One structured generation returns all three sections; a second call titles it.
        var llm = new FakeLlm(user =>
            user.Contains("Write a title") ? "Ship Thursday"
            : user.Contains("KEY POINTS") ?
                "KEY POINTS\n- Ship on Thursday\n- Keep email capture optional\n\n"
                + "ACTION ITEMS\n- Wire the webhook | owner: You | due: This week\n\n"
                + "SUMMARY\nThe team agreed to ship Thursday; the webhook is the last blocker."
            : "NONE");

        var notes = await new LlmNoteWriter(llm).WriteAsync(Transcript);
        // Key points now live in the template's flexible Sections (not the legacy Decisions bucket).
        Assert.Contains(notes.EffectiveSections().SelectMany(s => s.Bullets), b => b.Contains("Thursday"));
        Assert.Single(notes.Actions, a => a.Text.Contains("webhook") && a.Owner == "You" && a.Due == "This week");
        Assert.Contains("Thursday", notes.Tldr);
        Assert.Equal("Ship Thursday", notes.Title);
        Assert.Equal("General", notes.Template);   // nothing signalled a niche type → General
    }

    [Fact]
    public async Task DetectsTemplateAndWritesItsSections()
    {
        // Classify → Standup; final pass returns that template's sections (Updates / Blockers).
        var llm = new FakeLlm(user =>
            user.Contains("Write a title") ? "Sprint Standup"
            : user.Contains("Pick the single best note template") ? "Standup"
            : user.Contains("UPDATES") ?
                "UPDATES\n- Alice finished the API\n\nBLOCKERS\n- Waiting on the design\n\nSUMMARY\nQuick team sync."
            : "NONE");

        // The transcript has to actually LOOK like a standup: since TemplateGate landed, a specialised
        // template the transcript does not support is downgraded to General.
        var standup = new TranscriptSegment[]
        {
            new(Speaker.Them, 0, 4000, "Yesterday I finished the API, today I'm on the tests."),
            new(Speaker.You, 4100, 8000, "I'm blocked on the design review."),
        };
        var notes = await new LlmNoteWriter(llm).WriteAsync(standup);
        Assert.Equal("Standup", notes.Template);
        Assert.Contains(notes.Sections, s => s.Heading == "Updates" && s.Bullets.Any(b => b.Contains("API")));
        Assert.Contains(notes.Sections, s => s.Heading == "Blockers");
    }

    [Fact]
    public async Task PlaceholderOwnerDueAreDropped()
    {
        var llm = new FakeLlm(user =>
            user.Contains("Write a title") ? "Do the thing"
            : "KEY POINTS\nNONE\n\nACTION ITEMS\n- Do the thing | owner: - | due: ->\n\nSUMMARY\nOne task remains.");

        var notes = await new LlmNoteWriter(llm).WriteAsync(Transcript);
        Assert.Single(notes.Actions, a => a.Text.Contains("thing") && a.Owner is null && a.Due is null);
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
        public IAsyncEnumerable<string> StreamAsync(string s, string u, int m, CancellationToken ct = default,
            IReadOnlyList<string>? stop = null)
            => throw new InvalidOperationException("should not be called");
    }
}
