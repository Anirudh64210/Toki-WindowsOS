using System.IO;
using System.Net.Http;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Toki.Transcription;

/// <summary>Identifies an on-device model, where it downloads from, and how to find its files once extracted.</summary>
public sealed record ModelSpec(string Id, string Url, string Folder, string[] RequiredFiles);

/// <summary>
/// Downloads and unpacks on-device models into %APPDATA%\Toki\Models\. Models are
/// fetched during onboarding (never mid-meeting, lesson 2); this is the machinery.
/// </summary>
public sealed class ModelManager
{
    public static readonly ModelSpec Parakeet = new(
        Id: "parakeet-tdt-0.6b-v2-int8",
        Url: "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2",
        Folder: "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8",
        RequiredFiles: ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"]);

    /// <summary>
    /// Whisper base.en (OpenAI, MIT) as ONNX, run through the SAME sherpa-onnx runtime already here -
    /// no second native library, no Microsoft.ML.OnnxRuntime conflict. It is the ASK-BY-VOICE decoder
    /// ONLY: whisper pads every clip to its 30s mel window, so it is robust on the 1-2 word push-to-talk
    /// clips where Parakeet (great on whole meetings) collapses "Hi Toki" to "Yeah". Parakeet stays the
    /// meeting engine. int8 weights (~160MB on disk); the archive also carries fp32 we don't load.
    /// </summary>
    public static readonly ModelSpec Whisper = new(
        Id: "whisper-base.en",
        Url: "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-base.en.tar.bz2",
        Folder: "sherpa-onnx-whisper-base.en",
        RequiredFiles: ["base.en-encoder.int8.onnx", "base.en-decoder.int8.onnx", "base.en-tokens.txt"]);

    public static bool WhisperInstalled => IsInstalled(Whisper);
    public static Task EnsureWhisperAsync(IProgress<double>? progress = null, CancellationToken ct = default) =>
        EnsureAsync(Whisper, progress, ct);

    /// <summary>
    /// Streaming zipformer-transducer (20M, English, k2-fsa, Apache-2.0) as ONNX - the LIVE-PREVIEW
    /// decoder. It emits words as they are spoken (~1s latency) so the recording view fills instantly,
    /// instead of the ~15-21s that the offline Parakeet WINDOW takes. It is PREVIEW ONLY and disposable:
    /// the SAVED transcript is still the accurate whole-file/Parakeet pass. Tiny (int8 encoder ~43MB),
    /// cheap enough to run beside Parakeet during a recording; same sherpa-onnx runtime, no new native lib.
    /// </summary>
    public static readonly ModelSpec StreamingAsr = new(
        Id: "streaming-zipformer-en-20M",
        Url: "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-streaming-zipformer-en-20M-2023-02-17.tar.bz2",
        Folder: "sherpa-onnx-streaming-zipformer-en-20M-2023-02-17",
        RequiredFiles: ["encoder-epoch-99-avg-1.int8.onnx", "decoder-epoch-99-avg-1.int8.onnx",
                        "joiner-epoch-99-avg-1.int8.onnx", "tokens.txt"]);

    public static bool StreamingAsrInstalled => IsInstalled(StreamingAsr);
    public static Task EnsureStreamingAsrAsync(IProgress<double>? progress = null, CancellationToken ct = default) =>
        EnsureAsync(StreamingAsr, progress, ct);

    /// <summary>
    /// Speaker segmentation (pyannote 3.0, MIT, (c) 2022 CNRS): WHO is talking WHEN. The exact
    /// filename is pinned here and nowhere else: the same GitHub release also carries
    /// sherpa-onnx-reverb-diarization-v1/v2 under Rev's NON-PRODUCTION licence - cannot ship.
    /// </summary>
    public static readonly ModelSpec Segmentation = new(
        Id: "pyannote-segmentation-3-0",
        Url: "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2",
        Folder: "sherpa-onnx-pyannote-segmentation-3-0",
        RequiredFiles: ["model.onnx"]);

    /// <summary>Speaker embedding (3D-Speaker CAM++, Apache-2.0 on both code and weights - the cleanest
    /// licence position available). Turns a slice of speech into a voice fingerprint, which is what makes
    /// "the same person again" answerable.</summary>
    public const string SpeakerEmbedUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx";
    public static string SpeakerEmbedPath =>
        Path.Combine(ModelsRoot, "3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx");
    public static bool SpeakerEmbedInstalled => File.Exists(SpeakerEmbedPath);
    public static Task EnsureSpeakerEmbedAsync(IProgress<double>? progress = null, CancellationToken ct = default) =>
        EnsureFileAsync(SpeakerEmbedUrl, SpeakerEmbedPath, progress, ct);

    /// <summary>Both pieces present, so speakers can actually be told apart.</summary>
    public static bool DiarisationInstalled => IsInstalled(Segmentation) && SpeakerEmbedInstalled;

    public static string ModelsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toki", "Models");

    // Notes LLM: Qwen3-4B-Instruct-2507, Q4_K_M GGUF (~2.5GB, single file).
    public const string QwenUrl =
        "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf";
    public static string QwenPath => Path.Combine(ModelsRoot, "Qwen3-4B-Instruct-2507-Q4_K_M.gguf");
    public static bool QwenInstalled => File.Exists(QwenPath);
    public static Task EnsureQwenAsync(IProgress<double>? progress = null, CancellationToken ct = default) =>
        EnsureFileAsync(QwenUrl, QwenPath, progress, ct);

    // Meaning-search embedder: bge-small-en-v1.5 (MIT), Q8_0 GGUF (~34MB, single file). Runs on CPU via
    // LLamaSharp - no second inference runtime - so the GPU stays free for Qwen. Optional: when absent,
    // retrieval falls back to keyword/FTS only.
    public const string EmbedUrl =
        "https://huggingface.co/CompendiumLabs/bge-small-en-v1.5-gguf/resolve/main/bge-small-en-v1.5-q8_0.gguf";
    public static string EmbedPath => Path.Combine(ModelsRoot, "bge-small-en-v1.5-q8_0.gguf");
    public static bool EmbedInstalled => File.Exists(EmbedPath);
    public static Task EnsureEmbedAsync(IProgress<double>? progress = null, CancellationToken ct = default) =>
        EnsureFileAsync(EmbedUrl, EmbedPath, progress, ct);

    /// <summary>
    /// Voice activity detection: Silero VAD v5 (MIT), ~2MB. Gates ASR so Parakeet is never fed
    /// near-silence (it returns confident nonsense, not nothing). Runs through the sherpa-onnx
    /// runtime already shipping here - no second ONNX runtime (Microsoft.ML.OnnxRuntime conflicts).
    /// </summary>
    public const string VadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx";
    public static string VadPath => Path.Combine(ModelsRoot, "silero_vad.onnx");
    public static bool VadInstalled => File.Exists(VadPath);
    public static Task EnsureVadAsync(IProgress<double>? progress = null, CancellationToken ct = default) =>
        EnsureFileAsync(VadUrl, VadPath, progress, ct);

    /// <summary>Absolute path to a file inside an installed model (e.g. the encoder).</summary>
    public static string PathTo(ModelSpec spec, string file) =>
        Path.Combine(ModelsRoot, spec.Folder, file);

    public static bool IsInstalled(ModelSpec spec) =>
        spec.RequiredFiles.All(f => File.Exists(PathTo(spec, f)));

    /// <summary>Download + extract if not already present. Reports 0..1 progress.</summary>
    public static async Task EnsureAsync(ModelSpec spec, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (IsInstalled(spec)) { progress?.Report(1); return; }
        Directory.CreateDirectory(ModelsRoot);
        var archive = Path.Combine(ModelsRoot, spec.Id + ".tar.bz2");

        await DownloadAsync(spec.Url, archive, progress, ct).ConfigureAwait(false);
        Extract(archive, ModelsRoot);
        try { File.Delete(archive); } catch { /* leave the archive if locked */ }

        if (!IsInstalled(spec))
            throw new FileNotFoundException($"Model '{spec.Id}' still missing required files after extract.");
    }

    /// <summary>Download a single file (e.g. a GGUF) to <paramref name="destPath"/> if missing.</summary>
    public static async Task EnsureFileAsync(string url, string destPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (File.Exists(destPath)) { progress?.Report(1); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var tmp = destPath + ".part";
        await DownloadAsync(url, tmp, progress, ct).ConfigureAwait(false);
        File.Move(tmp, destPath, overwrite: true);
    }

    static async Task DownloadAsync(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(dest);
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total > 0) progress?.Report(Math.Min(0.99, (double)done / total));
        }
        progress?.Report(1);
    }

    static void Extract(string archivePath, string destDir)
    {
        using var stream = File.OpenRead(archivePath);
        using var reader = ReaderFactory.OpenReader(stream); // auto-detects tar.bz2 (renamed from Open in SharpCompress 0.48)
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory) continue;
            reader.WriteEntryToDirectory(destDir, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true,
            });
        }
    }
}
