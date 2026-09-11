using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Gates;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class DurumDeposuScars
{
    [Fact(DisplayName = "Y-058 · Aynı message id üç kez geçse de kullanım bir kez sayılır")]
    public void Y058_UsageIsDeduplicatedByMessageId()
    {
        var records = Enumerable.Range(0, 3).Select(_ => new UsageRecord("msg-58", "master", ScarFixture.Now, 100, 10, 1_000)).ToArray();
        var summary = new State().AggregateUsage(records);
        Assert.Equal(100, summary.InputTokens);
        Assert.Equal(10, summary.OutputTokens);
        Assert.Equal(1_000, summary.CacheReadTokens);
    }

    [Fact(DisplayName = "Y-059 · Sahiplik sınıfları tekilleştirilmiş genel toplamla eşleşir")]
    public void Y059_OwnerClassesSumToDeduplicatedTotal()
    {
        var records = new[]
        {
            new UsageRecord("master", "Master", ScarFixture.Now, 1, 1, 100),
            new UsageRecord("agent", "alt-ajan", ScarFixture.Now, 1, 1, 50_800_000),
            new UsageRecord("compile", "derleme", ScarFixture.Now, 1, 1, 200)
        };
        var summary = new State().AggregateUsage(records);
        Assert.Equal(50_800_000, summary.CacheReadByOwner["alt-ajan"]);
        Assert.Equal(summary.CacheReadTokens, summary.CacheReadByOwner.Values.Sum());
    }

    [Fact(DisplayName = "Y-060 · Gece yarısını aşan kullanım yanıt zamanına göre iki güne bölünür")]
    public void Y060_UsageIsAssignedByResponseTimestamp()
    {
        var before = new UsageRecord("before", "Master", new DateTimeOffset(2026, 9, 8, 23, 59, 0, TimeSpan.FromHours(3)), 10, 1, 0);
        var after = new UsageRecord("after", "Master", new DateTimeOffset(2026, 9, 9, 0, 1, 0, TimeSpan.FromHours(3)), 20, 2, 0);
        var firstDay = new State().AggregateUsage([before]);
        var secondDay = new State().AggregateUsage([after]);
        Assert.Equal(10, firstDay.InputTokens);
        Assert.Equal(20, secondDay.InputTokens);
    }

    [Fact(DisplayName = "Y-061 · Bir yıllık durum deposu elli MB altında ve saklama sürelerine uyar")]
    public void Y061_StateRetentionBoundsOneYearDatabase()
    {
        var stats = new State().SweepRetention(ScarFixture.Now);
        Assert.True(stats.Bytes < 50L * 1024 * 1024);
        Assert.InRange(stats.Calls, 0, 90 * 10_000);
        Assert.InRange(stats.QuotaRows, 0, 30 * 10_000);
    }

    [Fact(DisplayName = "Y-062 · Sekiz günlük retrieve kayıtları tek sweep ile budanır")]
    public void Y062_RetrieveRowsOlderThanSevenDaysArePruned()
    {
        var stats = new State().SweepRetention(ScarFixture.Now);
        Assert.Equal(0, stats.RetrieveRows);
    }

    [Fact(DisplayName = "Y-063 · Sekiz eşzamanlı health yazımı kaybolmaz ve 25 saatlik uyarı eskidir")]
    public void Y063_HealthWritesAreLockedAndWarningsAge()
    {
        var items = Enumerable.Range(0, 8).Select(i => new HealthItem("test", HealthLevel.Warning, $"w-{i}", i.ToString(), "uyarı", i == 0)).ToArray();
        var written = new State().WriteHealthConcurrently(items);
        Assert.Equal(8, written.Count);
        Assert.True(written.Single(x => x.Code == "w-0").Stale);
    }

    [Fact(DisplayName = "Y-064 · Sekiz iş parçacıklı atomik yazım veri ve dosya kaybetmez")]
    public void Y064_AtomicWriteUsesUniqueTemporaryNamesAndRetries()
    {
        var final = new State().AtomicWrite("state.json", "{\"version\":8}", writers: 8);
        Assert.Equal("{\"version\":8}", final);
        Assert.DoesNotContain(".tmp", final, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Y-114 · İçe aktarım tamamlanmaları state.db'de kalıcı; durum kapanıp yeniden açılsa da ikinci koşum aynı dosyayı atlar")]
    public void Y114_IngestCompletionPersistsAcrossStateReopen()
    {
        // Program.cs'teki eski `new Ingest()` durumu süreç ömürlü bellekte tutuyordu: her yeni
        // `oom ingest` çalıştırılışı aynı en eski dosyaları yeniden "içe aktarım" sayıyor, arşiv
        // hiç ilerlemiyordu. Burada iki AYRI `State`/`Ingest` çifti aynı state.db dosyasını paylaşır.
        var root = ScarFixture.TempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            var transcript = Path.Combine(root, "y114.jsonl");
            File.WriteAllText(transcript,
                "{\"sessionId\":\"y114\",\"type\":\"user\",\"timestamp\":\"2026-09-09T08:00:00+03:00\",\"message\":{\"content\":\"merhaba\"}}\n" +
                "{\"sessionId\":\"y114\",\"type\":\"assistant\",\"timestamp\":\"2026-09-09T08:00:01+03:00\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"merhaba Master Mind\"}]}}",
                new UTF8Encoding(false));

            IngestOutcome RunOnce()
            {
                using var state = new State(null, null, database);
                var ingest = new Ingest(state: state, flushSession: (session, path) => new FlushResult(FlushOutcome.Ok, 0, null, null));
                return ingest.RunWithOutcome("claude", [transcript]);
            }

            var first = RunOnce();
            var second = RunOnce();

            Assert.Single(first.Sessions);
            Assert.Equal(0, first.Skipped);
            Assert.Empty(second.Sessions);
            Assert.Equal(1, second.Skipped);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// Two opens of one file on disk, exactly as two hook processes see it: the first finds a v2
    /// database and migrates it, the second finds v4 and does nothing. Same process, two
    /// independent opens — the whole state lives in the file, so the open path is the production
    /// path; what this does NOT prove is anything about in-memory state surviving a process exit.
    /// </summary>
    [Fact(DisplayName = "Y-129 · Eski şemalı durum deposu kopyalanıp doğrulanarak göç eder, satırları kalır ve ikinci açılış hiçbir şey yapmaz")]
    public void Y129_OlderDatabaseIsMigratedByCopyAndVerifyAndKeepsItsRows()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            Seed(database, version: 2);

            using (var migrated = new State(null, null, database))
            {
                var report = migrated.SchemaReport;
                Assert.Equal(2, report.FoundVersion);
                Assert.Equal(5, report.Version);
                Assert.Equal(
                    ["2→3: önbelleksiz girdi sayacı (uncached_in_tok)", "2→4: bölünmüş sayaç anlambilimi (usage_rank, usage_semantics)", "2→5: getirim dizini ve served defteri kapsamı (notes, notes_fts, oom_index_meta, retrieve_served kapsam sütunları)"],
                    report.Applied);

                // Nothing was dropped and recreated: the pre-migration rows are still the same rows.
                Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM calls"));
                Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM flush_log"));
                Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM calls WHERE purpose = 'eski satır'"));
                Assert.Equal(5, migrated.Scalar("PRAGMA user_version"));

                // A proven way back exists before a single ALTER runs, and it is kept, not cleaned up.
                Assert.NotNull(report.BackupPath);
                Assert.True(File.Exists(report.BackupPath));
                Assert.Equal(2, Read(report.BackupPath!, "PRAGMA user_version"));
                Assert.Equal(1, Read(report.BackupPath!, "SELECT COUNT(*) FROM calls"));
                Assert.Equal(0, Read(report.BackupPath!, "SELECT COUNT(*) FROM pragma_table_info('calls') WHERE name = 'usage_rank'"));
            }

            SqliteConnection.ClearAllPools();
            using var reopened = new State(null, null, database);
            Assert.Equal(5, reopened.SchemaReport.FoundVersion);
            Assert.Empty(reopened.SchemaReport.Applied);
            Assert.Null(reopened.SchemaReport.BackupPath);
            Assert.Equal(1, reopened.Scalar("SELECT COUNT(*) FROM calls"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    [Fact(DisplayName = "Y-130 · Daha yeni şemalı durum deposu sessizce eski sürüme etiketlenmez, açılış reddedilir")]
    public void Y130_NewerSchemaIsRefusedInsteadOfBeingRelabelled()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            using (var provisioned = new State(null, null, database))
                Assert.Equal(5, provisioned.Scalar("PRAGMA user_version"));
            SqliteConnection.ClearAllPools();

            Execute(database, "PRAGMA user_version=99;");
            SqliteConnection.ClearAllPools();

            var error = Assert.Throws<StateSchemaException>(() => new State(null, null, database));
            Assert.Contains("99", error.Message, StringComparison.Ordinal);

            // Refused means untouched: the file still says 99, so a newer build still recognises it.
            SqliteConnection.ClearAllPools();
            Assert.Equal(99, Read(database, "PRAGMA user_version"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    [Fact(DisplayName = "Y-131 · Vault kimliği tek işlevdir; kurulum kendi kopyasını taşımaz, ikinci yazım bildirilerek yakalanır")]
    public void Y131_VaultIdentityIsOneFunctionAndASecondSpellingIsReported()
    {
        const string vault = @"E:\bir kasa";
        Assert.Equal(VaultIdentity.StateRoot(vault), VaultIdentity.StateRoot(vault + Path.DirectorySeparatorChar));
        Assert.Equal(Path.Combine(VaultIdentity.StateRoot(vault), "state.db"), VaultIdentity.DatabasePath(vault));
        Assert.Equal(VaultIdentity.StateRootsDirectory(), Path.GetDirectoryName(VaultIdentity.StateRoot(vault)));

        // A second spelling still names a second directory — that is not repointed silently, it is
        // detectable, which is what lets doctor say "two databases are reachable for one vault".
        Assert.True(VaultIdentity.IsCanonical(vault));
        Assert.False(VaultIdentity.IsCanonical("E:/bir kasa"));
        Assert.NotEqual(VaultIdentity.Hash(vault), VaultIdentity.Hash("E:/bir kasa"));

        // The installer used to keep its own quieter copy of the answer. One function, one caller path.
        var sources = Directory.EnumerateFiles(Path.Combine(ScarFixture.RepositoryRoot(), "src", "Oom"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .ToArray();
        Assert.DoesNotContain("LaneCVaultPaths", File.ReadAllText(sources.Single(path => path.EndsWith("Install.cs", StringComparison.Ordinal))), StringComparison.Ordinal);
        Assert.Equal(1, sources.SelectMany(File.ReadLines)
            .Count(line => line.Contains("SHA256.HashData", StringComparison.Ordinal) && line.Contains("TrimEnd(Path.DirectorySeparatorChar)", StringComparison.Ordinal)));
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
            Assert.Empty(reader.SchemaReport.Applied);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// Y-161 · The point of the whole lane, made concrete: a unit test that calls
    /// <c>State.OpenReadOnly</c> or <c>VaultIdentity.EnsureDatabase</c> directly proves nothing
    /// about whether <c>Program.cs</c>'s command dispatcher actually reaches those helpers for a
    /// given command — only running the SHIPPED <c>oom.exe</c> and watching what it does to disk
    /// proves that. A read-only command (`context`, `retrieve`) must leave no state root behind;
    /// a write command (`compile`, even with `--dry-run`, since `--dry-run` only suppresses the
    /// model call and still opens the write handle) must create one.
    /// </summary>
    [Fact(DisplayName = "Y-161 · Yayımlanan exe: salt okunur komut durum kökü yaratmaz, yazan komut yaratır")]
    public void Y161_ShippedExecutableCreatesNoStateRootOnReadOnlyCommandsButDoesOnWrite()
    {
        var root = ScarFixture.TempDirectory();
        var vault = Path.Combine(root, "kasa");
        Directory.CreateDirectory(vault);
        var stateRoot = VaultIdentity.StateRoot(vault);
        try
        {
            Assert.False(Directory.Exists(stateRoot), $"önceki bir koşumdan kalma durum kökü temizlenmemiş: {stateRoot}");

            var context = GateFixture.Run("context", "--vault", vault);
            Assert.True(context.ExitCode == 0, $"'context --vault {vault}' çıkış kodu {context.ExitCode} döndürdü: {context.StandardError}");
            Assert.False(Directory.Exists(stateRoot), $"salt okunur 'context' komutu durum kökünü yarattı: {stateRoot}");

            // No-hit sorgunun kendi çıkış kodu var; burada tek ilgilenilen şey diskte iz bırakmaması.
            var retrieve = GateFixture.Run("retrieve", "--query", "tokenizasyon", "--vault", vault);
            Assert.False(Directory.Exists(stateRoot),
                $"salt okunur 'retrieve' komutu durum kökünü yarattı: {stateRoot} (stderr: {retrieve.StandardError})");

            var compile = GateFixture.Run("compile", "--dry-run", "--vault", vault);
            Assert.True(compile.ExitCode == 0, $"'compile --dry-run --vault {vault}' çıkış kodu {compile.ExitCode} döndürdü: {compile.StandardError}");
            Assert.True(Directory.Exists(stateRoot), $"yazan 'compile --dry-run' komutu durum kökünü yaratmadı: {stateRoot}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
            ScarFixture.Remove(stateRoot);
        }
    }

    /// <summary>
    /// Y-162 · <see cref="Doctor"/>'s stray-root scan is off by default precisely so the rest of
    /// this suite does not start opening the owner's real <c>%LOCALAPPDATA%\oom</c> databases just
    /// by constructing a <c>Doctor</c> (see the constructor comment in Doctor.cs). That default is
    /// exactly why a unit test against <c>Doctor</c> directly cannot prove the production path
    /// turns the scan on — this test drives the shipped `oom doctor` command instead, which is the
    /// only place that wires the real scan into the health output. It must still be read-only.
    /// </summary>
    [Fact(DisplayName = "Y-162 · Yayımlanan exe: doctor durum kökü yaratmaz ve başıboş kök taramasını kullanıcıya bildirir")]
    public void Y162_ShippedExecutableDoctorIsReadOnlyAndCarriesStateRootScan()
    {
        var root = ScarFixture.TempDirectory();
        var vault = Path.Combine(root, "kasa");
        Directory.CreateDirectory(vault);
        var stateRoot = VaultIdentity.StateRoot(vault);
        try
        {
            // Tarama fikstüre yönlendirildi: yoksa yayımlanan doctor sahibin gerçek
            // %LOCALAPPDATA%\oom altındaki HER veritabanını açar -- biri bozuk ve onarım bekliyor.
            var scanRoot = Path.Combine(root, "profil");
            Directory.CreateDirectory(Path.Combine(scanRoot, "oom"));
            var doctor = GateFixture.RunScoped(scanRoot, "doctor", "--json", "--vault", vault);
            Assert.True(doctor.ExitCode == 0, $"'doctor --json --vault {vault}' çıkış kodu {doctor.ExitCode} döndürdü: {doctor.StandardError}");
            Assert.False(Directory.Exists(stateRoot), $"'doctor' (--fix olmadan) durum kökünü yarattı: {stateRoot}");

            using var document = JsonDocument.Parse(doctor.StandardOutput);
            var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
            var allCodes = items.Select(item => item.GetProperty("Code").GetString()).ToArray();

            Assert.True(allCodes.Contains("state-roots"),
                $"doctor çıktısında 'state-roots' kodlu sağlık kalemi yok; bulunan kodlar: {string.Join(", ", allCodes)}");

            var stateComponentCodes = items
                .Where(item => item.GetProperty("Component").GetString() == "state")
                .Select(item => item.GetProperty("Code").GetString())
                .ToArray();
            Assert.True(stateComponentCodes.Contains("state-yok"),
                $"doctor çıktısında eksik veritabanını dürüstçe bildiren 'state-yok' kodu yok; " +
                $"'state' bileşeninde bulunan kodlar: {string.Join(", ", stateComponentCodes)}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
            ScarFixture.Remove(stateRoot);
        }
    }

    /// <summary>
    /// Y-163 · <c>%LOCALAPPDATA%\oom</c> does not hold only state roots — it also holds <c>backup</c>
    /// and <c>claude-config</c>, which were being walked and reported as stray roots simply because
    /// they sit next to the real ones. The scan must recognise a state root by its NAME (exactly
    /// 16 lowercase hex characters, the shape <see cref="VaultIdentity.Hash(string)"/> produces),
    /// not merely by its position under the oom folder.
    /// </summary>
    [Fact(DisplayName = "Y-163 · Tarama yalnız durum köklerine bakar; backup ve claude-config başıboş kök diye bildirilmez")]
    public void Y163_ScanOnlyConsidersStateRootHashedDirectories()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var scanLocal = Path.Combine(root, "yerel");
            var roots = Path.Combine(scanLocal, "oom");
            var hashDirectory = Path.Combine(roots, VaultIdentity.Hash(@"E:\y163 kasa"));
            var backup = Path.Combine(roots, "backup");
            var claudeConfig = Path.Combine(roots, "claude-config");
            Directory.CreateDirectory(hashDirectory);
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(claudeConfig);
            File.WriteAllText(Path.Combine(backup, "not-a-state-root.txt"), "yedek dosyası — durum kökü değil");
            File.WriteAllText(Path.Combine(claudeConfig, "config.json"), "{}");

            var reports = new Doctor().InspectStateRoots(scanLocal);
            Assert.True(reports.Count == 1,
                $"tarama tam olarak bir durum kökü bulmalıydı, {reports.Count} buldu: {string.Join(", ", reports.Select(r => r.Path))}");
            Assert.Equal(Path.GetFileName(hashDirectory), reports[0].Hash);
            Assert.DoesNotContain(reports, r => Path.GetFileName(r.Path) is "backup" or "claude-config");

            var items = new Doctor().StateRootItems(scanLocal);
            Assert.Equal("state-roots", items[0].Code);
            Assert.DoesNotContain(items, item => item.Key is "backup" or "claude-config");

            // The scan only reports — it must never touch what it walked past.
            Assert.True(Directory.Exists(backup), $"tarama 'backup' dizinini sildi: {backup}");
            Assert.True(Directory.Exists(claudeConfig), $"tarama 'claude-config' dizinini sildi: {claudeConfig}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// Y-170 · There used to be a third DDL site in this codebase: <c>Retrieve.Build()</c> opened
    /// its own <c>SqliteConnection</c> to the very same <c>state.db</c> that <c>StateStore</c>
    /// owns, created <c>oom_index_meta</c> itself, and then <c>DROP TABLE</c>'d and re-<c>CREATE</c>d
    /// <c>notes</c> and <c>notes_fts</c> on every rebuild — DDL against a file whose schema
    /// belongs to somebody else. Now <c>StateStore</c> owns the retrieval index: opening the
    /// state store alone, with no retrieval code involved anywhere in this test, is what produces
    /// <c>notes</c>, <c>notes_fts</c>, <c>oom_index_meta</c>, <c>ix_notes_updated</c> and the
    /// served ledger's scope columns — on a brand-new file and on a file left by an older build
    /// alike, without moving the schema version off 4 and without losing a row.
    /// </summary>
    // yazan: claude · opus-5
    [Fact(DisplayName = "Y-170 · Arama indeksi şemasının tek sahibi durum deposudur")]
    public void Y170_StateStoreIsTheSoleOwnerOfTheRetrievalIndexSchema()
    {
        var freshRoot = ScarFixture.TempDirectory();
        var migratedRoot = ScarFixture.TempDirectory();
        try
        {
            // (a) A brand-new database: opening the STATE store alone must produce the whole
            // retrieval index — no Retrieve.Build() call anywhere near this block.
            var database = Path.Combine(freshRoot, "state.db");
            using (var state = new State(null, null, database))
            {
                Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'notes'"));
                Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'notes_fts'"));
                Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'oom_index_meta'"));
                Assert.True(state.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_notes_updated' AND tbl_name = 'notes'") == 1,
                    "notes üzerindeki ix_notes_updated indeksi yeni veritabanında yok");
                Assert.True(state.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_retrieve_served_scope'") == 1,
                    "ix_retrieve_served_scope indeksi yeni veritabanında yok");

                foreach (var column in new[] { "session_id", "query_sig", "note", "ts", "vault", "client", "entry", "content_hash", "status", "pid", "acked_ts" })
                    Assert.True(state.Scalar($"SELECT COUNT(*) FROM pragma_table_info('retrieve_served') WHERE name = '{column}'") == 1,
                        $"retrieve_served sütunu eksik: {column}");

                // İndeks şeması var olan merdiven basamağına indi, dosyayı sessizce yeniden etiketlemedi.
                Assert.Equal(5, state.Scalar("PRAGMA user_version"));
            }

            // (b) A file left by an older build gains the same shape on its next open, without
            // losing a row and without a version bump — agrees with what Y-129 already pins.
            var migratedDatabase = Path.Combine(migratedRoot, "state.db");
            Seed(migratedDatabase, version: 2);

            using (var migrated = new State(null, null, migratedDatabase))
            {
                Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'notes'"));
                Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'notes_fts'"));
                Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'oom_index_meta'"));
                Assert.True(migrated.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_notes_updated' AND tbl_name = 'notes'") == 1,
                    "notes üzerindeki ix_notes_updated indeksi eski şemalı dosyada oluşmadı");

                Assert.True(migrated.Scalar("SELECT COUNT(*) FROM pragma_table_info('retrieve_served') WHERE name = 'status'") == 1,
                    "retrieve_served sütunu eksik: status");
                Assert.True(migrated.Scalar("SELECT COUNT(*) FROM pragma_table_info('retrieve_served') WHERE name = 'content_hash'") == 1,
                    "retrieve_served sütunu eksik: content_hash");

                // Göçten önceki satırlar hâlâ aynı satırlar: hiçbir DROP TABLE çalışmadı.
                Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM calls"));
                Assert.Equal(1, migrated.Scalar("SELECT COUNT(*) FROM flush_log"));

                Assert.Equal(5, migrated.SchemaReport.Version);
                Assert.Equal(2, migrated.SchemaReport.FoundVersion);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(freshRoot);
            ScarFixture.Remove(migratedRoot);
        }
    }

    /// <summary>A database shaped the way build 2 left it: the split counters do not exist yet.</summary>
    private static void Seed(string database, int version)
    {
        Execute(database,
            // Sürüm 2 biçimi, tam haliyle: damga artık inanıldığı için eksik tablo bırakılamaz.
            "CREATE TABLE retry_queue(session_id TEXT PRIMARY KEY, attempts INTEGER, next_at TEXT, last_error TEXT);" +
            "CREATE TABLE sweep_stamps(path TEXT PRIMARY KEY, mtime TEXT, size INTEGER, outcome TEXT);" +
            "CREATE TABLE coverage(ts TEXT, total INTEGER, covered INTEGER, uncovered_json TEXT);" +
            "CREATE TABLE daily_ingest(name TEXT PRIMARY KEY, digest TEXT, status TEXT, attempts INTEGER, reasons TEXT, ts TEXT);" +
            "CREATE TABLE compile_runs(ts TEXT, daily TEXT, status TEXT, created INTEGER, updated INTEGER, ms INTEGER);" +
            "CREATE TABLE quarantine(digest TEXT PRIMARY KEY, source TEXT, reason TEXT, ts TEXT, path TEXT);" +
            "CREATE TABLE health(ts TEXT, component TEXT, level TEXT, code TEXT, key TEXT, detail TEXT);" +
            "CREATE TABLE notified(class TEXT, key TEXT, ts TEXT);" +
            "CREATE TABLE locks(name TEXT PRIMARY KEY, machine TEXT, pid INTEGER, ts TEXT);" +
            "CREATE TABLE kota(ts TEXT, \"window\" TEXT, used_pct REAL, resets_at TEXT);" +
            "CREATE TABLE sessions(session_id TEXT PRIMARY KEY, transcript_path TEXT, last_turn_index INTEGER, last_flush_ts TEXT);" +
            "CREATE TABLE retrieve_served(session_id TEXT, query_sig TEXT, note TEXT, ts TEXT);" +
            "CREATE VIEW v_flush_log AS SELECT ts, session_id, reason, outcome, turns, chars, backend FROM flush_log;" +
            "CREATE VIEW v_coverage AS SELECT ts, total, covered, uncovered_json FROM coverage;" +
            "CREATE VIEW v_health AS SELECT ts, component, level, code, key, detail FROM health;" +
            "CREATE VIEW v_kota AS SELECT ts, \"window\", used_pct, resets_at FROM kota;" +
            "CREATE VIEW v_calls AS SELECT ts, backend, component, tier, model, purpose FROM calls;" +
            "CREATE TABLE calls(ts TEXT, backend TEXT, component TEXT, tier TEXT, model TEXT, in_chars INTEGER, out_chars INTEGER, in_tok INTEGER, out_tok INTEGER, cache_r INTEGER, cache_w INTEGER, ms INTEGER, outcome TEXT, usage_source TEXT, purpose TEXT, operation_id TEXT, attempt_id TEXT, attempt_no INTEGER);" +
            "INSERT INTO calls(ts, backend, purpose) VALUES('2026-01-01T00:00:00+00:00', 'claude', 'eski satır');" +
            "CREATE TABLE flush_log(ts TEXT, session_id TEXT, reason TEXT, outcome TEXT, turns INTEGER, chars INTEGER, backend TEXT);" +
            "INSERT INTO flush_log(ts, session_id, outcome) VALUES('2026-01-01T00:00:00+00:00', 'eski', 'ok');" +
            $"PRAGMA user_version={version};");
        SqliteConnection.ClearAllPools();
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Read(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }
}
