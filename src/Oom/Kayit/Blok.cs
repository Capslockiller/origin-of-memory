using System.Globalization;
using System.Text;

namespace Oom.Contracts;

internal static class Blok
{
    internal static string Compose(Session session, IReadOnlyList<Turn> turns, string project, DateTimeOffset now, Redactor redactor)
    {
        var first = turns[0];
        var end = turns[^1];
        var text = new StringBuilder();
        var source = session.Source == "codex" ? "codex" : "claude-code";
        text.AppendLine($"## kayit · oturum {session.Id} · {source} · {Line(redactor.Mask(project).Text)} · {first.Timestamp.ToLocalTime():HH:mm}–{end.Timestamp.ToLocalTime():HH:mm} · tur {first.Index}–{end.Index}");
        text.AppendLine($"<!-- session:{session.Id} ts:{now.ToString("O", CultureInfo.InvariantCulture)} turns:{first.Index}-{end.Index} source:{session.Source} kayit:v1 son:{end.Uuid} -->");
        foreach (var turn in turns)
        {
            var tools = turn.Class == "a" && turn.Tools is { Count: > 0 }
                ? " · araç: " + string.Join(", ", turn.Tools.GroupBy(n => Line(redactor.Mask(n).Text)).Select(g => $"{g.Key} ×{g.Count()}")) : string.Empty;
            text.AppendLine($"\n### [{turn.Class} {turn.Uuid}] {turn.Timestamp.ToLocalTime():HH:mm}{(turn.Class == "h" ? " · ajan mesajı" : tools)}");
            if (turn.Class == "h") continue;
            var masked = redactor.Mask(turn.Text);
            var body = masked.TimedOut ? $"[maske: zaman aşımı, {turn.Text.Length} kr]" : masked.Text;
            var cap = turn.Class == "u" ? 20_000 : 4_000;
            text.AppendLine(body.Length > cap ? body[..cap] + $" (+{body.Length - cap} kr)" : body);
            if (masked.Count > 0 && !masked.TimedOut) text.AppendLine("[maske:tur]");
        }
        return text.ToString();
    }

    private static string Line(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}
