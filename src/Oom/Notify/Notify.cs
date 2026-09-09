// yazan: codex · gpt-5
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

public sealed class Notify
{
    private static readonly HashSet<string> ToastClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "parked", "parked-session", "parked-daily", "claude-cli-contract", "task-24h", "task-overdue",
        "coverage-three-sweeps", "coverage-low", "state-rebuilt"
    };
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastNotification = new(StringComparer.OrdinalIgnoreCase);
    private readonly INotifier? notifier;
    private readonly bool toastRegistered;

    public Notify(INotifier? notifier = null, bool toastRegistered = false)
    {
        this.notifier = notifier;
        this.toastRegistered = toastRegistered;
    }

    public NotificationResult Send(string notificationClass, string key, string text, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalized = NormalizeText(text);
        var identity = $"{notificationClass.Trim()}\u001f{key.Trim()}";
        var fresh = !LastNotification.TryGetValue(identity, out var previous) || now - previous >= TimeSpan.FromDays(7);
        var eligible = fresh && ToastClasses.Contains(notificationClass.Trim());
        var toastSent = false;

        if (eligible && toastRegistered && notifier is not null)
        {
            try { notifier.Notify(normalized); toastSent = true; }
            catch { toastSent = false; }
        }
        if (fresh) LastNotification[identity] = now;
        return new NotificationResult(toastSent, fresh, normalized);
    }

    private static string NormalizeText(string text)
    {
        var oneLine = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        if (oneLine.Length == 0) oneLine = "Hafıza denetimi gerekiyor";
        if (!oneLine.EndsWith("oom doctor", StringComparison.OrdinalIgnoreCase))
            oneLine = $"{oneLine.TrimEnd('.', ' ', '—', '-')} — oom doctor";
        return oneLine;
    }
}
