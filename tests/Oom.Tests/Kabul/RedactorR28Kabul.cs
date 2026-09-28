using Oom.Contracts;

namespace Oom.Tests.Kabul;

/// <summary>
/// Oracle acceptance tests for lane L6e-redactor (SPEC-3.1.0.md ruling R28, layered on top of
/// the S3/B6/B7 coverage RedactorKabul and L1guardredactRedactorScars already own). R28 closes
/// three masking gaps in the current Redactor:
///   1. The known-prefix GitHub-token rule only recognises gh[po]_ (ghp_/gho_); it misses
///      ghu_/ghs_/ghr_, including the long, variable-length ghs_ installation-token form
///      GitHub documents.
///   2. PEM private-key blocks (-----BEGIN [A-Z ]*PRIVATE KEY----- ... -----END [A-Z ]*PRIVATE
///      KEY-----) are not masked at all today.
///   3. The password/key/token keyword rule only recognises snake_case / lower-case keywords
///      (e.g. "key", "password", "api_key" via the underscore boundary); a camelCase or
///      PascalCase compound identifier (apiKey, secretKey, accessToken, panelKey,
///      adminPassword, dbPassword, clientSecret) glues past the existing "not preceded by a
///      letter" boundary and is never even considered a keyword.
///
/// Every positive value below is SYNTHETIC, built at runtime from a fixed filler alphabet
/// (Fill/FillUpper) or a repeating letters-only pattern — never a literal real token, key or
/// password — so no scanner flags this source file and no genuine secret is ever printed on a
/// failing assertion. Written to FAIL against the unchanged Redactor.cs (gh[po]_-only prefix
/// rule, no PEM handling, snake/lower-case-only keyword boundary) and to PASS once R28 lands.
/// </summary>
public sealed class RedactorR28Kabul
{
    private static string Fill(int length)
        => string.Concat(Enumerable.Range(0, length).Select(i => "0123456789abcdefghijklmnopqrstuvwxyz"[i % 36]));

    private static string SyntheticPemBody(int lines = 3, int lineLength = 48)
        => string.Join('\n', Enumerable.Range(0, lines).Select(i => Fill(lineLength) + i));

    // ------------------------------------------------------------------
    // R28 item 1 — GitHub token prefixes ghp_/gho_/ghu_/ghs_/ghr_
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> GitHubTokenPrefixes()
    {
        yield return new object[] { "ghp_" };
        yield return new object[] { "gho_" };
        yield return new object[] { "ghu_" };
        yield return new object[] { "ghs_" };
        yield return new object[] { "ghr_" };
    }

    [Theory(DisplayName = "R28-1 · GitHub jeton önekleri ghp_/gho_/ghu_/ghs_/ghr_ maskelenir, önek görünür kalır")]
    [MemberData(nameof(GitHubTokenPrefixes))]
    public void Mask_GitHubTokenPrefixes_AreMasked(string prefix)
    {
        var secret = prefix + Fill(36);
        var text = $"kayıt: {secret}\ndevamı burada.";
        var result = new Redactor().Mask(text);

        Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
        Assert.Contains(prefix + "****(maskelendi)", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, result.Count);
    }

    [Fact(DisplayName = "R28-1 · uzun/değişken uzunluklu ghs_ kurulum jetonu (GitHub'ın belgelediği biçim) maskelenir")]
    public void Mask_LongVariableLengthGhsInstallationToken_IsMasked()
    {
        // GitHub documents ghs_ (installation access token) as variable-length, well past the
        // 36-char minimum the other prefixed tokens use. 80 chars here stands in for "many
        // chars"; the pattern must not impose an upper bound.
        var secret = "ghs_" + Fill(80);
        var text = $"kurulum notu: {secret} bitti.";
        var result = new Redactor().Mask(text);

        Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
        Assert.Contains("ghs_****(maskelendi)", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, result.Count);
    }

    // ------------------------------------------------------------------
    // R28 item 2 — PEM private-key blocks
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> PemPrivateKeyHeaders()
    {
        yield return new object[] { "PRIVATE KEY" };
        yield return new object[] { "RSA PRIVATE KEY" };
        yield return new object[] { "ENCRYPTED PRIVATE KEY" };
    }

    [Theory(DisplayName = "R28-2 · PEM özel anahtar bloğu (-----BEGIN ... PRIVATE KEY----- .. -----END ... PRIVATE KEY-----) baştan sona maskelenir")]
    [MemberData(nameof(PemPrivateKeyHeaders))]
    public void Mask_PemPrivateKeyBlock_IsMaskedWhole(string header)
    {
        var body = SyntheticPemBody();
        var block = $"-----BEGIN {header}-----\n{body}\n-----END {header}-----";
        var text = $"yedek notu:\n{block}\nnot bitti.";

        var result = new Redactor().Mask(text);

        Assert.DoesNotContain(block, result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(body, result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("END", result.Text, StringComparison.Ordinal);
        Assert.Contains("maskelendi", result.Text, StringComparison.Ordinal);
        Assert.Contains("yedek notu:", result.Text, StringComparison.Ordinal);
        Assert.Contains("not bitti.", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, result.Count);
    }

    // ------------------------------------------------------------------
    // R28 item 3 — camelCase / PascalCase key names in the keyword rule
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> CompoundKeywords()
    {
        var names = new[]
        {
            ("apiKey", "ApiKey"),
            ("secretKey", "SecretKey"),
            ("accessToken", "AccessToken"),
            ("panelKey", "PanelKey"),
            ("adminPassword", "AdminPassword"),
            ("dbPassword", "DbPassword"),
            ("clientSecret", "ClientSecret"),
        };
        foreach (var (camel, pascal) in names)
        foreach (var separator in new[] { ":", "=" })
        {
            yield return new object[] { camel, separator };
            yield return new object[] { pascal, separator };
        }
    }

    [Theory(DisplayName = "R28-3 · camelCase/PascalCase anahtar adları (apiKey/secretKey/accessToken/panelKey/adminPassword/dbPassword/clientSecret) ':'/'=' sonrası değerle maskelenir")]
    [MemberData(nameof(CompoundKeywords))]
    public void Mask_CamelCaseAndPascalCaseKeyNames_AreMasked(string keyword, string separator)
    {
        const string value = "SentetikDeger9x";
        var text = $"kurulum notu: {keyword}{separator}{value} — bu satır sentetiktir.";

        var result = new Redactor().Mask(text);

        Assert.DoesNotContain(value, result.Text, StringComparison.Ordinal);
        Assert.Contains("maskelendi", result.Text, StringComparison.Ordinal);
        Assert.True(result.Count >= 1, $"expected at least 1 masked value, got {result.Count}");
    }

    [Fact(DisplayName = "R28-3 · ters tırnaklı camelCase biçim de (B7 ile aynı mekanizma) maskelenir")]
    public void Mask_CamelCaseKeyName_BacktickForm_IsMasked()
    {
        var result = new Redactor().Mask("kurulum notu: clientSecret`SentetikDeger1` — devam eder.");

        Assert.DoesNotContain("SentetikDeger1", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, result.Count);
    }

    // ------------------------------------------------------------------
    // Negative tests — false-positive guards R28 must not break.
    // ------------------------------------------------------------------

    [Theory(DisplayName = "R28 · yanlış pozitif korumaları: sıradan kelimeler maskelenmeden kalır")]
    [InlineData("apikeys kelimesi burada sadece örnek bir isim, başka bir şey değil.")]
    [InlineData("Bağlantı passwordless girişle kuruldu, ekstra adım yok.")]
    public void Mask_OrdinaryLookalikeWords_AreNotMasked(string text)
    {
        var result = new Redactor().Mask(text);

        Assert.Equal(0, result.Count);
        Assert.Equal(text, result.Text);
    }

    [Fact(DisplayName = "R28-1 · negatif: 'ghx_' öneki tanınan bir GitHub jeton öneki değildir")]
    public void Mask_UnknownGhPrefix_IsNotMasked()
    {
        var lookalike = "ghx_" + Fill(36);
        var text = $"kayıt: {lookalike} bitti.";

        var result = new Redactor().Mask(text);

        Assert.Equal(0, result.Count);
        Assert.Contains(lookalike, result.Text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R28-2 · negatif: BEGIN PUBLIC KEY bloğu (özel anahtar değil) maskelenmeden kalır")]
    public void Mask_PemPublicKeyBlock_IsNotMasked()
    {
        // Letters-only, digit-free body so the PRE-EXISTING near-'KEY'-keyword long-value rule
        // (S3/B6, unrelated to R28 and out of scope for this lane) cannot fire either:
        // LooksRandom requires at least one digit in the candidate value. That isolates this
        // assertion to the NEW PEM rule alone — it must require the literal "PRIVATE KEY" text
        // and must not treat a "PUBLIC KEY" block as one.
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var body = string.Join('\n', Enumerable.Range(0, 3)
            .Select(i => string.Concat(Enumerable.Range(0, 48).Select(j => alphabet[(i * 7 + j) % alphabet.Length]))));
        var block = $"-----BEGIN PUBLIC KEY-----\n{body}\n-----END PUBLIC KEY-----";
        var text = $"paylaşılan not:\n{block}\ndevamı burada.";

        var result = new Redactor().Mask(text);

        Assert.Equal(0, result.Count);
        Assert.Equal(text, result.Text);
    }

    // ------------------------------------------------------------------
    // Idempotence — masking already-masked R28 output changes nothing
    // (SPEC-wide contract: B6 re-runs Mask on already-masked text at
    // flush/index/retrieve time; see L1guardredactRedactorScars).
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> IdempotencyInputs()
    {
        yield return new object[] { "kayıt: " + "ghs_" + Fill(60) };
        yield return new object[] { "apiKey: SentetikDeger9x devam eder." };
        yield return new object[] { "ClientSecret=AnotherSentetik1 bitti." };
        yield return new object[]
        {
            $"not:\n-----BEGIN PRIVATE KEY-----\n{SyntheticPemBody()}\n-----END PRIVATE KEY-----\ndevam."
        };
    }

    [Theory(DisplayName = "R28 · idempotency: Mask(Mask(x).Text) metni değiştirmez ve Count=0 döner")]
    [MemberData(nameof(IdempotencyInputs))]
    public void Mask_IsIdempotent_AcrossR28FormShapes(string input)
    {
        var redactor = new Redactor();
        var once = redactor.Mask(input);
        var twice = redactor.Mask(once.Text);

        Assert.Equal(once.Text, twice.Text);
        Assert.Equal(0, twice.Count);

        var thrice = redactor.Mask(twice.Text);
        Assert.Equal(twice.Text, thrice.Text);
        Assert.Equal(0, thrice.Count);
    }
}
