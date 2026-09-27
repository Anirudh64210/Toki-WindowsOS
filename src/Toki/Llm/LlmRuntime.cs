using Microsoft.Win32;

namespace Toki.Llm;

/// <summary>
/// Decides once, before any llama.cpp call, how many layers go on the GPU and the context size.
/// The Vulkan backend is loaded ONLY when we intend to offload (Vulkan with 0 layers shuffles
/// every layer CPU-GPU, the worst case). Offload is sized with headroom because native OOM is an
/// uncatchable crash; VRAM comes from the driver's registry entry, small/absent GPU = CPU path.
/// </summary>
public static class LlmRuntime
{
    public static int GpuLayers { get; private set; }
    public static uint ContextSize { get; private set; } = 4096;
    public static bool UseVulkan => GpuLayers > 0;
    public static string Reason { get; private set; } = "unresolved";

    /// <summary>KV cache element types, K and V decided SEPARATELY on purpose. Quantising K alone
    /// costs 23% of the cache at KLD 0.00094 and needs nothing else. Quantising V additionally
    /// REQUIRES flash attention, and LLamaSharp exposes FlashAttention as a bool that cannot express
    /// llama.cpp's AUTO - forcing it on a Vulkan build without coopmat2 is a documented prefill
    /// collapse (llama.cpp #12526). So V stays f16 unless a load log confirms FA engaged.
    /// Override with TOKI_KV / TOKI_KV_V = f16|q8_0 for A/B runs.</summary>
    public static LLama.Native.GGMLType KvTypeK { get; private set; } = LLama.Native.GGMLType.GGML_TYPE_F16;
    public static LLama.Native.GGMLType KvTypeV { get; private set; } = LLama.Native.GGMLType.GGML_TYPE_F16;

    static void ResolveKv()
    {
        KvTypeK = ParseKv(Environment.GetEnvironmentVariable("TOKI_KV"));
        KvTypeV = ParseKv(Environment.GetEnvironmentVariable("TOKI_KV_V"));
    }

    static LLama.Native.GGMLType ParseKv(string? v) =>
        v?.Trim().ToLowerInvariant() switch
        {
            "q8_0" or "q8" => LLama.Native.GGMLType.GGML_TYPE_Q8_0,
            "f16" or null or "" => LLama.Native.GGMLType.GGML_TYPE_F16,
            var other => LogUnknown(other),
        };

    static LLama.Native.GGMLType LogUnknown(string v)
    {
        App.Log($"llm runtime: TOKI_KV='{v}' not recognised, using f16");
        return LLama.Native.GGMLType.GGML_TYPE_F16;
    }

    public static void Resolve()
    {
        ResolveKv();
        // Env overrides for tuning/tests: TOKI_GPU_LAYERS, TOKI_CTX.
        var envL = Environment.GetEnvironmentVariable("TOKI_GPU_LAYERS");
        var envC = Environment.GetEnvironmentVariable("TOKI_CTX");
        if (envL is not null && int.TryParse(envL, out var el))
        {
            GpuLayers = el;
            ContextSize = envC is not null && uint.TryParse(envC, out var ec) ? ec : 4096;
            Reason = $"env override (layers={GpuLayers}, ctx={ContextSize})";
            return;
        }

        // Explicit user setting (Settings → GPU): -1 forces CPU, >0 forces that many
        // layers, 0 means auto (the default) - decided from detected VRAM below.
        var user = AppSettings.Current.GpuLayers;
        if (user < 0) { GpuLayers = 0; ContextSize = 4096; Reason = "user setting: CPU only"; return; }
        if (user > 0) { GpuLayers = user; ContextSize = 3072; Reason = $"user setting: {user} layers on GPU"; return; }

        // CONTEXT SIZE IS MEASURED, NOT GUESSED. The partial-offload branch held 3072 defensively.
        // Measured 2026-09-03 on the owner's RTX 3050 (3370 MiB reported free), 20 layers offloaded:
        //
        //                 ctx 3072      ctx 4096
        //   model buffer  1401 MiB      1401 MiB
        //   KV cache       228 MiB       304 MiB
        //   compute        302 MiB       302 MiB
        //   TOTAL         1931 MiB      2007 MiB   -> 1363 MiB still free at 4096
        //
        // 76 MiB buys a 49% bigger map window (4347 to 6489 chars), which means fewer windows, fewer
        // model calls, and far less of the re-reduction step that summarises summaries and guts a
        // note. If a machine ever OOMs at load here, drop this back to 3072 rather than lowering the
        // reserve in MaxChunkChars: the reserve is what stops NoKvSlot.
        //
        // HOW MANY LAYERS IS ALSO MEASURED NOW (2026-09-04). The 20 above was never timed - it was a
        // defensive guess, and it left two thirds of the card unused. Generation, not prompt reading,
        // is the entire wait (a 12-token pass over a 3.4k prompt costs 2-3s; a 430-token pass costs
        // 20-58s), and generation speed tracks the share of the model on the GPU almost linearly.
        //
        // Replaying meeting 128 on the owner's RTX 3050 4GB, total MODEL time for the whole note:
        //
        //   20 layers   121.8s      <- the old guess
        //   28 layers    75.7s
        //   32 layers    ~50s
        //
        // CORRECTED 2026-09-04, same day. The single run above put full offload only 6% ahead of 32,
        // so 32 shipped. Repeating it properly (3 runs per config, medians, meeting 128) shows that
        // reading was noise - the same meeting varies 25% run to run:
        //
        //   32 layers, f16 KV   45.8 / 43.0 / 42.8s  -> 43.0s
        //   ALL layers, f16 KV  28.9 / 29.1 / 28.7s  -> 28.9s   <- chosen, 33% faster
        //   ALL layers, q8_0 K  100.5 / 98.0s        -> ~99s    <- 2.3x SLOWER, see below
        //
        // FULL OFFLOAD IS FASTER AND IS STILL NOT TAKEN. nvidia-smi reports 3965 MiB of 4096 free
        // (an RTX 3050 LAPTOP GPU whose display runs on the AMD iGPU, so nothing else is on it), but
        // llama.cpp is the allocator and IT reports "3370 MiB free". At 37/37 layers it allocates
        // model 2375.91 + KV 576.00 + compute 301.75 + host 18.01 = ~3254 MiB, leaving 116 MiB.
        // That is not a margin, and the failure is an uncatchable crash that drops the user onto the
        // heuristic floor - the worst note Toki can produce.
        //
        // 32 layers with the SELECT map (LlmNoteWriter.MapMode) puts the LONGEST meeting in the
        // Library at 35.9s, well under target, so the risk buys nothing that matters:
        //
        //   meeting 107, 22.3 min:   ngl 32 + select  35.9s      ngl 37 + select  24.7s
        //
        // Revisit only behind real free-VRAM gating (below), never on the timing alone.
        //
        // KV QUANTISATION IS A TRAP ON THIS BACKEND. q8_0 for K alone is documented as nearly free
        // (KLD 0.00094) and it is what would buy the context a parallel batch needs. Measured here it
        // is 2.3x slower - the Vulkan fused-attention path falls off onto something far worse, the
        // same class of collapse as llama.cpp #12526 / #27137. Do not reach for it again on Vulkan
        // without re-measuring; the arithmetic says yes and the hardware says no.
        //
        // STILL THE HONEST LIMIT: a constant cannot know how busy the GPU is RIGHT NOW, and
        // DetectVramBytes reads the card's TOTAL size, never what is free. That is tolerable while
        // the only user is the owner, whose display is on another GPU. It is a SHIP BLOCKER for M8:
        // a 4GB card that IS driving a display cannot take full offload, and a native VRAM OOM is an
        // uncatchable crash, not something to fall back from. Gate this on VK_EXT_memory_budget or
        // DXGI QueryVideoMemoryInfo before this reaches anyone else.
        var gb = DetectVramBytes() / (1024.0 * 1024 * 1024);
        (GpuLayers, ContextSize, Reason) = Tier(gb);
    }

    /// <summary>The offload tier for a card of <paramref name="gb"/> GB. Pure, so the numbers that
    /// decide the whole summarize wait can be asserted instead of only being read.</summary>
    internal static (int Layers, uint Ctx, string Why) Tier(double gb) =>
        gb >= 7.0 ? (999, 4096u, $"{gb:0.0}GB VRAM → full GPU offload")
        : gb >= 3.9 ? (32, 4096u, $"{gb:0.0}GB VRAM → partial GPU (32 layers)")
        : (0, 4096u, gb > 0.1 ? $"{gb:0.0}GB VRAM → CPU (too small to offload safely)" : "no GPU detected → CPU");

    /// <summary>Dedicated VRAM in bytes from the display driver's registry key (0 if unknown).</summary>
    static long DetectVramBytes()
    {
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (baseKey is null) return 0;
            long max = 0;
            foreach (var name in baseKey.GetSubKeyNames())
            {
                if (name.Length != 4 || !char.IsDigit(name[0])) continue; // 0000, 0001, …
                using var k = baseKey.OpenSubKey(name);
                var v = k?.GetValue("HardwareInformation.qwMemorySize");
                long bytes = v switch
                {
                    long l => l,
                    int i => i,
                    byte[] b when b.Length == 8 => BitConverter.ToInt64(b, 0),
                    _ => 0,
                };
                if (bytes > max) max = bytes;
            }
            return max;
        }
        catch { return 0; }
    }
}
