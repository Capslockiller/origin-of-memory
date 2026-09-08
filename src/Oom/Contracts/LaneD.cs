namespace Oom.Contracts;

public sealed class Doctor
{
    public DoctorResult Check(DateTimeOffset now) => throw new NotImplementedException("lane D: Doctor.Check");
    public DoctorResult Fix() => throw new NotImplementedException("lane D: Doctor.Fix");
    public IReadOnlyList<HealthItem> ValidateHooks(string userSettings, string projectSettings) => throw new NotImplementedException("lane D: Doctor.ValidateHooks");
    public HealthItem ValidateInstalledBinary(string installedPath, string releasedPath) => throw new NotImplementedException("lane D: Doctor.ValidateInstalledBinary");
    public VerifyResult VerifyIndex(IReadOnlyList<string> corpus, IReadOnlyList<string> index) => throw new NotImplementedException("lane D: Doctor.VerifyIndex");
    public IReadOnlyList<HealthItem> ValidateReleaseClaims(IReadOnlyDictionary<string, string> claims) => throw new NotImplementedException("lane D: Doctor.ValidateReleaseClaims");
}

public sealed class Notify
{
    public NotificationResult Send(string notificationClass, string key, string text, DateTimeOffset now) => throw new NotImplementedException("lane D: Notify.Send");
}

public sealed class Mcp
{
    public RetrieveResult MemorySearch(string query, int limit) => throw new NotImplementedException("lane D: Mcp.MemorySearch");
    public string MemoryRootMap() => throw new NotImplementedException("lane D: Mcp.MemoryRootMap");
    public string MemoryNote(string name) => throw new NotImplementedException("lane D: Mcp.MemoryNote");
}

public sealed class Ingest
{
    public Session ParseClaude(string jsonl) => throw new NotImplementedException("lane D: Ingest.ParseClaude");
    public Session ParseCodex(string jsonl) => throw new NotImplementedException("lane D: Ingest.ParseCodex");
    public IReadOnlyList<Session> Run(string source, IReadOnlyList<string> files, int? max = null) => throw new NotImplementedException("lane D: Ingest.Run");
    public IReadOnlyList<string> ValidateSourceInventory(IReadOnlyList<string> runners, IReadOnlyList<string> exclusions) => throw new NotImplementedException("lane D: Ingest.ValidateSourceInventory");
}

public sealed class Install
{
    public InstallResult Run(string vaultPath, bool fromV0 = false) => throw new NotImplementedException("lane D: Install.Run");
    public InstallResult Uninstall(string vaultPath) => throw new NotImplementedException("lane D: Install.Uninstall");
    public IReadOnlyList<string> ValidateConfiguration(string json) => throw new NotImplementedException("lane D: Install.ValidateConfiguration");
    public string PersistEnvironment(string name, string value) => throw new NotImplementedException("lane D: Install.PersistEnvironment");
    public string FindMcpConfiguration(IReadOnlyList<string> candidates) => throw new NotImplementedException("lane D: Install.FindMcpConfiguration");
    public string ApplyUserOnlyAcl(string path) => throw new NotImplementedException("lane D: Install.ApplyUserOnlyAcl");
    public string MigrateStructuredData(string json) => throw new NotImplementedException("lane D: Install.MigrateStructuredData");
    public IReadOnlyDictionary<string, bool> ValidateWindowsScenario(string workingDirectory, string tempDirectory, string responseJson, int processId) => throw new NotImplementedException("lane D: Install.ValidateWindowsScenario");
    public IReadOnlyList<string> PlanWorktreeCleanup(IReadOnlyList<string> namedTargets, IReadOnlyList<string> existingWorktrees) => throw new NotImplementedException("lane D: Install.PlanWorktreeCleanup");
    public string BackupPath(string fileName) => throw new NotImplementedException("lane D: Install.BackupPath");
}

public sealed class Save
{
    public CheckpointResult WriteCheckpoint(string text, IReadOnlyList<string> requiredFields) => throw new NotImplementedException("lane D: Save.WriteCheckpoint");
    public FlushResult SaveSessionJson(string json) => throw new NotImplementedException("lane D: Save.SaveSessionJson");
}
