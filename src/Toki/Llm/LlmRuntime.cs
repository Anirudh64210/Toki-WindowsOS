using Microsoft.Win32;

namespace Toki.Llm;

/// <summary>
/// Decides - ONCE, before any llama.cpp call - how to run the local model on THIS
/// machine: how many layers to put on the GPU and how big a context to allocate.
///
/// Why this matters: enabling the Vulkan backend but offloading 0 layers is the
/// worst case (llama.cpp shuffles every layer CPU↔GPU - hundreds of graph splits,
/// painfully slow). So GPU and CPU must be an all-or-partial decision made up front:
/// the Vulkan backend is loaded ONLY when we actually intend to offload.
///
/// VRAM is read from the driver's registry entry (no COM interop). Offload is sized
/// to fit with headroom so it can't hit the uncatchable native OOM crash. A small or
/// absent GPU falls back to a clean CPU path.
/// </summary>
public static class LlmRuntime
{
    public static int GpuLayers { get; private set; }
    public static uint ContextSize { get; private set; } = 4096;
    public static bool UseVulkan => GpuLayers > 0;
    public static string Reason { get; private set; } = "unresolved";

    public static void Resolve()
    {
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

        var gb = DetectVramBytes() / (1024.0 * 1024 * 1024);
        if (gb >= 7.0) { GpuLayers = 999; ContextSize = 4096; Reason = $"{gb:0.0}GB VRAM → full GPU offload"; }
        else if (gb >= 3.9) { GpuLayers = 20; ContextSize = 3072; Reason = $"{gb:0.0}GB VRAM → partial GPU (20 layers)"; }
        else { GpuLayers = 0; ContextSize = 4096; Reason = gb > 0.1 ? $"{gb:0.0}GB VRAM → CPU (too small to offload safely)" : "no GPU detected → CPU"; }
    }

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
