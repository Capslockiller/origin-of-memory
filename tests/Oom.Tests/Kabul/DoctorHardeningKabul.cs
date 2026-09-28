using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;

namespace Oom.Tests.Kabul;

/// <summary>
/// Acceptance tests for lane L7b-doctor (SPEC-3.1.0.md wave 7): the doctor defects a Claude
/// reviewer measured on eb19f02 (this wave's own review report, M3-M6 and the doctor
/// minors) plus R33 (missing-root Error/Warning ruling) and R35 (legacy sweep_stamps
/// outcome normalisation). Each fact below pins one finding: it is written to fail against
/// the UNCHANGED src/Oom/Doctor/Doctor.cs and src/Oom/Cli/Program.Doctor.cs, then the fix
/// in this same lane makes it pass.
///
/// Process-boundary tests use <see cref="KabulHarness"/> with OOM_LOCALAPPDATA and
/// OOM_USERPROFILE both redirected into the harness's own isolated scratch root — never the
/// real %LOCALAPPDATA%\oom or the real ~/.claude — exactly like DoctorFinalKabul's own
/// process-boundary tests. State rows are seeded directly through <see cref="State"/> against
/// the child's own state.db (SeedState), never through process-wide environment mutation.
/// </summary>
public sealed class DoctorHardeningKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = new(2026, 9, 28, 10, 0, 0, TimeSpan.FromHours(3));
    private static readonly string[] RequiredHookNames = ["SessionStart", "UserPromptSubmit", "SessionEnd", "PreCompact"];

    // ------------------------------------------------------------------
    // M3 — coverage takes the STRICTER of stamp coverage and the sweep's own coverage row,
    // so a budget-skipped/never-swept session (counted uncovered by the row, but never
    // stamped at all) cannot be hidden by an all-'ok' stamp table.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "M3 · kapsama, damga ile satırın daha SIKI (düşük) olanını alır")]
    public void Coverage_TakesStricterOfStampAndRowCoverage()
    {
        var root = NewDirectory("m3-stricter");
        try
        {
            var vault = BuildUnitVault(root);
            var transcripts = Directory.CreateDirectory(Path.Combine(root, "transcripts")).FullName;
            using var state = new State(null, null, Path.Combine(root, "state.db"));
            // The sweep's own coverage row: 20 covered / 50 total this run — 30 sessions were
            // budget-skipped (or otherwise never reached a real flush) and so never received a
            // sweep_stamps row at all.
            state.RecordCoverage(Today, 20, 50, 7);
            // Every STAMP that DOES exist happens to be 'ok' — stamp coverage alone would read
            // 100%, silently hiding the 30 sessions the row already knows are uncovered.
            for (var i = 0; i < 5; i++)
                state.WriteStamp(Path.Combine(transcripts, $"ok-{i}.jsonl"), Today.AddHours(-i).ToString("O"), 10, "ok");

            var snapshot = Program.Snapshot(Today, vault, Settings(vault, transcripts), state);

            Assert.Equal(20.0 / 50.0, snapshot.Coverage, 10);
            Assert.Equal(50, snapshot.WindowTotal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Remove(root);
        }
    }

    // ------------------------------------------------------------------
    // R35 (driver ruling, coordinator addition to this lane's brief) — a pre-3.1.0
    // sweep_stamps.outcome used a different status vocabulary (measured on the author's
    // live state: 1,799 legacy stamps — no-turns 1203, ok 277, no-new-turns 219, retry 82,
    // bos 18). Coverage must normalise the raw value per the ruling's mapping before
    // counting: ok/no-new-turns/bos/refused -> ok; no-turns -> skipped (excluded from the
    // denominator); retry/parked/locked -> partial; unreadable -> unreadable; anything
    // else -> partial. Scaled-down fixture here (3 ok, 2 no-new-turns, 1 bos, 5 no-turns,
    // 1 retry): 6 covered out of 7 in the denominator; the 5 no-turns stamps never enter it.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "R35 · eski (3.0.x) sweep_stamps.outcome değerleri kapsamaya sayılmadan önce normalize edilir")]
    public void StampCoverage_NormalizesLegacyOutcomeValues()
    {
        var root = NewDirectory("r35-legacy-outcomes");
        try
        {
            var vault = BuildUnitVault(root);
            var transcripts = Directory.CreateDirectory(Path.Combine(root, "transcripts")).FullName;
            using var state = new State(null, null, Path.Combine(root, "state.db"));

            WriteStamps(state, transcripts, "ok", 3);
            WriteStamps(state, transcripts, "no-new-turns", 2);
            WriteStamps(state, transcripts, "bos", 1);
            WriteStamps(state, transcripts, "no-turns", 5);
            WriteStamps(state, transcripts, "retry", 1);

            var snapshot = Program.Snapshot(Today, vault, Settings(vault, transcripts), state);

            Assert.Equal(6.0 / 7.0, snapshot.Coverage, 10);
            Assert.Equal(7, snapshot.WindowTotal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Remove(root);
        }
    }

    private static void WriteStamps(State state, string transcripts, string outcome, int count)
    {
        for (var i = 0; i < count; i++)
            state.WriteStamp(Path.Combine(transcripts, $"{outcome}-{i}.jsonl"), Today.ToString("O"), 10, outcome);
    }

    // ------------------------------------------------------------------
    // M4 — a flush session parked at attempts >= 5 is never retried again (Flush.cs never
    // re-queues a parked row): that is a dead session, ERROR, not a Warning.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "M4 · park edilmiş flush oturumu HATA'dır (asla tekrar denenmez)")]
    public void ParkedFlushSession_IsError_NotWarning()
    {
        using var harness = new KabulHarness();
        var fixture = BuildProcessFixture(harness, "m4");
        RunDoctor(harness, fixture, "--fix");
        SeedState(harness, fixture.Vault, state =>
        {
            state.RecordCoverage(Today, 10, 10, 7);
            state.WriteRetry("flush-session-m4", 5, Today.AddHours(-1), "runner başarısız");
        });

        var result = RunDoctor(harness, fixture, "--json");
        using var document = JsonDocument.Parse(result.Stdout);
        var session = Assert.Single(Items(document), item => Text(item, "code") == "parked-session");

        Assert.Equal("error", Text(session, "level"));
        Assert.Equal(1, result.ExitCode);
    }

    // ------------------------------------------------------------------
    // M5 — an event counts as REGISTERED only when a command found under it is itself an
    // oom command (ExecutableOf != null). A property literally named "SessionStart" whose
    // only command belongs to some other tool must still be reported missing.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "M5 · SessionStart vb. yalnızca oom-dışı bir komutla kayıtlıysa 'eksik' sayılır")]
    public void HasHook_RequiresAnOomCommandUnderTheEvent_NotJustThePropertyName()
    {
        var nonOomHooks = new Dictionary<string, object>();
        foreach (var hook in RequiredHookNames)
            nonOomHooks[hook] = new[] { new { hooks = new[] { new { type = "command", command = "echo merhaba" } } } };
        var project = JsonSerializer.Serialize(new { hooks = nonOomHooks });

        var items = new Doctor().ValidateHooks("{}", project);

        Assert.Equal(4, items.Count(item => item.Code == "missing-hook"));
        Assert.DoesNotContain(items, item => item.Code == "hook-path");
    }

    // ------------------------------------------------------------------
    // M5 minor — a command merely containing "oom" as a SUBSTRING (e.g. "zoom-status.js")
    // is not an oom hook and must not be judged as one.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "M5-minor · yalnızca alt dizge olarak 'oom' geçen komut (zoom-status.js) oom kancası sayılmaz")]
    public void SubstringOomMatch_IsNotJudgedAsAnOomHook()
    {
        var user = JsonSerializer.Serialize(new { statusLine = new { type = "command", command = "node C:/tools/zoom-status.js" } });

        var items = new Doctor().ValidateHooks(user, "{}");

        Assert.DoesNotContain(items, item => item.Code == "hook-path");
    }

    // ------------------------------------------------------------------
    // Minor — a scope with no settings.json at all is only a Warning when the OTHER scope,
    // alone, already registers all four required hooks.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "Minor · kullanıcı kapsamında dört oom kancası varsa 'settings.json (proje) yok' uyarısı bastırılır")]
    public void UserScopeOnlyHooks_SuppressProjectSettingsMissingWarning()
    {
        var root = NewDirectory("suppress-project-missing");
        try
        {
            var exe = Path.Combine(root, "oom.exe");
            File.WriteAllText(exe, "fixture", Utf8);

            var items = new Doctor().ValidateHooks(HookJson(exe), string.Empty);

            Assert.DoesNotContain(items, item => item.Code == "settings-missing");
            Assert.DoesNotContain(items, item => item.Code == "missing-hook");
        }
        finally
        {
            Remove(root);
        }
    }

    // ------------------------------------------------------------------
    // IMPORTANT real-world case — the author's live hooks are registered at USER scope
    // only, UNQUOTED (e.g. `E:/OdenaOS/.oom/bin/oom.exe --vault E:/OdenaOS context`); new
    // installs write them double-quoted. Both forms must be recognised as registered oom
    // hooks with no warning at all (no missing-hook, no hook-path, no settings-missing for
    // the empty project scope).
    // ------------------------------------------------------------------
    [Theory(DisplayName = "ÖNEMLİ · gerçek dünya: kullanıcı kapsamındaki oom kancaları (tırnaksız VE çift tırnaklı) uyarısız tanınır")]
    [InlineData(false)] // author's live hooks — unquoted
    [InlineData(true)]  // new installs — double-quoted
    public void UserScopeOomHooks_RecognizedWithNoWarning_QuotedAndUnquoted(bool quoted)
    {
        using var harness = new KabulHarness();
        var fixture = BuildProcessFixture(harness, "real-world-" + quoted);

        var exeToken = quoted ? $"\"{harness.ExePath}\"" : harness.ExePath;
        var vaultToken = quoted ? $"\"{fixture.Vault}\"" : fixture.Vault;
        var hooks = new Dictionary<string, object>
        {
            ["SessionStart"] = HookGroup($"{exeToken} --vault {vaultToken} context"),
            ["UserPromptSubmit"] = HookGroup($"{exeToken} --vault {vaultToken} nudge"),
            ["SessionEnd"] = HookGroup($"{exeToken} --vault {vaultToken} flush --reason sessionend"),
            ["PreCompact"] = HookGroup($"{exeToken} --vault {vaultToken} flush --reason precompact"),
        };
        File.WriteAllText(Path.Combine(fixture.Profile, ".claude", "settings.json"),
            JsonSerializer.Serialize(new { hooks }), Utf8);

        var result = RunDoctor(harness, fixture, "--json");

        using var document = JsonDocument.Parse(result.Stdout);
        var hookItems = Items(document).Where(item => Text(item, "component") == "hooks").ToArray();
        Assert.True(hookItems.All(item => Text(item, "level") == "info"),
            $"beklenmeyen hooks satırı ({(quoted ? "çift tırnaklı" : "tırnaksız")}): " +
            string.Join(" | ", hookItems.Where(item => Text(item, "level") != "info")
                .Select(item => $"{Text(item, "code")}:{Text(item, "level")}:{Text(item, "detail")}")));
    }

    // ------------------------------------------------------------------
    // R33/M6 — a missing DEFAULT root, while another root exists and nothing in oom.json
    // explicitly configured sweep.roots, is a WARNING (not a hard failure).
    // ------------------------------------------------------------------
    [Fact(DisplayName = "M6/R33 · varsayılan köklerden biri eksik ve oom.json'da açık ayar yoksa UYARI'dır")]
    public void MissingDefaultRoot_WithAnotherRootPresent_IsWarning()
    {
        var root = NewDirectory("m6-default-warning");
        try
        {
            var vault = BuildUnitVault(root);
            var existingRoot = Directory.CreateDirectory(Path.Combine(root, "existing")).FullName;
            var missingRoot = Path.Combine(root, "does-not-exist");
            var defaults = OomSettings.Defaults(vault);
            var settings = defaults with { Sweep = defaults.Sweep with { Roots = [existingRoot, missingRoot] } };

            var observation = Assert.Single(Program.Snapshot(Today, vault, settings, null).Observations,
                item => item.Item.Code == "missing-root");

            Assert.Equal(HealthLevel.Warning, observation.Item.Level);
        }
        finally
        {
            Remove(root);
        }
    }

    // ------------------------------------------------------------------
    // R33/M6 — the other half of the ruling: when oom.json sets sweep.roots EXPLICITLY,
    // a missing one of them stays an ERROR even though another root still exists.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "M6/R33 · oom.json roots'u AÇIKÇA ayarlarsa, başka kök var olsa bile eksik kök HATA'dır")]
    public void MissingRoot_ExplicitlyConfiguredInOomJson_StaysError()
    {
        var root = NewDirectory("m6-explicit-error");
        try
        {
            var vault = BuildUnitVault(root);
            var existingRoot = Directory.CreateDirectory(Path.Combine(root, "existing")).FullName;
            var missingRoot = Path.Combine(root, "does-not-exist");
            Directory.CreateDirectory(Path.Combine(vault, ".oom"));
            File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"),
                JsonSerializer.Serialize(new { sweep = new { roots = new[] { existingRoot, missingRoot } } }), Utf8);
            var settings = OomSettings.Load(vault);

            var observation = Assert.Single(Program.Snapshot(Today, vault, settings, null).Observations,
                item => item.Item.Code == "missing-root");

            Assert.Equal(HealthLevel.Error, observation.Item.Level);
        }
        finally
        {
            Remove(root);
        }
    }

    // ------------------------------------------------------------------
    // Minor — a NULL sweep_stamps.outcome (a hand-edited or pre-3.1.0 row) must not crash
    // doctor with an unhandled InvalidOperationException/InvalidCastException.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "Minor · sweep_stamps.outcome NULL olduğunda doctor çökmez (COALESCE)")]
    public void NullSweepStampOutcome_DoesNotCrashDoctor()
    {
        var root = NewDirectory("null-outcome");
        try
        {
            var vault = BuildUnitVault(root);
            var transcripts = Directory.CreateDirectory(Path.Combine(root, "transcripts")).FullName;
            var database = Path.Combine(root, "state.db");
            var stampPath = Path.Combine(transcripts, "null-outcome.jsonl");
            using (var seed = new State(null, null, database))
                seed.WriteStamp(stampPath, Today.ToString("O"), 10, "ok");
            SqliteConnection.ClearAllPools();

            using (var raw = new SqliteConnection($"Data Source={database}"))
            {
                raw.Open();
                using var command = raw.CreateCommand();
                command.CommandText = "UPDATE sweep_stamps SET outcome = NULL WHERE path = $p";
                command.Parameters.AddWithValue("$p", stampPath);
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            using var state = new State(null, null, database);
            var exception = Record.Exception(() => Program.Snapshot(Today, vault, Settings(vault, transcripts), state));

            Assert.Null(exception);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Remove(root);
        }
    }

    // ------------------------------------------------------------------
    // Minor — a corrupt state.db (measured shape: SQLite "file is not a database") must
    // not crash `oom doctor` outright; it should emit one red "state" row and keep going.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "Minor · bozuk state.db doctor'u çökertmez, kırmızı 'state' satırıyla devam eder")]
    public void CorruptStateDb_DoesNotCrashDoctor_EmitsRedStateRow()
    {
        using var harness = new KabulHarness();
        var fixture = BuildProcessFixture(harness, "corrupt-db");
        var canonical = Path.GetFullPath(fixture.Vault).TrimEnd(Path.DirectorySeparatorChar);
        var database = Path.Combine(harness.LocalAppData, "oom", VaultIdentity.Hash(canonical), VaultIdentity.DatabaseName);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        File.WriteAllBytes(database, Encoding.UTF8.GetBytes("bu geçerli bir sqlite dosyası değil, sadece çöp bayt dizisi"));

        var result = RunDoctor(harness, fixture, "--json");

        var parseError = Record.Exception(() => JsonDocument.Parse(result.Stdout));
        Assert.True(parseError is null,
            $"stdout JSON olarak ayrıştırılamadı (doctor çökmüş olabilir): {parseError}\nstdout={result.Stdout}\nstderr={result.Stderr}");
        using var document = JsonDocument.Parse(result.Stdout);
        var stateRow = Assert.Single(Items(document), item => Text(item, "component") == "state" && Text(item, "level") == "error");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("bozuk", Text(stateRow, "detail"), StringComparison.OrdinalIgnoreCase);
    }

    // ====================================================================
    // Fixture / harness plumbing
    // ====================================================================

    private static object HookGroup(string command) => new[] { new { hooks = new[] { new { type = "command", command } } } };

    private static string HookJson(string executable)
    {
        var hooks = new Dictionary<string, object>();
        foreach (var hook in RequiredHookNames)
            hooks[hook] = new[] { new { hooks = new[] { new { type = "command", command = $"\"{executable}\" hook --event {hook}" } } } };
        return JsonSerializer.Serialize(new { hooks });
    }

    private static string BuildUnitVault(string root)
    {
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts"));
        return vault;
    }

    private static OomSettings Settings(string vault, string sweepRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(sweepRoot) ?? sweepRoot);
        var defaults = OomSettings.Defaults(vault);
        return defaults with { Sweep = defaults.Sweep with { Roots = [sweepRoot] } };
    }

    private static ProcessFixture BuildProcessFixture(KabulHarness harness, string name)
    {
        var vault = BuildUnitVault(harness.NewScratchDirectory(name + "-vault"));
        var profile = harness.NewScratchDirectory(name + "-profile");
        Directory.CreateDirectory(Path.Combine(profile, ".claude"));
        File.WriteAllText(Path.Combine(vault, "daily", "2026-09-27.md"), "# 2026-09-27\n\n- fixture\n", Utf8);
        return new ProcessFixture(vault, profile);
    }

    private static void SeedState(KabulHarness harness, string vault, Action<State> seed)
    {
        var canonical = Path.GetFullPath(vault).TrimEnd(Path.DirectorySeparatorChar);
        var database = Path.Combine(harness.LocalAppData, "oom", VaultIdentity.Hash(canonical), VaultIdentity.DatabaseName);
        using (var state = new State(null, null, database))
            seed(state);
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Runs the harness exe as a real child process, exactly like DoctorFinalKabul's
    /// own RunDoctor, but always sets OOM_USERPROFILE to the fixture's own scratch profile —
    /// this suite must never let doctor fall through to the machine's real ~/.claude.</summary>
    private static ChildResult RunDoctor(KabulHarness harness, ProcessFixture fixture, params string[] arguments)
    {
        var start = new ProcessStartInfo(harness.ExePath)
        {
            WorkingDirectory = harness.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8
        };
        start.ArgumentList.Add("--vault");
        start.ArgumentList.Add(fixture.Vault);
        start.ArgumentList.Add("doctor");
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment.Remove("OOM_INVOKED_BY");
        start.Environment["OOM_LOCALAPPDATA"] = harness.LocalAppData;
        start.Environment["OOM_USERPROFILE"] = fixture.Profile;
        start.Environment["OOM_FAKE_NOW"] = Today.ToString("O");

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), $"doctor zaman aşımı: {stderr}");
        return new ChildResult(process.ExitCode, stdout, stderr);
    }

    private static JsonElement[] Items(JsonDocument document) =>
        document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();

    private static string Text(JsonElement item, string property) => item.GetProperty(property).GetString() ?? string.Empty;

    private static string NewDirectory(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "doctor-hardening", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Remove(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ProcessFixture(string Vault, string Profile);
    private sealed record ChildResult(int ExitCode, string Stdout, string Stderr);
}
