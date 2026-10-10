using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Kabul.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// Acceptance oracle for lane L3-doctor (SPEC-3.1.0.md F5-1..F5-4, S4, S7, NB-9, NB-8, F3-4,
/// R14, R5; Cut: ToolHealth running codebase-memory-mcp/agent-reach). Written independently
/// of the implementation by the lane's oracle author — the implementer never sees this file's
/// reasoning, only the assertions below. Drives a real oom.exe through <see cref="KabulHarness"/>
/// against small, synthetic, purpose-built vaults (this repo is public, so no real personal
/// content), following CompileKabul's precedent of NOT reusing <c>KabulVaultBuilder</c> (that
/// fixture shapes a vault for the F1 context-budget lane's hand layer, not for doctor's
/// flush_log/coverage/daily_ingest bookkeeping).
///
/// Numbering below follows the lane brief's own 1-9 oracle list. Every test seeds
/// flush_log/coverage/daily_ingest rows directly through <see cref="State"/> (the SAME public
/// API the product code itself writes through — RecordFlush, RecordCoverage, WriteDailyIngest —
/// not raw SQL), at the exact on-disk path the child oom.exe process will open
/// (<c>KabulHarness.LocalAppData/oom/&lt;hash of the vault path&gt;/state.db</c>, the same
/// convention FlushKabul.StateDbPath already established), then runs the real exe against that
/// seeded database. This is the "unit-level fixture setup, process-boundary behaviour check"
/// pattern the driver's own R16/R3 notes already sanction (state seeded directly, behaviour
/// observed only through the CLI).
///
/// MEASURED against this tree (current, pre-fix code) before writing these assertions — every
/// test below is RED here for the reason its comment states, not from a compile error:
///   - #1: Program.Doctor.cs's rejection-rate query has no date filter at all (flush_log WHERE
///     outcome <> 'summary' / outcome IN ('retry','parked') — no ts predicate), so it reports
///     an ALL-TIME ratio, not a 7-day one. Measured: 100 old-retry fixture → "Son 7 gün ret:
///     %100,0" (not the spec's "%0,0"); 3-in-49-windowed-plus-50-old fixture → "Son 7 gün ret:
///     %3,0" (not "%6,1").
///   - #2: State.LastCoverage() (State.cs) regex-parses the 'kapsama' health-table STRING, not
///     the numeric `coverage` table RecordCoverage/ReadCoverage already write/read (L2-flush).
///     A `coverage` row with no matching 'kapsama' health string yields coverage=NaN → "Son 7
///     gün kapsama ölçülmedi", never an age suffix.
///   - #3/#4: Program.Doctor.cs's Health() has no 'son tarama'/'son derleme' lines at all, and
///     `return 0` on every branch (lines 22, 30, 38) regardless of any Error-level item;
///     Doctor.ToJson hardcodes `exit_code = 0` (Doctor.cs:172) unconditionally.
///   - #5: Health() prints every item unconditionally in the default view (only `--quiet`
///     filters, to a different stderr shape) and recognises no `--all` flag; kit rows always
///     show. Measured: plain `oom doctor` on a from-scratch vault already prints a "kit …
///     bilgi … kit-yok … kit yok" row and no "N yeşil" summary line.
///   - #6: Measured directly (see this file's own DoctorKabul proof run): with fake
///     codebase-memory-mcp.cmd/agent-reach.cmd shims on PATH, `oom doctor` invokes BOTH —
///     Program.cs's ToolHealthRows() is called unconditionally from Health() (Program.Doctor.cs:16-18).
///   - #7: 'refused' is excluded from Program.Doctor.cs's own `rejected` query (only
///     'retry'/'parked' are counted) and from every other row; two fixtures differing only in
///     their refused-row count produce byte-identical doctor output (arac/kit lines filtered
///     out) on this tree.
///   - #8: Measured directly: making a provisioned state.db file read-only and running `oom
///     flush --detached` against it does not print "health yazılamadı" — it crashes the WHOLE
///     command with the raw "hata: SqliteException: attempt to write a readonly database."
///     HealthLedger.Record's own catch (HealthLedger.cs:32-34) never even gets a chance to run;
///     Provision()'s write happens first and is unhandled.
///   - #9: RootMap.LoadConfiguration's catch (RootMap.cs:288-290) silently falls back to
///     "genel" for a corrupt hub-config.json. Measured: both `oom doctor` and
///     `oom compile --dry-run` exit 0 against a syntactically-invalid hub-config.json.
///
/// Also measured RED against eski-exe/oom.exe (OOM_KABUL_EXE) for #1/#2/#3/#4/#6/#9 — see the
/// lane's proof notes; #5/#7/#8 depend on 3.1.0-only surfaces (--all, the refused outcome
/// value, the S7 resilience contract) that eski-exe does not have any equivalent of, so those
/// three are asserted process-boundary against THIS tree only and are covered for eski-exe by
/// the harness's own smoke tests (HarnessSmokeTests.cs) proving eski-exe runs at all.
/// </summary>
public sealed class DoctorKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = new(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(3));

    // ------------------------------------------------------------------
    // #1 — F5-1: the rejection-rate metric is a REAL 7-day window, not an
    // all-time ratio.
    // ------------------------------------------------------------------

    [Fact(DisplayName = "F5-1 #1a · 100 eski (>7 gün) retry, hiç yeni yok → 'Son 7 gün ret: %0,0' (tüm-zaman oranı değil)")]
    public void RejectionRate_AllRetriesOlderThan7Days_ShowsZeroPercentForTheWindow()
    {
        using var harness = new KabulHarness();
        var vault = BuildMinimalVault(harness.Root);
        SeedState(harness, vault, state =>
        {
            for (var i = 0; i < 100; i++)
                state.RecordFlush(Today.AddDays(-10).AddMinutes(-i), $"old-retry-{i}", "sweep", "retry", 4, 400, "extractive");
        });

        var result = harness.Run(vault, ["doctor"], fakeNow: Today);

        Assert.Contains("Son 7 gün ret: %0,0", result.Stdout, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "F5-1 #1b · son 7 günde 49 flush'tan 3'ü retry (+50 eski ok) → 'Son 7 gün ret: %6,1' (pencere dışı satırlar orana karışmaz)")]
    public void RejectionRate_WindowedThreeOfFortyNine_ShowsWindowedRatio_NotAllTimeRatio()
    {
        using var harness = new KabulHarness();
        var vault = BuildMinimalVault(harness.Root);
        SeedState(harness, vault, state =>
        {
            for (var i = 0; i < 46; i++)
                state.RecordFlush(Today.AddDays(-1).AddMinutes(-i), $"win-ok-{i}", "sweep", "ok", 4, 400, "extractive");
            for (var i = 0; i < 3; i++)
                state.RecordFlush(Today.AddDays(-2).AddMinutes(-i), $"win-retry-{i}", "sweep", "retry", 4, 400, "extractive");
            // Outside the 7-day window on purpose: if the implementation still computes an
            // all-time ratio these change the result (3/99 = %3,0); a correctly windowed
            // implementation must ignore them entirely and still show %6,1.
            for (var i = 0; i < 50; i++)
                state.RecordFlush(Today.AddDays(-30).AddMinutes(-i), $"old-ok-{i}", "sweep", "ok", 4, 400, "extractive");
        });

        var result = harness.Run(vault, ["doctor"], fakeNow: Today);

        Assert.Contains("Son 7 gün ret: %6,1", result.Stdout, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // #2 — F5-2: coverage is read from the NUMERIC `coverage` table
    // (RecordCoverage/ReadCoverage), with the measurement's own age
    // printed next to it — not a health-string regex with no age at all.
    // ------------------------------------------------------------------

    [Fact(DisplayName = "F5-2 #2 · kapsama `coverage` tablosundan okunur, 14 günlük ölçüm '(ölçüm: 14 gün önce)' ile gösterilir")]
    public void Coverage_ReadFromNumericTable_ShowsMeasurementAge()
    {
        using var harness = new KabulHarness();
        var vault = BuildMinimalVault(harness.Root);
        SeedState(harness, vault, state =>
        {
            // Deliberately NO matching health('sweep','kapsama') string row: the OLD path
            // (State.LastCoverage, a health-detail regex) has nothing to parse and must fall
            // back to "ölçülmedi"; only reading the numeric `coverage` row itself can show a
            // real ratio and age here.
            state.RecordCoverage(Today.AddDays(-14), covered: 3, total: 20, windowDays: 7);
        });

        var result = harness.Run(vault, ["doctor"], fakeNow: Today);

        Assert.Contains("(ölçüm: 14 gün önce)", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Son 7 gün kapsama ölçülmedi", result.Stdout, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // #3/#4 — F5-3/F5-4: 'son tarama'/'son derleme'/'bekleyen daily' lines
    // exist and reflect real data; any red (Error-level) row makes the
    // process exit 1, a fully-green fixture exits 0, and `doctor --json`'s
    // own exit_code field always equals the real process exit code.
    // ------------------------------------------------------------------





    // ------------------------------------------------------------------
    // #5 — NB-9: the default view shows only warning/error rows plus an
    // 'N yeşil' summary; kit rows appear only with --all.
    // ------------------------------------------------------------------

    [Fact(DisplayName = "NB-9 #5 · varsayılan çıktı 'kit' satırı basmaz ve 'N yeşil' özeti verir; --all ile kit satırı görünür")]
    public void DefaultView_OmitsKitRows_ShowsGreenSummary_AllFlagRevealsKit()
    {
        using var harness = new KabulHarness();
        var vault = BuildMinimalVault(harness.Root);

        var defaultResult = harness.Run(vault, ["doctor"], fakeNow: Today);
        Assert.DoesNotMatch(new Regex(@"\bkit\b", RegexOptions.IgnoreCase), defaultResult.Stdout);
        Assert.Matches(new Regex(@"\d+\s*ye[şs]il", RegexOptions.IgnoreCase), defaultResult.Stdout);

        var allResult = harness.Run(vault, ["doctor", "--all"], fakeNow: Today);
        Assert.Matches(new Regex(@"\bkit\b", RegexOptions.IgnoreCase), allResult.Stdout);
    }

    // ------------------------------------------------------------------
    // #6 — R14/Cut: doctor calls no other tool. codebase-memory-mcp and
    // agent-reach are never invoked, in any form.
    // ------------------------------------------------------------------

    [Fact(DisplayName = "R14/Cut #6 · PATH'teki sahte codebase-memory-mcp.cmd/agent-reach.cmd `oom doctor` tarafından hiç çalıştırılmaz")]
    public void Doctor_NeverInvokesCodebaseMemoryMcpOrAgentReach_EvenWhenBothAreOnPath()
    {
        using var harness = new KabulHarness();
        var vault = BuildMinimalVault(harness.Root);
        var (toolsDirectory, codebaseMarker, agentReachMarker) = WriteToolHealthMarkerScripts(harness.Root);

        // Full PATH replacement (not a prepend), same technique and same rationale as
        // FakeClaude.ReplaceProcessPath: KabulHarness never sets PATH itself, so the
        // child's PATH is whatever this process's environment holds at the moment
        // KabulHarness.Run builds its ProcessStartInfo — exactly the window this scope
        // covers. Safe because tests/Oom.Tests/xunit.runner.json already disables all
        // class/assembly parallelism (see FakeClaude.cs's own note on this).
        using (ReplacePath(toolsDirectory))
        {
            var result = harness.Run(vault, ["doctor"], fakeNow: Today);
            Assert.False(File.Exists(codebaseMarker),
                $"codebase-memory-mcp.cmd çalıştırılmış (marker var): {codebaseMarker}\nstdout: {result.Stdout}");
            Assert.False(File.Exists(agentReachMarker),
                $"agent-reach.cmd çalıştırılmış (marker var): {agentReachMarker}\nstdout: {result.Stdout}");
        }
    }

    // ------------------------------------------------------------------
    // #7 — S4: flush_log rows with outcome 'refused' produce a separate
    // count, distinct from retry/parked — proven differentially, since
    // the implementer is free to choose the exact wording/placement.
    // ------------------------------------------------------------------

    [Fact(DisplayName = "S4 #7 · outcome='refused' satırları ayrı bir sayaç olarak izlenir (yalnız refused/ok karışımı değişen, toplam ve retry sayısı SABİT iki repertuvar farklı çıktı verir)")]
    public void RefusedFlushRows_AreTrackedSeparately_ChangingOnlyTheRefusedSplitChangesDoctorOutput()
    {
        // Total flush_log rows (21) and retry count (2) are held IDENTICAL between the two
        // fixtures — only the ok/refused split differs (17/2 vs 10/9). This specifically
        // rules out the old code's own rejection-rate query (flush_log WHERE outcome <>
        // 'summary' — which counts 'refused' rows in its DENOMINATOR without ever treating
        // them as rejections) from accidentally making the two outputs differ for a reason
        // that has nothing to do with 'refused' being tracked on its own.
        using var harnessA = new KabulHarness();
        var vaultA = BuildMinimalVault(harnessA.Root);
        SeedState(harnessA, vaultA, state => SeedRefusedFixture(state, okCount: 17, refusedCount: 2));
        var resultA = harnessA.Run(vaultA, ["doctor"], fakeNow: Today);

        using var harnessB = new KabulHarness();
        var vaultB = BuildMinimalVault(harnessB.Root);
        SeedState(harnessB, vaultB, state => SeedRefusedFixture(state, okCount: 10, refusedCount: 9));
        var resultB = harnessB.Run(vaultB, ["doctor"], fakeNow: Today);

        // "arac"/"kit" rows are filtered out because they reflect whatever tools happen to
        // be installed on the machine running the test, not this fixture's data, and would
        // otherwise make this differential check noisy in either direction.
        var filteredA = WithoutToolAndKitLines(resultA.Stdout);
        var filteredB = WithoutToolAndKitLines(resultB.Stdout);

        Assert.NotEqual(filteredA, filteredB);
    }

    private static void SeedRefusedFixture(State state, int okCount, int refusedCount)
    {
        for (var i = 0; i < okCount; i++)
            state.RecordFlush(Today.AddHours(-i), $"ok-{i}", "sweep", "ok", 4, 400, "extractive");
        for (var i = 0; i < 2; i++)
            state.RecordFlush(Today.AddHours(-i), $"retry-{i}", "sweep", "retry", 4, 400, "extractive");
        for (var i = 0; i < refusedCount; i++)
            state.RecordFlush(Today.AddHours(-i), $"refused-{i}", "sweep", "refused", 4, 400, "extractive");
    }

    // ------------------------------------------------------------------
    // #8 — S7: a HealthLedger write failure is surfaced (not swallowed),
    // and persists across process boundaries via something other than
    // the (unwritable) health table itself.
    // ------------------------------------------------------------------



    // ------------------------------------------------------------------
    // #9 — S7: a corrupt hub-config.json is an ERROR (naming the file),
    // never a silent fallback to 'genel', for both doctor and
    // compile --dry-run.
    // ------------------------------------------------------------------



    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    private static void AssertFreshnessLines(string stdout, int? expectHoursAtLeast = null, int? expectHoursAtMost = null, int? expectPending = null)
    {
        var taramaMatch = Regex.Match(stdout, @"son tarama:\s*(\d+)\s*saat önce", RegexOptions.IgnoreCase);
        Assert.True(taramaMatch.Success, $"'son tarama: X saat önce' satırı yok. stdout:\n{stdout}");
        var hours = int.Parse(taramaMatch.Groups[1].Value);
        if (expectHoursAtLeast is { } atLeast)
            Assert.True(hours >= atLeast, $"son tarama {hours} saat önce, >= {atLeast} bekleniyordu. stdout:\n{stdout}");
        if (expectHoursAtMost is { } atMost)
            Assert.True(hours <= atMost, $"son tarama {hours} saat önce, <= {atMost} bekleniyordu. stdout:\n{stdout}");

        Assert.Contains("son derleme:", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("son derleme: hiç", stdout, StringComparison.OrdinalIgnoreCase);

        if (expectPending is { } pending)
        {
            var bekleyenMatch = Regex.Match(stdout, @"bekleyen daily:\s*(\d+)", RegexOptions.IgnoreCase);
            Assert.True(bekleyenMatch.Success, $"'bekleyen daily: N' satırı yok. stdout:\n{stdout}");
            Assert.Equal(pending, int.Parse(bekleyenMatch.Groups[1].Value));
        }
    }

    private static int ReadJsonExitCode(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        return document.RootElement.GetProperty("exit_code").GetInt32();
    }

    // Strips rows whose content is expected to vary for reasons that have nothing to do
    // with what a test is actually comparing: "arac"/"kit" rows reflect whatever tools are
    // installed on the machine running the test, and the state.db "... N bayt" file-size
    // row varies with the exact byte layout SQLite happens to pick for a given fixture's
    // row content (session id string lengths etc.), not with anything semantic.
    private static string WithoutToolAndKitLines(string stdout) =>
        string.Join('\n', stdout.Split('\n').Where(line =>
            !line.TrimStart().StartsWith("arac", StringComparison.OrdinalIgnoreCase) &&
            !line.TrimStart().StartsWith("kit", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("bayt", StringComparison.OrdinalIgnoreCase)));

    private static (string Directory, string CodebaseMemoryMarker, string AgentReachMarker) WriteToolHealthMarkerScripts(string root)
    {
        var directory = Path.Combine(root, "fake-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var codebaseMemoryMarker = Path.Combine(directory, "codebase-memory-mcp.marker");
        var agentReachMarker = Path.Combine(directory, "agent-reach.marker");

        File.WriteAllText(Path.Combine(directory, "codebase-memory-mcp.cmd"),
            "@echo off\r\n>\"" + codebaseMemoryMarker + "\" echo ran\r\nexit /b 0\r\n", Utf8);
        File.WriteAllText(Path.Combine(directory, "agent-reach.cmd"),
            "@echo off\r\n>\"" + agentReachMarker + "\" echo ran\r\nexit /b 0\r\n", Utf8);

        return (directory, codebaseMemoryMarker, agentReachMarker);
    }

    private static IDisposable ReplacePath(string directory)
    {
        var previous = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        Environment.SetEnvironmentVariable("PATH", directory);
        return new RestorePath(previous);
    }

    private sealed class RestorePath(string previous) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable("PATH", previous);
    }

    private static string BuildMinimalVault(string root)
    {
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts"));
        return vault;
    }

    private static void WriteDaily(string vault, string name) =>
        File.WriteAllText(Path.Combine(vault, "daily", name),
            $"# {Path.GetFileNameWithoutExtension(name)}\n\n- sentetik günlük satırı, kabul harness fixture'ı, gerçek içerik değil.\n", Utf8);

    private static void SeedState(KabulHarness harness, string vault, Action<State> seed)
    {
        var dbPath = StateDbPath(harness, vault);
        using (var state = new State(null, null, dbPath))
            seed(state);
        SqliteConnection.ClearAllPools();
    }

    // Same convention as FlushKabul.StateDbPath: the exact on-disk path the child oom.exe
    // process resolves OOM_LOCALAPPDATA/oom/<hash of the canonical vault path>/state.db to.
    private static string StateDbPath(KabulHarness harness, string vault)
    {
        var canonical = Path.GetFullPath(vault).TrimEnd(Path.DirectorySeparatorChar);
        return Path.Combine(harness.LocalAppData, "oom", VaultIdentity.Hash(canonical), VaultIdentity.DatabaseName);
    }
}
