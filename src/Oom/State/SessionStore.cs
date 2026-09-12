using System.Globalization;

namespace Oom.Contracts;

public sealed record SessionRow(string SessionId, string? TranscriptPath, int Cursor, int Attempts, DateTimeOffset NextAt, string? LastError);

public sealed partial class State
{
    public SessionRow? ReadSessionRow(string sessionId)
    {
        lock (_gate)
        {
            using var command = Command(
                "SELECT s.transcript_path, s.last_turn_index, r.attempts, r.next_at, r.last_error " +
                "FROM (SELECT $id AS id) k " +
                "LEFT JOIN sessions s ON s.session_id = k.id " +
                "LEFT JOIN retry_queue r ON r.session_id = k.id", [("$id", sessionId)]);

            using var reader = command.ExecuteReader();
            if (!reader.Read() || (reader.IsDBNull(0) && reader.IsDBNull(1) && reader.IsDBNull(2)))
                return null;

            return new SessionRow(
                sessionId,
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? -1 : reader.GetInt32(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                reader.IsDBNull(3) ? default : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : reader.GetString(4));
        }
    }

    public void WriteSessionRow(string sessionId, string? transcriptPath, int cursor) =>
        Write("INSERT INTO sessions(session_id, transcript_path, last_turn_index, last_flush_ts) VALUES ($id, $p, $c, $ts) " +
              "ON CONFLICT(session_id) DO UPDATE SET transcript_path = COALESCE($p, transcript_path), " +
              "last_turn_index = MAX(last_turn_index, $c), last_flush_ts = $ts",
            ("$id", sessionId), ("$p", (object?)transcriptPath ?? DBNull.Value), ("$c", cursor), ("$ts", Stamp(_clock.Now)));

    public void WriteRetry(string sessionId, int attempts, DateTimeOffset nextAt, string? error) =>
        Write("INSERT INTO retry_queue(session_id, attempts, next_at, last_error) VALUES ($id, $a, $n, $e) " +
              "ON CONFLICT(session_id) DO UPDATE SET attempts = $a, next_at = $n, last_error = $e",
            ("$id", sessionId), ("$a", attempts), ("$n", Stamp(nextAt)), ("$e", (object?)error ?? DBNull.Value));

    public void ClearRetry(string sessionId) =>
        Write("DELETE FROM retry_queue WHERE session_id = $id", ("$id", sessionId));

    public IReadOnlyList<SessionRow> ReadRetryQueue(DateTimeOffset now, int maxAttempts, int limit)
    {
        lock (_gate)
        {
            using var command = Command(
                "SELECT r.session_id, s.transcript_path, s.last_turn_index, r.attempts, r.next_at, r.last_error " +
                "FROM retry_queue r LEFT JOIN sessions s ON s.session_id = r.session_id " +
                "WHERE r.attempts < $max AND r.next_at <= $now ORDER BY r.next_at LIMIT $limit",
                [("$max", maxAttempts), ("$now", Stamp(now)), ("$limit", limit)]);

            using var reader = command.ExecuteReader();
            var rows = new List<SessionRow>();
            while (reader.Read())
            {
                rows.Add(new SessionRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? -1 : reader.GetInt32(2),
                    reader.GetInt32(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }

            return rows;
        }
    }

    public (string Mtime, long Size, string Outcome)? ReadStamp(string path)
    {
        lock (_gate)
        {
            using var command = Command("SELECT mtime, size, outcome FROM sweep_stamps WHERE path = $p", [("$p", path)]);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? (reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2))
                : null;
        }
    }

    public void SeedCursors(IReadOnlyDictionary<string, int> anchored)
    {
        foreach (var (sessionId, cursor) in anchored)
            WriteSessionRow(sessionId, null, cursor);
    }

    public int CountPrompt(string sessionId, DateTimeOffset now)
    {
        Write("INSERT INTO sessions(session_id, last_turn_index, prompt_count, first_seen) VALUES ($id, -1, 1, $ts) " +
              "ON CONFLICT(session_id) DO UPDATE SET prompt_count = prompt_count + 1, first_seen = COALESCE(first_seen, $ts)",
            ("$id", sessionId), ("$ts", Stamp(now)));
        return (int)ScalarFor("SELECT prompt_count FROM sessions WHERE session_id = $id", sessionId);
    }

    public (int PromptCount, DateTimeOffset? FirstSeen) ReadSessionActivity(string sessionId)
    {
        lock (_gate)
        {
            using var command = Command("SELECT prompt_count, first_seen FROM sessions WHERE session_id = $id", [("$id", sessionId)]);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return (0, null);

            return (reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture));
        }
    }

    public string? TakeReflectionDebt()
    {
        lock (_gate)
        {
            using var read = Command("SELECT detail FROM health WHERE component = 'hafiza' AND code = 'yansima-borcu' ORDER BY rowid DESC LIMIT 1", []);
            string? detail;
            using (var reader = read.ExecuteReader())
                detail = reader.Read() ? reader.GetString(0) : null;

            if (detail is null || _access is StateAccess.ReadOnly)
                return detail;

            using var delete = Command("DELETE FROM health WHERE component = 'hafiza' AND code = 'yansima-borcu'", []);
            delete.ExecuteNonQuery();
            return detail;
        }
    }

    public IReadOnlyList<string> ReadColumn(string sql)
    {
        lock (_gate)
        {
            using var command = Command(sql, []);
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read())
                values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));

            return values;
        }
    }

    private long ScalarFor(string sql, string sessionId)
    {
        lock (_gate)
        {
            using var command = Command(sql, [("$id", sessionId)]);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
        }
    }
}
