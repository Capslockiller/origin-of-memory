using System.Diagnostics;
using System.Text.RegularExpressions;
using Oom.Contracts;

namespace Oom.Tests.Scars;

/// <summary>
/// Review finding (should #4, L1-cli-surface repair): CliKabul's F9-1 oracle accepts
/// "3.1.0+unknown" as a passing version string (a legitimate fallback when git is
/// unavailable at build time), so a build target that ALWAYS fell back to "unknown" would
/// pass every existing gate unnoticed. This test closes that hole for the normal case where
/// git IS available: it independently re-derives the current commit with `git rev-parse
/// --short HEAD` and asserts BuildInfo.Version matches it exactly. It skips (does not fail)
/// only when git itself is unavailable or errors, mirroring the csproj's own fallback rule
/// (Oom.csproj's SetBuildRevision target) rather than asserting anything about that case.
/// </summary>
public sealed class BuildInfoScars
{
    [Fact(DisplayName = "BuildInfo-01 · BuildInfo.Version = '3.1.0+' + git rev-parse --short HEAD (git varsa)")]
    public void Version_MatchesGitShortHead_WhenGitIsAvailable()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
            return; // No .git above the test binaries (e.g. a git-less archive) — nothing to compare against.

        string hash;
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            })!;
            hash = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit();
            if (git.ExitCode != 0 || !Regex.IsMatch(hash, "^[0-9a-fA-F]{7,40}$"))
                return; // git failed or gave garbage — the csproj's own "unknown" fallback rule applies, not this test.
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            return; // git not on PATH.
        }

        Assert.Equal($"3.1.0+{hash}", BuildInfo.Version);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
