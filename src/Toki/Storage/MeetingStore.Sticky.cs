using Microsoft.Data.Sqlite;

namespace Toki.Storage;

/// <summary>One sticky note: the user's OWN writing about a meeting, in Markdown.</summary>
public sealed record StickyNote(
    long Id, string Title, string Body, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Sticky notes — the side of the product that is purely the user's, and the storage rules that keep
/// it from tangling into everything else.
///
/// ── Why its own tables, not a column and not a JSON file ──
/// A sticky note has a lifecycle nothing else in Toki has. It is written BEFORE the meeting record
/// exists (you jot things down while waiting for people to join), it may outlive any single meeting
/// (a running note for a weekly 1:1), and it must survive a crash mid-sentence — losing text someone
/// typed with their own hands is the worst data loss there is. So it gets a real row from the first
/// keystroke, in the same local SQLite file as everything else.
///
/// The version this replaces kept them in %APPDATA%\Toki\prep-scratch.json keyed by calendar event id.
/// That was wrong in two ways the owner hit directly: the file never pruned, and a note could only
/// ever belong to the one event it was born on.
///
/// ── Linking is EXPLICIT. Never by title. ──
/// Meeting titles change, meetings get renamed, recurring events get rescheduled, and two different
/// meetings are often called "Weekly sync". So Toki never guesses which note belongs to which meeting
/// by matching text. There are exactly two links, both by id, both written only because a person chose
/// them:
///   <c>sticky_event</c>   — the note this calendar event's prep card opens (the user picked it, or made it)
///   <c>sticky_meeting</c> — the note that was open during this RECORDED meeting (written once, at stop)
/// A note with no links is still a note; it just isn't attached to anything yet.
///
/// ── What it deliberately does NOT touch ──
/// Sticky notes are not in the meetings table, so they never appear as rows in the Library list. They
/// are not in the FTS index and are not an input to the summary writer, so they can neither steer nor
/// leak into what Toki says about a meeting. Deleting a meeting drops its LINK (cascade) and leaves the
/// note itself alone — you may still want it for next week.
/// </summary>
public sealed partial class MeetingStore
{
    static void EnsureStickyTables(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sticky_notes (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              title TEXT NOT NULL DEFAULT '',
              body TEXT NOT NULL DEFAULT '',
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sticky_event (
              event_id TEXT PRIMARY KEY,
              sticky_id INTEGER NOT NULL REFERENCES sticky_notes(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS sticky_meeting (
              meeting_id INTEGER PRIMARY KEY REFERENCES meetings(id) ON DELETE CASCADE,
              sticky_id INTEGER NOT NULL REFERENCES sticky_notes(id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS sticky_event_by_note ON sticky_event(sticky_id);
            CREATE INDEX IF NOT EXISTS sticky_meeting_by_note ON sticky_meeting(sticky_id);
            """;
        cmd.ExecuteNonQuery();
    }

    // ── the notes themselves ──

    /// <summary>Start a sticky note. Exists from this moment on, so nothing typed into it can be lost.</summary>
    public long CreateSticky(string title, string body = "")
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "INSERT INTO sticky_notes (title, body, created_at, updated_at) VALUES ($t,$b,$n,$n); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$t", title ?? "");
        cmd.Parameters.AddWithValue("$b", body ?? "");
        cmd.Parameters.AddWithValue("$n", now);
        return Convert.ToInt64(cmd.ExecuteScalar()!);
    }

    public StickyNote? GetSticky(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, title, body, created_at, updated_at FROM sticky_notes WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>Save what's been typed. Empty text is kept, not deleted — an empty note the user made on
    /// purpose is theirs to fill in; only Delete removes one.</summary>
    public void UpdateSticky(long id, string body, string? title = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = title is null
            ? "UPDATE sticky_notes SET body=$b, updated_at=$n WHERE id=$id"
            : "UPDATE sticky_notes SET body=$b, title=$t, updated_at=$n WHERE id=$id";
        cmd.Parameters.AddWithValue("$b", body ?? "");
        if (title is not null) cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$n", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Delete a sticky note and every link to it. The only thing that ever removes one.</summary>
    public void DeleteSticky(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM sticky_notes WHERE id=$id";   // links cascade
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Most recently written notes, newest first — what the "use an earlier note" picker
    /// offers. Blank ones are skipped: offering to reuse an empty note is offering nothing.</summary>
    public IReadOnlyList<StickyNote> RecentStickies(int limit = 20)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT id, title, body, created_at, updated_at FROM sticky_notes " +
            "WHERE TRIM(body) <> '' ORDER BY updated_at DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var outp = new List<StickyNote>();
        while (r.Read()) outp.Add(Read(r));
        return outp;
    }

    static StickyNote Read(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2),
        DateTimeOffset.Parse(r.GetString(3)), DateTimeOffset.Parse(r.GetString(4)));

    // ── links (both explicit, both by id) ──

    public long? StickyForEvent(string? eventId) =>
        string.IsNullOrEmpty(eventId) ? null : LinkedId("sticky_event", "event_id", eventId);

    /// <summary>Point this calendar event at this note. Replaces any previous choice — one event opens
    /// one note, and changing your mind is just picking another.</summary>
    public void LinkStickyToEvent(string eventId, long stickyId)
    {
        if (string.IsNullOrEmpty(eventId)) return;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "INSERT INTO sticky_event (event_id, sticky_id) VALUES ($e,$s) " +
            "ON CONFLICT(event_id) DO UPDATE SET sticky_id=excluded.sticky_id";
        cmd.Parameters.AddWithValue("$e", eventId);
        cmd.Parameters.AddWithValue("$s", stickyId);
        cmd.ExecuteNonQuery();
    }

    public long? StickyForMeeting(long meetingId) =>
        LinkedId("sticky_meeting", "meeting_id", meetingId);

    /// <summary>Record which note was open during this recorded meeting. Written once, when recording
    /// stops — that's the fact, and it doesn't change afterwards unless the user attaches another.</summary>
    public void LinkStickyToMeeting(long meetingId, long stickyId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "INSERT INTO sticky_meeting (meeting_id, sticky_id) VALUES ($m,$s) " +
            "ON CONFLICT(meeting_id) DO UPDATE SET sticky_id=excluded.sticky_id";
        cmd.Parameters.AddWithValue("$m", meetingId);
        cmd.Parameters.AddWithValue("$s", stickyId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Detach without deleting — "this note isn't about this meeting after all".</summary>
    public void UnlinkStickyFromMeeting(long meetingId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM sticky_meeting WHERE meeting_id=$m";
        cmd.Parameters.AddWithValue("$m", meetingId);
        cmd.ExecuteNonQuery();
    }

    long? LinkedId(string table, string keyColumn, object key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT sticky_id FROM {table} WHERE {keyColumn}=$k";   // both names are literals above
        cmd.Parameters.AddWithValue("$k", key);
        var v = cmd.ExecuteScalar();
        return v is null || v is DBNull ? null : Convert.ToInt64(v);
    }

    /// <summary>The note attached to a meeting, if any — what the meeting note's "Sticky note" button
    /// opens.</summary>
    public StickyNote? MeetingSticky(long meetingId) =>
        StickyForMeeting(meetingId) is { } id ? GetSticky(id) : null;
}
