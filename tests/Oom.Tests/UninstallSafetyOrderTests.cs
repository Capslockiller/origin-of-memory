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

            // Şerit U: the registrations have to be THIS vault's before the uninstall may touch
            // them. `fixture\oom.exe` named nothing on this machine, so under ownership
            // verification it is a stranger's command and correctly survives — which would have
            // made the assertions below assert the opposite of what this test is here to protect.
            var executable = Path.Combine(vault, ".oom", "oom.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "fixture binary");

            var settingsPath = Path.Combine(profile, ".claude", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, new JsonObject
            {
                ["hooks"] = new JsonObject
                {
                    ["SessionStart"] = new JsonArray(new JsonObject
                    {
                        ["hooks"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = $"\"{executable}\" hook"
                        })
                    })
                }
            }.ToJsonString());

            var mcpPath = Path.Combine(profile, "mcp", "claude_desktop_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(mcpPath)!);
            File.WriteAllText(mcpPath, new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["oom"] = new JsonObject { ["command"] = executable },
                    ["other"] = new JsonObject { ["command"] = "other.exe" }
                }
            }.ToJsonString());

            var processes = new RecordingProcessRunner(executable);
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
            // The protected contract is that the safe, identity-independent cleanup happens before
            // the identity path can be refused — not that a fixed task name is deleted on sight.
            // Deleting is now preceded by asking the machine which binary the task actually runs.
            Assert.Equal(2, processes.Requests.Count);
            Assert.All(processes.Requests, request => Assert.Equal("schtasks.exe", request.FileName));
            Assert.Equal(["/Query", "/TN", "OdenaOS Memory Sweep", "/XML"], processes.Requests[0].Arguments);
            Assert.Equal(["/Delete", "/TN", "OdenaOS Memory Sweep", "/F"], processes.Requests[1].Arguments);

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

    private sealed class RecordingProcessRunner(string registeredCommand) : IProcessRunner
    {
        internal List<ProcessRequest> Requests { get; } = [];

        public ProcessResult Run(ProcessRequest request, TimeSpan timeout)
        {
            Requests.Add(request);
            // The machine's answer to "which binary does this task run?"; without it the task is
            // unattributable and, correctly, would be left registered.
            var output = request.Arguments.Contains("/Query")
                ? $"<Task><Actions><Exec><Command>{registeredCommand}</Command></Exec></Actions></Task>"
                : string.Empty;
            return new ProcessResult(0, output, string.Empty, true);
        }
    }
}
