using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;

namespace Oom.Tests.Kabul;

public sealed class DoctorFinalKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = new(2026, 9, 28, 10, 0, 0, TimeSpan.FromHours(3));

    [Fact(DisplayName = "O7 · parked daily, retry oturumundan ayrı sayılır ve doctor exit 1 döndürür")]
    public void ParkedDaily_IsReportedSeparately_AndMakesDoctorRed()
    {
        using var harness = new KabulHarness();
        var fixture = BuildProcessFixture(harness, includeProjectHooks: true);
        RunDoctor(harness, fixture, "--fix");
        SeedState(harness, fixture.Vault, state =>
        {
            state.RecordCoverage(Today, 10, 10, 7);
            state.WriteDailyIngest("2026-09-27.md", "parked", Today, "üç gerçek ret");
        });

        var result = RunDoctor(harness, fixture, "--json");
        using var document = JsonDocument.Parse(result.Stdout);
        var parked = Assert.Single(Items(document), item =>
            Text(item, "component") == "daily" && Text(item, "code") == "parked");

        Assert.Equal("error", Text(parked, "level"));
        Assert.Equal("1", Text(parked, "key"));
        Assert.Equal(1, document.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Equal(1, result.ExitCode);
    }

    [Fact(DisplayName = "O7 · parked flush oturumu parked daily sayısını şişirmez")]
    public void ParkedFlushSession_HasItsOwnRow_AndLeavesParkedDailyAtZero()
    {
        using var harness = new KabulHarness();
        var fixture = BuildProcessFixture(harness, includeProjectHooks: true);
        RunDoctor(harness, fixture, "--fix");
        SeedState(harness, fixture.Vault, state =>
        {
            state.RecordCoverage(Today, 10, 10, 7);
            state.WriteRetry("flush-session", 5, Today.AddHours(-1), "runner başarısız");
        });

        var result = RunDoctor(harness, fixture, "--json");
        using var document = JsonDocument.Parse(result.Stdout);
        var items = Items(document);
        var daily = Assert.Single(items, item => Text(item, "component") == "daily" && Text(item, "code") == "parked");
        var session = Assert.Single(items, item => Text(item, "code") == "parked-session");

        Assert.Equal("0", Text(daily, "key"));
        Assert.NotEqual("daily", Text(session, "component"));
        Assert.Equal("1", Text(session, "key"));
        Assert.Contains("oturum", Text(session, "detail"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "O14 · PATH'te bulunan claude için hook-failed kaydı gizlenmez ve sonraki başarı iyileştirir")]
    public void ReachableClaude_DoesNotHideFailedSubprocess_AndSuccessHealsIt()
    {
        var reach = new Doctor().CheckClaudeReachability(Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        Assert.Equal(HealthLevel.Info, reach.Level);

        var root = NewDirectory("o14");
        try
        {
            var vault = BuildUnitVault(root);
            var settings = Settings(vault, Path.Combine(root, "transcripts"));
            var request = new ProcessRequest(reach.Key, [], root, new Dictionary<string, string>(), string.Empty);
            new Runner(new FixedProcessRunner(1)).RunProcess(request, TimeSpan.FromSeconds(1));

            Assert.Contains(Program.Snapshot(Today, vault, settings, null).Observations,
                observation => observation.Item is { Component: "hooks", Code: "hook-failed", Level: HealthLevel.Error }
                    && observation.Item.Key == reach.Key);

            new Runner(new FixedProcessRunner(0)).RunProcess(request, TimeSpan.FromSeconds(1));
            Assert.DoesNotContain(Program.Snapshot(Today, vault, settings, null).Observations,
                observation => observation.Item is { Component: "hooks", Code: "hook-failed", Level: HealthLevel.Error }
                    && observation.Item.Key == reach.Key);
        }
        finally
        {
            Remove(root);
        }
    }

    [Fact(DisplayName = "O15/D1 · boş ve bozuk settings sessiz geçmez; olmayan oom.exe reddedilir")]
    public void HookValidation_ReportsMissingMalformedAndMissingExecutables()
    {
        var doctor = new Doctor();

        var empty = doctor.ValidateHooks(string.Empty, string.Empty);
        Assert.Equal(4, empty.Count(item => item is { Component: "hooks", Code: "missing-hook" }));
        Assert.All(empty.Where(item => item.Code == "missing-hook"), item => Assert.NotEqual(HealthLevel.Info, item.Level));

        var malformed = doctor.ValidateHooks("{ bozuk json", string.Empty);
        var unreadable = Assert.Single(malformed, item => item.Code == "settings-unreadable");
        Assert.Equal(HealthLevel.Error, unreadable.Level);
        Assert.Contains("settings.json", unreadable.Key, StringComparison.OrdinalIgnoreCase);

        var root = NewDirectory("hooks");
        try
        {
            var missingExe = Path.Combine(root, "nope", "oom.exe");
            var missing = doctor.ValidateHooks(HookJson(missingExe), string.Empty);
            Assert.Equal(4, missing.Count(item => item is { Code: "hook-path", Level: HealthLevel.Error }));

            var realExe = Path.Combine(root, "oom.exe");
            File.WriteAllText(realExe, "fixture", Utf8);
            var valid = doctor.ValidateHooks(HookJson(realExe), string.Empty);
            Assert.DoesNotContain(valid, item => item.Code == "hook-path");
        }
        finally
        {
            Remove(root);
        }
    }

    [Fact(DisplayName = "O20 · dört project hook user settings boşken de mevcut sayılır")]
    public void ProjectHooks_SatisfyAllRequiredHooks()
    {
        var projectOnly = new Doctor().ValidateHooks("{}", HookJson(Path.Combine("E:\\v", ".oom", "bin", "oom.exe")));
        Assert.DoesNotContain(projectOnly, item => item.Code == "missing-hook");

        var neither = new Doctor().ValidateHooks("{}", "{}");
        Assert.Equal(4, neither.Count(item => item.Code == "missing-hook"));
    }

    [Fact(DisplayName = "O15 · boş OOM_USERPROFILE ile process doctor hooks bulgusu üretir")]
    public void EmptyUserProfile_ProcessDoctorReportsHookProblem()
    {
        using var harness = new KabulHarness();
        var fixture = BuildProcessFixture(harness, includeProjectHooks: false);

        var result = RunDoctor(harness, fixture, "--json");
        using var document = JsonDocument.Parse(result.Stdout);

        Assert.Contains(Items(document), item =>
            Text(item, "component") == "hooks" && Text(item, "level") is "warning" or "error");
    }

    [Fact(DisplayName = "R32 · doctor kapsaması ok/(ok+partial+unreadable), skipped payda dışıdır")]
    public void Coverage_UsesSweepStampStatusContract()
    {
        var root = NewDirectory("coverage");
        try
        {
            var vault = BuildUnitVault(root);
            var transcripts = Directory.CreateDirectory(Path.Combine(root, "transcripts")).FullName;
            using var state = new State(null, null, Path.Combine(root, "state.db"));
            state.RecordCoverage(Today, 4, 4, 7);
            state.WriteStamp(Path.Combine(transcripts, "ok.jsonl"), Today.ToString("O"), 10, "ok");
            state.WriteStamp(Path.Combine(transcripts, "partial.jsonl"), Today.AddDays(-1).ToString("O"), 10, "partial");
            state.WriteStamp(Path.Combine(transcripts, "unreadable.jsonl"), Today.AddDays(-2).ToString("O"), 10, "unreadable");
            state.WriteStamp(Path.Combine(transcripts, "skipped.jsonl"), Today.AddDays(-3).ToString("O"), 10, "skipped");
            state.WriteStamp(Path.Combine(transcripts, "old-partial.jsonl"), Today.AddDays(-8).ToString("O"), 10, "partial");

            var snapshot = Program.Snapshot(Today, vault, Settings(vault, transcripts), state);

            Assert.Equal(1.0 / 3.0, snapshot.Coverage, 10);
            Assert.Equal(3, snapshot.WindowTotal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Remove(root);
        }
    }

    [Fact(DisplayName = "R32 · yapılandırılmış eksik sweep root ERROR satırıdır")]
    public void MissingSweepRoot_IsAnErrorNamingTheRoot()
    {
        var root = NewDirectory("missing-root");
        try
        {
            var vault = BuildUnitVault(root);
            var missing = Path.Combine(root, "does-not-exist");

            var observation = Assert.Single(Program.Snapshot(Today, vault, Settings(vault, missing), null).Observations,
                item => item.Item.Code == "missing-root");

            Assert.Equal(HealthLevel.Error, observation.Item.Level);
            Assert.Equal(missing, observation.Item.Key);
            Assert.Contains(missing, observation.Item.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Remove(root);
        }
    }

    private static ProcessFixture BuildProcessFixture(KabulHarness harness, bool includeProjectHooks)
    {
        var vault = BuildUnitVault(harness.NewScratchDirectory("fixture"));
        var profile = harness.NewScratchDirectory("profile");
        var transcripts = harness.NewScratchDirectory("transcripts");
        Directory.CreateDirectory(Path.Combine(profile, ".claude"));
        Directory.CreateDirectory(Path.Combine(vault, ".oom"));
        File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"),
            JsonSerializer.Serialize(new { sweep = new { roots = new[] { transcripts } } }), Utf8);
        File.WriteAllText(Path.Combine(vault, "daily", "2026-09-27.md"), "# 2026-09-27\n\n- fixture\n", Utf8);
        if (includeProjectHooks)
        {
            Directory.CreateDirectory(Path.Combine(vault, ".claude"));
            File.WriteAllText(Path.Combine(vault, ".claude", "settings.json"), HookJson(harness.ExePath), Utf8);
        }

        return new ProcessFixture(vault, profile);
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

    private static string HookJson(string executable)
    {
        var hooks = new Dictionary<string, object>();
        foreach (var hook in new[] { "SessionStart", "UserPromptSubmit", "SessionEnd", "PreCompact" })
            hooks[hook] = new[] { new { hooks = new[] { new { type = "command", command = $"\"{executable}\" hook --event {hook}" } } } };
        return JsonSerializer.Serialize(new { hooks });
    }

    private static void SeedState(KabulHarness harness, string vault, Action<State> seed)
    {
        var canonical = Path.GetFullPath(vault).TrimEnd(Path.DirectorySeparatorChar);
        var database = Path.Combine(harness.LocalAppData, "oom", VaultIdentity.Hash(canonical), VaultIdentity.DatabaseName);
        using (var state = new State(null, null, database))
            seed(state);
        SqliteConnection.ClearAllPools();
    }

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
        var path = Path.Combine(AppContext.BaseDirectory, "doctor-final", name + "-" + Guid.NewGuid().ToString("N"));
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

    private sealed class FixedProcessRunner(int exitCode) : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request, TimeSpan timeout) => new(exitCode, "{}", string.Empty, true);
    }

    private sealed record ProcessFixture(string Vault, string Profile);
    private sealed record ChildResult(int ExitCode, string Stdout, string Stderr);
}
