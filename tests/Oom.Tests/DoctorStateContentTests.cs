// yazan: claude · opus-5
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests;

/// <summary>
/// The scan that decides whether a state root is abandoned rubble or somebody's memory.
///
/// It used to add up six tables — calls, flush_log, health, sessions, coverage, compile_runs —
/// and call the total "rows". The schema carries twenty tables. A root whose only records sat in
/// daily_ingest, retry_queue, ingest_done, quarantine or retrieve_served therefore counted zero,
/// was labelled "artık", and the report told the owner in so many words that it held nothing.
/// Reporting a root as empty is an invitation to delete it, so this particular wrong answer is
/// the expensive kind: the count has to be of the whole database, and where the whole database
/// cannot be read the verdict has to say so instead of guessing "boş".
/// </summary>
public sealed class DoctorStateContentTests
{
    /// <summary>
    /// Y-210 · Every table the previous count could not see, one root each. None of them may be
    /// called residue, and none of them may raise <c>stray-state-root</c> — including a table
    /// this build has never heard of, because a future table must not need this scan to be
    /// edited before it counts as content.
    /// </summary>
    [Fact(DisplayName = "Y-210 · Kısayol tablo listesi dışında kayıt taşıyan kök artık sayılmaz")]
    public void Y210_RowsOutsideTheShortlistedTablesStillCountAsContent()
    {
        var fixtureRoot = ScarFixture.TempDirectory();
        try
        {
            var localAppData = Path.Combine(fixtureRoot, "yerel");
            var roots = Path.Combine(localAppData, "oom");

            // Each root carries records in exactly ONE table the old six-table sum ignored, and
            // no vault.json — so the only thing standing between it and "artık" is the count.
            var seeds = new (string Label, string Sql)[]
            {
                ("daily_ingest", "INSERT INTO daily_ingest(name, digest, status, attempts, reasons, ts) VALUES ('2026-09-01.md', 'd1', 'ingested', 1, '', '2026-09-01T00:00:00+03:00')"),
                ("retry_queue", "INSERT INTO retry_queue(session_id, attempts, next_at, last_error) VALUES ('s1', 2, '2026-09-01T00:00:00+03:00', 'timeout')"),
                ("ingest_done", "INSERT INTO ingest_done(source, digest, ts) VALUES ('claude', 'abc', '2026-09-01T00:00:00+03:00')"),
                ("quarantine", "INSERT INTO quarantine(digest, source, reason, ts, path) VALUES ('def', 'claude', 'bozuk', '2026-09-01T00:00:00+03:00', 'x.md')"),
                ("retrieve_served", "INSERT INTO retrieve_served(session_id, query_sig, note, ts) VALUES ('s1', 'q1', 'note-001.md', '2026-09-01T00:00:00+03:00')"),
                // A table no version of this scan has a literal for. The rule must be "user table",
                // not "user table on a list somebody remembered to extend".
                ("bilinmeyen", "CREATE TABLE oom_gelecek_defter(id INTEGER PRIMARY KEY, govde TEXT); INSERT INTO oom_gelecek_defter(govde) VALUES ('gelecekte eklenmiş bir tablo')"),
            };

            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < seeds.Length; index++)
            {
                var path = Path.Combine(roots, RootName(index + 1));
                Provision(path, seeds[index].Sql);
                paths[seeds[index].Label] = path;
            }

            var doctor = new Doctor();
            var reports = doctor.InspectStateRoots(localAppData).ToDictionary(report => report.Path, StringComparer.Ordinal);
            var items = doctor.StateRootItems(localAppData);

            foreach (var (label, path) in paths)
            {
                var report = reports[path];
                Assert.True(report.Rows > 0,
                    $"'{label}' tablosunda kayıt var ama sayım {report.Rows} satır buldu: {path}");
                Assert.True(report.Status == "sahipsiz",
                    $"'{label}' tablosunda kayıt taşıyan kök '{report.Status}' diye sınıflandı, 'sahipsiz' olmalıydı: {path}");
                Assert.DoesNotContain(items, item => item.Code == "stray-state-root" && item.Key == Path.GetFileName(path));
                Assert.Contains(items, item => item.Code == "unattributed-state-root" && item.Key == Path.GetFileName(path));
            }

            // And the headline line must not announce six empty roots either.
            Assert.Contains(items, item => item.Code == "state-roots" && item.Detail.Contains("artık 0", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(fixtureRoot);
        }
    }

    /// <summary>
    /// Y-211 · The other half of the same fix. Counting "every table" naively would count FTS5's
    /// shadow tables, and a notes_fts that has never indexed a single note already carries rows
    /// in notes_fts_config and notes_fts_data — so every provisioned root would read as occupied
    /// and the honest "artık" verdict would vanish instead of becoming true. A freshly created,
    /// never-written state.db is empty, and the scan has to be able to say so.
    /// </summary>
    [Fact(DisplayName = "Y-211 · FTS gölge tabloları ve SQLite iç tabloları sayacı şişirmez")]
    public void Y211_FtsShadowTablesDoNotInflateTheCount()
    {
        var fixtureRoot = ScarFixture.TempDirectory();
        try
        {
            var localAppData = Path.Combine(fixtureRoot, "yerel");
            var path = Path.Combine(localAppData, "oom", RootName(1));
            Directory.CreateDirectory(path);

            var database = Path.Combine(path, VaultIdentity.DatabaseName);
            using (var state = new State(null, null, database))
            {
                // The trap, measured rather than assumed: the shadow tables are NOT empty here.
                Assert.True(state.Scalar("SELECT COUNT(*) FROM notes_fts_config") > 0,
                    "fikstür geçersiz: yeni bir notes_fts'in notes_fts_config gölgesi boş çıktı, bu testin yakaladığı tuzak yok demektir");
            }
            SqliteConnection.ClearAllPools();

            var doctor = new Doctor();
            var report = Assert.Single(doctor.InspectStateRoots(localAppData));
            Assert.True(report.Rows == 0,
                $"hiç yazılmamış bir state.db {report.Rows} satır bildirdi; gölge tablolar sayaca sızıyor");
            Assert.True(report.Status == "artık",
                $"hiç yazılmamış, künyesiz bir kök '{report.Status}' diye sınıflandı, 'artık' olmalıydı");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(fixtureRoot);
        }
    }

    /// <summary>
    /// Y-212 · "Bulamadım" is not "yok". A root carrying a structure this build does not
    /// understand — here an FTS4 index, a perfectly valid SQLite object that passes
    /// integrity_check and that nothing in this scan knows how to read — must not be handed the
    /// one verdict that means "there is nothing in here". Every ordinary table really is empty,
    /// which is exactly the shape that would otherwise produce a confident, wrong "boş".
    /// </summary>
    [Fact(DisplayName = "Y-212 · Boşluk kanıtlanamayan kök artık değil belirsiz sayılır")]
    public void Y212_UnprovableEmptinessIsNotReportedAsResidue()
    {
        var fixtureRoot = ScarFixture.TempDirectory();
        try
        {
            var localAppData = Path.Combine(fixtureRoot, "yerel");
            var path = Path.Combine(localAppData, "oom", RootName(1));
            Provision(path, "CREATE VIRTUAL TABLE eski_arama USING fts4(govde)");

            // The fixture's whole point is that nothing countable is in here: were the FTS4 index
            // to seed its own tables, this test would be measuring the wrong branch. And the file
            // must be sound, or the verdict under test would be "okunamıyor" instead.
            Assert.Equal(0L, Query(path,
                "SELECT (SELECT COUNT(*) FROM eski_arama_content) + (SELECT COUNT(*) FROM eski_arama_segments)" +
                " + (SELECT COUNT(*) FROM eski_arama_segdir) + (SELECT COUNT(*) FROM eski_arama_docsize)" +
                " + (SELECT COUNT(*) FROM eski_arama_stat)"));
            Assert.Equal("ok", Query(path, "PRAGMA integrity_check"));

            var doctor = new Doctor();
            var report = Assert.Single(doctor.InspectStateRoots(localAppData));
            Assert.True(report.Status == "belirsiz",
                $"tanınmayan bir yapı taşıyan kök '{report.Status}' diye sınıflandı, 'belirsiz' olmalıydı");

            var items = doctor.StateRootItems(localAppData);
            Assert.DoesNotContain(items, item => item.Code == "stray-state-root");
            Assert.Contains(items, item => item.Code == "state-root-indeterminate" && item.Key == Path.GetFileName(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(fixtureRoot);
        }
    }

    /// <summary>
    /// Y-213 · A corrupt file still reports "okunamıyor", and still reports zero rows. Corruption
    /// arrives by two different doors and both have to lead to the same verdict: a shredded data
    /// page, where <c>PRAGMA integrity_check</c> opens fine and simply <em>returns</em> a verdict
    /// that is not "ok", and a shredded schema page, where the read throws instead. Neither may
    /// come out as "artık" (a deletion candidate) or as "belirsiz" (merely unproven), because on
    /// a corrupt file a COUNT(*) still answers with a number and that number lies.
    /// </summary>
    [Fact(DisplayName = "Y-213 · Bozuk veritabanı okunamıyor kalır, artık'a da belirsiz'e de kaymaz")]
    public void Y213_ACorruptDatabaseStaysUnreadable()
    {
        var fixtureRoot = ScarFixture.TempDirectory();
        try
        {
            var localAppData = Path.Combine(fixtureRoot, "yerel");
            var roots = Path.Combine(localAppData, "oom");

            // (a) integrity_check returns a non-"ok" string: the last data page is shredded, the
            // header and schema are intact, so nothing throws and nothing warns us but the verdict.
            var reported = Path.Combine(roots, RootName(1));
            Provision(reported, "INSERT INTO flush_log(ts, session_id, reason, outcome, turns, chars, backend)" +
                " WITH RECURSIVE sayac(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM sayac WHERE n < 500)" +
                " SELECT '2026-09-01T00:00:00+03:00', 'oturum-' || n, 'sessionend', 'ok', 3, 3000, 'claude' FROM sayac");
            Shred(reported, from: -4096);
            Assert.NotEqual("ok", Query(reported, "PRAGMA integrity_check"));

            // (b) the read throws: the schema region itself is gone.
            var thrown = Path.Combine(roots, RootName(2));
            Provision(thrown, "INSERT INTO daily_ingest(name, status, ts) VALUES ('x.md', 'ingested', '2026-09-01T00:00:00+03:00')");
            Shred(thrown, from: 2048);

            var doctor = new Doctor();
            var reports = doctor.InspectStateRoots(localAppData).ToDictionary(report => report.Path, StringComparer.Ordinal);
            var items = doctor.StateRootItems(localAppData);

            foreach (var path in new[] { reported, thrown })
            {
                Assert.True(reports[path].Status == "okunamıyor",
                    $"bozuk veritabanı taşıyan kök '{reports[path].Status}' diye sınıflandı, 'okunamıyor' olmalıydı: {path}");
                Assert.Equal(0, reports[path].Rows);
                Assert.Contains(items, item => item.Code == "state-root-unreadable" && item.Key == Path.GetFileName(path));
            }
            Assert.DoesNotContain(items, item => item.Code is "stray-state-root" or "state-root-indeterminate");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(fixtureRoot);
        }
    }

    /// <summary>
    /// Y-214 · The scan reads. Walking twenty tables instead of six must not turn it into
    /// something that writes: no state.db brought into existence beside a root that has none, no
    /// schema added to one that has, no byte of any existing file disturbed.
    ///
    /// The one thing that does appear is SQLite's own: a read-only connection to a WAL database
    /// materialises <c>state.db-wal</c> and <c>state.db-shm</c> for the duration, and having no
    /// write access it cannot remove them on close. That is the read path this scan has always
    /// used and is unchanged here — it is pinned by name below so that it stays the single
    /// documented exception and cannot quietly grow into anything else.
    /// </summary>
    [Fact(DisplayName = "Y-214 · Tarama hiçbir kök, veritabanı veya şema oluşturmaz")]
    public void Y214_TheScanCreatesNoStateRootDatabaseOrSchema()
    {
        var fixtureRoot = ScarFixture.TempDirectory();
        try
        {
            var localAppData = Path.Combine(fixtureRoot, "yerel");
            var roots = Path.Combine(localAppData, "oom");

            var provisioned = Path.Combine(roots, RootName(1));
            Provision(provisioned, "INSERT INTO quarantine(digest, source, reason, ts, path) VALUES ('a', 'claude', 'bozuk', '2026-09-01T00:00:00+03:00', 'x.md')");

            // A root with no database at all, and a root holding a file that is not a database.
            var bare = Path.Combine(roots, RootName(2));
            var junk = Path.Combine(roots, RootName(3));
            Directory.CreateDirectory(bare);
            Directory.CreateDirectory(junk);
            File.WriteAllText(Path.Combine(junk, "okuma.txt"), "durum kökü değil");

            var schemaBefore = Query(provisioned, "SELECT group_concat(name || '|' || COALESCE(sql, ''), char(10)) FROM sqlite_master ORDER BY name");
            var before = Snapshot(localAppData);
            var directoriesBefore = Directory.GetDirectories(roots, "*", SearchOption.AllDirectories).Length;

            var doctor = new Doctor();
            doctor.InspectStateRoots(localAppData);
            doctor.StateRootItems(localAppData);
            SqliteConnection.ClearAllPools();

            var after = Snapshot(localAppData);
            var sidecars = new[] { VaultIdentity.DatabaseName + "-wal", VaultIdentity.DatabaseName + "-shm" };
            var appeared = after.Keys.Except(before.Keys)
                .Where(path => !sidecars.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                .ToArray();
            Assert.True(appeared.Length == 0, $"tarama dosya oluşturdu: {string.Join(", ", appeared)}");

            // Nothing that was already there may have moved a byte.
            foreach (var (path, length) in before)
                Assert.True(after.TryGetValue(path, out var now) && now == length,
                    $"tarama mevcut bir dosyayı değiştirdi ya da sildi: {path} ({length} → {(after.TryGetValue(path, out var n) ? n : -1)})");

            // No new root directory, and the empty root is still empty: a scan that provisions is
            // a scan that lies about what it found the next time it runs.
            Assert.Equal(directoriesBefore, Directory.GetDirectories(roots, "*", SearchOption.AllDirectories).Length);
            Assert.False(File.Exists(Path.Combine(bare, VaultIdentity.DatabaseName)),
                $"tarama veritabanı olmayan bir köke state.db yazdı: {bare}");
            Assert.Empty(Directory.GetFileSystemEntries(bare));

            // And not one table, index or trigger added to the database it did read.
            Assert.Equal(schemaBefore, Query(provisioned, "SELECT group_concat(name || '|' || COALESCE(sql, ''), char(10)) FROM sqlite_master ORDER BY name"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(fixtureRoot);
        }
    }

    /// <summary>Sixteen lowercase hex digits — the only directory shape the scan accepts as a state root.</summary>
    private static string RootName(int index) => index.ToString("x16");

    /// <summary>Overwrites a fixture database from the given offset on; a negative offset counts back from the end.</summary>
    private static void Shred(string root, int from)
    {
        var database = Path.Combine(root, VaultIdentity.DatabaseName);
        var bytes = File.ReadAllBytes(database);
        for (var offset = from < 0 ? bytes.Length + from : from; offset < bytes.Length; offset++)
            bytes[offset] = 0x5A;
        File.WriteAllBytes(database, bytes);
    }

    /// <summary>One scalar out of a fixture root's database, read through a connection of this test's own.</summary>
    private static object? Query(string root, string sql)
    {
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, VaultIdentity.DatabaseName)};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
    }

    /// <summary>A state root of this run's own: the real schema, then the given seed SQL.</summary>
    private static void Provision(string root, string seed)
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, VaultIdentity.DatabaseName);
        using (var state = new State(null, null, database)) { }
        SqliteConnection.ClearAllPools();

        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = seed;
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Every file under the fixture profile with its length, so a byte of drift is visible.</summary>
    private static Dictionary<string, long> Snapshot(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => new FileInfo(path).Length, StringComparer.OrdinalIgnoreCase);
}
