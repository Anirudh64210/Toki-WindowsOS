using System.Collections.Generic;
using System.Linq;
using Toki.Llm;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// PROVENANCE MUST SURVIVE RE-REDUCTION.
///
/// When the digests themselves overflow the final pass's context, they are mapped again. That step
/// used to call `provenance.Clear()`, reasoning that a digest of digests cannot be traced without
/// guessing. Right about guessing, wrong about the alternative: a re-digest covers a KNOWN set of
/// first-round windows, so the UNION of their ranges is observed, not inferred.
///
/// This mattered the moment the window budget shrank to fix the NoKvSlot overflow: smaller windows
/// make this loop run far more often. Measured 2026-09-03, coverage fell from 20/30 bullets to 8/44
/// purely because re-reduction ran on three of six meetings and wiped every range.
///
/// MUTATION PROBE: with GroupWindows returning ranges of -1, or the union taken over unverified
/// windows too, these fail.
/// </summary>
public sealed class ReReduceProvenanceTests
{
    static LlmNoteWriter.Window W(string text, int first, int last) => new(text, first, last);

    [Fact]
    public void A_group_carries_the_union_of_the_ranges_it_merges()
    {
        var groups = LlmNoteWriter.GroupWindows([W("aaa", 0, 9), W("bbb", 10, 19)], 1000);

        Assert.Single(groups);
        Assert.Equal(0, groups[0].First);
        Assert.Equal(19, groups[0].Last);
        Assert.Contains("aaa", groups[0].Text);
        Assert.Contains("bbb", groups[0].Text);
    }

    [Fact]
    public void An_unverified_window_contributes_text_but_never_widens_the_range()
    {
        // -1 marks "kept, provenance unknown". Letting it widen the range would manufacture a claim
        // about lines nothing verified, which is exactly the guessing this design refuses.
        var groups = LlmNoteWriter.GroupWindows([W("verified", 5, 8), W("uncited", -1, -1)], 1000);

        Assert.Single(groups);
        Assert.Equal(5, groups[0].First);
        Assert.Equal(8, groups[0].Last);
        Assert.Contains("uncited", groups[0].Text);
    }

    [Fact]
    public void A_group_with_nothing_verified_stays_unknown()
    {
        var groups = LlmNoteWriter.GroupWindows([W("one", -1, -1), W("two", -1, -1)], 1000);

        Assert.Single(groups);
        Assert.Equal(-1, groups[0].First);
    }

    [Fact]
    public void Groups_split_on_the_character_budget()
    {
        var parts = new List<LlmNoteWriter.Window>
        {
            W(new string('a', 40), 0, 4),
            W(new string('b', 40), 5, 9),
            W(new string('c', 40), 10, 14),
        };
        var groups = LlmNoteWriter.GroupWindows(parts, 90);

        Assert.True(groups.Count > 1, "a 120-char payload must not fit in one 90-char group");
        foreach (var g in groups) Assert.True(g.Text.Length <= 90, $"group of {g.Text.Length} exceeds the budget");
        // Every group that holds verified text reports a real range, and the ranges stay ordered.
        var known = groups.Where(g => g.First >= 0).ToList();
        Assert.NotEmpty(known);
        foreach (var g in known) Assert.True(g.Last >= g.First);
    }

    [Fact]
    public void Nothing_in_gives_nothing_out()
    {
        Assert.Empty(LlmNoteWriter.GroupWindows([], 100));
    }
}
