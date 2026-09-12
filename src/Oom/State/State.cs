using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Oom.Contracts;

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

    public static State? OpenReadOnly(string? databasePath = null)
    {
        var path = databasePath ?? VaultIdentity.ExistingDatabase();
        return path is not null && File.Exists(path) ? new State(null, null, path, StateAccess.ReadOnly) : null;
    }

    public string WorkDirectory => _workDirectory;

    public StateAccess Access => _access;

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

    public void RecordFlush(DateTimeOffset now, string sessionId, string reason, string outcome, int turns, int chars, string backend) =>
        Write("INSERT INTO flush_log(ts, session_id, reason, outcome, turns, chars, backend) VALUES ($ts, $s, $r, $o, $t, $c, $b)",
            ("$ts", Stamp(now)), ("$s", sessionId), ("$r", reason), ("$o", outcome), ("$t", turns), ("$c", chars), ("$b", backend));

    public bool IsStamped(string path, string mtime, long size) =>
        Text("SELECT 1 FROM sweep_stamps WHERE path = $p AND mtime = $m AND size = $s", ("$p", path), ("$m", mtime), ("$s", size)) is not null;

    public void WriteStamp(string path, string mtime, long size, string outcome) =>
        Write("INSERT INTO sweep_stamps(path, mtime, size, outcome) VALUES ($p, $m, $s, $o) ON CONFLICT(path) DO UPDATE SET mtime = $m, size = $s, outcome = $o",
            ("$p", path), ("$m", mtime), ("$s", size), ("$o", outcome));

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
        }
    }

    public long Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

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
            "CREATE TABLE IF NOT EXISTS sessions(session_id TEXT PRIMARY KEY, transcript_path TEXT, last_turn_index INTEGER, last_flush_ts TEXT, prompt_count INTEGER NOT NULL DEFAULT 0, first_seen TEXT);" +
            "CREATE TABLE IF NOT EXISTS flush_log(ts TEXT, session_id TEXT, reason TEXT, outcome TEXT, turns INTEGER, chars INTEGER, backend TEXT);" +
            "CREATE TABLE IF NOT EXISTS retry_queue(session_id TEXT PRIMARY KEY, attempts INTEGER, next_at TEXT, last_error TEXT);" +
            "CREATE TABLE IF NOT EXISTS sweep_stamps(path TEXT PRIMARY KEY, mtime TEXT, size INTEGER, outcome TEXT);" +
            "CREATE TABLE IF NOT EXISTS daily_ingest(name TEXT PRIMARY KEY, digest TEXT, status TEXT, attempts INTEGER, reasons TEXT, ts TEXT);" +
            "CREATE TABLE IF NOT EXISTS quarantine(digest TEXT PRIMARY KEY, source TEXT, reason TEXT, ts TEXT, path TEXT);" +
            "CREATE TABLE IF NOT EXISTS health(ts TEXT, component TEXT, level TEXT, code TEXT, key TEXT, detail TEXT);" +
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
