using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oom.Contracts;

namespace Oom.Tests.Scars.Fixtures;

internal static class ScarFixture
{
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;

    [ModuleInitializer]
    internal static void DisableWindowsErrorReportingDialogs()
    {
        if (OperatingSystem.IsWindows())
            SetErrorMode(SEM_NOGPFAULTERRORBOX);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    internal static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(3));

    internal static Session Session(string id, int turns, int charactersPerTurn = 12, DateTimeOffset? lastTurnAt = null, string source = "claude")
    {
        var start = (lastTurnAt ?? Now).AddMinutes(-(turns - 1));
        var items = Enumerable.Range(0, turns)
            .Select(i => new Turn(i, i % 2 == 0 ? "user" : "assistant", "text", new string((char)('a' + i % 26), charactersPerTurn), start.AddMinutes(i)))
            .ToArray();
        return new Session(id, source, items, start);
    }

    internal static string TranscriptJsonl(Session session, bool includeTool = false)
    {
        var lines = session.Turns.Select(t => $"{{\"session_id\":\"{session.Id}\",\"index\":{t.Index},\"role\":\"{t.Role}\",\"kind\":\"{t.Kind}\",\"text\":\"{t.Text}\",\"timestamp\":\"{t.Timestamp:O}\"}}").ToList();
        if (includeTool)
            lines.Insert(1, $"{{\"session_id\":\"{session.Id}\",\"index\":1,\"role\":\"assistant\",\"kind\":\"tool_result\",\"text\":\"tool-payload\",\"timestamp\":\"{Now:O}\"}}");
        return string.Join('\n', lines);
    }

    internal static string ValidSummary(string prefix = "") => $"{prefix}## Bağlam\nBağlam.\n## Önemli Konuşmalar\nKonuşma.\n## Alınan Kararlar\nKarar.\n## Öğrenilenler\nDers.\n## Yapılacaklar\nİş.";

    internal static Note Note(int index, string? body = null) => new(
        $"note-{index:D3}.md", $"Kavram {index}", [$"takma-{index}"], ["bellek"], ["2026-09-08.md"],
        new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 8), body ?? $"Kavram {index} gövdesi.");

    internal static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Oom.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Oom.sln bulunamadı.");
    }

    internal static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "oom-scar-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        return path;
    }

    internal const string CompanionDir = "🔮 850-Companion";

    internal static string RetrievalVault()
    {
        var vault = TempDirectory();
        var concepts = Path.Combine(vault, "knowledge", "concepts");
        Directory.CreateDirectory(concepts);
        foreach (var (name, title, body) in ConceptFixtures)
            File.WriteAllText(Path.Combine(concepts, name),
                $"---\nyazan: codex\nmodel: gpt-6\ntitle: {title}\naliases: []\ntags: []\nsources: [2026-09-08.md]\ncreated: 2026-09-01\nupdated: 2026-09-08\n---\n{body}",
                new UTF8Encoding(false));

        var companion = Path.Combine(vault, CompanionDir);
        Directory.CreateDirectory(companion);
        File.WriteAllText(Path.Combine(companion, "Duzeltmeler.md"),
            "# Düzeltmeler\n\n## Speaking sınavı tarihi ve ücreti değişti\nyerine: speaking-sinavi-tarihi.md\nSpeaking sınavı tarihi 20 Eylül'e alındı, ücret 52 EUR oldu.\n",
            new UTF8Encoding(false));

        return vault;
    }

    private static readonly (string Name, string Title, string Body)[] ConceptFixtures =
    [
        ("turkce-tokenizasyon-karari.md", "Türkçe tokenizasyon kararı",
            "Türkçe tokenizasyon kararı 8 Eylül'de alındı: beş harflik önek, stemmer yok."),
        ("gold-set-olcum-kosumu.md", "Gold set ölçüm koşumu", "Gold set koşumu 125 soruyla ölçüldü."),
        ("gold-set-kanarya-sorulari.md", "Gold set kanarya soruları", "Gold set kanarya soruları enjeksiyon üretmemeli."),
        ("gold-set-sinif-dagilimi.md", "Gold set sınıf dağılımı", "Gold set sınıf dağılımı tek-not ve çok-not olarak ikiye ayrılır."),
        ("gold-set-esik-degerleri.md", "Gold set eşik değerleri", "Gold set eşik değerleri recall@3 ve recall@5 için ayrı tutulur."),
        ("gold-set-kapsama-raporu.md", "Gold set kapsama raporu", "Gold set kapsama raporu her koşumdan sonra yazılır."),
        ("speaking-sinavi-tarihi.md", "Speaking sınavı tarihi ve ücreti", "Speaking sınavı tarihi 13 Eylül, ücret 48 EUR."),
        ("panel-guvenlik-kapisi.md", "Panel güvenlik kapısı", "Panel güvenlik kapısı ayrı bir konudur.")
    ];

    internal static string CompanionVault(string body)
    {
        var vault = TempDirectory();
        Directory.CreateDirectory(Path.Combine(vault, CompanionDir));
        File.WriteAllText(Path.Combine(vault, CompanionDir, "Duzeltmeler.md"), body, new UTF8Encoding(false));
        return vault;
    }

    internal static void Remove(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static bool HasUtf8Bom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    internal static byte[] WithBom(string text) => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)];
}
