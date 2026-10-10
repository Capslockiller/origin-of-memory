using System.Globalization;
using System.Text.Json;

namespace Oom.Contracts;

internal static class CodexParser
{
    internal static Session Parse(string jsonl, bool capture = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonl);
        var turns = new List<Turn>();
        string? sessionId = null;
        DateTimeOffset? sessionStarted = null;
        var responseMessages = Lines(jsonl).Any(line =>
        {
            try { using var doc = JsonDocument.Parse(line); return String(doc.RootElement, "type") == "response_item"
                && doc.RootElement.TryGetProperty("payload", out var item) && String(item, "type") == "message" && String(item, "role") is "user" or "assistant"; }
            catch (JsonException) { return false; }
        });

        foreach (var line in Lines(jsonl))
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                sessionId ??= String(root, "session_id");
                var outerTimestamp = Timestamp(root, "timestamp");
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    AddFlat(root, turns, sessionId ?? string.Empty);
                    continue;
                }

                var outerType = String(root, "type");
                var payloadType = String(payload, "type");
                if (outerType == "session_meta")
                {
                    sessionId ??= String(payload, "id") ?? String(payload, "session_id");
                    sessionStarted ??= Timestamp(payload, "timestamp") ?? outerTimestamp;
                    continue;
                }

                sessionStarted ??= outerTimestamp;

                if (outerType == "response_item")
                {
                    if (payloadType is "function_call" or "custom_tool_call")
                    {
                        if (turns.LastOrDefault(t => t.Class == "a") is { } previous)
                            turns[turns.IndexOf(previous)] = previous with { Tools = [.. previous.Tools ?? [], String(payload, "name") ?? "tool"] };
                        continue;
                    }
                    if (payloadType != "message") continue;
                    var messageRole = String(payload, "role");
                    if (messageRole is not ("user" or "assistant")) continue;
                    var body = Content(payload);
                    if (string.IsNullOrWhiteSpace(body)) continue;
                    if (!capture && messageRole == "user" && body.TrimStart()[0] == '<') continue;
                    var position = Integer(root, "ordinal") ?? Integer(root, "index") ?? turns.Count;
                    var turnClass = TurSiniflandirma.Classify(messageRole, "text", body);
                    turns.Add(new Turn(position, turnClass == "h" ? "harness" : messageRole, "text", body,
                        outerTimestamp ?? sessionStarted ?? DateTimeOffset.UnixEpoch.AddTicks(position),
                        String(payload, "id") ?? String(root, "id") ?? TurSiniflandirma.Id(sessionId ?? string.Empty, position), turnClass));
                    continue;
                }

                if (outerType != "event_msg" || responseMessages) continue;

                sessionId ??= String(payload, "session_id");
                var role = payloadType switch { "user_message" => "user", "agent_message" or "assistant_message" => "assistant", _ => null };
                var message = String(payload, "message") ?? String(payload, "text");
                if (role is null || string.IsNullOrWhiteSpace(message)) continue;
                var index = Integer(payload, "index") ?? Integer(root, "index") ?? turns.Count;
                var classification = TurSiniflandirma.Classify(role, "text", message);
                turns.Add(new Turn(index, classification == "h" ? "harness" : role, "text", message,
                    outerTimestamp ?? sessionStarted ?? DateTimeOffset.UnixEpoch.AddTicks(index),
                    String(payload, "id") ?? String(root, "id") ?? TurSiniflandirma.Id(sessionId ?? string.Empty, index), classification));
            }
        }

        if (string.IsNullOrWhiteSpace(sessionId) || turns.Count == 0)
            throw new FormatException("Codex rollout oturum kimliği ve en az bir metin turu içermelidir.");
        var ordered = turns.OrderBy(turn => turn.Index).ThenBy(turn => turn.Timestamp).ToArray();
        return new Session(sessionId, "codex", ordered, sessionStarted ?? ordered.Min(turn => turn.Timestamp));
    }

    private static void AddFlat(JsonElement root, ICollection<Turn> turns, string sessionId)
    {
        var role = String(root, "role");
        var kind = String(root, "kind") ?? "text";
        var text = String(root, "text");
        if (role is not ("user" or "assistant") || kind != "text" || string.IsNullOrWhiteSpace(text)) return;
        var index = Integer(root, "index") ?? turns.Count;
        var classification = TurSiniflandirma.Classify(role, kind, text);
        turns.Add(new Turn(index, classification == "h" ? "harness" : role, kind, text, Timestamp(root, "timestamp") ?? DateTimeOffset.UnixEpoch.AddTicks(index),
            String(root, "id") ?? TurSiniflandirma.Id(sessionId, index), classification));
    }

    private static string? Content(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content)) return String(payload, "text");
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        var blocks = content.EnumerateArray()
            .Where(block => block.ValueKind == JsonValueKind.Object && String(block, "type") is "text" or "input_text" or "output_text")
            .Select(block => String(block, "text"))
            .Where(value => value is not null);
        var joined = string.Join("\n", blocks);
        return joined.Length == 0 ? null : joined;
    }

    private static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal)
        .TrimStart('\uFEFF').Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string? String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Integer(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
    private static DateTimeOffset? Timestamp(JsonElement element, string name) => String(element, name) is { } value &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
}
