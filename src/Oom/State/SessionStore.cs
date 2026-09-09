using System.Globalization;

namespace Oom.Contracts;

/// <summary>One row of <c>sessions</c> joined with its <c>retry_queue</c> row, if it has one.</summary>
public sealed record SessionRow(string SessionId, string? TranscriptPath, int Cursor, int Attempts, DateTimeOffset NextAt, string? LastError);

/// <summary>
/// The write path's own two tables (spec 8): <c>sessions</c> holds the turn cursor so a
/// re-swept transcript costs no model call (Y-091), and <c>retry_queue</c> holds the attempts
/// so a rejected summary waits instead of vanishing (Y-012, Y-033).
/// </summary>
public sealed partial class State
{
    /// <summary>The session's cursor and queue state, or <c>null</c> when it was never seen.</summary>
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

    /// <summary>Moves the cursor. It is written only after the daily append succeeded (spec 6.3-7).</summary>
    public void WriteSessionRow(string sessionId, string? transcriptPath, int cursor) =>
        Write("INSERT INTO sessions(session_id, transcript_path, last_turn_index, last_flush_ts) VALUES ($id, $p, $c, $ts) " +
              "ON CONFLICT(session_id) DO UPDATE SET transcript_path = COALESCE($p, transcript_path), " +
              "last_turn_index = MAX(last_turn_index, $c), last_flush_ts = $ts",
            ("$id", sessionId), ("$p", (object?)transcriptPath ?? DBNull.Value), ("$c", cursor), ("$ts", Stamp(_clock.Now)));

    /// <summary>Queues a session with its exponential <c>next_at</c>; five attempts park it.</summary>
    public void WriteRetry(string sessionId, int attempts, DateTimeOffset nextAt, string? error) =>
        Write("INSERT INTO retry_queue(session_id, attempts, next_at, last_error) VALUES ($id, $a, $n, $e) " +
              "ON CONFLICT(session_id) DO UPDATE SET attempts = $a, next_at = $n, last_error = $e",
            ("$id", sessionId), ("$a", attempts), ("$n", Stamp(nextAt)), ("$e", (object?)error ?? DBNull.Value));

    /// <summary>A session that finally succeeded leaves the queue.</summary>
    public void ClearRetry(string sessionId) =>
        Write("DELETE FROM retry_queue WHERE session_id = $id", ("$id", sessionId));

    /// <summary>Due, not yet parked queue entries, oldest first — what a sweep works off (spec 6.3).</summary>
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

    /// <summary>The stamp a swept file carries, or <c>null</c> when the file was never swept.</summary>
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

    /// <summary>Sessions the <c>daily/</c> anchors already prove, so a lost database costs no re-summary.</summary>
    public void SeedCursors(IReadOnlyDictionary<string, int> anchored)
    {
        foreach (var (sessionId, cursor) in anchored)
            WriteSessionRow(sessionId, null, cursor);
    }

    /// <summary>The last quota sample sweep took; <c>context</c> prints it without measuring (spec 6.2).</summary>
    public string? ReadStatusLine(DateTimeOffset now)
    {
        var calls = Scalar($"SELECT COUNT(*) FROM calls WHERE ts >= '{Stamp(now.AddDays(-7))}'");
        lock (_gate)
        {
            using var command = Command("SELECT used_pct, \"window\" FROM kota ORDER BY ts DESC LIMIT 1", []);
            using var reader = command.ExecuteReader();
            var quota = reader.Read() && !reader.IsDBNull(0)
                ? $"kota %{reader.GetDouble(0):F0} ({reader.GetString(1)})"
                : "kota bilinmiyor";
            return $"{quota} · son 7 gün {calls} çağrı";
        }
    }

    /// <summary>The first column of one query, materialised; the reader never outlives its command.</summary>
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

    /// <summary>One <c>notified</c> row; the same (class, key) is toasted once in seven days (spec 6.8).</summary>
    public void RecordNotified(string notificationClass, string key, DateTimeOffset now) =>
        Write("INSERT INTO notified(class, key, ts) VALUES ($c, $k, $ts)",
            ("$c", notificationClass), ("$k", key), ("$ts", Stamp(now)));

    /// <summary>The queued notification line the next SessionStart block opens with (spec 6.2, 6.8).</summary>
    public string? ReadPendingNotification(DateTimeOffset now)
    {
        lock (_gate)
        {
            using var command = Command(
                "SELECT detail FROM health WHERE component = 'notify' AND ts >= $cutoff ORDER BY ts DESC LIMIT 1",
                [("$cutoff", Stamp(now.AddDays(-7)))]);
            using var reader = command.ExecuteReader();
            return reader.Read() ? reader.GetString(0) : null;
        }
    }

    /// <summary>One quota sample per sweep (spec 6.3); the reader never spends a reset credit (Y-057).</summary>
    public void RecordQuota(DateTimeOffset now, QuotaWindow window) =>
        Write("INSERT INTO kota(ts, \"window\", used_pct, resets_at) VALUES ($ts, $w, $u, $r)",
            ("$ts", Stamp(now)), ("$w", window.Name), ("$u", (object?)window.UsedPercent ?? DBNull.Value),
            ("$r", (object?)window.ResetsAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value));
}
