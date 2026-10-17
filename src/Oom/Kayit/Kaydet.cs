using System.Text.Json;

namespace Oom.Contracts;

public static class Kaydet
{
    public static int Write(string vault, string sessionId, string transcript, string trigger, DateTimeOffset now, Redactor? redactor = null)
        => WriteResult(vault, sessionId, transcript, trigger, now, redactor).Blocks;

    internal sealed record Result(int Blocks = 0, string Outcome = "bos", int Sessions = 0, int Skipped = 0, int Errors = 0, Exception? Error = null,
        long BytesRead = 0, string ReadPath = "tam");

    internal static Result WriteResult(string vault, string sessionId, string transcript, string trigger, DateTimeOffset now, Redactor? redactor = null,
        bool useTail = true, Action<long, string>? onRead = null)
    {
        if (Excluded(transcript) || sessionId.StartsWith("beyin-flush-", StringComparison.OrdinalIgnoreCase)) return new();
        var codexPath = transcript.Replace('\\', '/').Contains("/.codex/", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(transcript).StartsWith("rollout-", StringComparison.OrdinalIgnoreCase);
        TranskriptKuyruk.Input input;
        try { input = TranskriptKuyruk.Read(transcript, useTail && !codexPath ? Imlec.TailCursor(vault, sessionId, now) : null); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(Outcome: "dosya-yok"); }
        var result = new Result(BytesRead: input.Bytes, ReadPath: input.Tail ? "bayt" : "tam");
        onRead?.Invoke(result.BytesRead, result.ReadPath);
        var jsonl = input.Text;
        var offsets = new Dictionary<string, long>(StringComparer.Ordinal);
        var records = TranskriptKuyruk.Lines(jsonl, input.StartByte, input.LineOffsets).Select(line =>
        {
            try
            {
                using var document = JsonDocument.Parse(line.Text);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return default;
                var id = root.TryGetProperty("payload", out var payload) ? HookPayload.Field(payload, "id") : null;
                id ??= HookPayload.Field(root, "id");
                if (id is not null) offsets.TryAdd(id, line.Offset);
                return root.Clone();
            }
            catch (JsonException) { return default; }
        }).Where(r => r.ValueKind == JsonValueKind.Object).ToArray();
        var codex = transcript.Replace('\\', '/').Contains("/.codex/", StringComparison.OrdinalIgnoreCase)
            || records.Any(r => HookPayload.Field(r, "type") is "session_meta" or "response_item" or "event_msg");
        if (codex && input.Tail)
        {
            var full = WriteResult(vault, sessionId, transcript, trigger, now, redactor, useTail: false, onRead);
            return full with { BytesRead = full.BytesRead + input.Bytes };
        }
        if (!codex) records = records.Where(r => (HookPayload.Field(r, "sessionId") ?? HookPayload.Field(r, "session_id")) == sessionId).ToArray();
        if (records.Any(r => r.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)) return result;
        if (records.Any(r => HookPayload.Field(r, "type") == "session_meta" && r.TryGetProperty("payload", out var p)
            && p.TryGetProperty("source", out var source) && (source.ValueKind == JsonValueKind.String && source.GetString() == "exec"
                || source.ValueKind == JsonValueKind.Object && source.TryGetProperty("subagent", out _)))) return result;
        Session session;
        IReadOnlySet<string>? answers = null;
        if (codex) session = CodexParser.Parse(jsonl, capture: true);
        else
        {
            var read = ClaudeTranscript.Read(jsonl, new FixedClock(DateTimeOffset.UnixEpoch), sessionId, input.StartIndex, input.StartByte, input.LineOffsets);
            answers = read.OwnerAnswers;
            offsets = read.LineOffsets;
            if (read.SessionId is null) return result with { Outcome = input.Tail ? "bos" : "kimlik-yok" };
            if (read.Turns.Count == 0 || !input.Tail && !read.Turns.Any(t => t.Kind == "text")) return result;
            session = new Session(sessionId, "claude", read.Turns.OrderBy(t => t.Index).ThenBy(t => t.Timestamp).ToArray(), read.Turns.Min(t => t.Timestamp));
        }
        if (session.Id != sessionId) throw new InvalidDataException("oturum kimliği transkript ile uyuşmuyor");
        if (session.Turns.Any(t => string.IsNullOrWhiteSpace(t.Uuid) || t.Uuid.Any(c => char.IsWhiteSpace(c) || "<>[]".Contains(c)))
            || sessionId.Any(c => char.IsWhiteSpace(c) || "<>[]".Contains(c))) throw new InvalidDataException("geçersiz mesaj kimliği");
        var project = input.Project ?? records.Select(r => HookPayload.Field(r, "cwd")
            ?? (HookPayload.Field(r, "type") == "session_meta" && r.TryGetProperty("payload", out var p) ? HookPayload.Field(p, "cwd") : null)).FirstOrDefault(p => p is not null) ?? "—";
        if (Excluded(project)) return result;
        var daily = Path.Combine(vault, "daily", now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".md");
        using var held = DailyFileLock.Acquire(daily);
        var pending = Imlec.Pending(vault, session, now, input.RemovedCursor);
        if (pending.Count == 0) return result;
        if (trigger == "mesaj" && pending.Count(t => t.Class is "u" or "a") < 10) return result with { Outcome = "bekliyor" };
        var existing = File.Exists(daily) ? File.ReadAllText(daily) : "---\ntype: daily\nkayit: v1\n---\n";
        long? lastByte = offsets.TryGetValue(pending[^1].Uuid, out var lastOffset) ? lastOffset : null;
        VaultFiles.WriteAtomic(daily, existing + (existing.EndsWith('\n') ? "\n" : "\n\n") + Blok.Compose(session, pending, project, now, redactor ?? new Redactor(), answers, lastByte), new VaultFileOperations());
        return result with { Blocks = 1, Outcome = "yazdi" };
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
        long bytesRead = 0;
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
                var lane = false;
                var codexPath = path.Replace('\\', '/').Contains("/.codex/", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase);
                // Claude identity is the stem; anchored Claude files need no reads.
                // Codex rollouts must resolve metadata before consulting the anchor map.
                if (codexPath || !anchors.ContainsKey(id))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var r = doc.RootElement;
                            if (HookPayload.Field(r, "type") == "session_meta" && r.TryGetProperty("payload", out var p))
                            {
                                lane = HookPayload.Field(p, "source") == "exec";
                                id = HookPayload.Field(p, "id") ?? HookPayload.Field(p, "session_id") ?? id;
                                break;
                            }
                            if (HookPayload.Field(r, "sessionId") is not null) break;
                            if (codexPath && HookPayload.Field(r, "session_id") is { } flatId) { id = flatId; break; }
                        }
                        catch (JsonException) { }
                    }
                    bytesRead += stream.Position;
                }
                if (lane || id == excludedId) continue;
                if (anchors.TryGetValue(id, out var anchor) && modified < anchor.UtcDateTime.AddSeconds(-60)) { skipped++; continue; }
                sessions++;
                var result = WriteResult(vault, id, path, "tara", now, onRead: (bytes, _) => bytesRead += bytes);
                blocks += result.Blocks;
            }
            catch (Exception error) { errors++; firstError ??= error; }
        }
        return new(blocks, errors > 0 ? "hata" : blocks > 0 ? "yazdi" : "bos", sessions, skipped, errors, firstError, bytesRead);
    }
}
