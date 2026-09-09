using System.Globalization;
using System.Text.Json;

namespace Oom.Contracts;

/// <summary>What one transcript file says about itself: its session, its project and its turns.</summary>
public sealed record TranscriptRead(string? SessionId, string? WorkingDirectory, IReadOnlyList<Turn> Turns);

/// <summary>
/// The reader for the real Claude Code <c>*.jsonl</c> transcript (spec 6.3). Claude Code writes
/// one JSON object per line and only two of its line types are conversation: <c>user</c> and
/// <c>assistant</c>, each carrying <c>message.role</c> and a <c>message.content</c> that is
/// either a string or an array of blocks (<c>text</c>, <c>tool_use</c>, <c>tool_result</c>,
/// <c>thinking</c>). Everything else on the line — <c>queue-operation</c>, <c>attachment</c>,
/// <c>summary</c>, <c>system</c>, <c>bridge-session</c>, <c>last-prompt</c> — is bookkeeping and
/// is skipped, as are sub-agent lines (<c>isSidechain</c>) and the machine's own injected lines
/// (<c>isMeta</c>, tool results). The synthetic scar fixture shape (top level <c>role</c>,
/// <c>kind</c>, <c>text</c>, <c>index</c>) is read by the same function, so one parser serves the
/// fixtures and the real archive and exactly one file breaks when the outside format moves.
/// </summary>
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
                // A half-written last line is normal while a session is still live.
                continue;
            }

            sessionId ??= Text(root, "sessionId") ?? Text(root, "session_id");
            workingDirectory ??= Text(root, "cwd");
            if (Flag(root, "isSidechain") || Flag(root, "isMeta") || root.TryGetProperty("toolUseResult", out _))
                continue;

            var message = root.TryGetProperty("message", out var value) ? value : default;
            var role = (message.ValueKind is JsonValueKind.Object ? Text(message, "role") : null)
                ?? Text(root, "role") ?? Text(root, "type");
            if (role is not ("user" or "assistant"))
                continue;

            if (ReadText(root, message) is not { Length: > 0 } text)
                continue;

            var kind = Classify(Text(root, "kind") ?? "text", text);
            var position = root.TryGetProperty("index", out var declared) && declared.TryGetInt32(out var parsed) ? parsed : index;
            turns.Add(new Turn(position, role, kind, text, Stamp(root, clock)));
            index++;
        }

        return new TranscriptRead(sessionId, workingDirectory, turns);
    }

    private static string? ReadText(JsonElement root, JsonElement message)
    {
        // The scar fixtures put the text on the line itself; Claude Code puts it in `message`.
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

        // Only the text blocks are summarisable: tool_use, tool_result and thinking are the
        // machine talking to itself and never become user memory (spec 6.3-1).
        var blocks = content.EnumerateArray()
            .Where(block => block.ValueKind is JsonValueKind.Object && Text(block, "type") is "text" or "input_text" or "output_text")
            .Select(block => Text(block, "text"))
            .Where(value => !string.IsNullOrWhiteSpace(value));

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
