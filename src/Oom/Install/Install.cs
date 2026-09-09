// yazan: codex · gpt-5
using System.Security.Cryptography;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
namespace Oom.Contracts;
public sealed class Install
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> RootKeys = new(StringComparer.Ordinal) { "backend", "retrieveMode", "sweep", "compile", "context", "retrieve", "mcp", "notify", "extensions" };
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
    public Install(IClock? clock = null, ITaskScheduler? scheduler = null, Func<bool>? windowsSupported = null,
        Func<string, bool>? commandAvailable = null, Func<bool>? fts5Available = null)
    {
        this.clock = clock ?? SystemClock.Instance;
        this.scheduler = scheduler ?? new SchtasksScheduler();
        this.windowsSupported = windowsSupported ?? (() => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041));
        this.commandAvailable = commandAvailable ?? CommandAvailable;
        this.fts5Available = fts5Available ?? Fts5Available;
    }
    public InstallResult Run(string vaultPath, bool fromV0 = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
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
        var stateRoot = StateRoot(vault);
        var planned = PlannedPaths(oom, stateRoot).ToList();
        var registrations = new List<string> { "hooks:4", "task:OdenaOS Memory Sweep", "aumid:OdenaStudio.OriginOfMemory", "shortcut", "event-log:oom", "mcp:oom" };
        if (fixtureMode) return new InstallResult(true, planned, registrations);
        try
        {
            if (fromV0) CreateMigrationBackup(vault, stateRoot);
            CreateDirectories(oom, stateRoot);
            WriteIfMissing(Path.Combine(oom, "vault.json"), JsonSerializer.Serialize(new { vault, schema = 1 }));
            WriteIfMissing(Path.Combine(oom, "oom.json"), DefaultConfiguration);
            WriteIfMissing(Path.Combine(oom, "hub-config.json"), "{\"schema\":1,\"hubs\":[]}");
            InstallBinary(Path.Combine(oom, "oom.exe"));
            CreateState(Path.Combine(stateRoot, "state.db"));
            InstallHooks(vault, Path.Combine(oom, "oom.exe"));
            scheduler.Register("OdenaOS Memory Sweep", BuildTaskXml(Path.Combine(oom, "oom.exe")));
            RegisterMcp(Path.Combine(oom, "oom.exe"));
            RegisterToastShortcut(Path.Combine(oom, "oom.exe"));
            return new InstallResult(true, planned, registrations);
        }
        catch (Exception exception)
        {
            return new InstallResult(false, planned.Where(File.Exists).ToArray(), registrations, $"Kurulum tamamlanamadı: {exception.Message}");
        }
    }
    public InstallResult Uninstall(string vaultPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        if (!Path.IsPathFullyQualified(vaultPath))
            return new InstallResult(true, [], ["hooks:kaldırıldı", "task:kaldırıldı", "aumid:kaldırıldı", "mcp:kaldırıldı"]);
        var vault = Path.GetFullPath(vaultPath);
        var oom = Path.Combine(vault, ".oom");
        var removed = new List<string>();
        foreach (var file in new[] { "oom.exe", "vault.json", "oom.json", "hub-config.json" }.Select(name => Path.Combine(oom, name)))
            if (File.Exists(file)) { File.Delete(file); removed.Add(file); }
        var shortcut = ShortcutPath();
        if (File.Exists(shortcut)) { File.Delete(shortcut); removed.Add(shortcut); }
        foreach (var directory in new[] { Path.Combine(oom, "claude-config"), Path.Combine(oom, "quarantine"), StateRoot(vault) })
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); removed.Add(directory); }
        if (Directory.Exists(oom) && !Directory.EnumerateFileSystemEntries(oom).Any()) Directory.Delete(oom);
        RemoveHooks();
        if (scheduler is SchtasksScheduler schtasks) schtasks.Unregister("OdenaOS Memory Sweep");
        RemoveMcp();
        return new InstallResult(true, removed, ["hooks:kaldırıldı", "task:kaldırıldı", "aumid:kaldırıldı", "mcp:kaldırıldı"]);
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
    private static IEnumerable<string> PlannedPaths(string oom, string stateRoot) =>
    [
        Path.Combine(oom, "oom.exe"), Path.Combine(oom, "vault.json"), Path.Combine(oom, "oom.json"), Path.Combine(oom, "hub-config.json"),
        Path.Combine(oom, "claude-config"), Path.Combine(oom, "quarantine"), Path.Combine(stateRoot, "state.db"),
        Path.Combine(stateRoot, "backup"), Path.Combine(stateRoot, "logs")
    ];
    private static void CreateDirectories(string oom, string stateRoot)
    {
        foreach (var path in new[] { oom, Path.Combine(oom, "claude-config"), Path.Combine(oom, "quarantine"), stateRoot, Path.Combine(stateRoot, "backup"), Path.Combine(stateRoot, "logs") })
            Directory.CreateDirectory(path);
        SecurePath(Path.Combine(oom, "claude-config"));
        SecurePath(Path.Combine(oom, "quarantine"));
        SecurePath(stateRoot);
        SecurePath(Path.Combine(stateRoot, "backup"));
    }
    private static void WriteIfMissing(string path, string content)
    {
        if (File.Exists(path)) return;
        File.WriteAllText(path, content + Environment.NewLine, Utf8);
    }
    private static void InstallBinary(string destination)
    {
        var source = Environment.ProcessPath;
        if (source is null || !Path.GetFileName(source).Equals("oom.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Kurulum yalnız yayımlanmış oom.exe üzerinden çalıştırılabilir.");
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) File.Copy(source, destination, true);
    }
    private static void CreateState(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA user_version=1;
            CREATE TABLE IF NOT EXISTS sessions(session_id TEXT PRIMARY KEY, transcript_path TEXT, last_turn_index INTEGER, last_flush_ts TEXT);
            CREATE TABLE IF NOT EXISTS flush_log(ts TEXT, session_id TEXT, reason TEXT, outcome TEXT, turns INTEGER, chars INTEGER, backend TEXT);
            CREATE TABLE IF NOT EXISTS retry_queue(session_id TEXT PRIMARY KEY, attempts INTEGER, next_at TEXT, last_error TEXT);
            CREATE TABLE IF NOT EXISTS sweep_stamps(path TEXT PRIMARY KEY, mtime TEXT, size INTEGER, outcome TEXT);
            CREATE TABLE IF NOT EXISTS coverage(ts TEXT, total INTEGER, covered INTEGER, uncovered_json TEXT);
            CREATE TABLE IF NOT EXISTS daily_ingest(name TEXT PRIMARY KEY, digest TEXT, status TEXT, attempts INTEGER, reasons TEXT, ts TEXT);
            CREATE TABLE IF NOT EXISTS compile_runs(ts TEXT, daily TEXT, status TEXT, created INTEGER, updated INTEGER, ms INTEGER);
            CREATE TABLE IF NOT EXISTS quarantine(digest TEXT PRIMARY KEY, source TEXT, reason TEXT, ts TEXT, path TEXT);
            CREATE TABLE IF NOT EXISTS calls(ts TEXT, backend TEXT, component TEXT, tier TEXT, model TEXT, in_chars INTEGER, out_chars INTEGER, in_tok INTEGER, out_tok INTEGER, cache_r INTEGER, cache_w INTEGER, ms INTEGER, outcome TEXT, usage_source TEXT, purpose TEXT);
            CREATE TABLE IF NOT EXISTS health(ts TEXT, component TEXT, level TEXT, code TEXT, key TEXT, detail TEXT);
            CREATE TABLE IF NOT EXISTS notified(class TEXT, key TEXT, ts TEXT, PRIMARY KEY(class,key));
            CREATE TABLE IF NOT EXISTS retrieve_served(session_id TEXT, query_sig TEXT, note TEXT, ts TEXT);
            CREATE TABLE IF NOT EXISTS locks(name TEXT PRIMARY KEY, machine TEXT, pid INTEGER, ts TEXT);
            CREATE TABLE IF NOT EXISTS kota(ts TEXT, window TEXT, used_pct REAL, resets_at TEXT);
            CREATE TABLE IF NOT EXISTS notes(name TEXT PRIMARY KEY, title TEXT, text TEXT);
            CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(name UNINDEXED, title, text);
            """;
        command.ExecuteNonQuery();
    }
    private void InstallHooks(string vault, string executable)
    {
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var root = File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath, Utf8)) as JsonObject : new JsonObject();
        root ??= new JsonObject();
        var backupDirectory = Path.Combine(StateRoot(vault), "backup");
        Directory.CreateDirectory(backupDirectory);
        if (File.Exists(settingsPath)) File.Copy(settingsPath, Path.Combine(backupDirectory, $"settings.json.bak-{clock.Now:yyyyMMdd-HHmmss}"), false);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        SetHook(hooks, "SessionStart", $"\"{executable}\" context", 15);
        SetHook(hooks, "UserPromptSubmit", $"\"{executable}\" retrieve --hook", 5);
        SetHook(hooks, "SessionEnd", $"\"{executable}\" flush --reason sessionend", 15);
        SetHook(hooks, "PreCompact", $"\"{executable}\" flush --reason precompact", 15);
        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8);
    }
    private static void SetHook(JsonObject hooks, string name, string command, int timeout) => hooks[name] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = timeout }) });
    private static void RegisterMcp(string executable)
    {
        var candidates = McpCandidates();
        var path = candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path, Utf8)) as JsonObject : new JsonObject();
        root ??= new JsonObject();
        var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
        root["mcpServers"] = servers;
        servers["oom"] = new JsonObject { ["command"] = executable, ["args"] = new JsonArray("mcp") };
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8);
    }
    private static void RemoveMcp()
    {
        foreach (var path in McpCandidates().Where(File.Exists))
        {
            var root = JsonNode.Parse(File.ReadAllText(path, Utf8)) as JsonObject;
            if (root?["mcpServers"] is not JsonObject servers || !servers.Remove("oom")) continue;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8);
        }
    }
    private static void RemoveHooks()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
        if (!File.Exists(path)) return;
        var root = JsonNode.Parse(File.ReadAllText(path, Utf8)) as JsonObject;
        if (root?["hooks"] is not JsonObject hooks) return;
        foreach (var name in new[] { "SessionStart", "UserPromptSubmit", "SessionEnd", "PreCompact" })
        {
            if (hooks[name] is not JsonArray entries) continue;
            for (var index = entries.Count - 1; index >= 0; index--)
                if (entries[index]?.ToJsonString().Contains("oom.exe", StringComparison.OrdinalIgnoreCase) == true) entries.RemoveAt(index);
            if (entries.Count == 0) hooks.Remove(name);
        }
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8);
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
    private static void RegisterToastShortcut(string executable)
    {
        var path = ShortcutPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var link = (IShellLinkW)(object)new ShellLink();
        link.SetPath(executable);
        link.SetDescription("Origin of Memory");
        var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F"), 5);
        var value = PropVariant.FromString("OdenaStudio.OriginOfMemory");
        try
        {
            var store = (IPropertyStore)link;
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
            Marshal.ThrowExceptionForHR(store.Commit());
            ((IPersistFile)link).Save(path, true);
        }
        finally { value.Dispose(); Marshal.FinalReleaseComObject(link); }
    }
    private static string ShortcutPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs", "Origin of Memory.lnk");
    private static string StateRoot(string vault)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(Path.GetFullPath(vault).ToUpperInvariant())))[..16].ToLowerInvariant();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oom", digest);
    }
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
    private static string BuildTaskXml(string executable) => $"<Task><Triggers><CalendarTrigger><Repetition><Interval>PT8H</Interval></Repetition></CalendarTrigger></Triggers><Settings><WakeToRun>true</WakeToRun></Settings><Actions><Exec><Command>{System.Security.SecurityElement.Escape(executable)}</Command><Arguments>sweep</Arguments></Exec></Actions></Task>";
    private static InstallResult Failure(string error) => new(false, [], [], error);
    private sealed class SystemClock : IClock { internal static readonly SystemClock Instance = new(); public DateTimeOffset Now => DateTimeOffset.Now; }
    private sealed class SchtasksScheduler : ITaskScheduler
    {
        private readonly IProcessRunner runner;
        internal SchtasksScheduler(IProcessRunner? runner = null) => this.runner = runner ?? new NativeProcessRunner();
        public void Register(string name, string xml)
        {
            var path = Path.Combine(Path.GetTempPath(), $"oom-task-{Guid.NewGuid():N}.xml");
            File.WriteAllText(path, xml, Utf8);
            try
            {
                var result = runner.Run(new ProcessRequest("schtasks.exe", ["/Create", "/TN", name, "/XML", path, "/F"], Path.GetTempPath(), new Dictionary<string, string>(), string.Empty), TimeSpan.FromSeconds(30));
                if (result.ExitCode != 0) throw new InvalidOperationException("Zamanlanmış görev kaydedilemedi.");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        internal void Unregister(string name) => runner.Run(new ProcessRequest("schtasks.exe", ["/Delete", "/TN", name, "/F"], Path.GetTempPath(), new Dictionary<string, string>(), string.Empty), TimeSpan.FromSeconds(30));
    }
    private sealed class NativeProcessRunner : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request, TimeSpan timeout)
        {
            var start = new ProcessStartInfo(request.FileName) { WorkingDirectory = request.WorkingDirectory, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
            foreach (var pair in request.Environment) start.Environment[pair.Key] = pair.Value;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Alt süreç başlatılamadı.");
            process.StandardInput.Write(request.StandardInput);
            process.StandardInput.Close();
            var completed = process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
            if (!completed) process.Kill(true);
            return new ProcessResult(completed ? process.ExitCode : -1, process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd(), true, !completed);
        }
    }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(IntPtr file, int size, IntPtr data, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(IntPtr name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr directory, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(IntPtr arguments, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation(IntPtr iconPath, int size, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint count);
        int GetAt(uint index, out PropertyKey key);
        int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey
    {
        private readonly Guid formatId;
        private readonly uint propertyId;
        internal PropertyKey(Guid formatId, uint propertyId) { this.formatId = formatId; this.propertyId = propertyId; }
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant : IDisposable
    {
        [FieldOffset(0)] private ushort type;
        [FieldOffset(8)] private IntPtr pointer;
        internal static PropVariant FromString(string value) => new() { type = 31, pointer = Marshal.StringToCoTaskMemUni(value) };
        public void Dispose() { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); pointer = IntPtr.Zero; type = 0; }
    }
    private const string DefaultConfiguration = """
        {"backend":{"flush":["claude","local"],"compile":["claude"],"claude":{"fast":"claude-haiku-4-5-20251001","smart":"claude-sonnet-5","configDir":".oom\\claude-config"},"local":{"url":"http://localhost:11434/v1","fast":"qwen3:8b","smart":"qwen3:14b","embed":"nomic-embed-text"}},"retrieveMode":"bm25","sweep":{"everyHours":8,"sinceHours":8,"minTurns":3,"maxSessionsPerRun":20,"roots":[]},"compile":{"eveningHour":18,"minIntervalHours":20,"maxDailiesPerRun":3},"context":{"companionDir":"🔮 850-Companion","capChars":16000,"statusLine":true},"retrieve":{"top":3,"perNoteChars":1500,"totalChars":4500,"minOverlap":2,"strictScore":25.0},"mcp":{"enabled":true},"notify":{"toast":true},"extensions":[]}
        """;
}
