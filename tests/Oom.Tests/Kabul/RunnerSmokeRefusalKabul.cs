using System.Text;
using System.Text.Json;
using Oom.Tests.Kabul.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// O22 acceptance oracle: Runner's B2 smoke-check refuses a non-empty <c>tools</c> list, a
/// non-empty <c>plugins</c> list, and an <c>is_error=true</c> result event
/// (src/Oom/Runner/Runner.cs's <c>ProbeSmoke</c>, ~line 251) — each on its own, with the
/// OTHER two signals clean, so no single test could pass by accident from a different
/// check firing. RunnerKabul.cs already covers the sibling <c>mcp_servers</c> case
/// (B2-3a) and the all-clean pass-through case (B2-3b); before this lane, `tools` and
/// `plugins` non-empty and `is_error=true` had no test at all — deleting any one of the
/// three corresponding `if` branches in <c>ProbeSmoke</c> left the full suite green.
///
/// Same drive shape as RunnerKabul.cs's own B2-3a/B2-3b: a real oom.exe through
/// <see cref="KabulHarness"/> against a <see cref="FakeClaude"/> shim on PATH, never a
/// real model call. Every test here additionally asserts NO summarization call was ever
/// made (<see cref="FakeClaude.CallCount"/> stays at 1, the smoke call only) — a refusal
/// that still went on to summarize would be a worse bug than a refusal with no marker.
/// </summary>
public sealed class RunnerSmokeRefusalKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = KabulVaultBuilder.DefaultToday;

    [Fact(DisplayName = "O22 #1 · duman init olayında tools doluysa, çıkış 0 ve is_error=false olsa bile flush 'runner uyumsuz' ile durur, özetlemeye hiç geçmez")]
    public void SmokeInitWithNonEmptyTools_FailsLoud_NeverSummarizes()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var transcript = WriteTranscript(harness, "o22-tools", turnCount: 4);
        var dailyPath = DailyPath(vault);
        var dailyBefore = File.ReadAllText(dailyPath, Utf8);

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeWithTool(), FakeClaudeResponse.SummaryOk());
        using var pathScope = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["flush", "--detached", "--transcript", transcript, "--session", "s"], fakeNow: Today);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("runner uyumsuz", result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.Equal(dailyBefore, File.ReadAllText(dailyPath, Utf8));
        Assert.Equal(1, fake.CallCount);
    }

    [Fact(DisplayName = "O22 #2 · duman init olayında plugins doluysa, çıkış 0 ve is_error=false olsa bile flush 'runner uyumsuz' ile durur, özetlemeye hiç geçmez")]
    public void SmokeInitWithNonEmptyPlugins_FailsLoud_NeverSummarizes()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var transcript = WriteTranscript(harness, "o22-plugins", turnCount: 4);
        var dailyPath = DailyPath(vault);
        var dailyBefore = File.ReadAllText(dailyPath, Utf8);

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeWithPlugin(), FakeClaudeResponse.SummaryOk());
        using var pathScope = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["flush", "--detached", "--transcript", transcript, "--session", "s"], fakeNow: Today);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("runner uyumsuz", result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.Equal(dailyBefore, File.ReadAllText(dailyPath, Utf8));
        Assert.Equal(1, fake.CallCount);
    }

    [Fact(DisplayName = "O22 #3 · duman çağrısının result olayı is_error=true dönerse, tools/mcp_servers/plugins hepsi boş olsa bile flush 'runner uyumsuz' ile durur, özetlemeye hiç geçmez")]
    public void SmokeResultIsError_FailsLoud_NeverSummarizes()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var transcript = WriteTranscript(harness, "o22-iserror", turnCount: 4);
        var dailyPath = DailyPath(vault);
        var dailyBefore = File.ReadAllText(dailyPath, Utf8);

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeIsError(), FakeClaudeResponse.SummaryOk());
        using var pathScope = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["flush", "--detached", "--transcript", transcript, "--session", "s"], fakeNow: Today);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("runner uyumsuz", result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.Equal(dailyBefore, File.ReadAllText(dailyPath, Utf8));
        Assert.Equal(1, fake.CallCount);
    }

    private static string DailyPath(string vault) => Path.Combine(vault, "daily", $"{Today:yyyy-MM-dd}.md");

    // Same shape as RunnerKabul.WriteTranscript: turn timestamps pinned to noon-ish LOCAL
    // time on Today's calendar date using the running machine's own UTC offset, so
    // Flush.EventTime's conversion to TimeZoneInfo.Local can never roll the date across
    // midnight regardless of which timezone the test machine is in.
    private static readonly TimeSpan LocalOffsetOnToday =
        TimeZoneInfo.Local.GetUtcOffset(DateTime.SpecifyKind(Today.Date, DateTimeKind.Unspecified));

    private static string WriteTranscript(KabulHarness harness, string sessionId, int turnCount)
    {
        var directory = harness.NewScratchDirectory("transcripts-" + sessionId);
        var path = Path.Combine(directory, "session.jsonl");

        var lines = new List<string>();
        for (var i = 0; i < turnCount; i++)
        {
            var role = i % 2 == 0 ? "user" : "assistant";
            var timestamp = new DateTimeOffset(Today.Year, Today.Month, Today.Day, 9, i, 0, LocalOffsetOnToday);
            var text = $"Sentetik tur {i}: kabul harness fixture metni, gerçek içerik değildir (oturum {sessionId}).";
            lines.Add(JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                index = i,
                role,
                kind = "text",
                text,
                timestamp = timestamp.ToString("O")
            }));
        }

        File.WriteAllText(path, string.Join('\n', lines), Utf8);
        return path;
    }
}
