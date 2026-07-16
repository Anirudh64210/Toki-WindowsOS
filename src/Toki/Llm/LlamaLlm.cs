using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Toki.Llm;

/// <summary>
/// On-device notes LLM: Qwen3-4B-Instruct via LLamaSharp (llama.cpp). Weights are
/// heavy (~2.5GB) — loaded once and reused. Tries GPU offload (Vulkan backend,
/// configured in App) and falls back to CPU if the GPU can't allocate. Generations
/// are serialized (a llama context isn't concurrent).
///
/// Sampler params are Qwen3's official values; NEVER greedy (lesson 8: greedy
/// makes 4-bit Qwen loop/degenerate).
/// </summary>
public sealed class LlamaLlm : ILlm, IDisposable
{
    readonly LLamaWeights _model;
    readonly StatelessExecutor _executor;
    readonly SemaphoreSlim _gate = new(1, 1);

    public bool Ready => true;
    public bool OnGpu { get; }

    public LlamaLlm(string modelPath)
    {
        // Default CPU (GpuLayers=0): can never run out of VRAM, so it can't crash
        // llama.cpp natively (which .NET can't catch). Users with a big GPU can
        // opt into offload via settings. Context 4096 is plenty for note chunks
        // and keeps the KV cache / compute buffers small.
        var gpuLayers = Math.Max(0, AppSettings.Current.GpuLayers);
        var p = new ModelParams(modelPath) { ContextSize = 4096, GpuLayerCount = gpuLayers };
        _model = LLamaWeights.LoadFromFile(p);
        OnGpu = gpuLayers > 0;
        _executor = new StatelessExecutor(_model, p);
    }

    public async Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Build the exact Qwen3 <|im_start|>…<|im_end|> prompt from the GGUF's own template.
            var template = new LLamaTemplate(_model) { AddAssistant = true };
            template.Add("system", system);
            template.Add("user", user);
            var prompt = Encoding.UTF8.GetString(template.Apply());

            var infer = new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = ["<|im_end|>", "<|im_start|>"],
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.7f,
                    TopP = 0.8f,
                    TopK = 20,
                    PresencePenalty = 0.5f,
                },
            };

            var sb = new StringBuilder();
            await foreach (var tok in _executor.InferAsync(prompt, infer, ct).ConfigureAwait(false))
                sb.Append(tok);
            return sb.ToString().Replace("<|im_end|>", "").Trim();
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _model.Dispose();
    }
}
