using System.Globalization;

namespace Oom.Contracts;

internal static class SessionTiming
{
    public static string LastSession(DateTimeOffset now, DateTimeOffset? lastEnd) =>
        lastEnd is { } end && end <= now
            ? $"[Zaman] {Stamp(now)} · son oturum bitişi: {Stamp(end)} ({Span(now - end)} önce)"
            : $"[Zaman] {Stamp(now)} · son oturum bilinmiyor";

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

}
