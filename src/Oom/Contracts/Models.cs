namespace Oom.Contracts;

public enum FlushReason { SessionEnd, PreCompact, Sweep, Ingest }
public enum FlushOutcome { Ok, NoTurns, NoNewTurns, Empty, Retry, Parked, MissingTranscript, Unreadable, Locked }
public enum ModelTier { Fast, Smart }
public enum ComponentKind { Flush, Compile, Context, Retrieve, Sweep, Doctor, Ingest, Install }
/// <summary>
/// Which way text is crossing oom's boundary when the guard chain looks at it.
/// <c>In</c> and <c>Out</c> are both <em>admission</em>: text a person hands oom
/// (<c>In</c>) and text a model hands back (<c>Out</c>). Neither means departure, so
/// <c>Egress</c> is a third member rather than a reuse of <c>Out</c>: it is local text
/// on its way to an external model, the one direction where a finding means a secret
/// nearly left the machine instead of nearly entering the vault.
/// </summary>
public enum Direction { In, Out, Egress }
public enum HealthLevel { Info, Warning, Error }
public enum UsageSourceKind { Unknown = 0, Estimate = 1, Measured = 2 }

public sealed record Turn(int Index, string Role, string Kind, string Text, DateTimeOffset Timestamp);
public sealed record Session(string Id, string Source, IReadOnlyList<Turn> Turns, DateTimeOffset StartedAt);
public sealed record TurnRange(int Start, int End, IReadOnlyList<Turn> Turns, int Characters);
public sealed record FlushResult(FlushOutcome Outcome, int Cursor, string? DailyPath, string? Summary, string? Error = null);
public sealed record SweepOptions(int SinceHours = 8, int MinTurns = 3, int MaxSessionsPerRun = 20, int FreshSeconds = 0);
public sealed record SweepResult(int Total, int Covered, IReadOnlyList<string> UncoveredIds, int Skipped, IReadOnlyList<FlushResult> Results);
/// <summary>
/// One pass of the guard chain. <c>Direction</c> is carried out of the gate, not thrown
/// away inside it: a caller that records a finding has to be able to say whether it
/// masked something on the way in or something that was about to leave the machine.
/// </summary>
public sealed record GateResult(string Text, IReadOnlyList<string> Findings, bool Refused, Direction Direction);
public sealed record TokenUsage(long? UncachedInputTokens, long? OutputTokens, long? CacheReadTokens, long? CacheWriteTokens);
public sealed record RunResult(string Text, string? Error, string Backend, string Model, string UsageSource = "unknown", TokenUsage? Usage = null,
    string? OperationId = null, string? AttemptId = null, int? AttemptNumber = null)
{
    public UsageSourceKind UsageQuality => UsageSource switch { "measured" => UsageSourceKind.Measured, "estimate" => UsageSourceKind.Estimate, _ => UsageSourceKind.Unknown };
}
public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, string StandardInput);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool StandardInputClosed, bool TimedOut = false);
public sealed record LocalRequest(bool Stream, double Temperature, bool Think, int MaxTokens, string Model, string Prompt);
public sealed record CallTiming(string Temperature, long? LoadMs, long? PromptEvaluationMs, long? GenerationMs);
public sealed record SummaryValidation(bool Accepted, string Normalized, string? RejectionPath);
public sealed record CompileResult(string Status, IReadOnlyList<string> WrittenPaths, bool IndexCurrent, bool SourceIngested, string? QuarantinePath = null);
public sealed record CompileDecision(bool ShouldCompile, string Reason);
public sealed record Note(string Name, string Title, IReadOnlyList<string> Aliases, IReadOnlyList<string> Tags, IReadOnlyList<string> Sources, DateOnly Created, DateOnly Updated, string Body);
public sealed record SearchHit(string Name, double Score, string Text, string Source, DateTimeOffset Timestamp, bool Superseded = false);
public sealed record RetrieveResult(IReadOnlyList<SearchHit> Hits, string Output, int ExitCode = 0);
public sealed record ContextResult(string Text, IReadOnlyList<string> Sections, TimeSpan Elapsed);
public sealed record HealthItem(string Component, HealthLevel Level, string Code, string Key, string Detail, bool Stale = false);
public sealed record DoctorResult(IReadOnlyList<HealthItem> Items, double Coverage, double RejectionRate, int Pending, int ExitCode = 0);
public sealed record NotificationResult(bool ToastSent, bool ContextQueued, string Text);
public sealed record QuotaWindow(string Name, double? UsedPercent, DateTimeOffset? ResetsAt, DateTimeOffset? ObservedAt, string Status, string? Resolution = null);
public sealed record QuotaResult(IReadOnlyList<QuotaWindow> Windows, string Band, IReadOnlyList<HealthItem> Warnings);
public sealed record UsageRecord(string MessageId, string Owner, DateTimeOffset Timestamp, long InputTokens, long OutputTokens, long CacheReadTokens);
public sealed record UsageSummary(long InputTokens, long OutputTokens, long CacheReadTokens, IReadOnlyDictionary<string, long> CacheReadByOwner);
public sealed record StateStats(long Bytes, int FlushLogs, int HealthRows);
public sealed record HookStart(string SessionId, int ProcessId, DateTimeOffset Timestamp, bool Duplicate, string Context);
public sealed record HookState(string SessionId, int PromptCount, DateTimeOffset StartedAt);
public sealed record InstallResult(bool Success, IReadOnlyList<string> WrittenPaths, IReadOnlyList<string> Registrations, string? Error = null);
public sealed record CheckpointResult(bool Written, bool Verified, string? Error = null);
public sealed record BenchmarkResult(double EpisodicTop3, double RecallAt3, double RecallAt5, double HallucinationRate, IReadOnlyDictionary<string, double> Metrics);
public sealed record IngressRecord(string Id, DateTimeOffset ReceivedAt, string Status, string? StandardErrorPath);
public sealed record ReconciliationResult(IReadOnlyList<IngressRecord> Overdue, IReadOnlyList<IngressRecord> Completed);
public sealed record RetryRecord(string SessionId, int Attempts, DateTimeOffset NextAt, bool Parked, int Notifications);
public sealed record LockResult(bool Acquired, string Outcome, string MachineIdentity);
public sealed record VerifyResult(IReadOnlyList<string> Missing, IReadOnlyList<string> Extra, int ExitCode);
public sealed record PublicationResult(bool Atomic, bool RolledBack, bool SourcePending, IReadOnlyList<string> VisibleNotes);
public sealed record WaitResult(bool Completed, int Attempts, int ExitCode, string Error);
public sealed record FreshnessResult(bool ShouldProcess, int Skipped);

public interface IClock { DateTimeOffset Now { get; } }
public interface IProcessRunner { ProcessResult Run(ProcessRequest request, TimeSpan timeout); }
public interface INotifier { void Notify(string text); }
public interface IFileOperations { void Replace(string source, string destination); }
