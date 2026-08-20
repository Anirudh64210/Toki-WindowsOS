using LLama;
using LLama.Common;
using LLama.Native;
using Toki.Transcription;

namespace Toki.Llm;

/// <summary>
/// Text embeddings via bge-small-en-v1.5 (GGUF) through LLamaSharp, CPU only. Production retrieval
/// does NOT use this yet: in-process loading conflicts with the sherpa-onnx native backend (see
/// ARCHITECTURE); it ships for --embedtest and as the seam for that work. Optional by design:
/// if the model isn't installed, <see cref="Ready"/> is false and retrieval falls back to
/// keyword/FTS. One shared instance; generations serialized (a llama context isn't concurrent).
/// </summary>
public sealed class EmbeddingService : IDisposable
{
    static readonly Lazy<EmbeddingService> _lazy = new(() => new EmbeddingService());
    public static EmbeddingService Instance => _lazy.Value;

    readonly SemaphoreSlim _gate = new(1, 1);
    LLamaWeights? _weights;
    LLamaEmbedder? _embedder;
    bool _tried;

    public bool Ready => Ensure();

    public int Dim { get; private set; }

    EmbeddingService() { }

    readonly object _loadGate = new();

    bool Ensure()
    {
        if (_embedder is not null) return true;
        if (_tried) return false;
        lock (_loadGate)
        {
        if (_embedder is not null) return true;
        if (_tried) return false;
        _tried = true;
        try
        {
            if (!ModelManager.EmbedInstalled) return false;
            // Embedding mode + mean pooling; CPU only (GpuLayerCount=0) so it never competes with Qwen's VRAM.
            var p = new ModelParams(ModelManager.EmbedPath)
            {
                Embeddings = true,
                PoolingType = LLamaPoolingType.Mean,
                ContextSize = 512,
                GpuLayerCount = 0,
                BatchSize = 512,
            };
            _weights = LLamaWeights.LoadFromFile(p);
            _embedder = new LLamaEmbedder(_weights, p);
            App.Log("embedder: bge-small loaded (CPU)");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"embedder load failed (falling back to keyword search): {ex.Message}");
            _embedder = null;
            return false;
        }
        }   // _loadGate: check-and-load is atomic so concurrent callers can't double-load the
            // native weights or see a spurious permanent "not ready" from a half-set _tried.
    }

    /// <summary>Embed one text → a unit-normalized vector, or null if the embedder isn't available.</summary>
    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (!Ensure() || string.IsNullOrWhiteSpace(text)) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _embedder!.GetEmbeddings(text, ct).ConfigureAwait(false);
            var vec = result.FirstOrDefault();
            if (vec is null || vec.Length == 0) return null;
            Dim = vec.Length;
            return Normalize(vec);
        }
        catch (Exception ex) { App.Log($"embed failed: {ex.Message}"); return null; }
        finally { _gate.Release(); }
    }

    /// <summary>Cosine similarity of two unit vectors == dot product.</summary>
    public static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0f;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }

    static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * (double)x;
        var norm = Math.Sqrt(sum);
        if (norm < 1e-8) return v;
        var inv = (float)(1.0 / norm);
        for (var i = 0; i < v.Length; i++) v[i] *= inv;
        return v;
    }

    public void Dispose()
    {
        _embedder?.Dispose();
        _weights?.Dispose();
        _gate.Dispose();
    }
}
