using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oom.Contracts;

namespace Oom.Tests.Kabul;

/// <summary>
/// L7c-flush-sonnet: behaviour tests for the third-family review of the flush/sweep/save
/// drain code at eb19f02 (bunny-inceleme-flush-eb19f02.md, findings #1-#10), read against
/// SPEC-3.1.0.md R29-R32 and the new R35/R36. Each test proves ONE finding — see the
/// per-test DisplayName for the finding number — and was verified to FAIL against the
/// unchanged eb19f02 code before its corresponding fix landed (see the lane report).
/// </summary>
public sealed class FlushSweepHardeningKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = new(2026, 9, 28, 12, 0, 0, TimeSpan.FromHours(3));

    // ------------------------------------------------------------------
    // #5 / R35 — legacy 3.0.x sweep_stamps vocabulary
    // ------------------------------------------------------------------

    [Fact(DisplayName = "R35/#5 · 3.0.x sweep_stamps sözlüğü normalize edilip kapsamaya doğru yansır")]
    public void LegacyStampVocabulary_NormalizesToCurrentCoverageMeaning()
    {
        using var fixture = new SweepFixture("legacy-stamps");
        // R35's exact mapping: ok/no-new-turns/bos/refused -> ok; no-turns -> skipped
        // (excluded from the denominator entirely); retry/parked/locked -> partial;
        // unreadable -> unreadable; every one of those (bar 'skipped') stays IN the
        // denominator as uncovered.
        string[] legacy = ["ok", "no-new-turns", "bos", "refused", "no-turns", "retry", "parked", "locked", "unreadable"];
        foreach (var outcome in legacy)
            fixture.SeedStampedFile(outcome);

        var report = fixture.Sweep().Execute(dryRun: false);

        // 9 files seeded, 1 ('no-turns' -> skipped) excluded -> total 8; covered = the 4
        // that normalise to 'ok' (ok, no-new-turns, bos, refused).
        Assert.True(report.Result.Total == 8 && report.Result.Covered == 4,
            $"total={report.Result.Total}; covered={report.Result.Covered}; uncovered={string.Join(',', report.Result.UncoveredIds)}");
    }

    // ------------------------------------------------------------------
    // #6 / R36 — 7-day coverage denominator = every candidate in the window
    // ------------------------------------------------------------------

    [Fact(DisplayName = "R36/#6 · runner uyumsuzluğu ile erken kesilen tarama, ulaşılamayan adayları da kapsanmamış sayar")]
    public void EarlyBreakDuringSweep_StillCountsUnreachedRecentCandidatesAsUncovered()
    {
        using var fixture = new SweepFixture("early-break");
        var now = DateTimeOffset.Now;
        string[] ids = ["break-0", "break-1", "break-2", "break-3", "break-4"];
        for (var i = 0; i < ids.Length; i++)
        {
            var path = fixture.WriteTranscript(ids[i], turns: 4);
            // Descending mtime so Discover()'s OrderByDescending processes break-0 FIRST —
            // that is the one candidate whose smoke check the IncompatibleRunner fails,
            // triggering SweepRun's early `break`; break-1..4 are never even visited.
            File.SetLastWriteTime(path, now.AddMinutes(-i).LocalDateTime);
        }

        fixture.Sweep(new IncompatibleRunner()).Execute(dryRun: false);
        var coverage = fixture.State.ReadCoverage();

        // All 5 files are within the 7-day window; only break-0 was ever reconciled
        // (as 'partial', since its ProcessRange failed on the smoke check). The other 4
        // were never touched at all — they must still count as uncovered, not vanish from
        // the denominator the early `break` leaves them out of.
        Assert.True(coverage is not null && coverage.Total == 5 && coverage.Covered == 0,
            $"total={coverage?.Total}; covered={coverage?.Covered}");
    }

    // ------------------------------------------------------------------
    // #3 / R36 — Locked is a failure outcome and counts as uncovered (sweep side)
    // ------------------------------------------------------------------

    [Fact(DisplayName = "R36/#3 · kilitli aday tarama kapsamından düşmez, kapsanmamış sayılır")]
    public void LockedCandidate_DuringSweep_CountsAsUncoveredNotVanished()
    {
        using var fixture = new SweepFixture("locked-coverage");
        fixture.WriteTranscript("locked-session", turns: 4);
        // Holds the exact lock file SessionLock.TryAcquire will contend for, forcing the
        // sweep's own flush of this candidate to time out (10 s) and return Locked.
        using var heldLock = HoldSessionLock(fixture.State.WorkDirectory, "locked-session");

        var report = fixture.Sweep(new SummaryRunner()).Execute(dryRun: false);

        Assert.True(report.Result.Total == 1 && report.Result.Covered == 0 && report.Result.UncoveredIds.Contains("locked-session"),
            $"total={report.Result.Total}; covered={report.Result.Covered}; uncovered={string.Join(',', report.Result.UncoveredIds)}");
    }

    // ------------------------------------------------------------------
    // #4 / R36 — a sweep that covers nothing (all unreadable) exits 2
    // ------------------------------------------------------------------

    [Fact(DisplayName = "R36/#4 · tüm adayları okunamayan tarama exit 2 döner (Results boş kalsa da)")]
    public void ManualSweep_AllCandidatesUnreadable_ExitsTwo()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        var root = harness.NewScratchDirectory("all-unreadable-root");
        WriteSweepSettings(vault, [root]);
        for (var i = 0; i < 4; i++)
            File.WriteAllText(Path.Combine(root, $"bad-{i}.jsonl"), "{\"type\":\"unknown\",\"payload\":\"x\"}\n", Utf8);

        var result = harness.Run(vault, ["sweep"], fakeNow: Today, timeout: TimeSpan.FromSeconds(20));

        Assert.True(result.ExitCode == 2, $"exit={result.ExitCode}; stdout={result.Stdout}; stderr={result.Stderr}");
    }

    // ------------------------------------------------------------------
    // #1 / R36 — Locked is a manual-CLI failure outcome (flush side)
    // ------------------------------------------------------------------

    [Fact(DisplayName = "R36/#1 · kilitli oturumda manuel flush exit 2 döner (şimdi 0 dönmüyor)")]
    public void ManualFlush_LockedOutcome_ExitsTwo()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        // Materialises state.db under the isolated LocalAppData without needing a model.
        // An EXPLICIT empty scratch root is used (not the settings default): the default
        // roots expand %USERPROFILE% from the real process environment variable, not the
        // harness's OOM_USERPROFILE override, so an unconfigured sweep here would scan
        // this machine's actual ~/.claude/projects — slow, and not isolated.
        WriteSweepSettings(vault, [harness.NewScratchDirectory("empty-root")]);
        harness.Run(vault, ["sweep"], fakeNow: Today, timeout: TimeSpan.FromSeconds(20));
        var database = Directory.EnumerateFiles(harness.LocalAppData, "state.db", SearchOption.AllDirectories).Single();
        var stateDirectory = Path.GetDirectoryName(database)!;

        using var heldLock = HoldSessionLock(stateDirectory, "flush-locked-session");
        var result = harness.Run(vault,
            ["flush", "--detached", "--session", "flush-locked-session", "--transcript", "unused.jsonl", "--reason", "sessionend"],
            fakeNow: Today, timeout: TimeSpan.FromSeconds(20));

        Assert.True(result.ExitCode == 2, $"exit={result.ExitCode}; stdout={result.Stdout}; stderr={result.Stderr}");
    }

    // ------------------------------------------------------------------
    // #7 / R36 — save --session-json never leaves a retry row pointing at its own
    // deleted temp file
    // ------------------------------------------------------------------

    [Fact(DisplayName = "R36/#7 · save --session-json retry satırı kendi sildiği geçici transkripte işaret etmez")]
    public void SaveSessionJson_RetryRow_NeverPointsAtItsOwnDeletedTempFile()
    {
        using var fixture = new DirectFixture("save-retry-temp");
        var session = SmallSession("save-retry-session", 4);
        var json = JsonSerializer.Serialize(session);
        var flush = new Flush(new FlushOptions(VaultPath: fixture.Vault, RejectionPath: fixture.State.WorkDirectory), null,
            fixture.Runner(new FailureRunner("boom, olağan bir çalışma zamanı hatası")), null, fixture.State);
        var save = new Save(flush: flush);

        var result = save.SaveSessionJson(json);
        var row = fixture.State.ReadSessionRow(session.Id);

        Assert.True(result.Outcome == FlushOutcome.Retry
            && row is not null && !string.IsNullOrEmpty(row.TranscriptPath) && File.Exists(row.TranscriptPath)
            && fixture.State.Scalar($"SELECT COUNT(*) FROM retry_queue WHERE session_id = '{session.Id}'") == 1,
            $"outcome={result.Outcome}; transcriptPath={row?.TranscriptPath}; " +
            $"exists={(row?.TranscriptPath is { } p && File.Exists(p))}");
    }

    // ------------------------------------------------------------------
    // #2 / R36 — an early return mid-drain still reports the aggregate of ranges
    // already committed
    // ------------------------------------------------------------------

    [Fact(DisplayName = "R36/#2 · sürüklemenin ortasındaki erken dönüş, önceden işlenmiş aralıkların toplamını taşır")]
    public void EarlyReturnMidDrain_CarriesForwardAggregatedRangeTotals()
    {
        using var fixture = new DirectFixture("mid-drain-early-return");
        // 62 turns, sliceTurns=30, minTurns=3: ranges [0-29] and [30-59] commit Ok, then
        // [60-61] (2 turns) is below MinTurns -> NoTurns, in the SAME invocation.
        var session = SlicedSession("mid-drain-session", 62);
        var flush = new Flush(new FlushOptions(MinTurns: 3, VaultPath: fixture.Vault, RejectionPath: fixture.State.WorkDirectory,
            Mode: "dilim", SliceTurns: 30), null, fixture.Runner(new SummaryRunner()), null, fixture.State);

        var result = flush.FlushSession(session, string.Empty, FlushReason.SessionEnd);

        Assert.True(result.Outcome == FlushOutcome.NoTurns && result.Turns == 60 && result.DailyPath is not null
            && result.Summary is { Length: > 0 } && result.Summary.Contains("\n---\n", StringComparison.Ordinal),
            $"outcome={result.Outcome}; turns={result.Turns}; dailyPath={result.DailyPath}; summaryLen={result.Summary?.Length ?? -1}");
    }

    // ------------------------------------------------------------------
    // #8 / R36 — a capacity-hit drain leaves a distinguishable marker on the hook path
    // ------------------------------------------------------------------

    [Fact(DisplayName = "R36/#8 · MaxRangesPerInvocation kapasitesini dolduran flush ayırt edici bir iz bırakır")]
    public void CapHit_WithMoreWorkRemaining_LeavesDistinctHealthMarker()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        // sliceTurns=1, minTurns=1: 25 pending turns need 25 one-turn ranges; the 20-range
        // cap is hit after 20 commits with 5 turns still pending.
        WriteFlushSettings(vault, mode: "dilim", sliceTurns: 1, minTurns: 1);
        var transcript = WriteTranscript(harness.Root, "cap-hit-session", 25);
        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var path = fake.ReplaceProcessPath();

        var result = harness.Run(vault,
            ["flush", "--detached", "--session", "cap-hit-session", "--transcript", transcript, "--reason", "sessionend"],
            fakeNow: Today, timeout: TimeSpan.FromSeconds(30));
        var database = Directory.EnumerateFiles(harness.LocalAppData, "state.db", SearchOption.AllDirectories).Single();
        using var state = new State(null, null, database);
        var health = state.ReadColumn("SELECT code || ':' || detail FROM health WHERE component = 'flush' AND key = 'cap-hit-session'");

        Assert.True(result.ExitCode == 0 && health.Any(row => row.Contains("kismi", StringComparison.OrdinalIgnoreCase)),
            $"exit={result.ExitCode}; health={string.Join(" | ", health)}; stdout={result.Stdout}; stderr={result.Stderr}");
    }

    // ------------------------------------------------------------------
    // #9 / R31 — UnauthorizedAccessException on the atomic daily write is queued, not thrown
    // ------------------------------------------------------------------

    [Fact(DisplayName = "R31/#9 · günlük dosyası UnauthorizedAccessException fırlatınca çökmez, retry'e düşer")]
    public void DailyWriteUnauthorizedAccess_IsQueuedForRetry_NotThrown()
    {
        using var fixture = new DirectFixture("unauthorized-daily");
        var daily = Path.Combine(fixture.Vault, "daily", $"{Today:yyyy-MM-dd}.md");
        File.WriteAllText(daily, "# Günlük\n");
        File.SetAttributes(daily, FileAttributes.ReadOnly);
        try
        {
            var flush = new Flush(new FlushOptions(VaultPath: fixture.Vault, RejectionPath: fixture.State.WorkDirectory), null,
                fixture.Runner(new SummaryRunner()), null, fixture.State);

            var result = flush.FlushSession(SmallSession("readonly-daily-session", 4), string.Empty, FlushReason.SessionEnd);

            Assert.True(result.Outcome == FlushOutcome.Retry
                && fixture.State.Scalar("SELECT COUNT(*) FROM retry_queue WHERE session_id = 'readonly-daily-session'") == 1,
                $"outcome={result.Outcome}");
        }
        finally
        {
            File.SetAttributes(daily, FileAttributes.Normal);
        }
    }

    // ------------------------------------------------------------------
    // #10 / R31 — SessionLock directory-creation failure returns a clean outcome
    // ------------------------------------------------------------------

    [Fact(DisplayName = "R31/#10 · kilit dizini oluşturulamayınca çökme yerine Locked döner")]
    public void SessionLockDirectoryBlocked_ReturnsCleanOutcome_NotCrash()
    {
        using var fixture = new DirectFixture("lockdir-blocked");
        // "locks" already exists as a FILE (not a directory) under the state dir, so
        // SessionLock's own Directory.CreateDirectory(stateDir/"locks") throws — the same
        // failure FAMILY (IOException/UnauthorizedAccessException) a real ACL-denied
        // profile hits, reproduced here without needing to change filesystem permissions.
        File.WriteAllText(Path.Combine(fixture.State.WorkDirectory, "locks"), "blocker");
        var flush = new Flush(new FlushOptions(VaultPath: fixture.Vault, RejectionPath: fixture.State.WorkDirectory), null,
            fixture.Runner(new SummaryRunner()), null, fixture.State);

        var result = flush.FlushSession(SmallSession("lockdir-blocked-session", 4), string.Empty, FlushReason.SessionEnd);

        Assert.Equal(FlushOutcome.Locked, result.Outcome);
    }

    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    private static string BuildVault(string root)
    {
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts"));
        return vault;
    }

    private static void WriteSweepSettings(string vault, IReadOnlyList<string> roots)
    {
        Directory.CreateDirectory(Path.Combine(vault, ".oom"));
        File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"), JsonSerializer.Serialize(new
        {
            sweep = new { roots, maxSessionsPerRun = 20, minTurns = 3 },
            flush = new { mode = "dilim", sliceTurns = 30 }
        }), Utf8);
    }

    private static void WriteFlushSettings(string vault, string mode, int sliceTurns, int minTurns)
    {
        Directory.CreateDirectory(Path.Combine(vault, ".oom"));
        File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"), JsonSerializer.Serialize(new
        {
            sweep = new { roots = Array.Empty<string>(), maxSessionsPerRun = 20, minTurns },
            flush = new { mode, sliceTurns }
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
            message = new { role = i % 2 == 0 ? "user" : "assistant", content = $"turn-{i:D2}" }
        }));
        File.WriteAllText(path, string.Join('\n', lines), Utf8);
        return path;
    }

    /// <summary>Small (short-payload) session for tests that only need a plannable range,
    /// never a specific character budget — turn timestamps use the fixed <see cref="Today"/>
    /// so a Commit()'s daily file name is deterministic regardless of wall-clock time.</summary>
    private static Session SmallSession(string id, int count) => new(id, "claude",
        [.. Enumerable.Range(0, count).Select(i => new Turn(i, i % 2 == 0 ? "user" : "assistant", "text", $"turn-{i}", Today.AddMinutes(i)))], Today);

    /// <summary>Large-payload session for slice-mode tests: ~520 chars/turn keeps a
    /// 30-turn slice under FlushOptions.LocalMaxCharacters (24,000) while still being
    /// large enough to matter.</summary>
    private static Session SlicedSession(string id, int count) => new(id, "claude",
        [.. Enumerable.Range(0, count).Select(i => new Turn(i, i % 2 == 0 ? "user" : "assistant", "text",
            $"turn-{i:D2}-payload " + new string((char)('a' + i % 26), 500), Today.AddMinutes(i)))], Today);

    /// <summary>Replicates SessionLock's own path derivation (state dir + SHA256(sessionId)
    /// hex[..16], under "locks") and holds the file exclusively, so a real flush of that
    /// session id contends for the SAME lock SessionLock.TryAcquire would, and — after its
    /// 10 s timeout — returns FlushOutcome.Locked exactly the way a genuinely busy
    /// concurrent flush would.</summary>
    private static FileStream HoldSessionLock(string stateDirectory, string sessionId)
    {
        var directory = Path.Combine(stateDirectory, "locks");
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(sessionId)))[..16];
        var path = Path.Combine(directory, "session-" + key + ".lock");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private sealed class DirectFixture : IDisposable
    {
        private readonly string root;
        public string Vault { get; }
        public State State { get; }

        public DirectFixture(string name)
        {
            root = Path.Combine(AppContext.BaseDirectory, "kabul-runs", name + "-" + Guid.NewGuid().ToString("N"));
            Vault = Path.Combine(root, "vault");
            Directory.CreateDirectory(Path.Combine(Vault, "daily"));
            Directory.CreateDirectory(Path.Combine(Vault, "knowledge", "concepts"));
            var stateDir = Path.Combine(root, "state");
            Directory.CreateDirectory(stateDir);
            State = new State(null, null, Path.Combine(stateDir, "state.db"));
        }

        public Runner Runner(IProcessRunner process) => new(
            new RunnerProfile(Vault, new ClaudeSettings("claude-haiku-4-5-20251001", "claude-sonnet-5"), State.WorkDirectory), process);

        public void Dispose()
        {
            State.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class SweepFixture : IDisposable
    {
        private readonly string root;
        public string Vault { get; }
        public string SweepRoot { get; }
        public State State { get; }
        public OomSettings Settings { get; }

        public SweepFixture(string name)
        {
            root = Path.Combine(AppContext.BaseDirectory, "kabul-runs", name + "-" + Guid.NewGuid().ToString("N"));
            Vault = Path.Combine(root, "vault");
            Directory.CreateDirectory(Path.Combine(Vault, "daily"));
            SweepRoot = Path.Combine(root, "sweep-root");
            Directory.CreateDirectory(SweepRoot);
            var stateDir = Path.Combine(root, "state");
            Directory.CreateDirectory(stateDir);
            State = new State(null, null, Path.Combine(stateDir, "state.db"));
            Settings = OomSettings.Defaults(Vault) with { Sweep = new SweepSettings(3, 20, [SweepRoot]) };
        }

        public SweepRun Sweep(IProcessRunner? runner = null)
        {
            var flush = new Flush(new FlushOptions(MinTurns: Settings.Sweep.MinTurns, VaultPath: Vault, RejectionPath: State.WorkDirectory), null,
                new Runner(new RunnerProfile(Vault, new ClaudeSettings("claude-haiku-4-5-20251001", "claude-sonnet-5"), State.WorkDirectory),
                    runner ?? new SummaryRunner()),
                null, State);
            return new SweepRun(Vault, Settings, flush, State);
        }

        /// <summary>Writes a non-empty (but never actually parsed) transcript file and a
        /// matching sweep_stamps row with <paramref name="outcome"/> — matching mtime/size
        /// so SweepRun.Execute's FAST (unchanged-file) path reads the stamp instead of
        /// re-flushing.</summary>
        public string SeedStampedFile(string outcome)
        {
            var path = Path.Combine(SweepRoot, outcome + ".jsonl");
            File.WriteAllText(path, "{\"type\":\"unknown\"}\n", Utf8);
            var info = new FileInfo(path);
            var mtime = ((DateTimeOffset)info.LastWriteTime).ToString("O", CultureInfo.InvariantCulture);
            State.WriteStamp(path, mtime, info.Length, outcome);
            return path;
        }

        public string WriteTranscript(string id, int turns)
        {
            var path = Path.Combine(SweepRoot, id + ".jsonl");
            var now = DateTimeOffset.Now;
            var lines = Enumerable.Range(0, turns).Select(i => JsonSerializer.Serialize(new
            {
                sessionId = id,
                type = i % 2 == 0 ? "user" : "assistant",
                timestamp = now.AddMinutes(i).ToString("O", CultureInfo.InvariantCulture),
                message = new { role = i % 2 == 0 ? "user" : "assistant", content = $"turn-{i:D2} payload text" }
            }));
            File.WriteAllText(path, string.Join('\n', lines), Utf8);
            return path;
        }

        public void Dispose()
        {
            State.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class SummaryRunner : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request, TimeSpan timeout) => request.Arguments.Contains("stream-json")
            ? new ProcessResult(0, "{\"type\":\"system\",\"subtype\":\"init\",\"tools\":[],\"mcp_servers\":[],\"plugins\":[]}\n{\"type\":\"result\",\"is_error\":false,\"result\":\"ok\"}", string.Empty, true)
            : new ProcessResult(0, JsonSerializer.Serialize(new { result = FakeClaudeResponse.ValidSummaryText }), string.Empty, true);
    }

    private sealed class FailureRunner(string error) : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request, TimeSpan timeout) => request.Arguments.Contains("stream-json")
            ? new SummaryRunner().Run(request, timeout)
            : new ProcessResult(1, error, error, true);
    }

    private sealed class IncompatibleRunner : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request, TimeSpan timeout) =>
            new(0, "{\"type\":\"system\",\"subtype\":\"init\",\"tools\":[],\"mcp_servers\":[{\"name\":\"x\"}],\"plugins\":[]}\n{\"type\":\"result\",\"is_error\":false}", string.Empty, true);
    }
}
