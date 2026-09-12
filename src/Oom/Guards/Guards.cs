using System.Text;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

/// <summary>
/// The single guard chain (spec 6.7). Every untrusted text crosses exactly one
/// instance of this chain, in a fixed order: unicode, then directive. The unicode
/// stage runs first on purpose (scar Y-095): a line-start directive hidden behind
/// U+2028 or an inner BOM must become visible before the directive stage looks at
/// line starts.
/// </summary>
public sealed class Guards
{
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    /// <summary>Zero width and directional formatting characters, plus the BOM.</summary>
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

    /// <summary>
    /// Runs the chain. Returns the cleaned text, the guard classes that fired, whether the
    /// text is refused and the direction it was crossing. A refusal is only ever raised by
    /// the directive stage on compile traffic being <em>admitted</em>: compile is the only
    /// component whose text is turned into files, so a directive-shaped line there
    /// quarantines the whole run (scar Y-026 for the daily input, scar Y-095 for the model
    /// output). Egress is the exception and is spelled out at the verdict below (Y-127).
    /// </summary>
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

        // Direction decides one thing and is reported for another.
        //
        // It decides refusal. Refusal exists so that untrusted text does not become files:
        // a directive-shaped line in what compile is about to parse quarantines the run.
        // Text on egress becomes nothing here — it is handed to a model that answers with
        // a reply the inbound gate still reads — so egress redacts and never refuses.
        // Without this, a directive-shaped line the owner wrote in his own daily would
        // quarantine his compile run on the way out, every evening, forever.
        //
        // And it is reported: the direction rides out on the result, because "we masked a
        // credential that was about to leave this machine" and "we masked a credential on
        // its way into the vault" are different events and the health row has to tell them
        // apart. Nothing recorded it before; the argument was discarded here.
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

            // Zero width, word joiner, the bidi controls and an inner BOM are
            // dropped outright; the line and paragraph separators become real
            // line breaks so the directive stage sees the line they were hiding.
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
