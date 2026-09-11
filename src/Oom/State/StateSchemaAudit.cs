using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Oom.Contracts;

/// <summary>What a state database file is, judged before anything is written to it.</summary>
public enum StateFileKind
{
    /// <summary>No file at that path.</summary>
    Missing,

    /// <summary>A file that holds nothing of the owner's: no version, no tables. It gets provisioned.</summary>
    Empty,

    /// <summary>Content but no version stamp. It claims nothing, so the whole ladder legitimately applies.</summary>
    Unversioned,

    /// <summary>A valid older version: everything its own version promised is present, and it can be upgraded.</summary>
    Historic,

    /// <summary>Already this executable's version, and complete.</summary>
    Current,

    /// <summary>Stamped by a newer build. Not opened, not downgraded.</summary>
    Future,

    /// <summary>The version it claims and the shape it has disagree. Refused on a writing open.</summary>
    Incomplete,

    /// <summary>SQLite cannot read it, or it fails its own integrity check.</summary>
    Unreadable
}

/// <summary>One structure the file's own version promised and the file does not have.</summary>
/// <param name="Kind">"tablo", "görünüm", "indeks" or "sütun".</param>
/// <param name="Name">The object's name; for a column, <c>table.column</c>.</param>
public sealed record StateSchemaFinding(string Kind, string Name)
{
    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>
/// A read-only verdict on a state database, obtainable when a normal writing <see cref="State"/>
/// open would throw. This is the surface <c>doctor</c> can diagnose through: it creates no state
/// root, no database and no schema, and issues no DDL.
/// </summary>
public sealed record StateSchemaDiagnosis(
    string? Path,
    StateFileKind Kind,
    int? FoundVersion,
    int ExpectedVersion,
    IReadOnlyList<StateSchemaFinding> Missing,
    string? Integrity,
    string Summary)
{
    /// <summary>Whether a writing open of this file would be allowed to proceed.</summary>
    public bool WritableOpenAllowed => Kind
        is StateFileKind.Missing
        or StateFileKind.Empty
        or StateFileKind.Unversioned
        or StateFileKind.Historic
        or StateFileKind.Current;
}

/// <summary>
/// What each schema version actually promised, and the check that a file's claimed version and its
/// real shape agree.
///
/// Why this exists. The ladder replays a step only when the file's version is below it — that is
/// version 5's whole point, and it is right. The price is an assumption: that a file stamped 3
/// really did get everything version 3 shipped with. Nothing verified that. A file claiming 3 with
/// no <c>sweep_stamps</c> migrated to 5, reported success, and the next <c>sweep</c> died with
/// <c>SQLite Error 1: 'no such table: sweep_stamps'</c> — measured.
///
/// So the claim is checked instead of trusted, and the shapes below are <b>measured from this
/// repository's own history</b>, not guessed from today's <c>BaseTables</c>. That distinction is
/// load-bearing, because today's <c>BaseTables</c> creates a fresh file with the *modern* 21-column
/// <c>calls</c> and 11-column <c>retrieve_served</c>, while the builds that actually stamped 2 and
/// 3 wrote a 15-column <c>calls</c> and a 4-column <c>retrieve_served</c>. Reading version 1's
/// shape off today's step 1 would have declared every genuine shipped database broken.
///
/// The sources, by git revision:
/// <list type="bullet">
///   <item>version 1 — <c>100e6ce</c>: 14 tables, no views, no named indexes, 15-column <c>calls</c>.</item>
///   <item>version 2 — tag <c>v2.0.0</c> / <c>v2.0.1</c> (shipped): adds five views. <c>calls</c> unchanged.</item>
///   <item>version 3 — tag <c>v2.1.0</c> (shipped): adds <c>ingest_done</c> and <c>vault_meta</c>. <c>calls</c> still 15 columns.</item>
///   <item>version 4 — <c>ea3879e</c> (never shipped): adds the six call-accounting columns, <c>v_call_usage</c> and <c>ix_calls_attempt_id</c>.</item>
///   <item>version 5 — here: adds the retrieval index and <c>retrieve_served</c>'s scope columns.</item>
/// </list>
///
/// What this buys, beyond catching a damaged file: the ladder's step numbers do not line up with
/// the versions that shipped. Steps 2, 3 and 4 split the six call-accounting columns across three
/// numbers, but every one of them arrived together at stamp 4. A file stamped 3 — that is tag
/// <c>v2.1.0</c>, a real release — therefore runs only steps 4 and 5 today and lands on "version 5"
/// with <c>calls</c> still missing <c>operation_id</c>, <c>attempt_id</c>, <c>attempt_no</c> and
/// <c>uncached_in_tok</c>, which is every column <see cref="State.RecordCall"/> inserts by name.
/// Gating the steps on the shape a version really had, rather than on the step's own number, is
/// what makes a shipped database upgrade correctly instead of quietly becoming unwritable.
///
/// Nothing in the audit writes. Checking for a missing table does not create it: reporting a hole
/// by digging it is not a diagnosis.
/// </summary>
internal static class StateShape
{
    /// <summary>The v1 tables, with the columns they have carried unchanged since <c>100e6ce</c>.</summary>
    private static readonly (string Table, string[] Columns)[] BaseShape =
    [
        ("sessions", ["session_id", "transcript_path", "last_turn_index", "last_flush_ts"]),
        ("flush_log", ["ts", "session_id", "reason", "outcome", "turns", "chars", "backend"]),
        ("retry_queue", ["session_id", "attempts", "next_at", "last_error"]),
        ("sweep_stamps", ["path", "mtime", "size", "outcome"]),
        ("coverage", ["ts", "total", "covered", "uncovered_json"]),
        ("daily_ingest", ["name", "digest", "status", "attempts", "reasons", "ts"]),
        ("compile_runs", ["ts", "daily", "status", "created", "updated", "ms"]),
        ("quarantine", ["digest", "source", "reason", "ts", "path"]),
        ("health", ["ts", "component", "level", "code", "key", "detail"]),
        ("notified", ["class", "key", "ts"]),
        ("locks", ["name", "machine", "pid", "ts"]),
        ("kota", ["ts", "window", "used_pct", "resets_at"]),
        ("calls",
        [
            "ts", "backend", "component", "tier", "model", "in_chars", "out_chars", "in_tok", "out_tok",
            "cache_r", "cache_w", "ms", "outcome", "usage_source", "purpose"
        ]),
        ("retrieve_served", ["session_id", "query_sig", "note", "ts"])
    ];

    /// <summary>
    /// What each version introduced, measured from the revisions named in the class summary. A
    /// version's full requirement is the union of every introduction up to and including it.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, StepShape> Introduced = new Dictionary<int, StepShape>
    {
        [1] = new(
            BaseShape.Select(entry => entry.Table).ToArray(),
            [],
            [],
            BaseShape.SelectMany(entry => entry.Columns.Select(column => (entry.Table, column))).ToArray()),

        [2] = new([], ["v_calls", "v_coverage", "v_flush_log", "v_health", "v_kota"], [], []),

        [3] = new(["ingest_done", "vault_meta"], [], [],
            [
                ("ingest_done", "source"), ("ingest_done", "digest"), ("ingest_done", "ts"),
                ("vault_meta", "key"), ("vault_meta", "value")
            ]),

        [4] = new([], ["v_call_usage"], ["ix_calls_attempt_id"],
            [
                ("calls", "operation_id"), ("calls", "attempt_id"), ("calls", "attempt_no"),
                ("calls", "uncached_in_tok"), ("calls", "usage_rank"), ("calls", "usage_semantics")
            ]),

        [5] = new(
            ["notes", "notes_fts", "oom_index_meta"],
            [],
            ["ix_notes_updated", "ix_retrieve_served_scope"],
            [
                ("notes", "name"), ("notes", "title"), ("notes", "aliases"), ("notes", "tags"), ("notes", "body"), ("notes", "updated"),
                ("notes_fts", "name"), ("notes_fts", "title"), ("notes_fts", "aliases"), ("notes_fts", "tags"), ("notes_fts", "body"),
                ("oom_index_meta", "generation"), ("oom_index_meta", "manifest_digest"), ("oom_index_meta", "built_at"),
                ("retrieve_served", "vault"), ("retrieve_served", "client"), ("retrieve_served", "entry"),
                ("retrieve_served", "content_hash"), ("retrieve_served", "status"),
                ("retrieve_served", "pid"), ("retrieve_served", "acked_ts")
            ])
    };

    /// <summary>
    /// What each <i>ladder step</i> brings into existence — which is not the same thing as what a
    /// version introduced, because the steps split the version 4 columns across numbers 2, 3 and 4.
    /// Step 1 is declared at its version 1 shape on purpose: today's <c>BaseTables</c> writes the
    /// modern <c>calls</c> and <c>retrieve_served</c> for a fresh file, but a
    /// <c>CREATE TABLE IF NOT EXISTS</c> adds no column to a table that already exists, so step 1
    /// cannot be credited with columns it would only create from nothing.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, StepShape> StepProvides = new Dictionary<int, StepShape>
    {
        [1] = new(
            BaseShape.Select(entry => entry.Table).Concat(["ingest_done", "vault_meta"]).ToArray(),
            ["v_flush_log", "v_coverage", "v_health", "v_kota"],
            [],
            BaseShape.SelectMany(entry => entry.Columns.Select(column => (entry.Table, column)))
                .Concat([("ingest_done", "source"), ("ingest_done", "digest"), ("ingest_done", "ts"),
                         ("vault_meta", "key"), ("vault_meta", "value")]).ToArray()),

        [2] = new([], [], ["ix_calls_attempt_id"],
            [("calls", "operation_id"), ("calls", "attempt_id"), ("calls", "attempt_no")]),

        [3] = new([], [], [], [("calls", "uncached_in_tok")]),

        [4] = new([], [], [], [("calls", "usage_rank"), ("calls", "usage_semantics")]),

        // Step 5 owns what it needs: it creates retrieve_served's version 1 shape itself when that
        // table is absent, then adds the scope columns (Y-200).
        [5] = new(
            ["notes", "notes_fts", "oom_index_meta", "retrieve_served"],
            [],
            ["ix_notes_updated", "ix_retrieve_served_scope"],
            Introduced[5].Columns.Concat(
                [("retrieve_served", "session_id"), ("retrieve_served", "query_sig"),
                 ("retrieve_served", "note"), ("retrieve_served", "ts")]).ToArray())
    };

    /// <summary>The two views the migration's own <c>Views()</c> call recreates on any upgrade.</summary>
    private static readonly string[] MigrationViews = ["v_calls", "v_call_usage"];

    /// <summary>
    /// The columns the ladder adds with <c>ALTER TABLE</c>, by the step that adds them. The
    /// distinction from <see cref="StepProvides"/> is not pedantry: every other column in the
    /// ladder arrives inside a <c>CREATE TABLE IF NOT EXISTS</c>, which adds nothing at all to a
    /// table that already exists. A file carrying a <c>calls</c> table of its own invention is
    /// therefore beyond the ladder's reach, and saying so before the migration starts is the
    /// difference between a refusal that costs nothing and a rollback that already took a backup.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, (string Table, string Column)[]> AlterAdded =
        new Dictionary<int, (string, string)[]>
        {
            [2] = [("calls", "operation_id"), ("calls", "attempt_id"), ("calls", "attempt_no")],
            [3] = [("calls", "uncached_in_tok")],
            [4] = [("calls", "usage_rank"), ("calls", "usage_semantics")],
            [5] =
            [
                ("retrieve_served", "vault"), ("retrieve_served", "client"), ("retrieve_served", "entry"),
                ("retrieve_served", "content_hash"), ("retrieve_served", "status"),
                ("retrieve_served", "pid"), ("retrieve_served", "acked_ts")
            ]
        };

    internal sealed record StepShape(
        IReadOnlyList<string> Tables,
        IReadOnlyList<string> Views,
        IReadOnlyList<string> Indexes,
        IReadOnlyList<(string Table, string Column)> Columns);

    /// <summary>Everything a file stamped <paramref name="version"/> is required to carry.</summary>
    internal static StepShape Required(int version)
    {
        var tables = new List<string>();
        var views = new List<string>();
        var indexes = new List<string>();
        var columns = new List<(string, string)>();

        for (var step = 1; step <= version; step++)
        {
            if (!Introduced.TryGetValue(step, out var introduced))
                continue;
            tables.AddRange(introduced.Tables);
            views.AddRange(introduced.Views);
            indexes.AddRange(introduced.Indexes);
            columns.AddRange(introduced.Columns);
        }

        return new StepShape(tables, views, indexes, columns);
    }

    /// <summary>
    /// Whether ladder step <paramref name="step"/> has anything to contribute to a file stamped
    /// <paramref name="found"/>. This replaces gating on the step's number alone, which is what
    /// left a shipped version 3 file without the call-accounting columns: every one of them
    /// arrived at stamp 4, but the ladder hands them out at steps 2, 3 and 4, all of which a
    /// file stamped 3 skipped.
    /// </summary>
    internal static bool StepApplies(int step, int found)
    {
        if (!StepProvides.TryGetValue(step, out var provides))
            return false;

        var required = Required(found);
        var tables = new HashSet<string>(required.Tables, StringComparer.OrdinalIgnoreCase);
        var views = new HashSet<string>(required.Views, StringComparer.OrdinalIgnoreCase);
        var indexes = new HashSet<string>(required.Indexes, StringComparer.OrdinalIgnoreCase);
        var columns = new HashSet<(string, string)>(required.Columns, ColumnComparer.Instance);

        return provides.Tables.Any(name => !tables.Contains(name))
            || provides.Views.Any(name => !views.Contains(name))
            || provides.Indexes.Any(name => !indexes.Contains(name))
            || provides.Columns.Any(column => !columns.Contains(column));
    }

    /// <summary>
    /// What a file stamped <paramref name="found"/> owes and does not have. Empty means the file is
    /// a valid member of the version it claims. Reads <c>sqlite_master</c> and
    /// <c>pragma_table_info</c>; issues nothing else.
    /// </summary>
    internal static IReadOnlyList<StateSchemaFinding> Audit(SqliteConnection connection, int found)
    {
        var required = Required(found);
        var (actualTables, actualViews, actualIndexes) = ReadMaster(connection);
        var missing = new List<StateSchemaFinding>();

        var absentTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in required.Tables.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            if (actualTables.Contains(table))
                continue;
            absentTables.Add(table);
            missing.Add(new StateSchemaFinding("tablo", table));
        }

        // A missing table is one finding, not one per column it would have had.
        var byTable = required.Columns
            .Where(column => !absentTables.Contains(column.Table))
            .GroupBy(column => column.Table, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byTable.OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var actualColumns = ReadColumns(connection, group.Key);
            foreach (var (_, column) in group)
            {
                if (!actualColumns.Contains(column))
                    missing.Add(new StateSchemaFinding("sütun", $"{group.Key}.{column}"));
            }
        }

        foreach (var view in required.Views.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            if (!actualViews.Contains(view))
                missing.Add(new StateSchemaFinding("görünüm", view));
        }

        foreach (var index in required.Indexes.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            if (!actualIndexes.Contains(index))
                missing.Add(new StateSchemaFinding("indeks", index));
        }

        return missing;
    }

    /// <summary>
    /// What is still absent after the ladder has run, judged against the version this executable
    /// stamps. The closing guarantee: a migration that ran to the end and still did not produce a
    /// whole file must not stamp one.
    /// </summary>
    internal static IReadOnlyList<StateSchemaFinding> AuditMigrated(SqliteConnection connection) =>
        Audit(connection, StateStore.SchemaVersion);

    /// <summary>
    /// What the ladder will still not have supplied when it finishes with a file stamped
    /// <paramref name="found"/> — computed before a single statement runs, so a file the migration
    /// cannot complete is refused without being copied, WAL-stamped or half-written.
    /// </summary>
    internal static IReadOnlyList<StateSchemaFinding> AuditUpgradePath(SqliteConnection connection, int found)
    {
        if (found >= StateStore.SchemaVersion)
            return [];

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var views = new HashSet<string>(MigrationViews, StringComparer.OrdinalIgnoreCase);
        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var altered = new HashSet<(string, string)>(ColumnComparer.Instance);
        var createdWith = new HashSet<(string, string)>(ColumnComparer.Instance);

        for (var step = 1; step <= StateStore.SchemaVersion; step++)
        {
            if (!StepApplies(step, found) || !StepProvides.TryGetValue(step, out var provides))
                continue;

            foreach (var table in provides.Tables) tables.Add(table);
            foreach (var view in provides.Views) views.Add(view);
            foreach (var index in provides.Indexes) indexes.Add(index);

            var alterHere = AlterAdded.TryGetValue(step, out var list) ? list : [];
            foreach (var column in alterHere) altered.Add(column);
            foreach (var column in provides.Columns.Where(column => !altered.Contains(column)))
                createdWith.Add(column);
        }

        var (actualTables, actualViews, actualIndexes) = ReadMaster(connection);
        var required = Required(StateStore.SchemaVersion);
        var unreachable = new List<StateSchemaFinding>();

        foreach (var table in required.Tables.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            if (!actualTables.Contains(table) && !tables.Contains(table))
                unreachable.Add(new StateSchemaFinding("tablo", table));
        }

        foreach (var group in required.Columns
                     .GroupBy(column => column.Table, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var exists = actualTables.Contains(group.Key);
            var actualColumns = exists ? ReadColumns(connection, group.Key) : [];

            foreach (var (table, column) in group)
            {
                if (exists && actualColumns.Contains(column))
                    continue;
                if (altered.Contains((table, column)))
                    continue;
                // A CREATE TABLE IF NOT EXISTS only delivers its columns when it really creates
                // the table. On a table that is already there it is a no-op.
                if (!exists && tables.Contains(table) && createdWith.Contains((table, column)))
                    continue;
                unreachable.Add(new StateSchemaFinding("sütun", $"{table}.{column}"));
            }
        }

        foreach (var view in required.Views.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            if (!actualViews.Contains(view) && !views.Contains(view))
                unreachable.Add(new StateSchemaFinding("görünüm", view));
        }

        foreach (var index in required.Indexes.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            if (!actualIndexes.Contains(index) && !indexes.Contains(index))
                unreachable.Add(new StateSchemaFinding("indeks", index));
        }

        return unreachable;
    }

    /// <summary>Whether the file holds anything at all of the owner's, SQLite's own bookkeeping aside.</summary>
    internal static bool HasOwnerContent(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' LIMIT 1";
        return command.ExecuteScalar() is not null;
    }

    internal static string Describe(IReadOnlyList<StateSchemaFinding> missing) =>
        string.Join(", ", missing.Select(item => item.ToString()));

    /// <summary>The views a migration recreates on its way through, whatever version it started at.</summary>
    internal static IReadOnlyList<string> ViewsRecreatedByMigration => MigrationViews;

    private static (HashSet<string> Tables, HashSet<string> Views, HashSet<string> Indexes) ReadMaster(SqliteConnection connection)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var views = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name FROM sqlite_master";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            switch (reader.GetString(0))
            {
                case "table": tables.Add(name); break;
                case "view": views.Add(name); break;
                case "index": indexes.Add(name); break;
            }
        }

        return (tables, views, indexes);
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        // A pragma function, so the table name is a bound value rather than concatenated text.
        command.CommandText = "SELECT name FROM pragma_table_info($table)";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(0));
        return columns;
    }

    private sealed class ColumnComparer : IEqualityComparer<(string Table, string Column)>
    {
        internal static readonly ColumnComparer Instance = new();

        public bool Equals((string Table, string Column) x, (string Table, string Column) y) =>
            string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Column, y.Column, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Table, string Column) obj) =>
            HashCode.Combine(obj.Table.ToLowerInvariant(), obj.Column.ToLowerInvariant());
    }
}

/// <summary>
/// The read-only diagnosis path. It exists because the only honest answer about a database that
/// will not open is still an answer, and <c>doctor</c> has to be able to give it without first
/// succeeding at the very open that is failing. Nothing here creates a state root, a database or a
/// schema, and nothing here throws.
/// </summary>
/// <remarks>
/// One documented exception, the same one Y-214 pins for doctor's root scan: connecting read-only
/// to a database left in WAL mode makes SQLite materialise the <c>-wal</c>/<c>-shm</c> pair beside
/// it. <c>immutable=1</c> would suppress that and is deliberately not used — it also suppresses
/// reading the WAL, which would let this report a stale shape for a live file and call it current.
/// </remarks>
public static class StateDiagnostics
{
    /// <summary>
    /// Judges the file at <paramref name="databasePath"/> without opening it for writing. A caller
    /// that cannot construct a <see cref="State"/> at all can still call this.
    /// </summary>
    public static StateSchemaDiagnosis Diagnose(string? databasePath)
    {
        var expected = StateStore.SchemaVersion;

        if (databasePath is null || !File.Exists(databasePath))
            return new StateSchemaDiagnosis(databasePath, StateFileKind.Missing, null, expected, [], null,
                "durum veritabanı yok.");

        try
        {
            using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            connection.Open();

            if (Integrity(connection) is { } complaint)
                return new StateSchemaDiagnosis(databasePath, StateFileKind.Unreadable, null, expected, [], complaint,
                    $"durum veritabanı bütünlük denetimini geçmedi: {complaint}");

            var found = StateStore.ReadVersion(connection);
            if (found > expected)
                return new StateSchemaDiagnosis(databasePath, StateFileKind.Future, found, expected, [], "ok",
                    $"dosya şema sürümü {found}; bu ikili en çok {expected} biliyor — daha yeni bir oom sürümüyle açın.");

            if (found == 0 && !StateShape.HasOwnerContent(connection))
                return new StateSchemaDiagnosis(databasePath, StateFileKind.Empty, 0, expected, [], "ok",
                    "boş durum veritabanı — ilk yazan açılışta kurulur.");

            var missing = StateShape.Audit(connection, found);
            if (missing.Count > 0)
                return new StateSchemaDiagnosis(databasePath, StateFileKind.Incomplete, found, expected, missing, "ok",
                    $"dosya sürüm {found} olduğunu söylüyor ama o sürümün taşıması gereken {missing.Count} yapı yok: " +
                    $"{StateShape.Describe(missing)}");

            // Valid for the version it claims, and still beyond the ladder's reach. Reported as
            // incomplete because that is what the writing open will say, and a diagnosis that
            // promises an upgrade the product then refuses is worse than no diagnosis.
            var unreachable = StateShape.AuditUpgradePath(connection, found);
            if (unreachable.Count > 0)
                return new StateSchemaDiagnosis(databasePath, StateFileKind.Incomplete, found, expected, unreachable, "ok",
                    $"dosya sürüm {found} için geçerli, ama göç merdiveni onu sürüm {expected} biçimine tamamlayamıyor: " +
                    $"{StateShape.Describe(unreachable)}");

            if (found == 0)
                return new StateSchemaDiagnosis(databasePath, StateFileKind.Unversioned, 0, expected, [], "ok",
                    "sürüm damgası yok, içerik var — merdiven baştan uygulanabilir.");

            return found == expected
                ? new StateSchemaDiagnosis(databasePath, StateFileKind.Current, found, expected, [], "ok",
                    $"şema sürüm {found}, eksik yapı yok.")
                : new StateSchemaDiagnosis(databasePath, StateFileKind.Historic, found, expected, [], "ok",
                    $"şema sürüm {found}; {expected} sürümüne yükseltilebilir, eksik yapı yok.");
        }
        catch (SqliteException e)
        {
            return new StateSchemaDiagnosis(databasePath, StateFileKind.Unreadable, null, expected, [], e.Message,
                $"durum veritabanı okunamıyor: {e.Message}");
        }
    }

    /// <summary>The integrity complaint, or <c>null</c> when the file checks out.</summary>
    private static string? Integrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        var answer = command.ExecuteScalar()?.ToString();
        return answer is not null && answer.Equals("ok", StringComparison.OrdinalIgnoreCase)
            ? null
            : answer ?? "integrity_check yanıt vermedi";
    }
}
