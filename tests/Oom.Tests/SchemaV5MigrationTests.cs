using System.Text;
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests;

/// <summary>
/// Version 5 is the first schema step that exists because the previous one was not a step at all.
/// The retrieval index and the served ledger's scope columns were the tail of step 1, and step 1
/// ran on every open: a file stamped 4 gained tables, an index and columns every time a hook
/// process touched it, with no version change to mark it, no backup taken, and
/// <c>StateSchemaReport.Applied</c> reporting that nothing had been applied.
///
/// What these tests hold to, one file on disk at a time: the newer-version verdict comes before
/// the first persistent change; a file that holds anything is copied and the copy verified before
/// a statement runs against it; the steps and the stamp commit together or not at all; a file
/// already at this version is left alone; and no step ever deletes one of the owner's rows to make
/// its own DDL succeed.
/// </summary>
public sealed class SchemaV5MigrationTests
{
    private const string Stamp = "2026-01-01T00:00:00.0000000+00:00";

    [Fact(DisplayName = "Y-191 · Yeni veritabanı merdivenin tamamıyla 5'e kurulur, getirim dizini kendi basamağıdır")]
    public void Y191_FreshDatabaseIsProvisionedToFiveAndTheRetrievalIndexIsItsOwnStep()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            using var state = new State(null, null, database);
            var report = state.SchemaReport;

            Assert.Equal(0, report.FoundVersion);
            Assert.Equal(5, report.Version);
            Assert.Equal(5, report.Applied.Count);
            Assert.StartsWith("0→5: getirim dizini", report.Applied[4], StringComparison.Ordinal);
            Assert.Equal(5, state.Scalar("PRAGMA user_version"));

            // Nothing to copy: an empty file has no memory to lose, so no backup is written.
            Assert.Null(report.BackupPath);
            Assert.False(Directory.Exists(Path.Combine(root, "backup")));

            // The same shape the old code reached — reached by a numbered step instead of a passenger.
            Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'notes'"));
            Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'notes_fts'"));
            Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'oom_index_meta'"));
            Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_notes_updated'"));
            Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_retrieve_served_scope'"));
            Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM pragma_table_info('retrieve_served') WHERE name = 'content_hash'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// A real version 2 and a real version 3 file — the calls table genuinely lacks the columns
    /// those builds had not invented yet. The assertion is on content, not on counts: a row that
    /// survives with the wrong values has not survived.
    /// </summary>
    [Theory(DisplayName = "Y-192 · Sürüm 2 ve 3 dosyaları 5'e göç eder; eski satırların içeriği ve doğrulanmış yedek yerinde kalır")]
    [InlineData(2)]
    [InlineData(3)]
    public void Y192_OlderVersionsMigrateAndKeepTheContentOfTheirRows(int seeded)
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            Seed(database, seeded);

            using (var migrated = new State(null, null, database))
            {
                var report = migrated.SchemaReport;
                Assert.Equal(seeded, report.FoundVersion);
                Assert.Equal(5, report.Version);
                Assert.Equal(5, migrated.Scalar("PRAGMA user_version"));

                // The exact lines the owner reads back: one per step this file had not seen, and
                // none for the steps it had. Under the old code every step ran and only the
                // reporting was filtered, so this list was the one place the difference did not show.
                var steps = new Dictionary<int, string>
                {
                    [3] = "önbelleksiz girdi sayacı (uncached_in_tok)",
                    [4] = "bölünmüş sayaç anlambilimi (usage_rank, usage_semantics)",
                    [5] = "getirim dizini ve served defteri kapsamı (notes, notes_fts, oom_index_meta, retrieve_served kapsam sütunları)"
                };
                Assert.Equal(
                    Enumerable.Range(seeded + 1, 5 - seeded).Select(version => $"{seeded}→{version}: {steps[version]}").ToArray(),
                    report.Applied);

                // Content, not counts. These are the values the seeded build wrote.
                Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM calls WHERE purpose = 'eski satır' AND backend = 'claude' AND model = 'opus-4' AND in_chars = 120 AND out_chars = 240 AND out_tok = 31 AND ms = 900 AND outcome = 'ok'"));
                Assert.Equal(1, migrated.Scalar($"SELECT COUNT(*) FROM sessions WHERE session_id = 'oturum-1' AND transcript_path = 'E:/kasa/t.jsonl' AND last_turn_index = 7 AND last_flush_ts = '{Stamp}'"));
                Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM flush_log WHERE session_id = 'oturum-1' AND reason = 'stop' AND turns = 7 AND chars = 84"));
                Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM retrieve_served WHERE session_id = 'oturum-1' AND query_sig = 'sig-1' AND note = 'kavram-001.md'"));

                // The new columns arrive empty rather than guessed, and the served row keeps its defaults.
                Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM calls WHERE uncached_in_tok IS NULL AND usage_rank = 0 AND usage_semantics = 'legacy-total-input-v0'"));
                Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM retrieve_served WHERE vault = '' AND client = '' AND status = 'prepared' AND pid = 0 AND acked_ts IS NULL"));

                // A proven way back exists before a single statement runs against the original.
                Assert.NotNull(report.BackupPath);
                Assert.True(File.Exists(report.BackupPath), $"yedek dosyası yok: {report.BackupPath}");
                Assert.Equal(seeded, ReadLong(report.BackupPath!, "PRAGMA user_version"));
                Assert.Equal(1, ReadLong(report.BackupPath!, "SELECT COUNT(*) FROM calls WHERE purpose = 'eski satır' AND ms = 900"));
                Assert.Equal(1, ReadLong(report.BackupPath!, "SELECT COUNT(*) FROM sessions WHERE session_id = 'oturum-1'"));
                Assert.Equal(0, ReadLong(report.BackupPath!, "SELECT COUNT(*) FROM pragma_table_info('calls') WHERE name = 'usage_rank'"));
                Assert.Equal(0, ReadLong(report.BackupPath!, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'notes'"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// A version 4 file from a build that never carried the retrieval index. One step runs, and
    /// the file says so afterwards — which is the whole difference from the old behaviour, where
    /// the same tables appeared and <c>Applied</c> stayed empty.
    /// </summary>
    [Fact(DisplayName = "Y-193 · İndeksi hiç eklenmemiş sürüm 4 dosyası tek numaralı basamakla 5'e çıkar ve bunu bildirir")]
    public void Y193_VersionFourWithoutTheIndexGainsItAsOneNumberedStep()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            Seed(database, 4);

            using var migrated = new State(null, null, database);
            var report = migrated.SchemaReport;

            Assert.Equal(4, report.FoundVersion);
            Assert.Equal(5, report.Version);
            Assert.StartsWith("4→5: getirim dizini", Assert.Single(report.Applied), StringComparison.Ordinal);
            Assert.Equal(5, migrated.Scalar("PRAGMA user_version"));

            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'notes'"));
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'notes_fts'"));
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_retrieve_served_scope'"));
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM pragma_table_info('retrieve_served') WHERE name = 'content_hash'"));

            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM calls WHERE purpose = 'eski satır' AND ms = 900"));
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM retrieve_served WHERE note = 'kavram-001.md'"));

            // The copy is of the file as it was: no notes table, still stamped 4.
            Assert.NotNull(report.BackupPath);
            Assert.Equal(4, ReadLong(report.BackupPath!, "PRAGMA user_version"));
            Assert.Equal(0, ReadLong(report.BackupPath!, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'notes'"));
            Assert.Equal(1, ReadLong(report.BackupPath!, "SELECT COUNT(*) FROM retrieve_served"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// The file the released build actually leaves behind: stamped 4, and already carrying the
    /// tables, the index and the scope columns that step 1 kept re-applying to it. The migration
    /// must recognise it, stamp it, and touch none of its rows — the index it already has is not
    /// rebuilt and the notes row it already holds is not dropped and recreated.
    /// </summary>
    [Fact(DisplayName = "Y-194 · 021cc83 biçimindeki genişletilmiş sürüm 4 dosyası satırlarına dokunulmadan 5'e damgalanır")]
    public void Y194_TheExtendedVersionFourFileIsStampedWithoutTouchingItsRows()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            Seed(database, 4, retrievalIndex: true);

            using var migrated = new State(null, null, database);
            var report = migrated.SchemaReport;

            Assert.Equal(4, report.FoundVersion);
            Assert.Equal(5, report.Version);
            Assert.StartsWith("4→5: getirim dizini", Assert.Single(report.Applied), StringComparison.Ordinal);
            Assert.Equal(5, migrated.Scalar("PRAGMA user_version"));

            // Idempotent, not destructive: everything that was already there is still there, with
            // its content, and the notes row proves no DROP TABLE ran under the schema owner.
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM notes WHERE name = 'kavram-001.md' AND title = 'Kavram 1' AND body = 'gövde'"));
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_notes_updated'"));
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_retrieve_served_scope'"));
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM retrieve_served WHERE note = 'kavram-001.md'"));
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM calls WHERE purpose = 'eski satır' AND ms = 900"));

            Assert.NotNull(report.BackupPath);
            Assert.Equal(4, ReadLong(report.BackupPath!, "PRAGMA user_version"));
            Assert.Equal(1, ReadLong(report.BackupPath!, "SELECT COUNT(*) FROM notes"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// The scar this version number exists for. A file already at this version is opened and
    /// nothing is applied to it — proved the only way that cannot be argued with: an index this
    /// schema owns is dropped between the two opens, and the second open leaves it dropped.
    /// Under the old code step 1 ran every time and would have put it back, silently, in the
    /// owner's live database.
    /// </summary>
    [Fact(DisplayName = "Y-195 · Başarılı tekrar açılış hiçbir basamağı tekrar koşmaz, ikinci yedek üretmez")]
    public void Y195_ASuccessfulReopenReplaysNoStepAndTakesNoSecondBackup()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            Seed(database, 2);

            using (var migrated = new State(null, null, database))
                Assert.Equal(5, migrated.SchemaReport.Version);
            SqliteConnection.ClearAllPools();

            Assert.Single(Backups(root));

            // An outside hand removes something this schema owns. The next open must not quietly
            // reapply a historical step over the owner's file.
            Execute(database, "DROP INDEX ix_notes_updated;");
            SqliteConnection.ClearAllPools();

            using var reopened = new State(null, null, database);
            var report = reopened.SchemaReport;

            // The load-bearing one: a historical step did not run, so what it creates is still gone.
            Assert.Equal(0, reopened.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_notes_updated'"));

            Assert.Equal(5, report.FoundVersion);
            Assert.Equal(5, report.Version);
            Assert.Empty(report.Applied);
            Assert.Null(report.BackupPath);
            Assert.Single(Backups(root));
            Assert.Equal(1, reopened.Scalar("SELECT COUNT(*) FROM calls WHERE purpose = 'eski satır'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// The verdict comes first. <c>PRAGMA journal_mode=WAL</c> is written into the file header, so
    /// running it before the version check rewrote a database this executable had already decided
    /// it could not read. The evidence is the journal mode of the refused file: still the mode it
    /// arrived in.
    /// </summary>
    [Fact(DisplayName = "Y-196 · Daha yeni sürüm, journal_mode dahil hiçbir kalıcı değişiklik yapılmadan reddedilir")]
    public void Y196_ANewerVersionIsRefusedBeforeJournalModeTouchesTheFile()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            Execute(database, "CREATE TABLE calls(ts TEXT);INSERT INTO calls VALUES('x');PRAGMA user_version=9;");
            SqliteConnection.ClearAllPools();
            Assert.Equal("delete", ReadText(database, "PRAGMA journal_mode"));
            SqliteConnection.ClearAllPools();

            var error = Assert.Throws<StateSchemaException>(() => new State(null, null, database));
            Assert.Contains("9", error.Message, StringComparison.Ordinal);
            SqliteConnection.ClearAllPools();

            // Refused means untouched, and the header is part of the file.
            Assert.Equal("delete", ReadText(database, "PRAGMA journal_mode"));
            Assert.Equal(9, ReadLong(database, "PRAGMA user_version"));
            Assert.Equal(1, ReadLong(database, "SELECT COUNT(*) FROM calls"));
            Assert.False(Directory.Exists(Path.Combine(root, "backup")), "reddedilen dosya için yedek dizini açıldı");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// The old step opened with an unconditional DELETE over <c>retrieve_served</c>, justified in
    /// its own comment by "the table is empty on every installation in the field — nothing ever
    /// wrote to it". That nothing wrote to it is not evidence that it is empty. Here the rows are
    /// there, and the migration stops and says so instead of making its own DDL succeed.
    /// </summary>
    [Fact(DisplayName = "Y-197 · Çakışan served satırları sessizce silinmez; göç açık hatayla durur ve satırlar kalır")]
    public void Y197_ConflictingServedRowsStopTheMigrationInsteadOfBeingDeleted()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            Seed(database, 3, servedConflict: true);
            Assert.Equal(3, ReadLong(database, "SELECT COUNT(*) FROM retrieve_served"));
            SqliteConnection.ClearAllPools();

            var error = Assert.Throws<StateSchemaException>(() => new State(null, null, database));
            Assert.Contains("retrieve_served", error.Message, StringComparison.Ordinal);
            SqliteConnection.ClearAllPools();

            // Every row is still there, including both sides of the collision and the row that
            // only differs by content_hash's default.
            Assert.Equal(3, ReadLong(database, "SELECT COUNT(*) FROM retrieve_served"));
            Assert.Equal(2, ReadLong(database, "SELECT COUNT(*) FROM retrieve_served WHERE note = 'kavram-001.md' AND query_sig = 'sig-1'"));
            Assert.Equal(1, ReadLong(database, "SELECT COUNT(*) FROM retrieve_served WHERE note = 'kavram-002.md'"));
            Assert.Equal(0, ReadLong(database, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ix_retrieve_served_scope'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// A migration that stops halfway leaves the file at the version it arrived with AND at that
    /// version's shape — the two must agree, or the next open reads a version number that does not
    /// describe the file. Steps 4 and 5 are seeded to run together and step 5 to fail; afterwards
    /// the file must still be a version 3 file in every respect. Then the owner resolves the
    /// collision and the same file migrates cleanly, which is what "the step is idempotent and may
    /// be re-run" has to mean in practice.
    /// </summary>
    [Fact(DisplayName = "Y-198 · Yarıda kesilen göç geri alınır; dosya geldiği sürüm ve şekilde kalır, çözülünce göç tamamlanır")]
    public void Y198_AnInterruptedMigrationRollsBackAndResumesFromTheVersionTheFileCarries()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            Seed(database, 3, servedConflict: true);
            SqliteConnection.ClearAllPools();

            Assert.Throws<StateSchemaException>(() => new State(null, null, database));
            SqliteConnection.ClearAllPools();

            // Version and shape still agree, and they agree on 3: step 4's columns never landed.
            Assert.Equal(3, ReadLong(database, "PRAGMA user_version"));
            Assert.Equal(0, ReadLong(database, "SELECT COUNT(*) FROM pragma_table_info('calls') WHERE name = 'usage_rank'"));
            Assert.Equal(0, ReadLong(database, "SELECT COUNT(*) FROM pragma_table_info('calls') WHERE name = 'usage_semantics'"));
            Assert.Equal(0, ReadLong(database, "SELECT COUNT(*) FROM pragma_table_info('retrieve_served') WHERE name = 'vault'"));
            Assert.Equal(0, ReadLong(database, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'notes'"));
            Assert.Equal(1, ReadLong(database, "SELECT COUNT(*) FROM calls WHERE purpose = 'eski satır' AND ms = 900"));

            // The copy was taken before the attempt, so the owner has the pre-attempt file in hand.
            Assert.Single(Backups(root));
            Assert.Equal(3, ReadLong(Backups(root)[0], "PRAGMA user_version"));
            Assert.Equal(3, ReadLong(Backups(root)[0], "SELECT COUNT(*) FROM retrieve_served"));

            // The owner decides which row survives — the migration never decided it for them.
            Execute(database, "DELETE FROM retrieve_served WHERE rowid = (SELECT MAX(rowid) FROM retrieve_served WHERE note = 'kavram-001.md');");
            SqliteConnection.ClearAllPools();

            using var migrated = new State(null, null, database);
            Assert.Equal(3, migrated.SchemaReport.FoundVersion);
            Assert.Equal(5, migrated.SchemaReport.Version);
            Assert.Equal(5, migrated.Scalar("PRAGMA user_version"));
            Assert.Equal(2, migrated.Scalar("SELECT COUNT(*) FROM retrieve_served"));
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM calls WHERE purpose = 'eski satır' AND ms = 900"));
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ix_retrieve_served_scope'"));
            Assert.Equal(2, Backups(root).Length);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// The backup question is "does this file hold anything of the owner's", not "does it hold the
    /// ledger". Keying it on the <c>calls</c> table meant a database carrying sessions, a served
    /// ledger or a built index, and no ledger row, was migrated with no way back at all.
    /// </summary>
    [Fact(DisplayName = "Y-199 · Yedek koşulu calls tablosuna bağlı değildir: içerik taşıyan her dosya göçten önce kopyalanır")]
    public void Y199_TheBackupConditionIsNotKeyedOnTheCallsTable()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            Seed(database, 4, calls: false);
            Assert.Equal(0, ReadLong(database, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'calls'"));
            SqliteConnection.ClearAllPools();

            using var migrated = new State(null, null, database);
            var report = migrated.SchemaReport;

            Assert.Equal(4, report.FoundVersion);
            Assert.Equal(5, report.Version);
            Assert.NotNull(report.BackupPath);
            Assert.True(File.Exists(report.BackupPath), $"ledger satırı olmayan dosya yedeksiz göç etti: {report.BackupPath}");
            Assert.Equal(4, ReadLong(report.BackupPath!, "PRAGMA user_version"));
            Assert.Equal(1, ReadLong(report.BackupPath!, "SELECT COUNT(*) FROM sessions WHERE session_id = 'oturum-1' AND last_turn_index = 7"));
            Assert.Equal(1, ReadLong(report.BackupPath!, "SELECT COUNT(*) FROM retrieve_served WHERE note = 'kavram-001.md'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// Steps used to be able to lean on each other, because step 1 ran on every open and created
    /// every table with IF NOT EXISTS. Now that a step runs only for a file below its number, a
    /// step that assumes a table another step created crashes the migration outright — which is
    /// how this was found, on two existing fixtures, and not by reasoning about it.
    /// </summary>
    [Fact(DisplayName = "Y-200 · Basamak başka basamağın tablosunu varsaymaz: retrieve_served eksikse göç çakmaz, tabloyu kurar")]
    public void Y200_AStepDoesNotAssumeATableAnotherStepCreated()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            Seed(database, 4, served: false);
            Assert.Equal(0, ReadLong(database, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'retrieve_served'"));
            SqliteConnection.ClearAllPools();

            var failure = Record.Exception(() => { using var opened = new State(null, null, database); });
            Assert.True(failure is null, $"retrieve_served'i olmayan dosya göç edemedi: {failure?.Message}");
            SqliteConnection.ClearAllPools();

            using var migrated = new State(null, null, database);
            Assert.Equal(5, migrated.Scalar("PRAGMA user_version"));
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'retrieve_served'"));
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM pragma_table_info('retrieve_served') WHERE name = 'content_hash'"));
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_retrieve_served_scope'"));
            Assert.Equal(0, migrated.Scalar("SELECT COUNT(*) FROM retrieve_served"));

            // The table it had to create is empty; the rows it already had are untouched.
            Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM calls WHERE purpose = 'eski satır' AND ms = 900"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// A database shaped the way build <paramref name="version"/> left it — the calls table carries
    /// exactly the columns that build had, and nothing a later build added.
    /// </summary>
    private static void Seed(string database, int version, bool retrievalIndex = false, bool calls = true, bool servedConflict = false, bool served = true)
    {
        var sql = new StringBuilder();

        if (calls)
        {
            var columns = "ts TEXT, backend TEXT, component TEXT, tier TEXT, model TEXT, in_chars INTEGER, out_chars INTEGER, " +
                          "in_tok INTEGER, out_tok INTEGER, cache_r INTEGER, cache_w INTEGER, ms INTEGER, outcome TEXT, " +
                          "usage_source TEXT, purpose TEXT";
            if (version >= 2)
                columns += ", operation_id TEXT, attempt_id TEXT, attempt_no INTEGER";
            if (version >= 3)
                columns += ", uncached_in_tok INTEGER";
            if (version >= 4)
                columns += ", usage_rank INTEGER NOT NULL DEFAULT 0 CHECK(usage_rank BETWEEN 0 AND 2), usage_semantics TEXT NOT NULL DEFAULT 'legacy-total-input-v0'";

            sql.Append($"CREATE TABLE calls({columns});");
            if (version >= 2)
                sql.Append("CREATE UNIQUE INDEX ix_calls_attempt_id ON calls(attempt_id) WHERE attempt_id IS NOT NULL;");
            sql.Append("INSERT INTO calls(ts, backend, component, tier, model, in_chars, out_chars, out_tok, ms, outcome, usage_source, purpose) " +
                       $"VALUES('{Stamp}', 'claude', 'Master', 'Apex', 'opus-4', 120, 240, 31, 900, 'ok', 'measured', 'eski satır');");
        }

        sql.Append("CREATE TABLE sessions(session_id TEXT PRIMARY KEY, transcript_path TEXT, last_turn_index INTEGER, last_flush_ts TEXT);");
        sql.Append($"INSERT INTO sessions VALUES('oturum-1', 'E:/kasa/t.jsonl', 7, '{Stamp}');");
        sql.Append("CREATE TABLE flush_log(ts TEXT, session_id TEXT, reason TEXT, outcome TEXT, turns INTEGER, chars INTEGER, backend TEXT);");
        sql.Append($"INSERT INTO flush_log VALUES('{Stamp}', 'oturum-1', 'stop', 'ok', 7, 84, 'claude');");

        if (served)
        {
            var servedScope = retrievalIndex
                ? ", vault TEXT NOT NULL DEFAULT '', client TEXT NOT NULL DEFAULT '', entry TEXT NOT NULL DEFAULT '', " +
                  "content_hash TEXT NOT NULL DEFAULT '', status TEXT NOT NULL DEFAULT 'prepared', pid INTEGER NOT NULL DEFAULT 0, acked_ts TEXT"
                : string.Empty;
            sql.Append($"CREATE TABLE retrieve_served(session_id TEXT, query_sig TEXT, note TEXT, ts TEXT{servedScope});");
            sql.Append($"INSERT INTO retrieve_served(session_id, query_sig, note, ts) VALUES('oturum-1', 'sig-1', 'kavram-001.md', '{Stamp}');");
            if (servedConflict)
            {
                // Two deliveries of the same note recorded under the same scope key, which is
                // exactly what the version 1 table had no way to prevent.
                sql.Append($"INSERT INTO retrieve_served(session_id, query_sig, note, ts) VALUES('oturum-1', 'sig-1', 'kavram-001.md', '{Stamp}');");
                sql.Append($"INSERT INTO retrieve_served(session_id, query_sig, note, ts) VALUES('oturum-1', 'sig-1', 'kavram-002.md', '{Stamp}');");
            }
        }

        if (retrievalIndex)
        {
            sql.Append("CREATE TABLE notes(name TEXT PRIMARY KEY, title TEXT, aliases TEXT, tags TEXT, body TEXT, updated TEXT);");
            sql.Append("CREATE INDEX ix_notes_updated ON notes(updated);");
            sql.Append("CREATE VIRTUAL TABLE notes_fts USING fts5(name UNINDEXED, title, aliases, tags, body);");
            sql.Append("CREATE TABLE oom_index_meta(generation INTEGER NOT NULL, manifest_digest TEXT NOT NULL, built_at TEXT NOT NULL);");
            sql.Append("CREATE UNIQUE INDEX ix_retrieve_served_scope ON retrieve_served(vault, client, entry, session_id, query_sig, note, content_hash);");
            sql.Append("INSERT INTO notes VALUES('kavram-001.md', 'Kavram 1', '', '', 'gövde', '2026-01-01');");
        }

        sql.Append($"PRAGMA user_version={version};");
        Execute(database, sql.ToString());
        SqliteConnection.ClearAllPools();
    }

    /// <summary>The copies this state root holds, in the directory the migration writes them to.</summary>
    private static string[] Backups(string root)
    {
        var directory = Path.Combine(root, "backup");
        return Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ReadLong(string database, string sql) =>
        Convert.ToInt64(Read(database, sql) ?? 0L);

    private static string ReadText(string database, string sql) =>
        Read(database, sql)?.ToString() ?? string.Empty;

    private static object? Read(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
