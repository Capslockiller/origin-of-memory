using System.Text.Json;

namespace Oom.Contracts;

public sealed record HookPayload(bool IsHook, string? SessionId, string? TranscriptPath, string Event, string Prompt, string? WorkingDirectory)
{
    public static readonly HookPayload Empty = new(false, null, null, string.Empty, string.Empty, null);

    public string Reason => Event.Equals("PreCompact", StringComparison.OrdinalIgnoreCase) ? "precompact" : "sessionend";

    public static HookPayload Read(string? standardInput)
    {
        var text = (standardInput ?? string.Empty).TrimStart('﻿').Trim();
        if (text.Length == 0)
            return Empty;

        if (text[0] != '{')
            return Empty with { Prompt = text };

        try
        {
            var root = JsonDocument.Parse(text).RootElement;
            return new HookPayload(
                true,
                Field(root, "session_id") ?? Field(root, "sessionId"),
                Field(root, "transcript_path") ?? Field(root, "transcriptPath"),
                Field(root, "hook_event_name") ?? Field(root, "hookEventName") ?? string.Empty,
                Field(root, "prompt") ?? Field(root, "user_prompt") ?? string.Empty,
                Field(root, "cwd"));
        }
        catch (JsonException)
        {
            return Empty with { Prompt = text };
        }
    }

    internal static string? Field(JsonElement root, string name) =>
        root.ValueKind is JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
