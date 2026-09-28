using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// Lane L5a-release-scripts acceptance oracle (SPEC-3.1.0.md F9-1..F9-4, B5, Cut/A3-11,
/// NB-12). Covers the four NEW scripts this lane owns:
///   tools/release/etiketten-derle.ps1  — F9-1: build only from a clean tree at tag v3.1.0
///   tools/release/kur-3.1.0.ps1        — F9-3/F9-4/B5: install keeping exactly one
///                                          bin.onceki, never touching hooks/oom.json
///   tools/release/geri-al-3.1.0.ps1    — F9-3: byte-identical rollback of bin.onceki
///   tools/release/yerel-kapi.ps1       — NB-12: local gate, unmasked exit code
/// and one existing file this lane owns edits to:
///   .github/workflows/ci.yml           — F9-2: same unmasked tests, private traits excluded
///
/// None of these scripts exist on the current tree (this is a brand-new lane), so every
/// test below fails today with a real process-boundary failure (PowerShell's own "-File
/// parameter does not exist" exit, not a .NET exception and not a compile error) except
/// CiFilter_ExcludesPrivateAndCanaryTraits, which fails today because the CHECKED-IN
/// .github/workflows/ci.yml filter is genuinely stale (see that test's doc comment).
///
/// R3: this file is new, added under tests/Oom.Tests/Kabul/ without touching the
/// driver-owned KabulHarness.cs / Fixtures/KabulVaultBuilder.cs. It does not use
/// KabulHarness itself: that harness drives a built oom.exe as OOM_KABUL_EXE selects, and
/// none of these four scripts' contracts depend on which oom.exe build is under test — the
/// defects here are "the script file does not exist" and "the checked-in ci.yml filter is
/// stale", neither of which OOM_KABUL_EXE affects. Where a script needs a *built binary* at
/// all (etiketten-derle.ps1, which builds one itself from a scratch clone's own Oom.sln;
/// kur-3.1.0.ps1/geri-al-3.1.0.ps1, whose -Source is a fixture directory standing in for a
/// built bin — these scripts manage files, they do not run or interpret oom.exe's own
/// behaviour), the test builds or fabricates it directly rather than going through
/// KabulHarness's exe-selection seam.
/// </summary>
internal static class ReleaseScriptRunner
{
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static string RepoRoot() => ScarFixture.RepositoryRoot();

    internal static string ScriptPath(string name) => Path.Combine(RepoRoot(), "tools", "release", name);

    internal sealed record ScriptResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// Runs tools/release/&lt;scriptName&gt; with Windows PowerShell (powershell.exe, the
    /// 5.1 runtime the lane notes require these scripts to target — never pwsh) as
    /// <c>-File &lt;script&gt; &lt;arguments...&gt;</c>, non-interactively so a script that
    /// would otherwise prompt fails fast instead of hanging the test run.
    /// </summary>
    internal static ScriptResult RunScript(
        string scriptName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(ScriptPath(scriptName));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment)
                start.Environment[key] = value;

        using var process = new Process { StartInfo = start };
        process.Start();
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
        if (!process.WaitForExit((int)effectiveTimeout.TotalMilliseconds))
        {
            TryKill(process);
            throw new TimeoutException($"{scriptName} did not exit within {effectiveTimeout}.");
        }
        // WaitForExit(int) does not guarantee redirected streams are fully drained; the
        // parameterless overload does. Calling it after the timed wait succeeds is the
        // documented way to be sure stdout/stderr (and process.ExitCode) are final.
        process.WaitForExit();

        return new ScriptResult(process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    /// <summary>Runs an ad-hoc PowerShell command (not a script file) — used only for the
    /// read-only Get-ScheduledTask probe, never to write anything.</summary>
    internal static ScriptResult RunPowerShellCommand(string command, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);

        using var process = new Process { StartInfo = start };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (!process.WaitForExit((int)effectiveTimeout.TotalMilliseconds))
        {
            TryKill(process);
            throw new TimeoutException("Get-ScheduledTask probe did not exit in time.");
        }
        process.WaitForExit();
        return new ScriptResult(process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    internal static (int ExitCode, string Stdout, string Stderr) RunGit(string workingDirectory, string arguments)
    {
        var start = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Clones THIS lane's own working tree into a fresh scratch directory: local
    /// (same-machine, so git hardlinks or fast-copies objects rather than any network
    /// transfer) but explicitly <c>--no-hardlinks</c>, so the clone's object store is fully
    /// independent of the source repository's — tagging or committing in the clone can
    /// never write into the real origin-of-memory .git that this worktree and every other
    /// wave-5 lane share. The clone carries the real Oom.sln, so etiketten-derle.ps1's
    /// precondition checks are exercised against something that WOULD actually build and
    /// succeed if a check were skipped, not against an empty scratch repo that fails for the
    /// unrelated reason of "no solution file".
    /// </summary>
    internal static string CreateScratchClone(string scratchRoot)
    {
        var clone = Path.Combine(scratchRoot, "klon-" + Guid.NewGuid().ToString("N")[..8]);
        var (exitCode, _, stderr) = RunGit(scratchRoot, $"clone --local --no-hardlinks --no-tags --quiet \"{RepoRoot()}\" \"{clone}\"");
        if (exitCode != 0)
            throw new InvalidOperationException($"kabul: sentetik klon oluşturulamadı: {stderr}");
        RunGit(clone, "config user.email kahin@oracle.invalid");
        RunGit(clone, "config user.name \"Kabul Kahin\"");
        return clone;
    }

    internal static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

/// <summary>
/// F9-1 oracle: tools/release/etiketten-derle.ps1 must build the release exe ONLY from a
/// clean working tree whose HEAD sits exactly at tag v3.1.0, and must never report success
/// with a build whose own reported commit disagrees with HEAD or fell back to "+unknown".
///
/// SAFETY ("never tag the real repo"): every scenario runs against a fresh
/// <see cref="ReleaseScriptRunner.CreateScratchClone"/>, never this lane's own worktree and
/// never origin-of-memory's shared .git — a local clone has its own, independent refs
/// namespace, so <c>git tag v3.1.0</c> inside it cannot create that tag anywhere the real
/// repository (or any other wave-5 lane sharing it) would ever see it.
/// </summary>
public sealed class BuildFromTagKabul : IDisposable
{
    private readonly string _root = ScarFixture.TempDirectory();

    public void Dispose() => ScarFixture.Remove(_root);

    private static string BuiltExePath(string cloneRoot) =>
        Path.Combine(cloneRoot, "src", "Oom", "bin", "Release", "net9.0", "oom.exe");

    private string PrepareCleanTaggedClone()
    {
        var clone = ReleaseScriptRunner.CreateScratchClone(_root);
        var (tagExit, _, tagError) = ReleaseScriptRunner.RunGit(clone, "tag v3.1.0");
        Assert.True(tagExit == 0, $"kabul: sentetik v3.1.0 etiketi oluşturulamadı: {tagError}");
        return clone;
    }

    private static (int ExitCode, string Stdout) RunBuiltExe(string exePath, params string[] args)
    {
        var start = new ProcessStartInfo(exePath) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout);
    }

    [Fact(DisplayName = "F9-1a · kirli ağaçta (git status --porcelain boş değil) etiketten-derle.ps1 sıfır olmayan çıkışla biter")]
    public void DirtyTree_ExitsNonZero()
    {
        var clone = PrepareCleanTaggedClone();
        File.AppendAllText(Path.Combine(clone, "README.md"), "\nkabul: kirletme satırı, commit edilmedi\n");
        var (statusExit, statusOut, _) = ReleaseScriptRunner.RunGit(clone, "status --porcelain");
        Assert.Equal(0, statusExit);
        Assert.False(string.IsNullOrWhiteSpace(statusOut), "kabul fixture'ı ağacı kirletemedi — test hiçbir şey kanıtlamaz");

        var result = ReleaseScriptRunner.RunScript("etiketten-derle.ps1", ["-RepoRoot", clone], clone);

        Assert.True(result.ExitCode != 0,
            $"kirli ağaçta beklenmeyen sıfır çıkış. stdout={result.Stdout}\nstderr={result.Stderr}");
    }

    [Fact(DisplayName = "F9-1b · HEAD etiketin ilerisindeyken (v3.1.0'da değil) etiketten-derle.ps1 sıfır olmayan çıkışla biter")]
    public void HeadPastTag_ExitsNonZero()
    {
        var clone = PrepareCleanTaggedClone();
        File.WriteAllText(Path.Combine(clone, "KABUL-SENTETIK-EK-COMMIT.md"), "sentetik ek commit, kabul harness fixture'ı\n");
        ReleaseScriptRunner.RunGit(clone, "add -A");
        var (commitExit, _, commitError) = ReleaseScriptRunner.RunGit(clone, "commit -q -m \"kabul: etiketten sonraki sentetik commit\"");
        Assert.True(commitExit == 0, $"kabul: sentetik ek commit atılamadı: {commitError}");
        var (tagCommitExit, tagCommitOut, _) = ReleaseScriptRunner.RunGit(clone, "rev-parse v3.1.0");
        var (headExit, headOut, _) = ReleaseScriptRunner.RunGit(clone, "rev-parse HEAD");
        Assert.Equal(0, tagCommitExit);
        Assert.Equal(0, headExit);
        Assert.NotEqual(tagCommitOut.Trim(), headOut.Trim());

        var result = ReleaseScriptRunner.RunScript("etiketten-derle.ps1", ["-RepoRoot", clone], clone);

        Assert.True(result.ExitCode != 0,
            $"HEAD etiketin ilerisindeyken beklenmeyen sıfır çıkış. stdout={result.Stdout}\nstderr={result.Stderr}");
    }

    /// <summary>
    /// Covers "the built oom --version commit ... is '+unknown'" directly: with .git moved
    /// out of the way, Oom.csproj's own SetBuildRevision target (R17) falls back to
    /// "3.1.0+unknown" and `dotnet build` still exits 0 (verified empirically: see the lane
    /// report) — so a script that only checks "did dotnet build succeed?" without verifying
    /// the built version would wrongly report success here. See
    /// <c>assertions_not_expressible</c> in the lane report for why the OTHER half of this
    /// clause — "commit differs from HEAD" as a state distinct from "+unknown" — is not
    /// independently black-box-constructible in this codebase (SetBuildRevision recomputes
    /// the commit fresh on every build; there is no stale cache to desynchronize).
    /// </summary>
    [Fact(DisplayName = "F9-1c · .git yokken (derleme '+unknown' üretir) etiketten-derle.ps1 sıfır olmayan çıkışla biter, başarı bildirmez")]
    public void MissingGit_NeverAcceptsUnknownCommit()
    {
        var clone = PrepareCleanTaggedClone();
        Directory.Move(Path.Combine(clone, ".git"), Path.Combine(clone, ".git.disabled-by-kabul"));

        var result = ReleaseScriptRunner.RunScript("etiketten-derle.ps1", ["-RepoRoot", clone], clone);

        Assert.True(result.ExitCode != 0,
            $".git yokken beklenmeyen sıfır çıkış. stdout={result.Stdout}\nstderr={result.Stderr}");
        Assert.DoesNotContain("+unknown", result.Stdout, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "F9-1d · temiz ağaç + HEAD tam v3.1.0'da: etiketten-derle.ps1 başarılı olur, üretilen oom.exe --version etiketin commit'ini basar")]
    public void CleanTreeAtTag_Succeeds_AndBuiltExeVersionMatchesTagCommit()
    {
        var clone = PrepareCleanTaggedClone();
        var (headExit, headOut, _) = ReleaseScriptRunner.RunGit(clone, "rev-parse --short HEAD");
        Assert.Equal(0, headExit);
        var expectedCommit = headOut.Trim();
        Assert.Matches("^[0-9a-f]{7,40}$", expectedCommit);

        var result = ReleaseScriptRunner.RunScript("etiketten-derle.ps1", ["-RepoRoot", clone], clone, timeout: TimeSpan.FromMinutes(5));

        Assert.True(result.ExitCode == 0,
            $"temiz ağaç + HEAD tam etikette iken beklenmeyen çıkış {result.ExitCode}. stdout={result.Stdout}\nstderr={result.Stderr}");

        var exePath = BuiltExePath(clone);
        Assert.True(File.Exists(exePath), $"etiketten-derle.ps1 başarı bildirdi ama oom.exe yok: {exePath}");

        var (versionExit, versionOut) = RunBuiltExe(exePath, "--version");
        Assert.Equal(0, versionExit);
        Assert.Contains($"+{expectedCommit}", versionOut, StringComparison.Ordinal);
    }
}

/// <summary>
/// F9-3 / F9-4 / B5 / Cut(A3-11) oracle: tools/release/kur-3.1.0.ps1 and
/// tools/release/geri-al-3.1.0.ps1 manage exactly ONE prior generation of the installed bin
/// (never an accumulating pile of bin.bak-* directories — the old manual pattern the Cut
/// section names outright, A3-11), and never touch hooks, oom.json or any other already-
/// installed state while doing it.
///
/// Contract this lane's scripts add (named here per the task's "name APIs that don't exist
/// yet" instruction — nothing below reuses `oom install`/Program.Install.cs, which is
/// vault-scoped and knows nothing about Codex hooks.json or the .oom/bin directory):
///   kur-3.1.0.ps1    -Vault &lt;path&gt; -Source &lt;built bin dir&gt; -Home &lt;path&gt; [-Canli]
///   geri-al-3.1.0.ps1 -Vault &lt;path&gt; [-Canli]
/// -Home is an explicit parameter specifically so no test ever needs to read or write the
/// real user profile's ~/.claude or ~/.codex (forbidden outright by the lane brief); every
/// test here passes a scratch directory.
/// </summary>
public sealed class InstallRollbackKabul : IDisposable
{
    private readonly string _root = ScarFixture.TempDirectory();

    public void Dispose() => ScarFixture.Remove(_root);

    private static void WriteFakeBin(string dir, string generation)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "oom.exe"), $"kabul-sentetik-oom-ikili-nesil-{generation}\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "manifest.txt"), $"generation={generation}\n", new UTF8Encoding(false));
    }

    private static string[] Snapshot(string dir) =>
        Directory.Exists(dir)
            ? [.. Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(dir, file) + ":" + Convert.ToBase64String(File.ReadAllBytes(file)))
                .OrderBy(entry => entry, StringComparer.Ordinal)]
            : [];

    private sealed record ScratchLayout(string Vault, string Home, string ClaudeSettings, string CodexHooks, string OomJson, string ExpectedExePath);

    private ScratchLayout BuildScratchLayout(string suffix = "")
    {
        var vault = Path.Combine(_root, "kasa" + suffix);
        var home = Path.Combine(_root, "ev" + suffix);
        Directory.CreateDirectory(Path.Combine(vault, ".oom"));
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        Directory.CreateDirectory(Path.Combine(home, ".codex"));

        var expectedExePath = Path.Combine(vault, ".oom", "bin", "oom.exe");
        var registrations = HookTemplates.Build(expectedExePath, vault);

        var settingsPath = Path.Combine(home, ".claude", "settings.json");
        File.WriteAllText(settingsPath, BuildHookFileJson(registrations, includePreCompact: true), new UTF8Encoding(false));

        var hooksPath = Path.Combine(home, ".codex", "hooks.json");
        // Codex has no PreCompact hook (binding rule; also R6/driver rulings) — three
        // events only. The exact hooks.json schema is not defined anywhere else in this
        // codebase yet (no existing writer of it — grep confirms), so this fixture's shape
        // is this test's own convention, used only to prove "kur-3.1.0.ps1 leaves this file
        // byte-identical", never parsed or written by the script itself.
        File.WriteAllText(hooksPath, BuildHookFileJson(registrations, includePreCompact: false), new UTF8Encoding(false));

        var oomJsonPath = Path.Combine(vault, ".oom", "oom.json");
        File.WriteAllText(oomJsonPath, "{\n  \"context\": { \"capChars\": 16000 }\n}\n", new UTF8Encoding(false));

        return new ScratchLayout(vault, home, settingsPath, hooksPath, oomJsonPath, expectedExePath);
    }

    private static string BuildHookFileJson(IReadOnlyList<HookRegistration> registrations, bool includePreCompact)
    {
        var hooks = new JsonObject();
        foreach (var registration in registrations)
        {
            if (!includePreCompact && registration.Event == "PreCompact")
                continue;
            hooks[registration.Event] = new JsonArray(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = registration.Command,
                    ["timeout"] = registration.TimeoutSeconds
                })
            });
        }
        var root = new JsonObject { ["hooks"] = hooks };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>Extracts the leading executable-path token from a HookTemplates-shaped
    /// command string ("&lt;exe&gt; --vault &lt;vault&gt; &lt;verb&gt;...", the whole prefix
    /// quoted together when either segment contains a space — see HookTemplates.Quote /
    /// Install.Y-309). Test-local parsing, independent of the script under test.</summary>
    private static string ExtractLeadingExecutablePrefix(string command)
    {
        if (command.StartsWith('"'))
        {
            var closingQuote = command.IndexOf('"', 1);
            Assert.True(closingQuote > 0, $"kabul: kapanmamış tırnak: {command}");
            return command[1..closingQuote];
        }
        var firstSpace = command.IndexOf(' ');
        return firstSpace > 0 ? command[..firstSpace] : command;
    }

    private static IEnumerable<string> AllHookCommands(string hookFileJson)
    {
        var root = JsonNode.Parse(hookFileJson)!.AsObject();
        if (root["hooks"] is not JsonObject hooks)
            yield break;
        foreach (var (_, entries) in hooks)
        {
            if (entries is not JsonArray array)
                continue;
            foreach (var entry in array)
            {
                if (entry?["hooks"] is not JsonArray inner)
                    continue;
                foreach (var hook in inner)
                    if (hook?["command"]?.GetValue<string>() is { } command)
                        yield return command;
            }
        }
    }

    [Fact(DisplayName = "F9-3/F9-4 · üç ardışık kur-3.1.0.ps1 koşumu: tam bir bin.onceki kalır, yeni bin.bak-* sıfır, settings.json/hooks.json/oom.json hash'leri değişmez, üretilen kanca komutları tek exe'ye işaret eder")]
    public void ThreeConsecutiveInstalls_KeepExactlyOneBackup_AndLeaveConfigsUntouched()
    {
        var layout = BuildScratchLayout();
        var settingsBefore = File.ReadAllText(layout.ClaudeSettings);
        var hooksBefore = File.ReadAllText(layout.CodexHooks);
        var settingsHashBefore = ReleaseScriptRunner.Sha256File(layout.ClaudeSettings);
        var hooksHashBefore = ReleaseScriptRunner.Sha256File(layout.CodexHooks);
        var oomJsonHashBefore = ReleaseScriptRunner.Sha256File(layout.OomJson);

        string[] generations = ["nesil-bir", "nesil-iki", "nesil-uc"];
        var sources = generations.Select((generation, index) =>
        {
            var dir = Path.Combine(_root, $"kaynak-{index}");
            WriteFakeBin(dir, generation);
            return dir;
        }).ToArray();

        foreach (var source in sources)
        {
            var result = ReleaseScriptRunner.RunScript("kur-3.1.0.ps1",
                ["-Vault", layout.Vault, "-Source", source, "-Home", layout.Home], _root);
            Assert.True(result.ExitCode == 0,
                $"kurulum başarısız ({source}). stdout={result.Stdout}\nstderr={result.Stderr}");
        }

        var oomDir = Path.Combine(layout.Vault, ".oom");
        var binDir = Path.Combine(oomDir, "bin");
        var oncekiDir = Path.Combine(oomDir, "bin.onceki");

        Assert.Equal(Snapshot(sources[2]), Snapshot(binDir));
        Assert.True(Directory.Exists(oncekiDir), "bin.onceki oluşmadı");
        Assert.Equal(Snapshot(sources[1]), Snapshot(oncekiDir));

        Assert.Empty(Directory.EnumerateDirectories(oomDir, "bin.bak-*"));
        Assert.Single(Directory.EnumerateDirectories(oomDir, "bin.onceki*"));

        Assert.Equal(settingsHashBefore, ReleaseScriptRunner.Sha256File(layout.ClaudeSettings));
        Assert.Equal(hooksHashBefore, ReleaseScriptRunner.Sha256File(layout.CodexHooks));
        Assert.Equal(oomJsonHashBefore, ReleaseScriptRunner.Sha256File(layout.OomJson));
        Assert.Equal(settingsBefore, File.ReadAllText(layout.ClaudeSettings));
        Assert.Equal(hooksBefore, File.ReadAllText(layout.CodexHooks));

        var commands = AllHookCommands(File.ReadAllText(layout.ClaudeSettings))
            .Concat(AllHookCommands(File.ReadAllText(layout.CodexHooks)))
            .ToArray();
        Assert.True(commands.Length >= 4, $"kanca komutu sayısı beklenenden az: {commands.Length}");
        var exePrefixes = commands.Select(ExtractLeadingExecutablePrefix).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Single(exePrefixes);
        Assert.Equal(layout.ExpectedExePath.Replace('\\', '/'), exePrefixes[0].Replace('\\', '/'), ignoreCase: true);
    }

    [Fact(DisplayName = "F9-3 · geri-al-3.1.0.ps1 bin.onceki'yi bayt-bayt aynı şekilde geri yükler, settings.json/hooks.json/oom.json'a dokunmaz")]
    public void Rollback_RestoresPreviousGeneration_ByteIdentical_AndLeavesConfigsUntouched()
    {
        var layout = BuildScratchLayout("-geri-al");
        var settingsHashBefore = ReleaseScriptRunner.Sha256File(layout.ClaudeSettings);
        var hooksHashBefore = ReleaseScriptRunner.Sha256File(layout.CodexHooks);
        var oomJsonHashBefore = ReleaseScriptRunner.Sha256File(layout.OomJson);

        var sourceOne = Path.Combine(_root, "geri-al-kaynak-1");
        var sourceTwo = Path.Combine(_root, "geri-al-kaynak-2");
        WriteFakeBin(sourceOne, "bir");
        WriteFakeBin(sourceTwo, "iki");

        foreach (var source in new[] { sourceOne, sourceTwo })
        {
            var install = ReleaseScriptRunner.RunScript("kur-3.1.0.ps1",
                ["-Vault", layout.Vault, "-Source", source, "-Home", layout.Home], _root);
            Assert.True(install.ExitCode == 0, $"kurulum başarısız ({source}). stdout={install.Stdout}\nstderr={install.Stderr}");
        }

        var binDir = Path.Combine(layout.Vault, ".oom", "bin");
        Assert.Equal(Snapshot(sourceTwo), Snapshot(binDir));

        var rollback = ReleaseScriptRunner.RunScript("geri-al-3.1.0.ps1", ["-Vault", layout.Vault], _root);
        Assert.True(rollback.ExitCode == 0, $"geri alma başarısız. stdout={rollback.Stdout}\nstderr={rollback.Stderr}");

        Assert.Equal(Snapshot(sourceOne), Snapshot(binDir));
        Assert.Equal(settingsHashBefore, ReleaseScriptRunner.Sha256File(layout.ClaudeSettings));
        Assert.Equal(hooksHashBefore, ReleaseScriptRunner.Sha256File(layout.CodexHooks));
        Assert.Equal(oomJsonHashBefore, ReleaseScriptRunner.Sha256File(layout.OomJson));
    }

    /// <summary>
    /// Lane note (not one of the four numbered oracle assertions, but explicit in the lane
    /// brief): "fails loudly if oom.exe is locked". Opens the currently-installed oom.exe
    /// with FileShare.None (an exclusive read handle a running process would realistically
    /// hold) and asserts the second install refuses rather than partially overwriting it.
    /// </summary>
    [Fact(DisplayName = "Not · hedef oom.exe kilitliyken kur-3.1.0.ps1 yüksek sesle başarısız olur, mevcut bin bozulmadan kalır")]
    public void Install_FailsLoudly_WhenTargetExeIsLocked_AndLeavesExistingBinIntact()
    {
        var layout = BuildScratchLayout("-kilit");
        var sourceOne = Path.Combine(_root, "kilit-kaynak-1");
        WriteFakeBin(sourceOne, "once");
        var first = ReleaseScriptRunner.RunScript("kur-3.1.0.ps1",
            ["-Vault", layout.Vault, "-Source", sourceOne, "-Home", layout.Home], _root);
        Assert.True(first.ExitCode == 0, $"ilk kurulum başarısız. stdout={first.Stdout}\nstderr={first.Stderr}");

        var binDir = Path.Combine(layout.Vault, ".oom", "bin");
        var beforeLockedAttempt = Snapshot(binDir);

        var sourceTwo = Path.Combine(_root, "kilit-kaynak-2");
        WriteFakeBin(sourceTwo, "iki");

        ReleaseScriptRunner.ScriptResult resultAfterLock;
        using (new FileStream(Path.Combine(binDir, "oom.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            resultAfterLock = ReleaseScriptRunner.RunScript("kur-3.1.0.ps1",
                ["-Vault", layout.Vault, "-Source", sourceTwo, "-Home", layout.Home], _root);
        }

        Assert.True(resultAfterLock.ExitCode != 0,
            $"kilitli hedefe rağmen kurulum başarı bildirdi. stdout={resultAfterLock.Stdout}\nstderr={resultAfterLock.Stderr}");
        Assert.Equal(beforeLockedAttempt, Snapshot(binDir));
    }
}

/// <summary>
/// F9-3 refusal oracle: kur-3.1.0.ps1 and geri-al-3.1.0.ps1 must refuse outright — non-zero
/// exit, nothing written — when the resolved -Vault is the live vault, in ANY case, slash
/// or trailing-separator form, unless -Canli is passed. They must also never create, change
/// or delete a Windows scheduled task.
///
/// SAFETY: this suite never passes the literal string "E:/OdenaOS" (in any case or slash
/// form) to either script under any circumstance. Doing so would mean running an unproven —
/// possibly still buggy — implementation directly against the path the lane brief forbids
/// ever touching, with no way to guarantee the refusal fires before any write. Contract
/// seam added for exactly this reason (named per the task's instruction to name any new API
/// a test needs): the env var OOM_RELEASE_GUARD_VAULT overrides the path the live-vault
/// guard compares -Vault against; in real use it is unset and the guard's own default is
/// literally "E:/OdenaOS" as SPEC-3.1.0.md requires. Every test below points that seam at a
/// SCRATCH directory shaped like the real path ("...\OdenaOS-Kabul-Kopyasi...") and then
/// feeds -Vault the exact casing/slash variations the acceptance text lists, so the guard's
/// comparison logic is exercised end to end without the real E:/OdenaOS path ever appearing
/// as an argument anywhere in this suite.
/// </summary>
public sealed class LiveVaultGuardKabul : IDisposable
{
    private readonly string _root = ScarFixture.TempDirectory();

    public void Dispose() => ScarFixture.Remove(_root);

    [Fact(DisplayName = "F9-3 · kur-3.1.0.ps1 canlı-kasa yoluna (büyük/küçük harf, sondaki ayraç) -Canli'siz reddeder, hiçbir şey yazılmaz")]
    public void Install_RefusesLiveVaultPath_InEveryCasingAndSlashForm_WithoutCanli()
    {
        var guardVault = Path.Combine(_root, "OdenaOS-Kabul-Kopyasi-Kur");
        Directory.CreateDirectory(guardVault);
        var before = Directory.EnumerateFileSystemEntries(guardVault, "*", SearchOption.AllDirectories).ToArray();

        var source = Path.Combine(_root, "kur-canli-kaynak");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "oom.exe"), "kabul-sentetik\n");

        string[] vaultArguments =
        [
            guardVault,
            guardVault + Path.DirectorySeparatorChar,
            guardVault.ToUpperInvariant(),
            guardVault.ToLowerInvariant(),
            guardVault.Replace('\\', '/'),
        ];

        foreach (var vaultArgument in vaultArguments)
        {
            var result = ReleaseScriptRunner.RunScript("kur-3.1.0.ps1",
                ["-Vault", vaultArgument, "-Source", source, "-Home", Path.Combine(_root, "kur-canli-ev")],
                _root,
                new Dictionary<string, string> { ["OOM_RELEASE_GUARD_VAULT"] = guardVault });

            Assert.True(result.ExitCode != 0, $"canlı-kasa görünen yol reddedilmedi: '{vaultArgument}'");
        }

        Assert.Equal(before, Directory.EnumerateFileSystemEntries(guardVault, "*", SearchOption.AllDirectories).ToArray());
    }

    [Fact(DisplayName = "F9-3 · -Canli verilince kur-3.1.0.ps1 canlı-kasa görünen yola kurulum yapar")]
    public void Install_ProceedsOnLiveVaultLookingPath_WhenCanliPassed()
    {
        var guardVault = Path.Combine(_root, "OdenaOS-Kabul-Kopyasi-Canli");
        Directory.CreateDirectory(Path.Combine(guardVault, ".oom"));

        var source = Path.Combine(_root, "canli-kaynak");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "oom.exe"), "kabul-sentetik\n");

        var result = ReleaseScriptRunner.RunScript("kur-3.1.0.ps1",
            ["-Vault", guardVault, "-Source", source, "-Home", Path.Combine(_root, "canli-ev"), "-Canli"],
            _root,
            new Dictionary<string, string> { ["OOM_RELEASE_GUARD_VAULT"] = guardVault });

        Assert.True(result.ExitCode == 0, $"-Canli ile kurulum başarısız oldu. stdout={result.Stdout}\nstderr={result.Stderr}");
        Assert.True(File.Exists(Path.Combine(guardVault, ".oom", "bin", "oom.exe")), "-Canli ile kurulum bin üretmedi");
    }

    [Fact(DisplayName = "F9-3 · geri-al-3.1.0.ps1 canlı-kasa görünen yola -Canli'siz reddeder, mevcut bin değişmez")]
    public void Rollback_RefusesLiveVaultPath_WithoutCanli()
    {
        var guardVault = Path.Combine(_root, "OdenaOS-Kabul-Kopyasi-GeriAl");
        Directory.CreateDirectory(Path.Combine(guardVault, ".oom", "bin"));
        Directory.CreateDirectory(Path.Combine(guardVault, ".oom", "bin.onceki"));
        File.WriteAllText(Path.Combine(guardVault, ".oom", "bin", "oom.exe"), "guncel\n");
        File.WriteAllText(Path.Combine(guardVault, ".oom", "bin.onceki", "oom.exe"), "onceki\n");

        var result = ReleaseScriptRunner.RunScript("geri-al-3.1.0.ps1", ["-Vault", guardVault.ToUpperInvariant()],
            _root, new Dictionary<string, string> { ["OOM_RELEASE_GUARD_VAULT"] = guardVault });

        Assert.True(result.ExitCode != 0, $"canlı-kasa görünen yolda geri alma reddetmedi. stdout={result.Stdout}\nstderr={result.Stderr}");
        Assert.Equal("guncel\n", File.ReadAllText(Path.Combine(guardVault, ".oom", "bin", "oom.exe")));
    }

    /// <summary>Best-effort: depends on the ScheduledTasks PowerShell module being present
    /// and non-erroring in the execution environment. If it errors, the snapshot degrades to
    /// an empty list on both sides rather than failing the suite for an environment reason
    /// unrelated to the scripts under test — see <c>assertions_not_expressible</c> in the
    /// lane report.</summary>
    [Fact(DisplayName = "F9-3 · kurulum + geri alma döngüsü zamanlanmış görev oluşturmaz, değiştirmez, silmez")]
    public void InstallAndRollbackCycle_NeverTouchesScheduledTasks()
    {
        string[] SnapshotScheduledTaskNames()
        {
            try
            {
                var result = ReleaseScriptRunner.RunPowerShellCommand(
                    "Get-ScheduledTask -ErrorAction SilentlyContinue | Select-Object -ExpandProperty TaskName");
                if (result.ExitCode != 0)
                    return [];
                return [.. result.Stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
            }
            catch (TimeoutException)
            {
                return [];
            }
        }

        var before = SnapshotScheduledTaskNames();

        var vault = Path.Combine(_root, "gorev-kasa");
        var source = Path.Combine(_root, "gorev-kaynak");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "oom.exe"), "kabul-sentetik\n");

        ReleaseScriptRunner.RunScript("kur-3.1.0.ps1",
            ["-Vault", vault, "-Source", source, "-Home", Path.Combine(_root, "gorev-ev")], _root);
        ReleaseScriptRunner.RunScript("geri-al-3.1.0.ps1", ["-Vault", vault], _root);

        Assert.Equal(before, SnapshotScheduledTaskNames());
    }
}

/// <summary>
/// NB-12 oracle: tools/release/yerel-kapi.ps1 is the local gate used while the public push
/// remains unapproved (driver ruling: "CI config may be fixed but not triggered"). It must
/// run restore, build and test with NOTHING masking the real `dotnet test` exit code — no
/// pipe, no swallowed try/catch, no trailing `exit 0`. Proven both directions: a healthy run
/// against this lane's own tree exits 0, and a run seeded with the repo's own
/// [Trait("Category","IntentionalRed")] canary (tests/Oom.Tests/Scars/IntentionalRed.cs —
/// "Bu test yalnız --filter IntentionalRed ile çalıştırılır") exits non-zero.
///
/// Contract seam this lane adds: -Filter, passed straight through to `dotnet test --filter`,
/// defaulting to the SPEC R11 interim-gate filter
/// ("Kabul!=Regresyon&Kabul!=RetrieveSet&Category!=IntentionalRed" — the exact string the
/// harness's own build/test instructions use). Without -Filter exposed, seeding the canary
/// would mean editing the script's default just to prove its exit code is not masked.
/// </summary>
public sealed class LocalGateKabul
{
    [Fact(DisplayName = "NB-12 · yerel-kapi.ps1 sağlıklı ağaçta restore+build+test'i maskesiz koşar, sıfır çıkışla biter")]
    public void HealthyTree_ExitsZero()
    {
        var repoRoot = ReleaseScriptRunner.RepoRoot();
        var result = ReleaseScriptRunner.RunScript("yerel-kapi.ps1", [], repoRoot, timeout: TimeSpan.FromMinutes(10));

        var tail = result.Stdout.Length > 4000 ? result.Stdout[^4000..] : result.Stdout;
        Assert.True(result.ExitCode == 0, $"sağlıklı ağaçta beklenmeyen çıkış {result.ExitCode}. stdout (son 4000): {tail}\nstderr={result.Stderr}");
    }

    [Fact(DisplayName = "NB-12 · IntentionalRed tohumlu -Filter koşumu sıfır olmayan çıkışla biter — test çıkış kodu maskelenmiyor")]
    public void SeededIntentionalRedRun_ExitsNonZero()
    {
        var repoRoot = ReleaseScriptRunner.RepoRoot();
        var result = ReleaseScriptRunner.RunScript("yerel-kapi.ps1", ["-Filter", "Category=IntentionalRed"], repoRoot, timeout: TimeSpan.FromMinutes(5));

        Assert.True(result.ExitCode != 0,
            $"IntentionalRed tohumu maskelendi, betik yine de sıfır çıkışla bitti. stdout={result.Stdout}\nstderr={result.Stderr}");
    }
}

/// <summary>
/// F9-2 oracle: .github/workflows/ci.yml (never triggered by this build — the public push
/// is not approved, per the driver rulings) must run the same unmasked Release restore/
/// build/test sequence, with a filter that excludes the tests needing private data no CI
/// runner has: Kabul=OzelVault and Kabul=RetrieveSet (OOM_KABUL_PRIVATE / OOM_KABUL_VAULT /
/// OOM_KABUL_EVAL_SET), plus Category=IntentionalRed (the canary designed to fail whenever
/// it runs at all).
///
/// This is a REAL, present defect on this tree, not a hypothetical one: today's checked-in
/// filter is <c>"Kabul!=Regresyon&amp;Kabul!=RetrieveSet"</c> (verified by reading
/// .github/workflows/ci.yml directly) — it excludes neither Kabul=OzelVault nor
/// Category=IntentionalRed, so a real push would either hard-fail on the canary or run
/// OzelVault tests against a runner that has none of the three OOM_KABUL_* private-data
/// variables they need. This test needs no PowerShell process at all — it reads the checked-
/// in YAML directly — so it fails on the current tree immediately, with no build required.
/// </summary>
public sealed class CiWorkflowKabul
{
    [Fact(DisplayName = "F9-2 · ci.yml'nin dotnet test filtresi Kabul=OzelVault, Kabul=RetrieveSet ve Category=IntentionalRed'i dışlar; hiçbir boru çıkış kodunu maskelemez")]
    public void CiTestStep_ExcludesPrivateAndCanaryTraits_WithNoMaskingPipe()
    {
        var ciPath = Path.Combine(ReleaseScriptRunner.RepoRoot(), ".github", "workflows", "ci.yml");
        Assert.True(File.Exists(ciPath), $"ci.yml bulunamadı: {ciPath}");

        var testStepLine = File.ReadAllLines(ciPath)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("- run:", StringComparison.Ordinal) && line.Contains("dotnet test", StringComparison.Ordinal));

        Assert.True(testStepLine is not null, "ci.yml içinde 'dotnet test' koşan bir adım bulunamadı");
        Assert.Contains("Kabul!=OzelVault", testStepLine, StringComparison.Ordinal);
        Assert.Contains("Kabul!=RetrieveSet", testStepLine, StringComparison.Ordinal);
        Assert.Contains("Category!=IntentionalRed", testStepLine, StringComparison.Ordinal);
        Assert.DoesNotContain('|', testStepLine);
        Assert.DoesNotContain("exit 0", testStepLine, StringComparison.OrdinalIgnoreCase);
    }
}
