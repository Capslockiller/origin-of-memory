using System.Text.Json;

namespace Oom.Contracts;

public static class Kaydet
{
    public static int Write(string vault, string sessionId, string transcript, string trigger, DateTimeOffset now, Redactor? redactor = null)
        => WriteResult(vault, sessionId, transcript, trigger, now, redactor).Blocks;

    internal sealed record Result(int Blocks = 0, string Outcome = "bos", int Sessions = 0, int Skipped = 0, int Errors = 0, Exception? Error = null);

    internal static Result WriteResult(string vault, string sessionId, string transcript, string trigger, DateTimeOffset now, Redactor? redactor = null)
    {
        if (Excluded(transcript) || sessionId.StartsWith("beyin-flush-", StringComparison.OrdinalIgnoreCase)) return new();
        string jsonl;
        try { jsonl = File.ReadAllText(transcript); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(Outcome: "dosya-yok"); }
        var records = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            try { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }
            catch (JsonException) { return default; }
        }).Where(r => r.ValueKind == JsonValueKind.Object).ToArray();
        var codex = transcript.Replace('\\', '/').Contains("/.codex/", StringComparison.OrdinalIgnoreCase)
            || records.Any(r => HookPayload.Field(r, "type") is "session_meta" or "response_item" or "event_msg");
        if (!codex) records = records.Where(r => (HookPayload.Field(r, "sessionId") ?? HookPayload.Field(r, "session_id")) == sessionId).ToArray();
        if (records.Any(r => r.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)) return new();
        if (records.Any(r => HookPayload.Field(r, "type") == "session_meta" && r.TryGetProperty("payload", out var p)
            && p.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object && source.TryGetProperty("subagent", out _))) return new();
        Session session;
        if (codex) session = CodexParser.Parse(jsonl, capture: true);
        else
        {
            var read = ClaudeTranscript.Read(jsonl, new FixedClock(DateTimeOffset.UnixEpoch), sessionId);
            if (read.SessionId is null) return new(Outcome: "kimlik-yok");
            if (!read.Turns.Any(t => t.Kind == "text")) return new();
            session = new Session(sessionId, "claude", read.Turns.OrderBy(t => t.Index).ThenBy(t => t.Timestamp).ToArray(), read.Turns.Min(t => t.Timestamp));
        }
        if (session.Id != sessionId) throw new InvalidDataException("oturum kimliği transkript ile uyuşmuyor");
        if (session.Turns.Any(t => string.IsNullOrWhiteSpace(t.Uuid) || t.Uuid.Any(c => char.IsWhiteSpace(c) || "<>[]".Contains(c)))
            || sessionId.Any(c => char.IsWhiteSpace(c) || "<>[]".Contains(c))) throw new InvalidDataException("geçersiz mesaj kimliği");
        var project = records.Select(r => HookPayload.Field(r, "cwd")
            ?? (HookPayload.Field(r, "type") == "session_meta" && r.TryGetProperty("payload", out var p) ? HookPayload.Field(p, "cwd") : null)).FirstOrDefault(p => p is not null) ?? "—";
        if (Excluded(project)) return new();
        var daily = Path.Combine(vault, "daily", now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".md");
        using var held = DailyFileLock.Acquire(daily);
        var pending = Imlec.Pending(vault, session, now);
        if (pending.Count == 0) return new();
        if (trigger == "mesaj" && pending.Count(t => t.Class is "u" or "a") < 10) return new(Outcome: "bekliyor");
        var existing = File.Exists(daily) ? File.ReadAllText(daily) : "---\ntype: daily\nkayit: v1\n---\n";
        VaultFiles.WriteAtomic(daily, existing + (existing.EndsWith('\n') ? "\n" : "\n\n") + Blok.Compose(session, pending, project, now, redactor ?? new Redactor()), new VaultFileOperations());
        return new(1, "yazdi");
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset Now => now; }

    internal static bool Excluded(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/.claude/worktrees/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/subagents/", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(normalized).StartsWith("agent-", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("beyin-flush-", StringComparison.OrdinalIgnoreCase);
    }

    internal static Result Scan(string vault, OomSettings settings, DateTimeOffset now, int days = 2, string? excludedId = null, string? excludedPath = null)
    {
        var sessions = 0; var blocks = 0; var errors = 0; var skipped = 0;
        Exception? firstError = null;
        var anchors = excludedId is null ? new Dictionary<string, DateTimeOffset>() : Imlec.LatestAnchors(vault, now);
        var paths = new List<string>();
        foreach (var root in settings.Sweep.Roots.Where(Directory.Exists))
        {
            try { paths.AddRange(Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories)); }
            catch (Exception error) { errors++; firstError ??= error; }
        }
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Excluded(path) || Path.GetFileNameWithoutExtension(path) == excludedId
                || excludedPath is not null && Path.GetFullPath(path).Equals(Path.GetFullPath(excludedPath), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var modified = File.GetLastWriteTimeUtc(path);
                if (modified < now.UtcDateTime.AddDays(-days)) continue;
                var id = Path.GetFileNameWithoutExtension(path);
                if (anchors.TryGetValue(id, out var anchor) && modified < anchor.UtcDateTime.AddSeconds(-60)) { skipped++; continue; }
                sessions++;
                foreach (var line in File.ReadLines(path))
                {
                    try { using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
                        if (HookPayload.Field(r, "type") == "session_meta" && r.TryGetProperty("payload", out var p))
                        { id = HookPayload.Field(p, "id") ?? HookPayload.Field(p, "session_id") ?? id; break; }
                        if (HookPayload.Field(r, "sessionId") is not null) break;
                        if (path.Replace('\\', '/').Contains("/.codex/", StringComparison.OrdinalIgnoreCase) && HookPayload.Field(r, "session_id") is { } flatId) { id = flatId; break; }
                    }
                    catch (JsonException) { }
                }
                if (id == excludedId) continue;
                blocks += Write(vault, id, path, "tara", now);
            }
            catch (Exception error) { errors++; firstError ??= error; }
        }
        return new(blocks, errors > 0 ? "hata" : blocks > 0 ? "yazdi" : "bos", sessions, skipped, errors, firstError);
    }
}
