using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Oom.Contracts;

public static class Durum
{
    public sealed record TriggerStat(string Trigger, int Count, int P50Ms, int P95Ms, int MaxMs, int Errors);
    public sealed record Result(bool IsRed, string Line, int? LastRecordMinutes, string LastRecordText, bool LastRecordRed, IReadOnlyList<string> UnrecordedIds, int RecentErrors, int ElapsedMs, int MaskedTurns = 0, int DailyFiles = 0, double DailyMegabytes = 0, int OldDailyFiles = 0, IReadOnlyList<TriggerStat>? Triggers = null, int SevenDayErrors = 0);

    public static Result Evaluate(string vault, OomSettings settings, DateTimeOffset now, string? excludedId = null, string? excludedPath = null, bool includeLong = false, int? elapsedOverrideMs = null)
    {
        var timer = Stopwatch.StartNew();
        var anchors = Imlec.LatestAnchors(vault, now);
        var age = anchors.Count > 0 ? TimeSpan.FromTicks(Math.Max(0, (now - anchors.Values.Max()).Ticks)) : (TimeSpan?)null;
        var lastText = age is null ? "yok" : age.Value.TotalMinutes < 60 ? $"{(int)age.Value.TotalMinutes} dk" : $"{(int)age.Value.TotalHours} sa";
        var (lastRed, unrecorded, logPath) = (age is null || age.Value >= TimeSpan.FromHours(24), FindUnrecorded(settings, now, anchors, excludedId, excludedPath), KaydetLog.PathFor(vault));
        var recentErrors = KaydetLog.RecentErrors(logPath, now);
        var (isRed, elapsedMs, mins) = (lastRed || unrecorded.Count > 0 || recentErrors > 0, elapsedOverrideMs ?? (int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds), age is null ? (int?)null : (int)age.Value.TotalMinutes);
        var line = FormatMainLine(isRed, lastRed, lastText, unrecorded, recentErrors, elapsedMs);
        if (!includeLong) return new(isRed, line, mins, lastText, lastRed, unrecorded, recentErrors, elapsedMs);
        var (masked, files, mb, oldFiles) = ReadDailyStats(vault, now);
        var (triggers, sevenDayErrors) = ReadLogStats(logPath, now);
        return new(isRed, line, mins, lastText, lastRed, unrecorded, recentErrors, elapsedMs, masked, files, mb, oldFiles, triggers, sevenDayErrors);
    }

    public static string FormatLong(Result r) => string.Join('\n',
        new[] { r.Line, $"maske: {r.MaskedTurns} tur", $"daily: {r.DailyFiles} dosya, {r.DailyMegabytes.ToString("0.0", CultureInfo.GetCultureInfo("tr-TR"))} MB, 12 aydan eski {r.OldDailyFiles}" }
            .Concat((r.Triggers ?? []).Select(t => $"kaydet {t.Trigger}: n={t.Count} p50={t.P50Ms} ms p95={t.P95Ms} ms max={t.MaxMs} ms hata={t.Errors}"))
            .Append($"kaydet hata: {r.SevenDayErrors}"));
    private static List<string> FindUnrecorded(OomSettings settings, DateTimeOffset now, Dictionary<string, DateTimeOffset> anchors, string? excludedId, string? excludedPath)
    {
        var (unrecorded, paths) = (new List<string>(), new List<string>());
        foreach (var root in settings.Sweep.Roots.Where(Directory.Exists))
            try { paths.AddRange(Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories)); } catch (Exception) { }
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var codex = path.Replace('\\', '/').Contains("/.codex/", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase);
            if (Kaydet.Excluded(path) || codex || stem == excludedId || anchors.ContainsKey(stem)
                || excludedPath is not null && Path.GetFullPath(path).Equals(Path.GetFullPath(excludedPath), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero).ToOffset(now.Offset);
                if (modified.Date == now.Date && now - modified > TimeSpan.FromMinutes(30)
                    && ClaudeTranscript.Read(File.ReadAllText(path), SystemClock.Instance, stem).Turns.Any(t => t.Class == "u")) unrecorded.Add(stem);
            }
            catch (Exception) { unrecorded.Add(stem); }
        }
        return [.. unrecorded.Distinct(StringComparer.Ordinal)];
    }
    private static string FormatMainLine(bool isRed, bool lastRed, string lastText, IReadOnlyList<string> unrecorded, int recentErrors, int elapsedMs)
    {
        var slow = elapsedMs > 500 ? $" (yavaş: {(elapsedMs / 1000.0).ToString("0.0", CultureInfo.GetCultureInfo("tr-TR"))} sn)" : "";
        if (!isRed) return $"[Durum] yeşil · son kayıt {lastText} · kayıtsız 0 · kaydet hatası 0" + slow;
        for (var take = Math.Min(3, unrecorded.Count); take >= 0; take--)
        {
            var parts = new List<string> { "[Durum] KIRMIZI" };
            if (lastRed) parts.Add($"son kayıt {lastText}");
            if (unrecorded.Count > 0)
            {
                var ids = string.Join(", ", unrecorded.Take(take).Select(id => id[..Math.Min(8, id.Length)]));
                parts.Add($"kayıtsız {unrecorded.Count}" + (take == 0 ? "" : unrecorded.Count > take ? $": {ids} +{unrecorded.Count - take}" : $": {ids}"));
            }
            if (recentErrors > 0) parts.Add($"kaydet: son 24 saatte {recentErrors} hata");
            var candidate = string.Join(" · ", parts) + slow;
            if (candidate.Length <= 160 || take == 0) return candidate.Length <= 160 ? candidate : candidate[..160];
        }
        return "[Durum] KIRMIZI";
    }
    private static (int Masked, int Files, double Mb, int Old) ReadDailyStats(string vault, DateTimeOffset now)
    {
        var masked = Enumerable.Range(0, 8).Select(d => Path.Combine(vault, "daily", now.AddDays(-d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md")).Where(File.Exists).Sum(p => File.ReadAllText(p).Split("[maske:tur]").Length - 1);
        var (files, cutoff) = (Directory.Exists(Path.Combine(vault, "daily")) ? Directory.GetFiles(Path.Combine(vault, "daily"), "*.md") : [], DateOnly.FromDateTime(now.Date).AddMonths(-12));
        var old = files.Count(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt < cutoff : File.GetLastWriteTimeUtc(f) < now.UtcDateTime.AddMonths(-12));
        return (masked, files.Length, files.Sum(f => new FileInfo(f).Length) / (1024.0 * 1024.0), old);
    }
    private static (IReadOnlyList<TriggerStat> Triggers, int TotalErrors) ReadLogStats(string path, DateTimeOffset now)
    {
        var (rows, dir) = (new List<(string Trigger, int Ms, bool Error)>(), Path.GetDirectoryName(path));
        foreach (var file in (dir is null ? [path] : new[] { Path.Combine(dir, "kaydet.1.jsonl"), path }).Where(File.Exists))
            try
            {
                using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                while (reader.ReadLine() is { } line)
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var r = doc.RootElement;
                        if (DateTimeOffset.TryParse(HookPayload.Field(r, "ts"), out var ts) && ts >= now.AddDays(-7) && ts <= now)
                            rows.Add((HookPayload.Field(r, "tetik") ?? "?", r.TryGetProperty("ms", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var v) ? v : 0, HookPayload.Field(r, "sonuc") == "hata"));
                    }
                    catch (JsonException) { }
            }
            catch (Exception) { }
        return (rows.GroupBy(r => r.Trigger, StringComparer.Ordinal).Select(g => { var s = g.Select(x => x.Ms).Order().ToArray(); return new TriggerStat(g.Key, s.Length, s[(int)Math.Ceiling(0.50 * s.Length) - 1], s[(int)Math.Ceiling(0.95 * s.Length) - 1], s[^1], g.Count(x => x.Error)); }).ToArray(), rows.Count(r => r.Error));
    }
}
