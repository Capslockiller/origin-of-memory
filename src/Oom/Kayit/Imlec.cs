using System.Globalization;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

internal static class Imlec
{
    private static readonly Regex Anchor = new(@"^<!-- session:(?<id>\S+) ts:\S+ turns:\d+-(?<end>\d+)(?<extra>[^\r\n]*?) -->\r?$", RegexOptions.Multiline);
    private static readonly Regex Last = new(@"\bson:(\S+)");
    private static readonly Regex TurnId = new(@"^### \[[uah] ([^\]\r\n]+)\]", RegexOptions.Multiline);

    internal sealed record Cursor(string Last, int End, string? Project);

    internal static Cursor? TailCursor(string vault, string sessionId, DateTimeOffset now)
    {
        Cursor? cursor = null;
        for (var days = 7; days >= 0; days--)
        {
            var path = Path.Combine(vault, "daily", now.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md");
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            foreach (Match anchor in Anchor.Matches(text))
            {
                if (anchor.Groups["id"].Value != sessionId) continue;
                var last = Last.Match(anchor.Groups["extra"].Value);
                if (last.Success) cursor = int.TryParse(anchor.Groups["end"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var end)
                    && end < int.MaxValue ? new(last.Groups[1].Value, end, ProjectBefore(text, anchor.Index, sessionId)) : null;
            }
        }
        return cursor;
    }

    private static string? ProjectBefore(string text, int index, string sessionId)
    {
        var before = text[..index].TrimEnd('\r', '\n');
        var line = before[(before.LastIndexOf('\n') + 1)..];
        var prefix = $"## kayit · oturum {sessionId} · claude-code · ";
        var turns = line.LastIndexOf(" · tur ", StringComparison.Ordinal);
        var time = turns <= 0 ? -1 : line.LastIndexOf(" · ", turns - 1, StringComparison.Ordinal);
        return line.StartsWith(prefix, StringComparison.Ordinal) && time >= prefix.Length ? line[prefix.Length..time] : null;
    }

    internal static Dictionary<string, DateTimeOffset> LatestAnchors(string vault, DateTimeOffset now)
    {
        var latest = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        for (var days = 7; days >= 0; days--)
        {
            var path = Path.Combine(vault, "daily", now.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md");
            if (!File.Exists(path)) continue;
            foreach (Match anchor in Anchor.Matches(File.ReadAllText(path)))
            {
                var stamp = anchor.Value.Split(" ts:")[1].Split(' ')[0];
                if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)) continue;
                var id = anchor.Groups["id"].Value;
                if (!latest.TryGetValue(id, out var previous) || time > previous) latest[id] = time;
            }
        }
        return latest;
    }

    internal static IReadOnlyList<Turn> Pending(string vault, Session session, DateTimeOffset now, string? removedCursor = null)
    {
        var recorded = new HashSet<string>(StringComparer.Ordinal);
        string? last = null;
        var legacyEnd = -1;
        for (var days = 7; days >= 0; days--)
        {
            var path = Path.Combine(vault, "daily", now.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md");
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            var anchors = Anchor.Matches(text);
            for (var i = 0; i < anchors.Count; i++)
            {
                var anchor = anchors[i];
                if (anchor.Groups["id"].Value != session.Id) continue;
                var son = Last.Match(anchor.Groups["extra"].Value);
                if (son.Success) { last = son.Groups[1].Value; recorded.Add(last); }
                else legacyEnd = Math.Max(legacyEnd, int.Parse(anchor.Groups["end"].Value, CultureInfo.InvariantCulture));
                var end = i + 1 < anchors.Count ? anchors[i + 1].Index : text.Length;
                foreach (Match turn in TurnId.Matches(text[anchor.Index..end])) recorded.Add(turn.Groups[1].Value);
            }
        }
        var cursor = last is null || last == removedCursor ? -1 : session.Turns.ToList().FindIndex(t => t.Uuid == last);
        var hasCursor = last is not null || legacyEnd >= 0;
        var earliestDay = now.ToLocalTime().Date.AddDays(-7);
        return [.. session.Turns.Skip(cursor + 1).Where(t => t.Index > legacyEnd && t.Class != "t" && t.Kind != "compact"
            && (hasCursor || UnknownTimestamp(t) || t.Timestamp.ToLocalTime().Date >= earliestDay) && recorded.Add(t.Uuid))];
    }

    // Claude uses epoch; Codex adds the turn index as ticks when no timestamp exists.
    private static bool UnknownTimestamp(Turn turn) => turn.Timestamp == DateTimeOffset.UnixEpoch
        || turn.Timestamp == DateTimeOffset.UnixEpoch.AddTicks(turn.Index);
}
