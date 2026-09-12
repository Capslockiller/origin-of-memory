using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Oom.Contracts;

public sealed record ContextOptions(
    string CompanionDir = "🔮 850-Companion",
    int CapChars = 16_000,
    string? PendingNotification = null);

public sealed class Context
{
    private const string TrimNote = "[not: indeks kırpıldı — oom doctor]";

    private static readonly string[] SectionNames =
    [
        "Bildirim", "Son Oturum", "Aktif Threadler", "Kurallar", "Düzeltmeler", "Son Journal",
        "Bilgi Tabanı — İndeks", "Bugünün Logu"
    ];

    private static readonly (string Section, string File, int Lines)[] CompanionFiles =
    [
        ("Son Oturum", "Last-Session.md", 49), ("Aktif Threadler", "Threads.md", 12),
        ("Kurallar", "Kurallar.md", 60), ("Düzeltmeler", "Duzeltmeler.md", 30),
        ("Son Journal", "Journal.md", 10)
    ];

    private static readonly ConcurrentDictionary<string, string> ContentDigests = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> Starts = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly ContextOptions _options;
    private readonly Dictionary<string, ContextResult> _cache = new(StringComparer.Ordinal);

    public Context(ContextOptions? options = null) => _options = options ?? new ContextOptions();

    public ContextResult Build(string vaultPath, DateTimeOffset now)
    {
        var key = $"{vaultPath}|{now:O}";
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var companion = CompanionPath(vaultPath);
        foreach (var warning in CompanionWarnings(vaultPath))
            Console.Error.WriteLine(warning);

        var builder = new StringBuilder();
        Append(builder, "[Bildirim]", _options.PendingNotification);
        foreach (var (section, file, lines) in CompanionFiles)
            Append(builder, Label(section), Head(companion is null ? null : Path.Combine(companion, file), lines, section));

        var text = builder.ToString();
        var log = DailyTail(vaultPath, now);
        var index = ReadAll(vaultPath, Path.Combine("knowledge", "index.md"));
        var closing = "Hafıza protokolü zorunludur.\n";
        var fixedCost = text.Length + closing.Length + "[Bilgi Tabanı — İndeks]\n".Length + "[Bugünün Logu]\n".Length;
        var room = _options.CapChars - fixedCost - TrimNote.Length - 2;
        var trimmed = false;
        (log, room, trimmed) = Fit(log, room, trimmed);
        (index, _, trimmed) = Fit(index, room, trimmed);

        var flexible = new StringBuilder();
        Append(flexible, "[Bilgi Tabanı — İndeks]", index);
        Append(flexible, "[Bugünün Logu]", log);
        text += flexible.ToString() + (trimmed ? TrimNote + "\n" : string.Empty) + closing;
        var result = new ContextResult(text, SectionNames, System.Diagnostics.Stopwatch.GetElapsedTime(started));
        lock (_cache)
            _cache[key] = result;

        return result;
    }

    public ContextResult Start(HookStart start, string vaultPath, string @event = "SessionStart")
    {
        ArgumentNullException.ThrowIfNull(start);
        var identity = $"{start.ProcessId}|{start.Timestamp:yyyy-MM-ddTHH:mm:ss}";
        if (start.Duplicate || !Starts.TryAdd($"{start.SessionId}|{@event}|{identity}", 0))
        {
            var minimal = new StringBuilder();
            Append(minimal, "[Bildirim]", _options.PendingNotification);
            return new ContextResult(minimal.ToString(), ["Bildirim"], TimeSpan.Zero);
        }

        return Build(vaultPath, start.Timestamp);
    }

    public IReadOnlyList<HealthItem> AuditCompanion(string vaultPath)
    {
        var items = new List<HealthItem>();
        var companion = CompanionPath(vaultPath);
        foreach (var (section, file, _) in CompanionFiles)
        {
            var path = companion is null ? null : Path.Combine(companion, file);
            if (path is null || !File.Exists(path))
            {
                items.Add(new HealthItem("context", HealthLevel.Info, "companion-missing", file, $"{section} dosyası yok"));
                continue;
            }

            var text = File.ReadAllText(path);
            var digest = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text)));
            var touched = File.GetLastWriteTimeUtc(path);
            var unchanged = ContentDigests.TryGetValue(path, out var previous) && string.Equals(previous, digest, StringComparison.Ordinal);
            ContentDigests[path] = digest;
            if (unchanged && touched > SystemClock.Instance.Now.UtcDateTime.AddDays(-1))
                items.Add(new HealthItem("context", HealthLevel.Warning, "companion-content-stale", file,
                    $"{section} dosyasına dokunulmuş ama içeriği değişmemiş"));
        }

        return items;
    }

    public IReadOnlyList<string> CompanionWarnings(string vaultPath)
    {
        var companion = CompanionPath(vaultPath);
        if (companion is null)
            return [];

        var warnings = new List<string>();
        foreach (var (section, file, lines) in CompanionFiles)
        {
            var marker = Marker(section);
            var path = Path.Combine(companion, file);
            if (marker.Length == 0 || !File.Exists(path) || Head(path, lines, section) is not null)
                continue;

            warnings.Add($"context: '{section}' boş — dosyada '{marker}' başlığı yok");
        }

        return warnings;
    }

    private static string Marker(string section) => section switch
    {
        "Son Oturum" => "## Session:",
        "Aktif Threadler" => "## Active",
        "Son Journal" => "## ",
        _ => string.Empty
    };

    private static (string? Body, int Room, bool Trimmed) Fit(string? body, int room, bool trimmed)
    {
        if (body is null)
            return (null, room, trimmed);
        if (body.Length <= room)
            return (body, room - body.Length, trimmed);

        return (room <= 0 ? null : body[..room], 0, true);
    }

    private static string Label(string section) => $"[Hafıza — {section}]";

    private string? CompanionPath(string vaultPath)
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
            return null;

        var path = Path.Combine(vaultPath, _options.CompanionDir);
        return Directory.Exists(path) ? path : null;
    }

    private static void Append(StringBuilder builder, string label, string? body)
    {
        builder.Append(label);
        if (!string.IsNullOrWhiteSpace(body))
            builder.Append('\n').Append(body.TrimEnd('\n'));

        builder.Append('\n');
    }

    private static string? Head(string? path, int maxLines, string section)
    {
        if (path is null || !File.Exists(path))
            return null;

        var lines = File.ReadLines(path).ToArray();
        var selected = section switch
        {
            "Son Oturum" => Block(lines, "## Session:", maxLines),
            "Aktif Threadler" => lines.SkipWhile(line => !line.StartsWith("## Active", StringComparison.Ordinal))
                .Where(line => line.StartsWith("### ", StringComparison.Ordinal) || line.Contains("**Status:**", StringComparison.Ordinal))
                .Take(maxLines).ToArray(),
            "Son Journal" => LastEntry(lines, maxLines),
            _ => lines.Take(maxLines).ToArray()
        };

        return selected.Length == 0 ? null : string.Join('\n', selected);
    }

    private static string[] Block(string[] lines, string marker, int maxLines)
    {
        var start = Array.FindIndex(lines, line => line.StartsWith(marker, StringComparison.Ordinal));
        return start < 0 ? [] : lines.Skip(start).Take(maxLines).ToArray();
    }

    private static string[] LastEntry(string[] lines, int maxLines)
    {
        var start = Array.FindLastIndex(lines, line => line.StartsWith("## ", StringComparison.Ordinal));
        return start < 0 ? [] : lines.Skip(start).Take(maxLines).ToArray();
    }

    private static string? ReadAll(string vaultPath, string relative)
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
            return null;

        var path = Path.Combine(vaultPath, relative);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string? DailyTail(string vaultPath, DateTimeOffset now)
    {
        for (var back = 0; back <= 1; back++)
        {
            var text = ReadAll(vaultPath, Path.Combine("daily", $"{now.AddDays(-back):yyyy-MM-dd}.md"));
            if (text is null)
                continue;

            var lines = text.Split('\n');
            return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - 25)));
        }

        return null;
    }

}
