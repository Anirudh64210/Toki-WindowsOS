using System.IO;
using Microsoft.Data.Sqlite;
using Toki.Transcription;

namespace Toki.Storage;

/// <summary>
/// Local SQLite store for meetings, transcripts, and notes - everything lives in
/// %APPDATA%\Toki\toki.db (hard constraint: all data local, no cloud). Manual
/// notes and meetings share one table, distinguished by <c>kind</c>.
/// </summary>
public sealed partial class MeetingStore
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

        EnsureStickyTables(c);

        // Migration: a manual sort order for the Library list. Seed it to the current
        // newest-first order so nothing jumps around on first run.
        if (!ColumnExists(c, "meetings", "sort_order"))
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE meetings ADD COLUMN sort_order INTEGER;";
            alter.ExecuteNonQuery();
        }
        using var seed = c.CreateCommand();
        seed.CommandText = "UPDATE meetings SET sort_order = (SELECT COUNT(*) FROM meetings m2 WHERE m2.started_at > meetings.started_at) WHERE sort_order IS NULL;";
        seed.ExecuteNonQuery();

        // speaker_tag = the resolved person; deliberately separate from `speaker`, which stays the
        // audio stream (a fact, not an inference). NULL = never diarised, falls back to You/Them.
        if (!ColumnExists(c, "transcript", "speaker_tag"))
        {
            using var alterTag = c.CreateCommand();
            alterTag.CommandText = "ALTER TABLE transcript ADD COLUMN speaker_tag TEXT;";
            alterTag.ExecuteNonQuery();
        }

        // template/sections_json: older rows have neither and fall back to the legacy
        // decisions/questions tables via NoteSet.EffectiveSections. corrections_json: what Toki
        // rewrote in the transcript; NULL on older rows reads as "nothing was marked".
        foreach (var col in new[] { "template", "sections_json", "corrections_json" })
            if (!ColumnExists(c, "meetings", col))
            {
                using var alter2 = c.CreateCommand();
                alter2.CommandText = $"ALTER TABLE meetings ADD COLUMN {col} TEXT;";
                alter2.ExecuteNonQuery();
            }

        // Full-text search index for "Ask your notes" (FTS5, bundled in e_sqlite3). A standalone
        // table keyed by meeting id; body = every searchable field concatenated. If FTS5 is somehow
        // unavailable, retrieval falls back to keyword LIKE - never fatal.
        try
        {
            using (var mk = c.CreateCommand())
            {
                mk.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS meeting_fts USING fts5(title, body, tokenize='porter unicode61');";
                mk.ExecuteNonQuery();
            }
            // Backfill / self-heal: if the index count doesn't match the meetings count, rebuild it.
            if (Scalar(c, "SELECT COUNT(*) FROM meeting_fts") != Scalar(c, "SELECT COUNT(*) FROM meetings"))
                RebuildAllFts(c);
            _ftsOk = true;
        }
        catch (Exception ex) { App.Log($"FTS unavailable, using keyword fallback: {ex.Message}"); _ftsOk = false; }
    }

    bool _ftsOk;

    static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    // The one place the FTS "body" is defined: title + summary + sections + manual body + transcript +
    // action items + legacy decisions/questions - everything the user might search for.
    const string FtsSelect = """
        SELECT m.id, m.title,
          COALESCE(m.tldr,'') || ' ' || COALESCE(m.sections_json,'') || ' ' || COALESCE(m.body,'') || ' ' ||
          COALESCE((SELECT group_concat(text,' ') FROM transcript   WHERE meeting_id=m.id),'') || ' ' ||
          COALESCE((SELECT group_concat(text,' ') FROM action_items WHERE meeting_id=m.id),'') || ' ' ||
          COALESCE((SELECT group_concat(text,' ') FROM decisions    WHERE meeting_id=m.id),'') || ' ' ||
          COALESCE((SELECT group_concat(text,' ') FROM questions    WHERE meeting_id=m.id),'')
        FROM meetings m
        """;

    void RebuildAllFts(SqliteConnection c)
    {
        using var del = c.CreateCommand();
        del.CommandText = "DELETE FROM meeting_fts;";
        del.ExecuteNonQuery();
        using var ins = c.CreateCommand();
        ins.CommandText = $"INSERT INTO meeting_fts(rowid, title, body) {FtsSelect};";
        ins.ExecuteNonQuery();
    }

    // Re-index ONE meeting (call inside the caller's transaction, after its rows are written).
    void ReindexFts(SqliteConnection c, long id)
    {
        if (!_ftsOk) return;
        try
        {
            using (var del = c.CreateCommand())
            {
                del.CommandText = "DELETE FROM meeting_fts WHERE rowid=$id;";
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
            }
            using var ins = c.CreateCommand();
            ins.CommandText = $"INSERT INTO meeting_fts(rowid, title, body) {FtsSelect} WHERE m.id=$id;";
            ins.Parameters.AddWithValue("$id", id);
            ins.ExecuteNonQuery();
        }
        catch (Exception ex) { App.Log($"FTS reindex {id}: {ex.Message}"); }
    }

    /// <summary>The ONE FTS5 query builder - the single choke point for anything reaching a MATCH
    /// clause. Parameter binding does not protect a MATCH argument (FTS5 parses the bound value), so
    /// every term is reduced to alphanumerics and emitted as grammar-free prefix-OR. Returns "" when
    /// nothing usable remains; the caller must then skip the MATCH (an empty MATCH is a syntax error).</summary>
    public static string ToFtsQuery(string? query)
    {
        var terms = (query ?? "")
            .Split(new[] { ' ', '\t', '\n', '?', '.', ',', '!', ';', ':', '\'', '"', '(', ')', '/', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(w => w.Length > 1)
            .Distinct()
            .ToList();
        return terms.Count == 0 ? "" : string.Join(" OR ", terms.Select(t => $"{t}*"));   // prefix-OR, alnum-only
    }

    public List<long> SearchRelevant(string query, int limit)
    {
        var ids = new List<long>();
        if (!_ftsOk) return ids;
        var match = ToFtsQuery(query);
        if (match.Length == 0) return ids;
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT f.rowid FROM meeting_fts f JOIN meetings m ON m.id = f.rowid
                WHERE meeting_fts MATCH $q AND m.kind = 'Meeting'
                ORDER BY bm25(meeting_fts, 5.0, 1.0) LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$q", match);
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        catch (Exception ex) { App.Log($"FTS search: {ex.Message}"); }
        return ids;
    }

    /// <summary>Meetings whose title contains any given term (case-insensitive substring), newest
    /// first. Recall backstop for Ask retrieval: a title hit is near-certain relevance and must not
    /// drown in BM25 noise. Terms under 3 chars are ignored so a fragment can't over-match.</summary>
    public List<long> TitleMatchIds(IEnumerable<string> terms)
    {
        var ts = (terms ?? []).Where(t => !string.IsNullOrWhiteSpace(t) && t.Trim().Length > 2)
            .Select(t => t.Trim().ToLowerInvariant()).Distinct().Take(6).ToList();
        var found = new List<(long Id, DateTimeOffset At)>();
        if (ts.Count == 0) return new List<long>();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            var clauses = new List<string>();
            for (var i = 0; i < ts.Count; i++)
            {
                clauses.Add($"lower(title) LIKE $t{i} ESCAPE '\\'");
                var esc = ts[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                cmd.Parameters.AddWithValue($"$t{i}", $"%{esc}%");
            }
            cmd.CommandText = $"SELECT id, started_at FROM meetings WHERE kind='Meeting' AND ({string.Join(" OR ", clauses)})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (DateTimeOffset.TryParse(r.GetString(1), out var at)) found.Add((r.GetInt64(0), at));
        }
        catch (Exception ex) { App.Log($"title match: {ex.Message}"); }
        return found.OrderByDescending(x => x.At).Select(x => x.Id).ToList();
    }

    // (id, started_at) for every real meeting - the basis for deterministic temporal resolution
    // (recency / date-range), parsed in C# so it's robust to timezone-offset string differences.
    List<(long Id, DateTimeOffset At)> MeetingTimes()
    {
        var list = new List<(long, DateTimeOffset)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, started_at FROM meetings WHERE kind='Meeting'";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (DateTimeOffset.TryParse(r.GetString(1), out var at)) list.Add((r.GetInt64(0), at));
        return list;
    }

    /// <summary>The N most recent meetings by date (for "my most recent / latest meeting").</summary>
    public List<long> LatestMeetingIds(int n) =>
        MeetingTimes().OrderByDescending(x => x.At).Take(n).Select(x => x.Id).ToList();

    /// <summary>The N oldest meetings by date (for "my first / earliest meeting").</summary>
    public List<long> OldestMeetingIds(int n) =>
        MeetingTimes().OrderBy(x => x.At).Take(n).Select(x => x.Id).ToList();

    /// <summary>Meetings whose start falls in [start, end), newest first (for "meeting on July 30 / last week").</summary>
    public List<long> MeetingIdsInRange(DateTimeOffset start, DateTimeOffset end) =>
        MeetingTimes().Where(x => x.At >= start && x.At < end).OrderByDescending(x => x.At).Select(x => x.Id).ToList();

    /// <summary>Open (undone) action items across all meetings, newest meeting first - for the Ask
    /// "what are my open tasks / what did I commit to" router (answered from data, never hallucinated).</summary>
    public List<(string Meeting, string Text, string? Owner, string? Due)> OpenActions(int limit)
    {
        var list = new List<(string, string, string?, string?)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT m.title, a.text, a.owner, a.due FROM action_items a
            JOIN meetings m ON m.id = a.meeting_id
            WHERE a.done = 0
            ORDER BY m.started_at DESC, a.ord ASC LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    /// <summary>Open action items with their source meeting's id + start time - for due-date-aware task
    /// scoping ("what's due this week"): the meeting date is the reference point for resolving a free-text
    /// due like "Friday" (it means the Friday after THAT meeting).</summary>
    public List<OpenAction> OpenActionsWithDates(int limit)
    {
        var list = new List<OpenAction>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.title, m.started_at, a.text, a.owner, a.due FROM action_items a
            JOIN meetings m ON m.id = a.meeting_id
            WHERE a.done = 0
            ORDER BY m.started_at DESC, a.ord ASC LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new OpenAction(
                r.GetInt64(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)),
                r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    /// <summary>Recent transcript lines for building the name lexicon. Capped: the point is a
    /// frequency vote carried by the newest meetings, not a full corpus scan.</summary>
    public List<string> TranscriptLines(int limit = 20000)
    {
        var list = new List<string>();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT text FROM transcript ORDER BY id DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
        }
        catch (Exception ex) { App.Log($"transcript lines: {ex.Message}"); }
        return list;
    }

    /// <summary>Meeting titles and summaries - where a name often appears spelled correctly even when a
    /// transcript mangled it ("cascading from Craig's team").</summary>
    public List<string> NoteText(int limit = 500)
    {
        var list = new List<string>();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT title, COALESCE(tldr,'') FROM meetings ORDER BY id DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) { list.Add(r.GetString(0)); list.Add(r.GetString(1)); }
        }
        catch (Exception ex) { App.Log($"note text: {ex.Message}"); }
        return list;
    }

    /// <summary>Every distinct action-item owner name - the closed vocabulary the Ask person-filter
    /// resolves against, so a query word only counts as a person when the name exists in the data.</summary>
    public List<string> ActionOwners()
    {
        var list = new List<string>();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT owner FROM action_items WHERE owner IS NOT NULL AND TRIM(owner) != '';";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
        }
        catch (Exception ex) { App.Log($"action owners: {ex.Message}"); }
        return list;
    }

    // The template sections persist as a small JSON array on the meetings row (no extra table).
    static string? SerializeSections(IReadOnlyList<NoteSection> s) =>
        s.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(s);

    static List<NoteSection> DeserializeSections(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<List<NoteSection>>(json) ?? []; }
        catch { return []; }
    }

    // Same shape for the transcript's edit record. A meeting Toki did not change stores NULL rather
    // than an empty array, so "never marked" and "marked nothing" look identical to every older row.
    static string? SerializeCorrections(IReadOnlyList<TextFix> f) =>
        f.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(f);

    static List<TextFix> DeserializeCorrections(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<List<TextFix>>(json) ?? []; }
        catch { return []; }
    }

    static bool ColumnExists(SqliteConnection c, string table, string column)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Persist a new top-to-bottom order for the Library list (manual drag reorder).</summary>
    public void Reorder(IReadOnlyList<long> idsTopToBottom)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        for (var i = 0; i < idsTopToBottom.Count; i++)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE meetings SET sort_order=$o WHERE id=$id";
            cmd.Parameters.AddWithValue("$o", i);
            cmd.Parameters.AddWithValue("$id", idsTopToBottom[i]);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public long Save(MeetingRecord m)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        long id;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO meetings (kind, title, app, started_at, duration_ms, folder, body, tldr, template, sections_json, corrections_json)
                VALUES ($kind, $title, $app, $started, $dur, $folder, $body, $tldr, $template, $sections, $corrections);
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
            cmd.Parameters.AddWithValue("$template", (object?)m.Notes.Template ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sections", (object?)SerializeSections(m.Notes.Sections) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$corrections", (object?)SerializeCorrections(m.Corrections) ?? DBNull.Value);
            id = (long)cmd.ExecuteScalar()!;
        }

        // New items go to the top of the Library list.
        using (var so = c.CreateCommand())
        {
            so.CommandText = "UPDATE meetings SET sort_order = COALESCE((SELECT MIN(sort_order) FROM meetings WHERE id<>$id), 0) - 1 WHERE id=$id";
            so.Parameters.AddWithValue("$id", id);
            so.ExecuteNonQuery();
        }

        InsertLines(c, "transcript", id, m.Transcript.Select((s, i) =>
            (object[])[s.Speaker.ToString(), s.StartMs, s.EndMs, s.Text, i, (object?)s.Tag ?? DBNull.Value]),
            "speaker, start_ms, end_ms, text, ord, speaker_tag", "$speaker, $start_ms, $end_ms, $text, $ord, $speaker_tag");
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

        ReindexFts(c, id);
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

    /// <summary>Every recording folder referenced by a saved meeting (for the orphan-recovery sweep).</summary>
    public List<string> AllFolders()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT folder FROM meetings WHERE folder IS NOT NULL AND folder <> ''";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public IReadOnlyList<MeetingSummary> List(string? search = null)
    {
        var (where, speaker, term) = ParseSearch(search);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT m.id, m.kind, m.title, m.app, m.started_at, m.duration_ms, m.tldr,
                   (SELECT COUNT(*) FROM action_items a WHERE a.meeting_id=m.id AND a.done=0),
                   (SELECT COUNT(DISTINCT t.speaker) FROM transcript t WHERE t.meeting_id=m.id)
            FROM meetings m {where} ORDER BY COALESCE(m.sort_order, 1000000) ASC, m.started_at DESC;
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
            cmd.CommandText = "SELECT kind, title, app, started_at, duration_ms, folder, body, tldr, template, sections_json, corrections_json FROM meetings WHERE id=$id";
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
            m.Notes.Template = r.IsDBNull(8) ? null : r.GetString(8);
            m.Notes.Sections = DeserializeSections(r.IsDBNull(9) ? null : r.GetString(9));
            m.Corrections = DeserializeCorrections(r.IsDBNull(10) ? null : r.GetString(10));
        }
        m.Transcript = ReadTranscript(c, id);
        m.Notes.Decisions = ReadSimple(c, "decisions", id);   // legacy notes surface via EffectiveSections
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
            upd.CommandText = "UPDATE meetings SET tldr=$t, template=$template, sections_json=$sections WHERE id=$id";
            upd.Parameters.AddWithValue("$t", notes.Tldr);
            upd.Parameters.AddWithValue("$template", (object?)notes.Template ?? DBNull.Value);
            upd.Parameters.AddWithValue("$sections", (object?)SerializeSections(notes.Sections) ?? DBNull.Value);
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
        ReindexFts(c, meetingId);
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
        // FTS reindex must share the transaction: a crash between UPDATE and reindex leaves search
        // finding the old title forever (the startup self-heal only compares row counts).
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE meetings SET title=$t, body=$b WHERE id=$id";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$b", (object?)body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        ReindexFts(c, id);
        tx.Commit();
    }

    public void Delete(long id)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM meetings WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        if (_ftsOk)
            try
            {
                using var fdel = c.CreateCommand();
                fdel.CommandText = "DELETE FROM meeting_fts WHERE rowid=$id;";
                fdel.Parameters.AddWithValue("$id", id);
                fdel.ExecuteNonQuery();
            }
            catch (Exception ex) { App.Log($"FTS delete {id}: {ex.Message}"); }
        tx.Commit();
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

        // Escape LIKE wildcards so a literal % or _ in the search box matches itself.
        s = s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var transcriptFilter = speaker is null
            ? "SELECT 1 FROM transcript t WHERE t.meeting_id=m.id AND t.text LIKE $term ESCAPE '\\'"
            : "SELECT 1 FROM transcript t WHERE t.meeting_id=m.id AND t.speaker=$speaker AND t.text LIKE $term ESCAPE '\\'";
        var where = speaker is null
            ? $"WHERE (m.title LIKE $term ESCAPE '\\' OR m.tldr LIKE $term ESCAPE '\\' OR m.body LIKE $term ESCAPE '\\' OR EXISTS ({transcriptFilter}))"
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
            if (row.Length > 5) cmd.Parameters.AddWithValue("$speaker_tag", row[5]);
            cmd.ExecuteNonQuery();
        }
    }

    static List<TranscriptSegment> ReadTranscript(SqliteConnection c, long id)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT speaker, start_ms, end_ms, text, speaker_tag FROM transcript WHERE meeting_id=$id ORDER BY ord";
        cmd.Parameters.AddWithValue("$id", id);
        var list = new List<TranscriptSegment>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new TranscriptSegment(Enum.Parse<Speaker>(r.GetString(0)), r.GetInt32(1), r.GetInt32(2), r.GetString(3))
            {
                Tag = r.IsDBNull(4) ? null : r.GetString(4),
            });
        return list;
    }

    /// <summary>Write the resolved speaker names onto a saved transcript, by position. Called when
    /// diarisation finishes and on user rename; a rename is a plain text update, nothing to retrain.</summary>
    public void SetSpeakerTags(long meetingId, IReadOnlyList<string?> tagsInOrder)
    {
        try
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            for (var i = 0; i < tagsInOrder.Count; i++)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE transcript SET speaker_tag=$t WHERE meeting_id=$m AND ord=$o";
                cmd.Parameters.AddWithValue("$t", (object?)tagsInOrder[i] ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$m", meetingId);
                cmd.Parameters.AddWithValue("$o", i);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex) { App.Log($"set speaker tags: {ex.Message}"); }
    }

    /// <summary>
    /// Replace a saved meeting's transcript and its record of what Toki rewrote. Rewrites the
    /// TRANSCRIPT only: summary, sections and to-dos (which hold a person's edits) are never touched.
    /// </summary>
    public void ReplaceTranscript(long meetingId, IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<TextFix> corrections)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand())
        {
            del.CommandText = "DELETE FROM transcript WHERE meeting_id=$id";
            del.Parameters.AddWithValue("$id", meetingId);
            del.ExecuteNonQuery();
        }
        InsertLines(c, "transcript", meetingId, segments.Select((s, i) =>
            (object[])[s.Speaker.ToString(), s.StartMs, s.EndMs, s.Text, i, (object?)s.Tag ?? DBNull.Value]),
            "speaker, start_ms, end_ms, text, ord, speaker_tag", "$speaker, $start_ms, $end_ms, $text, $ord, $speaker_tag");
        using (var upd = c.CreateCommand())
        {
            upd.CommandText = "UPDATE meetings SET corrections_json=$c WHERE id=$id";
            upd.Parameters.AddWithValue("$c", (object?)SerializeCorrections(corrections) ?? DBNull.Value);
            upd.Parameters.AddWithValue("$id", meetingId);
            upd.ExecuteNonQuery();
        }
        ReindexFts(c, meetingId);   // search must match the words the transcript now contains
        tx.Commit();
    }

    /// <summary>Fill in a to-do's owner, by position, without touching its text or done state.</summary>
    public void SetActionOwners(long meetingId, IReadOnlyList<(int Index, string Owner)> owners)
    {
        if (owners.Count == 0) return;
        try
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            foreach (var (index, owner) in owners)
            {
                using var cmd = c.CreateCommand();
                // Only where the owner is still empty: the user may have typed one while this ran, and
                // a background inference must never overwrite a person's own answer.
                cmd.CommandText = "UPDATE action_items SET owner=$o WHERE meeting_id=$m AND ord=$i AND (owner IS NULL OR owner='')";
                cmd.Parameters.AddWithValue("$o", owner);
                cmd.Parameters.AddWithValue("$m", meetingId);
                cmd.Parameters.AddWithValue("$i", index);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex) { App.Log($"set action owners: {ex.Message}"); }
    }

    /// <summary>Rename one speaker everywhere in a meeting. "Assign the name once and it applies across
    /// the whole transcript" - the correction affordance that makes automatic naming safe to ship.</summary>
    public void RenameSpeaker(long meetingId, string from, string to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return;
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE transcript SET speaker_tag=$to WHERE meeting_id=$m AND speaker_tag=$from";
            cmd.Parameters.AddWithValue("$to", to.Trim());
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$m", meetingId);
            cmd.ExecuteNonQuery();

            // The to-dos carry the same labels ("Speaker 2 owns this"); renaming the voice and not
            // the owner leaves the note pointing at a person who no longer exists in the transcript.
            using var owners = c.CreateCommand();
            owners.CommandText = "UPDATE action_items SET owner=$to WHERE meeting_id=$m AND owner=$from";
            owners.Parameters.AddWithValue("$to", to.Trim());
            owners.Parameters.AddWithValue("$from", from);
            owners.Parameters.AddWithValue("$m", meetingId);
            owners.ExecuteNonQuery();
        }
        catch (Exception ex) { App.Log($"rename speaker: {ex.Message}"); }
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
