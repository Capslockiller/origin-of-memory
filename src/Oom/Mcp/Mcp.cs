using System.Text;
using System.Text.Json;

namespace Oom.Contracts;

public sealed class Mcp
{
    private readonly Guards guards;
    private readonly Retrieve retrieve;
    private readonly string vaultPath;
    private readonly Func<string>? rootMapProvider;
    private readonly Func<string, string?>? noteProvider;

    public Mcp(Guards? guards = null, Retrieve? retrieve = null, string? vaultPath = null,
        Func<string>? rootMapProvider = null, Func<string, string?>? noteProvider = null)
    {
        this.guards = guards ?? new Guards();
        this.retrieve = retrieve ?? new Retrieve();
        this.vaultPath = Path.GetFullPath(vaultPath ?? Environment.CurrentDirectory);
        this.rootMapProvider = rootMapProvider;
        this.noteProvider = noteProvider;
    }

    public RetrieveResult MemorySearch(string query, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var gated = guards.Gate(query, Direction.In, ComponentKind.Retrieve);
        if (gated.Refused) return new RetrieveResult([], "Sorgu güvenlik kapısında reddedildi.", 1);
        return retrieve.Query(gated.Text, "mcp", Math.Clamp(limit, 1, 5));
    }

    public string MemoryRootMap()
    {
        if (rootMapProvider is not null) return rootMapProvider();
        var path = Path.Combine(vaultPath, "knowledge", "index.md");
        return File.Exists(path) ? File.ReadAllText(path, new UTF8Encoding(false, true)) : string.Empty;
    }

    public string MemoryNote(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var fileName = Path.GetFileName(name);
        if (!fileName.Equals(name, StringComparison.Ordinal) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Not adı yalnız tek bir dosya adı olmalıdır.", nameof(name));
        if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) fileName += ".md";
        if (noteProvider is not null) return noteProvider(fileName) ?? string.Empty;
        var path = Path.Combine(vaultPath, "knowledge", "concepts", fileName);
        return File.Exists(path) ? File.ReadAllText(path, new UTF8Encoding(false, true)) : string.Empty;
    }

    public string HandleJsonRpc(string requestJson)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(requestJson); }
        catch (JsonException) { return Error(default, -32700, "JSON ayrıştırılamadı."); }
        using (document)
        {
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : default;
            if (!root.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0") return Error(id, -32600, "JSON-RPC sürümü 2.0 olmalıdır.");
            var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
            try
            {
                return method switch
                {
                    "initialize" => Result(id, new { protocolVersion = "2025-06-18", capabilities = new { tools = new { } }, serverInfo = new { name = "oom", version = "2.0" } }),
                    "notifications/initialized" => string.Empty,
                    "ping" => Result(id, new { }),
                    "tools/list" => Result(id, new { tools = ToolDefinitions() }),
                    "tools/call" => CallTool(id, root),
                    _ => Error(id, -32601, "Yöntem bulunamadı.")
                };
            }
            catch (ArgumentException exception) { return Error(id, -32602, exception.Message); }
            catch (Exception exception) { return Error(id, -32603, $"Araç çalıştırılamadı: {exception.Message}"); }
        }
    }

    public void Run(TextReader input, TextWriter output)
    {
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = HandleJsonRpc(line);
            if (response.Length == 0) continue;
            output.WriteLine(response);
            output.Flush();
        }
    }

    private string CallTool(JsonElement id, JsonElement request)
    {
        if (!request.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            return Error(id, -32602, "Araç parametreleri eksik.");
        var name = parameters.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        var arguments = parameters.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.Object
            ? argumentsElement : default;
        var text = name switch
        {
            "memory_search" => Search(arguments),
            "memory_root_map" => MemoryRootMap(),
            "memory_note" => MemoryNote(RequiredString(arguments, "name")),
            _ => throw new ArgumentException("Bilinmeyen hafıza aracı.")
        };
        return Result(id, new { content = new[] { new { type = "text", text } }, isError = false });
    }

    private string Search(JsonElement arguments)
    {
        var query = RequiredString(arguments, "query");
        var limit = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("limit", out var value) && value.TryGetInt32(out var parsed) ? parsed : 3;
        var result = MemorySearch(query, limit);
        return result.Output.Length > 0 ? result.Output : string.Join("\n\n", result.Hits.Select(hit => hit.Text));
    }

    private static string RequiredString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new ArgumentException($"{name} parametresi eksik.");

    private static object[] ToolDefinitions() =>
    [
        new { name = "memory_search", description = "Hafıza notlarında salt okunur arama yapar.", inputSchema = new { type = "object", properties = new { query = new { type = "string" }, limit = new { type = "integer", minimum = 1, maximum = 5 } }, required = new[] { "query" }, additionalProperties = false } },
        new { name = "memory_root_map", description = "Hafıza kök haritasını getirir.", inputSchema = new { type = "object", properties = new { }, additionalProperties = false } },
        new { name = "memory_note", description = "Tek bir hafıza notunun tam metnini getirir.", inputSchema = new { type = "object", properties = new { name = new { type = "string" } }, required = new[] { "name" }, additionalProperties = false } }
    ];

    private static string Result(JsonElement id, object result) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = IdValue(id), result });
    private static string Error(JsonElement id, int code, string message) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = IdValue(id), error = new { code, message } });
    private static object? IdValue(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => id.GetString(), JsonValueKind.Number when id.TryGetInt64(out var value) => value,
        JsonValueKind.Null or JsonValueKind.Undefined => null, _ => id.GetRawText()
    };
}
