using System.Text.Json;

namespace Oom.Contracts;

/// <summary>
/// The UserPromptSubmit hook, after retrieval-on-demand replaced hook-time injection. It no
/// longer decides what the model should remember; it counts, and every <c>nudgeEvery</c> prompts
/// it reminds the owner that the companion layer is written by hand at the end of a session.
///
/// The same counter answers the second question, at <c>flush --reason sessionend</c>: a session
/// that ran long enough to be worth reflecting on and whose <c>Last-Session.md</c> was not touched
/// while it ran leaves one <c>health</c> row behind, and the next <c>context</c> opens with it.
/// </summary>
public static class Nudge
{
    /// <summary>The reminder text; the message number is the only thing that varies.</summary>
    public static string Reminder(int promptCount) =>
        $"[Hafıza] {promptCount}. mesaj. Oturum sonunda 🔮 850-Companion/Last-Session.md ve Threads.md güncellemeyi unutma.";

    /// <summary>What the owner reads at the top of the next session's <c>[Bildirim]</c>.</summary>
    public const string ReflectionDebt =
        "[Hafıza] Geçen oturum 🔮 850-Companion/Last-Session.md güncellenmeden kapandı — yansıma borcu.";

    /// <summary>
    /// Counts this prompt and returns the hook's <c>additionalContext</c> line when the count lands
    /// on a multiple of <paramref name="every"/>; <c>null</c> otherwise. A session the counter has
    /// never seen starts at 1, so the first reminder arrives on the fifteenth prompt and not the
    /// first.
    /// </summary>
    public static string? Count(State state, string sessionId, DateTimeOffset now, int every)
    {
        ArgumentNullException.ThrowIfNull(state);
        var count = state.CountPrompt(string.IsNullOrWhiteSpace(sessionId) ? "cli" : sessionId, now);
        return every > 0 && count % every == 0 ? Reminder(count) : null;
    }

    /// <summary>The hook envelope the reminder travels in; empty when there is nothing to say.</summary>
    public static string Envelope(string? line) =>
        line is null
            ? string.Empty
            : JsonSerializer.Serialize(new { hookSpecificOutput = new { hookEventName = "UserPromptSubmit", additionalContext = line } });

    /// <summary>
    /// Whether this session closed owing a reflection. "Owing" is two measurements and no opinion:
    /// the session carried at least <paramref name="minimumPrompts"/> prompts, and the companion's
    /// <c>Last-Session.md</c> has not been written since the session's first prompt. A missing file
    /// counts as untouched — that is the debt in its purest form.
    /// </summary>
    public static bool OwesReflection(string vault, string companionDir, DateTimeOffset? firstSeen, int promptCount, int minimumPrompts)
    {
        if (firstSeen is not { } started || promptCount < minimumPrompts)
            return false;

        var path = Path.Combine(vault, companionDir, "Last-Session.md");
        return !File.Exists(path) || new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) < started;
    }
}
