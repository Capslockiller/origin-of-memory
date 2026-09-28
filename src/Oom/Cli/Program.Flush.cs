using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    /// <summary>R30: manual `oom flush`/`oom sweep` exit code for an outcome an automation
    /// must not read as success — unreadable, retry, parked or a runner-incompatible smoke
    /// check. A hook-stdin invocation never returns this (it always exits 0; see the early
    /// return below) — a hook must never disturb the session.</summary>
    internal const int ExitFailure = 2;

    private static int RunFlush(string[] args, string vault, OomSettings settings)
    {
        var detached = args.Contains("--detached");
        var hook = detached ? HookPayload.Empty : HookPayload.Read(ReadStandardInput());
        var session = Value(args, "--session") ?? hook.SessionId ?? string.Empty;
        var transcript = Value(args, "--transcript") ?? hook.TranscriptPath ?? string.Empty;
        var reason = Value(args, "--reason") ?? hook.Reason;

        if (!detached && hook.IsHook)
        {
            var child = DetachedProcess.Start(Executable(),
                ["flush", "--detached", "--vault", vault, "--session", session, "--transcript", transcript, "--reason", reason],
                Path.GetTempPath());
            Console.Error.WriteLine(child == 0 ? "flush: başlatılamadı" : $"flush ayrıldı: {child}");
            return 0;
        }

        using var state = OpenState();
        var result = MakeFlush(vault, settings, state).FlushSession(session, transcript, Reason(reason));
        var outcome = FlushOutcomes.Wire(result.Outcome);
        try
        {
            state.RecordFlush(Clock.Now, session, reason, outcome, result.Turns, result.Summary?.Length, "runner", result.Masked);
        }
        catch (Microsoft.Data.Sqlite.SqliteException writeError)
        {
            HealthLedger.Record(new HealthItem("flush", HealthLevel.Error, "flush-log-yazilamadi", session, $"flush_log yazılamadı: {writeError.Message}"), Clock.Now);
            throw;
        }
        if (Reason(reason) is FlushReason.SessionEnd)
            RecordReflectionDebt(state, vault, settings, session);

        // SPEC-3.1.0.md B2/S1: the summarizer subprocess fails loud on a runner it can't
        // trust (missing B2 flags, non-empty mcp_servers/plugins/tools in the smoke init
        // event). Every smoke verdict's RunResult.Error STARTS with Runner.IncompatibleMarker
        // and Flush passes it through unchanged; a summarizer failure whose stderr/stdout
        // merely mentions the phrase does not start with it, so it keeps the ordinary
        // retry/park path.
        var incompatible = result.Error is { } error && error.StartsWith(Runner.IncompatibleMarker, StringComparison.Ordinal);
        // R30/R36 (#1): unreadable/retry/parked/locked/runner-incompatible is a manual-CLI
        // failure, never the success exit code an automation would take at face value —
        // this line is reached on both the `--detached` path and a plain top-level call;
        // only the hook-stdin spawn above (which already returned 0) is exempt. Locked was
        // the newest outcome and had been left out of this list: a session-lock timeout
        // silently reported exit 0 with no durable trace (added by the `failing` block
        // below only once it is counted here).
        var failing = incompatible || result.Outcome is FlushOutcome.Unreadable or FlushOutcome.MissingTranscript
            or FlushOutcome.Retry or FlushOutcome.Parked or FlushOutcome.Locked;
        if (failing)
        {
            // O13: a durable trace survives even on --detached, where nothing else reads
            // this exit code back — a health row names the session, so a later `oom
            // doctor` shows it even though the hook that spawned this process already
            // returned before this ran.
            state.WriteHealthConcurrently([new HealthItem("flush", HealthLevel.Error, "flush-basarisiz", session,
                $"flush {outcome}: {result.Error ?? "neden kaydedilmedi"}")]);
        }

        // R36/#8: MaxRangesPerInvocation was reached with plannable turns still left —
        // Flush reports this the same as FlushOutcome.Ok (nothing here failed), so without
        // this marker a capped drain is indistinguishable from a genuine full drain for
        // every reader of this exit code. SweepRun already has its own signal for the same
        // situation (the 'partial' stamp, via IsDrained); the hook/manual flush path had
        // none until now.
        if (result.Outcome is FlushOutcome.Ok && !result.Drained)
        {
            state.WriteHealthConcurrently([new HealthItem("flush", HealthLevel.Info, "flush-kismi-bosaltma", session,
                "flush kapasiteyi doldurdu (MaxRangesPerInvocation); oturumda özetlenecek tur kaldı, sonraki çağrı devam eder")]);
        }

        if (incompatible)
        {
            Console.Error.WriteLine($"flush: {result.Error}");
            return ExitFailure;
        }

        Console.WriteLine($"flush: {outcome}{(result.DailyPath is null ? string.Empty : " → " + result.DailyPath)}");
        return failing ? ExitFailure : 0;
    }

    internal static void RecordReflectionDebt(State state, string vault, OomSettings settings, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var (prompts, firstSeen) = state.ReadSessionActivity(sessionId);
        if (!Nudge.OwesReflection(vault, settings.Context.CompanionDir, firstSeen, prompts, settings.ReflectionMinPrompts))
            return;

        state.WriteHealthConcurrently([new HealthItem("hafiza", HealthLevel.Warning, "yansima-borcu", sessionId, Nudge.ReflectionDebt)]);
    }

    private static Flush MakeFlush(string vault, OomSettings settings, State? state)
    {
        var options = new FlushOptions(MinTurns: settings.Sweep.MinTurns, VaultPath: vault, RejectionPath: state?.WorkDirectory, Mode: settings.Flush.Mode, SliceTurns: settings.Flush.SliceTurns);
        return new Flush(options, null, MakeRunner(vault, settings), null, state);
    }
}
