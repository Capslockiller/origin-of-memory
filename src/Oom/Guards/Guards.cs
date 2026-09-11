using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

/// <summary>
/// The single guard chain (spec 6.7). Every untrusted text crosses exactly one
/// instance of this chain, in a fixed order: unicode, secret, pii, directive.
/// The unicode stage runs first on purpose (scar Y-095): a line-start directive
/// hidden behind U+2028 or an inner BOM must become visible before the directive
/// stage looks at line starts.
/// </summary>
public sealed class Guards
{
    private const string UserSegmentToken = "%USER%";
    private const string UsersFolder = "Users";
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

    private static readonly (string Class, Regex Pattern)[] SecretPatterns =
    [
        ("anthropic-key", new Regex(@"sk-ant-[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled)),
        ("openai-key", new Regex(@"\bsk-(?!ant-)[A-Za-z0-9]{20,}\b", RegexOptions.Compiled)),
        ("github-token", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{20,}\b", RegexOptions.Compiled)),
        ("aws-access-key", new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        ("aws-secret", new Regex(@"(?i)\baws_secret_access_key\s*[:=]\s*\S{16,}", RegexOptions.Compiled)),
        ("google-key", new Regex(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.Compiled)),
        ("slack-token", new Regex(@"\bxox[baprs]-[A-Za-z0-9\-]{10,}", RegexOptions.Compiled)),
        ("jwt", new Regex(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.Compiled)),
        ("private-key", new Regex(@"-----BEGIN(?: [A-Z]+)* PRIVATE KEY-----", RegexOptions.Compiled)),
        ("password", new Regex(@"(?i)\b(?:password|passwd|parola|sifre|şifre)\s*[:=]\s*\S{6,}", RegexOptions.Compiled)),
        ("bearer", new Regex(@"(?i)\bbearer\s+[A-Za-z0-9._\-]{20,}", RegexOptions.Compiled)),
        ("connection-string", new Regex(@"(?i)\b(?:data source|server)\s*=[^;]{1,120};[^;]{0,120}(?:password|pwd)\s*=[^;\s]+", RegexOptions.Compiled))
    ];

    private static readonly Regex TurkishIban = new(@"\bTR\d{2}[ ]?(?:\d{4}[ ]?){5}\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex NationalId = new(@"(?<![0-9])[1-9][0-9]{10}(?![0-9])", RegexOptions.Compiled);
    private static readonly Regex TaxId = new(@"(?<![0-9])[0-9]{10}(?![0-9])", RegexOptions.Compiled);
    private static readonly Regex CardNumber = new(@"(?<![0-9])(?:[0-9]{4}[ \-]){3}[0-9]{4}(?![0-9])", RegexOptions.Compiled);
    private static readonly Regex Phone = new(@"(?<![0-9])(?:\+90|0)[ ]?5[0-9]{2}[ \-.]?[0-9]{3}[ \-.]?[0-9]{2}[ \-.]?[0-9]{2}(?![0-9])", RegexOptions.Compiled);
    private static readonly Regex Plate = new(@"\b(?:0[1-9]|[1-7][0-9]|8[01])[ ]?[A-Z]{1,3}[ ]?[0-9]{2,5}\b", RegexOptions.Compiled);

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

        cleaned = MaskSecrets(cleaned, out var secretTouched);
        if (secretTouched)
            findings.Add("secret");

        cleaned = MaskPersonalData(cleaned, out var personalTouched);
        if (personalTouched)
            findings.Add("pii");

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

    /// <summary>
    /// Canonical form of a Windows path for guard comparisons. Short (8.3) segments
    /// are expanded when the path exists; the user-name segment is replaced by a
    /// token because that is exactly the segment Windows shortens (RUNNER~1) and
    /// comparing it between processes is meaningless (scar Y-080).
    /// </summary>
    public string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try
        {
            expanded = Path.GetFullPath(expanded);
        }
        catch (ArgumentException)
        {
            // Keep the caller's text when it is not a well-formed path.
        }

        expanded = ExpandShortPath(expanded);
        expanded = expanded.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (expanded.Length > 3)
            expanded = expanded.TrimEnd(Path.DirectorySeparatorChar);

        var segments = expanded.Split(Path.DirectorySeparatorChar);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!segments[i].Equals(UsersFolder, StringComparison.OrdinalIgnoreCase))
                continue;
            segments[i + 1] = UserSegmentToken;
            break;
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
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

    private static string MaskSecrets(string text, out bool touched)
    {
        var masked = text;
        var hit = false;
        foreach (var (className, pattern) in SecretPatterns)
        {
            masked = pattern.Replace(masked, _ =>
            {
                hit = true;
                return $"[SIR:{className}]";
            });
        }

        touched = hit;
        return masked;
    }

    private static string MaskPersonalData(string text, out bool touched)
    {
        var hit = false;
        var masked = TurkishIban.Replace(text, match => IsIbanValid(match.Value) ? Mark("iban", ref hit) : match.Value);
        masked = CardNumber.Replace(masked, match => IsLuhnValid(match.Value) ? Mark("kart", ref hit) : match.Value);
        masked = Phone.Replace(masked, _ => Mark("telefon", ref hit));
        masked = NationalId.Replace(masked, match => IsNationalIdValid(match.Value) ? Mark("tckn", ref hit) : match.Value);
        masked = TaxId.Replace(masked, match => IsTaxIdValid(match.Value) ? Mark("vkn", ref hit) : match.Value);
        masked = Plate.Replace(masked, _ => Mark("plaka", ref hit));
        touched = hit;
        return masked;
    }

    private static string Mark(string className, ref bool hit)
    {
        hit = true;
        return $"[KVK:{className}]";
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

    private static bool IsNationalIdValid(string value)
    {
        var digits = value.Select(c => c - '0').ToArray();
        if (digits.Length != 11 || digits[0] == 0)
            return false;

        var odd = digits[0] + digits[2] + digits[4] + digits[6] + digits[8];
        var even = digits[1] + digits[3] + digits[5] + digits[7];
        var tenth = ((odd * 7) - even) % 10;
        if (tenth < 0)
            tenth += 10;

        return tenth == digits[9] && digits.Take(10).Sum() % 10 == digits[10];
    }

    private static bool IsTaxIdValid(string value)
    {
        var digits = value.Select(c => c - '0').ToArray();
        if (digits.Length != 10)
            return false;

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var digit = (digits[i] + 10 - (i + 1)) % 10;
            sum += digit == 9 ? digit : (digit * (1 << (9 - i))) % 9;
        }

        return (10 - (sum % 10)) % 10 == digits[9];
    }

    private static bool IsIbanValid(string value)
    {
        var compact = value.Replace(" ", string.Empty).ToUpperInvariant();
        if (compact.Length != 26)
            return false;

        var rearranged = compact[4..] + compact[..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            if (char.IsDigit(c))
                remainder = ((remainder * 10) + (c - '0')) % 97;
            else if (c is >= 'A' and <= 'Z')
                remainder = ((remainder * 100) + (c - 'A' + 10)) % 97;
            else
                return false;
        }

        return remainder == 1;
    }

    private static bool IsLuhnValid(string value)
    {
        var digits = value.Where(char.IsDigit).Select(c => c - '0').Reverse().ToArray();
        if (digits.Length is < 13 or > 19)
            return false;

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var digit = digits[i];
            if (i % 2 == 1)
            {
                digit *= 2;
                if (digit > 9)
                    digit -= 9;
            }

            sum += digit;
        }

        return sum % 10 == 0;
    }

    private static string ExpandShortPath(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.Contains('~'))
            return path;
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        var buffer = new StringBuilder(1024);
        var written = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        return written is 0 || written > buffer.Capacity ? path : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);
}
