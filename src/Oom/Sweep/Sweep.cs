using System.Collections.Concurrent;

namespace Oom.Contracts;

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

    public IReadOnlyList<SweepSkip> Skips => _skips;

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

    public bool ShouldProcess(DateTimeOffset modifiedAt, bool stamped, int sinceHours, DateTimeOffset now)
    {
        if (!stamped || sinceHours <= 0)
            return true;

        return modifiedAt >= now.AddHours(-sinceHours);
    }

    public FreshnessResult EvaluateFreshness(DateTimeOffset modifiedAt, int freshSeconds, DateTimeOffset now)
    {
        if (freshSeconds <= 0)
            return new FreshnessResult(true, 0);

        return modifiedAt > now.AddSeconds(-freshSeconds)
            ? new FreshnessResult(false, 1)
            : new FreshnessResult(true, 0);
    }

    private static readonly ConcurrentDictionary<string, byte> StampedSessions = new(StringComparer.Ordinal);

    private static bool IsCovered(FlushOutcome outcome) =>
        outcome is FlushOutcome.Ok or FlushOutcome.NoTurns or FlushOutcome.NoNewTurns or FlushOutcome.Empty;

    private static string TranscriptPathOf(Session session) => $"{session.Id}.jsonl";

    private static string SourceOf(string path) => SourceClassifier.FromPath(path);
}
