namespace Oom.Contracts;

public sealed class Context
{
    public ContextResult Build(string vaultPath, DateTimeOffset now) => throw new NotImplementedException("lane B: Context.Build");
    public IReadOnlyList<HealthItem> AuditCompanion(string vaultPath) => throw new NotImplementedException("lane B: Context.AuditCompanion");
}

public sealed class Retrieve
{
    public VerifyResult Build() => throw new NotImplementedException("lane B: Retrieve.Build");
    public RetrieveResult Query(string query, string sessionId, int top = 3) => throw new NotImplementedException("lane B: Retrieve.Query");
    public RetrieveResult Hook(string prompt, string sessionId, IReadOnlyDictionary<string, string>? environment = null) => throw new NotImplementedException("lane B: Retrieve.Hook");
    public IReadOnlyList<SearchHit> Rank(string query, IReadOnlyList<Note> notes, string mode = "bm25") => throw new NotImplementedException("lane B: Retrieve.Rank");
    public bool ShouldInject(string prompt, SearchHit hit) => throw new NotImplementedException("lane B: Retrieve.ShouldInject");
}

public sealed class Flush
{
    public FlushResult FlushSession(string sessionId, string transcriptPath, FlushReason reason) => throw new NotImplementedException("lane B: Flush.FlushSession");
    public IReadOnlyList<TurnRange> PlanRanges(Session session, int lastTurnIndex, int maxTurns, int maxCharacters) => throw new NotImplementedException("lane B: Flush.PlanRanges");
    public IReadOnlyList<Turn> ParseTranscript(string jsonl) => throw new NotImplementedException("lane B: Flush.ParseTranscript");
    public string StoreRawTranscript(string transcriptJsonl) => throw new NotImplementedException("lane B: Flush.StoreRawTranscript");
    public SummaryValidation ValidateSummary(string output, string sessionId) => throw new NotImplementedException("lane B: Flush.ValidateSummary");
    public string AppendDaily(string existing, string block, string sessionId) => throw new NotImplementedException("lane B: Flush.AppendDaily");
    public string AppendDailyConcurrently(string existing, IReadOnlyList<string> blocks) => throw new NotImplementedException("lane B: Flush.AppendDailyConcurrently");
    public RetryRecord Retry(string sessionId, int currentAttempts, string rawOutput) => throw new NotImplementedException("lane B: Flush.Retry");
    public bool IsMechanismTranscript(string transcriptPath, string projectPath) => throw new NotImplementedException("lane B: Flush.IsMechanismTranscript");
    public DateTimeOffset EventTime(Session session, DateTimeOffset fileTime, DateTimeOffset scanTime) => throw new NotImplementedException("lane B: Flush.EventTime");
    public IngressRecord ReadHookInput(byte[] input) => throw new NotImplementedException("lane B: Flush.ReadHookInput");
}

public sealed class Sweep
{
    public SweepResult Run(IReadOnlyList<Session> sessions, SweepOptions options) => throw new NotImplementedException("lane B: Sweep.Run");
    public ReconciliationResult Reconcile(IReadOnlyList<IngressRecord> ingress, DateTimeOffset now) => throw new NotImplementedException("lane B: Sweep.Reconcile");
    public Session RelocateMissingTranscript(string sessionId, string stalePath, IReadOnlyDictionary<string, string> roots) => throw new NotImplementedException("lane B: Sweep.RelocateMissingTranscript");
    public bool ShouldProcess(DateTimeOffset modifiedAt, bool stamped, int sinceHours, DateTimeOffset now) => throw new NotImplementedException("lane B: Sweep.ShouldProcess");
    public FreshnessResult EvaluateFreshness(DateTimeOffset modifiedAt, int freshSeconds, DateTimeOffset now) => throw new NotImplementedException("lane B: Sweep.EvaluateFreshness");
    public string BuildScheduledTaskXml(string executablePath) => throw new NotImplementedException("lane B: Sweep.BuildScheduledTaskXml");
}
