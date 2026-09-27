using Toki.Llm;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// HOW MUCH OF THE MODEL SITS ON THE GPU IS THE WHOLE SUMMARIZE WAIT.
///
/// Generation, not prompt reading, is where the time goes: instrumented 2026-09-04, a 12-token pass
/// over a 3.4k-char prompt costs 2-3s while a 430-token pass costs 20-58s on the same prompt size.
/// Generation speed tracks the share of the model on the GPU almost linearly.
///
/// The 4GB tier offloaded 20 layers. That number was never timed - it was a defensive guess against
/// an uncatchable native OOM, and it left two thirds of the card unclaimed. Replaying one stored
/// meeting on an RTX 3050 4GB, total model time for the whole note:
///
///     20 layers  121.8s      28 layers  75.7s      32 layers  45.8s
///     34 layers  47.5s / 48.7s          all layers 44.7s
///
/// 32 is the knee: 2.7x faster than the guess, within 3% of full offload, and it still leaves over a
/// gigabyte for the desktop and the video call this GPU is also driving. Full offload is refused on
/// purpose - the last 3% is not worth a crash that cannot be caught.
///
/// MUTATION PROBE: the old (20, 4096) tier fails Offloads_enough_of_the_model_to_matter, and any
/// change to full offload on a 4GB card fails Leaves_headroom_on_a_small_card.
/// </summary>
public sealed class GpuTierTests
{
    [Fact]
    public void Offloads_enough_of_the_model_to_matter()
    {
        var (layers, _, _) = LlmRuntime.Tier(4.0);
        // 28 was measured at 75.7s and 32 at 45.8s. Anything at or below the old 20 puts the wait
        // back over two minutes, which is what the owner reported as "Toki is never that late".
        Assert.True(layers >= 28, $"a 4GB card offloads only {layers} layers - the summarize wait roughly doubles");
    }

    [Fact]
    public void A_small_card_stops_short_of_the_whole_model()
    {
        var (layers, _, _) = LlmRuntime.Tier(4.0);
        // This assertion has been WRONG TWICE TODAY, in both directions, and the history is the
        // point. First it capped a 4GB card at 34 on a single run that put full offload 3% ahead;
        // repeating properly (3 runs, medians) showed 33%, so it was changed to demand 999. Then the
        // allocator's own numbers settled it: llama.cpp reports "3370 MiB free" and at 37/37 layers
        // allocates model 2375.91 + KV 576.00 + compute 301.75 + host 18.01 = ~3254 MiB. 116 MiB is
        // not a margin, and a native VRAM OOM is an uncatchable crash that drops the user onto the
        // heuristic floor.
        //
        // What makes 32 affordable is the SELECT map, not a tolerance for slowness: the longest
        // meeting in the Library runs 35.9s at 32 layers and 24.7s at 37. Both are under target, so
        // the extra 11s buys nothing worth a crash. Raise this ONLY behind real free-VRAM gating.
        Assert.InRange(layers, 28, 34);
    }

    [Fact]
    public void The_kv_cache_stays_unquantised()
    {
        // MEASURED, against the arithmetic. q8_0 for K alone is documented as nearly free (KLD
        // 0.00094) and halving the cache is what would buy room for a parallel batch. On this Vulkan
        // build it measured 99s against 28.9s - 2.3x SLOWER. The fused attention path falls off onto
        // something far worse. This test exists so the next person to read the KLD table and reach
        // for it has to read that first.
        Assert.Equal(LLama.Native.GGMLType.GGML_TYPE_F16, LlmRuntime.KvTypeK);
        Assert.Equal(LLama.Native.GGMLType.GGML_TYPE_F16, LlmRuntime.KvTypeV);
    }

    [Fact]
    public void A_big_card_still_takes_everything()
    {
        var (layers, ctx, _) = LlmRuntime.Tier(8.0);
        Assert.Equal(999, layers);
        Assert.Equal(4096u, ctx);
    }

    [Fact]
    public void No_gpu_falls_back_to_cpu_with_a_full_context()
    {
        var (layers, ctx, why) = LlmRuntime.Tier(0);
        Assert.Equal(0, layers);
        // The context must NOT shrink on the CPU path: MaxChunkChars is derived from it, and a
        // smaller window is what sent long meetings to the heuristic floor in the first place.
        Assert.Equal(4096u, ctx);
        Assert.Contains("no GPU", why);
    }

    [Fact]
    public void A_card_too_small_to_offload_safely_uses_the_cpu()
    {
        var (layers, _, why) = LlmRuntime.Tier(2.0);
        Assert.Equal(0, layers);
        Assert.Contains("too small", why);
    }

    [Fact]
    public void Every_tier_keeps_the_context_that_the_chunker_assumes()
    {
        // MaxChunkChars(ctx) is what stops NoKvSlot. A tier that quietly lowered ctx would shrink the
        // map window and reintroduce the re-reduction that guts a note.
        foreach (var gb in new[] { 0.0, 2.0, 4.0, 8.0, 24.0 })
            Assert.Equal(4096u, LlmRuntime.Tier(gb).Ctx);
    }
}
