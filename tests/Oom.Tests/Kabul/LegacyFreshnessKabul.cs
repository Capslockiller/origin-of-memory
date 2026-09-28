using Microsoft.Data.Sqlite;
using Oom.Contracts;

namespace Oom.Tests.Kabul;

using Oom;

public sealed class LegacyFreshnessKabul
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void LegacySweepSummary_DrivesFreshnessNoticeAndDoctor_WithoutWritingState()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        var dbPath = StateDbPath(harness, vault);
        var sweptAt = Now.AddDays(-14);
        using (var state = new State(null, null, dbPath))
            state.RecordFlush(sweptAt, "sweep", "sweep", "summary", 0, 0, "none");
        DropTable(dbPath, "coverage");

        using (var state = new State(null, null, dbPath, StateAccess.ReadOnly))
        {
            var freshness = MemoryFreshness.Read(vault, state, Now);
            Assert.Equal(sweptAt, freshness.LastSweep);
            Assert.True(freshness.SweepOverdue(Now));
        }
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(dbPath);

        var context = harness.Run(vault, ["context"], fakeNow: Now);
        Assert.Equal(0, context.ExitCode);
        Assert.Contains("hafiza: son tarama 14 gun once", Fold(context.Stdout), StringComparison.Ordinal);

        var doctor = harness.Run(vault, ["doctor"], fakeNow: Now);
        Assert.Equal(1, doctor.ExitCode);
        Assert.Contains("son tarama: 336 saat once", Fold(doctor.Stdout), StringComparison.Ordinal);
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(dbPath)),
            "expected legacy state.db bytes to stay unchanged after context and doctor");
    }

    [Fact]
    public void ReadOnlyCompile_ToleratesDailyIngestWithoutDigest_AndDoesNotMigrate()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        File.WriteAllText(Path.Combine(vault, "daily", "2026-09-27.md"), "gunluk");
        var dbPath = StateDbPath(harness, vault);
        CreateLegacyStateWithoutDigest(dbPath, includeHealth: true);
        var before = File.ReadAllBytes(dbPath);

        var result = harness.Run(vault, ["compile", "--dry-run"], fakeNow: Now);

        Assert.True(result.ExitCode == 0,
            $"expected compile --dry-run exit=0 on missing digest; actual exit={result.ExitCode}, stderr={result.Stderr.Trim()}");
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(dbPath)),
            "expected read-only compile not to migrate state.db");
    }

    [Fact]
    public void ReadOnlyContext_ToleratesMissingHealthTable_AndDoesNotCreateIt()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        var dbPath = StateDbPath(harness, vault);
        CreateLegacyStateWithoutDigest(dbPath, includeHealth: false);
        var before = File.ReadAllBytes(dbPath);

        var result = harness.Run(vault, ["context"], fakeNow: Now);

        Assert.True(result.ExitCode == 0,
            $"expected context exit=0 without health table; actual exit={result.ExitCode}, stderr={result.Stderr.Trim()}");
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(dbPath)),
            "expected read-only context not to create health table");
    }

    [Fact]
    public void ReadWriteOpen_AddsMissingDailyColumnsInPlace_AndPreservesRows()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        var dbPath = StateDbPath(harness, vault);
        CreateLegacyStateWithoutDigest(dbPath, includeHealth: true);

        using (var state = new State(null, null, dbPath, StateAccess.ReadWrite))
            Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM daily_ingest WHERE name = '2026-09-27.md'"));

        var columns = Columns(dbPath, "daily_ingest");
        Assert.Contains("digest", columns);
        Assert.Contains("rejections", columns);
        Assert.Contains("last_success_ts", columns);
    }

    private static string BuildVault(string root)
    {
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts"));
        return vault;
    }

    private static string StateDbPath(KabulHarness harness, string vault)
    {
        var canonical = Path.GetFullPath(vault).TrimEnd(Path.DirectorySeparatorChar);
        return Path.Combine(harness.LocalAppData, "oom", VaultIdentity.Hash(canonical), VaultIdentity.DatabaseName);
    }

    private static void DropTable(string dbPath, string table)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE {table}";
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private static void CreateLegacyStateWithoutDigest(string dbPath, bool includeHealth)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE sessions(session_id TEXT PRIMARY KEY, transcript_path TEXT, last_turn_index INTEGER, last_flush_ts TEXT, prompt_count INTEGER NOT NULL DEFAULT 0, first_seen TEXT, last_prompt_ts TEXT);" +
            "CREATE TABLE flush_log(ts TEXT, session_id TEXT, reason TEXT, outcome TEXT, turns INTEGER, chars INTEGER, backend TEXT);" +
            "CREATE TABLE retry_queue(session_id TEXT PRIMARY KEY, attempts INTEGER, next_at TEXT, last_error TEXT);" +
            "CREATE TABLE sweep_stamps(path TEXT PRIMARY KEY, mtime TEXT, size INTEGER, outcome TEXT);" +
            "CREATE TABLE daily_ingest(name TEXT PRIMARY KEY, status TEXT, attempts INTEGER, reasons TEXT, ts TEXT);" +
            "INSERT INTO daily_ingest(name,status,attempts,reasons,ts) VALUES ('2026-09-27.md','ingested',1,'','2026-09-27T14:00:00+03:00');" +
            "CREATE TABLE notes(name TEXT PRIMARY KEY, title TEXT, aliases TEXT, tags TEXT, body TEXT, updated TEXT);" +
            "CREATE VIRTUAL TABLE notes_fts USING fts5(name UNINDEXED, title, aliases, tags, body);" +
            "CREATE TABLE oom_index_meta(generation INTEGER NOT NULL, manifest_digest TEXT NOT NULL, built_at TEXT NOT NULL);" +
            (includeHealth ? "CREATE TABLE health(ts TEXT, component TEXT, level TEXT, code TEXT, key TEXT, detail TEXT);" : string.Empty);
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private static IReadOnlyList<string> Columns(string dbPath, string table)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static string Fold(string text) => text
        .Replace('ı', 'i').Replace('İ', 'I').Replace('ş', 's').Replace('Ş', 'S')
        .Replace('ğ', 'g').Replace('Ğ', 'G').Replace('ü', 'u').Replace('Ü', 'U')
        .Replace('ö', 'o').Replace('Ö', 'O').Replace('ç', 'c').Replace('Ç', 'C')
        .ToLowerInvariant();
}
