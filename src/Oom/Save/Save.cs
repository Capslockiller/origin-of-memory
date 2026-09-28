using System.Text;
using System.Text.Json;

namespace Oom.Contracts;

public sealed class Save
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Guards guards;
    private readonly Flush flush;

    public Save(Guards? guards = null, Flush? flush = null)
    {
        this.guards = guards ?? new Guards();
        this.flush = flush ?? new Flush();
    }

    /// <summary>
    /// Writes free text to the vault's daily file (SPEC-3.1.0.md F6-2, Cut: save's
    /// mandatory 3-field rule — `oom save` no longer requires "karar"/"düzeltme"/"devir"
    /// fields). The text passes through <see cref="Guards.Gate"/> once (inside
    /// <see cref="FormatDailyBlock"/>, which also does this for its other caller) for
    /// unicode folding and directive detection only — save is ungated free text (F6-2,
    /// SECURITY.md), so Direction.In here never refuses — and is then appended under a
    /// "### Kayıt (HH:mm)" heading, never written as-is.
    /// </summary>
    public CheckpointResult WriteToVault(string vault, string text, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vault);
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
            return new CheckpointResult(false, false, "Kayıt metni boş olamaz.");
        var written = AppendToDaily(vault, text, now);
        return new CheckpointResult(written, written, written ? null : "Kayıt günlük dosyaya yazılamadı.");
    }

    private bool AppendToDaily(string vault, string text, DateTimeOffset now)
    {
        try
        {
            var directory = Path.Combine(vault, "daily");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{now:yyyy-MM-dd}.md");
            var block = FormatDailyBlock(text, now);
            using var dailyLock = DailyFileLock.Acquire(path);
            var existing = File.Exists(path) ? File.ReadAllText(path, Utf8) : string.Empty;
            var separator = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : "\n";
            File.AppendAllText(path, separator + block, Utf8);
            return File.ReadAllText(path, Utf8).Contains(block.Trim(), StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public string FormatDailyBlock(string text, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var gated = guards.Gate(text, Direction.In, ComponentKind.Flush);
        return $"### Kayıt ({now:HH:mm})\n{gated.Text.Trim()}\n";
    }

    /// <summary>
    /// R36/#7: <paramref name="durableDirectory"/> is where the temp transcript this
    /// builds from <paramref name="json"/> is written when the flush does NOT finish
    /// terminally (Retry/Parked) — i.e. a retry_queue row now references it. Defaults to
    /// the OS temp directory (unchanged behaviour) when null, which is what every caller
    /// with no durable <see cref="State"/> configured (both real callers with VaultPaths
    /// cleared and every acceptance test that constructs a bare <see cref="Save"/>) still
    /// gets. The real CLI path (Program.Save.cs) passes the durable state directory so the
    /// file survives at least as long as the retry row that points at it.
    /// </summary>
    public FlushResult SaveSessionJson(string json, string? durableDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        Session session;
        try
        {
            session = JsonSerializer.Deserialize<Session>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new FormatException("Dış oturum nesnesi boş.");
        }
        catch (JsonException exception) { throw new FormatException("Dış oturum JSON sözleşmesine uymuyor.", exception); }
        if (string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(session.Source) || session.Turns is null)
            throw new FormatException("Dış oturum id, source ve turns alanlarını içermelidir.");

        var safeTurns = new List<Turn>();
        foreach (var turn in session.Turns)
        {
            var gated = guards.Gate(turn.Text, Direction.In, ComponentKind.Flush);
            safeTurns.Add(turn with { Text = gated.Text });
        }

        var tempRoot = string.IsNullOrEmpty(durableDirectory) ? Path.GetTempPath() : durableDirectory;
        Directory.CreateDirectory(tempRoot);
        var tempPath = Path.Combine(tempRoot, $"oom-save-{Guid.NewGuid():N}.jsonl");
        var lines = safeTurns.Select(turn => JsonSerializer.Serialize(new
        {
            session_id = session.Id,
            index = turn.Index,
            role = turn.Role,
            kind = turn.Kind,
            text = turn.Text,
            timestamp = turn.Timestamp
        }));
        File.WriteAllText(tempPath, string.Join('\n', lines), Utf8);
        FlushResult result;
        try
        {
            result = flush.FlushSession(session with { Turns = safeTurns }, tempPath, FlushReason.Ingest);
        }
        catch
        {
            // Never reached this far with a persisted reference to tempPath — safe to
            // always clean up on an exception, same as the previous unconditional delete.
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }

        // R36/#7: Retry/Parked is the ONLY outcome that writes a retry_queue row (and
        // sessions.transcript_path) pointing at tempPath — deleting it unconditionally
        // here left that row (and that session's transcript_path) referencing a file that
        // no longer existed the instant this call returned, so SweepRun.DrainQueue's
        // `!File.Exists(path)` guard skipped it FOREVER (permanently orphaned, one of the
        // 5-row-per-run ReadRetryQueue cap, so a handful of these could starve every other
        // real retry). Every other outcome (Ok/NoNewTurns/Refused/Unreadable/
        // MissingTranscript/Locked) never persists a reference to this path, so it is
        // always safe to delete then.
        if (result.Outcome is not (FlushOutcome.Retry or FlushOutcome.Parked) && File.Exists(tempPath))
            File.Delete(tempPath);

        return result;
    }
}
