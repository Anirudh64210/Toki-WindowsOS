using System.IO;

namespace Toki.Transcription;

/// <summary>
/// App-level transcription: turns a finished recording folder (you.wav / them.wav)
/// into one merged, echo-de-duped transcript. Owns the heavy Parakeet recognizer
/// and creates it once, lazily. Degrades gracefully (returns null) if the model
/// isn't installed yet — a recording is never lost, it just isn't transcribed
/// until the model is present (lesson 2).
/// </summary>
public sealed class TranscriptionService
{
    static readonly Lazy<TranscriptionService> Lazy = new(() => new TranscriptionService());
    public static TranscriptionService Instance => Lazy.Value;

    readonly object _gate = new();
    Transcriber? _transcriber;

    public static bool ModelReady => ModelManager.IsInstalled(ModelManager.Parakeet);

    TranscriptionService() { }

    /// <summary>
    /// Transcribe both streams of a recording folder (sequentially — a sherpa-onnx
    /// offline recognizer is NOT safe to run concurrently, even as two instances:
    /// concurrent decode produced non-deterministic, corrupt transcripts in testing,
    /// so correctness wins over the ~15% wall-clock a parallel run would save) and
    /// merge them. Returns null if the model isn't available. Background-thread safe.
    /// </summary>
    public IReadOnlyList<TranscriptSegment>? Process(string sessionFolder)
    {
        if (!ModelReady)
        {
            App.Log("transcription skipped: Parakeet model not installed");
            return null;
        }

        // On a small-RAM PC the 2.5GB notes LLM cannot be resident while the recognizer
        // decodes a long meeting — that's the native OOM crash. Free it first so the
        // recognizer has room; the LLM reloads on demand after transcription.
        if (App.LowMemoryMode) Toki.Llm.LlmService.Instance.Release();

        lock (_gate)
        {
            try { _transcriber ??= new Transcriber(); }
            catch (Exception ex) { App.Log($"transcriber init failed: {ex.Message}"); return null; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var you = TranscribeStream(Path.Combine(sessionFolder, "you.wav"), Speaker.You);
            var them = TranscribeStream(Path.Combine(sessionFolder, "them.wav"), Speaker.Them);
            var merged = TranscriptMerger.Merge(you, them);
            App.Log($"transcribed in {sw.ElapsedMilliseconds} ms: you={you.Count} them={them.Count} -> merged {merged.Count} segments");
            return merged;
        }
    }

    IReadOnlyList<TranscriptSegment> TranscribeStream(string wavPath, Speaker speaker)
    {
        if (!File.Exists(wavPath)) return [];
        try
        {
            var samples = AudioLoader.To16kMono(wavPath);
            return _transcriber!.Transcribe(samples, speaker);
        }
        catch (Exception ex)
        {
            // Parakeet errored (not silence) — this is where the whisper.cpp
            // fallback will run (lesson 4). For now, log and skip this stream.
            App.Log($"Parakeet error on {speaker} stream: {ex.Message} (whisper fallback: TODO)");
            return [];
        }
    }

    /// <summary>Load the recognizer ahead of the first meeting (lesson 2: warm at launch).</summary>
    public void Warm()
    {
        if (!ModelReady) return;
        lock (_gate)
        {
            try { _transcriber ??= new Transcriber(); }
            catch (Exception ex) { App.Log($"transcriber warm failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Free the recognizer (~630MB). Used in low-memory mode so the notes LLM can load
    /// without the two models fighting over RAM. The next transcription reloads it.
    /// </summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_transcriber is null) return;
            try { _transcriber.Dispose(); } catch { }
            _transcriber = null;
            App.Log("ASR recognizer released (low-memory)");
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
