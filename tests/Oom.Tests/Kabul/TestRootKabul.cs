using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oom.Tests.Kabul;

/// <summary>
/// F7-1 acceptance: the test temp root and the product's TEMP-mechanism exclusion are
/// injectable (OOM_TEST_ROOT). Before this lane, Flush.IsMechanismTranscript and
/// SweepRun's temporary-project-prefix always compared against the real OS temp
/// directory (Path.GetTempPath()) with no way to point them elsewhere. A transcript that
/// happens to sit under the real OS temp directory is silently excluded from `sweep`
/// discovery as a "mechanism" artifact, while the identical transcript sitting outside
/// the OS temp directory is discovered normally — so whether a transcript is found at all
/// depends on an accident of where it (or the worktree/CI checkout it lives under)
/// physically sits, not on anything about the transcript itself.
///
/// This test drives the SAME transcript-discovery scenario twice through the real exe —
/// once with the fixture physically under %TEMP%, once physically outside %TEMP% — with
/// OOM_TEST_ROOT set (via env var) to one fixed, unrelated stand-in directory both times.
/// On the current tree (which ignores OOM_TEST_ROOT and always consults the real OS temp
/// path) the two runs disagree: the %TEMP%-rooted transcript is excluded, the other is
/// not. After F7-1, both runs consult the injected root instead, agree with each other,
/// and no longer depend on the real OS temp path at all — this test goes green only once
/// that seam exists.
/// </summary>
public sealed class TestRootKabul
{
    [Fact]
    public void SweepDiscoveryAgreesWhetherTheFixtureSitsUnderOrOutsideRealTemp()
    {
        using var harness = new KabulHarness();
        var vault = harness.NewScratchDirectory("vault");

        // A stand-in "mechanism root" both runs inject via OOM_TEST_ROOT. It is
        // deliberately unrelated to either transcript location below, so a fix that
        // consults it instead of the real OS temp path finds nothing to exclude in
        // either run — proving the outcome no longer depends on where the transcript
        // physically sits relative to the real %TEMP%.
        var mechanismStandIn = Path.Combine(harness.Root, "mechanism-stand-in-" + Guid.NewGuid().ToString("N")[..8]);

        var underTemp = Path.Combine(Path.GetTempPath(), "oom-testroot-kabul-" + Guid.NewGuid().ToString("N")[..12]);
        var outsideTemp = harness.NewScratchDirectory("outside-temp-projects");

        // The two runs only prove anything if the fixtures are actually on opposite
        // sides of the real OS temp directory. outsideTemp comes from
        // AppContext.BaseDirectory (see KabulHarness.CreateIsolatedRoot), which is not
        // guaranteed to sit outside %TEMP% when the checkout or test run is itself
        // deployed under it (finding: should) - so check the physical fact instead of
        // assuming it.
        var realTemp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        Assert.StartsWith(realTemp + Path.DirectorySeparatorChar, Path.GetFullPath(underTemp), StringComparison.OrdinalIgnoreCase);
        Assert.False(
            Path.GetFullPath(outsideTemp).StartsWith(realTemp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"outsideTemp ('{outsideTemp}') must physically sit outside the real OS temp directory ('{realTemp}') for this test to prove anything.");

        // A transcript placed directly under the injected mechanism root itself must
        // still be discovered as excluded - otherwise an implementation that dropped
        // TEMP exclusion entirely (and always returns "found") would pass the two
        // assertions above for the wrong reason (finding: should).
        var underMechanismRoot = Path.Combine(mechanismStandIn, "under-mechanism-root");

        try
        {
            WriteTranscript(Directory.CreateDirectory(underTemp).FullName, "sess-under-temp");
            WriteTranscript(outsideTemp, "sess-outside-temp");
            WriteTranscript(underMechanismRoot, "sess-under-mechanism-root");

            var discoveredUnderTemp = RunSweepDryRun(harness, vault, underTemp, mechanismStandIn);
            var discoveredOutsideTemp = RunSweepDryRun(harness, vault, outsideTemp, mechanismStandIn);
            var discoveredUnderMechanismRoot = RunSweepDryRun(harness, vault, underMechanismRoot, mechanismStandIn);

            Assert.Equal(1, discoveredOutsideTemp);
            Assert.Equal(discoveredOutsideTemp, discoveredUnderTemp);
            Assert.Equal(0, discoveredUnderMechanismRoot);
        }
        finally
        {
            TryDelete(underTemp);
        }
    }

    [Fact]
    public void DefaultMechanismRootStillExcludesRealTempWhenOomTestRootIsUnset()
    {
        using var harness = new KabulHarness();
        var vault = harness.NewScratchDirectory("vault-default-root");

        // Lane assertion 2 (graft suggestion): with OOM_TEST_ROOT left unset, a
        // transcript physically under the real OS temp directory must still be excluded
        // by the default mechanism root - proving the default (production, no env var)
        // path was not broken while making the injected seam independently testable.
        var underRealTemp = Path.Combine(Path.GetTempPath(), "oom", "run", "oom-testroot-kabul-default-" + Guid.NewGuid().ToString("N")[..12]);

        var previous = Environment.GetEnvironmentVariable("OOM_TEST_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("OOM_TEST_ROOT", null);
            WriteTranscript(Directory.CreateDirectory(underRealTemp).FullName, "sess-default-under-temp");
            WriteSweepSettings(vault, underRealTemp);

            var result = harness.Run(vault, ["sweep", "--dry-run"], fakeNow: new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(3)));
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(0, ParseDiscoveredFileCount(result.Stdout));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OOM_TEST_ROOT", previous);
            // Only remove this test's own unique leaf directory, never the shared
            // %TEMP%\oom\run parent - that path may be in real use by hooks running on
            // this machine outside this test.
            TryDelete(underRealTemp);
        }
    }

    private static int RunSweepDryRun(KabulHarness harness, string vault, string sweepRoot, string mechanismStandIn)
    {
        WriteSweepSettings(vault, sweepRoot);

        var previous = Environment.GetEnvironmentVariable("OOM_TEST_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("OOM_TEST_ROOT", mechanismStandIn);
            var result = harness.Run(vault, ["sweep", "--dry-run"], fakeNow: new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(3)));
            Assert.Equal(0, result.ExitCode);
            return ParseDiscoveredFileCount(result.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OOM_TEST_ROOT", previous);
        }
    }

    private static void WriteSweepSettings(string vault, string sweepRoot)
    {
        var directory = Path.Combine(vault, ".oom");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new { sweep = new { roots = new[] { sweepRoot } } });
        File.WriteAllText(Path.Combine(directory, "oom.json"), json, new UTF8Encoding(false));
    }

    private static void WriteTranscript(string directory, string sessionId)
    {
        Directory.CreateDirectory(directory);
        var lines = Enumerable.Range(0, 3).Select(i =>
            $"{{\"session_id\":\"{sessionId}\",\"index\":{i},\"role\":\"{(i % 2 == 0 ? "user" : "assistant")}\",\"kind\":\"text\",\"text\":\"gercek proje mesaji {i}\",\"timestamp\":\"2026-09-20T0{i}:00:00+03:00\"}}");
        File.WriteAllText(Path.Combine(directory, sessionId + ".jsonl"), string.Join('\n', lines), new UTF8Encoding(false));
    }

    // The dry-run summary line looks like: "tarama (kuru koşum): 1 dosya, 0 değişmiş, ...".
    private static readonly Regex DosyaCount = new(@"(\d+)\s+dosya", RegexOptions.Compiled);

    private static int ParseDiscoveredFileCount(string stdout)
    {
        var match = DosyaCount.Match(stdout);
        Assert.True(match.Success, $"'dosya' sayacı stdout'ta bulunamadı: {stdout}");
        return int.Parse(match.Groups[1].Value);
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
