// yazan: claude · opus-5
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Gates;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests;

/// <summary>
/// <c>Doctor</c>'s stray-root scan releases its file handles with
/// <see cref="SqliteConnection.ClearAllPools"/> — twice: once per scan
/// (<c>Doctor.InspectStateRoots</c>) and once per copied root
/// (<c>StateRootSnapshot.Dispose</c>). Both are measured necessities: without them the read-only
/// handles outlive the scan and the owner cannot move a root, and byte-for-byte copies of the
/// owner's database pile up under the temporary directory.
///
/// Both are also <em>process-wide</em>. That is the property these tests are about: a cleanup
/// whose blast radius is the whole process is a cleanup that can reach a connection nobody asked
/// it to touch. Disabling xunit's class parallelism keeps the suite from tripping over this, but
/// it says nothing about the product — <c>oom doctor</c> runs its scan in the same process as an
/// open state handle every time (<c>Program.Health</c>), and nothing in the product serialises
/// the two. So these tests build their own concurrency and keep it whatever the runner does:
/// threads in <c>Y-290</c>, two operating-system processes in <c>Y-291</c>.
///
/// Neither test goes near the owner's real profile. Every root they scan is one they made
/// themselves under the temporary directory, and the scan is pointed at it explicitly —
/// <c>InspectStateRoots(localAppData)</c> in-process, <c>OOM_LOCALAPPDATA</c> for the executable.
/// <c>--fix</c> is never passed: the redirect steers the scan, not the writing side.
/// </summary>
public sealed class EszamanliTemizlikTests
{
    /// <summary>
    /// Y-290 · An open state handle and <c>Doctor</c>'s root scan, in one process, at the same
    /// time. The handle belongs to a database the scan is not reading and has no business
    /// touching; the scan's process-wide pool clearing must not be able to reach it. The intermittent
    /// <c>ObjectDisposedException: SQLitePCL.sqlite3</c> that closed this lane is exactly this
    /// shape — a handle destroyed underneath a thread that was still using it — so the assertion
    /// is that the handle keeps answering for as long as it is held, not merely that it was
    /// opened once.
    /// </summary>
    [Fact(DisplayName = "Y-290 · Doctor'ın kök taraması başka bir iş parçacığındaki açık durum tutamacını öldürmez")]
    public void Y290_TheRootScanDoesNotKillAnOpenStateHandleHeldOnAnotherThread()
    {
        var fixtureRoot = ScarFixture.TempDirectory();
        try
        {
            var localAppData = Path.Combine(fixtureRoot, "yerel");
            for (var index = 1; index <= 3; index++)
                Provision(Path.Combine(localAppData, "oom", index.ToString("x16")));

            // The handle under test lives OUTSIDE the scanned profile on purpose: the scan has no
            // reason to know about it, which is what makes reaching it a blast-radius question
            // rather than a question about reading the wrong file.
            var owned = Path.Combine(fixtureRoot, "sahip", "state.db");
            Directory.CreateDirectory(Path.GetDirectoryName(owned)!);
            SqliteConnection.ClearAllPools();

            Exception? scanError = null;
            Exception? holderError = null;
            var scans = 0;
            var queries = 0;
            var stop = false;

            var scanning = new Thread(() =>
            {
                try
                {
                    var doctor = new Doctor();
                    while (!Volatile.Read(ref stop))
                    {
                        var reports = doctor.InspectStateRoots(localAppData);
                        if (reports.Count != 3)
                            throw new InvalidOperationException($"tarama {reports.Count} kök gördü, 3 bekleniyordu.");
                        Interlocked.Increment(ref scans);
                    }
                }
                catch (Exception error)
                {
                    scanError = error;
                }
            })
            { IsBackground = true, Name = "kar1-tarama" };

            using (var state = new State(null, null, owned))
            {
                state.Scalar("SELECT COUNT(*) FROM calls");
                scanning.Start();
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(3);
                    while (DateTime.UtcNow < deadline && scanError is null)
                    {
                        Assert.Equal(0, state.Scalar("SELECT COUNT(*) FROM calls"));
                        Assert.Equal(5, state.Scalar("PRAGMA user_version"));
                        queries++;
                    }
                }
                catch (Exception error)
                {
                    holderError = error;
                }
                finally
                {
                    Volatile.Write(ref stop, true);
                    Assert.True(scanning.Join(TimeSpan.FromSeconds(60)), "tarama iş parçacığı 60 saniyede bitmedi.");
                }
            }

            // The window has to have been a real one. A test that held a handle while nothing
            // happened next to it would pass for the wrong reason.
            Assert.True(scans > 0, "tarama hiç koşmadı; eşzamanlılık penceresi açılmadı.");
            Assert.True(queries > 0, "açık tutamaç hiç sorgulanmadı; eşzamanlılık penceresi açılmadı.");
            Assert.True(scanError is null, $"tarama iş parçacığı düştü: {scanError}");
            Assert.True(holderError is null,
                $"açık durum tutamacı, {scans} tarama süresince {queries} sorgudan sonra öldü: {holderError}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(fixtureRoot);
        }
    }

    /// <summary>
    /// Y-291 · The product's own concurrency, which no runner setting can serialise away: two
    /// <c>oom doctor</c> processes reading one profile at the same instant. Each is its own
    /// process with its own pool, so neither can clear the other's — what they do share is the
    /// owner's files and the write-denying guard the scan takes over them. The failure this pins
    /// is the quiet one: a scan that cannot take the guard because its twin holds it, reporting
    /// the root <c>ölçülemedi</c> or <c>okunamıyor</c> and telling the owner a root is unreadable
    /// when the only thing wrong was that somebody else was reading it. Both runs must reach the
    /// same verdict about the same roots, and must leave the roots' bytes alone doing it.
    ///
    /// What this test deliberately does NOT assert: that the scan removes its temporary copies.
    /// It does not, and measurement says so — a single, non-concurrent `oom doctor` over this same
    /// two-root fixture leaves copies behind too (1–3 per run, see <c>KAR1.md</c>). That is a
    /// product defect in <c>StateRootSnapshot.Dispose</c>, named and left as a patch proposal
    /// rather than papered over here; it is not a concurrency property and this lane does not
    /// touch <c>src/</c>. The copies this run makes are counted and removed below all the same.
    /// </summary>
    [Fact(DisplayName = "Y-291 · Aynı izole profili eşzamanlı okuyan iki doctor süreci aynı hükmü verir ve bayta dokunmaz")]
    public void Y291_TwoConcurrentDoctorProcessesReachTheSameVerdictWithoutTouchingAByte()
    {
        var fixtureRoot = ScarFixture.TempDirectory();
        var vault = Path.Combine(fixtureRoot, "kasa");
        Directory.CreateDirectory(vault);
        var strayStateRoot = VaultIdentity.StateRoot(vault);
        try
        {
            var localAppData = Path.Combine(fixtureRoot, "yerel");
            for (var index = 1; index <= 2; index++)
                Provision(Path.Combine(localAppData, "oom", index.ToString("x16")));
            SqliteConnection.ClearAllPools();

            var before = Fingerprint(localAppData);
            var copiesBefore = DoctorTemporaryDirectories();

            var results = new (int ExitCode, string StandardOutput, string StandardError)[2];
            var failures = new Exception?[2];
            var ready = new Barrier(2);
            var threads = new Thread[2];
            for (var slot = 0; slot < threads.Length; slot++)
            {
                var index = slot;
                threads[index] = new Thread(() =>
                {
                    try
                    {
                        // Both processes are released at once; staggering them would test two
                        // sequential runs wearing a concurrency costume.
                        ready.SignalAndWait();
                        results[index] = GateFixture.RunScoped(localAppData, "doctor", "--json", "--vault", vault);
                    }
                    catch (Exception error)
                    {
                        failures[index] = error;
                    }
                })
                { IsBackground = true, Name = $"kar1-doctor-{index}" };
            }

            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads)
                Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "eşzamanlı doctor süreci 120 saniyede bitmedi.");

            var verdicts = new string[results.Length];
            for (var index = 0; index < results.Length; index++)
            {
                Assert.True(failures[index] is null, $"{index}. doctor süreci sürülemedi: {failures[index]}");
                Assert.True(results[index].ExitCode == 0,
                    $"{index}. doctor süreci çıkış kodu {results[index].ExitCode} döndürdü: {results[index].StandardError}");

                using var document = JsonDocument.Parse(results[index].StandardOutput);
                var scan = document.RootElement.GetProperty("items").EnumerateArray()
                    .Where(item => item.GetProperty("Code").GetString() == "state-roots")
                    .ToArray();
                Assert.True(scan.Length == 1,
                    $"{index}. doctor süreci kök taramasını {scan.Length} kez bildirdi, 1 bekleniyordu.");
                verdicts[index] = scan[0].GetProperty("Detail").GetString() ?? string.Empty;

                // The quiet failure: a root the twin had open, written off as unreadable.
                Assert.Contains("okunamıyor 0", verdicts[index]);
                Assert.Contains("ölçülemedi 0", verdicts[index]);
                Assert.Contains("2 durum kökü", verdicts[index]);
            }

            // Two readers, one answer. A scan whose verdict depends on who else is scanning is not
            // a diagnosis, it is a coin toss.
            Assert.Equal(verdicts[0], verdicts[1]);

            // Read-only means read-only for both of them at once, too.
            Assert.Equal(before, Fingerprint(localAppData));
            Assert.False(Directory.Exists(strayStateRoot),
                $"'doctor' (--fix olmadan) durum kökünü yarattı: {strayStateRoot}");

            // Counted and removed, not asserted away: see the summary above.
            foreach (var copy in DoctorTemporaryDirectories().Except(copiesBefore, StringComparer.OrdinalIgnoreCase))
            {
                try { Directory.Delete(copy, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(fixtureRoot);
            ScarFixture.Remove(strayStateRoot);
        }
    }

    /// <summary>A state root of this run's own, carrying the real schema and one row.</summary>
    private static void Provision(string root)
    {
        Directory.CreateDirectory(root);
        using (var state = new State(null, null, Path.Combine(root, VaultIdentity.DatabaseName)))
            state.Scalar("SELECT COUNT(*) FROM calls");
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Every file under the fixture profile with its length, so a byte of drift is visible.</summary>
    private static Dictionary<string, long> Fingerprint(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => new FileInfo(path).Length, StringComparer.OrdinalIgnoreCase);

    /// <summary>The scan's working copies, by the name it gives them.</summary>
    private static string[] DoctorTemporaryDirectories() =>
        Directory.Exists(Path.GetTempPath())
            ? Directory.EnumerateDirectories(Path.GetTempPath(), "oom-doctor-*").ToArray()
            : [];
}
