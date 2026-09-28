namespace Oom.Contracts;

public enum FlushReason { SessionEnd, PreCompact, Sweep, Ingest }
/// <summary>
/// Flush result. Written to flush_log only through <see cref="FlushOutcomes.Wire"/>.
/// Flush no longer produces <see cref="Empty"/> or <see cref="MissingTranscript"/>: a
/// FLUSH_BOS reply is <see cref="NoNewTurns"/> with 0 chars, and a missing or unparsable
/// transcript is <see cref="Unreadable"/> whatever the reason. The two members stay only
/// while SweepRun.Name and Sweep.IsCovered still name them; Wire folds them into the
/// eight values.
/// </summary>
public enum FlushOutcome { Ok, NoTurns, NoNewTurns, Empty, Retry, Parked, MissingTranscript, Unreadable, Locked, Refused }

/// <summary>SPEC-3.1.0.md F4-4 and R5: the single outcome vocabulary, in English lowercase.</summary>
public static class FlushOutcomes
{
    public static string Wire(FlushOutcome outcome) => outcome switch
    {
        FlushOutcome.Ok => "ok",
        FlushOutcome.NoTurns => "no-turns",
        FlushOutcome.NoNewTurns or FlushOutcome.Empty => "no-new-turns",
        FlushOutcome.Unreadable or FlushOutcome.MissingTranscript => "unreadable",
        FlushOutcome.Refused => "refused",
        FlushOutcome.Retry => "retry",
        FlushOutcome.Parked => "parked",
        FlushOutcome.Locked => "locked",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };
}

public enum ModelTier { Fast, Smart }
public enum ComponentKind { Flush, Compile, Context, Retrieve, Sweep, Doctor, Ingest, Install }
public enum Direction { In, Out, Egress }
public enum HealthLevel { Info, Warning, Error }
public enum UsageSourceKind { Unknown = 0, Estimate = 1, Measured = 2 }

public sealed record Turn(int Index, string Role, string Kind, string Text, DateTimeOffset Timestamp);
public sealed record Session(string Id, string Source, IReadOnlyList<Turn> Turns, DateTimeOffset StartedAt);
public sealed record TurnRange(int Start, int End, IReadOnlyList<Turn> Turns, int Characters, string? Compact = null);
/// <summary>
/// <paramref name="Drained"/> (R36/#8): true unless this Ok result stopped because
/// Flush.MaxRangesPerInvocation was reached with plannable turns still left — the one
/// case where FlushOutcome.Ok alone is indistinguishable from a genuine full drain.
/// Every other outcome leaves it at its default (true); nothing outside RunFlush's new
/// cap-hit health row currently reads it for a non-Ok result, so the default is never
/// misleading by omission the way FlushOutcome itself would be.
/// </summary>
public sealed record FlushResult(FlushOutcome Outcome, int Cursor, string? DailyPath, string? Summary, string? Error = null, int Turns = 0, int Masked = 0, bool Drained = true);
public sealed record SweepResult(int Total, int Covered, IReadOnlyList<string> UncoveredIds, int Skipped, IReadOnlyList<FlushResult> Results);
public sealed record GateResult(string Text, IReadOnlyList<string> Findings, bool Refused, Direction Direction);
public sealed record TokenUsage(long? UncachedInputTokens, long? OutputTokens, long? CacheReadTokens, long? CacheWriteTokens);
public sealed record RunResult(string Text, string? Error, string Backend, string Model, string UsageSource = "unknown", TokenUsage? Usage = null,
    string? OperationId = null, string? AttemptId = null, int? AttemptNumber = null)
{
    public UsageSourceKind UsageQuality => UsageSource switch { "measured" => UsageSourceKind.Measured, "estimate" => UsageSourceKind.Estimate, _ => UsageSourceKind.Unknown };
}
public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, string StandardInput);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool StandardInputClosed, bool TimedOut = false);
public sealed record SummaryValidation(bool Accepted, string Normalized, string? RejectionPath, bool ModelRefusal = false);
public sealed record CoverageReading(DateTimeOffset MeasuredAt, int Covered, int Total, int WindowDays);
public sealed record CompileResult(string Status, IReadOnlyList<string> WrittenPaths, bool IndexCurrent, bool SourceIngested, string? QuarantinePath = null, string? Reason = null);
public sealed record Note(string Name, string Title, IReadOnlyList<string> Aliases, IReadOnlyList<string> Tags, IReadOnlyList<string> Sources, DateOnly Created, DateOnly Updated, string Body, string? Type = null, string? Hub = null);
public sealed record SearchHit(string Name, double Score, string Text, string Source, DateTimeOffset Timestamp, bool Superseded = false);
public sealed record RetrieveResult(IReadOnlyList<SearchHit> Hits, string Output, int ExitCode = 0);
public sealed record ContextResult(string Text, IReadOnlyList<string> Sections, TimeSpan Elapsed);
public sealed record HealthItem(string Component, HealthLevel Level, string Code, string Key, string Detail, bool Stale = false);
public sealed record DoctorResult(IReadOnlyList<HealthItem> Items, double Coverage, double RejectionRate, int Pending, int ExitCode = 0);
public sealed record StateStats(long Bytes, int FlushLogs, int HealthRows);
public sealed record HookStart(string SessionId, int ProcessId, DateTimeOffset Timestamp, bool Duplicate, string Context);
public sealed record CheckpointResult(bool Written, bool Verified, string? Error = null);
public sealed record BenchmarkResult(double EpisodicTop3, double RecallAt3, double RecallAt5, double HallucinationRate, IReadOnlyDictionary<string, double> Metrics);
public sealed record RetryRecord(string SessionId, int Attempts, DateTimeOffset NextAt, bool Parked, int Notifications);
public sealed record VerifyResult(IReadOnlyList<string> Missing, IReadOnlyList<string> Extra, int ExitCode);
public sealed record PublicationResult(bool Atomic, bool RolledBack, bool SourcePending, IReadOnlyList<string> VisibleNotes);

public interface IClock { DateTimeOffset Now { get; } }
public interface IProcessRunner { ProcessResult Run(ProcessRequest request, TimeSpan timeout); }
public interface IFileOperations { void Replace(string source, string destination); }
