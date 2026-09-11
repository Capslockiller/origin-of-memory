// yazan: codex · gpt-5
using System.Diagnostics;
using System.Security.Cryptography;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests;

public sealed class KimlikScars
{
    private const string Canary = "OOM_SYNTHETIC_CREDENTIAL_CANARY_7f691c";

    [Fact]
    public void Install_InGitVault_StagesNoCredentialShapedFile()
    {
        var root = ScarFixture.TempDirectory();
        var vault = Path.Combine(root, "Türkçe kasa");
        var local = Path.Combine(root, "yerel-veri");
        var claude = Path.Combine(root, "claude-kaynak");
        var profile = Path.Combine(root, "profil");
        Directory.CreateDirectory(vault);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(claude);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(claude, ".credentials.json"), $"{{\"token\":\"{Canary}\"}}");
        var executable = Path.Combine(root, "oom.exe");
        File.WriteAllText(executable, "synthetic executable");
        Run("git.exe", vault, "init");

        try
        {
            var install = FixtureInstall(root, local, claude, profile, executable);
            var result = install.Run(vault);
            Assert.True(result.Success, result.Error);

            Run("git.exe", vault, "add", "-A");
            var staged = Run("git.exe", vault, "diff", "--cached", "--name-only");
            Assert.DoesNotContain(staged.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                name => name.Contains("credential", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(Directory.EnumerateFiles(vault, "*", SearchOption.AllDirectories)
                    .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")),
                path => File.ReadAllText(path).Contains(Canary, StringComparison.Ordinal));
        }
        finally
        {
            ScarFixture.Remove(root);
        }
    }

    [Fact]
    public void IsolationPath_ResolvingThroughJunctionIntoVault_IsRejected()
    {
        var root = ScarFixture.TempDirectory();
        var vault = Path.Combine(root, "kasa");
        var local = Path.Combine(root, "local");
        var productLink = Path.Combine(local, "oom");
        Directory.CreateDirectory(vault);
        Directory.CreateDirectory(local);
        try
        {
            Run(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", root,
                "/c", "mklink", "/J", productLink, vault);
            Assert.Throws<InvalidOperationException>(() =>
                ClaudeIsolation.ConfigurationDirectory(vault, local, []));
        }
        finally
        {
            if (Directory.Exists(productLink)) Directory.Delete(productLink, recursive: false);
            ScarFixture.Remove(root);
        }
    }

    [Fact]
    public void Uninstall_OwnedCopy_LeavesSourceHashUnchanged()
    {
        var root = ScarFixture.TempDirectory();
        var vault = Path.Combine(root, "kasa");
        var local = Path.Combine(root, "local");
        var claude = Path.Combine(root, "claude");
        var profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(vault);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(claude);
        Directory.CreateDirectory(profile);
        var source = Path.Combine(claude, ".credentials.json");
        File.WriteAllText(source, Canary);
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var config = ClaudeIsolation.ConfigurationDirectory(vault, local, []);
        Assert.Equal("kopyalandı", ClaudeIsolation.Prepare(config, vault, claude, local, []));

        try
        {
            var install = FixtureInstall(root, local, claude, profile, processPath: null);
            var result = install.Uninstall(vault);
            Assert.True(result.Success);
            Assert.False(Directory.Exists(config));
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
        }
        finally { ScarFixture.Remove(root); }
    }

    [Fact]
    public void Refresh_UsesContentHash_NotTimestamp()
    {
        var root = ScarFixture.TempDirectory();
        var vault = Path.Combine(root, "kasa");
        var local = Path.Combine(root, "local");
        var claude = Path.Combine(root, "claude");
        Directory.CreateDirectory(vault);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(claude);
        var source = Path.Combine(claude, ".credentials.json");
        File.WriteAllText(source, "old-token");
        var config = ClaudeIsolation.ConfigurationDirectory(vault, local, []);
        Assert.Equal("kopyalandı", ClaudeIsolation.Prepare(config, vault, claude, local, []));
        var timestamp = File.GetLastWriteTimeUtc(Path.Combine(config, ".credentials.json"));
        File.WriteAllText(source, "new-token");
        File.SetLastWriteTimeUtc(source, timestamp.AddDays(-1));

        try
        {
            Assert.Equal("kopyalandı", ClaudeIsolation.Prepare(config, vault, claude, local, []));
            Assert.Equal("new-token", File.ReadAllText(Path.Combine(config, ".credentials.json")));
        }
        finally { ScarFixture.Remove(root); }
    }

    [Theory]
    [InlineData("http://192.0.2.10:11434/v1")]
    [InlineData("http://user:pass@127.0.0.1:11434/v1")]
    [InlineData("ftp://127.0.0.1:11434/v1")]
    public void LocalUrl_UnsafeValue_IsRejected(string url) =>
        Assert.Throws<FormatException>(() => OomSettings.ValidateLocalUrl(url));

    [Fact]
    public void LocalUrl_DefaultIsNumericLoopback()
    {
        Assert.Equal("http://127.0.0.1:11434/v1", OomSettings.Defaults().Backend.Local.Url);
        Assert.Throws<FormatException>(() => OomSettings.ValidateLocalUrl("http://localhost:11434/v1"));
    }

    private static Install FixtureInstall(string root, string local, string claude, string profile, string? processPath) => new(
        scheduler: new FakeScheduler(),
        windowsSupported: () => true,
        commandAvailable: _ => true,
        fts5Available: () => true,
        shortcutRegistrar: _ => false,
        eventLogRegistrar: () => true,
        userSettingsPath: () => Path.Combine(profile, ".claude", "settings.json"),
        mcpCandidates: () => [Path.Combine(profile, "mcp", "claude_desktop_config.json")],
        processPath: () => processPath,
        localAppDataPath: () => local,
        claudeUserConfigPath: () => claude,
        knownSyncRoots: () => [],
        shortcutRemover: () => false,
        eventLogRemover: () => { },
        stateRootPath: _ => Path.Combine(root, "state"),
        doctorAction: () => { });

    private static string Run(string fileName, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Başlatılamadı: {fileName}");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{fileName} çıkış {process.ExitCode}: {error}");
        return output;
    }

    private sealed class FakeScheduler : ITaskScheduler
    {
        public void Register(string name, string xml) { }
    }
}
