using System.Text;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

public sealed class Guards
{
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    private static readonly char[] Invisible =
    [
        (char)0x200B, (char)0x200C, (char)0x200D, (char)0x2060,
        (char)0x202A, (char)0x202B, (char)0x202C, (char)0x202D, (char)0x202E,
        (char)0x2066, (char)0x2067, (char)0x2068, (char)0x2069,
        (char)0xFEFF
    ];

    private static readonly Regex[] DirectivePatterns =
    [
        new(@"^\s*(?:system|assistant|human|user)\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^\s*(?:ignore|disregard|forget)\s+(?:all\s+)?(?:the\s+)?(?:previous|prior|above|earlier)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^\s*(?:önceki|yukarıdaki|yukaridaki|bütün|butun)\b.*\b(?:yok say|unut|görmezden gel|gormezden gel)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^\s*\[?\s*(?:INST|/INST)\s*\]?\s*$", RegexOptions.Compiled),
        new(@"^\s*<{2}\s*SYS\s*>{2}", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^\s*#{1,6}\s*(?:instruction|instructions|talimat)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^\s*</?\s*(?:system|instructions|system-reminder)\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^\s*(?:new\s+instructions?|yeni\s+talimat)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)
    ];

    public GateResult Gate(string text, Direction direction, ComponentKind component)
    {
        ArgumentNullException.ThrowIfNull(text);
        var findings = new List<string>();

        var cleaned = FoldUnicode(text, out var unicodeTouched);
        if (unicodeTouched)
            findings.Add("unicode");

        var directive = HasDirective(cleaned);
        if (directive)
            findings.Add("directive");

        var refused = directive && component == ComponentKind.Compile && direction is not Direction.Egress;
        return new GateResult(cleaned, findings, refused, direction);
    }

    private static string FoldUnicode(string text, out bool touched)
    {
        var builder = new StringBuilder(text.Length);
        touched = false;
        foreach (var c in text)
        {
            switch (c)
            {
                case '\t' or '\n' or '\r':
                    builder.Append(c);
                    continue;
            }

            if (Invisible.Contains(c))
            {
                touched = true;
                continue;
            }

            if (c is LineSeparator or ParagraphSeparator)
            {
                touched = true;
                builder.Append('\n');
                continue;
            }

            if (!char.IsControl(c))
            {
                builder.Append(c);
                continue;
            }

            touched = true;
            builder.Append(' ');
        }

        return builder.ToString();
    }

    private static bool HasDirective(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var candidate = line.TrimEnd('\r');
            foreach (var pattern in DirectivePatterns)
            {
                if (pattern.IsMatch(candidate))
                    return true;
            }
        }

        return false;
    }

}
