using System.IO;
using System.Text.Json;

namespace Toki.Storage;

/// <summary>
/// Moves the old prep-card scratchpad file into the sticky-note tables, once, and then gets out of the
/// way.
///
/// Sticky notes used to live in <c>%APPDATA%\Toki\prep-scratch.json</c>, keyed by calendar event id.
/// This is the ONLY code that still reads that file — everything else now goes through
/// <see cref="MeetingStore"/>, so there is exactly one place the legacy format is understood and one
/// moment it matters.
///
/// It is deliberately careful, because the file holds text the user typed by hand: it never deletes,
/// only renames to <c>.migrated</c> once every entry is safely in the database; it skips any event that
/// already has a note (so running twice can't duplicate); and any failure leaves the file exactly where
/// it was, to be retried next launch.
/// </summary>
public static class StickyMigration
{
    /// <summary>Tests only: redirect the legacy file so a test run can never touch the real one.</summary>
    internal static string? PathOverride;

    static string LegacyPath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toki", "prep-scratch.json");

    public static void RunOnce(MeetingStore store)
    {
        try
        {
            if (!File.Exists(LegacyPath)) return;

            var moved = 0;
            foreach (var (eventId, title, body) in ReadLegacy(File.ReadAllText(LegacyPath)))
            {
                if (string.IsNullOrWhiteSpace(body)) continue;
                if (store.StickyForEvent(eventId) is not null) continue;   // already carried across
                store.LinkStickyToEvent(eventId, store.CreateSticky(title, body));
                moved++;
            }

            // Renamed, never deleted: if anything about this went wrong the user's words are still on
            // disk, in plain JSON, where a person can read them.
            File.Move(LegacyPath, LegacyPath + ".migrated", overwrite: true);
            App.Log($"sticky: migrated {moved} prep-card note(s) from prep-scratch.json");
        }
        catch (Exception ex)
        {
            App.Log($"sticky migration failed (non-fatal — file left in place, retried next launch): {ex.Message}");
        }
    }

    /// <summary>Both shipped shapes: the v2 record, and the v1 bare string per event.</summary>
    static List<(string EventId, string Title, string Body)> ReadLegacy(string raw)
    {
        var outp = new List<(string, string, string)>();
        try
        {
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, LegacyNote>>(raw) ?? new())
                outp.Add((k, v.Title ?? "", v.Text ?? ""));
        }
        catch (JsonException)
        {
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? new())
                outp.Add((k, "", v ?? ""));
        }
        return outp;
    }

    sealed class LegacyNote
    {
        public string? Text { get; set; }
        public string? Title { get; set; }
    }
}
