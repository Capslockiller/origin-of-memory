using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Oom.Contracts;

/// <summary>SessionStart block settings (Spec 4.1 <c>context</c> block).</summary>
public sealed record ContextOptions(
    string CompanionDir = "🔮 850-Companion",
    int CapChars = 16_000,
    bool StatusLine = true,
    string? PendingNotification = null);

public sealed class Context
{
    private const string TrimNote = "[not: indeks kırpıldı — oom doctor]";

    // Fixed sections first, in this order; the two flexible ones are trimmed first (Spec 6.2).
    private static readonly string[] SectionNames =
    [
        "Bildirim", "Son Oturum", "Aktif Threadler", "Kurallar", "Düzeltmeler", "Son Journal",
        "Durum", "Bilgi Tabanı — İndeks", "Bugünün Logu"
    ];

    private static readonly (string Section, string File, int Lines)[] CompanionFiles =
    [
        ("Son Oturum", "Last-Session.md", 49), ("Aktif Threadler", "Threads.md", 12),
        ("Kurallar", "Kurallar.md", 60), ("Düzeltmeler", "Duzeltmeler.md", 30),
        ("Son Journal", "Journal.md", 10)
    ];

    private static readonly ConcurrentDictionary<string, string> ContentDigests = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly ContextOptions _options;
    private readonly Dictionary<string, ContextResult> _cache = new(StringComparer.Ordinal);

    public Context(ContextOptions? options = null) => _options = options ?? new ContextOptions();

    /// <summary>
    /// Builds the SessionStart block. The same session start inside the same second gets the very
    /// same block, so a second (helper) start costs nothing (Y-046, Y-077).
    /// </summary>
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
        // A companion file without its heading used to print an empty section and say nothing.
        // One stderr line per such section, and no behaviour change beyond it.
        foreach (var warning in CompanionWarnings(vaultPath))
            Console.Error.WriteLine(warning);

        var builder = new StringBuilder();
        Append(builder, "[Bildirim]", _options.PendingNotification);
        foreach (var (section, file, lines) in CompanionFiles)
            Append(builder, Label(section), Head(companion is null ? null : Path.Combine(companion, file), lines, section));

        Append(builder, "[Durum]", _options.StatusLine ? StatusText(vaultPath) : null);

        // The two flexible bodies are trimmed, never their labels: every section of the
        // Spec 7 contract stays present and in order even when the cap bites.
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

    /// <summary>
    /// The hand-written companion layer is audited by content, never by mtime: a file that was
    /// touched but not updated is reported stale (Y-050).
    /// </summary>
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

    /// <summary>
    /// Sections whose companion file is present but carries none of the headings <see cref="Head"/>
    /// looks for. Such a section used to print empty and silent.
    /// </summary>
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

    /// <summary>The heading <see cref="Head"/> needs for a section; empty when it reads from the top.</summary>
    private static string Marker(string section) => section switch
    {
        "Son Oturum" => "## Session:",
        "Aktif Threadler" => "## Active",
        "Son Journal" => "## ",
        _ => string.Empty
    };

    /// <summary>Gives one flexible body what is left of the budget and reports the rest.</summary>
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

    /// <summary>
    /// Quota percentage and the last seven days of calls, sampled by sweep into state.db. Nothing
    /// is measured here: the SessionStart hook may not spend seconds on a status line.
    /// </summary>
    private static string? StatusText(string vaultPath) => _statusLine;

    private static string? _statusLine;

    /// <summary>Sweep publishes the sampled status line; until then the section stays empty.</summary>
    public static void PublishStatusLine(string? line) => _statusLine = line;
}
