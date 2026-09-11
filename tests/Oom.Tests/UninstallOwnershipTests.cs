// Şerit U — kaldırma yalnız sahipliği doğrulanmış hedeflere dokunur.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests;

/// <summary>
/// Every registration oom writes to a machine is keyed by a constant that carries no vault: one
/// task name, one shortcut path, one AUMID, one Event Log source, one MCP key <c>oom</c>, and a
/// shared <c>settings.json</c> whose hook commands merely contain the substring <c>oom.exe</c>.
/// Uninstall used to read those constants as ownership. This suite puts vault A, vault B and a
/// foreign <c>oom.exe</c> in one fixture and pins that uninstalling A reaches none of the other
/// two — and that a step which was refused, skipped or failed is never reported as removed.
///
/// Nothing here touches the running machine: every path an <see cref="Install"/> can reach is
/// injected, the scheduler is driven by a scripted process runner, and the shortcut and Event Log
/// boundaries are seams.
/// </summary>
public sealed class UninstallOwnershipTests
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly string[] HookEvents = ["SessionStart", "UserPromptSubmit", "SessionEnd", "PreCompact"];

    // ---------------------------------------------------------------- fixture

    /// <summary>One vault plus the per-vault machine paths an install of it would reach.</summary>
    private sealed class Vault(string root, string name)
    {
        internal string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(root, name)).FullName;
        internal string StateRoot { get; } = System.IO.Path.Combine(root, $"state-{name}");
        internal string Local { get; } = Directory.CreateDirectory(System.IO.Path.Combine(root, $"local-{name}")).FullName;
        internal string ClaudeSource { get; } = Directory.CreateDirectory(System.IO.Path.Combine(root, $"claude-{name}")).FullName;

        internal string Executable => System.IO.Path.Combine(Path, ".oom", "oom.exe");
        internal string ProjectSettings => Install.ProjectSettingsPath(Path);
        internal string ProjectMcp => Install.ProjectMcpPath(Path);
    }

    /// <summary>The files and registrations that exist exactly once per machine.</summary>
    private sealed class Machine(string root)
    {
        internal string UserSettings { get; } = System.IO.Path.Combine(root, "profil", ".claude", "settings.json");
        internal string DesktopMcp { get; } = System.IO.Path.Combine(root, "profil", "mcp", "claude_desktop_config.json");
        internal List<string> ShortcutAsks { get; } = [];
        internal int EventLogRemovals { get; set; }
        internal string? EventLogTarget { get; set; }
    }

    /// <summary>Answers <c>schtasks</c> from a script instead of from the machine.</summary>
    private sealed class ScriptedSchtasks(string? registeredCommand, int deleteExitCode = 0, int queryExitCode = 0) : IProcessRunner
    {
        internal List<IReadOnlyList<string>> Calls { get; } = [];

        public ProcessResult Run(ProcessRequest request, TimeSpan timeout)
        {
            Calls.Add(request.Arguments);
            if (request.Arguments.Contains("/Query"))
                return new ProcessResult(queryExitCode, queryExitCode == 0 ? TaskXml(registeredCommand) : string.Empty, string.Empty, true);
            return new ProcessResult(deleteExitCode, string.Empty, deleteExitCode == 0 ? string.Empty : "HATA", true);
        }

        private static string TaskXml(string? command) => command is null
            ? "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task><Actions /></Task>"
            : $"<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task><Actions Context=\"Author\"><Exec><Command>{command}</Command><Arguments>sweep</Arguments></Exec></Actions></Task>";
    }

    private static Install Installer(Vault vault, Machine machine, string executable, IProcessRunner schtasks,
        Func<string, bool>? shortcutRemover = null) => new(
        processRunner: schtasks,
        windowsSupported: () => true,
        commandAvailable: _ => true,
        fts5Available: () => true,
        shortcutRegistrar: _ => true,
        eventLogRegistrar: () => true,
        userSettingsPath: () => machine.UserSettings,
        mcpCandidates: () => [machine.DesktopMcp],
        processPath: () => executable,
        localAppDataPath: () => vault.Local,
        claudeUserConfigPath: () => vault.ClaudeSource,
        knownSyncRoots: () => [],
        eventLogRemover: () => machine.EventLogRemovals++,
        stateRootPath: _ => vault.StateRoot,
        doctorAction: () => { }, // never run doctor from a test: its stray-root scan opens real databases.
        eventLogTarget: () => machine.EventLogTarget,
        ownedShortcutRemover: owned => { machine.ShortcutAsks.Add(owned); return shortcutRemover?.Invoke(owned) ?? false; });

    private static string SyntheticExecutable(string root, string name = "oom.exe")
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "yayim")).FullName;
        var executable = Path.Combine(directory, name);
        File.WriteAllText(executable, "synthetic executable");
        return executable;
    }

    /// <summary>A stranger's <c>oom.exe</c>: the right file name, somebody else's install.</summary>
    private static string ForeignExecutable(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "yabanci", ".oom")).FullName;
        var executable = Path.Combine(directory, "oom.exe");
        File.WriteAllText(executable, "foreign product that took the same name");
        return executable;
    }

    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));

    private static JsonObject HookEntry(string command) => new()
    {
        ["type"] = "command",
        ["command"] = command.Contains(' ', StringComparison.Ordinal) ? $"\"{command}\" context" : $"{command} context",
        ["timeout"] = 15
    };

    private static IReadOnlyList<string> HookCommands(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return [];
        var hooks = JsonNode.Parse(File.ReadAllText(settingsPath, Utf8))?["hooks"] as JsonObject;
        return hooks is null
            ? []
            : [.. HookEvents
                .Where(name => hooks[name] is JsonArray)
                .SelectMany(name => hooks[name]!.AsArray())
                .SelectMany(group => group?["hooks"]?.AsArray() ?? [])
                .Select(entry => entry?["command"]?.GetValue<string>() ?? string.Empty)];
    }

    // ------------------------------------------------------------------ tests

    [Fact(DisplayName = "Y-184 · A kaldırılırken B'nin ve yabancı bir oom.exe'nin kayıtları ve dosyaları bozulmadan kalır")]
    public void Y184_UninstallingOneVaultLeavesTheNeighbourAndTheForeignInstallIntact()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var machine = new Machine(root);
            var published = SyntheticExecutable(root);
            var a = new Vault(root, "kasa-a");
            var b = new Vault(root, "kasa-b");
            var foreign = ForeignExecutable(root);
            var schtasks = new ScriptedSchtasks(foreign); // the machine's task runs the stranger's exe

            Assert.True(Installer(a, machine, published, schtasks).Run(a.Path).Success);
            Assert.True(Installer(b, machine, published, schtasks).Run(b.Path).Success);

            // One shared hook file holding all three, two of them in the SAME group: a group is a
            // container, and the old removal deleted containers rather than entries.
            Directory.CreateDirectory(Path.GetDirectoryName(machine.UserSettings)!);
            File.WriteAllText(machine.UserSettings, new JsonObject
            {
                ["hooks"] = new JsonObject
                {
                    ["SessionStart"] = new JsonArray(
                        new JsonObject { ["hooks"] = new JsonArray(HookEntry(a.Executable), HookEntry(b.Executable), HookEntry(foreign)) }),
                    ["SessionEnd"] = new JsonArray(
                        new JsonObject { ["hooks"] = new JsonArray(HookEntry(foreign)) })
                }
            }.ToJsonString(), Utf8);

            // One shared MCP file whose `oom` key belongs to the stranger.
            Directory.CreateDirectory(Path.GetDirectoryName(machine.DesktopMcp)!);
            File.WriteAllText(machine.DesktopMcp, new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["oom"] = new JsonObject { ["command"] = foreign, ["args"] = new JsonArray("mcp") },
                    ["other"] = new JsonObject { ["command"] = "other.exe" }
                }
            }.ToJsonString(), Utf8);

            machine.EventLogTarget = Path.Combine(Environment.SystemDirectory, "EventCreate.exe");
            var neighbourSettings = Hash(b.ProjectSettings);
            var neighbourMcp = Hash(b.ProjectMcp);
            var neighbourExe = Hash(b.Executable);
            var foreignExe = Hash(foreign);
            var foreignMcp = Hash(machine.DesktopMcp);

            var uninstall = Installer(a, machine, published, schtasks);
            var result = uninstall.Uninstall(a.Path);
            Assert.True(result.Success, result.Error);

            // (a) A is gone from every file it was in.
            Assert.DoesNotContain(HookCommands(a.ProjectSettings), command => command.Contains(a.Executable, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(HookCommands(machine.UserSettings), command => command.Contains(a.Executable, StringComparison.OrdinalIgnoreCase));
            Assert.False(File.Exists(a.Executable));
            Assert.Contains("hooks:kaldırıldı", result.Registrations);

            // (b) B is byte-identical, registration and binary alike.
            Assert.Equal(neighbourSettings, Hash(b.ProjectSettings));
            Assert.Equal(neighbourMcp, Hash(b.ProjectMcp));
            Assert.Equal(neighbourExe, Hash(b.Executable));
            Assert.Contains(HookCommands(machine.UserSettings), command => command.Contains(b.Executable, StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(b.StateRoot), "komşunun durum kökü taşındı");

            // (c) So is the stranger's: the same file name is not the same install.
            Assert.Equal(foreignExe, Hash(foreign));
            Assert.Equal(foreignMcp, Hash(machine.DesktopMcp));
            Assert.Equal(2, HookCommands(machine.UserSettings).Count(command => command.Contains(foreign, StringComparison.OrdinalIgnoreCase)));
            // A's own `.mcp.json` was removed; the shared file's `oom` key was not, because the key
            // is a name and the command under it is the stranger's.
            Assert.Contains("mcp:kaldırıldı", result.Registrations);
            Assert.False(File.Exists(a.ProjectMcp) &&
                JsonNode.Parse(File.ReadAllText(a.ProjectMcp, Utf8))?["mcpServers"]?.AsObject().ContainsKey("oom") == true);
            Assert.Contains(uninstall.Ownership,
                verdict => verdict.Target == $"mcp:{machine.DesktopMcp}" && !verdict.Owned && verdict.Detail.Contains(foreign, StringComparison.Ordinal));

            // (d) The task ran the stranger's exe, so the constant task name bought it nothing.
            Assert.Contains("task:korundu:yabancı-hedef", result.Registrations);
            Assert.DoesNotContain(schtasks.Calls, call => call.Contains("/Delete"));

            // (e) The Event Log source names Windows' own binary and no vault at all.
            Assert.Contains("event-log:korundu:paylaşılan-kaynak", result.Registrations);
            Assert.Equal(0, machine.EventLogRemovals);

            // (f) The shortcut removal was asked to match A's exe, not merely "a shortcut"; the
            // match failed, so neither the link nor the AUMID may be reported as removed.
            Assert.Equal([a.Executable], machine.ShortcutAsks);
            Assert.Contains("shortcut:korundu", result.Registrations);
            Assert.DoesNotContain("shortcut:kaldırıldı", result.Registrations);
            Assert.DoesNotContain("aumid:kaldırıldı", result.Registrations);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); ScarFixture.Remove(root); }
    }

    [Fact(DisplayName = "Y-185 · Karma bir hook grubunda yalnız sahip olunan alt kayıt kalkar, grup ayakta kalır")]
    public void Y185_MixedHookGroupLosesOnlyTheOwnedEntry()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var machine = new Machine(root);
            var published = SyntheticExecutable(root);
            var a = new Vault(root, "kasa");
            var schtasks = new ScriptedSchtasks(null, queryExitCode: 1);
            Assert.True(Installer(a, machine, published, schtasks).Run(a.Path).Success);

            // Another tool's hook shares the group with ours. Removing the container removes both.
            var settings = JsonNode.Parse(File.ReadAllText(a.ProjectSettings, Utf8))!.AsObject();
            var group = settings["hooks"]!["SessionStart"]!.AsArray()[0]!["hooks"]!.AsArray();
            group.Add(new JsonObject { ["type"] = "command", ["command"] = "other-tool.exe hook", ["timeout"] = 5 });
            // And an argument that merely mentions an oom.exe path is an argument, not a target.
            group.Add(new JsonObject { ["type"] = "command", ["command"] = $"logger.exe --watch \"{a.Executable}\"", ["timeout"] = 5 });
            File.WriteAllText(a.ProjectSettings, settings.ToJsonString(), Utf8);

            var result = Installer(a, machine, published, schtasks).Uninstall(a.Path);
            Assert.True(result.Success, result.Error);

            var remaining = HookCommands(a.ProjectSettings);
            Assert.Contains("other-tool.exe hook", remaining);
            Assert.Contains(remaining, command => command.StartsWith("logger.exe ", StringComparison.Ordinal));
            Assert.DoesNotContain(remaining, command => command.StartsWith(a.Executable, StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith($"\"{a.Executable}\"", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("task:yok", result.Registrations);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); ScarFixture.Remove(root); }
    }

    [Theory(DisplayName = "Y-186 · Eksik, bozuk ve uyuşmayan künye durum kökünü ve paketi yerinde bırakır")]
    [InlineData("eksik")]
    [InlineData("bozuk")]
    [InlineData("uyusmayan")]
    public void Y186_MissingCorruptAndMismatchedIdentityPreserveTheirTargets(string kind)
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var machine = new Machine(root);
            var published = SyntheticExecutable(root);
            var a = new Vault(root, "kasa");
            var schtasks = new ScriptedSchtasks(null, queryExitCode: 1);
            Assert.True(Installer(a, machine, published, schtasks).Run(a.Path).Success);

            var stateDescriptor = Path.Combine(a.StateRoot, VaultIdentity.DescriptorName);
            var packageDescriptor = Path.Combine(a.Path, ".oom", "vault.json");
            switch (kind)
            {
                case "eksik":
                    File.Delete(stateDescriptor);
                    break;
                case "bozuk":
                    File.WriteAllText(stateDescriptor, "{ bu JSON değil", Utf8);
                    File.WriteAllText(packageDescriptor, "{ bu JSON değil", Utf8);
                    break;
                default:
                    File.WriteAllText(stateDescriptor, $"{{\"vault\":\"{Path.Combine(root, "baska-kasa").Replace("\\", "\\\\")}\",\"schema\":1}}", Utf8);
                    File.WriteAllText(packageDescriptor, $"{{\"vault\":\"{Path.Combine(root, "baska-kasa").Replace("\\", "\\\\")}\",\"schema\":1}}", Utf8);
                    break;
            }

            // The install left a pooled handle on state.db; release it before hashing the file.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var databaseBefore = Hash(Path.Combine(a.StateRoot, VaultIdentity.DatabaseName));
            var uninstall = Installer(a, machine, published, schtasks);
            var result = uninstall.Uninstall(a.Path);
            Assert.True(result.Success, result.Error);

            // The state root is where every session ever summarised lives. An unattributable root
            // stays put, and the result says so instead of leaving the silence to be discovered.
            Assert.True(Directory.Exists(a.StateRoot), "künyesi doğrulanmayan durum kökü taşındı");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.Equal(databaseBefore, Hash(Path.Combine(a.StateRoot, VaultIdentity.DatabaseName)));
            Assert.Contains(result.Registrations, line => line.StartsWith("durum-kökü:korundu:", StringComparison.Ordinal));
            Assert.Contains(uninstall.Ownership, verdict => verdict.Target == "durum-kökü" && !verdict.Owned);

            if (kind == "eksik") return; // a state root without a descriptor says nothing about the package
            Assert.True(File.Exists(a.Executable), "künyesi başka kasayı gösteren paket silindi");
            Assert.Contains(result.Registrations, line => line.StartsWith("paket:korundu:", StringComparison.Ordinal));
            Assert.Contains(uninstall.Ownership, verdict => verdict.Target == "paket" && !verdict.Owned);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); ScarFixture.Remove(root); }
    }

    [Fact(DisplayName = "Y-187 · Reddedilen kimlik yolunda doğrulanmış bağımsız kayıtlar yine de temizlenir")]
    public void Y187_RejectedConfigPathStillCleansVerifiedIndependentRegistrations()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var machine = new Machine(root);
            var published = SyntheticExecutable(root);
            var a = new Vault(root, "kasa");
            var schtasks = new ScriptedSchtasks(null, queryExitCode: 1);
            Assert.True(Installer(a, machine, published, schtasks).Run(a.Path).Success);
            Assert.True(File.Exists(a.ProjectMcp));

            // The identity directory now resolves inside a declared sync root, which
            // ClaudeIsolation refuses. The hooks and the MCP entry do not depend on that path, so
            // they must already be gone by the time the refusal is raised — a partial uninstall
            // that leaves live entry points pointing at a half-removed install is the worse half.
            var identity = Path.Combine(a.Local, "oom", "claude-config");
            var owned = new ScriptedSchtasks(a.Executable);
            var install = new Install(
                processRunner: owned,
                userSettingsPath: () => machine.UserSettings,
                mcpCandidates: () => [machine.DesktopMcp],
                processPath: () => null,
                localAppDataPath: () => a.Local,
                knownSyncRoots: () => [identity],
                shortcutRemover: () => false,
                eventLogRemover: () => machine.EventLogRemovals++,
                stateRootPath: _ => a.StateRoot,
                doctorAction: () => { },
                eventLogTarget: () => null);

            var result = install.Uninstall(a.Path);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Contains("hooks:kaldırıldı", result.Registrations);
            Assert.Contains("mcp:kaldırıldı", result.Registrations);
            Assert.Contains("task:kaldırıldı", result.Registrations);
            Assert.Equal(["/Query", "/TN", "OdenaOS Memory Sweep", "/XML"], owned.Calls[0]);
            Assert.Equal(["/Delete", "/TN", "OdenaOS Memory Sweep", "/F"], owned.Calls[1]);
            Assert.Empty(HookCommands(a.ProjectSettings));
            Assert.False(JsonNode.Parse(File.ReadAllText(a.ProjectMcp, Utf8))?["mcpServers"]?.AsObject().ContainsKey("oom"));
            // And the vault's own state root survived the refusal untouched.
            Assert.True(Directory.Exists(a.StateRoot));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); ScarFixture.Remove(root); }
    }

    [Fact(DisplayName = "Y-188 · Başarısız görev silme 'kaldırıldı' diye bildirilmez")]
    public void Y188_FailedTaskRemovalIsNotReportedAsRemoved()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var machine = new Machine(root);
            var published = SyntheticExecutable(root);
            var a = new Vault(root, "kasa");
            var probe = new ScriptedSchtasks(null, queryExitCode: 1);
            Assert.True(Installer(a, machine, published, probe).Run(a.Path).Success);

            // The task is ours — and schtasks refuses to delete it anyway.
            var failing = new ScriptedSchtasks(a.Executable, deleteExitCode: 1);
            var uninstall = Installer(a, machine, published, failing);
            var result = uninstall.Uninstall(a.Path);

            Assert.True(result.Success, result.Error);
            Assert.Contains("task:kaldırılamadı", result.Registrations);
            Assert.DoesNotContain("task:kaldırıldı", result.Registrations);
            Assert.Contains(failing.Calls, call => call.Contains("/Delete"));
            var verdict = Assert.Single(uninstall.Ownership, item => item.Target == "task:OdenaOS Memory Sweep");
            Assert.True(verdict.Owned);
            Assert.Contains("silme başarısız", verdict.Detail, StringComparison.Ordinal);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); ScarFixture.Remove(root); }
    }

    [Fact(DisplayName = "Y-189 · Sahiplik kararı kayıt satırından ayrı, hedef hedef bildirilir")]
    public void Y189_OwnershipVerdictsAreReportedTargetByTarget()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var machine = new Machine(root);
            var published = SyntheticExecutable(root);
            var a = new Vault(root, "kasa");
            var schtasks = new ScriptedSchtasks(a.Executable);
            Assert.True(Installer(a, machine, published, schtasks).Run(a.Path).Success);

            // This Event Log source does name our exe, so the one case where removing it is
            // provably right is exercised rather than assumed away.
            machine.EventLogTarget = a.Executable;
            var uninstall = Installer(a, machine, published, schtasks, shortcutRemover: _ => true);
            var result = uninstall.Uninstall(a.Path);
            Assert.True(result.Success, result.Error);

            Assert.Contains("event-log:kaldırıldı", result.Registrations);
            Assert.Equal(1, machine.EventLogRemovals);
            Assert.Contains("shortcut:kaldırıldı", result.Registrations);
            Assert.Contains("aumid:kaldırıldı", result.Registrations);

            foreach (var target in new[] { "task:OdenaOS Memory Sweep", "shortcut", "event-log:oom", "claude-config", "paket", "durum-kökü" })
                Assert.Contains(uninstall.Ownership, verdict => verdict.Target == target);
            Assert.All(uninstall.Ownership, verdict => Assert.False(string.IsNullOrWhiteSpace(verdict.Detail)));
            Assert.Contains(uninstall.Ownership, verdict => verdict.Target.StartsWith("hooks:", StringComparison.Ordinal) && verdict.Owned);
            Assert.Contains(uninstall.Ownership, verdict => verdict.Target.StartsWith("mcp:", StringComparison.Ordinal) && verdict.Owned);

            // A relative vault path names no machine: nothing is examined, so nothing is claimed.
            var fixtureResult = new Install().Uninstall("göreli-kasa");
            Assert.Equal(["kaldırma:atlandı:göreli-yol"], fixtureResult.Registrations);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); ScarFixture.Remove(root); }
    }

    [Fact(DisplayName = "Y-190 · Sahiplik kanıtı yol ve komut düzeyinde okunur, ad benzerliğiyle değil")]
    public void Y190_OwnershipEvidenceIsReadFromPathsAndCommandsNotNames()
    {
        var owned = Path.Combine(Path.GetTempPath(), "kasa", ".oom", "oom.exe");

        // A relative spelling resolves against whatever directory the uninstall runs in: a
        // coincidence, never an identity.
        Assert.False(InstallOwnership.PathsEqual("fixture\\oom.exe", owned));
        Assert.False(InstallOwnership.PathsEqual(owned, "oom.exe"));
        Assert.True(InstallOwnership.PathsEqual(owned, Path.Combine(Path.GetTempPath(), "kasa", ".oom", ".", "oom.exe")));
        Assert.False(InstallOwnership.PathsEqual(Path.Combine(Path.GetTempPath(), "baska", ".oom", "oom.exe"), owned));

        // Only the leading token is executed; the rest is arguments.
        Assert.True(InstallOwnership.CommandRuns($"\"{owned}\" flush --reason sessionend", owned));
        Assert.True(InstallOwnership.CommandRuns($"{owned} context", owned));
        Assert.False(InstallOwnership.CommandRuns($"logger.exe --watch \"{owned}\"", owned));
        Assert.False(InstallOwnership.CommandRuns($"\"{owned}", owned));
        Assert.False(InstallOwnership.CommandRuns(null, owned));

        // And the task's target comes from the machine's own XML, entities and quotes included.
        Assert.Equal("C:\\a b\\oom.exe", InstallRuntime.SchtasksScheduler.RegisteredCommand(
            "<Task><Actions><Exec><Command>\"C:\\a b\\oom.exe\"</Command></Exec></Actions></Task>"));
        Assert.Equal("C:\\a&b\\oom.exe", InstallRuntime.SchtasksScheduler.RegisteredCommand(
            "<Task><Actions><Exec><Command>C:\\a&amp;b\\oom.exe</Command></Exec></Actions></Task>"));
        Assert.Null(InstallRuntime.SchtasksScheduler.RegisteredCommand("<Task><Actions /></Task>"));
        Assert.Null(InstallRuntime.SchtasksScheduler.RegisteredCommand(string.Empty));
    }
}
