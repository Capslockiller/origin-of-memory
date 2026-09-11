// Faz 5 — ayrışma: bir makinede iki proje, iki vault, yan yana.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests;

/// <summary>
/// The separation half of Faz 5. Every path an <see cref="Install"/> can reach is injected here:
/// these tests never touch the running machine's real profile, its real scheduled tasks, its real
/// Start-menu folder or its real registry. What they pin is that the DEFAULT install writes only
/// inside the vault it was given, so a second vault can be installed beside the first without
/// taking anything over from it.
/// </summary>
public sealed class AyrismaTests
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly string[] HookEvents = ["SessionStart", "UserPromptSubmit", "SessionEnd", "PreCompact"];

    /// <summary>Records what was asked of the machine instead of doing it.</summary>
    private sealed class RecordingScheduler : ITaskScheduler
    {
        internal List<string> Registered { get; } = [];

        public void Register(string name, string xml) => Registered.Add(name);
    }

    /// <summary>One vault plus every machine-level path that install would otherwise reach.</summary>
    private sealed class Bench(string root, string name)
    {
        internal string Root { get; } = root;
        internal string Vault { get; } = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        internal string StateRoot { get; } = Path.Combine(root, $"state-{name}");
        internal string InstallLocal { get; } = Directory.CreateDirectory(Path.Combine(root, $"local-{name}")).FullName;
        internal string ClaudeSource { get; } = Directory.CreateDirectory(Path.Combine(root, $"claude-{name}")).FullName;

        internal string InstalledExe => Path.Combine(Vault, ".oom", "oom.exe");
        internal string ProjectSettings => Install.ProjectSettingsPath(Vault);
        internal string ProjectMcp => Install.ProjectMcpPath(Vault);
    }

    /// <summary>The machine-shared files and registrations, shared by every bench in one test.</summary>
    private sealed class Machine(string root)
    {
        internal string UserSettings { get; } = Path.Combine(root, "profil", ".claude", "settings.json");
        internal string DesktopMcp { get; } = Path.Combine(root, "profil", "mcp", "claude_desktop_config.json");
        internal RecordingScheduler Scheduler { get; } = new();
        internal List<string> ShortcutCalls { get; } = [];
        internal int EventLogCalls { get; set; }
    }

    private static Install Installer(Bench bench, Machine machine, string executable) => new(
        scheduler: machine.Scheduler,
        windowsSupported: () => true,
        commandAvailable: _ => true,
        fts5Available: () => true,
        shortcutRegistrar: path => { machine.ShortcutCalls.Add(path); return true; },
        eventLogRegistrar: () => { machine.EventLogCalls++; return true; },
        userSettingsPath: () => machine.UserSettings,
        mcpCandidates: () => [machine.DesktopMcp],
        processPath: () => executable,
        localAppDataPath: () => bench.InstallLocal,
        claudeUserConfigPath: () => bench.ClaudeSource,
        knownSyncRoots: () => [],
        shortcutRemover: () => false,
        eventLogRemover: () => { },
        stateRootPath: _ => bench.StateRoot,
        doctorAction: () => { }); // never run doctor from a test: its stray-root scan opens real databases.

    private static string SyntheticExecutable(string root)
    {
        var executable = Path.Combine(root, "oom.exe");
        File.WriteAllText(executable, "synthetic executable");
        return executable;
    }

    private static IReadOnlyList<string> HookCommands(string settingsPath)
    {
        var hooks = JsonNode.Parse(File.ReadAllText(settingsPath, Utf8))?["hooks"] as JsonObject;
        return hooks is null
            ? []
            : [.. HookEvents
                .Where(name => hooks[name] is JsonArray)
                .SelectMany(name => hooks[name]!.AsArray())
                .SelectMany(group => group?["hooks"]?.AsArray() ?? [])
                .Select(entry => entry?["command"]?.GetValue<string>() ?? string.Empty)];
    }

    [Fact(DisplayName = "Y-180 · Varsayılan kurulum proje kapsamlıdır: makine geneli hiçbir kayda dokunmaz")]
    public void Y180_DefaultInstallIsProjectScopedAndTouchesNothingShared()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var bench = new Bench(root, "kasa");
            var machine = new Machine(root);
            var install = Installer(bench, machine, SyntheticExecutable(root));
            var result = install.Run(bench.Vault);
            Assert.True(result.Success, result.Error);

            // (a) Nothing shared was written. These five are the registrations that exist exactly
            // once per machine; every one of them belongs to every project, not to this vault.
            Assert.False(File.Exists(machine.UserSettings), $"ortak hook dosyası yazıldı: {machine.UserSettings}");
            Assert.False(File.Exists(machine.DesktopMcp), $"ortak MCP dosyası yazıldı: {machine.DesktopMcp}");
            Assert.Empty(machine.Scheduler.Registered);
            Assert.Empty(machine.ShortcutCalls);
            Assert.Equal(0, machine.EventLogCalls);

            // (b) The project package is what the vault gained instead.
            Assert.True(File.Exists(bench.ProjectSettings), $"proje hook dosyası yazılmadı: {bench.ProjectSettings}");
            Assert.True(File.Exists(bench.ProjectMcp), $"proje MCP dosyası yazılmadı: {bench.ProjectMcp}");
            var commands = HookCommands(bench.ProjectSettings);
            Assert.Equal(4, commands.Count);
            Assert.All(commands, command => Assert.Contains(bench.InstalledExe, command, StringComparison.OrdinalIgnoreCase));
            using (var mcp = JsonDocument.Parse(File.ReadAllText(bench.ProjectMcp, Utf8)))
                Assert.Equal(bench.InstalledExe,
                    mcp.RootElement.GetProperty("mcpServers").GetProperty("oom").GetProperty("command").GetString());

            // (c) The skip is reported, not hidden: an install that quietly does less is its own defect.
            Assert.Contains("kapsam:proje", result.Registrations);
            Assert.Contains("task:atlandı", result.Registrations);
            Assert.Contains("shortcut:atlandı", result.Registrations);
            Assert.Contains("event-log:atlandı", result.Registrations);
            Assert.False(install.ToastRegistered);
            Assert.Contains(install.Health, item => item.Code == "proje-kapsami");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); ScarFixture.Remove(root); }
    }

    [Fact(DisplayName = "Y-181 · İkinci vault'un kurulumu birincinin kancalarını, MCP'sini ve durum kökünü bozmaz")]
    public void Y181_SecondVaultInstallLeavesTheFirstVaultUntouched()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var machine = new Machine(root);
            var executable = SyntheticExecutable(root);
            var first = new Bench(root, "kasa-bir");
            var second = new Bench(root, "kasa-iki");

            Assert.True(Installer(first, machine, executable).Run(first.Vault).Success);
            Assert.True(File.Exists(first.ProjectSettings), $"birinci vault proje paketini almadı: {first.ProjectSettings}");
            Assert.False(File.Exists(machine.UserSettings), $"birinci kurulum ortak hook dosyasını yazdı: {machine.UserSettings}");
            var settingsBefore = File.ReadAllBytes(first.ProjectSettings);
            var mcpBefore = File.ReadAllBytes(first.ProjectMcp);

            Assert.True(Installer(second, machine, executable).Run(second.Vault).Success);

            // The first vault's package is byte-identical. Under the old default both installs
            // wrote the one shared settings.json, and SetHook drops every entry naming oom.exe
            // before adding its own — so the second install deleted the first vault's four hooks.
            Assert.Equal(settingsBefore, File.ReadAllBytes(first.ProjectSettings));
            Assert.Equal(mcpBefore, File.ReadAllBytes(first.ProjectMcp));
            Assert.All(HookCommands(first.ProjectSettings),
                command => Assert.Contains(first.InstalledExe, command, StringComparison.OrdinalIgnoreCase));
            Assert.All(HookCommands(second.ProjectSettings),
                command => Assert.Contains(second.InstalledExe, command, StringComparison.OrdinalIgnoreCase));
            Assert.NotEqual(first.InstalledExe, second.InstalledExe);

            // Two state roots, each naming its own vault (8436f94's descriptor is what makes this
            // checkable at all — before it a root was eight hex digits nobody could attribute).
            foreach (var bench in new[] { first, second })
            {
                var descriptor = Path.Combine(bench.StateRoot, VaultIdentity.DescriptorName);
                Assert.True(File.Exists(descriptor), $"durum kökü künyesi yok: {descriptor}");
                Assert.Equal(bench.Vault, VaultIdentity.ReadDescriptor(bench.StateRoot));
            }
            Assert.NotEqual(first.StateRoot, second.StateRoot);

            // And after two installs the machine still carries none of it.
            Assert.False(File.Exists(machine.UserSettings));
            Assert.False(File.Exists(machine.DesktopMcp));
            Assert.Empty(machine.Scheduler.Registered);
            Assert.Empty(machine.ShortcutCalls);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); ScarFixture.Remove(root); }
    }

    [Fact(DisplayName = "Y-182 · Makine geneli yazma yalnız --user-scope ile istenir ve dağıtıcı bayrağı gerçekten geçirir")]
    public void Y182_UserLevelWriteIsOptInAndTheDispatcherPassesTheFlag()
    {
        // (a) The flag, and only the flag, selects the user scope.
        Assert.Equal(InstallScope.Project, Oom.Program.InstallScopeFor([]));
        Assert.Equal(InstallScope.Project, Oom.Program.InstallScopeFor(["install", "--vault", "x", "--dry-run"]));
        Assert.Equal(InstallScope.User, Oom.Program.InstallScopeFor(["install", "--user-scope"]));

        // (b) The dispatcher hands that scope to Install. A helper proven in isolation proves
        // nothing about the command that has to reach it, so the wiring is pinned as text too.
        var program = File.ReadAllText(Path.Combine(ScarFixture.RepositoryRoot(), "src", "Oom", "Program.cs"));
        Assert.Contains("var scope = InstallScopeFor(args);", program, StringComparison.Ordinal);
        Assert.Contains(": install.Run(target, args.Contains(\"--from-v0\"), args.Contains(\"--dry-run\"), scope);", program, StringComparison.Ordinal);
        Assert.Contains("--user-scope", program, StringComparison.Ordinal);

        // (c) Asked for by name, the user scope still does exactly what it always did.
        var root = ScarFixture.TempDirectory();
        try
        {
            var bench = new Bench(root, "kasa");
            var machine = new Machine(root);
            var result = Installer(bench, machine, SyntheticExecutable(root))
                .Run(bench.Vault, fromV0: false, dryRun: false, InstallScope.User);
            Assert.True(result.Success, result.Error);

            Assert.True(File.Exists(machine.UserSettings), "kullanıcı kapsamı ortak hook dosyasını yazmadı");
            Assert.Equal(4, HookCommands(machine.UserSettings).Count);
            Assert.True(File.Exists(machine.DesktopMcp), "kullanıcı kapsamı Claude Desktop MCP kaydını yazmadı");
            Assert.Single(machine.Scheduler.Registered);
            Assert.Single(machine.ShortcutCalls);
            Assert.Equal(1, machine.EventLogCalls);
            Assert.Contains("kapsam:kullanıcı", result.Registrations);

            // The user scope is the old behaviour, not the old behaviour plus a project package.
            Assert.False(File.Exists(bench.ProjectSettings), $"kullanıcı kapsamı proje paketini de yazdı: {bench.ProjectSettings}");
            Assert.False(File.Exists(bench.ProjectMcp), $"kullanıcı kapsamı proje paketini de yazdı: {bench.ProjectMcp}");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); ScarFixture.Remove(root); }
    }

    [Fact(DisplayName = "Y-183 · Bir vault'un kaldırılması komşu vault'un paketine ve yabancı kancalara dokunmaz")]
    public void Y183_UninstallingOneVaultLeavesTheNeighbourAndForeignHooksAlone()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var machine = new Machine(root);
            var executable = SyntheticExecutable(root);
            var first = new Bench(root, "kasa-bir");
            var second = new Bench(root, "kasa-iki");
            Assert.True(Installer(first, machine, executable).Run(first.Vault).Success);
            Assert.True(Installer(second, machine, executable).Run(second.Vault).Success);
            Assert.True(File.Exists(first.ProjectSettings), $"birinci vault proje paketini almadı: {first.ProjectSettings}");

            // Another tool's hook, in the first vault's own project file. Merge-never-replace is a
            // project-level obligation too: the vault's settings.json is not ours alone either.
            var settings = JsonNode.Parse(File.ReadAllText(first.ProjectSettings, Utf8)) as JsonObject;
            ((JsonObject)settings!["hooks"]!)["SessionEnd"]!.AsArray().Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "other-tool.exe hook", ["timeout"] = 5 })
            });
            File.WriteAllText(first.ProjectSettings, settings.ToJsonString(), Utf8);
            var neighbourSettings = File.ReadAllBytes(second.ProjectSettings);
            var neighbourMcp = File.ReadAllBytes(second.ProjectMcp);

            var uninstalled = Installer(first, machine, executable).Uninstall(first.Vault);
            Assert.True(uninstalled.Success, uninstalled.Error);

            Assert.DoesNotContain(HookCommands(first.ProjectSettings),
                command => command.Contains("oom.exe", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(HookCommands(first.ProjectSettings), command => command == "other-tool.exe hook");
            var servers = JsonNode.Parse(File.ReadAllText(first.ProjectMcp, Utf8))?["mcpServers"]?.AsObject();
            Assert.NotNull(servers);
            Assert.False(servers.ContainsKey("oom"));

            // The neighbour is untouched, byte for byte, and so is the machine.
            Assert.Equal(neighbourSettings, File.ReadAllBytes(second.ProjectSettings));
            Assert.Equal(neighbourMcp, File.ReadAllBytes(second.ProjectMcp));
            Assert.False(File.Exists(machine.UserSettings), "kaldırma ortak hook dosyasını yarattı");
            Assert.False(File.Exists(machine.DesktopMcp), "kaldırma ortak MCP dosyasını yarattı");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); ScarFixture.Remove(root); }
    }
}
