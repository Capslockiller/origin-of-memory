using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Gates;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class DurumDeposuScars
{

    [Fact(DisplayName = "Y-063 · Sekiz eşzamanlı health yazımı kaybolmaz ve 25 saatlik uyarı eskidir")]
    public void Y063_HealthWritesAreLockedAndWarningsAge()
    {
        var items = Enumerable.Range(0, 8).Select(i => new HealthItem("test", HealthLevel.Warning, $"w-{i}", i.ToString(), "uyarı", i == 0)).ToArray();
        var written = new State().WriteHealthConcurrently(items);
        Assert.Equal(8, written.Count);
        Assert.True(written.Single(x => x.Code == "w-0").Stale);
    }

    [Fact(DisplayName = "Y-132 · Salt okunur açılış hiçbir durum kökü yaratmaz; yazan açılış yaratır")]
    public void Y132_ReadOnlyOpenCreatesNoStateRoot()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var missing = Path.Combine(root, "yok");
            var database = Path.Combine(missing, "state.db");

            Assert.Null(State.OpenReadOnly(database));
            Assert.False(Directory.Exists(missing));
            Assert.Throws<FileNotFoundException>(() => new State(null, null, database, StateAccess.ReadOnly));
            Assert.False(Directory.Exists(missing));

            using (var writer = new State(null, null, database))
                writer.RecordFlush(ScarFixture.Now, "y132", "sessionend", "ok", 4, 40, "claude");
            SqliteConnection.ClearAllPools();
            Assert.True(File.Exists(database));

            using var reader = State.OpenReadOnly(database);
            Assert.NotNull(reader);
            Assert.Equal(StateAccess.ReadOnly, reader!.Access);
            Assert.Equal(1, reader.Scalar("SELECT COUNT(*) FROM flush_log"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    [Fact(DisplayName = "Y-161 · Yayımlanan exe: salt okunur komut durum kökü yaratmaz, yazan komut yaratır")]
    public void Y161_ShippedExecutableCreatesNoStateRootOnReadOnlyCommandsButDoesOnWrite()
    {
        var root = ScarFixture.TempDirectory();
        var vault = Path.Combine(root, "kasa");
        var profile = Path.Combine(root, "profil");
        Directory.CreateDirectory(vault);
        Directory.CreateDirectory(Path.Combine(profile, "oom"));
        var previous = Environment.GetEnvironmentVariable("OOM_LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("OOM_LOCALAPPDATA", profile);
            var stateRoot = VaultIdentity.StateRoot(vault);
            Assert.StartsWith(profile, stateRoot, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(stateRoot), $"önceki bir koşumdan kalma durum kökü temizlenmemiş: {stateRoot}");

            var context = GateFixture.RunScoped(profile, "context", "--vault", vault);
            Assert.True(context.ExitCode == 0, $"'context --vault {vault}' çıkış kodu {context.ExitCode} döndürdü: {context.StandardError}");
            Assert.False(Directory.Exists(stateRoot), $"salt okunur 'context' komutu durum kökünü yarattı: {stateRoot}");

            var retrieve = GateFixture.RunScoped(profile, "retrieve", "--query", "tokenizasyon", "--vault", vault);
            Assert.False(Directory.Exists(stateRoot),
                $"salt okunur 'retrieve' komutu durum kökünü yarattı: {stateRoot} (stderr: {retrieve.StandardError})");

            // R24(b): `compile --dry-run` is read-only too; the real `compile` is the writer.
            var dryRun = GateFixture.RunScoped(profile, "compile", "--dry-run", "--vault", vault);
            Assert.True(dryRun.ExitCode == 0, $"'compile --dry-run --vault {vault}' çıkış kodu {dryRun.ExitCode} döndürdü: {dryRun.StandardError}");
            Assert.False(Directory.Exists(stateRoot), $"salt okunur 'compile --dry-run' komutu durum kökünü yarattı: {stateRoot}");

            var compile = GateFixture.RunScoped(profile, "compile", "--vault", vault);
            Assert.True(compile.ExitCode == 0, $"'compile --vault {vault}' çıkış kodu {compile.ExitCode} döndürdü: {compile.StandardError}");
            Assert.True(Directory.Exists(stateRoot), $"yazan 'compile' komutu durum kökünü yaratmadı: {stateRoot}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OOM_LOCALAPPDATA", previous);
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    [Fact(DisplayName = "Y-302 · Durum dosyası silinebilir; yeni açılış on tabloyu sıfırdan kurar ve sürüm damgası aramaz")]
    public void Y302_DeletedStateFileIsRebuiltFromScratchOnTheNextOpen()
    {
        // 3.1.0 wave 2 (driver edit): the additive 'coverage' table (F5-2) joins the schema.
        string[] expected =
        [
            "coverage", "daily_ingest", "flush_log", "health", "notes", "notes_fts",
            "oom_index_meta", "retry_queue", "sessions", "sweep_stamps"
        ];

        var root = ScarFixture.TempDirectory();
        var database = Path.Combine(root, "state.db");
        try
        {
            using (var first = new State(null, null, database))
            {
                Assert.Equal(expected, Tables(first));
                first.RecordFlush(ScarFixture.Now, "y302", "sessionend", "ok", 3, 30, "claude");
                Assert.Equal(1, first.Scalar("SELECT COUNT(*) FROM flush_log"));
                Assert.Equal(0, first.Scalar("PRAGMA user_version"));
                Assert.Equal(0, first.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'view'"));
            }

            SqliteConnection.ClearAllPools();
            File.Delete(database);
            Assert.False(File.Exists(database));

            using var rebuilt = new State(null, null, database);
            Assert.Equal(expected, Tables(rebuilt));
            Assert.Equal(0, rebuilt.Scalar("SELECT COUNT(*) FROM flush_log"));
            rebuilt.RecordFlush(ScarFixture.Now, "y302", "sessionend", "ok", 3, 30, "claude");
            Assert.Equal(1, rebuilt.Scalar("SELECT COUNT(*) FROM flush_log"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    private static string[] Tables(State state)
    {
        var names = new List<string>();
        using var connection = new SqliteConnection($"Data Source={Path.Combine(state.WorkDirectory, "state.db")};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' " +
            "AND name NOT LIKE 'notes\\_fts\\_%' ESCAPE '\\' ORDER BY name";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return [.. names];
    }

    [Fact(DisplayName = "Y-304 · Eski şemalı state.db göç edilmez: kenara alınır, yenisi sıfırdan kurulur, eski dosya silinmez")]
    public void OlderShapeIsRetiredNotMigrated()
    {
        var root = Directory.CreateTempSubdirectory("oom-scar-y304-").FullName;
        try
        {
            var path = Path.Combine(root, "state.db");
            using (var old = new SqliteConnection($"Data Source={path}"))
            {
                old.Open();
                using var ddl = old.CreateCommand();
                ddl.CommandText = "CREATE TABLE sessions(session_id TEXT PRIMARY KEY, transcript_path TEXT, last_turn_index INTEGER, last_flush_ts TEXT); INSERT INTO sessions VALUES ('s-eski', 'x', 3, 't'); PRAGMA user_version = 5;";
                ddl.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            using (var state = new State(null, null, path))
            {
                Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name = 'prompt_count'"));
                Assert.Equal(0, state.Scalar("SELECT COUNT(*) FROM sessions"));
                Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM health WHERE code = 'eski-sema'"));
            }
            SqliteConnection.ClearAllPools();

            var retired = Directory.GetFiles(root, "state.db.eski-*").Where(f => !f.EndsWith("-wal") && !f.EndsWith("-shm")).ToArray();
            Assert.Single(retired);
            using var check = new SqliteConnection($"Data Source={retired[0]};Mode=ReadOnly");
            check.Open();
            using var count = check.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM sessions";
            Assert.Equal(1L, count.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

}
