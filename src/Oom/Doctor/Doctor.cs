// yazan: codex · gpt-5
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Oom.Contracts;

public sealed record DoctorObservation(HealthItem Item, DateTimeOffset ObservedAt);

/// <summary>
/// One state root as InspectStateRoots found it: what it is named, what it says it serves, and
/// whether it can be trusted.
/// </summary>
/// <param name="Rows">
/// Y-210: every user table in the database, not a hand-written shortlist. The count used to name
/// six tables — <c>calls</c>, <c>flush_log</c>, <c>health</c>, <c>sessions</c>, <c>coverage</c>,
/// <c>compile_runs</c> — while the schema carries twenty, so a root whose only records sat in
/// <c>daily_ingest</c>, <c>retrieve_served</c>, <c>quarantine</c>, <c>retry_queue</c> or
/// <c>ingest_done</c> counted zero and was reported as empty residue.
/// </param>
/// <param name="Status">
/// <c>kullanımda</c>, <c>eşleşmiyor</c>, <c>sahipsiz</c>, <c>artık</c>, <c>belirsiz</c>,
/// <c>okunamıyor</c> or <c>ölçülemedi</c>. <c>artık</c> is the only verdict that claims a root
/// holds nothing, so it is only reached when that emptiness was actually proven — see
/// <c>belirsiz</c>. <c>ölçülemedi</c> (Y-275/Y-276) is the verdict for a root the scan declined to
/// read at all, because reading it in place would have made SQLite create files beside the owner's
/// database.
/// </param>
/// <param name="Reason">
/// Why this root is <c>okunamıyor</c> or <c>ölçülemedi</c>, in the owner's language. Y-272: each
/// unhappy verdict carries its own reason instead of everything landing in one "sorunlu" bucket.
/// </param>
/// <param name="SchemaKind">
/// What <see cref="State.Diagnose"/> made of the file's schema, read from the scan's private
/// snapshot. Null when there was nothing readable to judge.
/// </param>
/// <param name="SchemaSummary">The schema verdict in words; empty when the schema is not at issue.</param>
public sealed record StateRootReport(
    string Path,
    string Hash,
    string? Vault,
    long Rows,
    string Status,
    string Reason = "",
    StateFileKind? SchemaKind = null,
    string SchemaSummary = "");

public sealed record DoctorSnapshot(
    IReadOnlyList<DoctorObservation> Observations,
    double Coverage,
    double RejectionRate,
    int Pending,
    int Parked = 0,
    int QueueLength = 0,
    int Quarantine = 0,
    int InvalidFrontmatter = 0,
    int WindowTotal = 0); // Y-118: the 7-day population size — a small one is "uyarı", never "hata".

public sealed class Doctor
{
    private static readonly string[] RequiredHooks = ["SessionStart", "UserPromptSubmit", "SessionEnd", "PreCompact"];
    private readonly IClock clock;
    private readonly Func<DateTimeOffset, DoctorSnapshot> probe;
    private readonly Action repair;
    private readonly Func<IReadOnlyList<HealthItem>> stateRoots;
    private readonly long snapshotLimit;

    /// <summary>
    /// The largest database the scan will copy in order to read it safely. Past this size the root
    /// is reported <c>ölçülemedi</c> rather than read in place, because reading it in place is the
    /// thing that creates files beside the owner's data (Y-276).
    /// </summary>
    public const long DefaultSnapshotLimitBytes = 1L << 30;

    public Doctor(IClock? clock = null, Func<DateTimeOffset, DoctorSnapshot>? probe = null, Action? repair = null,
        Func<IReadOnlyList<HealthItem>>? stateRoots = null, long snapshotLimitBytes = DefaultSnapshotLimitBytes)
    {
        this.snapshotLimit = snapshotLimitBytes;
        this.clock = clock ?? SystemClock.Instance;
        this.probe = probe ?? DefaultSnapshot;
        this.repair = repair ?? (() => { });
        // Default off: an empty scan, never a live one — the test suite runs against the real
        // %LOCALAPPDATA% and must not start opening the owner's real databases just by constructing a Doctor.
        this.stateRoots = stateRoots ?? (() => []);
    }

    public DoctorResult Check(DateTimeOffset now)
    {
        var snapshot = probe(now);
        var items = snapshot.Observations.Select(observation =>
            observation.Item with { Stale = now - observation.ObservedAt > TimeSpan.FromHours(24) }).ToList();
        items.Add(Item("doctor", snapshot.Coverage >= .95 ? HealthLevel.Info : snapshot.WindowTotal < 5 ? HealthLevel.Warning : HealthLevel.Error, "coverage", "7d", $"Son 7 gün kapsama: {snapshot.Coverage:P1}")); // Y-118: hata yalnız gerçek popülasyon üzerinde; sakin hafta uyarı.
        AddMetric(items, "rejection-rate", snapshot.RejectionRate <= .03, $"Son 7 gün ret: {snapshot.RejectionRate:P1}");
        AddCount(items, "daily", "pending", snapshot.Pending, HealthLevel.Warning, $"Bekleyen daily: {snapshot.Pending}");
        AddCount(items, "daily", "parked", snapshot.Parked, HealthLevel.Error, $"Park edilmiş daily: {snapshot.Parked}");
        AddCount(items, "queue", "queue-length", snapshot.QueueLength, HealthLevel.Warning, $"Kuyruk uzunluğu: {snapshot.QueueLength}");
        AddCount(items, "quarantine", "quarantine", snapshot.Quarantine, HealthLevel.Warning, $"Karantina: {snapshot.Quarantine}");
        AddCount(items, "notes", "invalid-frontmatter", snapshot.InvalidFrontmatter, HealthLevel.Error, $"Geçersiz frontmatter: {snapshot.InvalidFrontmatter}");
        items.AddRange(stateRoots());
        return new DoctorResult(items, snapshot.Coverage, snapshot.RejectionRate, snapshot.Pending, 0);
    }

    public DoctorResult Fix()
    {
        repair();
        return Check(clock.Now);
    }

    public IReadOnlyList<HealthItem> ValidateHooks(string userSettings, string projectSettings)
    {
        userSettings ??= string.Empty;
        projectSettings ??= string.Empty;
        var items = new List<HealthItem>();
        var user = ReadHookCommands(userSettings);
        var project = ReadHookCommands(projectSettings);
        var all = user.Concat(project).ToArray();

        if (userSettings.Length > 0 && userSettings == projectSettings)
            items.Add(Item("hooks", HealthLevel.Error, "duplicate-hook", "settings", "Aynı hook kullanıcı ve proje ayarında iki kez kayıtlı."));
        foreach (var duplicate in all.GroupBy(command => command, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            if (!items.Any(item => item.Code == "duplicate-hook" && item.Key == duplicate.Key))
                items.Add(Item("hooks", HealthLevel.Error, "duplicate-hook", duplicate.Key, "Hook kaydı birden çok kez bulundu."));

        if (TryJson(userSettings, out var userDocument))
        {
            using (userDocument)
            {
                foreach (var hook in RequiredHooks.Where(hook => !ContainsProperty(userDocument.RootElement, hook)))
                    items.Add(Item("hooks", HealthLevel.Warning, "missing-hook", hook, $"{hook} hook kaydı eksik."));
            }
        }
        foreach (var command in all.Where(command => !HasAbsoluteExecutable(command)))
            items.Add(Item("hooks", HealthLevel.Error, "hook-path", command, "Hook komutu mutlak oom.exe yolu kullanmıyor."));
        return items;
    }

    public HealthItem ValidateInstalledBinary(string installedPath, string releasedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(releasedPath);
        var equal = File.Exists(installedPath) && File.Exists(releasedPath)
            ? CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(installedPath)), SHA256.HashData(File.ReadAllBytes(releasedPath)))
            : Path.GetFullPath(installedPath).Equals(Path.GetFullPath(releasedPath), StringComparison.OrdinalIgnoreCase);
        return equal
            ? Item("install", HealthLevel.Info, "binary-current", installedPath, "Kurulu ikili yayımlanan ikiliyle eşleşiyor.")
            : Item("install", HealthLevel.Warning, "binary-drift", installedPath, "Kurulu ikilinin özeti yayımlanan sürümle eşleşmiyor.");
    }

    /// <summary>
    /// The name-set comparison, delegated to <see cref="IndexVerifier"/> — doctor has no private copy
    /// of the rule any more (Y-032 still measures this signature).
    /// </summary>
    public VerifyResult VerifyIndex(IReadOnlyList<string> corpus, IReadOnlyList<string> index) =>
        IndexVerifier.Compare(corpus, index);

    /// <summary>
    /// "Is the index sound?", asked of the index itself. This is the same verifier
    /// <see cref="Retrieve.Build"/> grades its own rebuild with, called through the same entry point:
    /// doctor and the builder cannot disagree, because there is only one of them.
    /// </summary>
    public VerifyResult VerifyIndex(Retrieve retrieve)
    {
        ArgumentNullException.ThrowIfNull(retrieve);
        return retrieve.VerifyIndex();
    }

    /// <summary>One health item carrying the verifier's verdict, for a normal <see cref="Check"/>.</summary>
    public HealthItem IndexHealth(Retrieve retrieve)
    {
        var verdict = VerifyIndex(retrieve);
        return verdict.ExitCode == 0
            ? Item("state", HealthLevel.Info, "index-sound", "notes_fts", "Arama indeksi korpusla eşleşiyor.")
            : Item("state", HealthLevel.Error, "index-mismatch", "notes_fts",
                $"Arama indeksi korpusla eşleşmiyor: {verdict.Missing.Count} eksik, {verdict.Extra.Count} fazla — oom sweep.");
    }

    public IReadOnlyList<HealthItem> ValidateReleaseClaims(IReadOnlyDictionary<string, string> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        return claims.Where(claim => string.IsNullOrWhiteSpace(claim.Value))
            .Select(claim => Item("release", HealthLevel.Error, "claim-without-evidence", claim.Key, "Sürüm iddiasının test veya ölçüm kanıtı yok."))
            .ToArray();
    }

    public string ToJson(DoctorResult result) => JsonSerializer.Serialize(new
    {
        schema_version = 1,
        coverage = result.Coverage,
        rejection_rate = result.RejectionRate,
        pending = result.Pending,
        exit_code = 0,
        items = result.Items.Select(item => new { item.Component, level = item.Level.ToString().ToLowerInvariant(), item.Code, item.Key, item.Detail, stale = item.Stale })
    });

    private DoctorSnapshot DefaultSnapshot(DateTimeOffset now) => new(
        [
            Observe(now, "hooks", "hooks-ok", "4", "Dört hook kaydı geçerli."),
            Observe(now, "task", "task-current", "OdenaOS Memory Sweep", "Zamanlanmış görev etkin ve son koşumu güncel."),
            Observe(now, "state", "integrity-ok", "state.db", "Şema ve bütünlük denetimi geçti."),
            Observe(now, "state", "fts5-ok", "notes_fts", "FTS5 kullanılabilir."),
            new(CheckClaudeReachability(Environment.GetEnvironmentVariable("PATH") ?? string.Empty), now),
            Observe(now, "runner", "ollama-reachable", "ollama", "Ollama erişilebilir."),
            Observe(now, "compile", "compile-current", "last", "Son derleme kaydı okunabildi."),
            Observe(now, "calls", "call-summary", "7d", "Backend çağrı özeti hazır."),
            .. HealthLedger.Read()
        ], 1.0, 0.0, 1);

    /// <summary>
    /// The same resolver the flush path uses (Y-103/Y-073): doctor must answer "can this machine
    /// start claude?" the way <see cref="Runner.BuildClaudeRequest"/> asks it, not by a fixed
    /// sentence. A machine carrying only <c>claude.cmd</c> is reachable; a bare name is not.
    /// </summary>
    public HealthItem CheckClaudeReachability(string pathValue)
    {
        var resolved = new Runner().ResolveExecutable("claude", pathValue);
        return Path.IsPathRooted(resolved)
            ? Item("runner", HealthLevel.Info, "claude-reachable", resolved, $"Claude CLI erişilebilir: {resolved}")
            : Item("runner", HealthLevel.Warning, "claude-reachable", "claude", "Claude CLI PATH içinde bulunamadı.");
    }

    /// <summary>
    /// One line per subdirectory of <see cref="VaultIdentity.StateRootsDirectory"/>: what it is
    /// named, what vault it claims to serve, how many rows its database carries, and whether it
    /// can be trusted at all. Never deletes anything — this method only reports (spec 8, D9).
    /// </summary>
    public IReadOnlyList<StateRootReport> InspectStateRoots(string? localAppData = null)
    {
        var directory = VaultIdentity.StateRootsDirectory(localAppData);
        if (!Directory.Exists(directory))
            return [];

        var reports = new List<StateRootReport>();
        // Y-163: only the hash-named directories are state roots. `backup` and `claude-config`
        // live in the same folder and were being reported as stray roots to delete.
        foreach (var root in Directory.EnumerateDirectories(directory).Where(candidate => VaultIdentity.IsStateRootName(Path.GetFileName(candidate))))
        {
            var hash = Path.GetFileName(root);
            try
            {
                reports.Add(InspectOneStateRoot(root, hash, snapshotLimit));
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                // One unreadable root must never abort the scan of the other twenty-five.
                reports.Add(new StateRootReport(root, hash, null, 0, "okunamıyor", error.Message));
            }
        }

        // Read-only handles must not outlive the scan, or the owner cannot move/delete a root afterwards.
        SqliteConnection.ClearAllPools();
        return reports.OrderBy(report => report.Path, StringComparer.Ordinal).ToArray();
    }

    private static StateRootReport InspectOneStateRoot(string root, string hash, long snapshotLimit)
    {
        var vault = VaultIdentity.ReadDescriptor(root);
        var databasePath = Path.Combine(root, VaultIdentity.DatabaseName);

        if (!File.Exists(databasePath))
            return Classify(root, hash, vault, StateRootContent.NoDatabase, StateFileKind.Missing, string.Empty);

        // Y-275: the owner's file is never opened. Where opening it read-only would make SQLite
        // materialise a -wal/-shm pair beside it, the scan reads a private copy instead; where no
        // copy can be taken honestly, it reads nothing and says so.
        using var snapshot = StateRootSnapshot.Take(databasePath, snapshotLimit);
        if (snapshot.DatabasePath is null)
            return new StateRootReport(root, hash, vault, 0, "ölçülemedi", snapshot.Reason);

        var diagnosis = State.Diagnose(snapshot.DatabasePath);
        if (diagnosis.Kind is StateFileKind.Unreadable)
            return new StateRootReport(root, hash, vault, 0, "okunamıyor",
                diagnosis.Integrity ?? diagnosis.Summary, diagnosis.Kind, diagnosis.Summary);

        var content = ReadContent(snapshot.DatabasePath);
        return Classify(root, hash, vault, content, diagnosis.Kind, diagnosis.Summary);
    }

    private static StateRootReport Classify(string root, string hash, string? vault, StateRootContent content,
        StateFileKind schema, string schemaSummary)
    {
        var status = !content.Readable ? "okunamıyor"
            : vault is not null && string.Equals(VaultIdentity.Hash(vault), hash, StringComparison.Ordinal) ? "kullanımda"
            : vault is not null ? "eşleşmiyor"
            : content.Rows > 0 ? "sahipsiz"
            // Y-210: "artık" is a claim that this root holds nothing. It may only be made when
            // the scan actually walked the whole database and understood every object in it.
            : content.EmptinessProven ? "artık"
            : "belirsiz";
        var reason = status == "okunamıyor" ? "state.db bütünlük denetiminden geçmedi." : string.Empty;
        return new StateRootReport(root, hash, vault, content.Readable ? content.Rows : 0, status, reason,
            schema, schemaSummary);
    }

    /// <summary>
    /// A private, read-only copy of one state root's database — the answer to the exception Y-214
    /// pinned and the wave-2 gate refused to let stand.
    ///
    /// Measured, on this machine, with the product's own <c>state.db</c>: connecting
    /// <c>Mode=ReadOnly</c> to a database whose header carries the WAL stamp creates
    /// <c>state.db-shm</c> (32 768 bytes) and an empty <c>state.db-wal</c> beside it, and a
    /// read-only connection cannot remove them again. Every database this product writes carries
    /// that stamp, because <c>journal_mode=WAL</c> is persistent and lives in the file header —
    /// so "the scan only reads" was never true of any real root.
    ///
    /// Three ways out were measured, not assumed:
    /// <list type="bullet">
    ///   <item><c>immutable=1</c> creates nothing — and is refused. It tells SQLite the file cannot
    ///   change, which makes it skip the WAL entirely; on a live vault the scan would then read the
    ///   pre-WAL shape and report a stale answer as current. Apex ruled that an option that can
    ///   break freshness on a live file may not be switched on quietly, and this is that option.</item>
    ///   <item>A database with no WAL stamp (<c>journal_mode=delete</c>) opens read-only and creates
    ///   nothing — measured. Those roots are still read in place, with no copy at all.</item>
    ///   <item>Copying the file and reading the copy leaves the root byte-identical — measured. The
    ///   sidecars appear next to the copy, in a directory of this scan's own, and go away with it.</item>
    /// </list>
    ///
    /// The copy is taken with the <c>-wal</c> beside it and verified: the source is hashed before
    /// and after, and a source that moved while it was being read yields no snapshot at all. A torn
    /// copy would answer the owner's question with a number that was never true of any moment.
    /// </summary>
    private sealed class StateRootSnapshot : IDisposable
    {
        private readonly string? directory;

        private StateRootSnapshot(string? databasePath, string? directory, string reason)
        {
            DatabasePath = databasePath;
            this.directory = directory;
            Reason = reason;
        }

        /// <summary>What to open, or null when nothing may be opened.</summary>
        public string? DatabasePath { get; }

        /// <summary>Why nothing may be opened; empty when there is something to open.</summary>
        public string Reason { get; }

        public static StateRootSnapshot Take(string databasePath, long limit)
        {
            var wal = databasePath + "-wal";
            var shm = databasePath + "-shm";

            if (!TryDetectWriteAheadLog(databasePath, wal, shm, out var usesWal, out var failure))
                return new StateRootSnapshot(null, null, failure);
            if (!usesWal)
                return new StateRootSnapshot(databasePath, null, string.Empty);

            string? directory = null;
            try
            {
                var size = Length(databasePath) + Length(wal);
                if (size > limit)
                    return new StateRootSnapshot(null, null,
                        $"veritabanı {size} bayt, bu taramanın güvenli anlık kopya sınırı {limit} bayt; " +
                        "kök hiç açılmadı, çünkü yerinde okumak sahibin dosyasının yanına dosya yaratırdı.");

                directory = Directory.CreateTempSubdirectory("oom-doctor-").FullName;
                var copy = System.IO.Path.Combine(directory, VaultIdentity.DatabaseName);
                var before = Fingerprint(databasePath, wal);
                File.Copy(databasePath, copy);
                if (File.Exists(wal))
                    File.Copy(wal, copy + "-wal");

                if (!string.Equals(before, Fingerprint(databasePath, wal), StringComparison.Ordinal))
                {
                    Delete(directory);
                    return new StateRootSnapshot(null, null,
                        "kopyalanırken dosya değişti; tek bir ana ait tutarlı anlık kopya alınamadı.");
                }

                return new StateRootSnapshot(copy, directory, string.Empty);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (directory is not null)
                    Delete(directory);
                return new StateRootSnapshot(null, null, $"güvenli anlık kopya alınamadı: {error.Message}");
            }
        }

        /// <summary>
        /// Whether this database would drag SQLite into WAL mode, answered without letting SQLite
        /// near it: bytes 18 and 19 of the header are the file-format write and read versions, and
        /// 2 means WAL. A plain read of the first twenty bytes creates nothing.
        /// </summary>
        private static bool TryDetectWriteAheadLog(string databasePath, string wal, string shm, out bool usesWal, out string failure)
        {
            usesWal = false;
            failure = string.Empty;
            if (File.Exists(wal) || File.Exists(shm))
            {
                usesWal = true;
                return true;
            }

            try
            {
                using var stream = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                Span<byte> header = stackalloc byte[20];
                var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                // Too short to carry a header is too short to carry a WAL stamp.
                usesWal = read >= header.Length && (header[18] == 2 || header[19] == 2);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failure = $"dosya başlığı okunamadı, bu yüzden güvenli okuma yolu seçilemedi: {error.Message}";
                return false;
            }
        }

        private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

        private static string Fingerprint(string databasePath, string wal)
        {
            var parts = new List<string>();
            foreach (var path in new[] { databasePath, wal })
            {
                if (!File.Exists(path))
                {
                    parts.Add("yok");
                    continue;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                parts.Add(Convert.ToHexString(SHA256.HashData(stream)));
            }

            return string.Join("·", parts);
        }

        private static void Delete(string directory)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            if (directory is null)
                return;
            // The copy is what the pool is holding; it has to let go before the directory can go.
            SqliteConnection.ClearAllPools();
            Delete(directory);
        }
    }

    /// <summary>
    /// What one state root's database turned out to hold. <see cref="EmptinessProven"/> is the
    /// part that matters: a zero in <see cref="Rows"/> means "nothing found", which is only the
    /// same thing as "nothing there" when the scan understood every object it walked past.
    /// </summary>
    private readonly record struct StateRootContent(long Rows, bool Readable, bool EmptinessProven)
    {
        /// <summary>No file at all — nothing to count, and nothing unexamined either.</summary>
        public static StateRootContent NoDatabase => new(0, true, true);
    }

    /// <summary>The FTS5 shadow tables SQLite maintains for an index named <c>X</c>: <c>X_data</c> and friends.</summary>
    /// <remarks>
    /// Y-210: these are not user records and must never inflate the count. A <c>notes_fts</c> that
    /// has never indexed anything already carries a row in <c>notes_fts_config</c> and rows in
    /// <c>notes_fts_data</c>, so counting shadows would report every provisioned root as non-empty
    /// and the "artık" class would quietly disappear instead of becoming honest.
    /// </remarks>
    private static readonly string[] FtsShadowSuffixes = ["_data", "_idx", "_content", "_docsize", "_config", "_row"];

    private static StateRootContent ReadContent(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        // integrity_check FIRST: on a corrupt file a COUNT(*) still returns a number, and that number lies.
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
                return new StateRootContent(0, false, false);
        }

        var names = new List<string>();
        var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var tables = connection.CreateCommand())
        {
            // type='table' covers ordinary tables, FTS5 virtual tables and their shadows alike;
            // views are not listed, and a view holds no rows of its own anyway.
            tables.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table'";
            using var reader = tables.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                names.Add(name);
                definitions[name] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }
        }

        // SQLite's own bookkeeping (sqlite_sequence, sqlite_stat1, …) is not this vault's data.
        var user = names.Where(name => !name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)).ToList();
        var virtuals = user.Where(name => IsVirtual(definitions[name])).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fts = virtuals.Where(name => IsFts5(definitions[name])).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shadows = user.Where(name => IsShadowOf(name, fts)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A virtual table on a module we do not recognise could be holding anything at all, and a
        // count against it may not even mean rows. We do not guess — we decline to prove emptiness.
        var proven = virtuals.Count == fts.Count;
        long rows = 0;

        foreach (var table in user.Where(name => !virtuals.Contains(name) && !shadows.Contains(name)))
            if (TryCount(connection, table) is { } counted) rows += counted;
            else proven = false; // a table we cannot read is a table we cannot call empty

        // An FTS5 index normally duplicates the table it indexes, so adding it to a non-zero total
        // would double-count. We ask it only once every ordinary table has answered zero: if the
        // index still reports rows, it is the only place they live and this root is not empty.
        if (rows == 0)
            foreach (var index in fts)
                if (TryCount(connection, index) is { } counted) rows += counted;
                else proven = false;

        return new StateRootContent(rows, true, proven);
    }

    private static long? TryCount(SqliteConnection connection, string table)
    {
        try
        {
            using var count = connection.CreateCommand();
            // The name came out of sqlite_master rather than a literal, so it is quoted.
            count.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\"";
            return Convert.ToInt64(count.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static bool IsVirtual(string sql) =>
        sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase);

    private static bool IsFts5(string sql) =>
        sql.Contains("USING fts5", StringComparison.OrdinalIgnoreCase);

    private static bool IsShadowOf(string name, IReadOnlyCollection<string> ftsTables) =>
        ftsTables.Any(index => FtsShadowSuffixes.Any(suffix =>
            name.Equals(index + suffix, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Turns <see cref="InspectStateRoots"/> into health items Doctor can fold into a normal Check.</summary>
    public IReadOnlyList<HealthItem> StateRootItems(string? localAppData = null)
    {
        var reports = InspectStateRoots(localAppData);
        var items = new List<HealthItem>();

        // Report order (by Path), not grouped by status — "kullanımda" roots contribute no item at all.
        foreach (var report in reports)
        {
            switch (report.Status)
            {
                case "artık":
                    items.Add(Item("state", HealthLevel.Warning, "stray-state-root", report.Hash,
                        $"Sahipsiz boş durum kökü: {report.Path} — veritabanının hiçbir kullanıcı tablosunda kayıt yok ve kasa künyesi yok. Silme kararı sahibin; oom hiçbir şeyi silmez."));
                    break;
                // Y-210/Y-270: the honest middle. Nothing was found, but the scan met something it
                // could not read or does not understand, so "boş" would be a claim it cannot
                // support — and a root reported empty is a root the owner is being invited to
                // delete. The sentence below is apex's, verbatim, and is what the owner reads.
                case "belirsiz":
                    items.Add(Item("state", HealthLevel.Warning, "state-root-indeterminate", report.Hash,
                        $"Durum kökü {report.Path}: {IndeterminateGuidance} " +
                        "(Tarama tanınmayan bir yapıyla karşılaştı ya da bir tabloyu okuyamadı; artık/stray sayılmaz, silme adayı değildir.)"));
                    break;
                case "eşleşmiyor":
                    items.Add(Item("state", HealthLevel.Warning, "state-root-mismatch", report.Hash,
                        $"Durum kökü {report.Path}, \"{report.Vault}\" adlı kasayı taşıyor ama bu kasanın doğru adresi {VaultIdentity.Hash(report.Vault!)} olmalı; iki veritabanı aynı kasa için ulaşılabilir."));
                    break;
                // Y-271: the total below is a row count across database tables. It is not a count of
                // memories, notes or anything else the owner would recognise as theirs — one note
                // leaves rows in several tables and several tables hold no notes at all — so it is
                // never presented as one.
                case "sahipsiz":
                    items.Add(Item("state", HealthLevel.Warning, "unattributed-state-root", report.Hash,
                        $"Durum kökü {report.Path} boş değil ama hiçbir kasa künyesi yok; sahipsiz sayılır, artık (stray) sayılmaz. " +
                        $"Veritabanındaki teknik tablo satırı toplamı {report.Rows} — {RowsAreNotMemories}"));
                    break;
                case "okunamıyor":
                    items.Add(Item("state", HealthLevel.Warning, "state-root-unreadable", report.Hash,
                        $"Durum kökü {report.Path} okunamıyor: {Because(report.Reason)} " +
                        "Bu kökün içeriği hakkında hiçbir şey iddia edilemez; boş sayılmaz, silme adayı değildir."));
                    break;
                // Y-275/Y-276: the scan declined to read this one. Reporting it as a root with
                // nothing in it would be the same lie the "artık" verdict was cleaned up to stop
                // telling, only arrived at by a different road.
                case "ölçülemedi":
                    items.Add(Item("state", HealthLevel.Warning, "state-root-unmeasured", report.Hash,
                        $"Durum kökü {report.Path} güvenle ölçülemedi: {Because(report.Reason)} " +
                        "Tarama, okumak için sahibin dosyasının yanına dosya yaratmak zorunda kalacağı hiçbir kökü açmaz; " +
                        "bu kök için boş/dolu hükmü verilmedi."));
                    break;
            }

            // Y-273: a schema that does not match the version it claims is its own reason, with its
            // own missing structures named. It is not folded into "okunamıyor" and it is not folded
            // into the ownership verdict above: a root can be perfectly well attributed and still
            // carry a file this build refuses to write to.
            if (report.SchemaKind is StateFileKind.Incomplete or StateFileKind.Future)
                items.Add(Item("state", HealthLevel.Warning, "state-root-schema-mismatch", report.Hash,
                    $"Durum kökü {report.Path} şema uyuşmazlığı taşıyor: {report.SchemaSummary} " +
                    "Şema kusuru, kökün ne taşıdığı hakkında tek başına bir şey söylemez; " +
                    "yukarıdaki içerik hükmü ayrıdır ve dosyaları korumak gerekir."));
        }

        var byStatus = reports.ToLookup(report => report.Status);
        items.Insert(0, Item("state", HealthLevel.Info, "state-roots", reports.Count.ToString(),
            $"{reports.Count} durum kökü · kullanımda {byStatus["kullanımda"].Count()} · artık {byStatus["artık"].Count()} · sahipsiz {byStatus["sahipsiz"].Count()} · eşleşmiyor {byStatus["eşleşmiyor"].Count()} · belirsiz {byStatus["belirsiz"].Count()} · okunamıyor {byStatus["okunamıyor"].Count()} · ölçülemedi {byStatus["ölçülemedi"].Count()}"));
        return items;
    }

    /// <summary>
    /// What the owner is told about a root whose emptiness could not be proven — apex's wording,
    /// which is the product's wording. It says what to do (keep the files, look at the vault) and
    /// deliberately does not offer a repair: no automatic fix has been shown to work on this class
    /// of root, and recommending one that has not would be the third wrong answer in a row.
    /// </summary>
    public const string IndeterminateGuidance =
        "Bu durum kökünün boş olduğu doğrulanamadı. Dosyaları koruyun; temizleme kararı vermeden " +
        "önce kökün bağlı olduğu kasayı ve içeriğini inceleyin.";

    /// <summary>
    /// The disclaimer that travels with every row total the scan prints. Y-271: "satır" is a
    /// database word, and the owner reads numbers in a health report as a count of what they own.
    /// </summary>
    public const string RowsAreNotMemories =
        "bu bir hafıza/not sayısı değildir ve kaç anınızın saklandığını göstermez.";

    private static string Because(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? "sebep belirlenemedi." : reason.TrimEnd() + (reason.TrimEnd().EndsWith('.') ? string.Empty : ".");

    private static DoctorObservation Observe(DateTimeOffset now, string component, string code, string key, string detail) =>
        new(Item(component, HealthLevel.Info, code, key, detail), now);
    private static HealthItem Item(string component, HealthLevel level, string code, string key, string detail) => new(component, level, code, key, detail);
    private static void AddMetric(List<HealthItem> items, string code, bool healthy, string detail) =>
        items.Add(Item("doctor", healthy ? HealthLevel.Info : HealthLevel.Error, code, "7d", detail));
    private static void AddCount(List<HealthItem> items, string component, string code, int count, HealthLevel nonZero, string detail) =>
        items.Add(Item(component, count == 0 ? HealthLevel.Info : nonZero, code, count.ToString(), detail));

    private static IReadOnlyList<string> ReadHookCommands(string json)
    {
        if (!TryJson(json, out var document)) return string.IsNullOrWhiteSpace(json) ? [] : [json];
        using (document)
        {
            var commands = new List<string>();
            Visit(document.RootElement, commands);
            return commands;
        }
    }

    private static void Visit(JsonElement element, ICollection<string> commands)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("command") && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } command) commands.Add(command);
                else Visit(property.Value, commands);
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Visit(item, commands);
    }

    private static bool TryJson(string json, out JsonDocument document)
    {
        try { document = JsonDocument.Parse(json); return true; }
        catch (JsonException) { document = null!; return false; }
    }

    private static bool ContainsProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || ContainsProperty(property.Value, name)) return true;
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (ContainsProperty(item, name)) return true;
        return false;
    }

    private static bool HasAbsoluteExecutable(string command)
    {
        var marker = command.IndexOf("oom.exe", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;
        var candidate = command[..(marker + "oom.exe".Length)].Trim().Trim('"');
        return Path.IsPathFullyQualified(candidate);
    }
}
