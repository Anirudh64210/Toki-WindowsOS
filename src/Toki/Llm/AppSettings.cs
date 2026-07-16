using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Toki.Llm;

public enum NotesMode { Local, HeuristicOnly, ApiKey }

/// <summary>
/// User settings persisted to %APPDATA%\Toki\settings.json. The BYO API key is
/// DPAPI-encrypted (per-user) so it's never stored in plaintext (brief §4).
/// </summary>
public sealed class AppSettings
{
    public NotesMode NotesMode { get; set; } = NotesMode.Local;
    public string ApiModel { get; set; } = "gpt-4o-mini";
    public string ApiBaseUrl { get; set; } = "https://api.openai.com/";

    /// <summary>GPU layers to offload for the local model. 0 = CPU only (safe on any
    /// machine; can't run out of VRAM). Raise (e.g. 999 = all) only with a 6GB+ GPU.</summary>
    public int GpuLayers { get; set; } = 0;

    /// <summary>DPAPI-encrypted API key (base64). Use <see cref="ApiKey"/> to read/write in the clear.</summary>
    public string? ApiKeyProtected { get; set; }

    [JsonIgnore]
    public string? ApiKey
    {
        get => Unprotect(ApiKeyProtected);
        set => ApiKeyProtected = Protect(value);
    }

    // ---- persistence ----

    static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toki", "settings.json");

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
        }
        catch { /* corrupt settings → defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { App.Log($"settings save failed: {ex.Message}"); }
    }

    static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch { return null; }
    }

    static string? Unprotect(string? protectedB64)
    {
        if (string.IsNullOrEmpty(protectedB64)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedB64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }
}
