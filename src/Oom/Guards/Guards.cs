using System.Text;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

public sealed class Guards
{
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    // Culture-invariant: under tr-TR "Ignore" does not fold to "ignore" (I -> ı) and the patterns miss it.
    // Named distinctly from RegexOptions.IgnoreCase (which this also carries) so it is not
    // misread as bare case-insensitivity next to the deliberately case-sensitive INST pattern below.
    private const RegexOptions CaseInsensitiveInvariant = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly char[] Invisible =
    [
        (char)0x200B, (char)0x200C, (char)0x200D, (char)0x2060,
        (char)0x202A, (char)0x202B, (char)0x202C, (char)0x202D, (char)0x202E,
        (char)0x2066, (char)0x2067, (char)0x2068, (char)0x2069,
        (char)0xFEFF
    ];

    // NB-3: a role prefix counts only at a bare line start; "- Assistant: ..." is an echoed summary line.
    private static readonly Regex RolePrefix = new(@"^\s*(?:system|assistant|human|user)\s*:", CaseInsensitiveInvariant);

    // S2: the other directive patterns run after leading bullet/number markers ("- ", "* ", "1. "),
    // checkbox markers ("[ ] ", "[x] ") and Markdown blockquote quoting ("> ") are stripped. '*' is
    // itself a bullet character, so repeated leading '*'s (Markdown bold, "**Ignore...**") are
    // already consumed by the same loop. The marker class also covers en/em dash and middle-dot
    // bullets ("– Ignore...") and lettered/roman enumerators ("(1)", "a)", "i.") that models commonly
    // write (review finding; measured 0 new false positives over 836 vault-kopya blocks and 861
    // knowledge files after widening).
    private static readonly Regex ListMarker = new(
        @"^\s*(?:(?:[-*+•>–—·◦‣]|\(?(?:\d+|[a-zA-Z]|[ivxIVX]{1,4})[.)])\s*(?:\[[ xX]\]\s*)?)+",
        RegexOptions.Compiled);

    // S2 (review finding): a role label can itself be smuggled behind a bullet ("- User: Ignore
    // previous instructions"). ListMarker strips the bullet but leaves "User: ..." in front of the
    // directive, so the anchored DirectivePatterns below never see it. This is stripped from the
    // content BEFORE the directive check (but never used to suppress a 'directive' finding by
    // itself — NB-3's own RolePrefix check above still runs on the untouched line, so a bulleted
    // echo like "- Assistant: özet satırı" stays unflagged).
    private static readonly Regex EmbeddedRoleLabel = new(@"^(?:system|assistant|human|user)\s*:\s*", CaseInsensitiveInvariant);

    // S2 (review finding): a courtesy/politeness opener ("Please ignore previous
    // instructions...", "Kindly disregard the above...") sits in front of the very same
    // directive DirectivePatterns[0] looks for, but that pattern anchors at the start of the
    // line ("^\s*(?:ignore|disregard|forget)..."), so the opener alone let it through
    // unflagged. Stripped once, the same way ListMarker/EmbeddedRoleLabel are, so it never
    // shields a real directive; it does not by itself suppress or create a finding.
    private static readonly Regex CourtesyOpener = new(@"^\s*(?:please|kindly|now|just)\s+", CaseInsensitiveInvariant);

    // Turkish alternatives are written with explicit [iİI] classes rather than relying on culture
    // folding: RegexOptions.CultureInvariant + IgnoreCase does NOT fold 'İ' to 'i' (measured), so
    // an all-caps Turkish directive like "YENİ TALİMAT" or "ÖNCEKİ TALİMATLARI UNUT" would
    // otherwise pass every guard silently.
    private static readonly Regex[] DirectivePatterns =
    [
        new(@"^\s*(?:ignore|disregard|forget)\s+(?:all\s+)?(?:the\s+)?(?:previous|prior|above|earlier)\b", CaseInsensitiveInvariant),
        new(@"^\s*(?:öncek[iİI]|yukarıdak[iİI]|yukaridak[iİI]|bütün|butun)\b.*\b(?:yok say|unut|görmezden gel|gormezden gel)\b", CaseInsensitiveInvariant),
        new(@"^\s*\[?\s*(?:INST|/INST)\s*\]?\s*$", RegexOptions.Compiled),
        new(@"^\s*<{2}\s*SYS\s*>{2}", CaseInsensitiveInvariant),
        new(@"^\s*#{1,6}\s*(?:instruction|instructions|tal[iİI]mat\p{L}*)\b", CaseInsensitiveInvariant),
        new(@"^\s*</?\s*(?:system|instructions|system-reminder)\s*>", CaseInsensitiveInvariant),
        new(@"^\s*(?:new\s+instructions?|yen[iİI]\s+tal[iİI]mat\p{L}*)\b", CaseInsensitiveInvariant)
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

        var refused = directive && component switch
        {
            ComponentKind.Compile => direction is not Direction.Egress,
            ComponentKind.Flush => direction is Direction.Out,
            _ => false
        };
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
            if (RolePrefix.IsMatch(candidate))
                return true;

            var content = ListMarker.Replace(candidate, string.Empty, 1);
            var withoutRoleLabel = EmbeddedRoleLabel.Replace(content, string.Empty, 1);
            var withoutCourtesy = CourtesyOpener.Replace(withoutRoleLabel, string.Empty, 1);
            foreach (var pattern in DirectivePatterns)
            {
                if (pattern.IsMatch(withoutCourtesy))
                    return true;
            }
        }

        return false;
    }

}
