using System.Globalization;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

internal static class Imlec
{
    private static readonly Regex Anchor = new(@"^<!-- session:(?<id>\S+) ts:\S+ turns:\d+-(?<end>\d+)(?<extra>[^\r\n]*?) -->\r?$", RegexOptions.Multiline);
    private static readonly Regex Last = new(@"\bson:(\S+)");
    private static readonly Regex TurnId = new(@"^### \[[uah] ([^\]\r\n]+)\]", RegexOptions.Multiline);

    internal static IReadOnlyList<Turn> Pending(string vault, Session session, DateTimeOffset now)
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
        var cursor = last is null ? -1 : session.Turns.ToList().FindIndex(t => t.Uuid == last);
        return [.. session.Turns.Skip(cursor + 1).Where(t => t.Index > legacyEnd && t.Class != "t" && t.Kind != "compact" && recorded.Add(t.Uuid))];
    }
}
