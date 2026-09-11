// yazan: codex · gpt-5
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests;

public sealed class UninstallSafetyOrderTests
{
    [Fact]
    public void RejectedIdentityPath_StillCleansIndependentRegistrations_AndReturnsPartial()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var vault = Path.Combine(root, "vault");
            var local = Path.Combine(root, "local");
            var profile = Path.Combine(root, "profile");
            Directory.CreateDirectory(vault);
            Directory.CreateDirectory(local);

            var identity = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(Path.GetFullPath(vault).ToUpperInvariant())))[..24].ToLowerInvariant();
            var rejectedIdentity = Path.Combine(local, "oom", "claude-config", identity);
            Directory.CreateDirectory(rejectedIdentity);
            var canaryPath = Path.Combine(rejectedIdentity, "canary.bin");
            byte[] canary = [0x00, 0xFF, 0x4F, 0x4F, 0x4D, 0x0A];
            File.WriteAllBytes(canaryPath, canary);

            var settingsPath = Path.Combine(profile, ".claude", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath,
                """{"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"fixture\\oom.exe hook"}]}]}}""");

            var mcpPath = Path.Combine(profile, "mcp", "claude_desktop_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(mcpPath)!);
            File.WriteAllText(mcpPath,
                """{"mcpServers":{"oom":{"command":"fixture\\oom.exe"},"other":{"command":"other.exe"}}}""");

            var processes = new RecordingProcessRunner();
            var install = new Install(
                processRunner: processes,
                userSettingsPath: () => settingsPath,
                mcpCandidates: () => [mcpPath],
                processPath: () => null,
                localAppDataPath: () => local,
                knownSyncRoots: () => [rejectedIdentity],
                shortcutRemover: () => false,
                eventLogRemover: () => { },
                stateRootPath: _ => Path.Combine(root, "state"));

            var result = install.Uninstall(vault);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Contains("hooks:kaldırıldı", result.Registrations);
            Assert.Contains("task:kaldırıldı", result.Registrations);
            Assert.Contains("mcp:kaldırıldı", result.Registrations);
            var task = Assert.Single(processes.Requests);
            Assert.Equal("schtasks.exe", task.FileName);
            Assert.Equal(["/Delete", "/TN", "OdenaOS Memory Sweep", "/F"], task.Arguments);

            var settings = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
            Assert.False(settings?["hooks"]?.AsObject().ContainsKey("SessionStart"));
            var servers = JsonNode.Parse(File.ReadAllText(mcpPath))?["mcpServers"]?.AsObject();
            Assert.NotNull(servers);
            Assert.False(servers.ContainsKey("oom"));
            Assert.True(servers.ContainsKey("other"));
            Assert.Equal(canary, File.ReadAllBytes(canaryPath));
        }
        finally { ScarFixture.Remove(root); }
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        internal List<ProcessRequest> Requests { get; } = [];

        public ProcessResult Run(ProcessRequest request, TimeSpan timeout)
        {
            Requests.Add(request);
            return new ProcessResult(0, string.Empty, string.Empty, true);
        }
    }
}
