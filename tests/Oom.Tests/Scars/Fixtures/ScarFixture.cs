using System.Text;
using Oom.Contracts;

namespace Oom.Tests.Scars.Fixtures;

internal static class ScarFixture
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(3));

    internal static Session Session(string id, int turns, int charactersPerTurn = 12, DateTimeOffset? lastTurnAt = null, string source = "claude")
    {
        var start = (lastTurnAt ?? Now).AddMinutes(-turns);
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

    internal static bool HasUtf8Bom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    internal static byte[] WithBom(string text) => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)];
}
