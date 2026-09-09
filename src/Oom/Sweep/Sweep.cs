using System.Collections.Concurrent;

namespace Oom.Contracts;

/// <summary>One skipped sweep candidate, classified and counted (Y-015).</summary>
public sealed record SweepSkip(string Path, string Reason);

public sealed class Sweep
{
    private static readonly string[] TerminalOutcomes =
    [
        "ok", "no-turns", "no-new-turns", "bos", "parked", "missing-transcript", "unreadable", "locked"
    ];

    private readonly Flush _flush;
    private readonly IClock _clock;
    private readonly List<SweepSkip> _skips = [];

    public Sweep(Flush? flush = null, IClock? clock = null)
    {
        _flush = flush ?? new Flush();
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>Every skipped candidate of the last run, classified by reason.</summary>
    public IReadOnlyList<SweepSkip> Skips => _skips;

    /// <summary>
    /// The authoritative write path: every session is handed to the single write function and the
    /// run ends with a coverage reconciliation (Spec 6.3, Y-003).
    /// </summary>
    public SweepResult Run(IReadOnlyList<Session> sessions, SweepOptions options)
    {
        _skips.Clear();
        var results = new List<FlushResult>();
        var uncovered = new List<string>();
        var covered = 0;
        var now = _clock.Now;

        foreach (var session in sessions)
        {
            var last = session.Turns.Count > 0 ? session.Turns.Max(turn => turn.Timestamp) : session.StartedAt;
            var freshness = EvaluateFreshness(last, options.FreshSeconds, now);
            if (!freshness.ShouldProcess)
            {
                _skips.Add(new SweepSkip(session.Id, "settling"));
                continue;
            }

            if (!ShouldProcess(last, StampedSessions.ContainsKey(session.Id), options.SinceHours, now))
            {
                _skips.Add(new SweepSkip(session.Id, "stamped-and-old"));
                continue;
            }

            var result = _flush.FlushSession(session, TranscriptPathOf(session), FlushReason.Sweep);
            results.Add(result);
            if (result.Outcome is FlushOutcome.Locked)
            {
                // A locked session is skipped and is never stamped: the next run sees it again.
                _skips.Add(new SweepSkip(session.Id, "locked"));
                uncovered.Add(session.Id);
                continue;
            }

            StampedSessions.TryAdd(session.Id, 0);
            if (IsCovered(result.Outcome))
                covered++;
            else
                uncovered.Add(session.Id);
        }

        return new SweepResult(sessions.Count, covered, uncovered, _skips.Count, results);
    }

    /// <summary>Ingress without a terminal outcome inside the window is reported overdue (Y-004).</summary>
    public ReconciliationResult Reconcile(IReadOnlyList<IngressRecord> ingress, DateTimeOffset now)
    {
        var overdue = new List<IngressRecord>();
        var completed = new List<IngressRecord>();
        foreach (var record in ingress)
        {
            if (TerminalOutcomes.Contains(record.Status, StringComparer.OrdinalIgnoreCase))
                completed.Add(record);
            else if (now - record.ReceivedAt > TimeSpan.FromMinutes(15))
                overdue.Add(record);
        }

        return new ReconciliationResult(overdue, completed);
    }

    /// <summary>A vanished transcript is searched for by session id under the roots (Y-013).</summary>
    public Session RelocateMissingTranscript(string sessionId, string stalePath, IReadOnlyDictionary<string, string> roots)
    {
        foreach (var (path, content) in roots)
        {
            var named = Path.GetFileNameWithoutExtension(path).Contains(sessionId, StringComparison.OrdinalIgnoreCase);
            if (!named && !content.Contains($"\"{sessionId}\"", StringComparison.Ordinal))
                continue;

            var turns = _flush.ParseTranscript(content);
            var started = turns.Count > 0 ? turns.Min(turn => turn.Timestamp) : _clock.Now;
            return new Session(sessionId, SourceOf(path), turns, started);
        }

        return new Session(sessionId, SourceOf(stalePath), [], _clock.Now);
    }

    /// <summary>The age gate applies only to sources that already carry a stamp (Y-007).</summary>
    public bool ShouldProcess(DateTimeOffset modifiedAt, bool stamped, int sinceHours, DateTimeOffset now)
    {
        if (!stamped || sinceHours <= 0)
            return true;

        return modifiedAt >= now.AddHours(-sinceHours);
    }

    /// <summary>A zero window filters nothing; a positive window skips still-settling files (Y-015).</summary>
    public FreshnessResult EvaluateFreshness(DateTimeOffset modifiedAt, int freshSeconds, DateTimeOffset now)
    {
        if (freshSeconds <= 0)
            return new FreshnessResult(true, 0);

        return modifiedAt > now.AddSeconds(-freshSeconds)
            ? new FreshnessResult(false, 1)
            : new FreshnessResult(true, 0);
    }

    /// <summary>
    /// Task Scheduler registration for <c>schtasks /Create /XML</c>: no duration element anywhere,
    /// eight-hourly, run-if-missed, thirty-minute limit (Y-009, D4).
    /// </summary>
    public string BuildScheduledTaskXml(string executablePath) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Author>oom</Author><Description>OdenaOS bellek taraması</Description></RegistrationInfo>
          <Triggers>
            <CalendarTrigger>
              <StartBoundary>2026-01-01T06:00:00</StartBoundary>
              <Enabled>true</Enabled>
              <Repetition><Interval>PT8H</Interval></Repetition>
              <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
            </CalendarTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
          </Principals>
          <Settings>
            <StartWhenAvailable>true</StartWhenAvailable>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec><Command>{Escape(executablePath)}</Command><Arguments>sweep</Arguments></Exec>
          </Actions>
        </Task>
        """;

    /// <summary>Sessions stamped in this process; the durable store is <c>sweep_stamps</c>.</summary>
    private static readonly ConcurrentDictionary<string, byte> StampedSessions = new(StringComparer.Ordinal);

    private static bool IsCovered(FlushOutcome outcome) =>
        outcome is FlushOutcome.Ok or FlushOutcome.NoTurns or FlushOutcome.NoNewTurns or FlushOutcome.Empty;

    private static string TranscriptPathOf(Session session) => $"{session.Id}.jsonl";

    private static string SourceOf(string path) =>
        path.Contains("codex", StringComparison.OrdinalIgnoreCase) ? "codex" : "claude";

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
