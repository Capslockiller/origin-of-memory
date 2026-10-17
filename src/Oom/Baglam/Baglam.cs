using System.Security.Cryptography;
using System.Text;

namespace Oom.Contracts;

public static class Baglam
{
    public static string Build(string vault, OomSettings settings, DateTimeOffset now, Durum.Result durum, string? nonce = null)
    {
        var fence = nonce ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var (open, close) = (Context.FenceLine("başı", fence), Context.FenceLine("sonu", fence));
        var companion = new Context(settings.Context).CompanionPath(vault);
        var dir = settings.Context.CompanionDir;
        (string Label, string? Body, int CapChars, string? FileRef, bool LineMode)[] specs =
        [
            ("[Durum]", durum.IsRed ? durum.Line : null, 160, null, false),
            ("[Kurallar]", ReadFirstLines(companion, "Kurallar.md", 60), 2_500, $"{dir}/Kurallar.md", true),
            ("[Düzeltmeler]", ReadFirstLines(companion, "Duzeltmeler.md", 30), 1_800, $"{dir}/Duzeltmeler.md", true),
            ("[Kartlar]", ReadFirstLines(companion, Path.Combine("Kartlar", "_dizin.md"), int.MaxValue), 1_000, $"{dir}/Kartlar/_dizin.md", true),
            ("[Son oturum]", ReadFirstParagraph(companion, "Last-Session.md"), 800, $"{dir}/Last-Session.md", false),
            ("[Arama]", SearchLine(vault, new(Context.TotalChars, Context.TotalBytes)), 140, null, false)
        ];

        var remaining = new Context.Room(Context.TotalChars, Context.TotalBytes);
        var builder = new StringBuilder();
        foreach (var (label, rawBody, capChars, fileRef, lineMode) in specs)
        {
            if (string.IsNullOrWhiteSpace(rawBody)) continue;
            var wrapperCost = Context.Room.Of($"{label}\n{open}\n{close}\n");
            if (!remaining.Holds(wrapperCost)) continue;
            var body = label == "[Arama]" ? SearchLine(vault, remaining - wrapperCost) : rawBody;
            if (string.IsNullOrWhiteSpace(body)) continue;
            var fitted = FitBody(Neutralize(body), capChars, remaining - wrapperCost, fileRef, lineMode);
            if (string.IsNullOrWhiteSpace(fitted)) continue;
            var block = $"{label}\n{open}\n{fitted}\n{close}\n";
            if (!remaining.Holds(Context.Room.Of(block))) continue;
            builder.Append(block);
            remaining -= Context.Room.Of(block);
        }
        return builder.ToString();
    }

    private static string? SearchLine(string vault, Context.Room room)
    {
        var full = $"Ara: oom --vault \"{vault}\" retrieve --query \"<terim>\"";
        if (full.Length <= 140 && room.Holds(Context.Room.Of(full + "\n"))) return full;
        const string fallback = "Ara: oom --vault <vault> retrieve --query \"<terim>\"";
        return room.Holds(Context.Room.Of(fallback + "\n")) ? fallback : null;
    }

    private static string? FitBody(string body, int capChars, Context.Room bodyRoom, string? file, bool lineMode)
    {
        var limit = new Context.Room(Math.Min(capChars + 1, bodyRoom.Chars), bodyRoom.Bytes);
        if (limit.Chars <= 1 || limit.Bytes <= 1) return null;
        if (file is null) return Context.Head(body, new Context.Room(limit.Chars - 1, limit.Bytes - 1)).TrimEnd() is { Length: > 0 } h ? h : null;
        if (lineMode && body.Contains('\n'))
        {
            var lineRoom = limit;
            if (Context.FitLines(body, ref lineRoom, file) is { } byLines && !byLines.StartsWith("… [kısaltıldı", StringComparison.Ordinal))
                return byLines;
        }
        var charRoom = limit;
        return Context.Fit(body, capChars + 1, ref charRoom, file);
    }

    private static string? ReadFirstLines(string? companion, string relativePath, int maxLines) =>
        SafeLines(companion, relativePath) is { } lines ? Context.Join(maxLines == int.MaxValue ? lines : [.. lines.Take(maxLines)]) : null;

    private static string? ReadFirstParagraph(string? companion, string relativePath)
    {
        if (SafeLines(companion, relativePath) is not { Length: > 0 } lines) return null;
        var index = 0;
        if (lines[0].Trim() == "---")
            for (index = 1; index < lines.Length; index++)
                if (lines[index].Trim() == "---") { index++; break; }
        var paragraph = new List<string>();
        var hasContentLine = false;
        for (; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) { if (hasContentLine) break; continue; }
            if (!lines[index].TrimStart().StartsWith('#')) hasContentLine = true;
            paragraph.Add(lines[index]);
        }
        return Context.Join([.. paragraph]);
    }

    private static string[]? SafeLines(string? companion, string relativePath)
    {
        try { return Context.Lines(companion, relativePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Neutralize(string text) => string.Join('\n', text.Split('\n').Select(line =>
    {
        var trimmed = line.TrimStart();
        return trimmed.Contains(Context.FencePhrase, StringComparison.Ordinal) || trimmed.StartsWith("--- veri", StringComparison.Ordinal) ? "> " + line : line;
    }));
}
