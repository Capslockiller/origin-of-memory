using System.Globalization;
using System.Text.Json;

namespace Oom.Contracts;

public sealed record TranscriptRead(string? SessionId, string? WorkingDirectory, IReadOnlyList<Turn> Turns);

internal static class ClaudeTranscript
{
    private static readonly string[] EnvelopeMarkers =
    [
        "<task-notification", "<system-reminder", "<local-command-stdout", "<command-message", "<command-name"
    ];

    internal static TranscriptRead Read(string jsonl, IClock clock)
    {
        var turns = new List<Turn>();
        string? sessionId = null;
        string? workingDirectory = null;
        var index = 0;

        foreach (var line in (jsonl ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('﻿');
            if (trimmed.Length == 0 || trimmed[0] != '{')
                continue;

            JsonElement root;
            try
            {
                root = JsonDocument.Parse(trimmed).RootElement;
            }
            catch (JsonException)
            {
                continue;
            }

            sessionId ??= Text(root, "sessionId") ?? Text(root, "session_id");
            workingDirectory ??= Text(root, "cwd");
            if (Flag(root, "isSidechain") || root.TryGetProperty("toolUseResult", out _))
                continue;

            var message = root.TryGetProperty("message", out var value) ? value : default;
            var role = (message.ValueKind is JsonValueKind.Object ? Text(message, "role") : null)
                ?? Text(root, "role") ?? Text(root, "type");
            if (role is not ("user" or "assistant"))
                continue;

            var content = message.ValueKind is JsonValueKind.Object && message.TryGetProperty("content", out var blocks) ? blocks : default;
            var tools = content.ValueKind is JsonValueKind.Array
                ? content.EnumerateArray().Where(b => Text(b, "type") == "tool_use").Select(b => Text(b, "name") ?? "tool").ToArray() : [];
            var toolResult = content.ValueKind is JsonValueKind.Array && content.EnumerateArray().Any(b => Text(b, "type") == "tool_result");
            var text = ReadText(root, message) ?? string.Empty;
            if (text.Length == 0 && tools.Length == 0 && !toolResult) continue;
            var kind = toolResult ? "tool_result" : text.Length == 0 && tools.Length > 0 ? "tool_use"
                : Flag(root, "isCompactSummary") ? "compact" : Flag(root, "isMeta") ? "envelope" : Classify(Text(root, "kind") ?? "text", text);
            if (kind == "tool_use" && turns.LastOrDefault(t => t.Class == "a") is { } previous)
                turns[turns.IndexOf(previous)] = previous with { Tools = [.. previous.Tools ?? [], .. tools] };
            var position = root.TryGetProperty("index", out var declared) && declared.TryGetInt32(out var parsed) ? parsed : index;
            var turnClass = TurSiniflandirma.Classify(role, kind, text);
            turns.Add(new Turn(position, turnClass == "h" ? "harness" : role, kind, text, Stamp(root, clock),
                Text(root, "uuid") ?? TurSiniflandirma.Id(sessionId ?? string.Empty, position), turnClass, tools));
            index++;
        }

        return new TranscriptRead(sessionId, workingDirectory, turns);
    }

    private static string? ReadText(JsonElement root, JsonElement message)
    {
        if (Text(root, "text") is { } direct)
            return direct;

        if (message.ValueKind is JsonValueKind.String)
            return message.GetString();

        if (message.ValueKind is not JsonValueKind.Object || !message.TryGetProperty("content", out var content))
            return null;

        if (content.ValueKind is JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind is not JsonValueKind.Array)
            return null;

        var blocks = content.EnumerateArray()
            .Where(block => block.ValueKind is JsonValueKind.Object && Text(block, "type") is "text" or "input_text" or "output_text")
            .Select(block => Text(block, "text"))
            .Where(value => value is not null);

        var joined = string.Join("\n", blocks);
        return joined.Length == 0 ? null : joined;
    }

    private static string Classify(string kind, string text)
    {
        if (kind is "tool_use" or "tool_result" or "thinking" or "system")
            return kind;

        return EnvelopeMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)) ? "envelope" : "text";
    }

    private static DateTimeOffset Stamp(JsonElement root, IClock clock) =>
        Text(root, "timestamp") is { } value
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : clock.Now;

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind is JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
