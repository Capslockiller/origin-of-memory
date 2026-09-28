using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oom.Contracts;

namespace Oom.Tests.Kabul;

public sealed class FlushDrainKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = new(2026, 9, 28, 12, 0, 0, TimeSpan.FromHours(3));
    private static readonly Regex Anchor = new(@"<!-- session:(?<id>\S+) ts:\S+ turns:(?<start>\d+)-(?<end>\d+)", RegexOptions.Compiled);

    [Fact(DisplayName = "R29/O1 · save tek çağrıda 70 turun tamamını boşaltır")]
    public void SaveSessionJson_DrainsAllSeventyTurns()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        var session = Session("save-drain", 70);
        var json = Path.Combine(harness.Root, "save-session.json");
        File.WriteAllText(json, JsonSerializer.Serialize(session), Utf8);
        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var path = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["save", "--session-json", json], fakeNow: Today, timeout: TimeSpan.FromSeconds(30));
        var anchors = Anchors(vault, session.Id);

        Assert.True(result.ExitCode == 0 && anchors.SequenceEqual([(0, 29), (30, 59), (60, 69)]),
            $"exit={result.ExitCode}; anchors={string.Join(',', anchors)}; stdout={result.Stdout}; stderr={result.Stderr}");
    }

    [Fact(DisplayName = "R29/Codex-7 · bütçeyi tek başına aşan ilk tur MinTurns yüzünden kilitlenmez")]
    public void OversizedSinglePendingTurn_IsSummarisedDespiteMinTurns()
    {
        var session = new Session("oversized", "claude",
            [new Turn(0, "user", "text", new string('x', 15_001), Today)], Today);
        // The profile's vault must not contain %TEMP%\oom\run, or the runner's isolation check
        // refuses (and rightly): use a vault directory below %TEMP%, not %TEMP% itself.
        var runner = new Runner(new RunnerProfile(Path.Combine(Path.GetTempPath(), "oom-r29-vault-" + Guid.NewGuid().ToString("N")), new ClaudeSettings("claude-haiku-4-5-20251001", "claude-sonnet-5"),
            Path.Combine(Path.GetTempPath(), "oom-r29-" + Guid.NewGuid().ToString("N"))), new SummaryRunner());
        var result = new Flush(new FlushOptions(MinTurns: 3), null, runner).FlushSession(session, string.Empty, FlushReason.SessionEnd);

        Assert.Equal(FlushOutcome.Ok, result.Outcome);
        Assert.Equal(1, result.Turns);
    }

    [Fact(DisplayName = "R30/Codex-12 · manuel flush unreadable sonucunda exit 2 döner")]
    public void ManualDetachedFlush_UnreadableReturnsExitTwo()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        var missing = Path.Combine(harness.Root, "missing.jsonl");

        var result = harness.Run(vault, ["flush", "--detached", "--session", "missing", "--transcript", missing], fakeNow: Today);

        Assert.True(result.ExitCode == 2, $"exit={result.ExitCode}; stdout={result.Stdout}; stderr={result.Stderr}");
    }

    [Fact(DisplayName = "O6 · runner hata ayrıntısı retry_queue ve FlushResult içinde maskelenir")]
    public void RunnerFailure_IsMaskedInRetryStateAndResult()
    {
        using var fixture = new DirectFixture("mask");
        const string secret = "Wq73fakeLEAK99";
        var runner = fixture.Runner(new FailureRunner($"## Bağlam\npanel şifre: {secret} kullanıldı"));
        var flush = new Flush(new FlushOptions(VaultPath: fixture.Vault, RejectionPath: fixture.State.WorkDirectory), null, runner, null, fixture.State);

        var result = flush.FlushSession(Session("masked-error", 4), string.Empty, FlushReason.SessionEnd);
        var stored = Assert.Single(fixture.State.ReadColumn("SELECT last_error FROM retry_queue WHERE session_id = 'masked-error'"));

        Assert.Equal(FlushOutcome.Retry, result.Outcome);
        Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        Assert.Contains("maskelendi", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, result.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("maskelendi", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "R30/O11 · runner smoke uyumsuzluğu retry hakkı tüketmez ve sonra iyileşir")]
    public void RunnerIncompatible_DoesNotConsumeRetriesOrParkSession()
    {
        using var fixture = new DirectFixture("smoke");
        var session = Session("smoke-session", 4);
        for (var i = 0; i < 6; i++)
        {
            var flush = new Flush(new FlushOptions(VaultPath: fixture.Vault, RejectionPath: fixture.State.WorkDirectory), null,
                fixture.Runner(new IncompatibleRunner()), null, fixture.State);
            var failure = flush.FlushSession(session, string.Empty, FlushReason.Sweep);
            Assert.NotEqual(FlushOutcome.Parked, failure.Outcome);
        }

        Assert.Equal(0, fixture.State.Scalar("SELECT COUNT(*) FROM retry_queue WHERE session_id = 'smoke-session'"));
        var passing = new Flush(new FlushOptions(VaultPath: fixture.Vault, RejectionPath: fixture.State.WorkDirectory), null,
            fixture.Runner(new SummaryRunner()), null, fixture.State).FlushSession(session, string.Empty, FlushReason.Sweep);
        Assert.Equal(FlushOutcome.Ok, passing.Outcome);
    }

    private static string BuildVault(string root)
    {
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts"));
        return vault;
    }

    private static Session Session(string id, int count) => new(id, "claude",
        [.. Enumerable.Range(0, count).Select(i => new Turn(i, i % 2 == 0 ? "user" : "assistant", "text",
            $"turn-{i:D2}-payload " + new string((char)('a' + i % 26), 500), Today.AddMinutes(i)))], Today);

    private static IReadOnlyList<(int Start, int End)> Anchors(string vault, string id) =>
        [.. Directory.EnumerateFiles(Path.Combine(vault, "daily"), "*.md")
            .SelectMany(file => Anchor.Matches(File.ReadAllText(file, Utf8)))
            .Where(match => match.Groups["id"].Value == id)
            .Select(match => (int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture)))];

    private sealed class DirectFixture : IDisposable
    {
        private readonly string root;
        public string Vault { get; }
        public State State { get; }

        public DirectFixture(string name)
        {
            root = Path.Combine(AppContext.BaseDirectory, "kabul-runs", name + "-" + Guid.NewGuid().ToString("N"));
            Vault = BuildVault(root);
            var stateDir = Path.Combine(root, "state");
            Directory.CreateDirectory(stateDir);
            State = new State(null, null, Path.Combine(stateDir, "state.db"));
        }

        public Runner Runner(IProcessRunner process) => new(
            new RunnerProfile(Vault, new ClaudeSettings("claude-haiku-4-5-20251001", "claude-sonnet-5"), State.WorkDirectory), process);

        public void Dispose()
        {
            State.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private sealed class SummaryRunner : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request, TimeSpan timeout) => request.Arguments.Contains("stream-json")
            ? new ProcessResult(0, "{\"type\":\"system\",\"subtype\":\"init\",\"tools\":[],\"mcp_servers\":[],\"plugins\":[]}\n{\"type\":\"result\",\"is_error\":false,\"result\":\"ok\"}", string.Empty, true)
            : new ProcessResult(0, JsonSerializer.Serialize(new { result = FakeClaudeResponse.ValidSummaryText }), string.Empty, true);
    }

    private sealed class FailureRunner(string secret) : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request, TimeSpan timeout) => request.Arguments.Contains("stream-json")
            ? new SummaryRunner().Run(request, timeout)
            : new ProcessResult(1, secret, secret, true);
    }

    private sealed class IncompatibleRunner : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request, TimeSpan timeout) =>
            new(0, "{\"type\":\"system\",\"subtype\":\"init\",\"tools\":[],\"mcp_servers\":[{\"name\":\"x\"}],\"plugins\":[]}\n{\"type\":\"result\",\"is_error\":false}", string.Empty, true);
    }
}
