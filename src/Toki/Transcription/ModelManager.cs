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

    public static string ModelsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toki", "Models");

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
        using var reader = ReaderFactory.Open(stream); // auto-detects tar.bz2
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
