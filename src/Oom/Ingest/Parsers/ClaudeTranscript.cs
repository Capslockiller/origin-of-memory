using System.Globalization;
using System.Text.Json;

namespace Oom.Contracts;

public sealed record TranscriptRead(string? SessionId, string? WorkingDirectory, IReadOnlyList<Turn> Turns)
{
    internal HashSet<string> OwnerAnswers { get; init; } = new(StringComparer.Ordinal);
}

internal static class ClaudeTranscript
{
    private static readonly string[] EnvelopeMarkers =
    [
        "<task-notification", "<system-reminder", "<local-command-stdout", "<command-message", "<command-name"
    ];

    internal static TranscriptRead Read(string jsonl, IClock clock, string? requestedId = null, int startIndex = 0)
    {
        var turns = new List<Turn>();
        string? sessionId = null;
        string? workingDirectory = null;
        var index = startIndex;
        var questions = new HashSet<string>(StringComparer.Ordinal);
        var answers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in (jsonl ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('﻿');
            if (trimmed.Length == 0 || trimmed[0] != '{')
                continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(trimmed);
            }
            catch (JsonException)
            {
                continue;
            }

            using var held = document;
            var root = document.RootElement;
            var recordId = Text(root, "sessionId") ?? Text(root, "session_id");
            if (requestedId is not null && recordId != requestedId) continue;
            sessionId ??= recordId;
            workingDirectory ??= Text(root, "cwd");
            if (Flag(root, "isSidechain"))
                continue;

            var message = root.TryGetProperty("message", out var value) ? value : default;
            var role = (message.ValueKind is JsonValueKind.Object ? Text(message, "role") : null)
                ?? Text(root, "role") ?? Text(root, "type");
            if (role is not ("user" or "assistant"))
                continue;

            var content = message.ValueKind is JsonValueKind.Object && message.TryGetProperty("content", out var blocks) ? blocks : default;
            if (role == "assistant" && content.ValueKind == JsonValueKind.Array)
                foreach (var block in content.EnumerateArray().Where(b => Text(b, "type") == "tool_use" && Text(b, "name") == "AskUserQuestion"))
                    if (Text(block, "id") is { } question) questions.Add(question);
            var answer = role == "user" ? OwnerAnswer(content, questions) : null;
            if (answer is null && root.TryGetProperty("toolUseResult", out _)) continue;
            var tools = content.ValueKind is JsonValueKind.Array
                ? content.EnumerateArray().Where(b => Text(b, "type") == "tool_use").Select(b => Text(b, "name") ?? "tool").ToArray() : [];
            var toolResult = content.ValueKind is JsonValueKind.Array && content.EnumerateArray().Any(b => Text(b, "type") == "tool_result");
            var text = answer ?? ReadText(root, message) ?? string.Empty;
            if (text.Length == 0 && tools.Length == 0 && !toolResult) continue;
            var kind = answer is not null ? "text" : toolResult ? "tool_result" : text.Length == 0 && tools.Length > 0 ? "tool_use"
                : Flag(root, "isCompactSummary") ? "compact" : Flag(root, "isMeta") ? "envelope" : Classify(Text(root, "kind") ?? "text", text);
            if (kind == "tool_use" && turns.LastOrDefault(t => t.Class == "a") is { } previous)
                turns[turns.IndexOf(previous)] = previous with { Tools = [.. previous.Tools ?? [], .. tools] };
            var position = root.TryGetProperty("index", out var declared) && declared.TryGetInt32(out var parsed) ? parsed : index;
            var turnClass = answer is not null ? "u" : TurSiniflandirma.Classify(role, kind, text);
            turns.Add(new Turn(position, turnClass == "h" ? "harness" : role, kind, text, Stamp(root, clock),
                Text(root, "uuid") ?? TurSiniflandirma.Id(sessionId ?? string.Empty, position), turnClass, tools));
            if (answer is not null) answers.Add(turns[^1].Uuid);
            index++;
        }

        return new TranscriptRead(sessionId, workingDirectory, turns) { OwnerAnswers = answers };
    }

    private static string? OwnerAnswer(JsonElement content, HashSet<string> questions)
    {
        if (content.ValueKind != JsonValueKind.Array) return null;
        foreach (var block in content.EnumerateArray().Where(b => Text(b, "type") == "tool_result"))
        {
            if (!block.TryGetProperty("content", out var result)) continue;
            var text = result.ValueKind == JsonValueKind.String ? result.GetString() ?? ""
                : result.ValueKind == JsonValueKind.Array ? string.Join("\n", result.EnumerateArray()
                    .Where(b => Text(b, "type") == "text").Select(b => Text(b, "text") ?? "")) : "";
            if (text.StartsWith("User has approved your plan", StringComparison.Ordinal)) continue;
            if (questions.Contains(Text(block, "tool_use_id") ?? "") || text.StartsWith("The user answered:", StringComparison.Ordinal)
                || text.Contains("To tell you how to proceed, the user said:", StringComparison.Ordinal)) return text;
        }
        return null;
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
