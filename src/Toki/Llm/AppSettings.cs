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

    // Model + base URL are trimmed on set — pasted values arrive with stray whitespace,
    // and a trailing space in a URL or model name fails in confusing ways downstream.
    string _apiModel = "gpt-4o-mini";
    public string ApiModel { get => _apiModel; set => _apiModel = (value ?? "").Trim(); }

    string _apiBaseUrl = "https://api.openai.com/";
    public string ApiBaseUrl { get => _apiBaseUrl; set => _apiBaseUrl = (value ?? "").Trim(); }

    /// <summary>Display name for the greeting ("Welcome back, X"). Set during onboarding;
    /// falls back to the Windows account name when empty.</summary>
    public string? UserName { get; set; }

    /// <summary>Best available first name for the greeting: the configured <see cref="UserName"/>,
    /// else a cleaned Windows account name, else "" (caller shows a nameless greeting).</summary>
    [JsonIgnore]
    public string DisplayName => ResolveDisplayName(UserName);

    static string ResolveDisplayName(string? configured)
    {
        var raw = !string.IsNullOrWhiteSpace(configured) ? configured! : Environment.UserName;
        if (string.IsNullOrWhiteSpace(raw)) return "";
        // Windows account names come as "First Last" or "first.last" — take the first
        // token and title-case it so "sid.desai" → "Sid".
        var first = raw.Replace('.', ' ').Replace('_', ' ').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts ? parts[0] : "";
        if (first.Length == 0) return "";
        return char.ToUpperInvariant(first[0]) + (first.Length > 1 ? first[1..].ToLowerInvariant() : "");
    }

    /// <summary>Local-model GPU offload policy. 0 = auto (detect VRAM and offload a
    /// safe number of layers — the default); -1 = force CPU only; &gt;0 = force that
    /// many layers. Resolved once at launch by <see cref="LlmRuntime"/>.</summary>
    public int GpuLayers { get; set; } = 0;

    /// <summary>DPAPI-encrypted API key (base64). Use <see cref="ApiKey"/> to read/write in the clear.</summary>
    public string? ApiKeyProtected { get; set; }

    /// <summary>True only after a successful "Test connection" — i.e. the key/model/endpoint
    /// were actually confirmed to reach the provider and get a reply. Reset to false whenever
    /// any of the three fields change (a fresh edit is unverified until re-tested). Drives the
    /// positive/working state in Settings, so we never claim a key is working on faith.</summary>
    public bool ApiVerified { get; set; }

    [JsonIgnore]
    public string? ApiKey
    {
        get => Unprotect(ApiKeyProtected);
        set => ApiKeyProtected = Protect(value?.Trim());   // trimmed: pasted keys carry stray whitespace
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
