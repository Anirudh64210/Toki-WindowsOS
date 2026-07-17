using Toki.Storage;
using Toki.Transcription;

namespace Toki.Llm;

/// <summary>
/// App-level LLM notes: picks the local Qwen model (or a BYO API key, per
/// settings) and produces an upgraded <see cref="NoteSet"/> from a transcript.
/// Owns the heavy local model — loaded once, lazily, warmed at launch. Degrades
/// to null (caller keeps the heuristic notes) whenever no model is available.
/// </summary>
public sealed class LlmService
{
    static readonly Lazy<LlmService> Lazy = new(() => new LlmService());
    public static LlmService Instance => Lazy.Value;

    readonly object _gate = new();
    ILlm? _local;
    bool _localFailed;

    LlmService() { }

    public static bool LocalModelInstalled => ModelManager.QwenInstalled;
    public bool LocalLoaded => _local is not null;

    /// <summary>The active model per settings: BYO API key if set, else local Qwen if installed.</summary>
    public ILlm? Active()
    {
        var s = AppSettings.Current;
        if (s.NotesMode == NotesMode.ApiKey && !string.IsNullOrWhiteSpace(s.ApiKey))
            return new OpenAiLlm(s.ApiKey!, s.ApiModel, s.ApiBaseUrl);
        if (s.NotesMode == NotesMode.HeuristicOnly) return null;
        return EnsureLocal();
    }

    ILlm? EnsureLocal()
    {
        if (_local is not null) return _local;
        if (_localFailed || !ModelManager.QwenInstalled) return null;
        // On a small-RAM PC, free the ~630MB recognizer before loading the 2.5GB LLM —
        // both resident is what crashes/pages the machine. The recognizer reloads for
        // the next meeting.
        if (App.LowMemoryMode) TranscriptionService.Instance.Release();
        lock (_gate)
        {
            if (_local is not null) return _local;
            try
            {
                var llama = new LlamaLlm(ModelManager.QwenPath);
                _local = llama;
                App.Log($"LLM loaded on {(llama.OnGpu ? "GPU" : "CPU")}");
            }
            catch (Exception ex) { App.Log($"LLM load failed: {ex.Message}"); _localFailed = true; }
            return _local;
        }
    }

    /// <summary>Load the local model ahead of the first meeting (lesson 2), off the UI thread.</summary>
    public void Warm()
    {
        if (AppSettings.Current.NotesMode == NotesMode.Local && ModelManager.QwenInstalled)
            System.Threading.Tasks.Task.Run(() => EnsureLocal());
    }

    /// <summary>
    /// Warm synchronously on the caller's thread — used by the gentle low-priority
    /// startup warmer so the model load inherits that below-normal priority instead
    /// of running on a normal-priority thread-pool thread.
    /// </summary>
    public void WarmBlocking()
    {
        if (AppSettings.Current.NotesMode == NotesMode.Local && ModelManager.QwenInstalled)
            EnsureLocal();
    }

    /// <summary>
    /// Free the local model (~2.5GB). Used in low-memory mode so the recognizer can run
    /// without the two models fighting over RAM, and so idle Toki isn't holding the big
    /// model. It reloads on demand for the next set of notes.
    /// </summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_local is IDisposable d) { try { d.Dispose(); } catch { } }
            if (_local is not null) App.Log("LLM released (low-memory)");
            _local = null;
            _localFailed = false;
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    /// <summary>Write notes with the active model, or null to keep the heuristic notes.</summary>
    public async Task<NoteSet?> WriteNotesAsync(IReadOnlyList<TranscriptSegment> transcript, CancellationToken ct = default)
    {
        var llm = Active();
        if (llm is null || !llm.Ready) return null;
        try { return await new LlmNoteWriter(llm).WriteAsync(transcript, ct); }
        catch (Exception ex) { App.Log($"LLM notes failed (non-fatal): {ex.Message}"); return null; }
    }
}
