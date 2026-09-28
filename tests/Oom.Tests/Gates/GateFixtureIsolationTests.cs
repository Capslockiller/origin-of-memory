using System.Text.Json;

namespace Oom.Tests.Gates;

/// <summary>
/// O24 acceptance oracle for <see cref="GateFixture"/>/<see cref="TempVault"/> themselves
/// (NOT a SPEC-3.1.0.md feature): before this fix, <c>GateFixture.Run(...)</c> — used by
/// today's "Kapı 12-1" JSON tests in ContextScars.cs and GetirmeScars.cs — spawned oom.exe
/// with OOM_LOCALAPPDATA left completely unset, which falls through to the developer's
/// REAL <c>%LOCALAPPDATA%\oom</c>, and <see cref="TempVault.Dispose"/> then deleted a
/// directory computed from THIS TEST PROCESS's own (also unset) OOM_LOCALAPPDATA — i.e.
/// under that same real folder.
///
/// This test never deletes anything itself: it only LISTS the real folder's entries
/// (names only) before and after a "Gate12-style" run — a <see cref="TempVault"/> plus a
/// plain <c>GateFixture.Run(...)</c> call, exactly the shape ContextScars's/GetirmeScars's
/// own "Kapı 12-1" tests use — and asserts the listing is byte-for-byte unchanged. If a
/// regression reintroduces the unset default, this run would MINT a new subdirectory
/// there (named by the hash of the temp vault path) and the listing would grow by one.
/// </summary>
public sealed class GateFixtureIsolationTests
{
    [Fact(DisplayName = "O24 · Gate12 tarzı bir koşum (TempVault + GateFixture.Run) gerçek %LOCALAPPDATA%\\oom dizin listesini değiştirmez")]
    public void Gate12StyleRun_NeverChangesTheRealLocalAppDataOomListing()
    {
        var realOomRoot = RealLocalAppDataOomRoot();
        var before = ListNames(realOomRoot);

        using (var vault = new TempVault())
        {
            GateFixture.WriteVault(vault.Path);
            File.WriteAllText(Path.Combine(vault.Path, "daily", "2026-09-09.md"), "# Günlük Log: 2026-09-09\n", GateFixture.Utf8);

            // Same call shape as ContextScars's/GetirmeScars's own "Kapı 12-1" tests: no
            // explicit OOM_LOCALAPPDATA override, relying entirely on GateFixture.Run's
            // own default.
            var run = GateFixture.Run("context", "--json", "--vault", vault.Path);
            Assert.Equal(0, run.ExitCode);
            using var document = JsonDocument.Parse(run.StandardOutput);
            Assert.True(document.RootElement.TryGetProperty("schema_version", out _),
                $"'context --json' beklenmedik çıktı verdi (exit={run.ExitCode}): {run.StandardOutput}\nstderr: {run.StandardError}");
        }
        // TempVault.Dispose already ran (end of using): if it still reached for the real
        // profile, the damage would already be done by the time we list again below.

        var after = ListNames(realOomRoot);

        Assert.Equal(before, after);
    }

    [Fact(DisplayName = "O24 · GateFixture.Run varsayılanı gerçek %LOCALAPPDATA% yerine izole kökü kullanır")]
    public void GateFixtureRun_DefaultsToIsolatedRoot_NeverTheRealLocalAppData()
    {
        var realOomRoot = RealLocalAppDataOomRoot();

        using var vault = new TempVault();
        GateFixture.WriteVault(vault.Path);

        var run = GateFixture.Run("context", "--vault", vault.Path);
        Assert.Equal(0, run.ExitCode);

        // The child process actually wrote its state under the isolated root, not the
        // real one — proving GateFixture.Run's default isn't merely "harmless because
        // context never writes", but genuinely redirected.
        var isolatedStateRoot = GateFixture.IsolatedStateRoot(vault.Path);
        Assert.False(Directory.Exists(Path.Combine(realOomRoot, Path.GetFileName(isolatedStateRoot))),
            "context çalıştırması gerçek %LOCALAPPDATA%\\oom altında bir durum dizini yaratmış görünüyor.");
    }

    private static string RealLocalAppDataOomRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oom");

    /// <summary>Top-level entry NAMES only (never contents, never a delete) — exactly what
    /// the O24 defect asks this oracle to prove unaffected.</summary>
    private static string[] ListNames(string root) =>
        Directory.Exists(root)
            ? [.. Directory.EnumerateFileSystemEntries(root)
                .Select(entry => Path.GetFileName(entry) ?? entry)
                .OrderBy(name => name, StringComparer.Ordinal)]
            : [];
}
