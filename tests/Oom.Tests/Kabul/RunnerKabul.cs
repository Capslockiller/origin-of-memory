using System.Text;
using System.Text.Json;
using Oom.Contracts;
using Oom.Tests.Kabul.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// Acceptance oracle for lane L1-runner (SPEC-3.1.0.md S1, as amended by B2; NB-16;
/// Cut: Runner.ReadNumber). Drives a real oom.exe through <see cref="KabulHarness"/>
/// against a <see cref="FakeClaude"/> shim on PATH — never a real model call — plus one
/// unit-level test through <see cref="IProcessRunner"/> for the one behaviour that is
/// not cleanly observable at the process boundary (NB-16).
///
/// Every black-box test here is RED against this tree's current code (Runner.cs still
/// builds the pre-B2 flag set: -p --output-format json --permission-mode default --tools
/// "" --max-turns 1 --model &lt;id&gt;, with no smoke call at all) and RED again against
/// eski-exe/oom.exe (OOM_KABUL_EXE) for the same reason — see the lane's proof notes.
/// </summary>
public sealed class RunnerKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = KabulVaultBuilder.DefaultToday;

    // The turn timestamps below are pinned to noon-ish LOCAL time on Today's calendar
    // date using the running machine's OWN utc offset for that date (not Today's stored
    // +03:00 offset), so Flush.EventTime's conversion to TimeZoneInfo.Local can never
    // roll the date across midnight regardless of which timezone the test machine is in.
    // KabulVaultBuilder.WriteDaily names its fixture daily file from Today's raw
    // yyyy-MM-dd (never timezone-converted), so this keeps both agreeing on the same file.
    private static readonly TimeSpan LocalOffsetOnToday =
        TimeZoneInfo.Local.GetUtcOffset(DateTime.SpecifyKind(Today.Date, DateTimeKind.Unspecified));

    [Fact(DisplayName = "S1/B2-1 · flush claude'u B2 bayrak setiyle çağırır (-p, --strict-mcp-config, --no-session-persistence, --setting-sources project, --tools \"\", --max-turns 1, --model, --system-prompt), --bare hiçbir çağrıda geçmez")]
    public void MeasuredFlagSet_IsPassedToClaude_AndBareIsNeverUsed()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var transcript = WriteTranscript(harness, "b2-argv", turnCount: 4);

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var pathScope = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["flush", "--detached", "--transcript", transcript, "--session", "s"], fakeNow: Today);

        Assert.True(fake.CallCount >= 2,
            $"en az 2 claude çağrısı bekleniyordu (duman + özet); ölçülen: {fake.CallCount}. stdout: {result.Stdout} stderr: {result.Stderr}");

        var calls = fake.AllArgv();
        var summarizing = calls.LastOrDefault(argv => argv.Contains("--model"));
        Assert.True(summarizing is not null,
            $"hiçbir claude çağrısının argv'sinde --model yok; kaydedilen çağrılar: {DumpCalls(calls)}");

        Assert.Contains("-p", summarizing!);
        Assert.Contains("--strict-mcp-config", summarizing!);
        Assert.Contains("--no-session-persistence", summarizing!);
        Assert.Contains("--system-prompt", summarizing!);
        AssertFlagValue(summarizing!, "--setting-sources", "project");
        AssertFlagValue(summarizing!, "--tools", string.Empty);
        AssertFlagValue(summarizing!, "--max-turns", "1");

        var modelIndex = summarizing!.ToList().IndexOf("--model");
        Assert.True(modelIndex >= 0 && modelIndex + 1 < summarizing.Count, "--model bayrağının bir değeri yok");
        Assert.Matches(@"^claude-[a-z]+-[0-9-]+$", summarizing[modelIndex + 1]);

        foreach (var argv in calls)
            Assert.DoesNotContain("--bare", argv);
    }

    [Fact(DisplayName = "S1/B2-2 · duman çağrısı 'unknown option' ile çıkış 1 verirse flush 'runner uyumsuz' ile durur ve günlük değişmez")]
    public void SmokeCliFailure_ReportsRunnerUyumsuzAndLeavesDailyUntouched()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var transcript = WriteTranscript(harness, "b2-cli-fail", turnCount: 4);
        var dailyPath = DailyPath(vault);
        var dailyBefore = File.ReadAllText(dailyPath, Utf8);

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.UnknownOptionFailure());
        using var pathScope = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["flush", "--detached", "--transcript", transcript, "--session", "s"], fakeNow: Today);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("runner uyumsuz", result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.Equal(dailyBefore, File.ReadAllText(dailyPath, Utf8));
    }

    [Fact(DisplayName = "B2-3a · duman init olayında mcp_servers doluysa, çıkış 0 ve is_error=false olsa bile flush 'runner uyumsuz' ile durur")]
    public void SmokeInitWithNonEmptyMcpServers_FailsLoudDespiteZeroExit()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var transcript = WriteTranscript(harness, "b2-mcp", turnCount: 4);
        var dailyPath = DailyPath(vault);
        var dailyBefore = File.ReadAllText(dailyPath, Utf8);

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeWithMcpServer(), FakeClaudeResponse.SummaryOk());
        using var pathScope = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["flush", "--detached", "--transcript", transcript, "--session", "s"], fakeNow: Today);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("runner uyumsuz", result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.Equal(dailyBefore, File.ReadAllText(dailyPath, Utf8));
    }

    [Fact(DisplayName = "B2-3b · duman init olayında tools/mcp_servers/plugins boş ve is_error=false ise flush özetlemeye geçer")]
    public void SmokeInitEmpty_LetsFlushProceedToSummarize()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var transcript = WriteTranscript(harness, "b2-clean", turnCount: 4);
        var dailyPath = DailyPath(vault);
        var dailyBefore = File.ReadAllText(dailyPath, Utf8);

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var pathScope = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["flush", "--detached", "--transcript", transcript, "--session", "s"], fakeNow: Today);

        Assert.DoesNotContain("runner uyumsuz", result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.True(fake.CallCount >= 2,
            $"duman geçtiyse özetleme çağrısı da yapılmalı; ölçülen çağrı sayısı: {fake.CallCount}");
        Assert.NotEqual(dailyBefore, File.ReadAllText(dailyPath, Utf8));
    }

    [Fact(DisplayName = "NB-16 · Runner.cs:59 — claude sıfırdan farklı çıkarsa RunResult.Text boş döner, girdi geri yansımaz")]
    public void NonZeroExit_GivesEmptyRunResultText_NotTheInputPrompt()
    {
        var vault = Path.Combine(AppContext.BaseDirectory, "runner-kabul-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vault);
        try
        {
            var runner = new Runner(
                new RunnerProfile(vault, new ClaudeSettings("claude-haiku-4-5-20251001", "claude-sonnet-5")),
                new AlwaysFailsProcessRunner());

            const string prompt = "gizli-girdi-metni-geri-yansimamali";
            var result = runner.Run(prompt, ModelTier.Fast, ComponentKind.Flush, "summary");

            Assert.NotNull(result.Error);
            Assert.Equal(string.Empty, result.Text);
            Assert.DoesNotContain(prompt, result.Text, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(vault, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string DailyPath(string vault) => Path.Combine(vault, "daily", $"{Today:yyyy-MM-dd}.md");

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

    private static void AssertFlagValue(IReadOnlyList<string> argv, string flag, string expectedValue)
    {
        var list = argv.ToList();
        var index = list.IndexOf(flag);
        Assert.True(index >= 0, $"{flag} argv'de yok: {string.Join(' ', list.Select(Quote))}");
        Assert.True(index + 1 < list.Count, $"{flag} bir değer olmadan bitiyor: {string.Join(' ', list.Select(Quote))}");
        Assert.Equal(expectedValue, list[index + 1]);
    }

    private static string DumpCalls(IReadOnlyList<IReadOnlyList<string>> calls) =>
        string.Join(" | ", calls.Select((argv, i) => $"çağrı {i + 1}: {string.Join(' ', argv.Select(Quote))}"));

    private static string Quote(string token) => token.Length == 0 ? "\"\"" : token;

    private sealed class AlwaysFailsProcessRunner : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request, TimeSpan timeout) => new(1, string.Empty, "claude: kimlik doğrulama başarısız", true);
    }
}
