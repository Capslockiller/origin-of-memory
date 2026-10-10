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
