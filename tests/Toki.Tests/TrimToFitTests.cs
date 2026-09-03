using System.Linq;
using Toki.Llm;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// WHEN THE DIGESTS OVERFLOW, SELECT. DO NOT SUMMARISE AGAIN.
///
/// The pipeline used to re-map the digests when they did not fit the final pass, up to two extra
/// rounds. That reintroduced the exact failure the map step exists to prevent: the writer reads a
/// paraphrase of a paraphrase.
///
/// MEASURED 2026-09-03. Meeting 48 mapped cleanly (25/26, 19/21, 29/31, 22/23, 21/22 lines kept
/// across seven windows), then re-reduced once, and the finished note had ZERO bullets with its
/// checkable facts down from 25 to 2. Meeting 28, which did not re-reduce, went from 15% to 62%
/// recall in the same run.
///
/// Selecting is lossy in a way a reader can reason about. Re-summarising is lossy in a way nobody
/// can see.
///
/// MUTATION PROBE: scoring every line equally, or rebuilding in score order rather than reading
/// order, fails these.
/// </summary>
public sealed class TrimToFitTests
{
    static LlmNoteWriter.Window W(string text, int first, int last) => new(text, first, last);

    [Fact]
    public void Lines_carrying_hard_facts_are_kept_over_vague_ones()
    {
        var d = W("The team discussed things at length\nBudget set to 40000 on March 3", 0, 9);

        var kept = LlmNoteWriter.TrimToFit([d], 40);

        var text = string.Join("\n", kept.Select(k => k.Text));
        Assert.Contains("40000", text);
        Assert.DoesNotContain("discussed things", text);
    }

    [Fact]
    public void Survivors_keep_the_exact_provenance_of_their_own_window()
    {
        var a = W("Pricing raised to 19 pounds on Tuesday", 0, 4);
        var b = W("Launch moved to 12 March by Priya", 50, 59);

        var kept = LlmNoteWriter.TrimToFit([a, b], 1000);

        Assert.Equal(2, kept.Count);
        Assert.Equal(0, kept[0].First);
        Assert.Equal(4, kept[0].Last);
        Assert.Equal(50, kept[1].First);
        Assert.Equal(59, kept[1].Last);
    }

    [Fact]
    public void Reading_order_is_restored_after_selection()
    {
        // The writer is handed its evidence in the order the meeting happened. Out of order, it
        // invents transitions to explain the jumps.
        var d = W("First we agreed 5 items\nThen Priya raised 3 risks\nFinally we set 2 dates", 0, 9);

        var kept = LlmNoteWriter.TrimToFit([d], 1000);

        var text = kept[0].Text;
        Assert.True(text.IndexOf("First") < text.IndexOf("Then"), "reading order lost");
        Assert.True(text.IndexOf("Then") < text.IndexOf("Finally"), "reading order lost");
    }

    [Fact]
    public void The_result_fits_the_budget()
    {
        var d = W(string.Join("\n", Enumerable.Range(0, 60).Select(i => $"Line {i} with number {i * 7} on March {1 + i % 28}")), 0, 99);

        var kept = LlmNoteWriter.TrimToFit([d], 300);

        var total = kept.Sum(k => k.Text.Length);
        Assert.True(total <= 300, $"trimmed to {total} chars, over the 300 budget");
        Assert.NotEmpty(kept);
    }

    [Fact]
    public void A_verified_window_outranks_an_uncited_one_when_the_facts_tie()
    {
        // -1 marks a window that came back uncited. Its content is kept when there is room, but when
        // something has to go, the line nothing could verify goes first.
        var verified = W("Ship date is 12 March", 0, 9);
        var uncited = W("Ship date is 12 March", -1, -1);

        var kept = LlmNoteWriter.TrimToFit([uncited, verified], 25);

        Assert.Single(kept);
        Assert.Equal(0, kept[0].First);
    }

    [Fact]
    public void Nothing_in_gives_nothing_out()
    {
        Assert.Empty(LlmNoteWriter.TrimToFit([], 100));
    }
}
