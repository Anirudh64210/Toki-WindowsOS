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
        // How to run on THIS machine was decided up front (LlmRuntime): a safe GPU
        // offload sized to the detected VRAM, or a clean CPU path. Offload is capped
        // to fit with headroom so it can't hit the uncatchable native OOM crash.
        var gpuLayers = Math.Max(0, LlmRuntime.GpuLayers);
        var p = new ModelParams(modelPath) { ContextSize = LlmRuntime.ContextSize, GpuLayerCount = gpuLayers };
        _model = LLamaWeights.LoadFromFile(p);
        OnGpu = gpuLayers > 0;
        _executor = new StatelessExecutor(_model, p);
    }

    public async Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var tok in StreamAsync(system, user, maxTokens, ct).ConfigureAwait(false))
            sb.Append(tok);
        return sb.ToString().Trim();
    }

    public async IAsyncEnumerable<string> StreamAsync(string system, string user, int maxTokens,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        IReadOnlyList<string>? stop = null)
    {
        // Serialize generations (a llama context isn't concurrent) — hold the gate across the whole stream.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Build the exact Qwen3 <|im_start|>…<|im_end|> prompt from the GGUF's own template.
            var template = new LLamaTemplate(_model) { AddAssistant = true };
            template.Add("system", system);
            template.Add("user", user);
            var prompt = Encoding.UTF8.GetString(template.Apply());

            // Caller-supplied stop sequences ride as extra anti-prompts (llama.cpp stops when the
            // accumulated output ends with one) — a clean stop beats generating long then trimming.
            var anti = new List<string> { "<|im_end|>", "<|im_start|>" };
            if (stop is not null) anti.AddRange(stop);

            var infer = new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = anti,
                // Qwen3-4B-Instruct-2507 model-card "Best Practices" values: T=0.7, TopP=0.8, TopK=20,
                // MinP=0 (explicit — the LLamaSharp default is 0.1), presence penalty in [0,2] against
                // repetition (0.5 keeps it mild; high values cause language mixing). NEVER greedy (lesson 8).
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.7f,
                    TopP = 0.8f,
                    TopK = 20,
                    MinP = 0f,
                    PresencePenalty = 0.5f,
                },
            };

            await foreach (var tok in _executor.InferAsync(prompt, infer, ct).ConfigureAwait(false))
            {
                // Never surface the template's own markers if one slips through mid-stream.
                var piece = tok.Replace("<|im_end|>", "").Replace("<|im_start|>", "");
                if (piece.Length > 0) yield return piece;
            }
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _model.Dispose();
    }
}
