using System.Diagnostics;
using System.Text;
using Oom.Contracts;
using Oom.Tests.Kabul.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// Acceptance oracle for lane L5d-hook-quote (SPEC-3.1.0.md external review finding #3,
/// verified REAL by an Opus judge on da8138d): <c>HookTemplates.Quote</c> wraps an
/// executable or vault path in double quotes only when the path contains a space. Any
/// other shell-special character — <c>&amp;</c>, <c>'</c>, <c>$</c>, a backtick, <c>"</c>,
/// <c>%</c>, <c>^</c>, <c>|</c>, <c>&lt;</c>, <c>&gt;</c>, <c>(</c>, <c>)</c>, <c>;</c> — is
/// written into <c>.claude/settings.json</c> UNQUOTED, and the shell that Claude Code's
/// hook runner launches the command through (<c>bash -c</c> on POSIX-style shells,
/// <c>cmd /c</c> on Windows) then mis-parses it. This is silent: a hook's stdout/stderr
/// never reaches Master, so a cut <c>--vault</c> argument or an outright parse failure
/// just looks like "nothing happened this session".
///
/// MEASURED on da8138d (this lane's own starting tree — the build this file's own asserts
/// run against by default) while building this oracle, exact commands in this lane's
/// report:
///  - A vault at <c>&lt;scratch&gt;/Proj&amp;Test/vault</c> built into the SessionStart hook
///    command with TODAY's <c>HookTemplates.Quote</c> (unquoted, no space in either path)
///    and run via <c>bash -c</c>: bash reports "unexpected EOF while looking for matching
///    `''" (the bare apostrophe two segments earlier opens a quoted string bash never
///    finds a matching close for) — the whole command fails before anything runs. Run via
///    <c>cmd /c</c> (with the well-known <c>cmd /d /s /c "&lt;whole command&gt;"</c>
///    outer-quoting a real launcher uses): exit 2, "Test: missing argument after
///    'context'", and argv reconstructed by an argv-echo probe standing in for oom.exe is
///    <c>[--vault, …/Proj]</c> — cmd treated the unquoted <c>&amp;</c> as its own
///    command-separator and cut the vault argument there.
///  - An exe path under a directory literally named <c>O'Brien</c>, quoted with nothing
///    else on the command line, already fails bash on its own with the same "unexpected
///    EOF while looking for matching `''" — reproduced in isolation before this file's own
///    assertion even runs.
///  - Once both paths are unconditionally double-quoted, the SAME four commands round-trip
///    through both shells with argv exactly <c>[--vault, &lt;vault, forward-slashed&gt;,
///    &lt;hook verb and args&gt;]</c> — see
///    <see cref="HookCommands_SurviveAmpersandVaultAndApostropheExe_UnderBashAndCmd"/>.
///  - <c>oom install</c> performs no validation of the vault path string at all today: a
///    vault containing <c>$</c>, a backtick or <c>%</c> installs cleanly, exit 0,
///    <c>.claude/settings.json</c> written with the unsafe, unquotable path baked into
///    every hook command (measured identically on this tree's own build AND on
///    OOM_KABUL_EXE = eski-exe/oom.exe). A vault containing <c>"</c> does exit non-zero
///    today, but only because <c>Directory.CreateDirectory</c> throws an unhandled
///    <c>IOException</c> in English ("Dosya adı, dizin adı veya birim etiketi sözdizimi
///    hatalı") — not because anything recognised the character and refused cleanly; no
///    single quoting scheme makes any of these four characters safe for bash AND cmd at
///    once (a literal <c>"</c> breaks the surrounding quotes outright; <c>$</c> and a
///    backtick are still expanded by bash INSIDE double quotes; <c>%…%</c> is still
///    expanded by cmd inside its own quotes), so the only sound fix is refusing the
///    install outright, loudly, in Turkish, naming the character — see
///    <see cref="Install_RefusesVaultPathWithUnsafeCharacter_SettingsUntouched"/> and
///    <see cref="Install_RefusedInstall_LeavesExistingSettingsByteIdentical"/>.
///
/// Test seam for the shell round-trip: <see cref="BuildProbeOnce"/> compiles a tiny,
/// throwaway .NET console app ONCE per test run, into this test assembly's own isolated
/// build output (never the repo, never committed — same AppContext.BaseDirectory
/// convention <see cref="KabulHarness"/> itself uses, chosen for the identical reason:
/// audit A2-12 found the existing Scars suite location-dependent because it resolved paths
/// relative to the OS temp directory). Copied under whatever special-character directory a
/// test needs (always as <c>oom.exe</c> plus its apphost companions, so its identity as
/// "the exe HookTemplates.Build was told about" is exact), it stands in for the real
/// oom.exe: a REAL Windows PE, launched by a REAL CreateProcess call — the same mechanism
/// both bash's fork/exec emulation and cmd.exe's own command execution ultimately go
/// through — so the argv it echoes (plain <c>string[] args</c> in <c>Main</c>) is exactly
/// what the real oom.exe would have received from the same hook command line, with no
/// test-only shortcut in between. <c>oom install</c> itself is exercised black-box, through
/// <see cref="KabulHarness"/>, for the refusal behaviour (assertion 2); the quoting logic
/// that decides what a plain path looks like today (assertion 3) is checked unit-level,
/// directly against <see cref="HookTemplates.Build"/>, since a string equality check needs
/// no process boundary.
/// </summary>
public sealed class HookQuoteKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly object ProbeBuildLock = new();
    private static string? _probeBinDir;

    // ------------------------------------------------------------------
    // Assertion 1 — a vault path containing '&' and an exe path under a directory literally
    // named "O'Brien" (SPEC's own worked example) round-trip through BOTH bash -c and
    // cmd /c with argv exactly [--vault, <vault, forward-slashed>, <hook verb and args>],
    // for all four hook events.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "L5d-hook-quote #1 · '&' içeren vault ve O'Brien altındaki exe: dört kanca komutu da bash -c VE cmd /c altında argv'yi tam korur")]
    public void HookCommands_SurviveAmpersandVaultAndApostropheExe_UnderBashAndCmd()
    {
        SkipUnlessWindows();

        using var harness = new KabulHarness();
        var exeDir = harness.NewScratchDirectory("O'Brien");
        var probeExe = CopyProbeTo(exeDir);

        var vault = Path.Combine(harness.NewScratchDirectory("Proj&Test"), "vault");
        Directory.CreateDirectory(vault);

        var expectedVaultArgument = vault.Replace(Path.DirectorySeparatorChar, '/');
        (string Event, string[] Tail)[] expectations =
        [
            ("SessionStart", ["context"]),
            ("UserPromptSubmit", ["nudge"]),
            ("SessionEnd", ["flush", "--reason", "sessionend"]),
            ("PreCompact", ["flush", "--reason", "precompact"]),
        ];

        var registrations = HookTemplates.Build(probeExe, vault);
        Assert.Equal(expectations.Length, registrations.Count);

        var failures = new List<string>();
        foreach (var (eventName, tail) in expectations)
        {
            var registration = registrations.Single(r => r.Event == eventName);
            var expectedArgv = new[] { "--vault", expectedVaultArgument }.Concat(tail).ToArray();

            var bashArgv = RunViaBash(registration.Command);
            if (!bashArgv.SequenceEqual(expectedArgv))
                failures.Add($"{eventName} - bash -c: beklenen [{string.Join(", ", expectedArgv)}], gelen [{string.Join(", ", bashArgv)}]\n  komut: {registration.Command}");

            var cmdArgv = RunViaCmd(registration.Command);
            if (!cmdArgv.SequenceEqual(expectedArgv))
                failures.Add($"{eventName} - cmd /c: beklenen [{string.Join(", ", expectedArgv)}], gelen [{string.Join(", ", cmdArgv)}]\n  komut: {registration.Command}");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    // ------------------------------------------------------------------
    // Assertion 2 - `oom install` refuses a vault path containing a character no single
    // quoting scheme makes safe for bash AND cmd at once ('"', '$', a backtick, '%'):
    // non-zero exit, and the refusal is a real, handled one (never a raw .NET exception
    // dump), leaving no trace under the vault.
    // ------------------------------------------------------------------
    [Theory(DisplayName = "L5d-hook-quote #2 - Guvensiz karakter tasiyan vault yolunda `oom install` reddeder, iz birakmaz")]
    [InlineData('"')]
    [InlineData('$')]
    [InlineData('`')]
    [InlineData('%')]
    public void Install_RefusesVaultPathWithUnsafeCharacter_SettingsUntouched(char unsafeCharacter)
    {
        using var harness = new KabulHarness();
        var baseDir = harness.NewScratchDirectory("kasa-guvensiz-" + (int)unsafeCharacter);

        // Windows forbids a literal '"' as a real filename character outright, so that one
        // case is passed only in the --vault ARGUMENT STRING (never as a directory the test
        // itself creates) - the refusal must fire at argument validation, before any
        // filesystem call the exe has no business making on an unsafe string in the first
        // place. The other three characters ARE legal Windows filename characters, so those
        // vaults are real directories, matching how a vault path arrives in practice.
        var vault = unsafeCharacter == '"'
            ? Path.Combine(baseDir, "kasa\"tirnakli")
            : Directory.CreateDirectory(Path.Combine(baseDir, $"kasa{unsafeCharacter}ozel")).FullName;

        var settingsPath = Path.Combine(vault, ".claude", "settings.json");
        var oomDir = Path.Combine(vault, ".oom");

        var result = harness.Run(vault, ["install"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("Exception", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(unsafeCharacter.ToString(), result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(settingsPath), $"kurulum reddedilmeliydi ama settings.json yazilmis:\n{result.Stdout}\n{result.Stderr}");
        Assert.False(Directory.Exists(oomDir), $"kurulum reddedilmeliydi ama .oom yazilmis:\n{result.Stdout}\n{result.Stderr}");
    }

    // ------------------------------------------------------------------
    // Assertion 2b - the same refusal, but against a vault that already has a
    // .claude/settings.json (some OTHER tool's own hooks, say): the file must come out of
    // a refused install byte-identical, not merely "still present".
    // ------------------------------------------------------------------
    [Fact(DisplayName = "L5d-hook-quote #2b - Reddedilen kurulum var olan settings.json'u bayt bayt ayni birakir")]
    public void Install_RefusedInstall_LeavesExistingSettingsByteIdentical()
    {
        using var harness = new KabulHarness();
        var vault = Directory.CreateDirectory(Path.Combine(harness.NewScratchDirectory("kasa-mevcut-ayar"), "kasa%ozel")).FullName;
        var settingsPath = Path.Combine(vault, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        const string original = """{"permissions":{"allow":["Bash"]}}""";
        File.WriteAllText(settingsPath, original, Utf8);
        var before = File.ReadAllBytes(settingsPath);

        var result = harness.Run(vault, ["install"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(before, File.ReadAllBytes(settingsPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(settingsPath)!, "*.bak-*"));
    }

    // ------------------------------------------------------------------
    // Assertion 3 - a plain path (no shell-special character, no space) produces the exact
    // hook verb/arg text of today; the ONLY change is that every path is now always
    // double-quoted, not just when it contains a space.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "L5d-hook-quote #3 - Siradan yol (ozel karaktersiz): metin bugunkuyle ayni, yalniz her yol artik her zaman cift tirnakli")]
    public void PlainPaths_AreAlwaysDoubleQuoted_VerbsAndArgsUnchanged()
    {
        var registrations = HookTemplates.Build(@"E:\OdenaOS\.oom\bin\oom.exe", @"E:\OdenaOS");

        Assert.Collection(registrations,
            r => Assert.Equal("\"E:/OdenaOS/.oom/bin/oom.exe\" --vault \"E:/OdenaOS\" context", r.Command),
            r => Assert.Equal("\"E:/OdenaOS/.oom/bin/oom.exe\" --vault \"E:/OdenaOS\" nudge", r.Command),
            r => Assert.Equal("\"E:/OdenaOS/.oom/bin/oom.exe\" --vault \"E:/OdenaOS\" flush --reason sessionend", r.Command),
            r => Assert.Equal("\"E:/OdenaOS/.oom/bin/oom.exe\" --vault \"E:/OdenaOS\" flush --reason precompact", r.Command));
    }

    // ====================================================================
    // Shell round-trip plumbing (assertion 1 only)
    // ====================================================================

    private static void SkipUnlessWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "L5d-hook-quote #1 yalniz Windows'ta kosar (bash.exe ve cmd.exe gerektirir).");
    }

    private static string ResolveBashExe()
    {
        var overridePath = Environment.GetEnvironmentVariable("OOM_KABUL_BASH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        // NOT plain "bash" resolved via PATH: on a machine with WSL's App Execution Alias
        // ahead of Git for Windows on PATH, that resolves to %WINDIR%\system32\bash.exe (a
        // WSL launcher stub), which fails outright if no WSL distro is installed - measured
        // on this exact machine while building this oracle. Git Bash's real bash.exe is
        // looked up by its well-known install path instead.
        string[] candidates =
        [
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
        ];
        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return candidate;

        throw new FileNotFoundException(
            "Git Bash (bash.exe) bulunamadi. OOM_KABUL_BASH ile tam yolu ver, ya da Git for Windows kur.");
    }

    private static string ResolveCmdExe()
    {
        var comspec = Environment.GetEnvironmentVariable("COMSPEC");
        if (!string.IsNullOrWhiteSpace(comspec) && File.Exists(comspec))
            return comspec;

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        if (File.Exists(fallback))
            return fallback;

        throw new FileNotFoundException("cmd.exe bulunamadi (COMSPEC ayarli degil).");
    }

    private static string[] RunViaBash(string command)
    {
        var startInfo = new ProcessStartInfo(ResolveBashExe())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // ArgumentList (not a raw string): bash.exe is a normal argv-consuming console
        // app, so letting .NET apply the standard MSVCRT quoting/escaping convention here
        // is correct - bash then receives this ENTIRE command as its single -c argument,
        // literal embedded double quotes intact, and parses THOSE as bash syntax itself.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        return RunAndReadArgv(startInfo);
    }

    private static string[] RunViaCmd(string command)
    {
        var startInfo = new ProcessStartInfo(ResolveCmdExe())
        {
            // Raw Arguments, NOT ArgumentList: cmd.exe does not follow the standard
            // MSVCRT argv convention ArgumentList's quoting targets - it calls
            // GetCommandLineW itself and parses everything after /c with its own,
            // idiosyncratic rules. A command that itself contains double quotes (every
            // HookTemplates.Build command does, once fixed) must reach cmd verbatim,
            // wrapped in exactly one MORE, outer pair of quotes spanning the whole thing -
            // the documented cmd /c workaround: cmd strips only the very first and very
            // last quote of the /c argument when the whole remainder is quoted end to end.
            // Without that outer wrap, cmd's parser misfires even on a plain, entirely
            // unspecial quoted path ("the filename, directory name or volume label syntax
            // is incorrect") - measured empirically while building this oracle, kept here
            // so the next reader does not have to rediscover it.
            Arguments = "/d /s /c \"" + command + "\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return RunAndReadArgv(startInfo);
    }

    private static string[] RunAndReadArgv(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            throw new TimeoutException($"{startInfo.FileName} 15 saniyede donmedi: {startInfo.Arguments}");
        }
        return stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string CopyProbeTo(string destinationDir)
    {
        var binDir = BuildProbeOnce();
        Directory.CreateDirectory(destinationDir);
        foreach (var name in new[] { "oom.exe", "oom.dll", "oom.deps.json", "oom.runtimeconfig.json" })
            File.Copy(Path.Combine(binDir, name), Path.Combine(destinationDir, name), overwrite: true);
        return Path.Combine(destinationDir, "oom.exe");
    }

    /// <summary>
    /// Builds the argv-echo probe exactly once per test run (cached in a static field, and
    /// on disk across runs since it lives under this test assembly's own persistent build
    /// output - a second run finds the exe already there and skips straight past the
    /// `dotnet build` call). Never touches Oom.sln or anything under source control: the
    /// probe's own tiny project lives under AppContext.BaseDirectory, generated at test
    /// time, the same way KabulHarness's own "kabul-runs" scratch root is.
    /// </summary>
    private static string BuildProbeOnce()
    {
        lock (ProbeBuildLock)
        {
            if (_probeBinDir is not null)
                return _probeBinDir;

            var projectDir = Path.Combine(AppContext.BaseDirectory, "hook-quote-argv-probe");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, "probe.csproj"), ProbeCsproj, Utf8);
            File.WriteAllText(Path.Combine(projectDir, "Program.cs"), ProbeProgramCs, Utf8);

            var binDir = Path.Combine(projectDir, "bin", "Release", "net9.0");
            var exePath = Path.Combine(binDir, "oom.exe");
            if (!File.Exists(exePath))
            {
                var startInfo = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = projectDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                startInfo.ArgumentList.Add("build");
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("Release");
                startInfo.ArgumentList.Add("-v");
                startInfo.ArgumentList.Add("quiet");

                using var process = Process.Start(startInfo)!;
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(120_000))
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                    throw new TimeoutException("argv-echo probe derlemesi 120 saniyede bitmedi.");
                }
                if (process.ExitCode != 0 || !File.Exists(exePath))
                    throw new InvalidOperationException(
                        $"argv-echo probe derlenemedi (exit {process.ExitCode}):\n{stdout}\n{stderr}");
            }

            _probeBinDir = binDir;
            return binDir;
        }
    }

    private const string ProbeCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net9.0</TargetFramework>
            <AssemblyName>oom</AssemblyName>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <InvariantGlobalization>true</InvariantGlobalization>
          </PropertyGroup>
        </Project>
        """;

    // Prints each argument this process's own argv delivered, one per line, exactly as
    // received - nothing else. Standing in for the real oom.exe at the front of a hook
    // command line, it lets HookQuoteKabul observe the REAL, OS-delivered argv on the far
    // side of whatever bash or cmd did to the command text, rather than trusting a
    // re-implementation of either shell's parsing rules.
    private const string ProbeProgramCs = """
        foreach (var a in args)
            Console.WriteLine(a);
        """;
}
