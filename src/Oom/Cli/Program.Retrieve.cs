using System.Text.Json;
using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    private static int RunRetrieve(string[] args, string vault, OomSettings settings)
    {
        if (args.Contains("--session", StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("hata: retrieve --session kaldırıldı");
            return 1;
        }

        var top = ReadInt(args, "--top") ?? settings.Retrieve.Top;
        var retrieve = MakeRetrieve(vault, settings, top);
        const string cli = "cli";
        if (Value(args, "--batch") is { } batch)
        {
            foreach (var line in File.ReadAllLines(batch, Utf8))
            {
                if (ReadQuery(line) is { } query)
                    Console.WriteLine(Json(query, retrieve.Query(query, cli, top)));
            }

            return 0;
        }

        var single = Value(args, "--query") ?? string.Empty;
        var result = retrieve.Query(single, cli, top);
        Console.WriteLine(args.Contains("--json") ? Json(single, result) : result.Output);
        return result.ExitCode;
    }

    private static string Json(string query, RetrieveResult result) => JsonSerializer.Serialize(new
    {
        schema_version = 1,
        query,
        hits = result.Hits.Select(hit => new { name = hit.Name, score = hit.Score, source = hit.Source, updated = hit.Timestamp })
    });

    private static string? ReadQuery(string line)
    {
        var text = line.Trim();
        if (text.Length == 0)
            return null;
        if (text[0] != '{')
            return text;

        try
        {
            var root = JsonDocument.Parse(text).RootElement;
            return HookPayload.Field(root, "soru") ?? HookPayload.Field(root, "query");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
