// yazan: codex · gpt-5
using System.Globalization;
using System.Text.Json;

namespace Oom.Contracts;

internal static class ClaudeParser
{
    internal static Session Parse(string jsonl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonl);
        var turns = new List<Turn>();
        string? sessionId = null;

        foreach (var line in Lines(jsonl))
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException exception) { throw new FormatException("Claude transkript satırı geçerli JSON değil.", exception); }
            using (document)
            {
                var root = document.RootElement;
                sessionId ??= String(root, "sessionId") ?? String(root, "session_id");
                var role = String(root, "role") ?? String(root, "type");
                var kind = String(root, "kind") ?? "text";
                var text = String(root, "text");

                if (root.TryGetProperty("message", out var message))
                {
                    if (message.ValueKind == JsonValueKind.Object)
                    {
                        role = String(message, "role") ?? role;
                        text = ReadContent(message);
                    }
                    else if (message.ValueKind == JsonValueKind.String)
                        text = message.GetString();
                }

                if (!IsConversationRole(role) || !kind.Equals("text", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(text))
                    continue;

                var index = Integer(root, "index") ?? turns.Count;
                var timestamp = Timestamp(root, "timestamp") ?? DateTimeOffset.UnixEpoch.AddTicks(index);
                turns.Add(new Turn(index, NormalizeRole(role!), "text", text!, timestamp));
            }
        }

        if (string.IsNullOrWhiteSpace(sessionId) || turns.Count == 0)
            throw new FormatException("Claude transkripti oturum kimliği ve en az bir metin turu içermelidir.");
        var ordered = turns.OrderBy(turn => turn.Index).ThenBy(turn => turn.Timestamp).ToArray();
        return new Session(sessionId, "claude", ordered, ordered.Min(turn => turn.Timestamp));
    }

    private static string? ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        return string.Join("\n", content.EnumerateArray()
            .Where(item => String(item, "type") is "text" or "input_text" or "output_text")
            .Select(item => String(item, "text"))
            .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal)
        .TrimStart('\uFEFF').Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static bool IsConversationRole(string? role) => role is not null &&
        (role.Equals("user", StringComparison.OrdinalIgnoreCase) || role.Equals("assistant", StringComparison.OrdinalIgnoreCase));
    private static string NormalizeRole(string role) => role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "user" : "assistant";
    private static string? String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Integer(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
    private static DateTimeOffset? Timestamp(JsonElement element, string name) => String(element, name) is { } value &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
}
