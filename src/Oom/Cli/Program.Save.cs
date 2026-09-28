using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    private static int RunSave(string[] args, string vault)
    {
        if (Value(args, "--session-json") is { } sessionJson)
        {
            // Threaded through the vault (SPEC-3.1.0.md F6-2): a bare `new Flush()`
            // leaves FlushOptions.VaultPath null, so Flush.DailyPath() falls back to
            // a CWD-relative daily/ instead of the vault's — the exact bug this fixes.
            //
            // Review finding (L5e-redact-timeout, BLOCKING): this path used to build a
            // Flush with neither RejectionPath nor State, so a masking timeout on
            // `oom save --session-json` quarantined its output only in process memory —
            // no .reason file, no health row, no flush_log row — unlike `flush`/`sweep`,
            // which both go through MakeFlush(vault, settings, state). VaultPathKabul #4
            // and Y-107/Y-107b invoke RunSave in-process with VaultPaths cleared and rely
            // on the un-profiled `Runner` (extractive-summary fallback, never a real
            // subprocess), so the profiled runner below is taken only when VaultPaths is
            // configured. The durable state directory flush/sweep use is wired only when Dispatch has
            // already resolved VaultPaths to this exact `vault` — i.e. only on the real
            // CLI path. The acceptance-harness invocations above deliberately clear
            // VaultPaths (VaultPathKabul #4: `VaultPaths.UseVault(null)`) or never set it
            // (Y-107/Y-107b/Gate11), so EnsureStateDatabase() then returns null, no
            // state.db is opened, and today's in-memory/extractive behaviour those tests
            // pin is unchanged — the real %LOCALAPPDATA%\oom is never touched by them.
            var databasePath = string.Equals(VaultPaths.ReadVault(), vault, StringComparison.Ordinal)
                ? VaultPaths.EnsureStateDatabase()
                : null;
            using var state = databasePath is null ? null : new State(null, null, databasePath, StateAccess.ReadWrite);
            try
            {
                var json = File.ReadAllText(sessionJson, Utf8);
                var sessionId = TryReadSessionId(json);
                // R29 drain + S1 (final review): the same settings and profiled runner flush/sweep use,
                // so a multi-range drain reuses the cached B2 smoke check. The profiled runner is taken
                // only when this process is configured for a vault; the in-process acceptance tests
                // clear VaultPaths and must never reach a real claude subprocess.
                var settings = OomSettings.Load(vault);
                var options = new FlushOptions(MinTurns: settings.Sweep.MinTurns, VaultPath: vault,
                    RejectionPath: state?.WorkDirectory, Mode: settings.Flush.Mode, SliceTurns: settings.Flush.SliceTurns);
                var runner = VaultPaths.ReadVault() is not null ? MakeRunner(vault, settings) : new Runner(null, null, false);
                // R36/#7: when a durable state is configured, the temp transcript this
                // builds from `json` lives under the state directory (not the volatile OS
                // temp dir) for as long as a retry row keeps pointing at it — see
                // Save.SaveSessionJson's own doc comment.
                var imported = new Save(flush: new Flush(options, null, runner, null, state))
                    .SaveSessionJson(json, state?.WorkDirectory);

                if (state is not null && sessionId is not null)
                {
                    try
                    {
                        state.RecordFlush(Clock.Now, sessionId, "ingest", FlushOutcomes.Wire(imported.Outcome),
                            imported.Turns, imported.Summary?.Length, "runner", imported.Masked);
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException writeError)
                    {
                        HealthLedger.Record(new HealthItem("flush", HealthLevel.Error, "flush-log-yazilamadi",
                            sessionId, $"flush_log yazılamadı: {writeError.Message}"), Clock.Now);
                    }
                }

                if (imported.Outcome is FlushOutcome.Ok or FlushOutcome.NoNewTurns)
                {
                    Console.WriteLine($"kayıt: {imported.Outcome}");
                    return 0;
                }

                // Review finding: a masking timeout already writes exactly one stderr
                // line from inside Flush.FlushSession itself (the only layer that knows
                // it happened); reporting the same refusal again here produced two lines
                // for one failure. Every other refusal reason has no such line from
                // Flush, so this stays the only place that reports those.
                if (imported.Error != Flush.MaskTimeoutReason)
                {
                    Console.Error.WriteLine($"kayıt yazılmadı: {imported.Outcome}"
                        + (imported.Error is null ? string.Empty : " " + imported.Error));
                }
                return 1;
            }
            catch (Exception error) when (error is FormatException or ArgumentException)
            {
                Console.Error.WriteLine($"kayıt yazılmadı: {error.Message}");
                return 1;
            }
        }

        var text = Argument(args, 0) ?? ReadStandardInput();
        var written = new Save().WriteToVault(vault, text, Clock.Now);
        Console.WriteLine(written.Written ? "kayıt yazıldı" : $"kayıt yazılmadı: {written.Error}");
        return written.Written ? 0 : 1;
    }

    private static string? TryReadSessionId(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("id", out var value)
                && value.ValueKind is System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
