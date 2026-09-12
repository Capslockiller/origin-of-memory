using System.Globalization;
using System.Text.Json;

namespace Oom.Contracts;

public static class Nudge
{
    public static string Reminder(int promptCount) =>
        $"[Memory] prompt {promptCount}. Before the session ends, update 🔮 850-Companion/Last-Session.md and Threads.md.";

    public const string ReflectionDebt =
        "[Hafıza] Geçen oturum 🔮 850-Companion/Last-Session.md güncellenmeden kapandı — yansıma borcu.";

    public static string? Count(State state, string sessionId, DateTimeOffset now, int every)
    {
        ArgumentNullException.ThrowIfNull(state);
        var count = state.CountPrompt(string.IsNullOrWhiteSpace(sessionId) ? "cli" : sessionId, now);
        return every > 0 && count % every == 0 ? Reminder(count) : null;
    }

    public static string Lines(State state, string sessionId, DateTimeOffset now, int every)
    {
        ArgumentNullException.ThrowIfNull(state);
        var mark = state.MarkPrompt(string.IsNullOrWhiteSpace(sessionId) ? "cli" : sessionId, now);
        var clock = Clock(now, mark.FirstSeen, mark.LastPrompt, mark.Count);
        return every > 0 && mark.Count % every == 0 ? clock + "\n" + Reminder(mark.Count) : clock;
    }

    public static string Clock(DateTimeOffset now, DateTimeOffset? firstSeen, DateTimeOffset? lastPrompt, int promptCount)
    {
        var parts = new List<string>
        {
            $"[Zaman] {Stamp(now)}",
            firstSeen is { } start && now - start >= OneMinute
                ? $"oturum {Hm(start)}{Locative(start)} başladı ({Span(now - start)})"
                : "oturum şimdi başladı"
        };

        if (lastPrompt is { } previous)
        {
            var gap = now - previous;
            parts.Add(gap < OneMinute ? "son mesaj az önce" : $"son mesaj {Span(gap)} önce");
            if (gap >= Pause)
                parts.Add($"{Span(gap)} ara");
        }

        parts.Add($"{promptCount}. mesaj");
        return string.Join(" · ", parts);
    }

    public static string LastSession(DateTimeOffset now, DateTimeOffset? lastEnd) =>
        lastEnd is { } end && end <= now
            ? $"[Zaman] {Stamp(now)} · son oturum bitişi: {Stamp(end)} ({Span(now - end)} önce)"
            : $"[Zaman] {Stamp(now)} · son oturum bilinmiyor";

    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan Pause = TimeSpan.FromMinutes(40);

    private static string Hm(DateTimeOffset value) => value.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Stamp(DateTimeOffset value) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Span(TimeSpan value)
    {
        var total = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        if (total.TotalDays >= 1)
        {
            var days = (int)total.TotalDays;
            return total.Hours > 0 ? $"{days} gün {total.Hours} sa" : $"{days} gün";
        }

        if (total.TotalHours >= 1)
        {
            var hours = (int)total.TotalHours;
            return total.Minutes > 0 ? $"{hours} sa {total.Minutes} dk" : $"{hours} sa";
        }

        return $"{(int)total.TotalMinutes} dk";
    }

    private static string Locative(DateTimeOffset value)
    {
        var number = value.Minute == 0 ? value.Hour : value.Minute;
        return Suffix(number % 10 != 0 ? number % 10 : number);
    }

    private static string Suffix(int number) => number switch
    {
        1 or 2 or 7 or 8 or 20 or 50 => "'de",
        3 or 4 or 5 => "'te",
        40 => "'ta",
        _ => "'da"
    };

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
