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
    private readonly State? _state;
    private readonly IClock _clock;
    private readonly string _temporaryProjectPrefix;

    public SweepRun(string vault, OomSettings settings, Flush flush, State? state = null, IClock? clock = null)
    {
        _vault = vault;
        _settings = settings;
        _flush = flush;
        _state = state;
        _clock = clock ?? SystemClock.Instance;

        _temporaryProjectPrefix = NonWord.Replace(Path.GetFullPath(Flush.TestRoot()).TrimEnd(Path.DirectorySeparatorChar), "-");
    }

    private bool SlicingMode => string.Equals(_settings.Flush.Mode, "dilim", StringComparison.Ordinal);

    public IReadOnlyList<SweepCandidate> Discover()
    {
        var candidates = new List<SweepCandidate>();
        foreach (var root in _settings.Sweep.Roots.Where(Directory.Exists))
        {
            foreach (var path in Enumerate(root))
            {
                if (_flush.IsMechanismTranscript(path, string.Empty) || IsMechanismProject(root, path))
                    continue;

                if (SlicingMode && SourceClassifier.IsSubagentTranscript(path))
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
        // Review finding: a session's coverage outcome used to be folded into `covered`/
        // `uncovered` as soon as it was seen, so a session reconciled twice in the same
        // run (once from a stale stamp in the loop below, once for real in DrainQueue)
        // was counted twice. Keying the final outcome by session id makes a later
        // reconcile of the same session REPLACE the earlier one instead of adding to it.
        var outcomeBySession = new Dictionary<string, string>(StringComparer.Ordinal);
        // R36/#6: a session Reconcile'd as 'skipped' (mechanism/too-short) is removed from
        // outcomeBySession by Reconcile below — tracked here too so Coverage() can tell
        // "legitimately excluded from the denominator" apart from "never reconciled at all
        // this run" (budget cap, an early smoke-failure break, or a duplicate path for a
        // session already handled), which must count as UNCOVERED, not vanish from both
        // the numerator and the denominator the way it silently did before.
        var skippedSessions = new HashSet<string>(StringComparer.Ordinal);
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var candidates = Discover();
        var skipped = 0;
        var changed = 0;
        var characters = 0;

        foreach (var candidate in candidates)
        {
            if (!handled.Add(candidate.SessionId))
                continue;

            var stamp = _state?.ReadStamp(candidate.Path);
            if (candidate.Source == "codex" && stamp?.Outcome == "unreadable")
                stamp = null;
            if (stamp is { } previous && previous.Mtime == Stamp(candidate.ModifiedAt) && previous.Size == candidate.Size)
            {
                skipped++;
                // R35: previous.Outcome may still carry a 3.0.x stamp value (ok/no-new-turns/
                // no-turns/unreadable/retry/parked/refused — Wire()'s old vocabulary, never
                // rewritten by this read) rather than the current stamp vocabulary
                // (ok/partial/unreadable/skipped) StampStatus below writes; normalise it
                // before reconciling so a pre-3.1.0 vault's meaning survives instead of
                // silently inverting from covered to uncovered forever.
                Reconcile(NormalizeStampOutcome(previous.Outcome), candidate.SessionId, outcomeBySession, skippedSessions);
                continue;
            }

            if (results.Count >= budget)
            {
                skipped++;
                outcomeBySession[candidate.SessionId] = "budget-skipped";
                continue;
            }

            changed++;
            var session = Read(candidate);
            if (session is null)
            {
                // R32/O8: an unreadable transcript must stay COUNTED as uncovered, not
                // vanish from this run's coverage entirely — the old code stamped the file
                // 'unreadable' but never reconciled it into outcomeBySession, so it fell out
                // of both the numerator and the denominator and doctor's 7-day window read
                // 100% on nothing but unreadable files.
                skipped++;
                Write(candidate, "unreadable", dryRun);
                Reconcile("unreadable", candidate.SessionId, outcomeBySession, skippedSessions);
                continue;
            }

            FlushResult result;
            bool isDrained;
            if (dryRun)
            {
                var (predicted, drained) = Plan(session, anchors);
                result = new FlushResult(predicted, 0, null, null);
                isDrained = drained;
            }
            else
            {
                result = _flush.FlushSession(session, candidate.Path, FlushReason.Sweep);
                isDrained = result.Outcome is not (FlushOutcome.Unreadable or FlushOutcome.MissingTranscript or FlushOutcome.Locked)
                    && _flush.IsDrained(session, result.Cursor - 1);
            }

            results.Add(result);
            characters += session.Turns.Sum(turn => turn.Text.Length);
            var wireOutcome = FlushOutcomes.Wire(result.Outcome);
            if (!dryRun)
            {
                _state?.RecordFlush(now, session.Id, "sweep", wireOutcome, result.Turns, characters, result.Summary is null ? "extractive" : "runner", result.Masked);
                // A locked candidate's file was never actually examined this run, so its
                // durable stamp must not be written — the next run has to retry it.
                if (result.Outcome is not FlushOutcome.Locked)
                    Write(candidate, StampStatus(result.Outcome, isDrained), dryRun);
            }

            // R36/#1/#3: unlike the stamp write above, Locked must still be reconciled —
            // StampStatus(Locked, false) is 'partial', so it lands in outcomeBySession and
            // counts as UNCOVERED (not written to disk, but not silently dropped from
            // coverage either). Excluding it here used to make an all-Locked run report
            // 0 kuyruk, 0/0 kapsandı — a 100%-looking measurement of nothing.
            Reconcile(StampStatus(result.Outcome, isDrained), session.Id, outcomeBySession, skippedSessions);

            // R30/O11: a runner-smoke failure means the RUNNER is broken, not this
            // session's content — stop the whole sweep here instead of burning a smoke
            // check (and the exit code it forces) against every other candidate too.
            if (!dryRun && result.Error is { } smokeError && smokeError.StartsWith(Runner.IncompatibleMarker, StringComparison.Ordinal))
                break;
        }

        var queued = dryRun
            ? PredictQueue(now, budget - results.Count, anchors, outcomeBySession, skippedSessions)
            : DrainQueue(now, budget - results.Count, results, outcomeBySession, skippedSessions);
        var dailies = results.Where(entry => entry.DailyPath is not null).Select(entry => entry.DailyPath!).Distinct().ToArray();
        var uncovered = outcomeBySession.Where(pair => pair.Value != "ok").Select(pair => pair.Key).ToList();
        var covered = outcomeBySession.Count - uncovered.Count;
        var total = outcomeBySession.Count;
        var window = Coverage(candidates, outcomeBySession, skippedSessions, now);
        var summary = $"tarama: {candidates.Count} dosya, {changed} değişmiş, {results.Count} oturum, {queued} kuyruk, {covered}/{total} kapsandı, {skipped} atlandı";

        if (!dryRun && _state is not null)
        {
            // R32/O10: a configured sweep root that does not exist on disk is reported —
            // Error when every configured root is missing (nothing could ever be swept),
            // Warning when at least one root is still reachable (a partial degradation).
            var missingRoots = _settings.Sweep.Roots.Where(root => !Directory.Exists(root)).ToArray();
            var allRootsMissing = missingRoots.Length > 0 && missingRoots.Length == _settings.Sweep.Roots.Count;
            var rootItems = missingRoots.Select(root => new HealthItem("sweep",
                allRootsMissing ? HealthLevel.Error : HealthLevel.Warning, "sweep-kok-eksik", root, $"tarama kökü bulunamadı: {root}"));

            _state.RecordFlush(now, "sweep", "sweep", "summary", results.Count, characters, "sweep");
            _state.WriteHealthConcurrently(
            [
                new HealthItem("sweep", HealthLevel.Info, "sweep-summary", "son koşum", summary),
                new HealthItem("sweep", HealthLevel.Info, "kapsama", "7g", $"Son 7 gün kapsama: {window.Covered}/{window.Total}"),
                .. rootItems
            ]);
            // F5-2: the same 7-day window health writes as text also lands numerically so
            // ReadCoverage() has a real row to read back (previously only the health string
            // line was written; RecordCoverage existed but nothing production called it).
            _state.RecordCoverage(now, window.Covered, window.Total, CoverageWindowDays);
            _state.SweepRetention(now);
        }

        return new SweepReport(candidates.Count, changed, results.Count, skipped,
            new SweepResult(total, covered, uncovered, skipped, results), dailies, summary);
    }

    /// <summary>R32: the sweep-stamp status vocabulary — distinct from
    /// <see cref="FlushOutcomes.Wire"/>'s flush_log outcome vocabulary. 'ok' only for a
    /// fully drained session (Opus O1/R29); a session that committed something but still
    /// has plannable ranges left, or that failed outright, is 'partial' (not covered);
    /// 'unreadable' is counted separately in the denominator; 'skipped' (too-short/no-op)
    /// is excluded from it entirely.</summary>
    private static string StampStatus(FlushOutcome outcome, bool drained) => outcome switch
    {
        FlushOutcome.Unreadable or FlushOutcome.MissingTranscript => "unreadable",
        FlushOutcome.NoTurns => "skipped",
        _ => drained ? "ok" : "partial"
    };

    /// <summary>R35 (third-family review finding #5, measured on the live state: 1,799
    /// legacy stamps — no-turns 1203, ok 277, no-new-turns 219, retry 82, bos 18): a
    /// sweep_stamps row written by 3.0.x carries the OLD Wire() outcome vocabulary
    /// (ok/no-new-turns/no-turns/unreadable/retry/parked/refused/bos), never the current
    /// stamp vocabulary (ok/partial/unreadable/skipped) <see cref="StampStatus"/> writes
    /// today, and the row is never rewritten just because it was read. Every reader of a
    /// STORED outcome (the fast unchanged-file path in <see cref="Execute"/> today) must
    /// normalise it through here first, or a pre-3.1.0 vault's stamps silently invert from
    /// covered to uncovered forever.</summary>
    // Third-family review of wave 7, #4: re-reading a queued transcript must never throw out
    // of the sweep (a malformed Codex rollout makes CodexParser throw); unreadable = not drained.
    private bool DrainedAfterRetry(string sessionId, string path, int cursor)
    {
        try
        {
            return _flush.ReadSessionFile(sessionId, path, SourceOf(path)) is { } session && _flush.IsDrained(session, cursor);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static string NormalizeStampOutcome(string outcome) => outcome switch
    {
        "ok" or "no-new-turns" or "bos" or "refused" => "ok",
        "no-turns" or "skipped" => "skipped",
        "retry" or "parked" or "locked" or "partial" => "partial",
        "unreadable" => "unreadable",
        _ => "partial"
    };

    /// <summary>R36/#6: covered/total over EVERY candidate whose file falls in the 7-day
    /// window — not just the sessions this run happened to reconcile. A candidate that was
    /// legitimately excluded (<paramref name="skippedSessions"/>: mechanism/too-short, R32)
    /// is left out of the denominator entirely; every OTHER recent candidate counts,
    /// whether or not <paramref name="outcomeBySession"/> has an entry for it — a budget
    /// cap, a runner-incompatible early `break`, or any other reason this run never
    /// touched it must show up as UNCOVERED, not silently shrink the denominator down to
    /// only the sessions actually reconciled (the same "vanishes from both numerator and
    /// denominator" bug R32/O8 already fixed for 'unreadable', now for the window as a
    /// whole).</summary>
    private static (int Total, int Covered) Coverage(IReadOnlyList<SweepCandidate> candidates, Dictionary<string, string> outcomeBySession, HashSet<string> skippedSessions, DateTimeOffset now)
    {
        var recentIds = candidates.Where(candidate => now - candidate.ModifiedAt <= TimeSpan.FromDays(CoverageWindowDays))
            .Select(candidate => candidate.SessionId).Distinct(StringComparer.Ordinal);

        var total = 0;
        var covered = 0;
        foreach (var sessionId in recentIds)
        {
            if (skippedSessions.Contains(sessionId))
                continue;

            total++;
            if (outcomeBySession.TryGetValue(sessionId, out var status) && status == "ok")
                covered++;
        }

        return (total, covered);
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

    private int DrainQueue(DateTimeOffset now, int budget, List<FlushResult> results, Dictionary<string, string> outcomeBySession, HashSet<string> skippedSessions)
    {
        if (_state is null || budget <= 0)
            return 0;

        var processed = 0;
        foreach (var row in _state.ReadRetryQueue(now, 5, budget))
        {
            if (row.TranscriptPath is not { Length: > 0 } path || !File.Exists(path))
                continue;

            var result = _flush.FlushSession(row.SessionId, path, FlushReason.Sweep);
            results.Add(result);
            processed++;
            var wireOutcome = FlushOutcomes.Wire(result.Outcome);
            _state.RecordFlush(now, row.SessionId, "retry", wireOutcome, null, null, "runner", result.Masked);
            var isDrained = result.Outcome is not (FlushOutcome.Unreadable or FlushOutcome.MissingTranscript or FlushOutcome.Locked)
                && DrainedAfterRetry(row.SessionId, path, result.Cursor - 1);
            if (result.Outcome is not FlushOutcome.Locked)
            {
                // Review finding: without rewriting the stamp here, a session that
                // succeeds on retry stayed stamped "retry" forever, so every later run
                // re-read that stale stamp in the main loop above and reconciled it all
                // over again — permanently miscounting it as uncovered even after it was
                // actually covered.
                var info = new FileInfo(path);
                _state.WriteStamp(path, Stamp(info.LastWriteTime), info.Length, StampStatus(result.Outcome, isDrained));
            }

            // R36/#1/#3: reconciled regardless of Locked, same reasoning as the main loop
            // above — only the durable file stamp is skipped for a Locked retry (the file
            // was never actually examined this attempt), never the in-run coverage entry.
            Reconcile(StampStatus(result.Outcome, isDrained), row.SessionId, outcomeBySession, skippedSessions);

            // R30/O11: same early-stop as the main loop — a broken runner must not burn a
            // smoke check against every other queued retry either.
            if (result.Error is { } smokeError && smokeError.StartsWith(Runner.IncompatibleMarker, StringComparison.Ordinal))
                break;
        }

        return processed;
    }

    /// <summary>Dry-run counterpart of <see cref="DrainQueue"/>: reads the due retry rows
    /// read-only (F4-3: a dry run must never write to state.db) and predicts each one's
    /// outcome the same way the main loop predicts an ordinary candidate in dry-run mode
    /// (<see cref="Plan"/>, no flush), instead of leaving retry-queued sessions out of the
    /// dry-run count entirely and under-reporting coverage relative to the next real run.</summary>
    private int PredictQueue(DateTimeOffset now, int budget, IReadOnlyDictionary<string, int> anchors, Dictionary<string, string> outcomeBySession, HashSet<string> skippedSessions)
    {
        if (_state is null || budget <= 0)
            return 0;

        var predicted = 0;
        foreach (var row in _state.ReadRetryQueue(now, 5, budget))
        {
            if (row.TranscriptPath is not { Length: > 0 } path || !File.Exists(path))
                continue;

            var info = new FileInfo(path);
            var session = Read(new SweepCandidate(path, row.SessionId, SourceOf(path), info.LastWriteTime, info.Length));
            if (session is null)
                continue;

            predicted++;
            var (predictedOutcome, drained) = Plan(session, anchors);
            Reconcile(StampStatus(predictedOutcome, drained), row.SessionId, outcomeBySession, skippedSessions);
        }

        return predicted;
    }

    private Session? Read(SweepCandidate candidate)
    {
        try
        {
            var session = _flush.ReadSessionFile(candidate.SessionId, candidate.Path, candidate.Source);

            return session is not null && _flush.IsMechanismTranscript(candidate.Path, string.Empty) ? null : session;
        }
        // F4-4 (review finding): a malformed Codex rollout makes CodexParser.Parse throw
        // ArgumentException/FormatException, not an I/O error; that must land as
        // "unreadable" (Write(candidate, "unreadable", ...) below), not crash the sweep.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Dry-run prediction only (F4-3: never writes). Mirrors one FlushSession
    /// iteration's own planning decision (MinTurns gate included) and additionally reports
    /// whether that range would leave the session fully drained (R29/R32), so a dry run's
    /// predicted coverage matches what a real sweep would record.</summary>
    private (FlushOutcome Outcome, bool Drained) Plan(Session session, IReadOnlyDictionary<string, int> anchors)
    {
        var cursor = anchors.GetValueOrDefault(session.Id, -1);
        var ranges = _flush.PlanRanges(session, cursor, Math.Max(1, _settings.Flush.SliceTurns), 15_000);
        if (ranges.Count == 0)
            return (FlushOutcome.NoNewTurns, true);

        if (ranges[0].Turns.Count < _settings.Sweep.MinTurns && ranges[0].Compact is null)
            return (FlushOutcome.NoTurns, false);

        return (FlushOutcome.Ok, _flush.IsDrained(session, ranges[0].End));
    }

    private void Write(SweepCandidate candidate, string outcome, bool dryRun)
    {
        if (!dryRun)
            _state?.WriteStamp(candidate.Path, Stamp(candidate.ModifiedAt), candidate.Size, outcome);
    }

    private static void Reconcile(string status, string sessionId, Dictionary<string, string> outcomeBySession, HashSet<string> skippedSessions)
    {
        // R32: 'skipped' (mechanism/too-short) is excluded from the coverage denominator
        // entirely. 'unreadable' stays IN it — Opus O8's bug was exactly this stamp being
        // dropped here too, so an unreadable file counted toward neither covered nor
        // total and doctor read 100% on a window made only of unreadable sessions.
        if (status == "skipped")
        {
            outcomeBySession.Remove(sessionId);
            // R36/#6: recorded separately so Coverage() can tell "legitimately excluded"
            // apart from "never reconciled this run" — both leave no entry in
            // outcomeBySession, but only the former should stay out of the denominator.
            skippedSessions.Add(sessionId);
            return;
        }

        outcomeBySession[sessionId] = status;
        // A session reconciled again after an earlier 'skipped' verdict (e.g. a retry-queue
        // row later resolved) is no longer skipped — supersede that mark instead of leaving
        // it stale in skippedSessions.
        skippedSessions.Remove(sessionId);
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
}
