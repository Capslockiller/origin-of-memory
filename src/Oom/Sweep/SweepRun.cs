using System.Globalization;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

public sealed record SweepCandidate(string Path, string SessionId, string Source, DateTimeOffset ModifiedAt, long Size);

public sealed record SweepReport(int Files, int Changed, int Sessions, int Skipped, SweepResult Result, IReadOnlyList<string> Dailies, string Summary);

public sealed class SweepRun
{
    private const int CoverageWindowDays = 7;
    private static readonly Regex Anchor = new(@"<!-- session:(?<id>\S+) ts:\S+ turns:(?<start>\d+)-(?<end>\d+)", RegexOptions.Compiled);
    private static readonly Regex NonWord = new("[^A-Za-z0-9]", RegexOptions.Compiled);

    private readonly string _vault;
    private readonly OomSettings _settings;
    private readonly Flush _flush;
    private readonly Sweep _sweep;
    private readonly State? _state;
    private readonly IClock _clock;
    private readonly string _temporaryProjectPrefix;

    public SweepRun(string vault, OomSettings settings, Flush flush, State? state = null, IClock? clock = null)
    {
        _vault = vault;
        _settings = settings;
        _flush = flush;
        _sweep = new Sweep(flush, clock);
        _state = state;
        _clock = clock ?? SystemClock.Instance;

        _temporaryProjectPrefix = NonWord.Replace(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), "-");
    }

    public IReadOnlyList<SweepCandidate> Discover()
    {
        var candidates = new List<SweepCandidate>();
        foreach (var root in _settings.Sweep.Roots.Where(Directory.Exists))
        {
            foreach (var path in Enumerate(root))
            {
                if (_flush.IsMechanismTranscript(path, string.Empty) || IsMechanismProject(root, path))
                    continue;

                var info = new FileInfo(path);
                if (info.Length == 0)
                    continue;

                candidates.Add(new SweepCandidate(path, Path.GetFileNameWithoutExtension(path), SourceOf(root), info.LastWriteTime, info.Length));
            }
        }

        return [.. candidates.OrderByDescending(candidate => candidate.ModifiedAt)];
    }

    public SweepReport Execute(bool dryRun)
    {
        var now = _clock.Now;
        var anchors = ReadAnchors();
        if (!dryRun)
            _state?.SeedCursors(anchors);

        var budget = Math.Max(1, _settings.Sweep.MaxSessionsPerRun);
        var results = new List<FlushResult>();
        var uncovered = new List<string>();
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var candidates = Discover();
        var covered = 0;
        var skipped = 0;
        var changed = 0;
        var characters = 0;

        foreach (var candidate in candidates)
        {
            if (!handled.Add(candidate.SessionId))
                continue;

            var stamp = _state?.ReadStamp(candidate.Path);
            if (stamp is { } previous && previous.Mtime == Stamp(candidate.ModifiedAt) && previous.Size == candidate.Size)
            {
                skipped++;
                Reconcile(previous.Outcome, candidate.SessionId, ref covered, uncovered);
                continue;
            }

            if (stamp is not null && !_sweep.ShouldProcess(candidate.ModifiedAt, true, _settings.Sweep.SinceHours, now))
            {
                skipped++;
                Reconcile(stamp.Value.Outcome, candidate.SessionId, ref covered, uncovered);
                continue;
            }

            if (results.Count >= budget)
            {
                skipped++;
                uncovered.Add(candidate.SessionId);
                continue;
            }

            changed++;
            var session = Read(candidate);
            if (session is null)
            {
                skipped++;
                Write(candidate, "unreadable", dryRun);
                continue;
            }

            var result = dryRun
                ? new FlushResult(Plan(session, anchors), 0, null, null)
                : _flush.FlushSession(session, candidate.Path, FlushReason.Sweep);

            results.Add(result);
            characters += session.Turns.Sum(turn => turn.Text.Length);
            var outcome = Name(result.Outcome);
            if (!dryRun)
            {
                _state?.RecordFlush(now, session.Id, "sweep", outcome, session.Turns.Count, characters, result.Summary is null ? "extractive" : "runner");
                if (result.Outcome is not FlushOutcome.Locked)
                    Write(candidate, outcome, dryRun);
            }

            Reconcile(outcome, session.Id, ref covered, uncovered);
        }

        var queued = dryRun ? 0 : DrainQueue(now, budget - results.Count, results, ref covered, uncovered);
        var dailies = results.Where(entry => entry.DailyPath is not null).Select(entry => entry.DailyPath!).Distinct().ToArray();
        var total = covered + uncovered.Count;
        var window = Coverage(candidates, uncovered, now);
        var summary = $"tarama: {candidates.Count} dosya, {changed} değişmiş, {results.Count} oturum, {queued} kuyruk, {covered}/{total} kapsandı, {skipped} atlandı";

        if (!dryRun && _state is not null)
        {
            _state.RecordFlush(now, "sweep", "sweep", "summary", results.Count, characters, "sweep");
            _state.WriteHealthConcurrently([
                new HealthItem("sweep", HealthLevel.Info, "sweep-summary", "son koşum", summary),
                new HealthItem("sweep", HealthLevel.Info, "kapsama", "7g", $"Son 7 gün kapsama: {window.Covered}/{window.Total}")]);
            _state.SweepRetention(now);
        }

        return new SweepReport(candidates.Count, changed, results.Count, skipped,
            new SweepResult(total, covered, uncovered, skipped, results), dailies, summary);
    }

    private static (int Total, int Covered) Coverage(IReadOnlyList<SweepCandidate> candidates, IReadOnlyList<string> uncovered, DateTimeOffset now)
    {
        var recent = candidates.Where(candidate => now - candidate.ModifiedAt <= TimeSpan.FromDays(CoverageWindowDays))
            .Select(candidate => candidate.SessionId).ToHashSet(StringComparer.Ordinal);
        var missed = uncovered.Where(recent.Contains).Distinct(StringComparer.Ordinal).Count();
        return (recent.Count, recent.Count - missed);
    }

    public IReadOnlyDictionary<string, int> ReadAnchors()
    {
        var cursors = new Dictionary<string, int>(StringComparer.Ordinal);
        var directory = Path.Combine(_vault, "daily");
        if (!Directory.Exists(directory))
            return cursors;

        foreach (var path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
        {
            foreach (Match match in Anchor.Matches(File.ReadAllText(path)))
            {
                var id = match.Groups["id"].Value;
                var end = int.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture);
                if (!cursors.TryGetValue(id, out var known) || end > known)
                    cursors[id] = end;
            }
        }

        return cursors;
    }

    private int DrainQueue(DateTimeOffset now, int budget, List<FlushResult> results, ref int covered, List<string> uncovered)
    {
        if (_state is null || budget <= 0)
            return 0;

        var drained = 0;
        foreach (var row in _state.ReadRetryQueue(now, 5, budget))
        {
            if (row.TranscriptPath is not { Length: > 0 } path || !File.Exists(path))
                continue;

            var result = _flush.FlushSession(row.SessionId, path, FlushReason.Sweep);
            results.Add(result);
            drained++;
            _state.RecordFlush(now, row.SessionId, "retry", Name(result.Outcome), 0, 0, "runner");
            Reconcile(Name(result.Outcome), row.SessionId, ref covered, uncovered);
        }

        return drained;
    }

    private Session? Read(SweepCandidate candidate)
    {
        try
        {
            var session = _flush.ReadSessionFile(candidate.SessionId, candidate.Path, candidate.Source);

            return session is not null && _flush.IsMechanismTranscript(candidate.Path, string.Empty) ? null : session;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private FlushOutcome Plan(Session session, IReadOnlyDictionary<string, int> anchors)
    {
        var cursor = anchors.GetValueOrDefault(session.Id, -1);
        var ranges = _flush.PlanRanges(session, cursor, 30, 15_000);
        if (ranges.Count == 0)
            return FlushOutcome.NoNewTurns;

        return ranges[0].Turns.Count < _settings.Sweep.MinTurns ? FlushOutcome.NoTurns : FlushOutcome.Ok;
    }

    private void Write(SweepCandidate candidate, string outcome, bool dryRun)
    {
        if (!dryRun)
            _state?.WriteStamp(candidate.Path, Stamp(candidate.ModifiedAt), candidate.Size, outcome);
    }

    private static void Reconcile(string outcome, string sessionId, ref int covered, List<string> uncovered)
    {
        if (outcome is "no-turns" or "unreadable")
            return;

        if (outcome is "ok" or "no-new-turns" or "bos")
            covered++;
        else
            uncovered.Add(sessionId);
    }

    private IEnumerable<string> Enumerate(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 6
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private bool IsMechanismProject(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var project = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return project.StartsWith(_temporaryProjectPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string SourceOf(string root) => SourceClassifier.FromPath(root);

    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static string Name(FlushOutcome outcome) => outcome switch
    {
        FlushOutcome.Ok => "ok",
        FlushOutcome.NoTurns => "no-turns",
        FlushOutcome.NoNewTurns => "no-new-turns",
        FlushOutcome.Empty => "bos",
        FlushOutcome.Retry => "retry",
        FlushOutcome.Parked => "parked",
        FlushOutcome.MissingTranscript => "missing-transcript",
        FlushOutcome.Unreadable => "unreadable",
        _ => "locked"
    };
}
