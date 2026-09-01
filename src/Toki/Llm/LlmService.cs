using Toki.Storage;
using Toki.Transcription;

namespace Toki.Llm;

/// <summary>
/// App-level LLM notes: picks the local Qwen model (or a BYO API key, per
/// settings) and produces an upgraded <see cref="NoteSet"/> from a transcript.
/// Owns the heavy local model - loaded once, lazily, warmed at launch. Degrades
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

    /// <summary>The local model tried to load this session and threw. While true, note-writing
    /// silently falls back to the heuristic floor - the state the fallback banner reports.</summary>
    public bool LocalFailed => _localFailed;

    /// <summary>The load failure was a NATIVE type-initializer crash (the Vulkan/llama.cpp backend).
    /// .NET caches that failure for the whole process, so an in-process retry cannot recover it: the
    /// app has to be restarted. The banner uses this to say "restart" instead of "try again" when a
    /// retry would be futile.</summary>
    public bool LocalPoisoned { get; private set; }

    /// <summary>Clear a stuck load failure and attempt the local model ONE more time, on the calling
    /// (background) thread. Returns true if the model is now loaded. This is the "try again with the
    /// on-device model" action: a transient failure (a busy GPU, a momentary driver hiccup) recovers;
    /// a native poisoning does not (see <see cref="LocalPoisoned"/>).</summary>
    public bool RetryLocal()
    {
        lock (_gate)
        {
            if (_local is not null) return true;
            _localFailed = false;   // let EnsureLocal attempt the load again
        }
        return EnsureLocal() is not null;
    }

    /// <summary>The active model per settings: BYO API key if set, else local Qwen if installed.</summary>
    public ILlm? Active()
    {
        var s = AppSettings.Current;
        if (s.NotesMode == NotesMode.ApiKey)
        {
            // Only hand back the API client when it can actually work; otherwise log why (never
            // the key itself) and degrade one rung, not two: API → local model → heuristic.
            var api = new ApiLlm(s.ApiKey, s.ApiModel, s.ApiBaseUrl);
            if (api.Ready) return api;
            var why = string.IsNullOrWhiteSpace(s.ApiKey) ? "no API key set"
                : ApiLlm.TryBuildEndpoint(s.ApiBaseUrl) is null ? "base URL is not a valid https:// (or http:// loopback) address"
                : "no model name set";
            if (ModelManager.QwenInstalled)
            {
                App.Log($"API notes unavailable: {why} - falling back to the on-device model");
                return EnsureLocal();
            }
            App.Log($"API notes unavailable: {why} - keeping heuristic notes");
            return null;
        }
        if (s.NotesMode == NotesMode.HeuristicOnly) return null;
        return EnsureLocal();
    }

    ILlm? EnsureLocal()
    {
        if (_local is not null) return _local;
        if (_localFailed || !ModelManager.QwenInstalled) return null;
        // On a small-RAM PC, free the ~630MB recognizer before loading the 2.5GB LLM -
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
                _localFailed = false;
                LocalPoisoned = false;
                App.Log($"LLM loaded on {(llama.OnGpu ? "GPU" : "CPU")}");
            }
            catch (Exception ex)
            {
                // Log the WHOLE chain: the top is usually "type initializer threw", and the real
                // cause (a missing native DLL, a Vulkan init failure) is an inner exception.
                var detail = $"{ex.GetType().Name}: {ex.Message}";
                for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                    detail += $"  <-  {inner.GetType().Name}: {inner.Message}";
                App.Log($"LLM load failed: {detail}");
                _localFailed = true;
                // A native type-initializer crash (llama.cpp/Vulkan backend) is cached by .NET for
                // the whole process - retrying in-process throws the same cached exception, so the
                // only real recovery is an app restart. Flag it so the banner says so honestly.
                LocalPoisoned = ex is TypeInitializationException
                    || ex.InnerException is TypeInitializationException
                    || (ex.Message?.Contains("type initializer", StringComparison.OrdinalIgnoreCase) ?? false);
            }
            return _local;
        }
    }

    /// <summary>Load the local model ahead of the first meeting, off the UI thread.</summary>
    public void Warm()
    {
        if (AppSettings.Current.NotesMode == NotesMode.Local && ModelManager.QwenInstalled)
            System.Threading.Tasks.Task.Run(() => EnsureLocal());
    }

    /// <summary>
    /// Warm synchronously on the caller's thread - used by the gentle low-priority
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
        // Detach first (under the gate), dispose OUTSIDE it: DisposeWhenIdle blocks until any
        // in-flight generation finishes, and holding _gate through that wait would deadlock a
        // concurrent EnsureLocal. New callers see _local == null immediately and reload fresh.
        ILlm? doomed;
        lock (_gate)
        {
            doomed = _local;
            _local = null;
            _localFailed = false;
        }
        if (doomed is null) return;
        if (doomed is LlamaLlm llama) llama.DisposeWhenIdle();
        else if (doomed is IDisposable d) { try { d.Dispose(); } catch { } }
        App.Log("LLM released (low-memory)");
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    /// <summary>Write notes with the active model, or null to keep the heuristic notes. Pass
    /// <paramref name="force"/> to write in a specific template (the manual override).
    /// The summary is written from the TRANSCRIPT alone; the user's sticky note is deliberately
    /// not an input - the note must say what was actually said.</summary>
    public async Task<NoteSet?> WriteNotesAsync(IReadOnlyList<TranscriptSegment> transcript,
        CancellationToken ct = default, NoteTemplate? force = null)
    {
        var llm = Active();
        if (llm is null || !llm.Ready) return null;
        force ??= DefaultForced();   // honour the user's "always use X" default when no explicit override
        try { return await new LlmNoteWriter(llm).WriteAsync(transcript, ct, force); }
        catch (Exception ex) { App.Log($"LLM notes failed (non-fatal): {ex.Message}"); return null; }
    }

    static NoteTemplate? DefaultForced()
    {
        var name = AppSettings.Current.DefaultTemplate;
        return string.IsNullOrWhiteSpace(name) ? null
            : NoteTemplates.All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when a model is available to (re)write notes - WITHOUT loading it. (Calling
    /// <see cref="Active"/> would synchronously load the 2.5 GB local model, freezing the caller;
    /// this only checks that a model COULD be used, and the load happens later, off-thread.)</summary>
    public bool CanWrite()
    {
        var s = AppSettings.Current;
        return s.NotesMode switch
        {
            NotesMode.HeuristicOnly => false,
            // API mode can also write via the installed local model (the Active() fallback).
            NotesMode.ApiKey => new ApiLlm(s.ApiKey, s.ApiModel, s.ApiBaseUrl).Ready || ModelManager.QwenInstalled,
            _ => ModelManager.QwenInstalled,   // local: installed = can write (loads on demand during the rewrite)
        };
    }
}
