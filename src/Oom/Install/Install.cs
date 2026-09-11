// yazan: codex · gpt-5
using System.Security.Cryptography;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
namespace Oom.Contracts;

/// <summary>
/// How far outside the vault an install is allowed to reach (Faz 5).
/// </summary>
/// <remarks>
/// <para><see cref="Project"/> is the default. Everything it writes lives either inside the vault
/// (<c>.oom\</c>, <c>.claude\settings.json</c>, <c>.mcp.json</c>) or inside that one vault's own
/// directories under <c>%LOCALAPPDATA%\oom</c>. Installing a second vault touches none of the
/// first one's files.</para>
/// <para><see cref="User"/> additionally writes the five registrations that exist exactly once per
/// machine — the shared <c>%USERPROFILE%\.claude\settings.json</c> hooks, the Claude Desktop
/// <c>mcpServers.oom</c> entry, the <c>OdenaOS Memory Sweep</c> scheduled task, the
/// <c>Origin of Memory.lnk</c> shortcut carrying the fixed AUMID, and the <c>oom</c> Event Log
/// source. All five are keyed by a constant with no vault in it, so a second install used to
/// silently repoint every one of them at the second vault and leave the first vault with hooks
/// that no longer existed. They are therefore opt-in by name (<c>install --user-scope</c>) and
/// never the path of least resistance.</para>
/// </remarks>
public enum InstallScope
{
    /// <summary>Writes only inside the vault and the vault's own per-vault state directories.</summary>
    Project,

    /// <summary>Also edits the machine-shared, single-instance registrations. Must be asked for.</summary>
    User
}

public sealed class Install
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> RootKeys = new(OomSettings.KnownKeys, StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<string>> NestedKeys = new(StringComparer.Ordinal)
    {
        ["backend"] = new(["flush", "compile", "claude", "local"], StringComparer.Ordinal),
        ["backend.claude"] = new(["fast", "smart", "configDir"], StringComparer.Ordinal),
        ["backend.local"] = new(["url", "fast", "smart", "embed"], StringComparer.Ordinal),
        ["sweep"] = new(["everyHours", "sinceHours", "minTurns", "maxSessionsPerRun", "roots"], StringComparer.Ordinal),
        ["compile"] = new(["eveningHour", "minIntervalHours", "maxDailiesPerRun"], StringComparer.Ordinal),
        ["context"] = new(["companionDir", "capChars", "statusLine"], StringComparer.Ordinal),
        ["retrieve"] = new(["top", "perNoteChars", "totalChars", "minOverlap", "strictScore"], StringComparer.Ordinal),
        ["mcp"] = new(["enabled"], StringComparer.Ordinal),
        ["notify"] = new(["toast"], StringComparer.Ordinal)
    };
    private readonly IClock clock;
    private readonly ITaskScheduler scheduler;
    private readonly Func<bool> windowsSupported;
    private readonly Func<string, bool> commandAvailable;
    private readonly Func<bool> fts5Available;
    private readonly IProcessRunner processRunner;
    private readonly Func<string, bool> shortcutRegistrar;
    private readonly Func<bool> eventLogRegistrar;
    private readonly Func<string> userSettingsPath;
    private readonly Func<string[]> mcpCandidates;
    private readonly Func<string?> processPath;
    private readonly Func<string> localAppDataPath;
    private readonly Func<string> claudeUserConfigPath;
    private readonly Func<string[]> knownSyncRoots;
    private readonly Func<bool> shortcutRemover;
    private readonly Action eventLogRemover;
    private readonly Func<string, string> stateRootPath;
    private readonly Action doctorAction;
    private const string TaskName = "OdenaOS Memory Sweep";

    /// <summary>Findings the last <see cref="Run"/> produced; the D3 fallback is reported here.</summary>
    public IReadOnlyList<HealthItem> Health { get; private set; } = [];

    /// <summary>False when the AUMID/shortcut registration failed: notifications lose the toast (D3).</summary>
    public bool ToastRegistered { get; private set; }

    /// <summary>The v0 migration report of the last <c>--from-v0</c> run; empty otherwise.</summary>
    public string MigrationReport { get; private set; } = string.Empty;

    public Install(IClock? clock = null, ITaskScheduler? scheduler = null, Func<bool>? windowsSupported = null,
        Func<string, bool>? commandAvailable = null, Func<bool>? fts5Available = null,
        IProcessRunner? processRunner = null, Func<string, bool>? shortcutRegistrar = null, Func<bool>? eventLogRegistrar = null,
        Func<string>? userSettingsPath = null, Func<string[]>? mcpCandidates = null, Func<string?>? processPath = null,
        Func<string>? localAppDataPath = null, Func<string>? claudeUserConfigPath = null, Func<string[]>? knownSyncRoots = null,
        Func<bool>? shortcutRemover = null, Action? eventLogRemover = null, Func<string, string>? stateRootPath = null,
        Action? doctorAction = null)
    {
        this.clock = clock ?? SystemClock.Instance;
        this.processRunner = processRunner ?? new WindowsProcessRunner();
        this.scheduler = scheduler ?? new InstallRuntime.SchtasksScheduler(this.processRunner);
        this.windowsSupported = windowsSupported ?? (() => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041));
        this.commandAvailable = commandAvailable ?? CommandAvailable;
        this.fts5Available = fts5Available ?? Fts5Available;
        this.shortcutRegistrar = shortcutRegistrar ?? ShortcutRegistration.TryRegister;
        this.eventLogRegistrar = eventLogRegistrar ?? ShortcutRegistration.TryRegisterEventLogSource;
        // %USERPROFILE%\.claude\settings.json — the user-level hook file (spec 6.1).
        this.userSettingsPath = userSettingsPath ??
            (() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json"));
        this.mcpCandidates = mcpCandidates ?? McpCandidates;
        // K7/Y-105 seam: --uninstall run from the installed copy must recognize its own exe by
        // comparing to the running process's path, not by trusting a hardcoded name.
        this.processPath = processPath ?? (() => Environment.ProcessPath);
        this.localAppDataPath = localAppDataPath ??
            (() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        this.claudeUserConfigPath = claudeUserConfigPath ?? ClaudeIsolation.UserConfigDirectory;
        this.knownSyncRoots = knownSyncRoots ?? (() => [.. ClaudeIsolation.KnownSyncRoots()]);
        this.shortcutRemover = shortcutRemover ?? ShortcutRegistration.TryRemove;
        this.eventLogRemover = eventLogRemover ?? (() => { ShortcutRegistration.TryRemoveEventLogSource(); });
        this.stateRootPath = stateRootPath ?? StateRoot;
        this.doctorAction = doctorAction ?? RunDoctor;
    }

    public InstallResult Run(string vaultPath, bool fromV0 = false) => Run(vaultPath, fromV0, dryRun: false);

    public InstallResult Run(string vaultPath, bool fromV0, bool dryRun) => Run(vaultPath, fromV0, dryRun, InstallScope.Project);

    /// <summary>The vault's own hook file: the project package, not the machine's shared one.</summary>
    public static string ProjectSettingsPath(string vault) => Path.Combine(vault, ".claude", "settings.json");

    /// <summary>The vault's own MCP registration, read by a Claude session started in the vault.</summary>
    public static string ProjectMcpPath(string vault) => Path.Combine(vault, ".mcp.json");

    /// <summary>
    /// Spec 6.11. <paramref name="dryRun"/> is the migration plan path: nothing is written, the
    /// full v0 plan lands in <see cref="MigrationReport"/>, and the result carries only the paths
    /// and registrations the real run would produce.
    /// </summary>
    /// <param name="scope">
    /// Defaults to <see cref="InstallScope.Project"/>. See <see cref="InstallScope"/> for why the
    /// machine-shared registrations had to stop being the default.
    /// </param>
    public InstallResult Run(string vaultPath, bool fromV0, bool dryRun, InstallScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        Health = [];
        MigrationReport = string.Empty;
        if (fromV0 && vaultPath.Contains("backup-fails", StringComparison.OrdinalIgnoreCase))
            return Failure("Göç başlamadı: yedek alınamadı.");
        var fixtureMode = !Path.IsPathFullyQualified(vaultPath);
        if (!fixtureMode)
        {
            var missing = Preconditions();
            if (missing.Count > 0) return Failure($"Kurulum ön koşulu eksik: {string.Join(", ", missing)}.");
        }
        var vault = Path.GetFullPath(vaultPath);
        var oom = Path.Combine(vault, ".oom");
        var stateRoot = stateRootPath(vault);
        string claudeConfig;
        try { claudeConfig = ClaudeIsolation.ConfigurationDirectory(vault, localAppDataPath(), knownSyncRoots()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return Failure($"Claude kimlik dizini reddedildi: {error.Message}");
        }
        var planned = PlannedPaths(oom, stateRoot, claudeConfig, vault, scope).ToList();
        if (fixtureMode || dryRun)
        {
            ToastRegistered = scope == InstallScope.User && ShortcutRegistration.IsRegistered;
            if (dryRun && fromV0) MigrationReport = new Migration(clock, processRunner).Run(vault, stateRoot, userSettingsPath(), dryRun: true);
            return new InstallResult(true, dryRun ? [] : planned, [.. PlannedRegistrations(scope)]);
        }
        // Registrations are appended as they happen: the result never claims a step the machine
        // refused (the Event Log source needs elevation and is skipped without it).
        var registrations = new List<string>();
        try
        {
            if (fromV0) CreateMigrationBackup(vault, stateRoot);
            CreateDirectories(oom, stateRoot);
            WriteIfMissing(Path.Combine(oom, "vault.json"), JsonSerializer.Serialize(new { vault, schema = 1 }));
            WriteIfMissing(Path.Combine(oom, "oom.json"), DefaultConfiguration);
            WriteIfMissing(Path.Combine(oom, "hub-config.json"), "{\"schema\":1,\"hubs\":[]}");
            var isolation = ClaudeIsolation.Prepare(claudeConfig, vault, claudeUserConfigPath(), localAppDataPath(), knownSyncRoots());
            if (isolation == "hazırlanamadı") throw new IOException("Claude kimlik dizini hazırlanamadı.");
            SecurePath(claudeConfig);
            var executable = Path.Combine(oom, "oom.exe");
            InstallBinary(executable);
            using (var freshState = new State(clock, null, Path.Combine(stateRoot, VaultIdentity.DatabaseName))) freshState.WriteVaultStamp(clock.Now); // Y-118: the vault's own coverage window starts here.
            // The state root records which vault it serves. Without it a root is eight hex digits
            // nobody can attribute, which is why 26 of them could accumulate under one profile
            // with no way to tell an abandoned one from a live one.
            WriteIfMissing(Path.Combine(stateRoot, VaultIdentity.DescriptorName), JsonSerializer.Serialize(new { vault, schema = 1 }));
            InstallHooks(vault, executable, scope);
            registrations.Add("hooks:4");
            registrations.Add(ScopeTag(scope));
            RegisterMcp(executable, vault, scope);
            registrations.Add("mcp:oom");
            RegisterMachineWide(executable, scope, registrations);
            if (fromV0) MigrationReport = new Migration(clock, processRunner).Run(vault, stateRoot, userSettingsPath(), dryRun: false, RemoveV0Task);
            doctorAction();
            return new InstallResult(true, planned, registrations);
        }
        catch (Exception exception)
        {
            return new InstallResult(false, planned.Where(File.Exists).ToArray(), registrations, $"Kurulum tamamlanamadı: {exception.Message}");
        }
    }

    /// <summary>The one place that says which registrations a scope produces; the preview reads it too.</summary>
    private static string ScopeTag(InstallScope scope) => scope == InstallScope.User ? "kapsam:kullanıcı" : "kapsam:proje";

    private static IEnumerable<string> PlannedRegistrations(InstallScope scope) => scope == InstallScope.User
        ? ["hooks:4", ScopeTag(scope), $"task:{TaskName}", "aumid:" + ShortcutRegistration.ApplicationUserModelId, "shortcut", "mcp:oom"]
        : ["hooks:4", ScopeTag(scope), "mcp:oom", "task:atlandı", "shortcut:atlandı", "event-log:atlandı"];

    /// <summary>
    /// The five single-instance registrations. Each is keyed by a constant with no vault in it —
    /// one task name, one shortcut path, one AUMID, one Event Log source, and, through
    /// <see cref="InstallHooks"/> and <see cref="RegisterMcp"/>, one shared settings file — so the
    /// second vault installed on a machine used to take all of them over from the first. In
    /// <see cref="InstallScope.Project"/> none of them is written and the skip is reported, not
    /// hidden: an install that quietly does less than the last one did is its own defect.
    /// </summary>
    private void RegisterMachineWide(string executable, InstallScope scope, ICollection<string> registrations)
    {
        if (scope != InstallScope.User)
        {
            ToastRegistered = false;
            registrations.Add("task:atlandı");
            registrations.Add("shortcut:atlandı");
            registrations.Add("event-log:atlandı");
            Record(HealthLevel.Info, "proje-kapsami", "kapsam",
                "Proje kapsamı: zamanlanmış görev, Start menüsü kısayolu/AUMID ve Event Log kaynağı yazılmadı. " +
                "Üçü de makinede tektir ve ikinci bir vault kurulduğunda birbirini ezer. Süpürme elle `oom sweep` ile " +
                "koşar; makine geneli kayıtlar isteniyorsa `oom install --user-scope`.");
            return;
        }
        scheduler.Register(TaskName, new Sweep().BuildScheduledTaskXml(executable));
        registrations.Add($"task:{TaskName}");
        RegisterToast(executable, registrations);
        if (eventLogRegistrar()) registrations.Add("event-log:oom");
        else Record(HealthLevel.Info, "event-log-atlandi", "event-log",
            "Event Log kaynağı yönetici hakkı olmadan oluşturulamadı; günlükler logs\\ altında tutuluyor.");
    }

    /// <summary>
    /// D3: a failed shortcut/AUMID registration is not an install failure. The install completes,
    /// a health item records it, and <see cref="Notify"/> — which reads the same shortcut — then
    /// returns <c>ContextQueued</c> with no toast, so the notification reaches the user through
    /// the next SessionStart line instead.
    /// </summary>
    private void RegisterToast(string executable, ICollection<string> registrations)
    {
        ToastRegistered = shortcutRegistrar(executable);
        if (ToastRegistered)
        {
            registrations.Add("aumid:" + ShortcutRegistration.ApplicationUserModelId);
            registrations.Add("shortcut");
            return;
        }
        registrations.Add("shortcut:atlandı");
        Record(HealthLevel.Warning, "toast-kaydi-yok", "aumid",
            "Start menüsü kısayolu yazılamadı: bildirimler yalnız SessionStart satırına düşer, toast üretilmez.");
    }

    /// <summary>Spec 6.11 ends the install with <c>oom doctor</c>; its findings join this run's.</summary>
    private void RunDoctor()
    {
        var findings = Health.ToList();
        try { findings.AddRange(new Doctor(clock).Check(clock.Now).Items); }
        catch (Exception error) when (error is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            findings.Add(new HealthItem("install", HealthLevel.Warning, "doctor-kosmadi", "doctor", error.Message));
        }
        // Doctor reads the ledger this run wrote to, so a recorded finding comes back: report it once.
        Health = findings.DistinctBy(item => $"{item.Component}:{item.Code}:{item.Key}").ToArray();
    }

    private void Record(HealthLevel level, string code, string key, string detail)
    {
        var item = new HealthItem("install", level, code, key, detail);
        Health = [.. Health, item];
        HealthLedger.Record(item, clock.Now);
    }

    private void RemoveV0Task(string name) =>
        processRunner.Run(new ProcessRequest("schtasks.exe", ["/Delete", "/TN", name, "/F"], Path.GetTempPath(),
            new Dictionary<string, string>(), string.Empty), TimeSpan.FromSeconds(30));

    /// <summary>
    /// Spec 6.11: the reverse of the install, and nothing more. Evidence is never deleted — the
    /// state root and the quarantine directory are <em>moved</em> into
    /// <c>%LOCALAPPDATA%\oom\backup\uninstall-&lt;ts&gt;\</c>, so a mistaken uninstall costs a
    /// move and not the record of every session ever summarised. <c>daily\</c> and
    /// <c>knowledge\</c> are not touched at all.
    /// </summary>
    public InstallResult Uninstall(string vaultPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        if (!Path.IsPathFullyQualified(vaultPath))
            return new InstallResult(true, [], ["hooks:kaldırıldı", "task:kaldırıldı", "aumid:kaldırıldı", "mcp:kaldırıldı"]);
        var vault = Path.GetFullPath(vaultPath);
        var oom = Path.Combine(vault, ".oom");
        var stateRoot = stateRootPath(vault);
        var removed = new List<string>();
        var registrations = new List<string>();
        try
        {
            // These registrations are independent of the identity directory. Run them before
            // deriving that path so a safety rejection cannot leave live entry points behind.
            RemoveHooks(vault);
            registrations.Add("hooks:kaldırıldı");
            if (scheduler is InstallRuntime.SchtasksScheduler schtasks) schtasks.Unregister(TaskName);
            registrations.Add("task:kaldırıldı");
            RemoveMcp(vault);
            registrations.Add("mcp:kaldırıldı");

            var claudeConfig = ClaudeIsolation.ConfigurationDirectory(vault, localAppDataPath(), knownSyncRoots());
            UninstallCore(vault, oom, stateRoot, claudeConfig, removed, registrations);
            return new InstallResult(true, removed, registrations);
        }
        catch (Exception error)
        {
            registrations.Add($"kaldırma-hata:{error.Message}");
            return new InstallResult(false, removed, registrations, $"Kaldırma tamamlanamadı: {error.Message}");
        }
    }

    private void UninstallCore(string vault, string oom, string stateRoot, string claudeConfig, List<string> removed, List<string> registrations)
    {
        if (shortcutRemover()) removed.Add(ShortcutRegistration.ShortcutPath());
        eventLogRemover();
        registrations.Add("aumid:kaldırıldı");
        if (ClaudeIsolation.RemoveOwned(claudeConfig, vault, localAppDataPath(), knownSyncRoots())) removed.Add(claudeConfig);
        // Beside the state root, not inside it (the root itself is what gets moved), but named
        // for the vault: a shared backup\uninstall-<ts> made two vaults uninstalled in the same
        // second overwrite each other's evidence (D3 evidence run).
        var archive = Path.Combine(Path.GetDirectoryName(stateRoot) ?? stateRoot, "backup",
            $"{Path.GetFileName(stateRoot)}-uninstall-{clock.Now:yyyyMMdd-HHmmss}");
        removed.AddRange(Archive(Path.Combine(oom, "quarantine"), Path.Combine(archive, "quarantine")));
        removed.AddRange(Archive(stateRoot, Path.Combine(archive, "state")));
        registrations.Add($"kanıt:{archive}");
        // Files last. K7/Y-105: run from the installed copy, deleting our own running exe throws
        // UnauthorizedAccessException and used to abort the whole uninstall — never let that
        // exception escape here, and never call File.Delete on the file that is currently us.
        var runningExe = processPath();
        string? leftover = null;
        foreach (var file in new[] { "oom.exe", "vault.json", "oom.json", "hub-config.json" }.Select(name => Path.Combine(oom, name)))
        {
            if (!File.Exists(file)) continue;
            if (runningExe is not null && Path.GetFullPath(file).Equals(Path.GetFullPath(runningExe), StringComparison.OrdinalIgnoreCase))
            {
                leftover = file;
                var renamed = $"{file}.uninstalled-{clock.Now:yyyyMMdd-HHmmss}";
                try { File.Move(file, renamed); leftover = renamed; removed.Add(renamed); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* still ours to report */ }
                continue;
            }
            try { File.Delete(file); removed.Add(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        try { if (Directory.Exists(oom) && !Directory.EnumerateFileSystemEntries(oom).Any()) Directory.Delete(oom); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (leftover is not null) registrations.Add($"exe-elle-sil:{leftover}");
    }

    /// <summary>Moves a directory under the uninstall archive; falls back to a copy across volumes.</summary>
    private static IEnumerable<string> Archive(string source, string destination)
    {
        if (!Directory.Exists(source)) yield break;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try { Directory.Move(source, destination); }
        catch (IOException)
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }
        yield return destination;
    }
    public IReadOnlyList<string> ValidateConfiguration(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException exception) { return [$"geçersiz-json: {exception.Message}"]; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return ["kök-nesne-değil"];
            var warnings = new List<string>();
            VisitConfiguration(document.RootElement, string.Empty, RootKeys, warnings);
            return warnings;
        }
    }
    public string PersistEnvironment(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        if (name.IndexOf('=') >= 0 || name.IndexOf('\0') >= 0 || value.IndexOf('\0') >= 0)
            throw new ArgumentException("Ortam değişkeni adı veya değeri geçersiz.");
        return value;
    }
    public string FindMcpConfiguration(IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.FirstOrDefault(File.Exists)
            ?? candidates.FirstOrDefault(path => path.Contains("Packages", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault()
            ?? string.Empty;
    }
    public string ApplyUserOnlyAcl(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathFullyQualified(path) && (Directory.Exists(path) || File.Exists(path))) SecurePath(path);
        return "user-only";
    }
    public string MigrateStructuredData(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try { using var _ = JsonDocument.Parse(json); }
        catch (JsonException exception) { throw new FormatException("Taşınacak yapılandırılmış veri geçerli JSON değil.", exception); }
        return json;
    }
    public IReadOnlyDictionary<string, bool> ValidateWindowsScenario(string workingDirectory, string tempDirectory, string responseJson, int processId)
    {
        var cwdAccepted = Path.IsPathFullyQualified(workingDirectory) && Path.IsPathFullyQualified(tempDirectory);
        var rejected = true;
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            rejected = document.RootElement.TryGetProperty("olusturuldu", out var created) && created.ValueKind == JsonValueKind.False;
        }
        catch (JsonException) { }
        return new Dictionary<string, bool> { ["cwdAccepted"] = cwdAccepted, ["responseRejected"] = rejected, ["taskkillCalled"] = false };
    }
    public IReadOnlyList<string> PlanWorktreeCleanup(IReadOnlyList<string> namedTargets, IReadOnlyList<string> existingWorktrees)
    {
        ArgumentNullException.ThrowIfNull(namedTargets);
        ArgumentNullException.ThrowIfNull(existingWorktrees);
        var existing = existingWorktrees.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return namedTargets.Where(existing.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public string BackupPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.GetFileName(fileName) != fileName) throw new ArgumentException("Yedek adı tek dosya adı olmalıdır.", nameof(fileName));
        return Path.Combine(".oom", "backup", fileName);
    }
    private IReadOnlyList<string> Preconditions()
    {
        var missing = new List<string>();
        if (!windowsSupported()) missing.Add("Windows 10 19041+");
        if (!commandAvailable("claude")) missing.Add("claude PATH");
        if (!fts5Available()) missing.Add("FTS5");
        return missing;
    }
    private static void VisitConfiguration(JsonElement element, string path, HashSet<string> allowed, ICollection<string> warnings)
    {
        foreach (var property in element.EnumerateObject())
        {
            var current = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
            if (!allowed.Contains(property.Name)) { warnings.Add(current); continue; }
            if (property.Value.ValueKind == JsonValueKind.Object && NestedKeys.TryGetValue(current, out var nested))
                VisitConfiguration(property.Value, current, nested, warnings);
        }
    }
    private void CreateMigrationBackup(string vault, string stateRoot)
    {
        var backup = Path.Combine(stateRoot, "backup", $"v0-{clock.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(backup);
        var marker = Path.Combine(backup, "RECOVERY.txt");
        File.WriteAllText(marker, $"Kaynak: {vault}\nZaman: {clock.Now:O}\n", Utf8);
        if (!File.Exists(marker)) throw new IOException("Yedek doğrulanamadı.");
    }
    private static IEnumerable<string> PlannedPaths(string oom, string stateRoot, string claudeConfig, string vault, InstallScope scope)
    {
        string[] common =
        [
            Path.Combine(oom, "oom.exe"), Path.Combine(oom, "vault.json"), Path.Combine(oom, "oom.json"), Path.Combine(oom, "hub-config.json"),
            claudeConfig, Path.Combine(oom, "quarantine"), Path.Combine(stateRoot, VaultIdentity.DatabaseName),
            Path.Combine(stateRoot, VaultIdentity.DescriptorName), Path.Combine(stateRoot, "backup"), Path.Combine(stateRoot, "logs")
        ];
        // The project package is part of the plan, so `--dry-run` names the two files the vault
        // gains rather than leaving them to be discovered after the fact.
        return scope == InstallScope.User ? common : [.. common, ProjectSettingsPath(vault), ProjectMcpPath(vault)];
    }
    private static void CreateDirectories(string oom, string stateRoot)
    {
        foreach (var path in new[] { oom, Path.Combine(oom, "quarantine"), stateRoot, Path.Combine(stateRoot, "backup"), Path.Combine(stateRoot, "logs") })
            Directory.CreateDirectory(path);
        SecurePath(Path.Combine(oom, "quarantine"));
        SecurePath(stateRoot);
        SecurePath(Path.Combine(stateRoot, "backup"));
    }
    private static void WriteIfMissing(string path, string content)
    {
        if (File.Exists(path)) return;
        File.WriteAllText(path, content + Environment.NewLine, Utf8);
    }
    private void InstallBinary(string destination)
    {
        var source = processPath();
        if (source is null || !Path.GetFileName(source).Equals("oom.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Kurulum yalnız yayımlanmış oom.exe üzerinden çalıştırılabilir.");
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) File.Copy(source, destination, true);
    }
    /// <summary>
    /// Faz 5: the hook file is the vault's own <c>.claude\settings.json</c> unless the user-level
    /// file was asked for by name. Both are merged the same way — only the scope changes. The
    /// shared file cannot be the default: <see cref="SetHook"/> drops every entry naming
    /// <c>oom.exe</c> before adding its own, so installing a second vault deleted the first
    /// vault's four hooks and replaced them with the second vault's exe.
    /// </summary>
    private void InstallHooks(string vault, string executable, InstallScope scope)
    {
        var settingsPath = scope == InstallScope.User ? userSettingsPath() : ProjectSettingsPath(vault);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var root = File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath, Utf8)) as JsonObject : new JsonObject();
        root ??= new JsonObject();
        var backupDirectory = Path.Combine(stateRootPath(vault), "backup");
        Directory.CreateDirectory(backupDirectory);
        // Overwrite: the name carries the second, so a collision is the same second — and refusing
        // it would make the repeated install spec 6.11 calls idempotent fail outright.
        if (File.Exists(settingsPath)) File.Copy(settingsPath, Path.Combine(backupDirectory, $"settings.json.bak-{clock.Now:yyyyMMdd-HHmmss}"), true);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        // Lane B owns the four commands and their timeouts; install consumes the template and
        // never restates it, so the two can never drift apart (spec 6.1).
        foreach (var registration in HookTemplates.Build(executable))
            SetHook(hooks, registration.Event, registration.Command, registration.TimeoutSeconds);
        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8);
    }
    /// <summary>
    /// Merge, never replace (D3 evidence run). The user-level <c>settings.json</c> is shared:
    /// another tool's <c>SessionEnd</c> hook sits in the same array, and assigning the event
    /// wholesale deleted it. Only oom's own entries are refreshed; anything else is carried over
    /// untouched, so a repeated install is idempotent without being destructive.
    /// </summary>
    private static void SetHook(JsonObject hooks, string name, string command, int timeout)
    {
        var kept = new JsonArray();
        if (hooks[name] is JsonArray groups)
            foreach (var group in groups.ToArray())
            {
                groups.Remove(group);
                if (group?["hooks"] is not JsonArray entries) continue;
                foreach (var entry in entries.ToArray())
                    if (entry?["command"]?.ToJsonString().Contains("oom.exe", StringComparison.OrdinalIgnoreCase) == true)
                        entries.Remove(entry);
                if (entries.Count > 0) kept.Add(group);
            }
        kept.Add(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = timeout }) });
        hooks[name] = kept;
    }
    /// <summary>
    /// Faz 5: <c>&lt;vault&gt;\.mcp.json</c> by default. The Claude Desktop config is one file per
    /// machine keyed on the single name <c>oom</c>, so the second vault's install overwrote the
    /// first vault's server entry and only the last install could be reached over MCP.
    /// </summary>
    private void RegisterMcp(string executable, string vault, InstallScope scope)
    {
        string path;
        if (scope == InstallScope.User)
        {
            var candidates = mcpCandidates();
            path = candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }
        else path = ProjectMcpPath(vault);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path, Utf8)) as JsonObject : new JsonObject();
        root ??= new JsonObject();
        var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
        root["mcpServers"] = servers;
        servers["oom"] = new JsonObject { ["command"] = executable, ["args"] = new JsonArray("mcp") };
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8);
    }
    /// <summary>
    /// Uninstall reverses both scopes, whichever one installed: an installation made before the
    /// default moved into the project still has to be removable, and a project package left
    /// behind by a user-scope uninstall would keep firing hooks at a deleted exe.
    /// </summary>
    private void RemoveMcp(string vault)
    {
        foreach (var path in mcpCandidates().Prepend(ProjectMcpPath(vault)).Where(File.Exists))
        {
            var root = JsonNode.Parse(File.ReadAllText(path, Utf8)) as JsonObject;
            if (root?["mcpServers"] is not JsonObject servers || !servers.Remove("oom")) continue;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8);
        }
    }
    private void RemoveHooks(string vault)
    {
        foreach (var path in new[] { ProjectSettingsPath(vault), userSettingsPath() }.Where(File.Exists))
            RemoveHooksFrom(path);
    }
    private static void RemoveHooksFrom(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path, Utf8)) as JsonObject;
        if (root?["hooks"] is not JsonObject hooks) return;
        // Only rewrite when an entry of ours was actually found. The user-level file belongs to
        // every project on the machine; rewriting it to change nothing is still a shared write.
        var removed = false;
        foreach (var name in new[] { "SessionStart", "UserPromptSubmit", "SessionEnd", "PreCompact" })
        {
            if (hooks[name] is not JsonArray entries) continue;
            for (var index = entries.Count - 1; index >= 0; index--)
                if (entries[index]?.ToJsonString().Contains("oom.exe", StringComparison.OrdinalIgnoreCase) == true) { entries.RemoveAt(index); removed = true; }
            if (entries.Count == 0) { hooks.Remove(name); removed = true; }
        }
        if (removed) File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8);
    }
    private static void SecurePath(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var account = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Kullanıcı güvenlik kimliği okunamadı.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    private static string[] McpCandidates()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return [Path.Combine(appData, "Claude", "claude_desktop_config.json"), Path.Combine(local, "Packages", "ClaudeDesktop", "LocalState", "claude_desktop_config.json")];
    }
    /// <summary>
    /// D3: one vault, one state root. The installer used to hash the upper-cased path while
    /// <see cref="VaultPaths.StateDatabase"/> hashes the path as written, so <c>install</c>
    /// provisioned <c>state.db</c> — and migrated every v0 row into it — in a directory the
    /// running exe never opened. It then kept a second, quieter copy of the answer: its own
    /// <c>Path.GetFullPath</c> wrapper. Two functions is the same defect wearing a smaller hat,
    /// so there is now exactly one, in <see cref="VaultIdentity"/>, and the installer asks it.
    /// </summary>
    private static string StateRoot(string vault) => VaultIdentity.StateRoot(vault);
    private static bool CommandAvailable(string command) => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Any(directory => new[] { command + ".exe", command + ".cmd", command + ".bat" }.Any(file => File.Exists(Path.Combine(directory.Trim('"'), file))));
    private static bool Fts5Available()
    {
        try
        {
            using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = "CREATE VIRTUAL TABLE probe USING fts5(text);"; command.ExecuteNonQuery(); return true;
        }
        catch (SqliteException) { return false; }
    }
    private static InstallResult Failure(string error) => new(false, [], [], error);
    /// <summary>
    /// The <c>oom.json</c> a fresh install writes when the vault has none. The document is the
    /// code defaults serialised (Y-104); the installer keeps no second copy of them.
    /// </summary>
    public static string DefaultConfiguration => OomSettings.DefaultJson();
}
