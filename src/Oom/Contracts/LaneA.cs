namespace Oom.Contracts;

public sealed class State
{
    public UsageSummary AggregateUsage(IEnumerable<UsageRecord> records) => throw new NotImplementedException("lane A: State.AggregateUsage");
    public StateStats SweepRetention(DateTimeOffset now) => throw new NotImplementedException("lane A: State.SweepRetention");
    public IReadOnlyList<HealthItem> WriteHealthConcurrently(IEnumerable<HealthItem> items) => throw new NotImplementedException("lane A: State.WriteHealthConcurrently");
    public string AtomicWrite(string path, string content, int writers) => throw new NotImplementedException("lane A: State.AtomicWrite");
    public string MachineIdentity(string hostname, ulong discriminator) => throw new NotImplementedException("lane A: State.MachineIdentity");
    public LockResult AcquireLock(string name, string machineIdentity, int pid) => throw new NotImplementedException("lane A: State.AcquireLock");
    public QuotaResult ReadQuota(IReadOnlyDictionary<string, string> liveResponses, IReadOnlyList<QuotaWindow> cached, DateTimeOffset now) => throw new NotImplementedException("lane A: State.ReadQuota");
    public string BuildQuotaRequest(string source) => throw new NotImplementedException("lane A: State.BuildQuotaRequest");
}

public sealed class Runner
{
    public RunResult Run(string prompt, ModelTier tier, ComponentKind component, string purpose) => throw new NotImplementedException("lane A: Runner.Run");
    public ProcessRequest BuildClaudeRequest(string prompt, string model, string vaultPath, string isolatedConfigDir) => throw new NotImplementedException("lane A: Runner.BuildClaudeRequest");
    public LocalRequest BuildLocalRequest(string prompt, string model, int maxTokens) => throw new NotImplementedException("lane A: Runner.BuildLocalRequest");
    public CallTiming ReadLocalTiming(string responseJson) => throw new NotImplementedException("lane A: Runner.ReadLocalTiming");
    public ProcessResult RunProcess(ProcessRequest request, TimeSpan timeout) => throw new NotImplementedException("lane A: Runner.RunProcess");
    public string ResolveExecutable(string command, string pathValue) => throw new NotImplementedException("lane A: Runner.ResolveExecutable");
    public IntPtr PreserveHandle(ulong handle) => throw new NotImplementedException("lane A: Runner.PreserveHandle");
    public WaitResult WaitForOutcome(string target, int maxAttempts) => throw new NotImplementedException("lane A: Runner.WaitForOutcome");
}

public sealed class Guards
{
    public GateResult Gate(string text, Direction direction, ComponentKind component) => throw new NotImplementedException("lane A: Guards.Gate");
    public string NormalizePath(string path) => throw new NotImplementedException("lane A: Guards.NormalizePath");
}

public sealed class Notes
{
    public Note Parse(string path, string text) => throw new NotImplementedException("lane A: Notes.Parse");
    public Note Validate(Note note) => throw new NotImplementedException("lane A: Notes.Validate");
    public string IndexableText(Note note) => throw new NotImplementedException("lane A: Notes.IndexableText");
}

public sealed class TurkishFold
{
    public string Fold(string text) => throw new NotImplementedException("lane A: TurkishFold.Fold");
    public IReadOnlyList<string> Tokenize(string text) => throw new NotImplementedException("lane A: TurkishFold.Tokenize");
}
