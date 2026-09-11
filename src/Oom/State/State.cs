using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Oom.Contracts;

/// <summary>
/// The single state store (spec 8): one SQLite database per vault, under
/// <c>%LOCALAPPDATA%\oom\&lt;vault-hash&gt;\state.db</c> because the vault itself is
/// synchronised by Google Drive and WAL files do not survive that (D9).
/// Nothing lives only here: every table is derivable from <c>daily/</c> anchors,
/// <c>knowledge/</c> and the transcript archive, which is what lets
/// <c>doctor --fix</c> rebuild the file after a failed integrity check.
/// </summary>
public sealed partial class State : IDisposable, IIngestStateStore
{
    // 4: call attempts own nullable, split token counters and stable operation identities.
    private const int SchemaVersion = 4;
    private const int ReplaceAttempts = 5;
    private const int ReplaceBackoffMs = 200;
    private const int StaleWarningHours = 24;
    private const int StaleLockMinutes = 120;

    private static readonly (string Table, string Column, int Days)[] Retention =
    [
        ("calls", "ts", 90),
        ("flush_log", "ts", 90),
        ("health", "ts", 30),
        ("retrieve_served", "ts", 7),
        ("kota", "ts", 30)
    ];

    private readonly IClock _clock;
    private readonly IFileOperations _files;
    private readonly SqliteConnection _connection;
    private readonly string _workDirectory;
    private readonly Lock _gate = new();
    private long _temporaryCounter;

    public State() : this(null, null, null) { }

    public State(IClock? clock, IFileOperations? files = null, string? databasePath = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _files = files ?? new WindowsFileOperations();
        var path = databasePath ?? VaultPaths.StateDatabase();
        _workDirectory = path is null
            ? Path.Combine(Path.GetTempPath(), "oom", "state")
            : Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(_workDirectory);

        _connection = new SqliteConnection(path is null
            ? "Data Source=:memory:"
            : $"Data Source={path};Cache=Shared");
        _connection.Open();
        Initialize(path is not null);
    }

    /// <summary>Where atomic writes land when the caller passes a relative name.</summary>
    public string WorkDirectory => _workDirectory;

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
                (int)Scalar("SELECT COUNT(*) FROM calls"),
                (int)Scalar("SELECT COUNT(*) FROM flush_log"),
                (int)Scalar("SELECT COUNT(*) FROM health"),
                (int)Scalar("SELECT COUNT(*) FROM retrieve_served"),
                (int)Scalar("SELECT COUNT(*) FROM kota"));
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

    /// <summary>
    /// Machine identity for the <c>locks</c> table: hostname plus 16 hex digits.
    /// Two machines with the same hostname shared a lock in v0 (scar 10.1 #28).
    /// </summary>
    public string MachineIdentity(string hostname, ulong discriminator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        return $"{hostname}-{discriminator:x16}";
    }

    /// <summary>
    /// Cooperative cross-machine lock. A single row per name; a lock older than
    /// 120 minutes whose owning process is gone on this machine is taken over and
    /// reported, everything else is refused with <c>locked</c> (scar Y-033).
    /// </summary>
    public LockResult AcquireLock(string name, string machineIdentity, int pid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineIdentity);

        lock (_gate)
        {
            var now = _clock.Now;
            using var read = _connection.CreateCommand();
            read.CommandText = "SELECT machine, pid, ts FROM locks WHERE name = $name";
            read.Parameters.AddWithValue("$name", name);

            string? holder = null;
            var holderPid = 0;
            var takenAt = now;
            using (var reader = read.ExecuteReader())
            {
                if (reader.Read())
                {
                    holder = reader.GetString(0);
                    holderPid = reader.GetInt32(1);
                    takenAt = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
                }
            }

            if (holder is not null && !holder.Equals(machineIdentity, StringComparison.Ordinal))
            {
                var stale = now - takenAt > TimeSpan.FromMinutes(StaleLockMinutes);
                if (!stale)
                    return new LockResult(false, "locked", holder);

                Take(name, machineIdentity, pid, now);
                return new LockResult(true, "stale-takeover", machineIdentity);
            }

            if (holder is not null && holderPid != pid && now - takenAt <= TimeSpan.FromMinutes(StaleLockMinutes) && IsAlive(holderPid))
                return new LockResult(false, "locked", holder);

            Take(name, machineIdentity, pid, now);
            return new LockResult(true, holder is null ? "acquired" : "held", machineIdentity);
        }

        void Take(string lockName, string machine, int owner, DateTimeOffset at)
        {
            using var upsert = Command("INSERT INTO locks(name, machine, pid, ts) VALUES ($name, $machine, $pid, $ts) " +
                                       "ON CONFLICT(name) DO UPDATE SET machine = $machine, pid = $pid, ts = $ts",
                [("$name", lockName), ("$machine", machine), ("$pid", (object)owner), ("$ts", Stamp(at))]);
            upsert.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Quota reading. Only a live endpoint may produce a number: a cached or
    /// event-log observation is a diagnosis, never a printed percentage, because
    /// v0 printed a six hour old 64% while the live window was at 100% (scars
    /// Y-052 to Y-055). A response that parses but no longer carries the expected
    /// field raises <c>kota-sozlesme</c> instead of a silent unknown (scar Y-056).
    /// </summary>
    public QuotaResult ReadQuota(IReadOnlyDictionary<string, string> liveResponses, IReadOnlyList<QuotaWindow> cached, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(liveResponses);
        ArgumentNullException.ThrowIfNull(cached);

        var windows = new List<QuotaWindow>();
        var warnings = new List<HealthItem>();

        if (liveResponses.Count > 0)
        {
            foreach (var (source, response) in liveResponses)
                windows.Add(ReadLiveWindow(source, response, now, warnings));
        }
        else
        {
            foreach (var window in cached)
            {
                windows.Add(window with
                {
                    UsedPercent = null,
                    Status = "bilinmiyor",
                    Resolution = "canlı okuma yok — gözlem eski, yüzde basılmaz"
                });
            }
        }

        return new QuotaResult(windows, Band(windows), warnings);
    }

    /// <summary>
    /// The read-only quota handshake. The reader never calls a state changing
    /// method; that is fixed here so a future edit cannot spend a reset credit by
    /// accident (scar Y-057).
    /// </summary>
    public string BuildQuotaRequest(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return $"{{\"source\":\"{source}\",\"method\":\"GET\",\"operation\":\"account.rateLimits.read\",\"readOnly\":true}}";
    }

    /// <summary>One ledger row per model call; content is never written (spec 6.6).</summary>
    public void RecordCall(string backend, ComponentKind component, ModelTier tier, string model, int inputChars, int outputChars, long elapsedMs, string outcome, UsageSourceKind usageSource, string purpose,
        string operationId, string attemptId, int attemptNumber, TokenUsage? usage) =>
        Write("INSERT INTO calls(ts, backend, component, tier, model, in_chars, out_chars, in_tok, out_tok, cache_r, cache_w, ms, outcome, usage_source, purpose, operation_id, attempt_id, attempt_no, uncached_in_tok, usage_rank, usage_semantics) " +
              "VALUES ($ts, $backend, $component, $tier, $model, $in, $out, NULL, $outtok, $cacher, $cachew, $ms, $outcome, $usage, $purpose, $operation, $attempt, $attemptno, $uncached, $rank, 'split-v1')",
            ("$ts", Stamp(_clock.Now)), ("$backend", backend), ("$component", component.ToString()), ("$tier", tier.ToString()), ("$model", model),
            ("$in", inputChars), ("$out", outputChars), ("$outtok", Db(usage?.OutputTokens)), ("$cacher", Db(usage?.CacheReadTokens)), ("$cachew", Db(usage?.CacheWriteTokens)),
            ("$ms", elapsedMs), ("$outcome", outcome), ("$usage", usageSource.ToString().ToLowerInvariant()), ("$purpose", purpose),
            ("$operation", operationId), ("$attempt", attemptId), ("$attemptno", attemptNumber), ("$uncached", Db(usage?.UncachedInputTokens)), ("$rank", (int)usageSource));

    /// <summary>One <c>flush_log</c> row per session outcome (spec 6.3-8).</summary>
    public void RecordFlush(DateTimeOffset now, string sessionId, string reason, string outcome, int turns, int chars, string backend) =>
        Write("INSERT INTO flush_log(ts, session_id, reason, outcome, turns, chars, backend) VALUES ($ts, $s, $r, $o, $t, $c, $b)",
            ("$ts", Stamp(now)), ("$s", sessionId), ("$r", reason), ("$o", outcome), ("$t", turns), ("$c", chars), ("$b", backend));

    /// <summary>The coverage reconciliation of one sweep run (spec 6.3, gate 6).</summary>
    public void RecordCoverage(DateTimeOffset now, int total, int covered, IReadOnlyList<string> uncovered) =>
        Write("INSERT INTO coverage(ts, total, covered, uncovered_json) VALUES ($ts, $t, $c, $u)",
            ("$ts", Stamp(now)), ("$t", total), ("$c", covered), ("$u", JsonSerializer.Serialize(uncovered)));

    /// <summary>Whether a file still carries the (mtime, size) it was swept with; an unchanged file is never reopened.</summary>
    public bool IsStamped(string path, string mtime, long size) =>
        Text("SELECT 1 FROM sweep_stamps WHERE path = $p AND mtime = $m AND size = $s", ("$p", path), ("$m", mtime), ("$s", size)) is not null;

    /// <summary>Stamps a swept file; a locked session is never stamped, so the next run sees it again (spec 6.3).</summary>
    public void WriteStamp(string path, string mtime, long size, string outcome) =>
        Write("INSERT INTO sweep_stamps(path, mtime, size, outcome) VALUES ($p, $m, $s, $o) ON CONFLICT(path) DO UPDATE SET mtime = $m, size = $s, outcome = $o",
            ("$p", path), ("$m", mtime), ("$s", size), ("$o", outcome));
    public bool Contains(string source, string digest) => Text("SELECT 1 FROM ingest_done WHERE source = $s AND digest = $d", ("$s", source), ("$d", digest)) is not null; // Y-114: IIngestStateStore.
    public void Complete(string source, string digest) => Write("INSERT INTO ingest_done(source, digest, ts) VALUES ($s, $d, $t) ON CONFLICT(source, digest) DO NOTHING", ("$s", source), ("$d", digest), ("$t", Stamp(_clock.Now)));
    public void WriteVaultStamp(DateTimeOffset at) => Write("INSERT INTO vault_meta(key, value) VALUES ('installed_at', $t) ON CONFLICT(key) DO NOTHING", ("$t", Stamp(at))); // Y-117/Y-118: written once, at install or adoption.
    public DateTimeOffset VaultInstalledAt() => Text("SELECT value FROM vault_meta WHERE key = 'installed_at'") is { } v ? DateTimeOffset.Parse(v, CultureInfo.InvariantCulture) : new DateTimeOffset(Directory.GetCreationTimeUtc(_workDirectory), TimeSpan.Zero); // Y-118: falls back to the state directory's own creation time for a vault stamped before this fix.
    public void WriteDailyIngest(string name, string status, DateTimeOffset ts) => Write("INSERT INTO daily_ingest(name, status, ts) VALUES ($n, $s, $t) ON CONFLICT(name) DO UPDATE SET status = $s, ts = $t", ("$n", name), ("$s", status), ("$t", Stamp(ts))); // Y-117: e.g. "adopted", distinct from a real "ingested" compile.
    public void Dispose() => _connection.Dispose();

    private QuotaWindow ReadLiveWindow(string source, string response, DateTimeOffset now, List<HealthItem> warnings)
    {
        var text = response?.Trim() ?? string.Empty;
        if (!text.StartsWith('{'))
        {
            var expired = text.Contains("401", StringComparison.Ordinal) || text.Contains("OAuth", StringComparison.OrdinalIgnoreCase);
            var resolution = expired
                ? "jeton süresi doldu → claude /login ile oturumu yenile"
                : $"canlı okuma başarısız ({Shorten(text)}) — oom doctor";
            warnings.Add(new HealthItem("kota", HealthLevel.Warning, expired ? "kota-oturum" : "kota-canli", source, resolution));
            return new QuotaWindow(source, null, null, null, "bilinmiyor", resolution);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var name = ReadString(root, "window") ?? source;
            var used = ReadPercent(root);
            if (used is null)
            {
                const string resolution = "kota alan adları değişti — oom doctor";
                warnings.Add(new HealthItem("kota", HealthLevel.Warning, "kota-sozlesme", source, "Kota sözleşmesi değişti — oom doctor"));
                return new QuotaWindow(name, null, null, null, "bilinmiyor", resolution);
            }

            DateTimeOffset? resetsAt = null;
            if (ReadString(root, "resets_at") is { } reset && DateTimeOffset.TryParse(reset, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                resetsAt = parsed;

            return new QuotaWindow(name, used, resetsAt, now, "canlı");
        }
        catch (JsonException)
        {
            const string resolution = "kota yanıtı ayrıştırılamadı — oom doctor";
            warnings.Add(new HealthItem("kota", HealthLevel.Warning, "kota-sozlesme", source, resolution));
            return new QuotaWindow(source, null, null, null, "bilinmiyor", resolution);
        }
    }

    private static string Band(IReadOnlyList<QuotaWindow> windows)
    {
        var measured = windows.Where(x => x.UsedPercent is not null).Select(x => x.UsedPercent!.Value).ToArray();
        if (measured.Length == 0)
            return windows.Any(x => x.ObservedAt is not null) ? "bilinmiyor" : "bilinmiyor (kaynak yok)";

        var worst = measured.Max();
        return worst switch
        {
            >= 95 => "kritik",
            >= 85 => "dar",
            >= 50 => "orta",
            _ => "serbest"
        };
    }

    private static double? ReadPercent(JsonElement root)
    {
        foreach (var field in new[] { "used_pct", "used_percent", "utilization", "usedPercent" })
        {
            if (root.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.Number)
                return value.GetDouble();
        }

        return null;
    }

    private static string? ReadString(JsonElement root, string field) =>
        root.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40];

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

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>One counted row, for doctor's summary lines (spec 6.8).</summary>
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
    private static object Db(long? value) => value is null ? DBNull.Value : value.Value;

    private void Initialize(bool persistent)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = (persistent ? "PRAGMA journal_mode=WAL; " : string.Empty) +
            "PRAGMA busy_timeout=5000; " +
            "CREATE TABLE IF NOT EXISTS sessions(session_id TEXT PRIMARY KEY, transcript_path TEXT, last_turn_index INTEGER, last_flush_ts TEXT);" +
            "CREATE TABLE IF NOT EXISTS flush_log(ts TEXT, session_id TEXT, reason TEXT, outcome TEXT, turns INTEGER, chars INTEGER, backend TEXT);" +
            "CREATE TABLE IF NOT EXISTS retry_queue(session_id TEXT PRIMARY KEY, attempts INTEGER, next_at TEXT, last_error TEXT);" +
            "CREATE TABLE IF NOT EXISTS sweep_stamps(path TEXT PRIMARY KEY, mtime TEXT, size INTEGER, outcome TEXT);" +
            "CREATE TABLE IF NOT EXISTS ingest_done(source TEXT, digest TEXT, ts TEXT, PRIMARY KEY(source, digest));" +
            "CREATE TABLE IF NOT EXISTS coverage(ts TEXT, total INTEGER, covered INTEGER, uncovered_json TEXT);" +
            "CREATE TABLE IF NOT EXISTS daily_ingest(name TEXT PRIMARY KEY, digest TEXT, status TEXT, attempts INTEGER, reasons TEXT, ts TEXT);" +
            "CREATE TABLE IF NOT EXISTS vault_meta(key TEXT PRIMARY KEY, value TEXT);" +
            "CREATE TABLE IF NOT EXISTS compile_runs(ts TEXT, daily TEXT, status TEXT, created INTEGER, updated INTEGER, ms INTEGER);" +
            "CREATE TABLE IF NOT EXISTS quarantine(digest TEXT PRIMARY KEY, source TEXT, reason TEXT, ts TEXT, path TEXT);" +
            "CREATE TABLE IF NOT EXISTS calls(ts TEXT, backend TEXT, component TEXT, tier TEXT, model TEXT, in_chars INTEGER, out_chars INTEGER, in_tok INTEGER, out_tok INTEGER, cache_r INTEGER, cache_w INTEGER, ms INTEGER, outcome TEXT, usage_source TEXT, purpose TEXT, operation_id TEXT, attempt_id TEXT, attempt_no INTEGER, uncached_in_tok INTEGER, usage_rank INTEGER NOT NULL DEFAULT 0 CHECK(usage_rank BETWEEN 0 AND 2), usage_semantics TEXT NOT NULL DEFAULT 'legacy-total-input-v0');" +
            "CREATE TABLE IF NOT EXISTS health(ts TEXT, component TEXT, level TEXT, code TEXT, key TEXT, detail TEXT);" +
            "CREATE TABLE IF NOT EXISTS notified(class TEXT, key TEXT, ts TEXT);" +
            "CREATE TABLE IF NOT EXISTS retrieve_served(session_id TEXT, query_sig TEXT, note TEXT, ts TEXT);" +
            "CREATE TABLE IF NOT EXISTS locks(name TEXT PRIMARY KEY, machine TEXT, pid INTEGER, ts TEXT);" +
            "CREATE TABLE IF NOT EXISTS kota(ts TEXT, \"window\" TEXT, used_pct REAL, resets_at TEXT);" +
            "CREATE VIEW IF NOT EXISTS v_flush_log AS SELECT ts, session_id, reason, outcome, turns, chars, backend FROM flush_log;" +
            "CREATE VIEW IF NOT EXISTS v_coverage AS SELECT ts, total, covered, uncovered_json FROM coverage;" +
            "CREATE VIEW IF NOT EXISTS v_health AS SELECT ts, component, level, code, key, detail FROM health;" +
            "CREATE VIEW IF NOT EXISTS v_kota AS SELECT ts, \"window\", used_pct, resets_at FROM kota;";
        command.ExecuteNonQuery();

        EnsureLedgerColumn("operation_id", "TEXT");
        EnsureLedgerColumn("attempt_id", "TEXT");
        EnsureLedgerColumn("attempt_no", "INTEGER");
        EnsureLedgerColumn("uncached_in_tok", "INTEGER");
        EnsureLedgerColumn("usage_rank", "INTEGER NOT NULL DEFAULT 0 CHECK(usage_rank BETWEEN 0 AND 2)");
        EnsureLedgerColumn("usage_semantics", "TEXT NOT NULL DEFAULT 'legacy-total-input-v0'");

        using var ledger = _connection.CreateCommand();
        ledger.CommandText =
            "DROP VIEW IF EXISTS v_calls;" +
            "DROP VIEW IF EXISTS v_call_usage;" +
            // v_calls remains a detail view: estimates and unknowns stay visible and never collapse.
            "CREATE VIEW v_calls AS SELECT ts, backend, component, tier, model, in_chars, out_chars, in_tok, out_tok, cache_r, cache_w, ms, outcome, usage_source, purpose, operation_id, attempt_id, attempt_no, uncached_in_tok, usage_rank, usage_semantics FROM calls;" +
            // The default aggregate is deliberately measured-only. Estimates remain queryable in v_calls.
            "CREATE VIEW v_call_usage AS SELECT backend, component, tier, model, purpose, COUNT(*) AS attempt_count, COUNT(DISTINCT operation_id) AS operation_count, SUM(uncached_in_tok) AS uncached_in_tok, SUM(cache_r) AS cache_r, SUM(cache_w) AS cache_w, SUM(out_tok) AS out_tok FROM calls WHERE usage_rank = 2 AND usage_semantics = 'split-v1' GROUP BY backend, component, tier, model, purpose;" +
            "CREATE UNIQUE INDEX IF NOT EXISTS ix_calls_attempt_id ON calls(attempt_id) WHERE attempt_id IS NOT NULL;" +
            $"PRAGMA user_version={SchemaVersion};";
        ledger.ExecuteNonQuery();
    }

    private void EnsureLedgerColumn(string name, string definition)
    {
        using var columns = _connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(calls)";
        using var reader = columns.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                return;
        }

        reader.Close();
        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE calls ADD COLUMN {name} {definition}";
        alter.ExecuteNonQuery();
    }
}
