using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oom.Contracts;

namespace Oom.Tests.Kabul;

public sealed class SweepStampKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = new(2026, 9, 28, 12, 0, 0, TimeSpan.FromHours(3));
    private static readonly Regex Anchor = new(@"<!-- session:(?<id>\S+) ts:\S+ turns:(?<start>\d+)-(?<end>\d+)", RegexOptions.Compiled);

    [Fact(DisplayName = "R29/R32/O1 · sweep 70 turu boşaltır, ok damgalar ve ikinci koşu yazmaz")]
    public void Sweep_DrainsSeventyTurns_ThenSkipsStampedFile()
    {
        using var harness = new KabulHarness();
        var (vault, root) = Fixture(harness, "drain");
        var transcript = WriteTranscript(root, "sweep-drain", 70);
        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var path = fake.ReplaceProcessPath();

        var first = harness.Run(vault, ["sweep"], fakeNow: Today, timeout: TimeSpan.FromSeconds(30));
        var before = DailyText(vault);
        var second = harness.Run(vault, ["sweep"], fakeNow: Today, timeout: TimeSpan.FromSeconds(30));
        var after = DailyText(vault);
        var ranges = Anchors(vault, "sweep-drain");
        var stamp = ReadStamp(harness, transcript);

        Assert.True(first.ExitCode == 0 && ranges.SequenceEqual([(0, 29), (30, 59), (60, 69)])
            && stamp == "ok" && before == after,
            $"first={first.ExitCode}; ranges={string.Join(',', ranges)}; stamp={stamp}; second={second.Stdout}");
    }

    [Fact(DisplayName = "R30/O11 · sweep runner uyumsuzluğunda ilk dosyada durur ve exit 2 döner")]
    public void Sweep_RunnerIncompatibleStopsAndReturnsExitTwo()
    {
        using var harness = new KabulHarness();
        var (vault, root) = Fixture(harness, "incompatible");
        WriteTranscript(root, "first", 4);
        WriteTranscript(root, "second", 4);
        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeWithMcpServer());
        using var path = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["sweep"], fakeNow: Today, timeout: TimeSpan.FromSeconds(20));

        Assert.True(result.ExitCode == 2 && fake.CallCount == 1,
            $"exit={result.ExitCode}; calls={fake.CallCount}; stdout={result.Stdout}; stderr={result.Stderr}");
    }

    [Fact(DisplayName = "R32/O8 · okunamayan dökümler kapsanmış sayılmaz")]
    public void UnreadableTranscripts_AreUncoveredNotOneHundredPercent()
    {
        using var harness = new KabulHarness();
        var (vault, root) = Fixture(harness, "unreadable");
        for (var i = 0; i < 6; i++)
            File.WriteAllText(Path.Combine(root, $"bad-{i}.jsonl"), "{\"type\":\"unknown\",\"payload\":\"x\"}\n", Utf8);

        harness.Run(vault, ["sweep"], fakeNow: Today);
        harness.Run(vault, ["sweep"], fakeNow: Today);
        var doctor = harness.Run(vault, ["doctor", "--json"], fakeNow: Today);
        var coverage = LatestCoverage(harness);

        Assert.True(!(coverage.Covered == 6 && coverage.Total == 6)
            && !(doctor.ExitCode == 0 && doctor.Stdout.Contains("\"coverage\": 1", StringComparison.Ordinal)),
            $"coverage={coverage.Covered}/{coverage.Total}; doctor-exit={doctor.ExitCode}; json={doctor.Stdout}");
    }

    // Third-family review of wave 7, finding #1: the stamp table SweepRun reads back had no
    // 'skipped' arm, so a too-short session this release itself stamped 'skipped' turned
    // into 'partial' (uncovered) on the next, unchanged sweep.
    [Fact(DisplayName = "R32/R35 · 3.1'in yazdığı 'skipped' damgası sonraki taramada da paydanın dışında kalır")]
    public void SkippedStamp_StaysSkippedOnTheNextSweep()
    {
        using var harness = new KabulHarness();
        var (vault, root) = Fixture(harness, "skipped");
        WriteTranscript(root, "kisa", 2);
        WriteTranscript(root, "uzun", 4);
        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var path = fake.ReplaceProcessPath();

        var first = harness.Run(vault, ["sweep"], fakeNow: Today);
        var second = harness.Run(vault, ["sweep"], fakeNow: Today);

        Assert.True(first.Stdout.Contains("1/1 kapsandı", StringComparison.Ordinal), "ilk: " + first.Stdout);
        Assert.True(second.Stdout.Contains("1/1 kapsandı", StringComparison.Ordinal), "ikinci: " + second.Stdout);
    }

    [Fact(DisplayName = "R32/O10 · tüm sweep kökleri eksikse ölçülmedi/error olur")]
    public void MissingOnlySweepRoot_IsErrorAndNotFullCoverage()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        var missing = Path.Combine(harness.Root, "does-not-exist");
        WriteSettings(vault, [missing]);

        harness.Run(vault, ["sweep"], fakeNow: Today);
        var doctor = harness.Run(vault, ["doctor", "--json"], fakeNow: Today);

        // Parsed, not a raw substring: the JSON escapes every backslash of a Windows path.
        using var json = JsonDocument.Parse(doctor.Stdout);
        var named = json.RootElement.GetProperty("items").EnumerateArray()
            .Any(item => item.GetProperty("detail").GetString()?.Contains(missing, StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(doctor.ExitCode != 0 && named
            && !doctor.Stdout.Contains("\"coverage\": 1", StringComparison.Ordinal),
            $"exit={doctor.ExitCode}; json={doctor.Stdout}; stderr={doctor.Stderr}");
    }

    [Fact(DisplayName = "R32/O10 · karma köklerde eksik kök doctor'da ERROR satırıdır, adıyla")]
    public void MixedSweepRoots_ReportMissingRootAsError()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        var existing = harness.NewScratchDirectory("existing-root");
        var missing = Path.Combine(harness.Root, "missing-root");
        WriteTranscript(existing, "readable", 4);
        WriteSettings(vault, [existing, missing]);
        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var path = fake.ReplaceProcessPath();

        harness.Run(vault, ["sweep"], fakeNow: Today);
        var doctor = harness.Run(vault, ["doctor", "--json"], fakeNow: Today);
        using var json = JsonDocument.Parse(doctor.Stdout);
        var rows = json.RootElement.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("detail").GetString()?.Contains(missing, StringComparison.OrdinalIgnoreCase) == true).ToArray();

        // SPEC R32: "a configured sweep root that does not exist is an ERROR row naming the root".
        Assert.Contains(rows, row => row.GetProperty("level").GetString()?.Equals("Error", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static (string Vault, string Root) Fixture(KabulHarness harness, string name)
    {
        var vault = BuildVault(harness.Root);
        var root = harness.NewScratchDirectory(name + "-root");
        WriteSettings(vault, [root]);
        return (vault, root);
    }

    private static string BuildVault(string root)
    {
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts"));
        return vault;
    }

    private static void WriteSettings(string vault, IReadOnlyList<string> roots)
    {
        Directory.CreateDirectory(Path.Combine(vault, ".oom"));
        File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"), JsonSerializer.Serialize(new
        {
            sweep = new { roots, maxSessionsPerRun = 20, minTurns = 3 },
            flush = new { mode = "dilim", sliceTurns = 30 }
        }), Utf8);
    }

    private static string WriteTranscript(string root, string id, int count)
    {
        var path = Path.Combine(root, id + ".jsonl");
        var lines = Enumerable.Range(0, count).Select(i => JsonSerializer.Serialize(new
        {
            sessionId = id,
            type = i % 2 == 0 ? "user" : "assistant",
            timestamp = Today.AddMinutes(i).ToString("O", CultureInfo.InvariantCulture),
            message = new { role = i % 2 == 0 ? "user" : "assistant", content = $"turn-{i:D2} " + new string('x', 500) }
        }));
        File.WriteAllText(path, string.Join('\n', lines), Utf8);
        File.SetLastWriteTime(path, Today.LocalDateTime);
        return path;
    }

    private static IReadOnlyList<(int Start, int End)> Anchors(string vault, string id) =>
        [.. Directory.EnumerateFiles(Path.Combine(vault, "daily"), "*.md")
            .SelectMany(file => Anchor.Matches(File.ReadAllText(file, Utf8)))
            .Where(match => match.Groups["id"].Value == id)
            .Select(match => (int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture)))];

    private static string DailyText(string vault) => string.Join("\n", Directory.EnumerateFiles(Path.Combine(vault, "daily"), "*.md")
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => File.ReadAllText(path, Utf8)));

    private static string? ReadStamp(KabulHarness harness, string transcript)
    {
        var database = Directory.EnumerateFiles(harness.LocalAppData, "state.db", SearchOption.AllDirectories).Single();
        using var state = new State(null, null, database);
        return state.ReadStamp(transcript)?.Outcome;
    }

    private static CoverageReading LatestCoverage(KabulHarness harness)
    {
        var database = Directory.EnumerateFiles(harness.LocalAppData, "state.db", SearchOption.AllDirectories).Single();
        using var state = new State(null, null, database);
        return Assert.IsType<CoverageReading>(state.ReadCoverage());
    }
}
