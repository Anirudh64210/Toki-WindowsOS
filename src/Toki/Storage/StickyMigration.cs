using System.IO;
using System.Text.Json;

namespace Toki.Storage;

/// <summary>
/// One-time migration of the legacy prep-scratch.json into the sticky-note tables; the only code that
/// reads that file. The file holds hand-typed text, so it is never deleted, only renamed to .migrated
/// once every entry is in the database; already-linked events are skipped so reruns can't duplicate.
/// </summary>
public static class StickyMigration
{
    /// <summary>Tests only: redirect the legacy file so a test run can never touch the real one.</summary>
    internal static string? PathOverride;

    static string LegacyPath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toki", "prep-scratch.json");

    public static void RunOnce(MeetingStore store)
    {
        if (!File.Exists(LegacyPath)) return;

        // Claim by rename BEFORE reading a byte: rename is the one atomic filesystem operation, so
        // overlapping launches can't both migrate (check-then-act across processes duplicates notes).
        var claimed = LegacyPath + ".migrating";
        try { File.Move(LegacyPath, claimed, overwrite: false); }
        catch (IOException) { return; }          // someone else claimed it, or it vanished
        catch (UnauthorizedAccessException) { return; }

        try
        {
            List<(string EventId, string Title, string Body)> notes;
            try { notes = ReadLegacy(File.ReadAllText(claimed)); }
            catch (Exception ex)
            {
                // Unreadable in both shapes will never parse; park it instead of retrying forever.
                // The text stays on disk, in plain JSON, for a person to read.
                Move(claimed, LegacyPath + ".unreadable");
                App.Log($"sticky migration: prep-scratch.json could not be read ({ex.Message}) - "
                    + "parked as prep-scratch.json.unreadable and NOT retried; your text is still in that file");
                return;
            }

            var moved = 0;
            foreach (var (eventId, title, body) in notes)
            {
                if (string.IsNullOrWhiteSpace(body)) continue;
                if (store.StickyForEvent(eventId) is not null) continue;   // already carried across
                store.LinkStickyToEvent(eventId, store.CreateSticky(title, body));
                moved++;
            }

            // Renamed, never deleted: the user's words stay on disk in plain JSON.
            Move(claimed, LegacyPath + ".migrated");
            if (moved > 0) App.Log($"sticky: migrated {moved} prep-card note(s) from prep-scratch.json");
        }
        catch (Exception ex)
        {
            // Transient failure (locked db, disk error) may succeed later; put the file back for retry.
            try { Move(claimed, LegacyPath); } catch { }
            App.Log($"sticky migration failed (non-fatal - file restored, retried next launch): {ex.Message}");
        }
    }

    static void Move(string from, string to)
    {
        if (File.Exists(from)) File.Move(from, to, overwrite: true);
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
