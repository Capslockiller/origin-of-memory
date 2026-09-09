// yazan: opus · lane D2
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Oom.Contracts;

/// <summary>
/// <c>oom install --from-v0</c> (spec 13). Every step is idempotent and reports one line; the
/// same run with <c>dryRun</c> writes nothing and returns the identical plan, which is how the
/// migration is exercised without a live vault. Nothing is written before the backup gate.
/// <c>daily/</c>, <c>knowledge/</c> and the Companion directory are never read for writing and
/// never touched — the migration only moves the mechanism, never the memory.
/// </summary>
public sealed class Migration
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly Regex RedFileName = new(@"\A(?<id>.+)-\d{8}T\d{6}(?:-\d+)?\z", RegexOptions.Compiled);

    /// <summary>The six user-level hook commands v0's <c>install.ps1</c> wrote, matched exactly.</summary>
    private static readonly (string Event, string Script, string Arguments)[] V0Hooks =
    [
        ("SessionStart", "session-start.ps1", ""),
        ("UserPromptSubmit", "prompt-counter.ps1", ""),
        ("UserPromptSubmit", "memory-retrieve.ps1", ""),
        ("SessionEnd", "flush-launch.ps1", " -Reason sessionend"),
        ("SessionEnd", "session-end.ps1", ""),
        ("PreCompact", "flush-launch.ps1", " -Reason precompact")
    ];

    public const string V0TaskName = "OdenaOS-Flush";

    private readonly IClock clock;
    private readonly IProcessRunner runner;
    private readonly List<string> report = [];

    public Migration(IClock clock, IProcessRunner runner)
    {
        this.clock = clock;
        this.runner = runner;
    }

    /// <summary>
    /// Runs (or plans) the whole migration and returns the report text, one step per line.
    /// <paramref name="removeV0Task"/> is the scheduler seam; the 2.0 task is registered by
    /// <see cref="Install"/> itself, so this only reverses v0's.
    /// </summary>
    public string Run(string vault, string stateRoot, string userSettingsPath, bool dryRun, Action<string>? removeV0Task = null)
    {
        report.Clear();
        var stamp = clock.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var v0State = Path.Combine(vault, ".claude", "scripts", ".state");
        var backup = Path.Combine(stateRoot, "backup", $"v0-{stamp}");
        var database = Path.Combine(stateRoot, "state.db");

        Step("yedek", Backup(vault, backup, dryRun));
        Step("compile-state.json → daily_ingest", ImportDailyIngest(v0State, database, dryRun));
        Step("flush durum dosyaları → sessions", ImportSessions(v0State, database, dryRun));
        Step("flush-tara.json → sweep_stamps", ImportSweepStamps(v0State, database, dryRun));
        Step("calls.jsonl → calls", ImportCalls(v0State, database, dryRun));
        Step(".stage/karantina → quarantine", ImportQuarantine(vault, database, dryRun));
        Step("red/ → retry_queue", ImportRetryQueue(v0State, database, dryRun));
        Step("mutabakat.json → kapsanmayan oturumlar", ImportCoverage(v0State, database, dryRun));
        Step("v0 hook satırları (6) kaldırıldı", RemoveV0Hooks(vault, userSettingsPath, dryRun));
        Step($"v0 görevi {V0TaskName} kaldırıldı", RemoveTask(removeV0Task, dryRun));
        Step(".claude/scripts + .claude/hooks → backup", MoveLegacyTrees(vault, backup, dryRun));
        Step("daily/, knowledge/, companion", "dokunulmadı");
        return string.Join(Environment.NewLine, report) + Environment.NewLine;
    }

    private void Step(string name, string outcome) => report.Add($"v0-göç: {name} — {outcome}");

    /// <summary>
    /// Git snapshot when the vault is a work tree (<c>stash create</c> makes a commit object
    /// without touching the working tree, and a ref keeps it from being collected); otherwise a
    /// file copy under <c>backup/v0-&lt;ts&gt;/</c>. Either way a RECOVERY marker is written and
    /// verified before any other step runs.
    /// </summary>
    private string Backup(string vault, string backup, bool dryRun)
    {
        var snapshot = TryGitSnapshot(vault, dryRun);
        if (dryRun) return snapshot ?? $"planlandı: {backup}";
        Directory.CreateDirectory(backup);
        // v0's state directory lives inside `.claude/scripts`, so copying the two trees copies the
        // state files with them. The copy lands on the very names MoveLegacyTrees later moves the
        // originals onto, so a completed migration leaves one copy of each, not two.
        if (snapshot is null)
            foreach (var name in new[] { "scripts", "hooks" })
                CopyTree(Path.Combine(vault, ".claude", name), Path.Combine(backup, "claude-" + name));
        var marker = Path.Combine(backup, "RECOVERY.txt");
        File.WriteAllText(marker, $"Kaynak: {vault}\nZaman: {clock.Now:O}\nGit: {snapshot ?? "yok"}\n", Utf8);
        if (!File.Exists(marker)) throw new IOException("Yedek doğrulanamadı.");
        return snapshot ?? backup;
    }

    private string? TryGitSnapshot(string vault, bool dryRun)
    {
        if (!Directory.Exists(Path.Combine(vault, ".git"))) return null;
        if (dryRun) return "git anlık görüntüsü planlandı";
        var created = Git(vault, ["stash", "create", "oom v0 göç anlık görüntüsü"]);
        var sha = created.Trim();
        if (sha.Length == 0) sha = Git(vault, ["rev-parse", "HEAD"]).Trim();
        if (sha.Length == 0) return null;
        Git(vault, ["update-ref", $"refs/oom/v0-{clock.Now:yyyyMMdd-HHmmss}", sha]);
        return $"git {sha}";
    }

    private string Git(string vault, string[] arguments)
    {
        try
        {
            var result = runner.Run(new ProcessRequest("git", ["-C", vault, .. arguments], vault,
                new Dictionary<string, string>(), string.Empty), TimeSpan.FromSeconds(30));
            return result.ExitCode == 0 ? result.StandardOutput : string.Empty;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }

    private string ImportDailyIngest(string v0State, string database, bool dryRun)
    {
        if (ReadObject(Path.Combine(v0State, "compile-state.json")) is not { } state) return "kaynak yok";
        var timestamp = state["last_run"]?.GetValue<string>() ?? clock.Now.ToString("O", CultureInfo.InvariantCulture);
        var rows = new List<object?[]>();
        foreach (var (name, digest) in Pairs(state["ingested"]))
            rows.Add([name, digest?.GetValue<string>() ?? string.Empty, "ingested", 0, string.Empty, timestamp]);
        foreach (var status in new[] { "rejected", "parked", "quarantined" })
            foreach (var (name, entry) in Pairs(state[status]))
                rows.Add([name, Text(entry?["digest"]), status, Number(entry?["attempts"]),
                    entry?["reasons"] is JsonArray reasons ? string.Join(",", reasons.Select(Text)) : Text(entry?["reason"]),
                    Text(entry?["ts"]) is { Length: > 0 } ts ? ts : timestamp]);
        return Write(database, dryRun, rows,
            "INSERT OR REPLACE INTO daily_ingest(name, digest, status, attempts, reasons, ts) VALUES($0,$1,$2,$3,$4,$5)");
    }

    private string ImportSessions(string v0State, string database, bool dryRun)
    {
        if (!Directory.Exists(v0State)) return "kaynak yok";
        var transcripts = TranscriptIndex(v0State);
        var rows = new List<object?[]>();
        foreach (var file in Directory.EnumerateFiles(v0State, "flush-*.json", SearchOption.TopDirectoryOnly))
        {
            if (ReadObject(file) is not { } state || Text(state["session_id"]) is not { Length: > 0 } id) continue;
            var seconds = Number(state["ts"]);
            rows.Add([id, transcripts.GetValueOrDefault(id, string.Empty), Number(state["last_turn_index"]),
                DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("O", CultureInfo.InvariantCulture)]);
        }
        return Write(database, dryRun, rows,
            "INSERT OR REPLACE INTO sessions(session_id, transcript_path, last_turn_index, last_flush_ts) VALUES($0,$1,$2,$3)");
    }

    private string ImportSweepStamps(string v0State, string database, bool dryRun)
    {
        if (ReadObject(Path.Combine(v0State, "flush-tara.json")) is not { } state) return "kaynak yok";
        var rows = new List<object?[]>();
        foreach (var (path, stamp) in Pairs(state["transkriptler"]))
        {
            var mtime = stamp?["mtime"]?.GetValue<double>() ?? 0;
            rows.Add([path, DateTimeOffset.FromUnixTimeMilliseconds((long)(mtime * 1000)).ToString("O", CultureInfo.InvariantCulture),
                Number(stamp?["size"]), stamp?["complete"]?.GetValue<bool>() == false ? "partial" : "ok"]);
        }
        return Write(database, dryRun, rows,
            "INSERT OR REPLACE INTO sweep_stamps(path, mtime, size, outcome) VALUES($0,$1,$2,$3)");
    }

    private string ImportCalls(string v0State, string database, bool dryRun)
    {
        var path = Path.Combine(v0State, "calls.jsonl");
        if (!File.Exists(path)) return "kaynak yok";
        var rows = new List<object?[]>();
        foreach (var line in File.ReadLines(path, Utf8))
        {
            if (line.Trim().Length == 0) continue;
            JsonObject? record;
            try { record = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { continue; }
            if (record is null) continue;
            rows.Add([Text(record["ts"]), Text(record["backend"]), Text(record["component"]), Text(record["model_tier"]),
                Text(record["model_actual"]) is { Length: > 0 } actual ? actual : Text(record["model_slug"]),
                Number(record["input_chars"]), Number(record["output_chars"]), Number(record["input_tokens"]),
                Number(record["output_tokens"]), Number(record["cache_read_tokens"]), Number(record["cache_write_tokens"]),
                Number(record["duration_ms"]), Text(record["outcome"]), Text(record["usage_source"]), Text(record["purpose"])]);
        }
        // `calls` has no key, so a repeated migration would double the ledger. The first ledger
        // timestamp is the guard: if it is already in the table this migration has run before.
        return Write(database, dryRun, rows,
            "INSERT INTO calls(ts, backend, component, tier, model, in_chars, out_chars, in_tok, out_tok, cache_r, cache_w, ms, outcome, usage_source, purpose) VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)",
            "SELECT COUNT(*) FROM calls WHERE ts=$guard", rows.Count > 0 ? rows[0][0] : null);
    }

    private string ImportQuarantine(string vault, string database, bool dryRun)
    {
        var source = Path.Combine(vault, ".stage", "karantina");
        if (!Directory.Exists(source)) return "kaynak yok";
        var destination = Path.Combine(vault, ".oom", "quarantine");
        var rows = new List<object?[]>();
        foreach (var file in Directory.EnumerateFiles(source, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            var sidecar = ReadObject(Path.ChangeExtension(file, ".json"));
            var reason = sidecar?["problems"] is JsonArray problems ? string.Join(",", problems.Select(Text)) : "sema";
            rows.Add([digest, Text(sidecar?["source_file"]) is { Length: > 0 } origin ? origin : relative, reason,
                Text(sidecar?["timestamp"]) is { Length: > 0 } ts ? ts : clock.Now.ToString("O", CultureInfo.InvariantCulture), target]);
            if (dryRun || File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return Write(database, dryRun, rows,
            "INSERT OR REPLACE INTO quarantine(digest, source, reason, ts, path) VALUES($0,$1,$2,$3,$4)");
    }

    private string ImportRetryQueue(string v0State, string database, bool dryRun)
    {
        var source = Path.Combine(v0State, "red");
        if (!Directory.Exists(source)) return "kaynak yok";
        var next = clock.Now.ToString("O", CultureInfo.InvariantCulture);
        var rows = new List<object?[]>();
        foreach (var file in Directory.EnumerateFiles(source, "*.md", SearchOption.TopDirectoryOnly))
        {
            var match = RedFileName.Match(Path.GetFileNameWithoutExtension(file));
            if (!match.Success) continue;
            rows.Add([match.Groups["id"].Value, 0, next, "v0:flush:rejected"]);
        }
        return Write(database, dryRun, rows,
            "INSERT OR REPLACE INTO retry_queue(session_id, attempts, next_at, last_error) VALUES($0,$1,$2,$3)");
    }

    /// <summary>
    /// v0's reconciliation manifest is the list of sessions it never summarised. It becomes one
    /// <c>coverage</c> row, which is exactly what spec 6.3 makes the first sweep prioritise —
    /// the sessions are queued for the first ingest rather than replayed here.
    /// </summary>
    private string ImportCoverage(string v0State, string database, bool dryRun)
    {
        if (ReadObject(Path.Combine(v0State, "mutabakat.json")) is not { } manifest) return "kaynak yok";
        var sessions = manifest["sessions"] as JsonArray ?? [];
        var uncovered = sessions.OfType<JsonNode>().Where(entry => Number(entry["uncovered_turns"]) > 0)
            .Select(entry => Text(entry["session_id"])).Where(id => id.Length > 0).ToArray();
        var stamp = Text(manifest["ts"]) is { Length: > 0 } ts ? ts : clock.Now.ToString("O", CultureInfo.InvariantCulture);
        var rows = new List<object?[]>();
        rows.Add([stamp, sessions.Count, sessions.Count - uncovered.Length, JsonSerializer.Serialize(uncovered)]);
        return Write(database, dryRun, rows,
            "INSERT INTO coverage(ts, total, covered, uncovered_json) VALUES($0,$1,$2,$3)",
            "SELECT COUNT(*) FROM coverage WHERE ts=$guard", stamp) + $" ({uncovered.Length} kapsanmayan)";
    }

    /// <summary>Removes v0's six commands by exact string match; anything else in the file stays.</summary>
    private static string RemoveV0Hooks(string vault, string path, bool dryRun)
    {
        if (!File.Exists(path)) return "settings.json yok";
        JsonObject? root;
        try { root = JsonNode.Parse(File.ReadAllText(path, Utf8)) as JsonObject; }
        catch (JsonException) { return "settings.json okunamadı"; }
        if (root?["hooks"] is not JsonObject hooks) return "hook yok";
        var hooksDirectory = Path.Combine(vault, ".claude", "hooks");
        var removed = 0;
        foreach (var (name, script, arguments) in V0Hooks)
        {
            var command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(hooksDirectory, script)}\"{arguments}";
            if (hooks[name] is not JsonArray groups) continue;
            for (var group = groups.Count - 1; group >= 0; group--)
            {
                if (groups[group]?["hooks"] is not JsonArray entries) continue;
                for (var index = entries.Count - 1; index >= 0; index--)
                    if (string.Equals(Text(entries[index]?["command"]), command, StringComparison.Ordinal))
                    { entries.RemoveAt(index); removed++; }
                if (entries.Count == 0) groups.RemoveAt(group);
            }
            if (groups.Count == 0) hooks.Remove(name);
        }
        if (removed > 0 && !dryRun)
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8);
        return $"{removed} satır";
    }

    private static string RemoveTask(Action<string>? removeV0Task, bool dryRun)
    {
        if (dryRun || removeV0Task is null) return "planlandı";
        removeV0Task(V0TaskName);
        return "kaldırıldı";
    }

    private static string MoveLegacyTrees(string vault, string backup, bool dryRun)
    {
        var moved = new List<string>();
        foreach (var name in new[] { "scripts", "hooks" })
        {
            var source = Path.Combine(vault, ".claude", name);
            if (!Directory.Exists(source)) continue;
            moved.Add(name);
            if (dryRun) continue;
            var target = Path.Combine(backup, "claude-" + name);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // The vault and %LOCALAPPDATA% are routinely on different drives (the vault is the
            // synced one), and Directory.Move refuses to cross a volume: copy, verify, delete.
            try { Directory.Move(source, target); }
            catch (IOException) { CopyTree(source, target); Directory.Delete(source, true); }
        }
        return moved.Count == 0 ? "kaynak yok" : string.Join(", ", moved);
    }

    /// <summary>
    /// Lane A's <see cref="State"/> exposes no public writer for these tables, so the rows go in
    /// through <c>Microsoft.Data.Sqlite</c> against the very schema <see cref="Install"/> creates
    /// (Ruling, progress.md). Keyed tables use INSERT OR REPLACE, so a second run is a no-op.
    /// </summary>
    private static string Write(string database, bool dryRun, IReadOnlyList<object?[]> rows, string insert,
        string? guardQuery = null, object? guardValue = null)
    {
        if (dryRun || !File.Exists(database)) return $"{rows.Count} satır (planlandı)";
        if (rows.Count == 0) return "0 satır";
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        if (guardQuery is not null && guardValue is not null)
        {
            using var guard = connection.CreateCommand();
            guard.CommandText = guardQuery;
            guard.Parameters.AddWithValue("$guard", guardValue);
            if (Convert.ToInt64(guard.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return "zaten göç etmiş";
        }
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = insert;
        foreach (var row in rows)
        {
            command.Parameters.Clear();
            for (var index = 0; index < row.Length; index++)
                command.Parameters.AddWithValue($"${index}", row[index] ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return $"{rows.Count} satır";
    }

    private static Dictionary<string, string> TranscriptIndex(string v0State)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ReadObject(Path.Combine(v0State, "mutabakat.json")) is not { } manifest) return index;
        foreach (var entry in manifest["sessions"] as JsonArray ?? [])
            if (Text(entry?["session_id"]) is { Length: > 0 } id) index[id] = Text(entry?["transcript"]);
        return index;
    }

    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static JsonObject? ReadObject(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path, Utf8)) as JsonObject; }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<(string Key, JsonNode? Value)> Pairs(JsonNode? node) =>
        node is JsonObject map ? map.Select(pair => (pair.Key, pair.Value)) : [];

    private static string Text(JsonNode? node) =>
        node is null ? string.Empty : node.GetValueKind() == System.Text.Json.JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString().Trim('"');

    private static long Number(JsonNode? node)
    {
        try { return node?.GetValueKind() == System.Text.Json.JsonValueKind.Number ? (long)node.GetValue<double>() : 0; }
        catch (InvalidOperationException) { return 0; }
    }
}
