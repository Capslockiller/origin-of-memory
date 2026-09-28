using System.Text;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

/// <summary>
/// Result of a <see cref="Redactor.Mask"/> call: the text with every matched secret
/// replaced by its masked form, and how many values were masked. <paramref name="TimedOut"/>
/// is true when a pattern hit its match timeout and the whole text was masked fail-closed,
/// so a caller can tell that apart from one ordinary masked secret (review finding #7).
/// </summary>
public sealed record RedactionResult(string Text, int Count, bool TimedOut = false);

/// <summary>
/// High-confidence secret masker (SPEC-3.1.0.md S3, B6, B7, R28). Intended for flush output,
/// indexing and retrieve/MCP output (B6); wired by the W2 lanes. Every pattern runs on the
/// original text; overlapping hits collapse to the earliest, longest one, so a value is
/// masked and counted exactly once. A masked value renders as '&lt;prefix&gt;****(maskelendi)'.
/// Every pattern carries a match timeout; a timeout fails closed (the whole text is masked)
/// rather than ever returning a secret unmasked (review finding, S2's threat model).
///
/// R28 adds three high-confidence forms on top of S3/B6/B7: the remaining GitHub token
/// prefixes (ghu_/ghs_/ghr_, alongside the already-handled ghp_/gho_), a whole-block PEM
/// private-key rule (the block itself is the secret, so it is masked in full rather than
/// leaving a 'value' group visible), and camelCase/PascalCase compound keyword names
/// (apiKey, secretKey, accessToken, panelKey, adminPassword, dbPassword, clientSecret) that
/// glue past the snake/lower-case keyword rules' "not preceded by a letter" boundary.
/// </summary>
public sealed class Redactor
{
    private const string Mark = "****(maskelendi)";
    private const int KeywordDistance = 40;
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    // S3: known token formats. The 'prefix' group stays visible.
    // R28: gh[po]_ (ghp_/gho_) widened to gh[oprsu]_ so it also covers ghu_/ghs_/ghr_. The
    // {36,} minimum is intentionally unbounded above — GitHub documents ghs_ (installation
    // access token) as variable-length, sometimes well past 36 characters, and this pattern
    // already accepted any length at or above the minimum for every prefix.
    private static readonly Regex[] PrefixedTokens =
    [
        new(@"\b(?<prefix>gh[oprsu]_)[A-Za-z0-9]{36,}", Options, MatchTimeout),
        new(@"\b(?<prefix>github_pat_)[A-Za-z0-9_]{20,}", Options, MatchTimeout),
        new(@"\b(?<prefix>sk-ant-)[A-Za-z0-9_-]{20,}", Options, MatchTimeout),
        new(@"\b(?<prefix>AKIA)[0-9A-Z]{16}\b", Options, MatchTimeout)
    ];

    // R28: a PEM private-key block, BEGIN to END, masked in full — unlike the prefixed-token
    // rule above, there is no separate visible 'prefix' here: the block's very shape (that a
    // private key lives at this spot at all) is itself sensitive, so nothing of it survives,
    // not even the BEGIN/END marker text. '[\s\S]*?' (not '.*?' with Singleline) crosses the
    // body's newlines without changing Options for every other pattern in this class. The
    // literal "PRIVATE KEY" (not "PUBLIC KEY") keeps a public-key block out of this rule.
    private static readonly Regex PemPrivateKey = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
        Options, MatchTimeout);

    // S3: user:pass@ inside a URL; the scheme stays visible. Matches up to the LAST '@'
    // before the host so a password containing '@' (e.g. "u:p4ss@w0rd@host") is masked whole.
    // The scheme, user and password parts are all length-bounded (review finding: the old
    // unbounded '[^\s]+:[^\s]+' next to a lookahead is quadratic — measured 143 s on 1 MB of
    // 'https://a:b' repeated). The user/password classes also exclude '/', '?' and '#' so a
    // URL with a port and a path/query (e.g. "https://host:8080/path?x=a@b.com") is not
    // mistaken for embedded credentials (review finding: that host/port/path was hidden).
    private static readonly Regex UrlCredentials = new(
        @"(?<prefix>[A-Za-z][A-Za-z0-9+.-]{0,31}://)[^\s/@:?#]{1,128}:[^\s/?#]{1,256}(?=@[^\s@/]+(?:[/:?#]|\s|$))",
        Options, MatchTimeout);

    // B7: a password keyword, then ':' / '=' / a backtick, then a token of at least 6 characters.
    // Only the token is masked. Turkish keywords take suffixes ("şifresi"); English ones stand alone ("passed" is not one).
    // Case-insensitive-invariant so 'İ' folds explicitly via [iİI] (invariant IgnoreCase alone
    // does not fold dotted/dotless Turkish i); "passwords"/"passwd" are also keywords.
    // "password"/"passwd"/"pwd" only reject a LOWERCASE letter immediately before them, so an
    // uppercase env-var prefix ("PGPASSWORD=...") is still caught (review finding); the bare
    // "pass" alternative stays strict — no letter AND no hyphen before it ("one-pass" is not a
    // keyword there) — and is marked with a named group so Mask() can demand its value contain
    // a digit or symbol (see LooksCredible), because a plain English word after "pass:" is a
    // frequent false positive ("Second pass: rebuild index") that "password:"/"pwd:" do not share.
    // The optional closing quote lets a JSON/YAML key ('"password": "value"') match too.
    // The '\p{Ll}' lookbehinds are wrapped in '(?-i:...)' to force case-SENSITIVE evaluation:
    // under plain RegexOptions.IgnoreCase, .NET case-folds a Unicode category class too, so
    // '[\p{Ll}]' silently matches ANY letter (uppercase included, since it case-folds to a
    // lowercase one) and the env-var case never actually got through (measured while adding
    // the test for it) — '(?-i:...)' is the documented way to keep one fragment case-sensitive
    // inside an otherwise case-insensitive pattern.
    // R28: camelCase/PascalCase compound identifiers (apiKey, secretKey, accessToken,
    // panelKey, adminPassword, dbPassword, clientSecret, and their PascalCase forms). Every
    // other alternative above is matched case-insensitively (QuotedPassword/PlainPassword both
    // carry RegexOptions.IgnoreCase), which would blur "apiKey" into also accepting the
    // all-lowercase run-on "apikey" — exactly the "apikeys" false positive the negative tests
    // guard against. '(?-i:...)' (same technique the "PGPASSWORD" lookbehind above already
    // uses) forces exact-case matching for just this alternative, so only the literal
    // camelCase/PascalCase spellings match; the shared '(?!\p{L})' boundary that follows still
    // rejects a longer run-on word like "apiKeys" or "clientSecretly".
    private const string CompoundKeyword =
        @"|(?<!\p{L})(?-i:apiKey|ApiKey|secretKey|SecretKey|accessToken|AccessToken" +
        @"|panelKey|PanelKey|adminPassword|AdminPassword|dbPassword|DbPassword" +
        @"|clientSecret|ClientSecret)";

    private const string PasswordKeyword =
        @"(?:(?<!\p{L})(?:[şŞsS][iİI]fre|parola)\p{L}*" +
        @"|(?<!(?-i:[\p{Ll}]))passwords?" +
        @"|(?<!(?-i:[\p{Ll}]))passwd" +
        @"|(?<!(?-i:[\p{Ll}]))pwd" +
        CompoundKeyword +
        @"|(?<barepass>(?<![\p{L}-])pass))" +
        @"(?!\p{L})[""']?\**[ \t]*";
    // The (?!\*{4}\() guards keep Mask idempotent: without them, a second pass over already-masked
    // output re-matches the mask literal itself as a new "value" and corrupts it (measured:
    // re-masking accumulates extra ')' / '*' characters and a nonzero Count every pass). PlainPassword
    // needs the guard TWICE — once right after the separator and once after the decorative '\**'
    // — because the two failure shapes differ: "password=****(maskelendi)" has the mark's own 4
    // stars touching the separator with nothing to stop the decorative '\**' from swallowing them
    // (caught by the first guard, before '\**' runs at all), while "**Şifre:** ****(maskelendi)"
    // has a real Markdown '**' separated from the mark by a space, so '\**' legitimately consumes
    // only the decorative stars and the mark's own 4 stars are still intact afterwards (caught by
    // the second guard). A single guard in only one of the two spots left the other shape corrupting
    // on every re-pass (review finding).
    // '[ \t]*' (not '\s*') so a keyword at the end of one line cannot reach across a newline and
    // capture a token that starts the next line (review finding).
    private static readonly Regex QuotedPassword = new(
        PasswordKeyword + @"(?:[:=]\**[ \t]*)?\(?`(?!\*{4}\()(?<value>[^\s`]{6,})`",
        Options | RegexOptions.IgnoreCase, MatchTimeout);
    private static readonly Regex PlainPassword = new(
        PasswordKeyword + @"[:=](?!\*{4}\()\**[ \t]*(?!\*{4}\()(?<value>[^\s`]{5,}[^\s`.,;:)\]}""'*])",
        Options | RegexOptions.IgnoreCase, MatchTimeout);

    // S3: 32+ hex or base64 near a key word. '\p{L}*' + the trailing (?!\p{L}) give the keyword a
    // real right boundary (a bare "key"/"token" no longer partially matches inside an unrelated
    // word) while still accepting Turkish/English suffixes ("anahtarı", "tokens"); dotted İ folds
    // explicitly via [iİI], same reasoning as PasswordKeyword above.
    private static readonly Regex Keyword = new(
        @"(?<!\p{L})(?:anahtar\p{L}*|[şŞsS][iİI]fre\p{L}*|passwords?|keys?|tokens?)(?!\p{L})",
        Options | RegexOptions.IgnoreCase, MatchTimeout);
    private static readonly Regex LongValue = new(
        @"(?<![A-Za-z0-9+/=])(?:[0-9a-fA-F]{32,}|[A-Za-z0-9+/]{32,}={0,2})(?![A-Za-z0-9+/=])",
        Options, MatchTimeout);

    private readonly Regex[] _prefixedTokens;
    private readonly Regex _pemPrivateKey;
    private readonly Regex _urlCredentials;
    private readonly Regex _quotedPassword;
    private readonly Regex _plainPassword;
    private readonly Regex _keyword;
    private readonly Regex _longValue;

    public Redactor() : this(MatchTimeout)
    {
    }

    /// <summary>
    /// Test seam: the same patterns with another match timeout. The default timeout reuses
    /// the shared compiled patterns; any other value builds its own copies.
    /// </summary>
    public Redactor(TimeSpan matchTimeout)
    {
        _prefixedTokens = PrefixedTokens.Select(x => WithTimeout(x, matchTimeout)).ToArray();
        _pemPrivateKey = WithTimeout(PemPrivateKey, matchTimeout);
        _urlCredentials = WithTimeout(UrlCredentials, matchTimeout);
        _quotedPassword = WithTimeout(QuotedPassword, matchTimeout);
        _plainPassword = WithTimeout(PlainPassword, matchTimeout);
        _keyword = WithTimeout(Keyword, matchTimeout);
        _longValue = WithTimeout(LongValue, matchTimeout);
    }

    private static Regex WithTimeout(Regex pattern, TimeSpan matchTimeout) =>
        pattern.MatchTimeout == matchTimeout ? pattern : new Regex(pattern.ToString(), pattern.Options, matchTimeout);

    public RedactionResult Mask(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            var hits = new List<(int Start, int Length, string Prefix)>();
            foreach (var pattern in _prefixedTokens)
                AddMatches(hits, pattern, text);
            AddMatches(hits, _pemPrivateKey, text);
            AddMatches(hits, _urlCredentials, text);
            AddMatches(hits, _quotedPassword, text);
            AddMatches(hits, _plainPassword, text);
            AddLongValues(hits, text);
            if (hits.Count == 0)
                return new RedactionResult(text, 0);

            var builder = new StringBuilder(text.Length);
            var position = 0;
            var count = 0;
            foreach (var hit in hits.OrderBy(x => x.Start).ThenByDescending(x => x.Length))
            {
                if (hit.Start < position)
                    continue;
                builder.Append(text, position, hit.Start - position).Append(hit.Prefix).Append(Mark);
                position = hit.Start + hit.Length;
                count++;
            }

            builder.Append(text, position, text.Length - position);
            return new RedactionResult(builder.ToString(), count);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed (review finding): a pathological input must never come back unmasked
            // just because a pattern gave up. The whole text is treated as one masked value, and
            // TimedOut tells the caller this was not an ordinary match (review finding #7).
            return new RedactionResult(Mark, 1, TimedOut: true);
        }
    }

    // A 'value' group is masked alone; otherwise the whole match is, behind its 'prefix'. A match
    // whose keyword was the bare, unqualified "pass" (the 'barepass' group) is dropped unless its
    // value looks like a credential rather than a plain word (review finding: "one-pass: digest
    // hesapla" / "Second pass: rebuild index" are prose, not secrets — "password:"/"pwd:" carry no
    // such requirement because a bare English dictionary word almost never follows them literally).
    private static void AddMatches(List<(int, int, string)> hits, Regex pattern, string text)
    {
        foreach (Match match in pattern.Matches(text))
        {
            var value = match.Groups["value"];
            if (match.Groups["barepass"].Success && value.Success && !LooksCredible(value.Value))
                continue;
            hits.Add(value.Success
                ? (value.Index, value.Length, string.Empty)
                : (match.Index, match.Length, match.Groups["prefix"].Value));
        }
    }

    // A credential-shaped value carries at least one character that a plain word does not:
    // a digit or another non-letter (punctuation/symbol). Rejects "digest", "rebuild"; accepts
    // "hunter22x", "AnotherSecret1".
    private static bool LooksCredible(string value) => value.Any(c => !char.IsLetter(c));

    private void AddLongValues(List<(int, int, string)> hits, string text)
    {
        var keywords = _keyword.Matches(text);
        if (keywords.Count == 0)
            return;

        foreach (Match match in _longValue.Matches(text))
        {
            if (!LooksRandom(match.Value))
                continue;
            var end = match.Index + match.Length;
            var near = keywords.Any(k =>
                (k.Index + k.Length <= match.Index && match.Index - (k.Index + k.Length) <= KeywordDistance) ||
                (end <= k.Index && k.Index - end <= KeywordDistance));
            if (near)
                hits.Add((match.Index, match.Length, string.Empty));
        }
    }

    // A hex value carries a digit; a base64 value carries a digit, a lower and an upper letter.
    // This excludes values with no digit at all (plain words, letters-and-slashes paths) and
    // all-lowercase-or-all-uppercase base64-shaped runs; it does NOT exclude every path or every
    // word — a path segment or hex-like id that happens to carry a digit can still be masked.
    private static bool LooksRandom(string value)
    {
        if (!value.Any(char.IsAsciiDigit))
            return false;
        return value.All(char.IsAsciiHexDigit) ||
               (value.Any(char.IsAsciiLetterLower) && value.Any(char.IsAsciiLetterUpper));
    }
}
