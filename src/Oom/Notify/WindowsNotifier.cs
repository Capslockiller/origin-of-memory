using System.Globalization;

namespace Oom.Contracts;

/// <summary>
/// The Windows side of spec 6.8. A toast is shown through <c>Windows.UI.Notifications</c> only
/// when the AppUserModelID <c>install</c> registers actually exists — an unregistered AUMID
/// makes the platform throw, and a memory tool may never crash a hook — so without it the line
/// is only queued for the next SessionStart block. The same <c>(class, key)</c> notifies once
/// in seven days; the queued line lives in <c>health</c> so <c>doctor</c> sees it too.
/// </summary>
public sealed class WindowsNotifier(State? state = null, string? applicationId = null, IClock? clock = null) : INotifier
{
    /// <summary>The identity <c>install</c> registers for the Start menu shortcut (spec 6.8, D3).</summary>
    public const string ApplicationId = ShortcutRegistration.ApplicationUserModelId;

    private const int DedupeDays = 7;

    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly string _applicationId = applicationId ?? ApplicationId;

    /// <summary>What the last <see cref="Notify"/> did; the report and doctor read it.</summary>
    public NotificationResult Last { get; private set; } = new(false, false, string.Empty);

    public void Notify(string text) => Send("parked", text, text);

    /// <summary>One notification: deduped, queued for SessionStart, and toasted when it can be.</summary>
    public NotificationResult Send(string notificationClass, string key, string text)
    {
        var line = Line(text);
        var now = _clock.Now;
        var fresh = IsFresh(notificationClass, key, now);
        if (!fresh)
            return Last = new NotificationResult(false, false, line);

        Remember(notificationClass, key, line, now);
        return Last = new NotificationResult(Toast(line), true, line);
    }

    /// <summary>
    /// Whether a toast can be shown at all. The answer is the shortcut lane D2's
    /// <see cref="ShortcutRegistration"/> writes — the writer of the AUMID is the only honest
    /// source for "is it there?", so this no longer scans the Start menu on its own.
    /// </summary>
    public bool IsRegistered() => ShortcutRegistration.IsRegistered;

    private bool Toast(string line)
    {
        if (!IsRegistered() || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return false;

        try
        {
            var document = new Windows.Data.Xml.Dom.XmlDocument();
            document.LoadXml($"<toast><visual><binding template=\"ToastGeneric\"><text>{Escape(line)}</text></binding></visual></toast>");
            Windows.UI.Notifications.ToastNotificationManager
                .CreateToastNotifier(_applicationId)
                .Show(new Windows.UI.Notifications.ToastNotification(document));
            return true;
        }
        catch (Exception)
        {
            // An unregistered or revoked AUMID must never take a hook down with it (spec 6.8).
            return false;
        }
    }

    private bool IsFresh(string notificationClass, string key, DateTimeOffset now)
    {
        if (state is null)
            return true;

        var cutoff = now.AddDays(-DedupeDays).ToString("O", CultureInfo.InvariantCulture);
        var recent = state.Scalar(
            $"SELECT COUNT(*) FROM notified WHERE class = '{Sql(notificationClass)}' AND key = '{Sql(key)}' AND ts >= '{cutoff}'");
        return recent == 0;
    }

    private void Remember(string notificationClass, string key, string line, DateTimeOffset now)
    {
        if (state is null)
            return;

        state.RecordNotified(notificationClass, key, now);
        state.WriteHealthConcurrently([new HealthItem("notify", HealthLevel.Warning, notificationClass, key, line)]);
    }

    private static string Line(string text)
    {
        var single = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (single.Length == 0)
            single = "Hafıza denetimi gerekiyor";

        return single.EndsWith("oom doctor", StringComparison.OrdinalIgnoreCase)
            ? single
            : $"{single.TrimEnd('.', ' ', '—', '-')} — oom doctor";
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Sql(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
