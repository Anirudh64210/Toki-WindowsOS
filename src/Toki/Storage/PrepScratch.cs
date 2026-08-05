using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Toki.Storage;

/// <summary>What the user typed on one meeting's prep card, plus enough context to offer it back
/// afterwards. <paramref name="Awaiting"/> means the meeting has ended and Toki hasn't yet been told
/// whether to keep these notes.</summary>
public sealed record ScratchNote(
    string Text,
    string Title = "",
    DateTimeOffset UpdatedUtc = default,
    bool Awaiting = false);

/// <summary>
/// What the user has typed on a meeting's prep card, keyed by calendar event id.
///
/// Kept OUTSIDE the meeting database on purpose: this text exists BEFORE the meeting record does (you
/// jot things down while waiting for people to join), and it must survive a crash or a quit mid-meeting
/// — losing something the user typed with their own hands is the worst kind of data loss, far worse
/// than losing a regenerable summary. So it lands on disk on every pause in typing.
///
/// Nothing here is ever promoted into the Library on its own: when the meeting ends the entry is just
/// flagged <see cref="ScratchNote.Awaiting"/>, and the Library offers Keep / Discard on the note list
/// itself. A note the user didn't ask for is a hollow entry, and hollow entries are what make a
/// library feel like junk.
///
/// One small JSON file under %APPDATA%\Toki\, written atomically (temp + swap) for the same reason
/// settings.json is: a truncated file here would silently eat a page of the user's notes.
/// </summary>
public static class PrepScratch
{
    static readonly object Lock = new();
    static Dictionary<string, ScratchNote>? _cache;

    /// <summary>Tests only: redirect the file so a test run can never touch the real one. The live
    /// app leaves this null.</summary>
    internal static string? PathOverride;

    static string Path => PathOverride ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toki", "prep-scratch.json");

    /// <summary>What's written for this event, or "" if nothing.</summary>
    public static string Get(string? eventId) => Entry(eventId)?.Text ?? "";

    public static ScratchNote? Entry(string? eventId)
    {
        if (string.IsNullOrEmpty(eventId)) return null;
        lock (Lock)
        {
            Load();
            return _cache!.TryGetValue(eventId, out var n) ? n : null;
        }
    }

    /// <summary>Store (or, for empty text, forget) this event's notes. Keeps any pending decision.</summary>
    public static void Set(string? eventId, string? text, string? title = null)
    {
        if (string.IsNullOrEmpty(eventId)) return;
        lock (Lock)
        {
            Load();
            var trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0) _cache!.Remove(eventId);
            else
            {
                var prev = _cache!.TryGetValue(eventId, out var p) ? p : null;
                _cache[eventId] = new ScratchNote(
                    trimmed,
                    string.IsNullOrWhiteSpace(title) ? prev?.Title ?? "" : title!,
                    DateTimeOffset.UtcNow,
                    prev?.Awaiting ?? false);
            }
            Save();
        }
    }

    /// <summary>The meeting is over: if anything was written, ask the user whether to keep it. Called
    /// once when a recording stops — never creates anything on its own.</summary>
    public static void MarkAwaiting(string? eventId, string title)
    {
        if (string.IsNullOrEmpty(eventId)) return;
        lock (Lock)
        {
            Load();
            if (!_cache!.TryGetValue(eventId, out var n) || n.Text.Length == 0) return;
            _cache[eventId] = n with
            {
                Awaiting = true,
                Title = string.IsNullOrWhiteSpace(n.Title) ? title : n.Title,
            };
            Save();
        }
    }

    /// <summary>Every set of notes waiting on a Keep / Discard decision, newest first.</summary>
    public static IReadOnlyList<(string EventId, ScratchNote Note)> Awaiting()
    {
        lock (Lock)
        {
            Load();
            return _cache!.Where(kv => kv.Value.Awaiting && kv.Value.Text.Length > 0)
                          .OrderByDescending(kv => kv.Value.UpdatedUtc)
                          .Select(kv => (kv.Key, kv.Value))
                          .ToList();
        }
    }

    /// <summary>Drop this event's notes — once they've been promoted into a Library note, or when the
    /// user says not to keep them.</summary>
    public static void Clear(string? eventId)
    {
        if (string.IsNullOrEmpty(eventId)) return;
        lock (Lock) { Load(); if (_cache!.Remove(eventId)) Save(); }
    }

    /// <summary>True when there is something worth offering to save.</summary>
    public static bool Has(string? eventId) => Get(eventId).Length > 0;

    static void Load()
    {
        if (_cache is not null) return;
        try
        {
            if (!File.Exists(Path)) { _cache = new(); return; }
            var raw = File.ReadAllText(Path);
            try
            {
                _cache = JsonSerializer.Deserialize<Dictionary<string, ScratchNote>>(raw) ?? new();
            }
            catch (JsonException)
            {
                // The first shipped version stored a bare string per event. Read it rather than
                // throwing the user's notes away.
                var old = JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? new();
                _cache = old.ToDictionary(kv => kv.Key, kv => new ScratchNote(kv.Value));
            }
        }
        catch (Exception ex)
        {
            App.Log($"prep scratch load failed: {ex.Message}");
            _cache = new();
        }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception ex) { App.Log($"prep scratch save failed: {ex.Message}"); }
    }

    /// <summary>Tests/self-tests only: forget the in-memory copy so the next read hits disk.</summary>
    internal static void ResetForTests() { lock (Lock) _cache = null; }

    /// <summary>Render harness only: plant an entry IN MEMORY so a screenshot can show the
    /// Keep/Discard card. Never writes to disk — the harness must not touch the user's notes.</summary>
    internal static void SeedForRender(string eventId, ScratchNote note)
    {
        lock (Lock) { Load(); _cache![eventId] = note; }
    }
}
