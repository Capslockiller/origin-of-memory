namespace Oom.Contracts;

/// <summary>
/// F5-3: whether memory is being kept up, read once for `oom doctor` and the session-start
/// notice. The last real sweep is the latest numeric coverage row: only a real (not
/// dry-run) sweep writes one. The pending count is <see cref="CompileQueue.Pending"/>.
/// </summary>
public sealed record MemoryFreshness(CoverageReading? Coverage, DateTimeOffset? LastCompile, int Pending)
{
    public const int SweepRedHours = 48;
    public const int PendingRedCount = 7;

    /// Defect 3: on an UPGRADED (legacy) state.db there is no `coverage` row yet — the table
    /// only appeared in 3.1.0 — so LastSweep was null and the stale-sweep notice never showed
    /// (doctor said "son tarama: hiç" even though the vault HAD been swept). Fall back to the
    /// newest flush_log row a real sweep wrote: reason 'sweep' with a successful 'summary'
    /// outcome. A dry-run sweep writes neither, so this still means "a real sweep happened".
    internal DateTimeOffset? LastSweepFromFlushLog { get; init; }

    public DateTimeOffset? LastSweep => Coverage?.MeasuredAt ?? LastSweepFromFlushLog;

    /// A vault that was never swept is not overdue: there is no measurement to be stale.
    public bool SweepOverdue(DateTimeOffset now) =>
        LastSweep is { } sweep && now - sweep > TimeSpan.FromHours(SweepRedHours);

    public bool PendingOverdue => Pending > PendingRedCount;

    internal static MemoryFreshness Read(string vault, State? state, DateTimeOffset now)
    {
        var coverage = state?.ReadCoverage();
        return new MemoryFreshness(coverage, CompileQueue.LastCompile(state, now), CompileQueue.Pending(vault, state).Count)
        {
            LastSweepFromFlushLog = coverage is null ? state?.ReadLastSweep() : null
        };
    }
}
