using Toki.Llm;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// THE WINDOW MUST FIT IN THE CONTEXT.
///
/// Found 2026-09-03 on a real meeting. The chunk budget was `(ctx - 800) * 3`. On the owner's
/// machine a 4.0GB card takes the partial-offload branch, which sets ContextSize to 3072, so a
/// window was 6816 characters. At the rate numbered transcript lines actually tokenise that is
/// ~2600 tokens, plus ~500 for the instructions, plus up to 760 generated: comfortably over 3072.
///
/// Every window threw `LLamaDecodeError: llama_decode failed: 'NoKvSlot'`. `TextPass` swallowed the
/// exception and returned null, the caller counted every window as failed, and the meeting fell to
/// the heuristic floor, which writes raw transcript lines as to-dos. Nothing in the log said so.
///
/// So on a 4GB card, LONG meetings were never written by the model at all, while short ones fit and
/// looked fine. That is a large part of "the notes are mediocre".
///
/// MUTATION PROBE: with the old `(ctx - 800) * 3` these fail at both context sizes.
/// </summary>
public sealed class ContextBudgetTests
{
    // The two sizes LlmRuntime actually sets: 3072 on partial GPU offload (3.9GB to 7GB VRAM,
    // which is the owner's RTX 3050), 4096 on full offload and on CPU.
    [Theory]
    [InlineData(3072)]
    [InlineData(4096)]
    public void A_window_leaves_room_for_the_instructions_and_the_answer(int ctx)
    {
        var chunkTokens = LlmNoteWriter.MaxChunkChars(ctx) / LlmNoteWriter.ChunkCharsPerToken;

        // What else has to fit alongside the transcript in the same context.
        const int instructions = 500;        // system prompt + the map/final prompt scaffolding
        const int longestGeneration = 760;   // the final pass; the map pass asks for 420

        Assert.True(chunkTokens + instructions + longestGeneration <= ctx,
            $"a window at ctx={ctx} needs {chunkTokens:F0} + {instructions} + {longestGeneration} = " +
            $"{chunkTokens + instructions + longestGeneration:F0} tokens, which overflows the KV cache " +
            "and throws NoKvSlot on every call");
    }

    [Fact]
    public void The_ratio_stays_conservative_because_the_failure_is_asymmetric()
    {
        // A window slightly too SMALL costs a little detail. A window slightly too LARGE throws and
        // costs the whole window and everything said in it. 2.6 was an estimate; one window in 43
        // still overflowed on a real ten-meeting run, so the ratio may tighten but must never drift
        // back up on a hunch.
        Assert.True(LlmNoteWriter.ChunkCharsPerToken <= 2.4,
            "a generous chars-per-token estimate silently deletes whole windows of a meeting");
    }

    [Fact]
    public void The_budget_never_collapses_to_nothing()
    {
        // A tiny or misreported context must still produce a usable window rather than 0, which
        // would chunk the transcript one segment at a time and map-reduce forever.
        Assert.True(LlmNoteWriter.MaxChunkChars(512) >= 1200);
        Assert.True(LlmNoteWriter.MaxChunkChars(0) >= 1200);
    }

    [Fact]
    public void A_bigger_context_buys_a_bigger_window()
    {
        Assert.True(LlmNoteWriter.MaxChunkChars(4096) > LlmNoteWriter.MaxChunkChars(3072));
    }
}
