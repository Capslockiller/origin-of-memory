using System.Text.Json;

namespace Oom.Contracts;

public static class Kaydet
{
    public static int Write(string vault, string sessionId, string transcript, string trigger, DateTimeOffset now, Redactor? redactor = null)
    {
        if (Excluded(transcript) || sessionId.StartsWith("beyin-flush-", StringComparison.OrdinalIgnoreCase)) return 0;
        var jsonl = File.ReadAllText(transcript);
        var records = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            try { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }
            catch (JsonException) { return default; }
        }).Where(r => r.ValueKind == JsonValueKind.Object).ToArray();
        if (records.Any(r => r.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)) return 0;
        if (records.Any(r => HookPayload.Field(r, "type") == "session_meta" && r.TryGetProperty("payload", out var p)
            && p.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object && source.TryGetProperty("subagent", out _))) return 0;
        var codex = transcript.Replace('\\', '/').Contains("/.codex/", StringComparison.OrdinalIgnoreCase)
            || records.Any(r => HookPayload.Field(r, "type") is "session_meta" or "response_item" or "event_msg");
        var session = codex ? CodexParser.Parse(jsonl, capture: true) : ClaudeParser.Parse(jsonl, capture: true);
        if (session.Id != sessionId) throw new InvalidDataException("oturum kimliği transkript ile uyuşmuyor");
        if (session.Turns.Any(t => string.IsNullOrWhiteSpace(t.Uuid) || t.Uuid.Any(c => char.IsWhiteSpace(c) || "<>[]".Contains(c)))
            || sessionId.Any(c => char.IsWhiteSpace(c) || "<>[]".Contains(c))) throw new InvalidDataException("geçersiz mesaj kimliği");
        var project = records.Select(r => HookPayload.Field(r, "cwd")
            ?? (HookPayload.Field(r, "type") == "session_meta" && r.TryGetProperty("payload", out var p) ? HookPayload.Field(p, "cwd") : null)).FirstOrDefault(p => p is not null) ?? "—";
        if (Excluded(project)) return 0;
        var daily = Path.Combine(vault, "daily", now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".md");
        using var held = DailyFileLock.Acquire(daily);
        var pending = Imlec.Pending(vault, session, now);
        if (pending.Count == 0 || trigger == "mesaj" && pending.Count(t => t.Class is "u" or "a") < 10) return 0;
        var existing = File.Exists(daily) ? File.ReadAllText(daily) : "---\ntype: daily\nkayit: v1\n---\n";
        VaultFiles.WriteAtomic(daily, existing + (existing.EndsWith('\n') ? "\n" : "\n\n") + Blok.Compose(session, pending, project, now, redactor ?? new Redactor()), new VaultFileOperations());
        return 1;
    }

    internal static bool Excluded(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/.claude/worktrees/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/subagents/", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(normalized).StartsWith("agent-", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("beyin-flush-", StringComparison.OrdinalIgnoreCase);
    }

    internal static (int Sessions, int Blocks, int Errors) Scan(string vault, OomSettings settings, DateTimeOffset now)
    {
        var sessions = 0; var blocks = 0; var errors = 0;
        foreach (var path in settings.Sweep.Roots.Where(Directory.Exists).SelectMany(root => Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Excluded(path) || File.GetLastWriteTimeUtc(path) < now.UtcDateTime.AddHours(-48)) continue;
            sessions++;
            try
            {
                var jsonl = File.ReadAllText(path);
                var id = jsonl.Split('\n').Select(line =>
                {
                    try { using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
                        return HookPayload.Field(r, "sessionId") ?? HookPayload.Field(r, "session_id")
                            ?? (HookPayload.Field(r, "type") == "session_meta" && r.TryGetProperty("payload", out var p) ? HookPayload.Field(p, "id") ?? HookPayload.Field(p, "session_id") : null); }
                    catch (JsonException) { return null; }
                }).FirstOrDefault(id => id is not null) ?? throw new InvalidDataException("oturum kimliği yok");
                blocks += Write(vault, id, path, "tara", now);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or JsonException) { errors++; }
        }
        return (sessions, blocks, errors);
    }
}
