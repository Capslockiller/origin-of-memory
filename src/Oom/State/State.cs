using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Oom.Contracts;

/// <summary>
/// The single state store (spec 8): one SQLite database per vault, under
/// <c>%LOCALAPPDATA%\oom\&lt;vault-hash&gt;\state.db</c> because the vault itself is
/// synchronised by Google Drive and WAL files do not survive that (D9).
/// Nothing lives only here: every table is derivable from <c>daily/</c> anchors,
/// <c>knowledge/</c> and the transcript archive, which is why the file may simply be
/// deleted — the next open recreates the whole shape from scratch.
/// </summary>
public sealed partial class State : IDisposable
{
    private const int ReplaceAttempts = 5;
    private const int ReplaceBackoffMs = 200;
    private const int StaleWarningHours = 24;

    private static readonly (string Table, string Column, int Days)[] Retention =
    [
        ("flush_log", "ts", 90),
        ("health", "ts", 30)
    ];

    private readonly IClock _clock;
    private readonly IFileOperations _files;
    private readonly SqliteConnection _connection;
    private readonly StateAccess _access;
    private readonly string _workDirectory;
    private readonly Lock _gate = new();
    private long _temporaryCounter;

    public State() : this(null, null, null) { }

    public State(IClock? clock, IFileOperations? files = null, string? databasePath = null)
        : this(clock, files, databasePath, StateAccess.ReadWrite) { }

    /// <summary>
    /// Opens the state database. <see cref="StateAccess.ReadOnly"/> creates no directory and
    /// issues no DDL: a command that only reads may not bring a state root into existence, which
    /// is how 26 unattributable roots accumulated under one profile.
    /// </summary>
    public State(IClock? clock, IFileOperations? files, string? databasePath, StateAccess access)
    {
        _clock = clock ?? SystemClock.Instance;
        _files = files ?? new WindowsFileOperations();
        _access = access;
        var path = databasePath ?? (access is StateAccess.ReadOnly ? VaultIdentity.ExistingDatabase() : VaultPaths.StateDatabase());
        _workDirectory = path is null
            ? Path.Combine(Path.GetTempPath(), "oom", "state")
            : Path.GetDirectoryName(path)!;
        if (access is StateAccess.ReadWrite)
            Directory.CreateDirectory(_workDirectory);

        _connection = StateStore.Open(path, access);
    }

    /// <summary>
    /// Opens the vault's state database for reading without creating anything; <c>null</c> when
    /// there is no vault or the database has never been written.
    /// </summary>
    public static State? OpenReadOnly(string? databasePath = null)
    {
        var path = databasePath ?? VaultIdentity.ExistingDatabase();
        return path is not null && File.Exists(path) ? new State(null, null, path, StateAccess.ReadOnly) : null;
    }

    /// <summary>Where atomic writes land when the caller passes a relative name.</summary>
    public string WorkDirectory => _workDirectory;

    /// <summary>How this handle was opened.</summary>
    public StateAccess Access => _access;

    /// <summary>
    /// Spend aggregation. A Claude transcript republishes the same assistant
    /// message on every streaming update, retry and compaction rewrite, each copy
    /// carrying its own usage block; keying by <c>message.id</c> is the only way
    /// the ledger is not counted two or three times (scar Y-058). Ownership
    /// classes are summed from the same de-duplicated set so the classes always
    /// add up to the total (scar Y-059), and each response is assigned to the day
    /// of its own timestamp, never the session's last one (scar Y-060).
    /// </summary>
    public UsageSummary AggregateUsage(IEnumerable<UsageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        long input = 0, output = 0, cacheRead = 0;
        var byOwner = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var record in records)
        {
            if (!seen.Add(record.MessageId))
                continue;

            input += record.InputTokens;
            output += record.OutputTokens;
            cacheRead += record.CacheReadTokens;
            byOwner[record.Owner] = byOwner.GetValueOrDefault(record.Owner) + record.CacheReadTokens;
        }

        return new UsageSummary(input, output, cacheRead, byOwner);
    }

    /// <summary>
    /// Prunes every table to its retention window (spec 8) and reports what is
    /// left. The prune targets the very path the writer writes to; v0 pruned a
    /// different directory than the hook wrote to and 977 rows piled up unseen
    /// (scars Y-061 and Y-062).
    /// </summary>
    public StateStats SweepRetention(DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var (table, column, days) in Retention)
            {
                using var delete = Command($"DELETE FROM {table} WHERE {column} < $cutoff", [("$cutoff", (object)Stamp(now.AddDays(-days)))]);
                delete.ExecuteNonQuery();
            }

            return new StateStats(
                Scalar("SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()"),
                (int)Scalar("SELECT COUNT(*) FROM flush_log"),
                (int)Scalar("SELECT COUNT(*) FROM health"));
        }
    }

    /// <summary>
    /// Writes health rows from concurrent callers without losing one, and ages
    /// them: a warning older than 24 hours reads back as stale instead of looking
    /// live for three weeks (scar Y-063). An item handed in already marked stale
    /// keeps that age, so the flag survives a round trip.
    /// </summary>
    public IReadOnlyList<HealthItem> WriteHealthConcurrently(IEnumerable<HealthItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var pending = items.ToArray();
        var now = _clock.Now;

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

    /// <summary>
    /// Atomic write, hammered by <paramref name="writers"/> threads at once. The
    /// temporary name carries pid, thread id and a monotonic counter, because a
    /// pid-only name let two lane threads inside one process collide on the same
    /// temporary file; the replace itself retries five times at 200 ms because
    /// Windows fails it with a sharing violation while Drive or an antivirus has
    /// the file open (scars Y-064 and 10.1 #24). Returns the final content.
    /// </summary>
    public string AtomicWrite(string path, string content, int writers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(writers, 1);

        var target = Path.IsPathRooted(path) ? path : Path.Combine(_workDirectory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        Parallel.For(0, writers, _ => WriteOnce(target, content));

        foreach (var leftover in Directory.EnumerateFiles(Path.GetDirectoryName(target)!, Path.GetFileName(target) + ".*.tmp"))
            TryDelete(leftover);

        return File.ReadAllText(target, new UTF8Encoding(false));
    }

    /// <summary>One <c>flush_log</c> row per session outcome (spec 6.3-8).</summary>
    public void RecordFlush(DateTimeOffset now, string sessionId, string reason, string outcome, int turns, int chars, string backend) =>
        Write("INSERT INTO flush_log(ts, session_id, reason, outcome, turns, chars, backend) VALUES ($ts, $s, $r, $o, $t, $c, $b)",
            ("$ts", Stamp(now)), ("$s", sessionId), ("$r", reason), ("$o", outcome), ("$t", turns), ("$c", chars), ("$b", backend));

    /// <summary>Whether a file still carries the (mtime, size) it was swept with; an unchanged file is never reopened.</summary>
    public bool IsStamped(string path, string mtime, long size) =>
        Text("SELECT 1 FROM sweep_stamps WHERE path = $p AND mtime = $m AND size = $s", ("$p", path), ("$m", mtime), ("$s", size)) is not null;

    /// <summary>Stamps a swept file; a locked session is never stamped, so the next run sees it again (spec 6.3).</summary>
    public void WriteStamp(string path, string mtime, long size, string outcome) =>
        Write("INSERT INTO sweep_stamps(path, mtime, size, outcome) VALUES ($p, $m, $s, $o) ON CONFLICT(path) DO UPDATE SET mtime = $m, size = $s, outcome = $o",
            ("$p", path), ("$m", mtime), ("$s", size), ("$o", outcome));

    /// <summary>Y-117: e.g. "adopted", distinct from a real "ingested" compile.</summary>
    public void WriteDailyIngest(string name, string status, DateTimeOffset ts) =>
        Write("INSERT INTO daily_ingest(name, status, ts) VALUES ($n, $s, $t) ON CONFLICT(name) DO UPDATE SET status = $s, ts = $t",
            ("$n", name), ("$s", status), ("$t", Stamp(ts)));

    public void Dispose() => _connection.Dispose();

    private void WriteOnce(string target, string content)
    {
        var counter = Interlocked.Increment(ref _temporaryCounter);
        var temporary = $"{target}.{Environment.ProcessId}-{Environment.CurrentManagedThreadId}-{counter}.tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _files.Replace(temporary, target);
                return;
            }
            catch (IOException) when (attempt < ReplaceAttempts)
            {
                Thread.Sleep(ReplaceBackoffMs);
            }
            catch (UnauthorizedAccessException) when (attempt < ReplaceAttempts)
            {
                Thread.Sleep(ReplaceBackoffMs);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temporary file is harmless; doctor sweeps it later.
        }
    }

    /// <summary>One counted row, for doctor's summary lines (spec 6.8).</summary>
    public long Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    /// <summary>The last 7-day coverage the sweep measured, as (covered, total); null until a sweep has run.</summary>
    public (int Covered, int Total)? LastCoverage()
    {
        var detail = Text("SELECT detail FROM health WHERE component = 'sweep' AND code = 'kapsama' ORDER BY ts DESC LIMIT 1");
        var match = detail is null ? null : System.Text.RegularExpressions.Regex.Match(detail, @"(\d+)/(\d+)");
        return match is { Success: true } ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)) : null;
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

/// <summary>How a caller intends to use the state database.</summary>
public enum StateAccess
{
    /// <summary>The state root is created if missing and the schema is provisioned.</summary>
    ReadWrite,

    /// <summary>Nothing is created and no DDL runs; the file must already exist.</summary>
    ReadOnly
}

/// <summary>The state database could not be opened at all — not a schema verdict, an I/O one.</summary>
public sealed class StateSchemaException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The single owner of the state database's shape (spec 8), and now the whole of it: one
/// idempotent <c>CREATE TABLE IF NOT EXISTS</c> set, run on every writing open.
///
/// There is no <c>PRAGMA user_version</c>, no migration ladder, no backup-verify-migrate and no
/// views. The file carries nothing that cannot be rebuilt from <c>daily/</c>, <c>knowledge/</c>
/// and the transcript archive, so the honest contract is the simplest one: <b>the state file may
/// be deleted, and the next open recreates every table from scratch.</b> A ladder is what you
/// build when a file holds the only copy of something; this one never did, and maintaining the
/// ladder cost more than the thing it protected.
/// </summary>
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
            // No CreateDirectory and no DDL on this path: the read is the whole point.
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
            // A refused or aborted open owns no handle on the owner's file: the next attempt, and
            // the owner's own inspection of it, must not have to wait for this process to exit.
            connection.Dispose();
            throw new StateSchemaException(
                $"durum veritabanı açılamadı: {path} — {error.Message}. " +
                "Dosya silinip yeniden kurulabilir. Teşhis: oom doctor", error);
        }
    }

    /// <summary>
    /// The contract is "delete the file and the next open rebuilds it" — so a file written by an
    /// older build (a ladder stamp in <c>user_version</c>, or a <c>sessions</c> table without the
    /// columns this build creates) is not migrated: it is renamed to <c>state.db.eski-&lt;ts&gt;</c>
    /// next to itself, never deleted, and a fresh file takes its place. Returns the retired path.
    /// </summary>
    private static string? RetireOlderShape(ref SqliteConnection connection, string path)
    {
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "PRAGMA user_version";
            var stamped = Convert.ToInt64(probe.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture) != 0;
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name = 'prompt_count'";
            var current = Convert.ToInt64(probe.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture) == 1;
            if (!stamped && (current || !TableExists(connection, "sessions")))
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
            // sessions.prompt_count and sessions.first_seen belong to `oom nudge`: the count is
            // what the every-Nth-prompt reminder is keyed on, and first_seen is what tells the
            // sessionend flush whether Last-Session.md was touched during THIS session or before it.
            "CREATE TABLE IF NOT EXISTS sessions(session_id TEXT PRIMARY KEY, transcript_path TEXT, last_turn_index INTEGER, last_flush_ts TEXT, prompt_count INTEGER NOT NULL DEFAULT 0, first_seen TEXT);" +
            "CREATE TABLE IF NOT EXISTS flush_log(ts TEXT, session_id TEXT, reason TEXT, outcome TEXT, turns INTEGER, chars INTEGER, backend TEXT);" +
            "CREATE TABLE IF NOT EXISTS retry_queue(session_id TEXT PRIMARY KEY, attempts INTEGER, next_at TEXT, last_error TEXT);" +
            "CREATE TABLE IF NOT EXISTS sweep_stamps(path TEXT PRIMARY KEY, mtime TEXT, size INTEGER, outcome TEXT);" +
            "CREATE TABLE IF NOT EXISTS daily_ingest(name TEXT PRIMARY KEY, digest TEXT, status TEXT, attempts INTEGER, reasons TEXT, ts TEXT);" +
            "CREATE TABLE IF NOT EXISTS quarantine(digest TEXT PRIMARY KEY, source TEXT, reason TEXT, ts TEXT, path TEXT);" +
            "CREATE TABLE IF NOT EXISTS health(ts TEXT, component TEXT, level TEXT, code TEXT, key TEXT, detail TEXT);" +
            // The retrieval index. Y-172: ix_notes_updated belongs to this owner and a DROP TABLE
            // in the retrieval side would take it with the table, so its survival is the evidence
            // that the rebuild empties and refills rather than dropping and recreating.
            "CREATE TABLE IF NOT EXISTS notes(name TEXT PRIMARY KEY, title TEXT, aliases TEXT, tags TEXT, body TEXT, updated TEXT);" +
            "CREATE INDEX IF NOT EXISTS ix_notes_updated ON notes(updated);" +
            "CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(name UNINDEXED, title, aliases, tags, body);" +
            "CREATE TABLE IF NOT EXISTS oom_index_meta(generation INTEGER NOT NULL, manifest_digest TEXT NOT NULL, built_at TEXT NOT NULL);");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
