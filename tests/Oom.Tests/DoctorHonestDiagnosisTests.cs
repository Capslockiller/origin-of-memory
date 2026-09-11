// yazan: claude · opus-5
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests;

/// <summary>
/// What the state-root scan tells the owner, and what it costs them to be told it.
///
/// Two separate failures are measured here. The first is meaning: the scan had one shape of
/// answer for every unhappy root, so "I could not prove this is empty", "this has records but no
/// owner", "this file will not open" and "this file's schema does not match the version it claims"
/// all arrived looking alike — and a row total lifted out of twenty database tables was printed
/// where the owner reads a count of their own memories. The second is cost: the scan was
/// documented as read-only, and it was not. Every database this product writes carries a
/// persistent WAL stamp in its header, and connecting read-only to one of those makes SQLite
/// create <c>state.db-shm</c> and <c>state.db-wal</c> beside the owner's file — measured on this
/// machine, 32 768 bytes and 0 bytes, left behind because a read-only connection cannot remove
/// them again. A health check that writes into the thing it is checking has to stop doing that
/// before anything it says about the thing can be trusted.
/// </summary>
public sealed class DoctorHonestDiagnosisTests
{
    private const string Vault = @"E:\OdenaOS-fikstur-kasa";

    /// <summary>
    /// Y-270 · The one verdict that used to invite a deletion. A root whose emptiness could not be
    /// proven gets the owner's own instructions, word for word, and no repair suggestion at all:
    /// there is no fix known to work on this class of root, and offering one that has not been
    /// shown to work is how the previous two wrong answers were arrived at.
    /// </summary>
    [Fact(DisplayName = "Y-270 · Belirsiz kök, sahibe kararlaştırılan ürün metnini birebir söyler")]
    public void Y270_TheIndeterminateVerdictCarriesTheAgreedProductSentence()
    {
        WithProfile(profile =>
        {
            var root = Path.Combine(profile.Roots, RootName(1));
            // An FTS4 index: valid SQLite, passes integrity_check, and nothing in the scan knows
            // how to read it. Every ordinary table really is empty, which is the exact shape that
            // otherwise produces a confident, wrong "boş".
            Provision(root, "CREATE VIRTUAL TABLE eski_arama USING fts4(govde)");

            var report = Assert.Single(new Doctor().InspectStateRoots(profile.LocalAppData));
            Assert.Equal("belirsiz", report.Status);

            var item = Assert.Single(new Doctor().StateRootItems(profile.LocalAppData),
                candidate => candidate.Code == "state-root-indeterminate");
            Assert.Contains(
                "Bu durum kökünün boş olduğu doğrulanamadı. Dosyaları koruyun; temizleme kararı vermeden " +
                "önce kökün bağlı olduğu kasayı ve içeriğini inceleyin.",
                item.Detail, StringComparison.Ordinal);
            Assert.Contains(root, item.Detail, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Y-271 · "Satır" is a database word. The owner reads a number in a health report as a count
    /// of what they own, and one note leaves rows in several tables while several tables hold no
    /// notes at all — so the total may never be printed as a quantity of memory. It is printed as
    /// what it is, with what it is not said out loud beside it.
    /// </summary>
    [Fact(DisplayName = "Y-271 · Satır toplamı hafıza sayısı gibi sunulmaz")]
    public void Y271_TheRowTotalIsNeverPresentedAsACountOfMemories()
    {
        WithProfile(profile =>
        {
            var root = Path.Combine(profile.Roots, RootName(1));
            Provision(root, "INSERT INTO quarantine(digest, source, reason, ts, path)" +
                            " VALUES ('a', 'claude', 'bozuk', '2026-09-01T00:00:00+03:00', 'x.md')");

            var report = Assert.Single(new Doctor().InspectStateRoots(profile.LocalAppData));
            Assert.Equal("sahipsiz", report.Status);
            Assert.True(report.Rows > 0, "fikstür geçersiz: sahipsiz kök satır taşımıyor");

            var items = new Doctor().StateRootItems(profile.LocalAppData);
            var item = Assert.Single(items, candidate => candidate.Code == "unattributed-state-root");

            // The number may appear — it is a real measurement and hiding it helps nobody — but it
            // may never appear without the sentence that says what it is not.
            Assert.Contains(Doctor.RowsAreNotMemories, item.Detail, StringComparison.Ordinal);
            Assert.Contains("teknik tablo satırı toplamı", item.Detail, StringComparison.Ordinal);

            // And no item anywhere in the report may render a count as a quantity of the owner's
            // own material: "7 hafıza", "7 not", "7 anı", "7 kayıt".
            foreach (var candidate in items)
                foreach (var noun in new[] { "hafıza", "not", "anı", "kayıt" })
                    Assert.DoesNotContain($"{report.Rows} {noun}", candidate.Detail, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Y-272 · Four different things went wrong, so the owner is told four different things. The
    /// complaint this closes is a report where every unhappy root landed in one bucket and the
    /// owner had to guess which of them was dangerous to delete and which was merely odd.
    /// </summary>
    [Fact(DisplayName = "Y-272 · Sahipsiz, belirsiz, okunamıyor ve şema uyuşmazlığı ayrı gerekçeler taşır")]
    public void Y272_EachUnhappyVerdictCarriesItsOwnReason()
    {
        WithProfile(profile =>
        {
            var unattributed = Path.Combine(profile.Roots, RootName(1));
            Provision(unattributed, "INSERT INTO quarantine(digest, source, reason, ts, path)" +
                                    " VALUES ('a', 'claude', 'bozuk', '2026-09-01T00:00:00+03:00', 'x.md')");

            var indeterminate = Path.Combine(profile.Roots, RootName(2));
            Provision(indeterminate, "CREATE VIRTUAL TABLE eski_arama USING fts4(govde)");

            var unreadable = Path.Combine(profile.Roots, RootName(3));
            Provision(unreadable, "INSERT INTO daily_ingest(name, status, ts)" +
                                  " VALUES ('x.md', 'ingested', '2026-09-01T00:00:00+03:00')");
            Shred(unreadable, from: 2048);

            // Attributed, owned, live — and still carrying a file this build will not write to.
            var mismatched = Path.Combine(profile.Roots, VaultIdentity.Hash(Vault));
            Provision(mismatched, "DROP TABLE sweep_stamps");
            Describe(mismatched, Vault);

            var items = new Doctor().StateRootItems(profile.LocalAppData)
                .Where(item => item.Code != "state-roots").ToArray();

            var codes = new[]
            {
                "unattributed-state-root", "state-root-indeterminate",
                "state-root-unreadable", "state-root-schema-mismatch"
            };
            foreach (var code in codes)
                Assert.Single(items, item => item.Code == code);

            // Four codes, four keys, four distinct sentences: nothing collapsed into one "sorunlu".
            Assert.Equal(codes.Length, items.Length);
            Assert.Equal(items.Length, items.Select(item => item.Detail).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(items.Length, items.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count());
        });
    }

    /// <summary>
    /// Y-273 · Schema mismatch is a reason of its own, and it is not the ownership verdict. The
    /// first root here is attributed, named correctly for the vault it serves and perfectly well
    /// "kullanımda"; what is wrong with it is inside the file, and it is named structure by
    /// structure rather than reported as emptiness, residue or an unreadable file.
    ///
    /// The second root is why the two verdicts have to stay apart instead of merely being printed
    /// apart. It is empty <em>and</em> schema-mismatched, and a schema reason that also pronounced
    /// on contents put two contradicting sentences about one root in front of the owner in the same
    /// report — measured against the published executable, not imagined.
    /// </summary>
    [Fact(DisplayName = "Y-273 · Şema uyuşmazlığı kendi gerekçesiyle, eksik yapı adıyla raporlanır")]
    public void Y273_SchemaMismatchIsItsOwnReasonNamingWhatIsMissing()
    {
        WithProfile(profile =>
        {
            var attributed = Path.Combine(profile.Roots, VaultIdentity.Hash(Vault));
            Provision(attributed, "INSERT INTO quarantine(digest, source, reason, ts, path)" +
                                  " VALUES ('a', 'claude', 'bozuk', '2026-09-01T00:00:00+03:00', 'x.md');" +
                                  " DROP TABLE sweep_stamps");
            Describe(attributed, Vault);

            var empty = Path.Combine(profile.Roots, RootName(1));
            Provision(empty, "DROP TABLE sweep_stamps");

            var reports = new Doctor().InspectStateRoots(profile.LocalAppData)
                .ToDictionary(report => report.Path, StringComparer.Ordinal);
            Assert.Equal("kullanımda", reports[attributed].Status);
            Assert.Equal(StateFileKind.Incomplete, reports[attributed].SchemaKind);
            Assert.Equal("artık", reports[empty].Status);
            Assert.Equal(StateFileKind.Incomplete, reports[empty].SchemaKind);

            var items = new Doctor().StateRootItems(profile.LocalAppData);
            foreach (var root in new[] { attributed, empty })
            {
                var item = Assert.Single(items, candidate =>
                    candidate.Code == "state-root-schema-mismatch" && candidate.Key == Path.GetFileName(root));
                Assert.Contains("sweep_stamps", item.Detail, StringComparison.Ordinal);
                Assert.Contains(root, item.Detail, StringComparison.Ordinal);

                // The schema reason says nothing about contents in either direction — saying "this
                // root is not empty" beside a stray verdict that says it is, is how one report
                // contradicts itself.
                foreach (var claim in new[] { "boş değil", "artık değildir", "boş sayılmaz" })
                    Assert.DoesNotContain(claim, item.Detail, StringComparison.Ordinal);
            }

            // The attributed root is none of the other verdicts, and not a deletion candidate.
            Assert.DoesNotContain(items, candidate => candidate.Key == Path.GetFileName(attributed) && candidate.Code
                is "stray-state-root" or "state-root-indeterminate" or "state-root-unreadable" or "state-root-unmeasured");
        });
    }

    /// <summary>
    /// Y-274 · <c>doctor --fix</c> has never been shown to repair any of these roots. Until it has,
    /// it is not offered as the answer to any of them: a suggestion the owner follows and that
    /// quietly does nothing costs more than no suggestion at all.
    /// </summary>
    [Fact(DisplayName = "Y-274 · Kanıtlanmamış doctor --fix, kök sorunlarına genel çözüm diye önerilmez")]
    public void Y274_NoStateRootItemRecommendsTheUnprovenFix()
    {
        WithProfile(profile =>
        {
            Provision(Path.Combine(profile.Roots, RootName(1)), "INSERT INTO quarantine(digest, source, reason, ts, path)" +
                " VALUES ('a', 'claude', 'bozuk', '2026-09-01T00:00:00+03:00', 'x.md')");
            Provision(Path.Combine(profile.Roots, RootName(2)), "CREATE VIRTUAL TABLE eski_arama USING fts4(govde)");
            Provision(Path.Combine(profile.Roots, RootName(3)), string.Empty);
            var broken = Path.Combine(profile.Roots, RootName(4));
            Provision(broken, "INSERT INTO daily_ingest(name, status, ts) VALUES ('x.md', 'ingested', '2026-09-01T00:00:00+03:00')");
            Shred(broken, from: 2048);
            var incomplete = Path.Combine(profile.Roots, RootName(5));
            Provision(incomplete, "DROP TABLE sweep_stamps");

            var items = new Doctor().StateRootItems(profile.LocalAppData);
            Assert.True(items.Count > 5, "fikstür geçersiz: rapor her sınıfı içermiyor");
            foreach (var item in items)
                Assert.DoesNotContain("--fix", item.Detail, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Y-275 · The acceptance gate itself. Three isolated roots — one plain WAL database, one whose
    /// schema does not match its stamp, one physically corrupt — are inventoried by content before
    /// and after the scan. Nothing may appear, including the <c>-wal</c>/<c>-shm</c> pair that used
    /// to be the documented exception, and no existing byte may move.
    ///
    /// This is not a hypothetical: every root here carries the persistent WAL stamp, because
    /// <c>State</c> writes <c>journal_mode=WAL</c> into the header. Before this test the scan left
    /// two files behind in each of them, every time it ran.
    /// </summary>
    [Fact(DisplayName = "Y-275 · Tarama izole köklerde tek dosya yaratmaz, tek bayt değiştirmez")]
    public void Y275_TheScanCreatesNothingAndChangesNothingInAnyRoot()
    {
        WithProfile(profile =>
        {
            var healthy = Path.Combine(profile.Roots, RootName(1));
            Provision(healthy, "INSERT INTO flush_log(ts, session_id, reason, outcome, turns, chars, backend)" +
                               " VALUES ('2026-09-01T00:00:00+03:00', 's1', 'sessionend', 'ok', 3, 30, 'claude')");

            var incomplete = Path.Combine(profile.Roots, RootName(2));
            Provision(incomplete, "DROP TABLE sweep_stamps");

            var corrupt = Path.Combine(profile.Roots, RootName(3));
            Provision(corrupt, "INSERT INTO daily_ingest(name, status, ts) VALUES ('x.md', 'ingested', '2026-09-01T00:00:00+03:00')");
            Shred(corrupt, from: 2048);

            foreach (var root in new[] { healthy, incomplete, corrupt })
            {
                var stamp = File.ReadAllBytes(Path.Combine(root, VaultIdentity.DatabaseName));
                Assert.True(stamp[18] == 2 || stamp[19] == 2,
                    $"fikstür geçersiz: {root} WAL damgası taşımıyor, bu testin ölçtüğü tehlike yok demektir");
            }

            var before = Inventory(profile.LocalAppData);

            var doctor = new Doctor();
            doctor.InspectStateRoots(profile.LocalAppData);
            doctor.StateRootItems(profile.LocalAppData);
            SqliteConnection.ClearAllPools();

            var after = Inventory(profile.LocalAppData);

            var appeared = after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            Assert.True(appeared.Length == 0, $"tarama dosya oluşturdu: {string.Join(", ", appeared)}");

            var vanished = before.Keys.Except(after.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            Assert.True(vanished.Length == 0, $"tarama dosya sildi: {string.Join(", ", vanished)}");

            foreach (var (path, digest) in before)
                Assert.True(string.Equals(after[path], digest, StringComparison.Ordinal),
                    $"tarama mevcut bir dosyanın içeriğini değiştirdi: {path}");
        });
    }

    /// <summary>
    /// Y-276 · When the scan cannot read a root without creating something beside the owner's file,
    /// it reads nothing and says which root and why. The verdict it must not reach is any of the
    /// ones that describe contents — above all "artık", which is an invitation to delete.
    /// </summary>
    [Fact(DisplayName = "Y-276 · Güvenle okunamayan kök, gerekçesiyle ölçülemedi olarak raporlanır")]
    public void Y276_ARootThatCannotBeReadSafelyIsReportedUnmeasured()
    {
        WithProfile(profile =>
        {
            var root = Path.Combine(profile.Roots, RootName(1));
            Provision(root, string.Empty);
            var before = Inventory(profile.LocalAppData);

            // One byte of headroom: no honest snapshot fits, so no snapshot is taken and the file
            // is never opened.
            var doctor = new Doctor(snapshotLimitBytes: 1);
            var report = Assert.Single(doctor.InspectStateRoots(profile.LocalAppData));
            Assert.Equal("ölçülemedi", report.Status);
            Assert.NotEqual(0, report.Reason.Length);
            Assert.Equal(0, report.Rows);

            var items = doctor.StateRootItems(profile.LocalAppData);
            var item = Assert.Single(items, candidate => candidate.Code == "state-root-unmeasured");
            Assert.Contains(report.Reason, item.Detail, StringComparison.Ordinal);
            Assert.Contains(root, item.Detail, StringComparison.Ordinal);

            // Silence in either direction would be the lie: not "empty", not "fine".
            Assert.DoesNotContain(items, candidate => candidate.Code is "stray-state-root" or "state-root-indeterminate");
            Assert.Equal(HealthLevel.Warning, item.Level);

            // And declining to read really did mean not reading.
            var after = Inventory(profile.LocalAppData);
            Assert.Equal(before.Count, after.Count);
            foreach (var (path, digest) in before)
                Assert.Equal(digest, after[path]);
        });
    }

    /// <summary>
    /// Y-277 · Why <c>immutable=1</c> is refused, measured rather than argued. It is the one read
    /// mode that creates no sidecar, and it earns that by telling SQLite the file cannot change —
    /// which makes it skip the write-ahead log. Here a root's entire contents live in the WAL,
    /// exactly as they do on a live vault between checkpoints. The shortcut sees an empty database
    /// and would have called somebody's memory residue; the scan reads the log and does not.
    /// </summary>
    [Fact(DisplayName = "Y-277 · Tarama WAL'daki kayıtları görür; immutable kısayolu kullanılmaz")]
    public void Y277_TheScanReadsTheWriteAheadLogRatherThanSkippingIt()
    {
        WithProfile(profile =>
        {
            var root = Path.Combine(profile.Roots, RootName(1));
            Provision(root, string.Empty);
            var database = Path.Combine(root, VaultIdentity.DatabaseName);

            // A writer that has not checkpointed — the ordinary state of a vault in use.
            using var live = new SqliteConnection($"Data Source={database}");
            live.Open();
            using (var command = live.CreateCommand())
            {
                command.CommandText =
                    "INSERT INTO flush_log(ts, session_id, reason, outcome, turns, chars, backend)" +
                    " WITH RECURSIVE sayac(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM sayac WHERE n < 7)" +
                    " SELECT '2026-09-01T00:00:00+03:00', 'oturum-' || n, 'sessionend', 'ok', 3, 30, 'claude' FROM sayac";
                command.ExecuteNonQuery();
            }

            Assert.True(new FileInfo(database + "-wal").Length > 0,
                "fikstür geçersiz: kayıtlar WAL'da değil, bu testin ölçtüğü fark yok demektir");

            // The refused shortcut, run here once so the cost of taking it is a measurement and not
            // an opinion: it opens, it creates nothing, and it cannot see a single one of the rows.
            long skipped;
            using (var immutable = new SqliteConnection(
                       "Data Source=file:///" + database.Replace(Path.DirectorySeparatorChar, '/') + "?immutable=1"))
            {
                immutable.Open();
                using var command = immutable.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM flush_log";
                skipped = Convert.ToInt64(command.ExecuteScalar());
            }

            Assert.Equal(0L, skipped);

            var report = Assert.Single(new Doctor().InspectStateRoots(profile.LocalAppData));
            Assert.Equal(7, report.Rows);
            Assert.Equal("sahipsiz", report.Status);
        });
    }

    /// <summary>
    /// Y-278 · The headline. Every class is counted on its own line of the summary, the new one
    /// included — a root nobody measured must not be quietly folded into the count of roots that
    /// were measured and found empty.
    /// </summary>
    [Fact(DisplayName = "Y-278 · Özet satırı her sınıfı ayrı sayar, ölçülemedi dahil")]
    public void Y278_TheHeadlineCountsEveryClassSeparately()
    {
        WithProfile(profile =>
        {
            Provision(Path.Combine(profile.Roots, RootName(1)), string.Empty);
            Provision(Path.Combine(profile.Roots, RootName(2)), "INSERT INTO quarantine(digest, source, reason, ts, path)" +
                " VALUES ('a', 'claude', 'bozuk', '2026-09-01T00:00:00+03:00', 'x.md')");

            var measured = new Doctor().StateRootItems(profile.LocalAppData);
            var headline = measured[0];
            Assert.Equal("state-roots", headline.Code);
            Assert.Contains("artık 1", headline.Detail, StringComparison.Ordinal);
            Assert.Contains("sahipsiz 1", headline.Detail, StringComparison.Ordinal);
            Assert.Contains("ölçülemedi 0", headline.Detail, StringComparison.Ordinal);

            var declined = new Doctor(snapshotLimitBytes: 1).StateRootItems(profile.LocalAppData);
            Assert.Equal("state-roots", declined[0].Code);
            Assert.Contains("ölçülemedi 2", declined[0].Detail, StringComparison.Ordinal);
            // The two roots did not become empty by not being looked at.
            Assert.Contains("artık 0", declined[0].Detail, StringComparison.Ordinal);
            Assert.Contains("sahipsiz 0", declined[0].Detail, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Y-279 · A corrupt file stays unreadable, keeps its zero, and now says what SQLite actually
    /// complained about — and the scan leaves the damaged bytes exactly as damaged as it found
    /// them. A diagnosis of a broken file that alters the broken file destroys the evidence a
    /// recovery would have been built on.
    /// </summary>
    [Fact(DisplayName = "Y-279 · Bozuk kök okunamıyor kalır, gerekçesini taşır, baytına dokunulmaz")]
    public void Y279_ACorruptRootKeepsItsBytesAndGainsAReason()
    {
        WithProfile(profile =>
        {
            var root = Path.Combine(profile.Roots, RootName(1));
            Provision(root, "INSERT INTO flush_log(ts, session_id, reason, outcome, turns, chars, backend)" +
                            " WITH RECURSIVE sayac(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM sayac WHERE n < 500)" +
                            " SELECT '2026-09-01T00:00:00+03:00', 'oturum-' || n, 'sessionend', 'ok', 3, 3000, 'claude' FROM sayac");
            Shred(root, from: -4096);

            var before = Inventory(profile.LocalAppData);

            var report = Assert.Single(new Doctor().InspectStateRoots(profile.LocalAppData));
            Assert.Equal("okunamıyor", report.Status);
            Assert.Equal(0, report.Rows);
            Assert.NotEqual(0, report.Reason.Length);

            var item = Assert.Single(new Doctor().StateRootItems(profile.LocalAppData),
                candidate => candidate.Code == "state-root-unreadable");
            Assert.Contains(report.Reason, item.Detail, StringComparison.Ordinal);
            Assert.Contains("boş sayılmaz", item.Detail, StringComparison.Ordinal);

            SqliteConnection.ClearAllPools();
            var after = Inventory(profile.LocalAppData);
            Assert.Equal(before.Count, after.Count);
            foreach (var (path, digest) in before)
                Assert.True(after.TryGetValue(path, out var now) && string.Equals(now, digest, StringComparison.Ordinal),
                    $"bozuk dosyanın baytları taramadan sonra değişti: {path}");
        });
    }

    /// <summary>
    /// Y-280 · The race the fixed fixtures could not show. Four still databases prove that a copy
    /// of a file nobody is writing comes back unchanged; they prove nothing about a live root, and
    /// a live root is the only kind the owner has. SQLite says it plainly: an ordinary file copy
    /// taken while a transaction is active can carry mixed content —
    /// https://sqlite.org/howtocorrupt.html#backup_or_restore_while_a_transaction_is_active
    ///
    /// So a writer commits and checkpoints throughout this test while the scan runs against the
    /// same root. Every committed transaction puts one row in each of two tables, which makes the
    /// invariant checkable from the outside: a total that belongs to one moment is even. An odd
    /// total is a database copied at one moment and a log copied at another, and it is a number
    /// that was never true of the owner's vault.
    /// </summary>
    [Fact(DisplayName = "Y-280 · Eşzamanlı yazma ve checkpoint boyunca alınan kopya tek bir ana aittir")]
    public void Y280_AConcurrentWriterAndCheckpointerNeverProduceATornCount()
    {
        WithProfile(profile =>
        {
            var root = Path.Combine(profile.Roots, RootName(1));
            // Big enough that copying it takes long enough for a commit or a checkpoint to land in
            // the middle of the copy. On a hundred-kilobyte fixture the copy is over before the
            // writer can get a word in, and a test that cannot lose is not measuring anything.
            Provision(root, Seed(SeedPairs));
            var database = Path.Combine(root, VaultIdentity.DatabaseName);
            Assert.True(new FileInfo(database).Length > 2_000_000,
                "fikstür geçersiz: veritabanı kopyalanırken yarışacak kadar büyük değil");

            // Pooling off: this connection is the test's own writer and must not be reachable by
            // the ClearAllPools the scan runs, or the test would be measuring its own plumbing.
            using var writer = new SqliteConnection($"Data Source={database};Pooling=False");
            writer.Open();
            Commit(writer, SeedPairs + 1);

            Assert.True(new FileInfo(database + "-wal").Length > 0,
                "fikstür geçersiz: yazıcı WAL bırakmadı, bu testin ölçtüğü yarış yok demektir");

            var before = Names(profile.LocalAppData);
            var committed = 1;
            var running = true;
            var pump = Task.Run(() =>
            {
                var index = SeedPairs + 2;
                while (Volatile.Read(ref running))
                {
                    Commit(writer, index++);
                    Interlocked.Increment(ref committed);
                    using var checkpoint = writer.CreateCommand();
                    // TRUNCATE is the cruel one: it empties the log and moves its pages into the
                    // database file, so a scan that copies the two halves independently can end up
                    // with a database from before the checkpoint and a log from after it.
                    checkpoint.CommandText = index % 4 == 0 ? "PRAGMA wal_checkpoint(TRUNCATE)" : "PRAGMA wal_checkpoint(PASSIVE)";
                    checkpoint.ExecuteNonQuery();
                    // A vault in use is written in bursts, not without pause; the gaps are where an
                    // honest measurement becomes possible at all, and they are part of the scenario.
                    Thread.Sleep(1);
                }
            });

            var verdicts = new List<StateRootReport>();
            for (var pass = 0; pass < 60; pass++)
                verdicts.Add(Assert.Single(new Doctor().InspectStateRoots(profile.LocalAppData)));

            Volatile.Write(ref running, false);
            pump.Wait(TimeSpan.FromMinutes(1));

            foreach (var verdict in verdicts)
            {
                // Either a consistent answer, or no answer with a reason. There is no third thing,
                // and "artık" — the verdict that invites a deletion — is not available at all.
                Assert.True(verdict.Status is "sahipsiz" or "ölçülemedi",
                    $"canlı kök beklenmedik hüküm aldı: {verdict.Status} · {verdict.Reason}");
                if (verdict.Status == "ölçülemedi")
                {
                    Assert.NotEqual(0, verdict.Reason.Length);
                    Assert.Equal(0, verdict.Rows);
                    continue;
                }

                Assert.True(verdict.Rows % 2 == 0,
                    $"kopya iki ayrı ana ait: {verdict.Rows} satır, tek sayı — hiçbir anda doğru olmayan bir sayı");
            }

            // The race is real in this fixture and it is not quietly tolerated. The writer moved
            // the file under most of these copies, and every copy that could not be shown to belong
            // to one moment was thrown away rather than counted. Measured on this machine over
            // sixty scans: 29 declined with the before/after check in place, 0 declined with it
            // removed — the same sixty copies, all of them trusted.
            Assert.True(verdicts.Count(verdict => verdict.Status == "ölçülemedi") >= 3,
                "kaynak kopyalama boyunca değişti ama hiçbir kopya reddedilmedi: hareket eden bir dosyadan alınan " +
                "görüntü sınanmadan sahibe sayı olarak sunuluyor");

            // The scan left nothing of its own beside the owner's files; the writer's own sidecars
            // are the only things in there and they were there before the scan started.
            Assert.Equal(before, Names(profile.LocalAppData));

            // And with the writer quiet but still attached — rows sitting in a log nobody has
            // checkpointed, which is what a vault looks like between two saves — the scan reads the
            // log and gets the number exactly right.
            var settled = Assert.Single(new Doctor().InspectStateRoots(profile.LocalAppData));
            Assert.Equal("sahipsiz", settled.Status);
            Assert.Equal(2 * (SeedPairs + committed), settled.Rows);
            Assert.True(committed > 1, "fikstür geçersiz: yazıcı hiç işlem tamamlamadı");
        });
    }

    /// <summary>
    /// Y-281 · The guard, measured rather than asserted in a comment. While the scan reads a root
    /// in place — the one path on which the owner's own file is opened at all — the operating
    /// system refuses every handle that wants to write it. That is what makes the header probe
    /// safe: the file cannot become a WAL database between the moment the scan reads
    /// <c>journal_mode</c> out of its header and the moment SQLite opens it, so the read-only open
    /// that would have created <c>state.db-wal</c> and <c>state.db-shm</c> beside the owner's data
    /// can never be reached with a stale answer.
    ///
    /// The one-byte snapshot limit is here to keep the measurement honest rather than to constrain
    /// anything: it makes the copy impossible, so the only way this scan can answer at all is by
    /// reading the file where it lies, and the only thing that can be holding writers off while it
    /// does is the guard. Without it a scan that fell back to copying would hold writers off during
    /// <c>File.Copy</c> — measured — and this test would pass while proving nothing.
    /// </summary>
    [Fact(DisplayName = "Y-281 · Yerinde okunan kök, okuma boyunca hiçbir yazıcıyı kabul etmez")]
    public void Y281_WhileTheScanReadsARootInPlaceNoWriterCanAttachToIt()
    {
        WithProfile(profile =>
        {
            var root = Path.Combine(profile.Roots, RootName(1));
            Provision(root, PairInsert(0));
            Demote(root);
            var database = Path.Combine(root, VaultIdentity.DatabaseName);
            var stamp = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database)));

            var running = true;
            var blocked = 0;
            var admitted = 0;
            var probe = Task.Run(() =>
            {
                while (Volatile.Read(ref running))
                {
                    try
                    {
                        using var handle = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                        Interlocked.Increment(ref admitted);
                    }
                    catch (IOException)
                    {
                        Interlocked.Increment(ref blocked);
                    }
                }
            });

            var verdicts = new List<StateRootReport>();
            for (var pass = 0; pass < 60; pass++)
                verdicts.Add(Assert.Single(new Doctor(snapshotLimitBytes: 1).InspectStateRoots(profile.LocalAppData)));

            Volatile.Write(ref running, false);
            probe.Wait(TimeSpan.FromMinutes(1));

            // Not "was a writer refused once" but "was the file protected for the whole read". A
            // guard taken for the header probe and let go again before SQLite opens the file would
            // still turn the odd writer away, and would still leave the window apex named wide
            // open. Measured on this machine, over sixty scans against one root: 0,826 of the
            // attempts refused with the guard held across the read, 0,018 with it released right
            // after the probe. The threshold sits between two numbers that are a factor of forty
            // apart, and both halves of it are reported by the failure message.
            var refused = (double)blocked / (blocked + admitted);
            Assert.True(refused > 0.5,
                $"tarama sahibin dosyasını okurken korumasız kaldı: {blocked}/{blocked + admitted} deneme geri çevrildi ({refused:F3})");
            Assert.True(admitted > 0,
                "fikstür geçersiz: dosya tarama koşmazken de yazmaya açılamıyor, ölçülen şey tarama değil");

            foreach (var verdict in verdicts)
                Assert.True(verdict.Status is "sahipsiz" or "ölçülemedi",
                    $"WAL'sız kök beklenmedik hüküm aldı: {verdict.Status} · {verdict.Reason}");
            Assert.Contains(verdicts, verdict => verdict.Status == "sahipsiz" && verdict.Rows == 2);

            // Nothing turned this database into a WAL database, and nothing was left beside it.
            Assert.False(File.Exists(database + "-wal") || File.Exists(database + "-shm"),
                "tarama sahibin klasörüne yan dosya bıraktı");
            Assert.Equal(stamp, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database))));
        });
    }

    /// <summary>
    /// Y-282 · What the guard is for, without a thread in sight. Reading in place is allowed only
    /// while no writer can attach; when one already has, the scan does not open the owner's file at
    /// all — it goes the long way round, through the copy, where a mode change after the header
    /// probe lands in the scan's own temporary folder and not in the owner's.
    ///
    /// The snapshot limit is the instrument: it constrains the copy and nothing else, so a root
    /// that answers under a one-byte limit was read in place and a root that reports
    /// <c>ölçülemedi</c> under the same limit was not. Same root, same non-WAL header, same limit —
    /// the only difference between the two halves is whether somebody else is holding the file.
    /// </summary>
    [Fact(DisplayName = "Y-282 · Yazıcı bağlıyken WAL'sız kök yerinde okunmaz, kopyaya düşer")]
    public void Y282_ARootAnotherWriterHoldsIsNeverReadInPlace()
    {
        WithProfile(profile =>
        {
            var root = Path.Combine(profile.Roots, RootName(1));
            Provision(root, PairInsert(0));
            Demote(root);
            var database = Path.Combine(root, VaultIdentity.DatabaseName);

            // Nobody attached: the scan can promise the file will not change under it, so it reads
            // it where it lies and the copy limit never comes into play.
            var alone = Assert.Single(new Doctor(snapshotLimitBytes: 1).InspectStateRoots(profile.LocalAppData));
            Assert.Equal("sahipsiz", alone.Status);
            Assert.Equal(2, alone.Rows);

            using var writer = new SqliteConnection($"Data Source={database};Pooling=False");
            writer.Open();

            var held = Assert.Single(new Doctor(snapshotLimitBytes: 1).InspectStateRoots(profile.LocalAppData));
            Assert.Equal("ölçülemedi", held.Status);
            Assert.NotEqual(0, held.Reason.Length);
            Assert.Equal(0, held.Rows);

            // With room to copy, the same held root is measured again — correctly, and out of a
            // copy, so the owner's folder is still untouched.
            var copied = Assert.Single(new Doctor().InspectStateRoots(profile.LocalAppData));
            Assert.Equal("sahipsiz", copied.Status);
            Assert.Equal(2, copied.Rows);
            Assert.False(File.Exists(database + "-wal") || File.Exists(database + "-shm"),
                "tarama sahibin klasörüne yan dosya bıraktı");
        });
    }

    /// <summary>
    /// Y-283 · The copy has to be of everything the database needs to be itself. A rollback journal
    /// with a live writer behind it means pages of a transaction that has not committed may already
    /// be sitting in the database file; the journal is what puts them back. Copying the file alone
    /// and counting it would publish a half-finished transaction as a finished one, so the scan
    /// declines instead and says which file it declined over.
    /// </summary>
    [Fact(DisplayName = "Y-283 · Açık işlemin geri alma günlüğü varken kök ölçülemedi sayılır")]
    public void Y283_AHotRollbackJournalIsDeclinedRatherThanCopiedWithoutIt()
    {
        WithProfile(profile =>
        {
            var root = Path.Combine(profile.Roots, RootName(1));
            Provision(root, PairInsert(0));
            Demote(root);
            var database = Path.Combine(root, VaultIdentity.DatabaseName);

            using var writer = new SqliteConnection($"Data Source={database};Pooling=False");
            writer.Open();
            using var transaction = writer.BeginTransaction();
            using (var command = writer.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = PairInsert(1);
                command.ExecuteNonQuery();
            }

            Assert.True(File.Exists(database + "-journal") && new FileInfo(database + "-journal").Length > 0,
                "fikstür geçersiz: açık işlem geri alma günlüğü yaratmadı");

            var report = Assert.Single(new Doctor().InspectStateRoots(profile.LocalAppData));
            Assert.Equal("ölçülemedi", report.Status);
            Assert.Equal(0, report.Rows);
            Assert.Contains("-journal", report.Reason, StringComparison.Ordinal);

            var item = Assert.Single(new Doctor().StateRootItems(profile.LocalAppData),
                candidate => candidate.Code == "state-root-unmeasured");
            Assert.Contains(report.Reason, item.Detail, StringComparison.Ordinal);

            // Declining is not a verdict about contents, and above all not the one that invites a
            // deletion: two rows really are committed in there.
            Assert.DoesNotContain(new Doctor().StateRootItems(profile.LocalAppData),
                candidate => candidate.Code is "stray-state-root" or "unattributed-state-root");
            Assert.False(File.Exists(database + "-wal") || File.Exists(database + "-shm"),
                "tarama sahibin klasörüne yan dosya bıraktı");

            transaction.Rollback();
        });
    }

    /// <summary>
    /// Y-284 · A root the way a killed process leaves one: transactions committed into the log and
    /// nobody left alive to check them in. Nothing is holding the file, so the scan copies it under
    /// its own guard — and the copy has to be of the pair. A database carried off without its log
    /// is a vault as it stood at the last checkpoint, which on a root like this one is a vault with
    /// nothing in it, and "nothing in it" is the sentence that invites a deletion.
    ///
    /// The fixture is built elsewhere and moved in because a writer that closes cleanly checkpoints
    /// its log away on the way out; the only way to leave one standing is to put it there.
    /// </summary>
    [Fact(DisplayName = "Y-284 · Sahibi gitmiş bir kökün WAL'ı kopyaya dahil edilir")]
    public void Y284_ALogLeftBehindByADepartedWriterIsCopiedWithTheDatabase()
    {
        WithProfile(profile =>
        {
            var donor = Path.Combine(profile.LocalAppData, "bagiscil");
            Provision(donor, string.Empty);
            var source = Path.Combine(donor, VaultIdentity.DatabaseName);

            var root = Path.Combine(profile.Roots, RootName(1));
            Directory.CreateDirectory(root);
            var database = Path.Combine(root, VaultIdentity.DatabaseName);

            using (var writer = new SqliteConnection($"Data Source={source};Pooling=False"))
            {
                writer.Open();
                for (var pair = 1; pair <= 7; pair++)
                    Commit(writer, pair);

                // Copied while the writer still holds the log open, which is the only moment at
                // which the log exists at all.
                File.Copy(source, database);
                File.Copy(source + "-wal", database + "-wal");
            }

            SqliteConnection.ClearAllPools();
            Assert.True(new FileInfo(database + "-wal").Length > 0,
                "fikstür geçersiz: kökte bekleyen bir WAL yok, bu testin ölçtüğü kayıp yok demektir");
            Assert.False(File.Exists(database + "-shm"), "fikstür geçersiz: kök tek başına duran bir WAL taşımıyor");

            var report = Assert.Single(new Doctor().InspectStateRoots(profile.LocalAppData));
            Assert.Equal(14, report.Rows);
            Assert.Equal("sahipsiz", report.Status);

            // And the log the scan had to read is still exactly where the owner left it.
            Assert.True(new FileInfo(database + "-wal").Length > 0, "tarama sahibin WAL dosyasını boşalttı");
            Assert.False(File.Exists(database + "-shm"), "tarama sahibin klasörüne yan dosya bıraktı");
        });
    }

    // ---- fixture -----------------------------------------------------------

    private sealed record Profile(string LocalAppData, string Roots);

    /// <summary>A profile of this run's own, with the state-roots folder the scan will walk.</summary>
    private static void WithProfile(Action<Profile> test)
    {
        var fixtureRoot = ScarFixture.TempDirectory();
        try
        {
            var localAppData = Path.Combine(fixtureRoot, "yerel");
            var roots = Path.Combine(localAppData, "oom");
            Directory.CreateDirectory(roots);
            test(new Profile(localAppData, roots));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(fixtureRoot);
        }
    }

    /// <summary>Sixteen lowercase hex digits — the only directory shape the scan accepts as a root.</summary>
    private static string RootName(int index) => index.ToString("x16");

    /// <summary>A state root with the real schema, then the given seed SQL.</summary>
    private static void Provision(string root, string seed)
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, VaultIdentity.DatabaseName);
        using (var state = new State(null, null, database)) { }
        SqliteConnection.ClearAllPools();

        if (seed.Length > 0)
        {
            using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = seed;
                command.ExecuteNonQuery();
            }
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>How many transactions Y-280 lays down before the race starts — enough to make the copy slow.</summary>
    private const int SeedPairs = 6000;

    /// <summary>A filler that makes each row big enough for the fixture to reach megabytes.</summary>
    private const string Filler = "dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu-dolgu";

    /// <summary>The same pair, laid down <paramref name="pairs"/> times in one statement each.</summary>
    private static string Seed(int pairs) =>
        $"WITH RECURSIVE sayac(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM sayac WHERE n < {pairs})" +
        " INSERT INTO flush_log(ts, session_id, reason, outcome, turns, chars, backend)" +
        $" SELECT '2026-09-01T00:00:00+03:00', 'oturum-' || n || '{Filler}', 'sessionend', 'ok', 3, 30, 'claude' FROM sayac;" +
        $" WITH RECURSIVE sayac(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM sayac WHERE n < {pairs})" +
        " INSERT INTO quarantine(digest, source, reason, ts, path)" +
        $" SELECT 'ozet-' || n, 'claude', 'bozuk', '2026-09-01T00:00:00+03:00', 'x' || n || '{Filler}.md' FROM sayac";

    /// <summary>
    /// One transaction's worth of writing: a row in each of two tables, so that any snapshot taken
    /// between two transactions carries an even number of rows and a snapshot assembled out of two
    /// different moments can be caught carrying an odd one.
    /// </summary>
    private static string PairInsert(int index) =>
        "INSERT INTO flush_log(ts, session_id, reason, outcome, turns, chars, backend)" +
        $" VALUES ('2026-09-01T00:00:00+03:00', 'oturum-{index}', 'sessionend', 'ok', 3, 30, 'claude');" +
        " INSERT INTO quarantine(digest, source, reason, ts, path)" +
        $" VALUES ('ozet-{index}', 'claude', 'bozuk', '2026-09-01T00:00:00+03:00', 'x{index}.md')";

    /// <summary>Commits one <see cref="PairInsert"/> as a single transaction, both rows or neither.</summary>
    private static void Commit(SqliteConnection writer, int index)
    {
        using var transaction = writer.BeginTransaction();
        using (var command = writer.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = PairInsert(index);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Takes a provisioned root back out of WAL mode. <see cref="State"/> stamps every database it
    /// creates with <c>journal_mode=WAL</c>, and the path being measured in Y-281 and Y-282 is the
    /// other one: the database the scan is allowed to open where it lies.
    /// </summary>
    private static void Demote(string root)
    {
        var database = Path.Combine(root, VaultIdentity.DatabaseName);
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=delete";
            command.ExecuteScalar();
        }

        SqliteConnection.ClearAllPools();
        var header = File.ReadAllBytes(database);
        Assert.True(header[18] == 1 && header[19] == 1,
            "fikstür geçersiz: kök WAL kipinden çıkmadı, yerinde okuma yolu sınanmıyor");
        Assert.False(File.Exists(database + "-wal") || File.Exists(database + "-shm"),
            "fikstür geçersiz: kip düşürüldü ama yan dosyalar duruyor");
    }

    /// <summary>Which files exist under the fixture profile — names only, for a root a writer is changing under us.</summary>
    private static HashSet<string> Names(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The descriptor an installed root carries, naming the vault it serves.</summary>
    private static void Describe(string root, string vault) =>
        File.WriteAllText(Path.Combine(root, VaultIdentity.DescriptorName),
            "{\"vault\":" + System.Text.Json.JsonSerializer.Serialize(vault) + ",\"schema\":1}",
            new UTF8Encoding(false));

    /// <summary>Overwrites a fixture database from the given offset on; a negative offset counts back from the end.</summary>
    private static void Shred(string root, int from)
    {
        var database = Path.Combine(root, VaultIdentity.DatabaseName);
        var bytes = File.ReadAllBytes(database);
        for (var offset = from < 0 ? bytes.Length + from : from; offset < bytes.Length; offset++)
            bytes[offset] = 0x5A;
        File.WriteAllBytes(database, bytes);
    }

    /// <summary>
    /// Every file under the fixture profile with the SHA-256 of its contents. Lengths alone would
    /// call a rewritten page an untouched file, and on a WAL database a write lands in a sidecar
    /// the same size as the one that was there before.
    /// </summary>
    private static Dictionary<string, string> Inventory(string directory)
    {
        SqliteConnection.ClearAllPools();
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Convert.ToHexString(SHA256.HashData(stream));
            }, StringComparer.OrdinalIgnoreCase);
    }
}
