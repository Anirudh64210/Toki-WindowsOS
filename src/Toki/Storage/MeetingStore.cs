using System.IO;
using Microsoft.Data.Sqlite;
using Toki.Transcription;

namespace Toki.Storage;

/// <summary>
/// Local SQLite store for meetings, transcripts, and notes — everything lives in
/// %APPDATA%\Toki\toki.db (hard constraint: all data local, no cloud). Manual
/// notes and meetings share one table, distinguished by <c>kind</c>.
/// </summary>
public sealed class MeetingStore
{
    readonly string _connString;

    public static string DbPathDefault => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toki", "toki.db");

    public MeetingStore(string? dbPath = null)
    {
        var path = dbPath ?? DbPathDefault;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        EnsureCreated();
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return c;
    }

    void EnsureCreated()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS meetings (
              id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, title TEXT NOT NULL,
              app TEXT, started_at TEXT NOT NULL, duration_ms INTEGER NOT NULL,
              folder TEXT, body TEXT, tldr TEXT);
            CREATE TABLE IF NOT EXISTS transcript (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
              speaker TEXT NOT NULL, start_ms INTEGER, end_ms INTEGER, text TEXT NOT NULL, ord INTEGER);
            CREATE TABLE IF NOT EXISTS decisions (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE, text TEXT NOT NULL, ord INTEGER);
            CREATE TABLE IF NOT EXISTS questions (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE, text TEXT NOT NULL, ord INTEGER);
            CREATE TABLE IF NOT EXISTS action_items (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
              text TEXT NOT NULL, owner TEXT, due TEXT, done INTEGER DEFAULT 0, ord INTEGER);
            """;
        cmd.ExecuteNonQuery();
    }

    public long Save(MeetingRecord m)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        long id;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO meetings (kind, title, app, started_at, duration_ms, folder, body, tldr)
                VALUES ($kind, $title, $app, $started, $dur, $folder, $body, $tldr);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$kind", m.Kind.ToString());
            cmd.Parameters.AddWithValue("$title", m.Title);
            cmd.Parameters.AddWithValue("$app", (object?)m.App ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$started", m.StartedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$dur", m.DurationMs);
            cmd.Parameters.AddWithValue("$folder", (object?)m.Folder ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$body", (object?)m.Body ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tldr", m.Notes.Tldr);
            id = (long)cmd.ExecuteScalar()!;
        }

        InsertLines(c, "transcript", id, m.Transcript.Select((s, i) =>
            (object[])[s.Speaker.ToString(), s.StartMs, s.EndMs, s.Text, i]),
            "speaker, start_ms, end_ms, text, ord", "$speaker, $start_ms, $end_ms, $text, $ord");
        InsertSimple(c, "decisions", id, m.Notes.Decisions);
        InsertSimple(c, "questions", id, m.Notes.Questions);
        foreach (var (a, i) in m.Notes.Actions.Select((a, i) => (a, i)))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO action_items (meeting_id, text, owner, due, done, ord) VALUES ($m,$t,$o,$d,$done,$ord)";
            cmd.Parameters.AddWithValue("$m", id);
            cmd.Parameters.AddWithValue("$t", a.Text);
            cmd.Parameters.AddWithValue("$o", (object?)a.Owner ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$d", (object?)a.Due ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$done", a.Done ? 1 : 0);
            cmd.Parameters.AddWithValue("$ord", i);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        m.Id = id;
        return id;
    }

    public long CreateManualNote(string title, string body) => Save(new MeetingRecord
    {
        Kind = MeetingKind.Manual,
        Title = string.IsNullOrWhiteSpace(title) ? "Untitled note" : title,
        Body = body,
        StartedAt = DateTimeOffset.Now,
    });

    public IReadOnlyList<MeetingSummary> List(string? search = null)
    {
        var (where, speaker, term) = ParseSearch(search);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT m.id, m.kind, m.title, m.app, m.started_at, m.duration_ms, m.tldr,
                   (SELECT COUNT(*) FROM action_items a WHERE a.meeting_id=m.id AND a.done=0),
                   (SELECT COUNT(DISTINCT t.speaker) FROM transcript t WHERE t.meeting_id=m.id)
            FROM meetings m {where} ORDER BY m.started_at DESC;
            """;
        if (term is not null) cmd.Parameters.AddWithValue("$term", $"%{term}%");
        if (speaker is not null) cmd.Parameters.AddWithValue("$speaker", speaker);

        var list = new List<MeetingSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new MeetingSummary(
                r.GetInt64(0), Enum.Parse<MeetingKind>(r.GetString(1)), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), DateTimeOffset.Parse(r.GetString(4)),
                r.GetInt32(5), r.IsDBNull(6) ? "" : r.GetString(6), r.GetInt32(7), r.GetInt32(8)));
        return list;
    }

    public MeetingRecord? Get(long id)
    {
        using var c = Open();
        var m = new MeetingRecord { Id = id };
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT kind, title, app, started_at, duration_ms, folder, body, tldr FROM meetings WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            m.Kind = Enum.Parse<MeetingKind>(r.GetString(0));
            m.Title = r.GetString(1);
            m.App = r.IsDBNull(2) ? null : r.GetString(2);
            m.StartedAt = DateTimeOffset.Parse(r.GetString(3));
            m.DurationMs = r.GetInt32(4);
            m.Folder = r.IsDBNull(5) ? null : r.GetString(5);
            m.Body = r.IsDBNull(6) ? null : r.GetString(6);
            m.Notes.Tldr = r.IsDBNull(7) ? "" : r.GetString(7);
        }
        m.Transcript = ReadTranscript(c, id);
        m.Notes.Decisions = ReadSimple(c, "decisions", id);
        m.Notes.Questions = ReadSimple(c, "questions", id);
        m.Notes.Actions = ReadActions(c, id);
        return m;
    }

    /// <summary>Replace a meeting's notes (e.g. upgrade heuristic → LLM-written).</summary>
    public void ReplaceNotes(long meetingId, NoteSet notes)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var table in new[] { "decisions", "questions", "action_items" })
        {
            using var del = c.CreateCommand();
            del.CommandText = $"DELETE FROM {table} WHERE meeting_id=$id";
            del.Parameters.AddWithValue("$id", meetingId);
            del.ExecuteNonQuery();
        }
        using (var upd = c.CreateCommand())
        {
            upd.CommandText = "UPDATE meetings SET tldr=$t WHERE id=$id";
            upd.Parameters.AddWithValue("$t", notes.Tldr);
            upd.Parameters.AddWithValue("$id", meetingId);
            upd.ExecuteNonQuery();
        }
        InsertSimple(c, "decisions", meetingId, notes.Decisions);
        InsertSimple(c, "questions", meetingId, notes.Questions);
        for (var i = 0; i < notes.Actions.Count; i++)
        {
            var a = notes.Actions[i];
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO action_items (meeting_id, text, owner, due, done, ord) VALUES ($m,$t,$o,$d,$done,$ord)";
            cmd.Parameters.AddWithValue("$m", meetingId);
            cmd.Parameters.AddWithValue("$t", a.Text);
            cmd.Parameters.AddWithValue("$o", (object?)a.Owner ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$d", (object?)a.Due ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$done", a.Done ? 1 : 0);
            cmd.Parameters.AddWithValue("$ord", i);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void SetActionDone(long actionId, bool done)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE action_items SET done=$d WHERE id=$id";
        cmd.Parameters.AddWithValue("$d", done ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", actionId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateTitleAndBody(long id, string title, string? body)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE meetings SET title=$t, body=$b WHERE id=$id";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$b", (object?)body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM meetings WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---- helpers ----

    static (string Where, string? Speaker, string? Term) ParseSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return ("", null, null);
        var s = search.Trim();
        string? speaker = null;
        if (s.StartsWith("you:", StringComparison.OrdinalIgnoreCase)) { speaker = "You"; s = s[4..].Trim(); }
        else if (s.StartsWith("them:", StringComparison.OrdinalIgnoreCase)) { speaker = "Them"; s = s[5..].Trim(); }
        if (s.Length == 0) return ("", null, null);

        var transcriptFilter = speaker is null
            ? "SELECT 1 FROM transcript t WHERE t.meeting_id=m.id AND t.text LIKE $term"
            : "SELECT 1 FROM transcript t WHERE t.meeting_id=m.id AND t.speaker=$speaker AND t.text LIKE $term";
        var where = speaker is null
            ? $"WHERE (m.title LIKE $term OR m.tldr LIKE $term OR m.body LIKE $term OR EXISTS ({transcriptFilter}))"
            : $"WHERE EXISTS ({transcriptFilter})";
        return (where, speaker, s);
    }

    static void InsertSimple(SqliteConnection c, string table, long meetingId, List<string> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"INSERT INTO {table} (meeting_id, text, ord) VALUES ($m, $t, $o)";
            cmd.Parameters.AddWithValue("$m", meetingId);
            cmd.Parameters.AddWithValue("$t", rows[i]);
            cmd.Parameters.AddWithValue("$o", i);
            cmd.ExecuteNonQuery();
        }
    }

    static void InsertLines(SqliteConnection c, string table, long meetingId,
        IEnumerable<object[]> rows, string cols, string vals)
    {
        foreach (var row in rows)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"INSERT INTO {table} (meeting_id, {cols}) VALUES ($m, {vals})";
            cmd.Parameters.AddWithValue("$m", meetingId);
            cmd.Parameters.AddWithValue("$speaker", row[0]);
            cmd.Parameters.AddWithValue("$start_ms", row[1]);
            cmd.Parameters.AddWithValue("$end_ms", row[2]);
            cmd.Parameters.AddWithValue("$text", row[3]);
            cmd.Parameters.AddWithValue("$ord", row[4]);
            cmd.ExecuteNonQuery();
        }
    }

    static List<TranscriptSegment> ReadTranscript(SqliteConnection c, long id)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT speaker, start_ms, end_ms, text FROM transcript WHERE meeting_id=$id ORDER BY ord";
        cmd.Parameters.AddWithValue("$id", id);
        var list = new List<TranscriptSegment>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new TranscriptSegment(Enum.Parse<Speaker>(r.GetString(0)), r.GetInt32(1), r.GetInt32(2), r.GetString(3)));
        return list;
    }

    static List<string> ReadSimple(SqliteConnection c, string table, long id)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT text FROM {table} WHERE meeting_id=$id ORDER BY ord";
        cmd.Parameters.AddWithValue("$id", id);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    static List<ActionItem> ReadActions(SqliteConnection c, long id)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, text, owner, due, done FROM action_items WHERE meeting_id=$id ORDER BY ord";
        cmd.Parameters.AddWithValue("$id", id);
        var list = new List<ActionItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ActionItem
            {
                Id = r.GetInt64(0), Text = r.GetString(1),
                Owner = r.IsDBNull(2) ? null : r.GetString(2),
                Due = r.IsDBNull(3) ? null : r.GetString(3),
                Done = r.GetInt32(4) != 0,
            });
        return list;
    }
}
