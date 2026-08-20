using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Toki.Llm;

/// <summary>
/// On-device notes LLM: Qwen3-4B-Instruct via LLamaSharp (llama.cpp). Weights are
/// heavy (~2.5GB) - loaded once and reused. Tries GPU offload (Vulkan backend,
/// configured in App) and falls back to CPU if the GPU can't allocate. Generations
/// are serialized (a llama context isn't concurrent).
///
/// Sampler params are Qwen3's official model-card values; NEVER greedy (greedy makes
/// 4-bit Qwen loop/degenerate).
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
        // Leave two cores free: llama.cpp's default takes every core, which starves the WPF
        // render thread and visibly hitches the notch's loader while a generation runs.
        var threads = Math.Max(1, Environment.ProcessorCount - 2);
        var p = new ModelParams(modelPath)
        {
            ContextSize = LlmRuntime.ContextSize, GpuLayerCount = gpuLayers,
            Threads = threads, BatchThreads = threads,
        };
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
        // Serialize generations (a llama context isn't concurrent) - hold the gate across the whole stream.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Build the exact Qwen3 <|im_start|>…<|im_end|> prompt from the GGUF's own template.
            var template = new LLamaTemplate(_model) { AddAssistant = true };
            template.Add("system", system);
            template.Add("user", user);
            var prompt = Encoding.UTF8.GetString(template.Apply());

            // Caller-supplied stop sequences ride as extra anti-prompts (llama.cpp stops when the
            // accumulated output ends with one) - a clean stop beats generating long then trimming.
            var anti = new List<string> { "<|im_end|>", "<|im_start|>" };
            if (stop is not null) anti.AddRange(stop);

            var infer = new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = anti,
                // Qwen3-4B-Instruct-2507 model-card values. MinP=0 explicit (LLamaSharp defaults to
                // 0.1); presence 0.5 keeps repetition mild (high values cause language mixing). NEVER greedy.
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

    /// <summary>Tear down only once no generation is running. Freeing the native weights under
    /// an active InferAsync is a llama.cpp use-after-free - an uncatchable AccessViolation that
    /// takes the whole app down. Acquire the generation gate first (bounded); on timeout keep
    /// the model alive (a leak beats a crash), and dispose the semaphore LAST so a streamer's
    /// finally-release can never hit a disposed gate.</summary>
    public void DisposeWhenIdle()
    {
        if (!_gate.Wait(TimeSpan.FromSeconds(30)))
        {
            App.Log("LLM release skipped - a generation is still running");
            return;
        }
        try { _model.Dispose(); }
        finally { _gate.Dispose(); }
    }

    public void Dispose() => DisposeWhenIdle();
}
