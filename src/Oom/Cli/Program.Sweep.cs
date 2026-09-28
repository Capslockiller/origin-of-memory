using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    private static int RunSweep(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        var dryRun = args.Contains("--dry-run");
        using State? state = dryRun ? OpenStateForReading() : OpenState();
        var report = new SweepRun(vault, settings, MakeFlush(vault, settings, state), state).Execute(dryRun);

        Console.WriteLine(dryRun ? report.Summary.Replace("tarama:", "tarama (kuru koşum):", StringComparison.Ordinal) : report.Summary);
        var outcomes = report.Result.Results
            .GroupBy(entry => FlushOutcomes.Wire(entry.Outcome))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}")
            .ToArray();
        if (outcomes.Length > 0)
            Console.WriteLine("sonuçlar: " + string.Join(' ', outcomes));
        foreach (var daily in report.Dailies)
            Console.WriteLine($"daily: {daily}");

        if (dryRun)
            return 0;

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        ReportIndex(MakeRetrieve(vault, settings, settings.Retrieve.Top).Build());
        Console.WriteLine($"indeks: {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");

        // R6/R12 (binding): `oom sweep` NEVER starts a compile, at any hour — the
        // eveningHour/minIntervalHours decision is removed outright, not merely gated.
        // Compile only runs via an explicit `oom compile`. Sweep just reports the honest
        // pending count so Master (or `oom doctor`) knows a compile is due.
        var pendingCount = Pending(vault, state).Count;
        Console.WriteLine(pendingCount > 0 ? $"bekleyen daily: {pendingCount}" : "bekleyen daily: 0");

        var health = new Doctor(null, moment => Snapshot(moment, vault, settings, state), null).Check(now);
        var loud = health.Items.Where(item => item.Level is not HealthLevel.Info).ToArray();
        Console.WriteLine($"doctor: kapsama {Doctor.CoverageText(health.Coverage)} · ret {health.RejectionRate:P0} · {loud.Length} uyarı");

        // R30/R36: same manual-CLI failure contract as `oom flush` — unreadable/retry/
        // parked/locked or a runner-incompatible smoke check (SweepRun.Execute already
        // stopped the run at the first one of those, per O11) is exit 2, never the success
        // code. Locked is included here now (#1); it is added to report.Result.Results
        // like any other outcome.
        //
        // R36/#4: report.Result.Results alone is not enough — a candidate whose Session
        // itself never parses (SweepRun.Execute's `session is null` branch) is stamped
        // 'unreadable' and reconciled into coverage but NEVER gets a FlushResult, so a
        // sweep whose every candidate failed that way left Results empty and badOutcome
        // false, exiting 0 for a run that covered nothing. The Total>0/Covered==0 fallback
        // catches exactly that case without changing the per-outcome check above, which
        // still catches a single bad session inside an otherwise-successful run.
        var badOutcome = report.Result.Results.Any(entry => entry.Outcome is FlushOutcome.Unreadable or FlushOutcome.MissingTranscript
            or FlushOutcome.Retry or FlushOutcome.Parked or FlushOutcome.Locked
            || (entry.Error is { } error && error.StartsWith(Runner.IncompatibleMarker, StringComparison.Ordinal)))
            || (report.Result.Total > 0 && report.Result.Covered == 0);
        return badOutcome ? ExitFailure : 0;
    }
}
