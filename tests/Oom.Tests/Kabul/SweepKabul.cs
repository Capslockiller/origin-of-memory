using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oom.Contracts;

namespace Oom.Tests.Kabul;

/// <summary>
/// Acceptance oracle for lane L3-sweep (SPEC-3.1.0.md F4-1, F4-3, R6, NB-6, F5-2). Drives a
/// real oom.exe through <see cref="KabulHarness"/> against a <see cref="FakeClaude"/> shim on
/// PATH — never a real model call. Every black-box test here targets a behaviour that was
/// wrong at commit 2de59ac (see each test's own remarks for the exact mechanism, now fixed
/// in this tree) and wrong again against eski-exe/oom.exe (OOM_KABUL_EXE) — see the lane's
/// proof notes.
///
/// NB-6: <see cref="GrownOldStampedSessionIsProcessedNotSkipped"/> is the DenetimA2Probe
/// scenario (audit A2-03) taken into the repo as a test, the same scenario
/// tests/Oom.Tests/Kabul/Regresyon/RegresyonKabul.cs already pins as a [Trait("Kabul",
/// "Regresyon")] red-by-design regression oracle (SPEC R11: excluded from interim gates until
/// this wave lands). This copy carries no Regresyon trait — it is the ordinary gate this lane's
/// fix must turn green under.
/// </summary>
public sealed class SweepKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = new(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(3));
    private static readonly TimeSpan SlowRun = TimeSpan.FromSeconds(180);

    private static readonly Regex AnchorPattern = new(@"<!-- session:(?<id>\S+) ts:\S+ turns:(?<start>\d+)-(?<end>\d+)", RegexOptions.Compiled);
    private static readonly Regex CoveredOfTotal = new(@"(?<covered>\d+)/(?<total>\d+)\s+kapsandı", RegexOptions.Compiled);

    // ------------------------------------------------------------------
    // Assertion 1 (F4-1 / NB-6 / audit A2-03, "DenetimA2Probe"): a session stamped 'ok' at
    // 4 turns grows to 44 turns with its mtime set 10 hours in the past. `oom sweep` must
    // process it (changed=1, skipped=0, 1 result) rather than skip it on the stale-mtime
    // age gate now that its SIZE has changed. At commit 2de59ac, SweepRun.Execute only
    // treated a candidate as "unchanged" when BOTH mtime and size matched the stored stamp
    // (src/Oom/Sweep/SweepRun.cs) — size differed here, so that fast path correctly did
    // not fire — but a SECOND gate, `_sweep.ShouldProcess(candidate.ModifiedAt, true,
    // sinceHours, now)`, judged staleness from mtime ALONE and skipped regardless of the
    // size change, so the grown session was wrongly counted as already covered. That
    // second gate is removed outright in this tree (SweepSettings.SinceHours is gone; see
    // Sweep.cs, deleted). eski-exe: the same audit finding (A2-03), skipped=1.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "F4-1/NB-6 · DenetimA2Probe: 'ok' damgalı 4 turluk oturum 44 tura büyür, mtime −10 saat → skipped=0, changed=1, 1 sonuç")]
    public void GrownOldStampedSessionIsProcessedNotSkipped()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        var projects = harness.NewScratchDirectory("transcripts");
        WriteSweepSettings(vault, projects, maxSessionsPerRun: 20);
        const string session = "l3-denetim-a2-probe";
        var transcript = Path.Combine(projects, session + ".jsonl");
        var turnsStart = new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.FromHours(3));

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var pathScope = fake.ReplaceProcessPath();

        WriteTranscript(transcript, session, 4, turnsStart);
        var first = harness.Run(vault, ["sweep"], fakeNow: Today, timeout: SlowRun);
        var stampedAnchors = Anchors(vault, session);
        Assert.True(first.ExitCode == 0 && stampedAnchors.Any(range => range.Start == 0 && range.End == 3),
            $"fixture precondition: the first sweep must flush the 4-turn session 'ok' (daily anchor turns:0-3). " +
            $"exit {first.ExitCode}; anchors [{string.Join(", ", stampedAnchors)}]; stdout: {first.Stdout}; stderr: {first.Stderr}");

        WriteTranscript(transcript, session, 44, turnsStart);
        File.SetLastWriteTimeUtc(transcript, (Today - TimeSpan.FromHours(10)).UtcDateTime);

        // Review finding: the assertion below never checked the 'X/Y kapsandı' figure, so
        // a regression that counted the grown session as covered from its OLD stamp
        // (without actually reprocessing it) would still have passed. A `--dry-run` taken
        // right before the real sweep also proves the growth is seen as 'changed' before
        // any state is written — not as already-covered-from-stamp.
        var dry = harness.Run(vault, ["sweep", "--dry-run"], fakeNow: Today, timeout: SlowRun);
        var dryChanged = SummaryNumber(dry.Stdout, "değişmiş");
        var dryAnchorsStillOld = Anchors(vault, session).Where(range => range.Start > 3).ToArray();
        Assert.True(dry.ExitCode == 0 && dryChanged == 1 && dryAnchorsStillOld.Length == 0,
            $"F4-1: `sweep --dry-run` right before the real sweep must already see the grown session as changed " +
            $"(dry-run never writes, so no new anchor may exist yet). Got exit {dry.ExitCode}, changed={Show(dryChanged)}, " +
            $"new-turn anchors [{string.Join(", ", dryAnchorsStillOld)}]; stdout: {dry.Stdout}");

        var second = harness.Run(vault, ["sweep"], fakeNow: Today, timeout: SlowRun);

        var changed = SummaryNumber(second.Stdout, "değişmiş");
        var skipped = SummaryNumber(second.Stdout, "atlandı");
        var sessions = SummaryNumber(second.Stdout, "oturum");
        var newAnchors = Anchors(vault, session).Where(range => range.Start > 3).ToArray();
        var coverage = CoveredOfTotal.Match(second.Stdout);

        Assert.True(changed == 1 && skipped == 0 && sessions == 1 && newAnchors.Length > 0
            && coverage is { Success: true } && coverage.Groups["covered"].Value == "1" && coverage.Groups["total"].Value == "1",
            $"F4-1: a session stamped 'ok' at 4 turns that grows to 44 turns with mtime set 10h in the past must be " +
            $"processed (changed=1, skipped=0, 1 result), with a new daily anchor covering turns beyond index 3, and " +
            $"coverage must rise only after that processing (1/1 kapsandı) — not from the old stamp. " +
            $"Got changed={Show(changed)}, skipped={Show(skipped)}, sessions={Show(sessions)}, new-turn anchors " +
            $"[{string.Join(", ", newAnchors)}], coverage='{(coverage.Success ? coverage.Value : "(not printed)")}'. " +
            $"exit {second.ExitCode}; stdout: {second.Stdout}; stderr: {second.Stderr}");
    }

    // ------------------------------------------------------------------
    // Assertion 2 (F4-3): with sessions already stamped by an earlier real sweep, the
    // 'X/Y kapsandı' figure `oom sweep --dry-run` prints must equal the next real run's,
    // and the dry-run must not touch state.db (or its -wal file) at all. At commit
    // 2de59ac, RunSweep (src/Oom/Cli/Program.Sweep.cs) still constructed SweepRun with
    // `dryRun ? null : state` — passing NO state object into a dry run at all, not a
    // read-only one. With state=null, SweepRun.Execute could never read a session's prior
    // stamp (`_state?.ReadStamp(...)` was always null), so EVERY discovered candidate fell
    // through to the per-run session budget instead of taking the free "already covered,
    // unchanged" fast path a real run uses — starving the budget and under-counting
    // coverage for sessions that are, in fact, fully covered already. Fixed in this tree
    // by opening a read-only State for dry runs instead of null. eski-exe: the same defect
    // (state=null in dry-run, Program.cs:207), so the numbers differ (audit finding:
    // dry-run showed 17/1889 against a real run's different total).
    // ------------------------------------------------------------------
    [Fact(DisplayName = "F4-3 · Kuru koşum gerçek kapsamayı gösterir ve state.db'ye dokunmaz")]
    public void DryRunReportsRealCoverageAndNeverTouchesState()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        var projects = harness.NewScratchDirectory("transcripts");

        const int sessionCount = 5;
        var sessions = Enumerable.Range(1, sessionCount).Select(i => $"l3-dry-run-{i:D2}").ToArray();
        foreach (var session in sessions)
            WriteTranscript(Path.Combine(projects, session + ".jsonl"), session, 4, Today.AddDays(-1));

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var pathScope = fake.ReplaceProcessPath();

        // Seed: a generous budget lets one real sweep flush and stamp all five sessions.
        WriteSweepSettings(vault, projects, maxSessionsPerRun: sessionCount * 2);
        var seed = harness.Run(vault, ["sweep"], fakeNow: Today, timeout: SlowRun);
        Assert.True(seed.ExitCode == 0 && CoveredOfTotal.Match(seed.Stdout) is { Success: true } seedMatch
            && seedMatch.Groups["covered"].Value == sessionCount.ToString(CultureInfo.InvariantCulture)
            && seedMatch.Groups["total"].Value == sessionCount.ToString(CultureInfo.InvariantCulture),
            $"fixture precondition: the seeding sweep must cover all {sessionCount} sessions. stdout: {seed.Stdout}; stderr: {seed.Stderr}");

        var databasePath = FindStateDatabase(harness);
        Assert.True(databasePath is not null, $"fixture precondition: no state.db found under {harness.LocalAppData} after the seeding sweep.");

        // Tighten the budget well below the session count — real re-runs still cover
        // everything for free (a fully-stamped, unchanged session never touches the
        // budget), but a dry-run that cannot see the stamps (state=null) burns the whole
        // budget re-"processing" a handful of already-covered sessions and reports the
        // rest as uncovered.
        WriteSweepSettings(vault, projects, maxSessionsPerRun: 2);

        var beforeDryRun = Fingerprint(databasePath!);
        var dryRun = harness.Run(vault, ["sweep", "--dry-run"], fakeNow: Today, timeout: SlowRun);
        var afterDryRun = Fingerprint(databasePath!);

        var real = harness.Run(vault, ["sweep"], fakeNow: Today, timeout: SlowRun);

        var dryRunCoverage = CoveredOfTotal.Match(dryRun.Stdout);
        var realCoverage = CoveredOfTotal.Match(real.Stdout);

        Assert.True(beforeDryRun == afterDryRun,
            $"F4-3: `oom sweep --dry-run` must never write to state.db (or its -wal file); fingerprint before " +
            $"[{beforeDryRun}] differs from after [{afterDryRun}]. dry-run stdout: {dryRun.Stdout}");

        Assert.True(dryRunCoverage.Success && realCoverage.Success
            && dryRunCoverage.Groups["covered"].Value == realCoverage.Groups["covered"].Value
            && dryRunCoverage.Groups["total"].Value == realCoverage.Groups["total"].Value,
            $"F4-3: with {sessionCount} sessions already fully covered by an earlier real sweep, `sweep --dry-run`'s " +
            $"'X/Y kapsandı' must equal the very next real sweep's — dry-run must read the real coverage, not " +
            $"under-report it because it cannot see prior stamps. dry-run: '{(dryRunCoverage.Success ? dryRunCoverage.Value : "(not printed)")}' " +
            $"(stdout: {dryRun.Stdout}); real: '{(realCoverage.Success ? realCoverage.Value : "(not printed)")}' (stdout: {real.Stdout})");
    }

    // ------------------------------------------------------------------
    // Assertion 3 (R6): with compile.eveningHour=0 (or, in the second case, eveningHour=18
    // pinned to 19:00 local — the old code's own "compile is due now" hour) and a pending
    // daily, a plain `oom sweep` must start NO compile at all: no model call happens (the
    // FakeClaude shim's call count stays 0), no new knowledge/concepts/*.md file appears,
    // and knowledge/log.md gains no new 'compile |' entry. There is no `--no-compile` flag
    // in 3.1.0 (R6/R12): compile only ever runs via an explicit `oom compile`. eski-exe:
    // Program.cs:227-230 calls MaybeCompile after a real sweep and, when it says compile is
    // due, detaches a real `oom ... compile` child process — which, given a pending daily
    // and this test's FakeClaude shim on PATH, actually writes the marker note.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "R6 · 'oom sweep' bekleyen daily varken bile hiçbir saatte derleme başlatmaz (eveningHour=0 ve eveningHour=18/19:00)")]
    public void SweepNeverStartsCompileRegardlessOfEveningHour()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        WriteDaily(vault, "2026-09-27.md");
        var emptyRoot = harness.NewScratchDirectory("no-sweep-roots");

        // The compile response, if it were ever sent, publishes a single, distinctively
        // named concept note — the "compile marker" this test looks for.
        const string markerSlug = "l3-r6-derleme-baslamamali-isareti";
        var compileMarkerOutput =
            $"=== FILE: knowledge/concepts/{markerSlug}.md ===\n" +
            "---\n" +
            "title: R6 derleme başlamamalı işareti\n" +
            "aliases: []\ntags: [sentetik]\nsources: [2026-09-27.md]\ncreated: 2026-09-27\nupdated: 2026-09-27\n" +
            "type: concept\nhub: genel\n---\n" +
            "# R6 derleme başlamamalı işareti\n" +
            "Bu not yalnızca 'oom sweep' bir derleme başlatırsa var olur; kabul harness fixture'ı.\n\n" +
            "## İlgili Kavramlar\n- [[ilk-kavram]] ilk\n- [[ikinci-kavram]] ikinci\n" +
            "=== END FILE ===\n=== DONE ===\n";

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(),
            new FakeClaudeResponse(Stdout: JsonSerializer.Serialize(new { result = compileMarkerOutput })));
        using var pathScope = fake.ReplaceProcessPath();

        WriteSweepSettingsWithCompile(vault, emptyRoot, eveningHour: 0);
        var caseZero = harness.Run(vault, ["sweep"], fakeNow: Today, timeout: SlowRun);

        WriteSweepSettingsWithCompile(vault, emptyRoot, eveningHour: 18);
        var evening = new DateTimeOffset(2026, 9, 27, 19, 0, 0, TimeSpan.FromHours(3));
        var caseEighteenAtNineteen = harness.Run(vault, ["sweep"], fakeNow: evening, timeout: SlowRun);

        // A third run explicitly passes the nonexistent '--no-compile' flag (there is no
        // such flag in 3.1.0 — R6/R12): it must behave exactly like the two runs above,
        // never as a way to opt in to a compile that would otherwise have started. Review
        // finding: this must NOT pin exit 0 for that run — the brief says '--no-compile'
        // is not a valid flag, and F6/A2-10 treat no-op acceptance of an unknown flag as
        // dishonest surface. Whether an honest CLI later rejects it (non-zero exit) or
        // ignores it as an unrecognised sweep argument (exit 0) is a command-surface
        // decision outside this lane; only "no compile ever starts" is pinned here.
        var caseNoCompileFlag = harness.Run(vault, ["sweep", "--no-compile"], fakeNow: Today, timeout: SlowRun);

        var markerPath = Path.Combine(vault, "knowledge", "concepts", markerSlug + ".md");
        var logPath = Path.Combine(vault, "knowledge", "log.md");
        var logHasCompileEntry = File.Exists(logPath) && File.ReadAllText(logPath, Utf8).Contains("compile |", StringComparison.Ordinal);

        Assert.True(caseZero.ExitCode == 0 && caseEighteenAtNineteen.ExitCode == 0,
            $"fixture precondition: the two well-formed `oom sweep` runs must exit 0. exits: {caseZero.ExitCode}, " +
            $"{caseEighteenAtNineteen.ExitCode}");

        Assert.True(fake.CallCount == 0 && !File.Exists(markerPath) && !logHasCompileEntry,
            $"R6: `oom sweep` must never start a compile — not with compile.eveningHour=0, not with eveningHour=18 " +
            $"pinned to 19:00, and not with the (nonexistent) '--no-compile' flag. Got FakeClaude call count " +
            $"{fake.CallCount} (want 0), marker note written={File.Exists(markerPath)}, knowledge/log.md has a " +
            $"'compile |' entry={logHasCompileEntry}. Stdouts — eveningHour=0: {caseZero.Stdout} | " +
            $"eveningHour=18@19:00: {caseEighteenAtNineteen.Stdout} | --no-compile: {caseNoCompileFlag.Stdout}");
    }

    // ------------------------------------------------------------------
    // Assertion 4 (F5-2): after a real sweep, the state's numeric 'coverage' table has a
    // new covered/total row with Total > 0 — this lane's own write path
    // (SweepRun.Execute calling State.RecordCoverage) was the part that was missing at
    // commit 2de59ac and is what this test actually pins.
    //
    // Review finding: the second half this test's name and comment used to claim — that
    // `oom doctor`'s reported coverage comes from that numeric row rather than a value
    // regex-parsed back out of a health-table TEXT detail line — is NOT proven here.
    // Program.Snapshot (src/Oom/Cli/Program.Doctor.cs) still feeds doctor's coverage from
    // `state.LastCoverage()`, which regexes "(\d+)/(\d+)" out of the sweep's own
    // health-table detail STRING ("Son 7 gün kapsama: N/M") — never `ReadCoverage()`. That
    // still passes here only because SweepRun.Execute writes the same window values to
    // both the health text and the numeric row in the same run, so the two sources cannot
    // disagree in this fixture. Fixing doctor to read the numeric row is Program.Doctor.cs
    // (L3-doctor's file, not L3-sweep's); this lane keeps the Total>0 write-path check and
    // leaves the doctor-reads-the-numeric-row assertion to that lane. eski-exe predates
    // the 'coverage' table's schema entirely (State.cs's CREATE TABLE list), so
    // ReadCoverage() returns null: no numeric row is ever written by a real sweep at all.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "F5-2 · Gerçek sweep sonrası coverage tablosunda sayısal satır var ve doctor'ın kapsaması ona eşit")]
    public void RealSweepWritesNumericCoverageRowAndDoctorAgreesWithIt()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        var projects = harness.NewScratchDirectory("transcripts");
        const string session = "l3-f52-coverage";
        WriteSweepSettings(vault, projects, maxSessionsPerRun: 20);
        WriteTranscript(Path.Combine(projects, session + ".jsonl"), session, 4, Today.AddHours(-2));

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var pathScope = fake.ReplaceProcessPath();

        var sweep = harness.Run(vault, ["sweep"], fakeNow: Today, timeout: SlowRun);
        Assert.True(sweep.ExitCode == 0, $"fixture precondition: the sweep must exit 0. stdout: {sweep.Stdout}; stderr: {sweep.Stderr}");

        // Review finding: the 'sonuçlar:' line prints wire outcome names (FlushOutcomes.Wire,
        // e.g. "ok", "no-new-turns") rather than lowercased C# enum names ("noturns",
        // "nonewturns", "empty=") — a user-visible output change (F4(4)) that nothing pinned.
        Assert.Contains("sonuçlar: ok=1", sweep.Stdout);
        Assert.DoesNotMatch(new Regex(@"noturns|nonewturns|empty="), sweep.Stdout);

        var databasePath = FindStateDatabase(harness);
        Assert.True(databasePath is not null, $"fixture precondition: no state.db found under {harness.LocalAppData} after the sweep.");

        CoverageReading? coverage;
        using (var state = new State(null, null, databasePath, StateAccess.ReadOnly))
            coverage = state.ReadCoverage();

        Assert.True(coverage is { Total: > 0 },
            $"F5-2: after a real sweep, state.db's numeric 'coverage' table must hold a covered/total row with " +
            $"Total > 0. Got {(coverage is null ? "(no row — table missing or empty)" : $"Covered={coverage.Covered}, Total={coverage.Total}")}. " +
            $"sweep stdout: {sweep.Stdout}");

        var doctor = harness.Run(vault, ["doctor", "--json"], fakeNow: Today, timeout: SlowRun);
        Assert.True(doctor.ExitCode == 0 || doctor.ExitCode == 1, $"doctor --json exited unexpectedly ({doctor.ExitCode}); stderr: {doctor.Stderr}");

        using var document = JsonDocument.Parse(doctor.Stdout);
        var reportedCoverage = document.RootElement.TryGetProperty("coverage", out var value) && value.ValueKind is JsonValueKind.Number
            ? value.GetDouble()
            : (double?)null;

        var expected = coverage!.Total == 0 ? 1.0 : (double)coverage.Covered / coverage.Total;

        Assert.True(reportedCoverage is not null && Math.Abs(reportedCoverage.Value - expected) < 1e-9,
            $"F5-2: `oom doctor --json`'s coverage field must equal the coverage table's own row (Covered={coverage.Covered}/" +
            $"Total={coverage.Total} → {expected:R}), read straight from state, not re-derived by regexing a health-table " +
            $"text line. Got doctor coverage={(reportedCoverage is null ? "(missing/non-numeric)" : reportedCoverage.Value.ToString("R", CultureInfo.InvariantCulture))}. " +
            $"doctor --json: {doctor.Stdout}");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string BuildVault(string root)
    {
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts"));
        return vault;
    }

    private static void WriteDaily(string vault, string name) =>
        File.WriteAllText(Path.Combine(vault, "daily", name),
            $"# {Path.GetFileNameWithoutExtension(name)}\n\n- sentetik günlük satırı, kabul harness fixture'ı, gerçek içerik değil.\n", Utf8);

    private static void WriteSweepSettings(string vault, string root, int maxSessionsPerRun)
    {
        Directory.CreateDirectory(Path.Combine(vault, ".oom"));
        File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"), JsonSerializer.Serialize(new
        {
            sweep = new { roots = new[] { root }, maxSessionsPerRun }
        }), Utf8);
    }

    private static void WriteSweepSettingsWithCompile(string vault, string root, int eveningHour)
    {
        Directory.CreateDirectory(Path.Combine(vault, ".oom"));
        File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"), JsonSerializer.Serialize(new
        {
            sweep = new { roots = new[] { root } },
            compile = new { eveningHour }
        }), Utf8);
    }

    /// <summary>Writes a Claude Code-shaped transcript with alternating user/assistant text
    /// turns. Turn i is identical across calls, so a longer write "grows" the same session
    /// (mirrors RegresyonKabul.WriteTranscript — kept as an independent copy since this file
    /// does not own that one).</summary>
    private static void WriteTranscript(string path, string sessionId, int turns, DateTimeOffset start)
    {
        var lines = new StringBuilder();
        for (var i = 0; i < turns; i++)
        {
            var user = i % 2 == 0;
            var text = $"Sentetik tur {i:D2}: kabul senaryosu için {(user ? "kullanıcı sorusu" : "asistan yanıtı")}, gerçek içerik değildir.";
            object content = user ? text : new[] { new { type = "text", text } };
            lines.Append(JsonSerializer.Serialize(new
            {
                sessionId,
                type = user ? "user" : "assistant",
                timestamp = (start + TimeSpan.FromMinutes(i)).ToString("O", CultureInfo.InvariantCulture),
                message = new { role = user ? "user" : "assistant", content }
            })).Append('\n');
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, lines.ToString(), Utf8);
    }

    private static IReadOnlyList<(int Start, int End)> Anchors(string vault, string sessionId)
    {
        var directory = Path.Combine(vault, "daily");
        if (!Directory.Exists(directory))
            return [];

        return [.. Directory.GetFiles(directory, "*.md")
            .SelectMany(path => AnchorPattern.Matches(File.ReadAllText(path, Utf8)))
            .Where(match => match.Groups["id"].Value == sessionId)
            .Select(match => (int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture)))];
    }

    private static int? SummaryNumber(string stdout, string word)
    {
        var match = Regex.Match(stdout, $@"(\d+) {Regex.Escape(word)}\b");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static string Show(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "(not printed)";

    /// <summary>Finds the one state.db a KabulHarness run created under its isolated
    /// OOM_LOCALAPPDATA (<c>&lt;LocalAppData&gt;/oom/&lt;vault-hash&gt;/state.db</c>) without
    /// this test process itself having to set OOM_LOCALAPPDATA (a process-wide env var) to
    /// resolve VaultIdentity's path — the harness already isolates that directory per test.</summary>
    private static string? FindStateDatabase(KabulHarness harness) =>
        Directory.Exists(harness.LocalAppData)
            ? Directory.EnumerateFiles(harness.LocalAppData, "state.db", SearchOption.AllDirectories).SingleOrDefault()
            : null;

    /// <summary>A cheap "did anything write to this database" fingerprint: the main file's
    /// size and hash, plus the same for its WAL sidecar file when one exists (SQLite's
    /// default journal_mode=WAL can leave writes sitting in -wal without growing state.db
    /// itself).</summary>
    private static string Fingerprint(string databasePath)
    {
        var main = HashOf(databasePath);
        var wal = HashOf(databasePath + "-wal");
        return $"db:{main}|wal:{wal}";
    }

    private static string HashOf(string path)
    {
        if (!File.Exists(path))
            return "(absent)";
        var bytes = File.ReadAllBytes(path);
        return $"{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes))}";
    }
}
