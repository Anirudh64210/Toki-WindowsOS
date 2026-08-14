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

    // Model + base URL are trimmed on set - pasted values arrive with stray whitespace,
    // and a trailing space in a URL or model name fails in confusing ways downstream.
    string _apiModel = "gpt-4o-mini";
    public string ApiModel { get => _apiModel; set => _apiModel = (value ?? "").Trim(); }

    string _apiBaseUrl = "https://api.openai.com/";
    public string ApiBaseUrl { get => _apiBaseUrl; set => _apiBaseUrl = (value ?? "").Trim(); }

    /// <summary>Display name for the greeting ("Welcome back, X"). Set during onboarding;
    /// falls back to the Windows account name when empty.</summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Names and terms the user has told Toki to get right, one per line (or comma-separated).
    ///
    /// The escape hatch for what frequency cannot settle. Toki repairs most mis-heard names by counting
    /// spellings across the library, but some never appear correctly at all ("Yashtren" for "Yashina" is
    /// four edits away and does not even sound alike to the matcher). A name typed here is treated as
    /// ground truth and is allowed a wider match, because the user has asserted it.
    /// </summary>
    public string? PinnedNames { get; set; }

    /// <summary>
    /// How big note text is drawn, 1 to 12. A comfort setting that belongs to the person rather than
    /// to any one note - somebody who wants larger text wants it everywhere, and per-note sizing would
    /// mean setting it again on each new note. 0 or out of range means "never chosen" and falls back to
    /// the editor's default, so an existing install is pixel-identical until the dial is moved.
    /// </summary>
    public int NoteTextSize { get; set; }

    /// <summary>
    /// True once the user has been told, once, that saving an evidence clip puts other people's voices
    /// in a file Toki can no longer reach. Playing a clip never asks - that is between the user and
    /// their own disk. See <see cref="Toki.Storage.EvidenceAudio"/>.
    /// </summary>
    public bool AudioSharingAcknowledged { get; set; }

    /// <summary>True once the user has been through the first-run onboarding (name + privacy + done).
    /// Defaults false so EVERY existing install sees it once - which is also how a stale/wrong stored
    /// name gets corrected. Set true only when the user finishes (or skips) onboarding.</summary>
    public bool OnboardingComplete { get; set; }

    /// <summary>Best available first name for the greeting: the configured <see cref="UserName"/>,
    /// else a cleaned Windows account name, else "" (caller shows a nameless greeting).</summary>
    [JsonIgnore]
    public string DisplayName => ResolveDisplayName(UserName);

    static string ResolveDisplayName(string? configured)
    {
        var raw = !string.IsNullOrWhiteSpace(configured) ? configured! : Environment.UserName;
        if (string.IsNullOrWhiteSpace(raw)) return "";
        // Windows account names come as "First Last" or "first.last" - take the first
        // token and title-case it so "sid.desai" → "Sid".
        var first = raw.Replace('.', ' ').Replace('_', ' ').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts ? parts[0] : "";
        if (first.Length == 0) return "";
        return char.ToUpperInvariant(first[0]) + (first.Length > 1 ? first[1..].ToLowerInvariant() : "");
    }

    /// <summary>Durable user-stated facts from the Ask chat ("Works at Contoso") - the lightweight
    /// user profile the dialogue brain reads (see <see cref="UserProfile"/>). Null until the first
    /// fact is stored, so existing settings files are untouched. Capped small by the writer.</summary>
    public List<string>? UserFacts { get; set; }

    /// <summary>Local-model GPU offload policy. 0 = auto (detect VRAM and offload a
    /// safe number of layers - the default); -1 = force CPU only; &gt;0 = force that
    /// many layers. Resolved once at launch by <see cref="LlmRuntime"/>.</summary>
    public int GpuLayers { get; set; } = 0;

    /// <summary>DPAPI-encrypted API key (base64). Use <see cref="ApiKey"/> to read/write in the clear.</summary>
    public string? ApiKeyProtected { get; set; }

    /// <summary>When true, the notch is visible to screen recorders and screen-share (ghost mode
    /// OFF). Default false = hidden from capture (WDA_EXCLUDEFROMCAPTURE), so Toki never leaks into
    /// a shared screen during a meeting. The user turns this on in Settings only to record a demo.</summary>
    public bool ShowInRecordings { get; set; }

    /// <summary>UI theme: "dark" (default, Toki's signature black notch) or "light" (clean near-white).
    /// Applied live by <see cref="Toki.Theme.ThemeManager"/>; a plain string so the settings file
    /// stays human-readable and forward-compatible.</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>Glassy notch: swap the notch surface to a more see-through, frosted variant so the
    /// desktop shows through more. Default false (the shipped opaque-ish tinted mirror). Applied live.</summary>
    public bool GlassyNotch { get; set; }

    /// <summary>The note shape for NEW meetings: null = auto-detect (default); otherwise a fixed
    /// template name from <see cref="Toki.Llm.NoteTemplates"/> (e.g. "Standup"). Any note can still
    /// be re-shaped by hand from its template picker.</summary>
    public string? DefaultTemplate { get; set; }

    /// <summary>The Library list sort mode (a <see cref="Toki.LibrarySort"/> name). "Newest" default.</summary>
    public string LibrarySort { get; set; } = "Newest";

    /// <summary>True only after a successful "Test connection" - i.e. the key/model/endpoint
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

    // ---- Google Calendar (M7) ----

    /// <summary>DPAPI-encrypted Google OAuth refresh token (base64) - the ONLY long-lived Google
    /// credential kept at rest. Access tokens are never persisted; they live in CalendarService memory
    /// and refresh lazily. Use <see cref="GoogleRefreshToken"/> to read/write in the clear.</summary>
    public string? GoogleRefreshTokenProtected { get; set; }

    [JsonIgnore]
    public string? GoogleRefreshToken
    {
        get => Unprotect(GoogleRefreshTokenProtected);
        set => GoogleRefreshTokenProtected = Protect(value?.Trim());
    }

    /// <summary>The connected Google account's email (from the id_token) - display only
    /// ("Connected as sid@…"). Null when not connected.</summary>
    public string? GoogleAccountEmail { get; set; }

    // ---- Outlook / Microsoft 365 (2026-08-13) ----
    //
    // A SECOND, INDEPENDENT connection, deliberately not a replacement. Plenty of people have a work
    // Outlook calendar and a personal Google one, and asking them to pick would mean Toki misses half
    // their meetings. Both can be connected at once and the agenda is merged.
    //
    // Same storage shape as the Google grant: the long-lived refresh token DPAPI-encrypted at rest,
    // the short-lived access token never persisted.

    /// <summary>DPAPI-encrypted Microsoft OAuth refresh token (base64). Use
    /// <see cref="OutlookRefreshToken"/> to read/write in the clear.</summary>
    public string? OutlookRefreshTokenProtected { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? OutlookRefreshToken
    {
        get => Unprotect(OutlookRefreshTokenProtected);
        set => OutlookRefreshTokenProtected = Protect(value?.Trim());
    }

    /// <summary>The connected Outlook account's address - display only, and the only way to know which
    /// attendee on a Graph event is the user (Microsoft does not flag it).</summary>
    public string? OutlookAccountEmail { get; set; }

    /// <summary>True when an Outlook refresh token is stored.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool OutlookConnected => !string.IsNullOrEmpty(OutlookRefreshTokenProtected);

    /// <summary>True when a Google refresh token is stored (the calendar is connected).</summary>
    [JsonIgnore]
    public bool GoogleConnected => !string.IsNullOrEmpty(GoogleRefreshTokenProtected);

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
        catch (Exception ex)
        {
            // Loud, not silent: a parse failure here means the API key, Google connection, and
            // profile just reset to defaults - that must at least be diagnosable from the log.
            App.Log($"SETTINGS UNREADABLE - starting with defaults (key/calendar/profile reset): {ex.Message}");
        }
        return new AppSettings();
    }

    static readonly object SaveLock = new();

    public void Save()
    {
        // Serialized: background fact-captures ("call me Anirudh") and UI saves can fire
        // together, and two writers interleaving on the same temp file corrupt both.
        lock (SaveLock)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                // Atomic: write to a temp file, then swap it in. A crash or power loss mid-write
                // on the direct overwrite left a truncated settings.json - silently wiping the
                // DPAPI-encrypted API key and Google refresh token, both unrecoverable.
                var tmp = Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, Path, overwrite: true);
            }
            catch (Exception ex) { App.Log($"settings save failed: {ex.Message}"); }
        }
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
