using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oom.Contracts;

namespace Oom.Tests.Kabul;

public sealed class FlushLockKabul
{
    private static readonly DateTimeOffset Today = new(2026, 9, 28, 12, 0, 0, TimeSpan.FromHours(3));
    private static readonly Regex Anchor = new(@"<!-- session:(?<id>\S+) ts:\S+ turns:(?<start>\d+)-(?<end>\d+)", RegexOptions.Compiled);

    [Fact(DisplayName = "R31/O3 · eşzamanlı iki flush aynı session aralığını yalnız bir kez özetler")]
    public async Task ConcurrentFlushes_SummariseRangeExactlyOnce()
    {
        using var fixture = new LockFixture();
        var session = Session("same-session", 14);
        var calls = new CountingSummaryRunner(delayMilliseconds: 800);
        using var firstState = fixture.OpenState();
        using var secondState = fixture.OpenState();
        var first = fixture.Flush(firstState, calls);
        var second = fixture.Flush(secondState, calls);

        var results = await Task.WhenAll(
            Task.Run(() => first.FlushSession(session, string.Empty, FlushReason.PreCompact)),
            Task.Run(() => second.FlushSession(session, string.Empty, FlushReason.SessionEnd)));

        var anchors = fixture.Anchors(session.Id);
        Assert.True(anchors.Count == 1 && calls.SummaryCalls == 1
            && results.Count(result => result.Outcome == FlushOutcome.Ok) == 1
            && results.Any(result => result.Outcome is FlushOutcome.NoNewTurns or FlushOutcome.Locked),
            $"anchors={string.Join(',', anchors)}; runner={calls.SummaryCalls}; outcomes={string.Join(',', results.Select(r => r.Outcome))}");
    }

    [Fact(DisplayName = "R31/O3 · boş state günlük anchor imlecinden tohumlanır")]
    public void EmptyState_SeedsCursorFromExistingDailyAnchor()
    {
        using var fixture = new LockFixture();
        var session = Session("seeded-session", 14);
        File.WriteAllText(Path.Combine(fixture.Vault, "daily", "2026-09-28.md"),
            "# Günlük\n\n<!-- session:seeded-session ts:2026-09-28T10:00:00+03:00 turns:0-9 source:claude -->\nönceki\n");
        var runner = new CountingSummaryRunner();
        using var state = fixture.OpenState();

        var result = fixture.Flush(state, runner).FlushSession(session, string.Empty, FlushReason.SessionEnd);

        Assert.Equal(FlushOutcome.Ok, result.Outcome);
        Assert.Contains((10, 13), fixture.Anchors(session.Id));
        Assert.DoesNotContain((0, 13), fixture.Anchors(session.Id));
    }

    [Fact(DisplayName = "R31/O17 · günlük kilitliyken bitmiş özet retry olarak dayanıklı kaydedilir")]
    public void DailyLockedForWholeCall_QueuesWithoutAdvancingCursor()
    {
        using var fixture = new LockFixture();
        var session = Session("locked-daily", 8);
        var daily = fixture.SeedDaily();
        using var state = fixture.OpenState();
        var flush = fixture.Flush(state, new CountingSummaryRunner());
        using var held = new FileStream(daily, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = flush.FlushSession(session, string.Empty, FlushReason.SessionEnd);
        var row = state.ReadSessionRow(session.Id);

        Assert.Equal(FlushOutcome.Retry, result.Outcome);
        Assert.NotNull(row);
        Assert.True(row!.Attempts >= 1 && row.Cursor == -1 && !string.IsNullOrWhiteSpace(row.LastError));
        Assert.Empty(fixture.Anchors(session.Id));
    }

    [Fact(DisplayName = "R31/O17 · kısa paylaşım ihlali atomik yazım retry penceresinde iyileşir")]
    public async Task DailyLockReleasedWithinRetryWindow_WritesExactlyOnce()
    {
        using var fixture = new LockFixture();
        var session = Session("brief-lock", 8);
        var daily = fixture.SeedDaily();
        using var state = fixture.OpenState();
        var flush = fixture.Flush(state, new CountingSummaryRunner());
        var held = new FileStream(daily, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () => { await Task.Delay(300); held.Dispose(); });

        var result = flush.FlushSession(session, string.Empty, FlushReason.SessionEnd);
        await release;

        Assert.Equal(FlushOutcome.Ok, result.Outcome);
        Assert.Single(fixture.Anchors(session.Id));
    }

    [Fact(DisplayName = "O13 · detached commit istisnası health, flush_log ve retry_queue izi bırakır")]
    public void DetachedCommitFailure_LeavesDurableTraceAndCanRecover()
    {
        using var harness = new KabulHarness();
        var vault = Path.Combine(harness.Root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts"));
        var daily = Path.Combine(vault, "daily", "2026-09-28.md");
        File.WriteAllText(daily, "# Günlük\n");
        var transcript = WriteTranscript(harness.Root, "commit-failure", 8);
        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var path = fake.ReplaceProcessPath();
        using var held = new FileStream(daily, FileMode.Open, FileAccess.Read, FileShare.Read);

        var failed = harness.Run(vault, ["flush", "--detached", "--session", "commit-failure", "--transcript", transcript,
            "--reason", "sessionend"], fakeNow: Today, timeout: TimeSpan.FromSeconds(15));
        var database = Directory.EnumerateFiles(harness.LocalAppData, "state.db", SearchOption.AllDirectories).Single();
        using var state = new State(null, null, database);
        var health = state.ReadColumn("SELECT code || ':' || key FROM health WHERE component = 'flush'");
        var logs = state.ReadColumn("SELECT outcome FROM flush_log WHERE session_id = 'commit-failure'");
        var retries = state.Scalar("SELECT COUNT(*) FROM retry_queue WHERE session_id = 'commit-failure'");

        Assert.True(failed.ExitCode != 0 && health.Any(x => x.Contains("flush-basarisiz:commit-failure", StringComparison.Ordinal))
            && logs.Contains("retry") && retries == 1,
            $"exit={failed.ExitCode}; health={string.Join(',', health)}; logs={string.Join(',', logs)}; retries={retries}; stderr={failed.Stderr}");
    }

    private static Session Session(string id, int count) => new(id, "claude",
        [.. Enumerable.Range(0, count).Select(i => new Turn(i, i % 2 == 0 ? "user" : "assistant", "text",
            $"turn-{i:D2} " + new string('x', 40), Today.AddMinutes(i)))], Today);

    private static string WriteTranscript(string root, string id, int count)
    {
        var path = Path.Combine(root, id + ".jsonl");
        var lines = Enumerable.Range(0, count).Select(i => JsonSerializer.Serialize(new
        {
            sessionId = id,
            type = i % 2 == 0 ? "user" : "assistant",
            timestamp = Today.AddMinutes(i).ToString("O", CultureInfo.InvariantCulture),
            message = new { role = i % 2 == 0 ? "user" : "assistant", content = $"turn-{i}" }
        }));
        File.WriteAllText(path, string.Join('\n', lines));
        return path;
    }

    private sealed class LockFixture : IDisposable
    {
        private readonly string root = Path.Combine(AppContext.BaseDirectory, "kabul-runs", "lock-" + Guid.NewGuid().ToString("N"));
        private readonly string database;
        public string Vault { get; }

        public LockFixture()
        {
            Vault = Path.Combine(root, "vault");
            Directory.CreateDirectory(Path.Combine(Vault, "daily"));
            database = Path.Combine(root, "state", "state.db");
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        }

        public State OpenState() => new(null, null, database);

        public Flush Flush(State state, IProcessRunner process) => new(
            new FlushOptions(VaultPath: Vault, RejectionPath: state.WorkDirectory, MaxCharacters: 100_000), null,
            new Runner(new RunnerProfile(Vault, new ClaudeSettings("claude-haiku-4-5-20251001", "claude-sonnet-5"), state.WorkDirectory), process), null, state);

        public string SeedDaily()
        {
            var path = Path.Combine(Vault, "daily", "2026-09-28.md");
            File.WriteAllText(path, "# Günlük\n");
            return path;
        }

        public IReadOnlyList<(int Start, int End)> Anchors(string id) =>
            [.. Directory.EnumerateFiles(Path.Combine(Vault, "daily"), "*.md")
                .SelectMany(file => Anchor.Matches(File.ReadAllText(file)))
                .Where(match => match.Groups["id"].Value == id)
                .Select(match => (int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture)))];

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private sealed class CountingSummaryRunner(int delayMilliseconds = 0) : IProcessRunner
    {
        private int summaryCalls;
        public int SummaryCalls => Volatile.Read(ref summaryCalls);

        public ProcessResult Run(ProcessRequest request, TimeSpan timeout)
        {
            if (request.Arguments.Contains("stream-json"))
                return new ProcessResult(0, "{\"type\":\"system\",\"subtype\":\"init\",\"tools\":[],\"mcp_servers\":[],\"plugins\":[]}\n{\"type\":\"result\",\"is_error\":false,\"result\":\"ok\"}", string.Empty, true);
            Interlocked.Increment(ref summaryCalls);
            if (delayMilliseconds > 0)
                Thread.Sleep(delayMilliseconds);
            return new ProcessResult(0, JsonSerializer.Serialize(new { result = FakeClaudeResponse.ValidSummaryText }), string.Empty, true);
        }
    }
}
