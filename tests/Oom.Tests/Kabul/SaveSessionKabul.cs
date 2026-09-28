using System.Text.Json;
using Oom.Contracts;

namespace Oom.Tests.Kabul;

public sealed class SaveSessionKabul
{
    private static readonly DateTimeOffset Today = new(2026, 9, 28, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact(DisplayName = "S1 · save --session-json profilli runner ve durable state kullanır")]
    public void SaveSessionJson_UsesProfiledRunnerAndDurableState()
    {
        using var harness = new KabulHarness();
        var vault = Path.Combine(harness.Root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts"));
        var session = new Session("save-profiled", "claude",
            [.. Enumerable.Range(0, 4).Select(i => new Turn(i, i % 2 == 0 ? "user" : "assistant", "text", $"turn-{i}", Today.AddMinutes(i)))], Today);
        var json = Path.Combine(harness.Root, "session.json");
        File.WriteAllText(json, JsonSerializer.Serialize(session));
        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var path = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["save", "--session-json", json], fakeNow: Today);
        var databases = Directory.EnumerateFiles(harness.LocalAppData, "state.db", SearchOption.AllDirectories).ToArray();

        Assert.True(result.ExitCode == 0 && fake.CallCount >= 2 && databases.Length == 1,
            $"exit={result.ExitCode}; calls={fake.CallCount}; db={databases.Length}; stdout={result.Stdout}; stderr={result.Stderr}");
    }
}
