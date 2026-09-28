using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class RunnerScars
{
    [Fact(DisplayName = "Y-011 · Claude runner kullanıcının kendi oturumuyla, vault dışından koşar; biçimsiz yanıt fallback'e gider")]
    public void Y011_ClaudeRunnerIsIsolatedAndFallsBack()
    {
        var request = new Runner().BuildClaudeRequest("özetle", "claude-haiku-4-5-20251001", @"D:\vault");
        Assert.False(request.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.Equal("oom", request.Environment["OOM_INVOKED_BY"]);
        Assert.DoesNotContain(@"D:\vault", request.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        var validation = new Flush().ValidateSummary("<ExitPlanMode/>\n" + ScarFixture.ValidSummary(), "isolated");
        Assert.False(validation.Accepted);
        Assert.NotNull(validation.RejectionPath);
    }

    [Fact(DisplayName = "Y-018 · Model takma adı reddedilir, gerçek model modelUsage'dan kaydedilir")]
    public void Y018_ModelIdMustBeExactAndRecordedFromResponse()
    {
        // configured: false pins the unconfigured path explicitly instead of depending on
        // the process-wide VaultPaths override another test may have left behind; that
        // override would make this runner spawn the machine's real claude.
        var runner = new Runner(null, configured: false);
        Assert.Throws<ArgumentException>(() => runner.BuildClaudeRequest("x", "haiku", "vault", "isolated"));
        var result = runner.Run("x", ModelTier.Fast, ComponentKind.Flush, "summary");
        Assert.Equal("claude-haiku-4-5-20251001", result.Model);
    }

    [Fact(DisplayName = "Y-312 · claude sıfırdan farklı çıkarsa red dosyası stderr'i ve stdout'un ilk 500 karakterini tutar")]
    public void Y312_RejectionFileKeepsStandardErrorAndOutput()
    {
        var directory = ScarFixture.TempDirectory();
        try
        {
            // The smoke-cache state dir is injected through the profile (SPEC R16), so this
            // test never mutates the process-wide OOM_LOCALAPPDATA and never writes under
            // the real machine's %LOCALAPPDATA%\oom. The smoke call passes, so this pins the
            // SUMMARIZER failure path (CallClaude), not the smoke-failure path.
            var processes = new Y312ProcessRunner();
            var runner = new Runner(new RunnerProfile(directory, new ClaudeSettings("claude-haiku-4-5-20251001", "claude-sonnet-5"),
                    Path.Combine(directory, "state")),
                processes);
            var attempt = runner.Run("istem", ModelTier.Smart, ComponentKind.Compile, "concepts");
            Assert.Equal(1, processes.SmokeCalls);
            Assert.Equal(1, processes.SummaryCalls);
            Assert.Equal(string.Empty, attempt.Text);
            Assert.NotNull(attempt.Error);
            Assert.False(attempt.Error!.StartsWith(Runner.IncompatibleMarker, StringComparison.Ordinal),
                "özetleyici hatası 'runner uyumsuz' ile başlamamalı: " + attempt.Error);
            Assert.Contains("kimlik doğrulama başarısız", attempt.Error!, StringComparison.Ordinal);
            Assert.Contains(new string('g', 500), attempt.Error!, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('g', 501), attempt.Error!, StringComparison.Ordinal);

            var flush = new Flush(new FlushOptions(RejectionPath: directory));
            var record = flush.Retry("oturum-312", 0, attempt.Error!);
            Assert.Equal(1, record.Attempts);
            var red = Directory.EnumerateFiles(Path.Combine(directory, "red")).Single();
            var text = File.ReadAllText(red);
            Assert.Contains("kimlik doğrulama başarısız", text, StringComparison.Ordinal);
            Assert.Contains("stdout:", text, StringComparison.Ordinal);
        }
        finally { ScarFixture.Remove(directory); }
    }

    [Fact(DisplayName = "R16 · Başarısız duman kararı önbelleğe yazılmaz; sonraki çağrı yeniden dener, başarı enjekte edilen durum dizinine yazılır")]
    public void R16_FailedSmokeVerdictIsNotCached_PassIsCachedInInjectedStateDirectory()
    {
        var directory = ScarFixture.TempDirectory();
        try
        {
            var state = Path.Combine(directory, "state");
            var processes = new SmokeScriptedProcessRunner { SmokePasses = false };
            var runner = new Runner(new RunnerProfile(directory, new ClaudeSettings("claude-haiku-4-5-20251001", "claude-sonnet-5"), state),
                processes);

            var first = runner.Run("istem", ModelTier.Fast, ComponentKind.Flush, "summary");
            var second = runner.Run("istem", ModelTier.Fast, ComponentKind.Flush, "summary");
            Assert.Contains("runner uyumsuz", first.Error ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("runner uyumsuz", second.Error ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(2, processes.SmokeCalls);
            Assert.Equal(0, processes.SummaryCalls);
            Assert.Empty(SmokeCacheFiles(state));

            // The login is fixed: the very next flush must probe again and proceed, not
            // replay a cached failure for a day.
            processes.SmokePasses = true;
            var third = runner.Run("istem", ModelTier.Fast, ComponentKind.Flush, "summary");
            Assert.Null(third.Error);
            Assert.Equal("özet", third.Text);
            Assert.Equal(3, processes.SmokeCalls);
            Assert.Single(SmokeCacheFiles(state));

            var fourth = runner.Run("istem", ModelTier.Fast, ComponentKind.Flush, "summary");
            Assert.Null(fourth.Error);
            Assert.Equal(3, processes.SmokeCalls);
            Assert.Equal(2, processes.SummaryCalls);
        }
        finally { ScarFixture.Remove(directory); }
    }

    private sealed class SmokeScriptedProcessRunner : IProcessRunner
    {
        public bool SmokePasses { get; set; }
        public int SmokeCalls { get; private set; }
        public int SummaryCalls { get; private set; }

        public ProcessResult Run(ProcessRequest request, TimeSpan timeout)
        {
            if (request.Arguments.Contains("stream-json"))
            {
                SmokeCalls++;
                return SmokePasses
                    ? new ProcessResult(0,
                        "{\"type\":\"system\",\"subtype\":\"init\",\"tools\":[],\"mcp_servers\":[],\"plugins\":[]}\n" +
                        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\"}\n",
                        string.Empty, true)
                    : new ProcessResult(1, string.Empty, "Not logged in", true);
            }

            SummaryCalls++;
            return new ProcessResult(0,
                "{\"result\":\"özet\",\"modelUsage\":{\"claude-haiku-4-5-20251001\":{\"inputTokens\":1,\"outputTokens\":1}}}",
                string.Empty, true);
        }
    }

    /// <summary>Failed smoke verdicts are never cached; a pass lands in the injected dir only.</summary>
    private static string[] SmokeCacheFiles(string state) =>
        Directory.Exists(state) ? Directory.GetFiles(state, "runner-smoke*.json") : [];

    /// <summary>Answers the stream-json smoke call cleanly and fails only the summarize call.</summary>
    private sealed class Y312ProcessRunner : IProcessRunner
    {
        public int SmokeCalls { get; private set; }
        public int SummaryCalls { get; private set; }

        public ProcessResult Run(ProcessRequest request, TimeSpan timeout)
        {
            if (request.Arguments.Contains("stream-json"))
            {
                SmokeCalls++;
                return new ProcessResult(0,
                    "{\"type\":\"system\",\"subtype\":\"init\",\"tools\":[],\"mcp_servers\":[],\"plugins\":[]}\n" +
                    "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\"}\n",
                    string.Empty, true);
            }

            SummaryCalls++;
            return new ProcessResult(1, new string('g', 900), "kimlik doğrulama başarısız", true);
        }
    }
}
