using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Oom.Contracts;

public sealed partial class State : IDisposable
{
    private const int StaleWarningHours = 24;

    private static readonly (string Table, string Column, int Days)[] Retention =
    [
        ("flush_log", "ts", 90),
        ("coverage", "ts", 90),
        ("health", "ts", 30)
    ];

    private readonly IClock _clock;
    private readonly SqliteConnection _connection;
    private readonly StateAccess _access;
    private readonly string _workDirectory;
    private readonly Lock _gate = new();

    public State() : this(null, null, null) { }

    public State(IClock? clock, IFileOperations? files = null, string? databasePath = null)
        : this(clock, files, databasePath, StateAccess.ReadWrite) { }

    public State(IClock? clock, IFileOperations? files, string? databasePath, StateAccess access)
    {
        _clock = clock ?? SystemClock.Instance;
        _access = access;
        var path = databasePath ?? (access is StateAccess.ReadOnly ? VaultIdentity.ExistingDatabase() : VaultPaths.StateDatabase());
        _workDirectory = path is null
            ? Path.Combine(Path.GetTempPath(), "oom", "state")
            : Path.GetDirectoryName(path)!;
        if (access is StateAccess.ReadWrite)
            Directory.CreateDirectory(_workDirectory);

        _connection = StateStore.Open(path, access);
        if (access is StateAccess.ReadWrite)
            HealthLedger.AllowWrites();
    }

    public static State? OpenReadOnly(string? databasePath = null)
    {
        var path = databasePath ?? VaultIdentity.ExistingDatabase();
        return path is not null && File.Exists(path) ? new State(null, null, path, StateAccess.ReadOnly) : null;
    }

    public string WorkDirectory => _workDirectory;

    public StateAccess Access => _access;

    public StateStats SweepRetention(DateTimeOffset now)
    {
        lock (_gate)
        {
            // Defect 4: a legacy state.db may predate `coverage` (or `health`); retention for
            // an absent table is a no-op rather than a "no such table" crash.
            foreach (var (table, column, days) in Retention)
            {
                if (!StateStore.TableExists(_connection, table))
                    continue;

                using var delete = Command($"DELETE FROM {table} WHERE {column} < $cutoff", [("$cutoff", (object)Stamp(now.AddDays(-days)))]);
                delete.ExecuteNonQuery();
            }

            return new StateStats(
                Scalar("SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()"),
                (int)Scalar("SELECT COUNT(*) FROM flush_log"),
                StateStore.TableExists(_connection, "health") ? (int)Scalar("SELECT COUNT(*) FROM health") : 0);
        }
    }

    public IReadOnlyList<HealthItem> WriteHealthConcurrently(IEnumerable<HealthItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var pending = items.ToArray();
        var now = _clock.Now;
        // Defect 4: a legacy state.db may have no `health` table at all. On a read-write open
        // Provision() has just created it, so this only ever skips an unwritable/legacy shape
        // rather than crashing the caller.
        if (!StateStore.TableExists(_connection, "health"))
            return [];

        Parallel.ForEach(pending, item =>
        {
            var observedAt = item.Stale ? now.AddHours(-(StaleWarningHours + 1)) : now;
            Write("INSERT INTO health(ts, component, level, code, key, detail) VALUES ($ts, $component, $level, $code, $key, $detail)",
                ("$ts", Stamp(observedAt)), ("$component", item.Component), ("$level", item.Level.ToString()), ("$code", item.Code), ("$key", item.Key), ("$detail", item.Detail));
        });

        var written = new List<HealthItem>();
        lock (_gate)
        {
            using var read = _connection.CreateCommand();
            read.CommandText = "SELECT ts, component, level, code, key, detail FROM health ORDER BY rowid";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var observedAt = DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
                written.Add(new HealthItem(
                    reader.GetString(1),
                    Enum.Parse<HealthLevel>(reader.GetString(2)),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    now - observedAt > TimeSpan.FromHours(StaleWarningHours)));
            }
        }

        return written;
    }

    public void RecordFlush(DateTimeOffset now, string sessionId, string reason, string outcome, int? turns, int? chars, string backend, int? masked = null) =>
        Write("INSERT INTO flush_log(ts, session_id, reason, outcome, turns, chars, backend, masked) VALUES ($ts, $s, $r, $o, $t, $c, $b, $m)",
            ("$ts", Stamp(now)), ("$s", sessionId), ("$r", reason), ("$o", outcome), ("$t", (object?)turns ?? DBNull.Value), ("$c", (object?)chars ?? DBNull.Value), ("$b", backend),
            ("$m", (object?)masked ?? DBNull.Value));

    /// <summary>
    /// NB-3: one health row per quarantined flush summary (component 'flush', code
    /// <see cref="QuarantineCode"/>, key = session id, detail names the red/ file and the
    /// reason). The session-start notice reads these rows.
    /// </summary>
    public void RecordQuarantine(DateTimeOffset now, string sessionId, string path, string reason) =>
        Write("INSERT INTO health(ts, component, level, code, key, detail) VALUES ($ts, 'flush', 'Warning', $code, $k, $d)",
            ("$ts", Stamp(now)), ("$code", QuarantineCode), ("$k", sessionId), ("$d", $"özet karantinaya alındı ({reason}): {path}"));

    public const string QuarantineCode = "karantina";

    /// <summary>F5-2: numeric coverage, one row per measurement; read back by <see cref="ReadCoverage"/>.</summary>
    public void RecordCoverage(DateTimeOffset now, int covered, int total, int windowDays) =>
        Write("INSERT INTO coverage(ts, covered, total, window_days) VALUES ($ts, $c, $t, $w)",
            ("$ts", Stamp(now)), ("$c", covered), ("$t", total), ("$w", windowDays));

    /// <summary>The latest coverage row, or null when none was written or the table predates 3.1.0.</summary>
    public CoverageReading? ReadCoverage()
    {
        lock (_gate)
        {
            if (!StateStore.TableExists(_connection, "coverage"))
                return null;

            using var command = Command("SELECT ts, covered, total, window_days FROM coverage ORDER BY rowid DESC LIMIT 1", []);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new CoverageReading(DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3))
                : null;
        }
    }

    /// <summary>Defect 3: the newest flush_log row a REAL sweep wrote (reason 'sweep' with a
    /// successful 'summary' outcome), or null when there is none / the table is unreadable.
    /// Only consulted when no `coverage` row exists, since coverage is the precise measure and
    /// this is the legacy fallback that keeps an upgraded vault's last real sweep visible.</summary>
    public DateTimeOffset? ReadLastSweep()
    {
        if (!HasColumn("flush_log", "ts") || !HasColumn("flush_log", "reason"))
            return null;

        // NOT SQL MAX(ts): that is a lexicographic max over the stored TEXT, and the stamps
        // carry different UTC offsets, so it would pick the wrong row. Parse and take the
        // real maximum; an unparseable stamp is skipped rather than reported as the newest.
        var newest = (DateTimeOffset?)null;
        foreach (var stamp in ReadColumn("SELECT ts FROM flush_log WHERE reason = 'sweep' AND outcome = 'summary'"))
            if (DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                && (newest is null || parsed > newest))
                newest = parsed;
        return newest;
    }

    public void WriteStamp(string path, string mtime, long size, string outcome) =>
        Write("INSERT INTO sweep_stamps(path, mtime, size, outcome) VALUES ($p, $m, $s, $o) ON CONFLICT(path) DO UPDATE SET mtime = $m, size = $s, outcome = $o",
            ("$p", path), ("$m", mtime), ("$s", size), ("$o", outcome));

    public void WriteDailyIngest(string name, string status, DateTimeOffset ts, string? reason = null, string? digest = null)
    {
        if (string.Equals(status, "ok", StringComparison.Ordinal))
            status = "ingested";
        var stamped = string.IsNullOrWhiteSpace(reason) ? string.Empty : $"[{Stamp(ts)}] {reason}";
        // `rejections` (SPEC F3-2, should-finding) counts only real rejections, separately
        // from `attempts` (every call for this name, any status): it goes up on 'rejected'
        // or the terminal 'parked', resets to 0 on a successful 'ingested'/'partial', and is
        // left untouched by 'retry'/'quarantined'/'low-confidence'/'fail:rebuild' — none of
        // those are rejections, so they must never push a day toward parking.
        // `digest` (should-finding, review): the content digest (CompileQueue.ContentDigest)
        // of the daily text this write is FOR, so CompileQueue.Pending can tell a genuinely
        // done day from one that was appended to after it was marked done. A caller that
        // does not have (or does not want to change) a digest passes null, which leaves
        // any existing recorded digest untouched rather than wiping it to NULL.
        // `last_success_ts` (defect 1) is the time of the newest compile that PUBLISHED notes
        // for this day. `ts` moves on every attempt, so a retry/rejected/quarantined/
        // fail:rebuild write would otherwise erase the real last-compile answer; this column
        // only moves forward on a success ('ingested'/'partial') and is otherwise left alone.
        Write("INSERT INTO daily_ingest(name, status, attempts, rejections, reasons, ts, digest, last_success_ts) " +
              "VALUES ($n, $s, 1, CASE WHEN $s IN ('rejected', 'parked') THEN 1 ELSE 0 END, $r, $t, $d, $ls) " +
              "ON CONFLICT(name) DO UPDATE SET status = $s, ts = $t, " +
              "attempts = COALESCE(daily_ingest.attempts, 0) + 1, " +
              "rejections = CASE " +
              "WHEN $s IN ('rejected', 'parked') THEN COALESCE(daily_ingest.rejections, 0) + 1 " +
              "WHEN $s IN ('ingested', 'partial') THEN 0 " +
              "ELSE COALESCE(daily_ingest.rejections, 0) END, " +
              "reasons = CASE WHEN $r = '' THEN daily_ingest.reasons " +
              "WHEN COALESCE(daily_ingest.reasons, '') = '' THEN $r " +
              "ELSE daily_ingest.reasons || char(10) || $r END, " +
              "digest = CASE WHEN $d IS NULL THEN daily_ingest.digest ELSE $d END, " +
              "last_success_ts = CASE WHEN $ls IS NULL THEN daily_ingest.last_success_ts ELSE $ls END",
            ("$n", name), ("$s", status), ("$r", stamped), ("$t", Stamp(ts)), ("$d", (object?)digest ?? DBNull.Value),
            ("$ls", status is "ingested" or "partial" ? (object)Stamp(ts) : DBNull.Value));
    }

    /// <summary>Reads a (name, nullable second column) pair per row (should-finding: used by
    /// CompileQueue.Pending to compare each terminal day's recorded content digest against
    /// its current one).</summary>
    public IReadOnlyList<(string Name, string? Value)> ReadNamedColumn(string sql)
    {
        lock (_gate)
        {
            using var command = Command(sql, []);
            using var reader = command.ExecuteReader();
            var values = new List<(string, string?)>();
            while (reader.Read())
                values.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            return values;
        }
    }

    /// <summary>True when <paramref name="table"/> exists and carries <paramref name="column"/>.
    /// R24 legacy tolerance: a state.db written by 3.0.x has no <c>coverage</c> table and no
    /// <c>daily_ingest.digest</c>/<c>last_success_ts</c> column, so every read-only path has
    /// to ask before it SELECTs rather than assume the current shape.</summary>
    public bool HasColumn(string table, string column)
    {
        lock (_gate)
        {
            if (!StateStore.TableExists(_connection, table))
                return false;
            return ColumnExists(_connection, table, column);
        }
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var probe = connection.CreateCommand();
        probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        return Convert.ToInt64(probe.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Every daily_ingest row as (name, status, digest) — the newest write per name
    /// is what CompileQueue.Pending judges, and a NON-done status means pending, so this must
    /// include the retry/rejected/quarantined/fail:rebuild rows, not just the done ones.
    /// Tolerates a legacy daily_ingest with no <c>digest</c> column (reported as null, i.e.
    /// "no digest", which R24(a) requires to count as done).</summary>
    public IReadOnlyList<DailyState> ReadDailyStates()
    {
        var rows = new List<DailyState>();
        lock (_gate)
        {
            if (!StateStore.TableExists(_connection, "daily_ingest"))
                return rows;

            var digest = ColumnExists(_connection, "daily_ingest", "digest") ? "digest" : "NULL";
            using var command = Command($"SELECT name, COALESCE(status, ''), {digest} FROM daily_ingest", []);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new DailyState(reader.GetString(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return rows;
    }

    /// <summary>The timestamps of every compile that actually published notes. `last_success_ts`
    /// is the authoritative column (it is not overwritten by a later failed retry);
    /// the 'ingested'/'partial' <c>ts</c> values are unioned in so a legacy state.db that has
    /// no <c>last_success_ts</c> column at all still reports its real compile history instead
    /// of "hiç" (never).</summary>
    public IReadOnlyList<string> ReadLastCompileStamps()
    {
        if (!HasColumn("daily_ingest", "ts"))
            return [];

        var hasSuccess = HasColumn("daily_ingest", "last_success_ts");
        var sql = hasSuccess
            ? "SELECT last_success_ts FROM daily_ingest WHERE last_success_ts IS NOT NULL AND last_success_ts <> ''" +
              " UNION ALL SELECT ts FROM daily_ingest WHERE status IN ('ingested','partial')"
            : "SELECT ts FROM daily_ingest WHERE status IN ('ingested','partial')";
        return ReadColumn(sql);
    }

    public void Dispose() => _connection.Dispose();

    public long Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private void Write(string sql, params (string Name, object Value)[] parameters) => Text(sql, parameters);

    private string? Text(string sql, params (string Name, object Value)[] parameters)
    {
        lock (_gate)
        {
            using var command = Command(sql, parameters);
            return command.ExecuteScalar()?.ToString();
        }
    }

    private SqliteCommand Command(string sql, (string Name, object Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>One daily_ingest row as CompileQueue.Pending needs it. A null <see cref="Digest"/>
/// is both "no digest recorded" and "this legacy state.db has no digest column" — R24(a)
/// makes both count as "no digest", i.e. a done day stays done.</summary>
public sealed record DailyState(string Name, string Status, string? Digest);

public enum StateAccess
{
    ReadWrite,

    ReadOnly
}

public sealed class StateSchemaException(string message, Exception? inner = null) : Exception(message, inner);

internal static class StateStore
{
    internal static SqliteConnection Open(string? path, StateAccess access)
    {
        if (path is null)
        {
            if (access is StateAccess.ReadOnly)
                throw new StateSchemaException("salt okunur durum açılışı için bir veritabanı yolu gerekir.");
            var memory = new SqliteConnection("Data Source=:memory:");
            memory.Open();
            Provision(memory, persistent: false);
            return memory;
        }

        if (access is StateAccess.ReadOnly)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"durum veritabanı yok: {path}", path);
            var reader = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Cache=Shared");
            reader.Open();
            return reader;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection($"Data Source={path};Cache=Shared");
        connection.Open();
        var retired = RetireOlderShape(ref connection, path);
        try
        {
            Provision(connection, persistent: true);
            MigrateDailyIngest(connection);
            if (retired is not null)
            {
                using var note = connection.CreateCommand();
                note.CommandText = "INSERT INTO health(ts, component, level, code, key, detail) VALUES ($ts, 'state', 'Info', 'eski-sema', 'state.db', $d)";
                note.Parameters.AddWithValue("$ts", DateTimeOffset.Now.ToString("O"));
                note.Parameters.AddWithValue("$d", $"Eski şemalı durum dosyası kenara alındı, yenisi sıfırdan kuruldu: {retired}");
                note.ExecuteNonQuery();
            }
            return connection;
        }
        catch (SqliteException error)
        {
            connection.Dispose();
            throw new StateSchemaException(
                $"durum veritabanı açılamadı: {path} — {error.Message}. " +
                "Dosya silinip yeniden kurulabilir. Teşhis: oom doctor", error);
        }
    }

    private static string? RetireOlderShape(ref SqliteConnection connection, string path)
    {
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "PRAGMA user_version";
            var stamped = Convert.ToInt64(probe.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture) != 0;
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name IN ('prompt_count', 'last_prompt_ts')";
            var sessionsCurrent = Convert.ToInt64(probe.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture) == 2;
            // BLOCKING review finding (F3-2 park counter): daily_ingest gaining a `rejections`
            // column used to retire the WHOLE database (every daily_ingest row — last-compile
            // timestamps, parked/ingested status — thrown away) on the very first 3.1.0 open,
            // violating R12 ("son derleme" must read the real history) and un-parking every
            // parked day. `rejections` is now migrated in place by MigrateDailyIngestRejections
            // below; it no longer participates in the decision to retire the whole file.
            if (!stamped && (sessionsCurrent || !TableExists(connection, "sessions")))
                return null;
        }
        connection.Dispose();
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={path};Cache=Shared"));
        var retired = $"{path}.eski-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        File.Move(path, retired);
        foreach (var side in new[] { "-wal", "-shm" })
            if (File.Exists(path + side)) File.Move(path + side, retired + side);
        connection = new SqliteConnection($"Data Source={path};Cache=Shared");
        connection.Open();
        return retired;
    }

    // BLOCKING review finding fix (F3-2, R12): migrates a pre-3.1.0 daily_ingest table in
    // place instead of retiring the whole state.db. A legacy row has no record of how many
    // times it was actually REJECTED (only the overall `attempts` counter and its terminal
    // `status`), so the seed is defensible, not exact: 'parked' can only be reached at
    // Compile.MaxAttempts (3), so it seeds the max; 'rejected' inherits its own `attempts`
    // (an overcount there only moves a day toward parked sooner — it can never make a
    // genuinely pending day look done); every other status seeds 0 (unchanged from the
    // column default). Everything else — status, ts, reasons, digest — is left untouched.
    //
    // Defect 4: this is also the additive home for the columns a 3.0.x/legacy daily_ingest
    // lacks — `digest` and `last_success_ts` (defect 1). ALTER TABLE ... ADD COLUMN only,
    // never a drop/recreate, so existing rows and their history survive (LegacyFreshnessKabul
    // ReadWriteOpen_AddsMissingDailyColumnsInPlace_AndPreservesRows).
    private static void MigrateDailyIngest(SqliteConnection connection)
    {
        if (!TableExists(connection, "daily_ingest"))
            return;

        AddColumn(connection, "daily_ingest", "digest", "TEXT");
        AddColumn(connection, "daily_ingest", "last_success_ts", "TEXT");

        if (ColumnCount(connection, "daily_ingest", "rejections") == 1)
            return;

        using var transaction = connection.BeginTransaction();
        using (var alter = connection.CreateCommand())
        {
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE daily_ingest ADD COLUMN rejections INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
        using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText =
                "UPDATE daily_ingest SET rejections = CASE " +
                "WHEN status = 'parked' THEN 3 " +
                "WHEN status = 'rejected' THEN COALESCE(attempts, 1) " +
                "ELSE 0 END";
            seed.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static int ColumnCount(SqliteConnection connection, string table, string column)
    {
        using var probe = connection.CreateCommand();
        probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        return Convert.ToInt32(probe.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    internal static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static void Provision(SqliteConnection connection, bool persistent)
    {
        Execute(connection, "PRAGMA busy_timeout=5000;");
        if (persistent)
            Execute(connection, "PRAGMA journal_mode=WAL;");

        Execute(connection,
            "CREATE TABLE IF NOT EXISTS sessions(session_id TEXT PRIMARY KEY, transcript_path TEXT, last_turn_index INTEGER, last_flush_ts TEXT, prompt_count INTEGER NOT NULL DEFAULT 0, first_seen TEXT, last_prompt_ts TEXT);" +
            "CREATE TABLE IF NOT EXISTS flush_log(ts TEXT, session_id TEXT, reason TEXT, outcome TEXT, turns INTEGER, chars INTEGER, backend TEXT, masked INTEGER);" +
            "CREATE TABLE IF NOT EXISTS retry_queue(session_id TEXT PRIMARY KEY, attempts INTEGER, next_at TEXT, last_error TEXT);" +
            "CREATE TABLE IF NOT EXISTS sweep_stamps(path TEXT PRIMARY KEY, mtime TEXT, size INTEGER, outcome TEXT);" +
            "CREATE TABLE IF NOT EXISTS daily_ingest(name TEXT PRIMARY KEY, digest TEXT, status TEXT, attempts INTEGER, rejections INTEGER, reasons TEXT, ts TEXT, last_success_ts TEXT);" +
            "CREATE TABLE IF NOT EXISTS health(ts TEXT, component TEXT, level TEXT, code TEXT, key TEXT, detail TEXT);" +
            "CREATE TABLE IF NOT EXISTS notes(name TEXT PRIMARY KEY, title TEXT, aliases TEXT, tags TEXT, body TEXT, updated TEXT);" +
            "CREATE INDEX IF NOT EXISTS ix_notes_updated ON notes(updated);" +
            "CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(name UNINDEXED, title, aliases, tags, body);" +
            "CREATE TABLE IF NOT EXISTS oom_index_meta(generation INTEGER NOT NULL, manifest_digest TEXT NOT NULL, built_at TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS coverage(ts TEXT, covered INTEGER, total INTEGER, window_days INTEGER);");

        // Additive and in place: a live-shaped state.db keeps its rows and is never retired
        // for a column 3.1.0 added (user_version stays 0, see RetireOlderShape).
        AddColumn(connection, "flush_log", "masked", "INTEGER");
    }

    private static void AddColumn(SqliteConnection connection, string table, string column, string type)
    {
        if (!TableExists(connection, table) || ColumnCount(connection, table, column) > 0)
            return;

        try
        {
            Execute(connection, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
        }
        catch (SqliteException error) when (error.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // A concurrent opener added it between the probe and the ALTER.
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
