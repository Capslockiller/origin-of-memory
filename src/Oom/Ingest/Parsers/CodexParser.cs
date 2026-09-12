using System.Globalization;
using System.Text.Json;

namespace Oom.Contracts;

internal static class CodexParser
{
    internal static Session Parse(string jsonl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonl);
        var turns = new List<Turn>();
        string? sessionId = null;
        DateTimeOffset? sessionStarted = null;

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
                    AddFlat(root, turns);
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
                if (outerType != "event_msg") continue;

                sessionId ??= String(payload, "session_id");
                var role = payloadType switch { "user_message" => "user", "agent_message" or "assistant_message" => "assistant", _ => null };
                var message = String(payload, "message") ?? String(payload, "text");
                if (role is null || string.IsNullOrWhiteSpace(message)) continue;
                var index = Integer(payload, "index") ?? Integer(root, "index") ?? turns.Count;
                turns.Add(new Turn(index, role, "text", message, outerTimestamp ?? sessionStarted ?? DateTimeOffset.UnixEpoch.AddTicks(index)));
            }
        }

        if (string.IsNullOrWhiteSpace(sessionId) || turns.Count == 0)
            throw new FormatException("Codex rollout oturum kimliği ve en az bir event_msg metin turu içermelidir.");
        var ordered = turns.OrderBy(turn => turn.Index).ThenBy(turn => turn.Timestamp).ToArray();
        return new Session(sessionId, "codex", ordered, sessionStarted ?? ordered.Min(turn => turn.Timestamp));
    }

    private static void AddFlat(JsonElement root, ICollection<Turn> turns)
    {
        var role = String(root, "role");
        var kind = String(root, "kind") ?? "text";
        var text = String(root, "text");
        if (role is not ("user" or "assistant") || kind != "text" || string.IsNullOrWhiteSpace(text)) return;
        var index = Integer(root, "index") ?? turns.Count;
        turns.Add(new Turn(index, role, kind, text, Timestamp(root, "timestamp") ?? DateTimeOffset.UnixEpoch.AddTicks(index)));
    }

    private static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal)
        .TrimStart('\uFEFF').Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string? String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Integer(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
    private static DateTimeOffset? Timestamp(JsonElement element, string name) => String(element, name) is { } value &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
}
