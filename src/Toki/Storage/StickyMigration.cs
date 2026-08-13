using System.IO;
using System.Text.Json;

namespace Toki.Storage;

/// <summary>
/// Moves the old prep-card scratchpad file into the sticky-note tables, once, and then gets out of the
/// way.
///
/// Sticky notes used to live in <c>%APPDATA%\Toki\prep-scratch.json</c>, keyed by calendar event id.
/// This is the ONLY code that still reads that file - everything else now goes through
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
        if (!File.Exists(LegacyPath)) return;

        // CLAIM the file by renaming it BEFORE reading a byte of it.
        //
        // The old order was read-migrate-rename, which is not atomic. Toki's single-instance guard stops
        // a second window, but the migration runs on a background thread 3 seconds after start, so two
        // launches in quick succession genuinely overlapped: both saw the file, both read it, both wrote
        // the same note. The owner's log showed "migrated 1 prep-card note(s)" three times in under a
        // second - three copies of one note. `StickyForEvent` cannot prevent that; it is a check-then-act
        // across processes.
        //
        // Renaming is the one operation the filesystem makes atomic. Whoever wins owns the migration;
        // everyone else finds nothing and leaves.
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
                // Unreadable in BOTH shapes. The old code left it in place "to be retried next launch",
                // but a file that cannot be parsed will never parse - so that was an error logged on
                // every single launch, forever, with no path to success. Park it under a name that says
                // what happened. The text is still on disk, in plain JSON, for a person to read.
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

            // Renamed, never deleted: if anything about this went wrong the user's words are still on
            // disk, in plain JSON, where a person can read them.
            Move(claimed, LegacyPath + ".migrated");
            if (moved > 0) App.Log($"sticky: migrated {moved} prep-card note(s) from prep-scratch.json");
        }
        catch (Exception ex)
        {
            // Something unexpected (a locked database, a disk error). Put the file BACK so the next
            // launch can try again - unlike an unreadable file, this really might succeed later.
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
