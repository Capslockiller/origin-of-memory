using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests;

/// <summary>
/// The version–shape contract of the state database.
///
/// The ladder replays a step only when the file's version is below it, which is correct and is
/// version 5's whole reason for existing. What was never checked is the assumption underneath it:
/// that a file stamped N really did receive everything version N shipped with. These tests hold
/// both halves of the answer.
///
/// The fixtures here are built from the shapes this repository actually released, read off the
/// revisions themselves rather than off today's <c>BaseTables</c>:
/// <c>100e6ce</c> (version 1), tags <c>v2.0.0</c>/<c>v2.0.1</c> (version 2), tag <c>v2.1.0</c>
/// (version 3) and <c>ea3879e</c> (version 4). That matters: the shipped builds wrote a 15-column
/// <c>calls</c>, while today's step 1 writes 21 columns for a fresh file. A fixture copied from
/// today's step 1 would be a version that never existed, and would have hidden the defect
/// <c>Y-231</c> pins.
/// </summary>
public sealed class SchemaIntegrityTests
{
    private const string Stamp = "2026-01-01T00:00:00.0000000+00:00";

    // ---------------------------------------------------------------- valid versions upgrade

    [Fact(DisplayName = "Y-230 · Yayımlanmış sürüm 2 yükseltilir: kayıt içerikleri korunur, yedek alınır ve doğrulanır")]
    public void Y230_AShippedVersionTwoUpgradesKeepingTheContentOfItsRows()
    {
        WithRoot((root, database) =>
        {
            Seed(database, 2);

            string backup;
            using (var state = new State(null, null, database))
            {
                var report = state.SchemaReport;
                Assert.Equal(2, report.FoundVersion);
                Assert.Equal(5, report.Version);
                Assert.Empty(report.Missing);
                Assert.NotEmpty(report.Applied);
                backup = Assert.IsType<string>(report.BackupPath);
            }

            SqliteConnection.ClearAllPools();

            // Values, not counts: a count survives a migration that rewrote every row.
            Assert.Equal("eski satır", ReadText(database, "SELECT purpose FROM calls"));
            Assert.Equal(120L, ReadLong(database, "SELECT in_chars FROM calls"));
            Assert.Equal("E:/kasa/t.jsonl", ReadText(database, "SELECT transcript_path FROM sessions"));
            Assert.Equal("kavram-001.md", ReadText(database, "SELECT note FROM retrieve_served"));
            Assert.Equal("daily/2026-01-01.md", ReadText(database, "SELECT path FROM sweep_stamps"));

            // The columns version 2 never had arrive empty rather than guessed.
            Assert.Equal(1L, ReadLong(database, "SELECT COUNT(*) FROM calls WHERE operation_id IS NULL AND uncached_in_tok IS NULL"));

            // The backup is a real copy of what was there before, at the version it was before.
            Assert.True(File.Exists(backup));
            Assert.Equal(2L, ReadLong(backup, "PRAGMA user_version"));
            Assert.Equal("eski satır", ReadText(backup, "SELECT purpose FROM calls"));
            Assert.Equal("daily/2026-01-01.md", ReadText(backup, "SELECT path FROM sweep_stamps"));
            Assert.Equal("ok", ReadText(backup, "PRAGMA integrity_check"));
        });
    }

    [Fact(DisplayName = "Y-231 · Yayımlanmış sürüm 3 çağrı kimliği sütunlarını kazanır: göç sonrası defter yazılabilir")]
    public void Y231_AShippedVersionThreeGainsTheCallIdentityColumnsTheLadderNumbersSkipped()
    {
        WithRoot((root, database) =>
        {
            // Tag v2.1.0 stamps 3 and writes a 15-column calls table. The ladder hands out
            // operation_id/attempt_id/attempt_no at step 2 and uncached_in_tok at step 3, both of
            // which a file stamped 3 skips by number — yet stamp 3 never carried them. Gated on
            // the number alone this file reaches "version 5" unable to accept a single ledger row.
            Seed(database, 3);
            Assert.Equal(0L, ReadLong(database, "SELECT COUNT(*) FROM pragma_table_info('calls') WHERE name = 'operation_id'"));

            using (var state = new State(null, null, database))
            {
                Assert.Equal(3, state.SchemaReport.FoundVersion);
                Assert.Equal(5, state.SchemaReport.Version);
                Assert.Empty(state.SchemaReport.Missing);

                // The measurement that matters: RecordCall names every one of these columns.
                state.RecordCall("claude", ComponentKind.Flush, ModelTier.Fast, "opus-4", 10, 5, 20, "ok",
                    UsageSourceKind.Measured, "summary", "islem-1", "deneme-1", 1, new TokenUsage(7, 3, 0, 0));

                Assert.Equal(1L, state.Scalar("SELECT COUNT(*) FROM calls WHERE operation_id = 'islem-1'"));
            }

            SqliteConnection.ClearAllPools();
            foreach (var column in new[] { "operation_id", "attempt_id", "attempt_no", "uncached_in_tok", "usage_rank", "usage_semantics" })
                Assert.Equal(1L, ReadLong(database, $"SELECT COUNT(*) FROM pragma_table_info('calls') WHERE name = '{column}'"));

            Assert.Equal(1L, ReadLong(database, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_calls_attempt_id'"));
            Assert.Equal("eski satır", ReadText(database, "SELECT purpose FROM calls WHERE operation_id IS NULL"));
        });
    }

    [Fact(DisplayName = "Y-232 · Geçerli sürüm 4 tek basamakla 5'e çıkar ve satırları değişmez")]
    public void Y232_AValidVersionFourTakesTheRetrievalIndexStepAlone()
    {
        WithRoot((root, database) =>
        {
            Seed(database, 4);

            using (var state = new State(null, null, database))
            {
                Assert.Equal(4, state.SchemaReport.FoundVersion);
                Assert.Equal(5, state.SchemaReport.Version);
                Assert.Empty(state.SchemaReport.Missing);
                Assert.Equal(["4→5: getirim dizini ve served defteri kapsamı (notes, notes_fts, oom_index_meta, retrieve_served kapsam sütunları)"],
                    state.SchemaReport.Applied);
            }

            SqliteConnection.ClearAllPools();
            Assert.Equal("eski satır", ReadText(database, "SELECT purpose FROM calls"));
            Assert.Equal(1L, ReadLong(database, "SELECT COUNT(*) FROM pragma_table_info('retrieve_served') WHERE name = 'content_hash'"));
        });
    }

    [Fact(DisplayName = "Y-233 · Genişletilmiş sürüm 4 reddedilmez: fazlası olan dosya bozuk değildir")]
    public void Y233_AnExtendedVersionFourIsStampedWithoutTouchingItsRows()
    {
        WithRoot((root, database) =>
        {
            // Revision 4c51a01 shipped the version 5 shape under stamp 4. The audit is a floor,
            // not an equality: a file carrying more than its version promised is not damaged.
            Seed(database, 4, extended: true);
            Execute(database, "CREATE TABLE oom_sahibin_tablosu(id INTEGER PRIMARY KEY, govde TEXT);" +
                              "INSERT INTO oom_sahibin_tablosu(govde) VALUES('sahibin verisi');");

            using (var state = new State(null, null, database))
            {
                Assert.Equal(4, state.SchemaReport.FoundVersion);
                Assert.Equal(5, state.SchemaReport.Version);
                Assert.Empty(state.SchemaReport.Missing);
            }

            SqliteConnection.ClearAllPools();
            Assert.Equal("Kavram 1", ReadText(database, "SELECT title FROM notes"));
            Assert.Equal("sahibin verisi", ReadText(database, "SELECT govde FROM oom_sahibin_tablosu"));
            Assert.Equal(5L, ReadLong(database, "PRAGMA user_version"));
        });
    }

    [Fact(DisplayName = "Y-234 · Sağlam sürüm 5 tekrar açılışta hiçbir basamak koşmaz ve ikinci yedek üretmez")]
    public void Y234_AHealthyCurrentFileIsReopenedWithoutAnyStepOrBackup()
    {
        WithRoot((root, database) =>
        {
            using (var first = new State(null, null, database))
                Assert.Equal(5, first.SchemaReport.Version);
            SqliteConnection.ClearAllPools();

            using var reopened = new State(null, null, database);
            var report = reopened.SchemaReport;

            Assert.Equal(5, report.FoundVersion);
            Assert.Equal(5, report.Version);
            Assert.Empty(report.Applied);
            Assert.Empty(report.Missing);
            Assert.Null(report.BackupPath);
            Assert.Empty(Backups(root));
        });
    }

    // ---------------------------------------------------------------- refusals

    [Fact(DisplayName = "Y-235 · Eksik sweep_stamps taşıyan sürüm 3 reddedilir; dosya baytına dokunulmaz")]
    public void Y235_AVersionThreeMissingSweepStampsIsRefusedWithoutTouchingTheFile()
    {
        WithRoot((root, database) =>
        {
            Seed(database, 3);
            Execute(database, "DROP TABLE sweep_stamps;");
            SqliteConnection.ClearAllPools();
            var before = Fingerprint(database);

            var error = Assert.Throws<StateSchemaException>(() => new State(null, null, database));

            Assert.Contains("sweep_stamps", error.Message, StringComparison.Ordinal);
            Assert.Contains("3", error.Message, StringComparison.Ordinal);
            AssertUntouched(database, before, expectedVersion: 3);

            // Reporting the hole did not dig it: no DDL ran to make the missing table reportable.
            Assert.Equal(0L, ReadLong(database, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'sweep_stamps'"));
            Assert.Empty(Backups(root));
        });
    }

    [Fact(DisplayName = "Y-236 · Eksik calls sütunu taşıyan sürüm 4 reddedilir; veri ve sürüm değişmez")]
    public void Y236_AVersionFourMissingACallsColumnIsRefused()
    {
        WithRoot((root, database) =>
        {
            // SQLite cannot drop a column a CHECK constraint mentions, so the table is rebuilt
            // without usage_semantics — the same shape a half-applied ALTER would leave behind.
            Seed(database, 4);
            Execute(database,
                "ALTER TABLE calls RENAME TO calls_eski;" +
                "CREATE TABLE calls(ts TEXT, backend TEXT, component TEXT, tier TEXT, model TEXT, in_chars INTEGER, " +
                "out_chars INTEGER, in_tok INTEGER, out_tok INTEGER, cache_r INTEGER, cache_w INTEGER, ms INTEGER, " +
                "outcome TEXT, usage_source TEXT, purpose TEXT, operation_id TEXT, attempt_id TEXT, attempt_no INTEGER, " +
                "uncached_in_tok INTEGER, usage_rank INTEGER NOT NULL DEFAULT 0);" +
                "INSERT INTO calls(ts, backend, purpose) SELECT ts, backend, purpose FROM calls_eski;" +
                "DROP TABLE calls_eski;");
            SqliteConnection.ClearAllPools();
            var before = Fingerprint(database);

            var error = Assert.Throws<StateSchemaException>(() => new State(null, null, database));

            Assert.Contains("calls.usage_semantics", error.Message, StringComparison.Ordinal);
            AssertUntouched(database, before, expectedVersion: 4);
            Assert.Equal("eski satır", ReadText(database, "SELECT purpose FROM calls"));
        });
    }

    [Fact(DisplayName = "Y-237 · Eksik görünüm taşıyan sürüm 5 reddedilir")]
    public void Y237_ACurrentFileMissingAViewIsRefused()
    {
        WithRoot((root, database) =>
        {
            using (var _ = new State(null, null, database)) { }
            SqliteConnection.ClearAllPools();
            Execute(database, "DROP VIEW v_health;");
            SqliteConnection.ClearAllPools();
            var before = Fingerprint(database);

            var error = Assert.Throws<StateSchemaException>(() => new State(null, null, database));

            Assert.Contains("görünüm v_health", error.Message, StringComparison.Ordinal);
            AssertUntouched(database, before, expectedVersion: 5);
        });
    }

    [Fact(DisplayName = "Y-238 · Eksik indeks taşıyan sürüm 5 reddedilir ve indeks sessizce geri kurulmaz")]
    public void Y238_ACurrentFileMissingAnIndexIsRefusedAndTheIndexIsNotQuietlyRebuilt()
    {
        WithRoot((root, database) =>
        {
            using (var _ = new State(null, null, database)) { }
            SqliteConnection.ClearAllPools();
            Execute(database, "DROP INDEX ix_notes_updated;");
            SqliteConnection.ClearAllPools();
            var before = Fingerprint(database);

            var error = Assert.Throws<StateSchemaException>(() => new State(null, null, database));

            Assert.Contains("indeks ix_notes_updated", error.Message, StringComparison.Ordinal);
            AssertUntouched(database, before, expectedVersion: 5);
            Assert.Equal(0L, ReadLong(database, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_notes_updated'"));
        });
    }

    [Fact(DisplayName = "Y-239 · Fiziksel bozulma şema eksikliğinden ayrı gerekçeyle reddedilir")]
    public void Y239_APhysicallyDamagedFileIsRefusedForADifferentReasonThanAnIncompleteOne()
    {
        WithRoot((root, database) =>
        {
            using (var _ = new State(null, null, database)) { }
            SqliteConnection.ClearAllPools();

            // The header, overwritten. Not a schema question at all.
            using (var file = new FileStream(database, FileMode.Open, FileAccess.Write))
                file.Write(Encoding.ASCII.GetBytes("BU BIR SQLITE DOSYASI DEGIL............"), 0, 38);

            var before = Fingerprint(database);
            var error = Assert.Throws<StateSchemaException>(() => new State(null, null, database));

            Assert.Contains("okunamıyor", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("taşıması gereken", error.Message, StringComparison.Ordinal);
            Assert.IsType<SqliteException>(error.InnerException);
            Assert.Equal(before, Fingerprint(database));

            // The read-only verdict names the same thing without needing the writing open.
            var diagnosis = State.Diagnose(database);
            Assert.Equal(StateFileKind.Unreadable, diagnosis.Kind);
            Assert.False(diagnosis.WritableOpenAllowed);
        });
    }

    [Fact(DisplayName = "Y-240 · Gelecek sürüm reddedilir ve dosya baytına dokunulmaz")]
    public void Y240_ANewerVersionIsRefusedWithoutTouchingTheFile()
    {
        WithRoot((root, database) =>
        {
            using (var _ = new State(null, null, database)) { }
            SqliteConnection.ClearAllPools();
            Execute(database, "PRAGMA user_version=9;");
            SqliteConnection.ClearAllPools();
            var before = Fingerprint(database);

            var error = Assert.Throws<StateSchemaException>(() => new State(null, null, database));

            Assert.Contains("9", error.Message, StringComparison.Ordinal);
            AssertUntouched(database, before, expectedVersion: 9);

            var diagnosis = State.Diagnose(database);
            Assert.Equal(StateFileKind.Future, diagnosis.Kind);
            Assert.Equal(9, diagnosis.FoundVersion);
        });
    }

    // ---------------------------------------------------------------- the diagnosis surface

    [Fact(DisplayName = "Y-241 · Yazan açılış reddedilse de teşhis alınabilir ve teşhis hiçbir şey yaratmaz")]
    public void Y241_DiagnosisIsAvailableEvenWhenTheWritingOpenIsRefused()
    {
        WithRoot((root, database) =>
        {
            Seed(database, 3);
            Execute(database, "DROP TABLE sweep_stamps;");
            SqliteConnection.ClearAllPools();

            Assert.Throws<StateSchemaException>(() => new State(null, null, database));
            SqliteConnection.ClearAllPools();

            var diagnosis = State.Diagnose(database);

            Assert.Equal(StateFileKind.Incomplete, diagnosis.Kind);
            Assert.Equal(3, diagnosis.FoundVersion);
            Assert.Equal(5, diagnosis.ExpectedVersion);
            Assert.False(diagnosis.WritableOpenAllowed);
            Assert.Contains(diagnosis.Missing, item => item.Kind == "tablo" && item.Name == "sweep_stamps");
            Assert.Contains("sweep_stamps", diagnosis.Summary, StringComparison.Ordinal);

            // A question creates nothing: no state root, no database, no schema.
            SqliteConnection.ClearAllPools();
            Assert.Equal(0L, ReadLong(database, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'sweep_stamps'"));
            Assert.Equal(3L, ReadLong(database, "PRAGMA user_version"));

            var absent = Path.Combine(root, "hic-olmayan", "state.db");
            var nothing = State.Diagnose(absent);
            Assert.Equal(StateFileKind.Missing, nothing.Kind);
            Assert.False(Directory.Exists(Path.GetDirectoryName(absent)!));
            Assert.False(File.Exists(absent));
        });
    }

    [Fact(DisplayName = "Y-242 · Boş dosya, sürümsüz dolu dosya ve geçerli tarihî dosya birbirine karışmaz")]
    public void Y242_EmptyUnversionedAndHistoricFilesAreNotConfusedWithOneAnother()
    {
        WithRoot((root, database) =>
        {
            Execute(database, "PRAGMA user_version=0;");
            SqliteConnection.ClearAllPools();
            Assert.Equal(StateFileKind.Empty, State.Diagnose(database).Kind);

            // A real pre-versioning file: the version 1 shape, never stamped.
            var unversioned = Path.Combine(root, "damgasiz.db");
            Seed(unversioned, 1);
            Execute(unversioned, "PRAGMA user_version=0;");
            SqliteConnection.ClearAllPools();
            var unversionedDiagnosis = State.Diagnose(unversioned);
            Assert.Equal(StateFileKind.Unversioned, unversionedDiagnosis.Kind);
            Assert.Empty(unversionedDiagnosis.Missing);

            // A file that claims nothing is owed nothing, so the whole ladder legitimately applies.
            using (var adopted = new State(null, null, unversioned))
                Assert.Equal(0, adopted.SchemaReport.FoundVersion);
            SqliteConnection.ClearAllPools();
            Assert.Equal(5L, ReadLong(unversioned, "PRAGMA user_version"));

            var historic = Path.Combine(root, "tarihi.db");
            Seed(historic, 3);
            var historicDiagnosis = State.Diagnose(historic);
            Assert.Equal(StateFileKind.Historic, historicDiagnosis.Kind);
            Assert.Equal(3, historicDiagnosis.FoundVersion);
            Assert.True(historicDiagnosis.WritableOpenAllowed);
        });
    }

    [Fact(DisplayName = "Y-243 · Salt okunur açılış eksikleri bildirir ama reddetmez ve DDL çalıştırmaz")]
    public void Y243_AReadOnlyOpenReportsWhatIsMissingInsteadOfRefusingIt()
    {
        WithRoot((root, database) =>
        {
            Seed(database, 3);
            Execute(database, "DROP TABLE sweep_stamps;");
            SqliteConnection.ClearAllPools();
            var objectsBefore = ReadLong(database, "SELECT COUNT(*) FROM sqlite_master");

            using (var reader = State.OpenReadOnly(database))
            {
                Assert.NotNull(reader);
                Assert.Equal(StateAccess.ReadOnly, reader.Access);
                Assert.Equal(3, reader.SchemaReport.FoundVersion);
                Assert.Equal(3, reader.SchemaReport.Version);
                Assert.Contains(reader.SchemaReport.Missing, item => item.Name == "sweep_stamps");
            }

            SqliteConnection.ClearAllPools();
            Assert.Equal(objectsBefore, ReadLong(database, "SELECT COUNT(*) FROM sqlite_master"));
            Assert.Equal(3L, ReadLong(database, "PRAGMA user_version"));
        });
    }

    [Fact(DisplayName = "Y-244 · Merdivenin tamamlayamayacağı dosya göç başlamadan reddedilir; teşhis bunu önceden söyler")]
    public void Y244_AFileTheLadderCannotCompleteIsRefusedBeforeTheMigrationStarts()
    {
        WithRoot((root, database) =>
        {
            // A calls table of somebody else's invention, with no version stamp. Step 1's
            // CREATE TABLE IF NOT EXISTS adds nothing to a table that is already there, and no
            // ALTER step supplies these columns, so the ladder cannot finish this file.
            Execute(database, "CREATE TABLE calls(ts TEXT);INSERT INTO calls VALUES('x');PRAGMA user_version=0;");
            SqliteConnection.ClearAllPools();

            var diagnosis = State.Diagnose(database);
            Assert.Equal(StateFileKind.Incomplete, diagnosis.Kind);
            Assert.False(diagnosis.WritableOpenAllowed);
            Assert.Contains(diagnosis.Missing, item => item.Name == "calls.backend");
            Assert.Contains("tamamlayamıyor", diagnosis.Summary, StringComparison.Ordinal);

            SqliteConnection.ClearAllPools();
            var before = Fingerprint(database);

            var error = Assert.Throws<StateSchemaException>(() => new State(null, null, database));

            Assert.Contains("tamamlayamıyor", error.Message, StringComparison.Ordinal);
            Assert.Contains("Göç başlatılmadı", error.Message, StringComparison.Ordinal);
            AssertUntouched(database, before, expectedVersion: 0);

            // Refused before the copy, so there is not even a backup to clean up.
            Assert.Empty(Backups(root));
            Assert.Equal("x", ReadText(database, "SELECT ts FROM calls"));
        });
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A database in the shape the build that stamped <paramref name="version"/> actually wrote.
    /// Cumulative: every version carries what the ones before it introduced.
    /// </summary>
    private static void Seed(string database, int version, bool extended = false)
    {
        var sql = new StringBuilder();

        // Version 1 (100e6ce): fourteen tables, no views, no named indexes, 15-column calls.
        var callsColumns = "ts TEXT, backend TEXT, component TEXT, tier TEXT, model TEXT, in_chars INTEGER, " +
                           "out_chars INTEGER, in_tok INTEGER, out_tok INTEGER, cache_r INTEGER, cache_w INTEGER, " +
                           "ms INTEGER, outcome TEXT, usage_source TEXT, purpose TEXT";
        if (version >= 4)
            callsColumns += ", operation_id TEXT, attempt_id TEXT, attempt_no INTEGER, uncached_in_tok INTEGER, " +
                            "usage_rank INTEGER NOT NULL DEFAULT 0 CHECK(usage_rank BETWEEN 0 AND 2), " +
                            "usage_semantics TEXT NOT NULL DEFAULT 'legacy-total-input-v0'";

        var servedColumns = "session_id TEXT, query_sig TEXT, note TEXT, ts TEXT";
        if (version >= 5 || extended)
            servedColumns += ", vault TEXT NOT NULL DEFAULT '', client TEXT NOT NULL DEFAULT '', " +
                             "entry TEXT NOT NULL DEFAULT '', content_hash TEXT NOT NULL DEFAULT '', " +
                             "status TEXT NOT NULL DEFAULT 'prepared', pid INTEGER NOT NULL DEFAULT 0, acked_ts TEXT";

        sql.Append("CREATE TABLE sessions(session_id TEXT PRIMARY KEY, transcript_path TEXT, last_turn_index INTEGER, last_flush_ts TEXT);");
        sql.Append("CREATE TABLE flush_log(ts TEXT, session_id TEXT, reason TEXT, outcome TEXT, turns INTEGER, chars INTEGER, backend TEXT);");
        sql.Append("CREATE TABLE retry_queue(session_id TEXT PRIMARY KEY, attempts INTEGER, next_at TEXT, last_error TEXT);");
        sql.Append("CREATE TABLE sweep_stamps(path TEXT PRIMARY KEY, mtime TEXT, size INTEGER, outcome TEXT);");
        sql.Append("CREATE TABLE coverage(ts TEXT, total INTEGER, covered INTEGER, uncovered_json TEXT);");
        sql.Append("CREATE TABLE daily_ingest(name TEXT PRIMARY KEY, digest TEXT, status TEXT, attempts INTEGER, reasons TEXT, ts TEXT);");
        sql.Append("CREATE TABLE compile_runs(ts TEXT, daily TEXT, status TEXT, created INTEGER, updated INTEGER, ms INTEGER);");
        sql.Append("CREATE TABLE quarantine(digest TEXT PRIMARY KEY, source TEXT, reason TEXT, ts TEXT, path TEXT);");
        sql.Append("CREATE TABLE health(ts TEXT, component TEXT, level TEXT, code TEXT, key TEXT, detail TEXT);");
        sql.Append("CREATE TABLE notified(class TEXT, key TEXT, ts TEXT);");
        sql.Append("CREATE TABLE locks(name TEXT PRIMARY KEY, machine TEXT, pid INTEGER, ts TEXT);");
        sql.Append("CREATE TABLE kota(ts TEXT, \"window\" TEXT, used_pct REAL, resets_at TEXT);");
        sql.Append($"CREATE TABLE calls({callsColumns});");
        sql.Append($"CREATE TABLE retrieve_served({servedColumns});");

        sql.Append($"INSERT INTO calls(ts, backend, component, tier, model, in_chars, out_chars, ms, outcome, usage_source, purpose) " +
                   $"VALUES('{Stamp}', 'claude', 'Master', 'Apex', 'opus-4', 120, 240, 900, 'ok', 'measured', 'eski satır');");
        sql.Append($"INSERT INTO sessions VALUES('oturum-1', 'E:/kasa/t.jsonl', 7, '{Stamp}');");
        sql.Append($"INSERT INTO flush_log VALUES('{Stamp}', 'oturum-1', 'stop', 'ok', 7, 84, 'claude');");
        sql.Append($"INSERT INTO sweep_stamps VALUES('daily/2026-01-01.md', '{Stamp}', 412, 'ok');");
        sql.Append($"INSERT INTO retrieve_served(session_id, query_sig, note, ts) VALUES('oturum-1', 'sig-1', 'kavram-001.md', '{Stamp}');");

        // Version 2 (v2.0.0): the five read-only views.
        if (version >= 2)
        {
            sql.Append("CREATE VIEW v_flush_log AS SELECT ts, session_id, reason, outcome, turns, chars, backend FROM flush_log;");
            sql.Append("CREATE VIEW v_coverage AS SELECT ts, total, covered, uncovered_json FROM coverage;");
            sql.Append("CREATE VIEW v_health AS SELECT ts, component, level, code, key, detail FROM health;");
            sql.Append("CREATE VIEW v_kota AS SELECT ts, \"window\", used_pct, resets_at FROM kota;");
            sql.Append("CREATE VIEW v_calls AS SELECT ts, backend, component, tier, model, purpose FROM calls;");
        }

        // Version 3 (v2.1.0): ingest_done and vault_meta.
        if (version >= 3)
        {
            sql.Append("CREATE TABLE ingest_done(source TEXT, digest TEXT, ts TEXT, PRIMARY KEY(source, digest));");
            sql.Append("CREATE TABLE vault_meta(key TEXT PRIMARY KEY, value TEXT);");
            sql.Append($"INSERT INTO vault_meta VALUES('installed_at', '{Stamp}');");
        }

        // Version 4 (ea3879e): v_call_usage and the unique attempt index.
        if (version >= 4)
        {
            sql.Append("CREATE VIEW v_call_usage AS SELECT backend, model, COUNT(*) AS attempt_count FROM calls GROUP BY backend, model;");
            sql.Append("CREATE UNIQUE INDEX ix_calls_attempt_id ON calls(attempt_id) WHERE attempt_id IS NOT NULL;");
        }

        // The version 5 shape, which revision 4c51a01 also shipped under stamp 4.
        if (version >= 5 || extended)
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

    private static void WithRoot(Action<string, string> test)
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            test(root, Path.Combine(root, "state.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// Content plus stamp: a refusal must leave both exactly as it found them. The <c>-wal</c> and
    /// <c>-shm</c> sidecars are hashed too, because on a WAL database a write lands there and not
    /// in <c>state.db</c> — hashing the main file alone would call an untouched file a file that
    /// had just been written to.
    /// </summary>
    private static string Fingerprint(string database)
    {
        SqliteConnection.ClearAllPools();
        var parts = new List<string>();
        foreach (var path in new[] { database, database + "-wal", database + "-shm" })
        {
            if (!File.Exists(path))
            {
                parts.Add($"{Path.GetExtension(path)}:yok");
                continue;
            }

            using var stream = File.OpenRead(path);
            parts.Add(Convert.ToHexString(SHA256.HashData(stream)));
        }

        return string.Join("·", parts);
    }

    private static void AssertUntouched(string database, string before, long expectedVersion)
    {
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, Fingerprint(database));
        Assert.Equal(expectedVersion, ReadLong(database, "PRAGMA user_version"));
    }

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
        Convert.ToInt64(Read(database, sql) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);

    private static string ReadText(string database, string sql) => Read(database, sql)?.ToString() ?? string.Empty;

    private static object? Read(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
