using System.Text.Json;

namespace Oom.Contracts;

/// <summary>
/// The Claude Code hook payload (spec 6.1). Every hook writes one JSON object on the child's
/// stdin — <c>session_id</c>, <c>transcript_path</c>, <c>hook_event_name</c>, <c>prompt</c>,
/// <c>cwd</c> — and v0's strict reader rejected it whenever the writer added a BOM (Y-089),
/// so the BOM is tolerated here and never written anywhere by oom itself.
/// </summary>
public sealed record HookPayload(bool IsHook, string? SessionId, string? TranscriptPath, string Event, string Prompt, string? WorkingDirectory)
{
    public static readonly HookPayload Empty = new(false, null, null, string.Empty, string.Empty, null);

    /// <summary>The flush reason the event implies (spec 6.1); a non-hook run defaults to sessionend.</summary>
    public string Reason => Event.Equals("PreCompact", StringComparison.OrdinalIgnoreCase) ? "precompact" : "sessionend";

    /// <summary>Reads the payload; text that is not a hook object comes back as a bare prompt.</summary>
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
