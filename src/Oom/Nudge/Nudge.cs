using System.Text.Json;

namespace Oom.Contracts;

public static class Nudge
{
    public static string Reminder(int promptCount) =>
        $"[Hafıza] {promptCount}. mesaj. Oturum sonunda 🔮 850-Companion/Last-Session.md ve Threads.md güncellemeyi unutma.";

    public const string ReflectionDebt =
        "[Hafıza] Geçen oturum 🔮 850-Companion/Last-Session.md güncellenmeden kapandı — yansıma borcu.";

    public static string? Count(State state, string sessionId, DateTimeOffset now, int every)
    {
        ArgumentNullException.ThrowIfNull(state);
        var count = state.CountPrompt(string.IsNullOrWhiteSpace(sessionId) ? "cli" : sessionId, now);
        return every > 0 && count % every == 0 ? Reminder(count) : null;
    }

    public static string Envelope(string? line) =>
        line is null
            ? string.Empty
            : JsonSerializer.Serialize(new { hookSpecificOutput = new { hookEventName = "UserPromptSubmit", additionalContext = line } });

    public static bool OwesReflection(string vault, string companionDir, DateTimeOffset? firstSeen, int promptCount, int minimumPrompts)
    {
        if (firstSeen is not { } started || promptCount < minimumPrompts)
            return false;

        var path = Path.Combine(vault, companionDir, "Last-Session.md");
        return !File.Exists(path) || new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) < started;
    }
}
