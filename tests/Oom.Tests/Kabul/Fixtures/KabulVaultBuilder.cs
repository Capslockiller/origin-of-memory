using System.Text;
using Oom.Contracts;

namespace Oom.Tests.Kabul.Fixtures;

/// <summary>
/// Sizes for the synthetic vault <see cref="KabulVaultBuilder"/> builds. Every knob a
/// later acceptance lane might want to vary (to probe a threshold, an edge case, or a
/// truncation boundary) is a parameter here instead of a baked-in constant.
/// </summary>
public sealed record KabulVaultSizes(
    int LastSessionChars = 9_700,
    int ThreadsChars = 60_000,
    int ActiveThreadCount = 11,
    int LongestStatusChars = 3_711,
    int KurallarChars = 2_200,
    int DuzeltmelerChars = 1_500,
    int DailyLineCount = 127,
    int KnowledgeIndexChars = 1_700,
    int CapChars = 16_000);

/// <summary>
/// Builds a SYNTHETIC vault (no real personal content, no real names, no real secrets —
/// this repo is public) that mirrors the real SHAPE and SIZES of the live hand layer
/// described in SPEC-3.1.0.md F1/F7:
///   - 🔮 850-Companion/Last-Session.md ≈ 9.7 KB with a "## Session:" block
///   - Threads.md ≈ 60 KB, "## Active Threads", N active threads with "**Status:**"
///     lines (one up to KabulVaultSizes.LongestStatusChars, one thread using
///     "**Durum:**" instead), "## Closed Threads"
///   - Journal.md in MIXED order: newest entry at top, older entries appended below,
///     three entries sharing one date, one heading using the "YYYY-MM-DD/DD" range form
///   - Kurallar.md ≈ 2.2 KB, Duzeltmeler.md ≈ 1.5 KB
///   - daily/&lt;today&gt;.md with ~127 lines
///   - knowledge/index.md ≈ 1.7 KB, a few knowledge/concepts/*.md
///   - .oom/oom.json with context.capChars = 16000 (the live, untouched value)
/// </summary>
public static class KabulVaultBuilder
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>The companion directory name Context.cs defaults to — read from ContextOptions
    /// itself rather than duplicated here, so this fixture never drifts from the real default.</summary>
    public static readonly string CompanionDir = new ContextOptions().CompanionDir;

    /// <summary>A fixed "today" fixtures are built against, so daily/&lt;today&gt;.md and the
    /// Journal's date arithmetic are deterministic across machines and days. Pair with
    /// KabulHarness.Run(..., fakeNow: KabulVaultBuilder.DefaultToday) so the exe agrees.</summary>
    public static readonly DateTimeOffset DefaultToday = new(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(3));

    private static readonly (string Slug, string Title)[] ConceptSeeds =
    [
        ("kabul-sentetik-kavram-01", "Kabul harness sentetik kavram 01"),
        ("kabul-sentetik-kavram-02", "Kabul harness sentetik kavram 02"),
        ("kabul-sentetik-kavram-03", "Kabul harness sentetik kavram 03"),
    ];

    /// <summary>Builds the vault under <paramref name="root"/>\vault and returns its path.</summary>
    public static string Build(string root, KabulVaultSizes? sizes = null, DateTimeOffset? today = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var effectiveSizes = sizes ?? new KabulVaultSizes();
        var effectiveToday = today ?? DefaultToday;

        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(vault);

        var companion = Path.Combine(vault, CompanionDir);
        Directory.CreateDirectory(companion);

        WriteLastSession(companion, effectiveToday, effectiveSizes.LastSessionChars);
        WriteThreads(companion, effectiveSizes);
        WriteJournal(companion, effectiveToday);
        WriteKurallar(companion, effectiveSizes.KurallarChars);
        WriteDuzeltmeler(companion, effectiveSizes.DuzeltmelerChars);
        WriteDaily(vault, effectiveToday, effectiveSizes.DailyLineCount);
        WriteKnowledge(vault, effectiveSizes.KnowledgeIndexChars);
        WriteSettings(vault, effectiveSizes.CapChars);

        return vault;
    }

    private static void WriteLastSession(string companion, DateTimeOffset today, int targetChars)
    {
        var header = $"## Session: {today:yyyy-MM-dd HH:mm} — sentetik oturum\n\n";
        var body = "Bu oturumda kabul kancası (Kabul harness) için sentetik veri üretildi, gerçek içerik değildir.\n\n"
            + Filler(Math.Max(0, targetChars - header.Length - 90));
        WriteFile(Path.Combine(companion, "Last-Session.md"), header + body);
    }

    private static void WriteThreads(string companion, KabulVaultSizes sizes)
    {
        var builder = new StringBuilder();
        builder.Append("# Threads\n\n## Active Threads\n\n");

        for (var i = 1; i <= sizes.ActiveThreadCount; i++)
        {
            var last = i == sizes.ActiveThreadCount;
            var label = last ? "**Durum:**" : "**Status:**";
            var statusLength = i == 1 ? sizes.LongestStatusChars : 120 + (i * 11 % 180);

            builder.Append($"### Konu {i:D2} — sentetik aktif iş\n");
            builder.Append(label).Append(' ').Append(Filler(statusLength)).Append('\n');
            builder.Append("Sentetik thread gövdesi, kabul harness fixture'ı için üretildi.\n\n");
        }

        builder.Append("## Closed Threads\n\n");
        for (var i = 1; i <= 2; i++)
        {
            builder.Append($"### Kapanan Konu {i:D2}\n");
            builder.Append("**Status:** kapandı — sentetik.\n\n");
        }

        var text = builder.ToString();
        if (text.Length < sizes.ThreadsChars)
            text += Filler(sizes.ThreadsChars - text.Length);

        WriteFile(Path.Combine(companion, "Threads.md"), text);
    }

    private static void WriteJournal(string companion, DateTimeOffset today)
    {
        var builder = new StringBuilder();
        builder.Append("# Journal\n\n");

        builder.Append($"## {today:yyyy-MM-dd} — bugünün en yeni girdisi\n");
        builder.Append(Filler(400)).Append("\n\n");

        var yesterday = today.AddDays(-1);
        builder.Append($"## {yesterday:yyyy-MM-dd} — dünkü girdi\n");
        builder.Append(Filler(300)).Append("\n\n");

        var sameDay = today.AddDays(-2);
        for (var i = 1; i <= 3; i++)
        {
            builder.Append($"## {sameDay:yyyy-MM-dd} — aynı güne ait girdi {i}\n");
            builder.Append(Filler(200)).Append("\n\n");
        }

        // Fixed literal per SPEC-3.1.0.md's own example of the undefined date-range heading form.
        builder.Append("## 2026-09-14/15 — eski aralık girdisi\n");
        builder.Append(Filler(250)).Append('\n');

        WriteFile(Path.Combine(companion, "Journal.md"), builder.ToString());
    }

    private static void WriteKurallar(string companion, int targetChars)
    {
        var header = "# Kurallar\n\n";
        WriteFile(Path.Combine(companion, "Kurallar.md"), header + Filler(Math.Max(0, targetChars - header.Length)));
    }

    private static void WriteDuzeltmeler(string companion, int targetChars)
    {
        var header = "# Düzeltmeler\n\n## Sentetik düzeltme\n";
        WriteFile(Path.Combine(companion, "Duzeltmeler.md"), header + Filler(Math.Max(0, targetChars - header.Length)));
    }

    private static void WriteDaily(string vault, DateTimeOffset today, int lineCount)
    {
        var directory = Path.Combine(vault, "daily");
        Directory.CreateDirectory(directory);

        var builder = new StringBuilder();
        builder.Append($"# {today:yyyy-MM-dd}\n\n");
        for (var i = 1; i <= Math.Max(0, lineCount - 2); i++)
            builder.Append($"- log satırı {i:D3}: sentetik günlük kaydı, kabul harness fixture'ı.\n");

        WriteFile(Path.Combine(directory, $"{today:yyyy-MM-dd}.md"), builder.ToString());
    }

    private static void WriteKnowledge(string vault, int indexTargetChars)
    {
        var knowledge = Path.Combine(vault, "knowledge");
        var concepts = Path.Combine(knowledge, "concepts");
        Directory.CreateDirectory(concepts);

        var header = "# Bilgi Tabanı — İndeks\n\n";
        WriteFile(Path.Combine(knowledge, "index.md"), header + Filler(Math.Max(0, indexTargetChars - header.Length)));

        foreach (var (slug, title) in ConceptSeeds)
        {
            var content =
                "---\n" +
                "yazan: kahin\n" +
                "model: sentetik\n" +
                $"title: {title}\n" +
                "aliases: []\n" +
                "tags: [sentetik]\n" +
                "sources: []\n" +
                "created: 2026-09-01\n" +
                "updated: 2026-09-08\n" +
                "---\n" +
                $"{title} hakkında sentetik kavram notu, kabul harness fixture'ı için üretildi.\n";
            WriteFile(Path.Combine(concepts, $"{slug}.md"), content);
        }
    }

    private static void WriteSettings(string vault, int capChars)
    {
        var directory = Path.Combine(vault, ".oom");
        Directory.CreateDirectory(directory);

        // Deliberately just the one key the F1 lane's threshold work cares about (SPEC
        // B5: "dokunulmamış oom.json (16000) ile context chars ≤ 8000"), left at the
        // live, untouched value — everything else falls back to OomSettings defaults.
        var json = "{\n  \"context\": { \"capChars\": " + capChars.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }\n}\n";
        WriteFile(Path.Combine(directory, "oom.json"), json);
    }

    private static string Filler(int targetChars)
    {
        if (targetChars <= 0)
            return string.Empty;

        const string seed = "Bu satır sentetik doldurma metnidir, kabul harness fixture'ı için üretildi ve gerçek bir içerik taşımaz. ";
        var builder = new StringBuilder(targetChars);
        while (builder.Length < targetChars)
            builder.Append(seed);
        builder.Length = targetChars;
        return builder.ToString();
    }

    private static void WriteFile(string path, string content) => File.WriteAllText(path, content, Utf8);
}
