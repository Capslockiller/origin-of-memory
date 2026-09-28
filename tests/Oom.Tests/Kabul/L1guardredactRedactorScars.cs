using Oom.Contracts;

namespace Oom.Tests.Kabul;

/// <summary>
/// New file added by the L1-guard-redact lane's final-fix pass (R3: lanes may add their own
/// new files under tests/Oom.Tests/Kabul/, never edit the existing oracle files). RedactorKabul
/// checks that known secret shapes get masked (presence, not precision); these tests pin
/// specific Redactor.Mask branches that a future change could silently break: idempotency
/// (review finding, B6 re-runs Mask on already-masked text at flush/index/retrieve time),
/// overlap collapse, trailing-punctuation preservation, Markdown-bold keywords, the English
/// stand-alone-word guard, the LooksRandom digit requirement, and UrlCredentials leaving the
/// scheme visible.
/// </summary>
public sealed class L1guardredactRedactorScars
{
    private static string Fill(int length)
        => string.Concat(Enumerable.Range(0, length).Select(i => "0123456789abcdefghijklmnopqrstuvwxyz"[i % 36]));

    public static IEnumerable<object[]> IdempotencyInputs()
    {
        yield return new object[] { "kayıt: " + "ghp_" + Fill(36) };
        yield return new object[] { "AKIA" + Fill(16).ToUpperInvariant() + " burada." };
        yield return new object[] { "panel bağlantısı: https://operator:Kule9GizliDeger@panel.example.com/durum sayfasında." };
        yield return new object[] { "şifre: gizliDeger9x" };
        yield return new object[] { "password=hunter22" };
        yield return new object[] { "parola=AnotherSecret1" };
        yield return new object[] { "pwd`BacktickToken1`" };
        // "MERCAN" is a neutral synthetic panel name (same convention GetirmeScars.cs/
        // IndeksScars.cs already use), not a real project word.
        yield return new object[] { "MERCAN paneli için anahtarı: " + string.Concat(Enumerable.Range(0, 48).Select(i => "0123456789abcdef"[i % 16])) + " — bugün sızdı." };
        // Review finding: the Markdown-bold keyword form was NOT idempotent (the mark's own "****"
        // corrupted into extra ')' / '*' characters on the second pass).
        yield return new object[] { "**Şifre:** gizliDeger9x" };
        yield return new object[] { "**Password:** hunter22x" };
    }

    [Theory(DisplayName = "Idempotency · Mask(Mask(x).Text) metni değiştirmez ve Count=0 döner (S3+B7 formları)")]
    [MemberData(nameof(IdempotencyInputs))]
    public void Mask_IsIdempotent_AcrossKnownFormShapes(string input)
    {
        var redactor = new Redactor();
        var once = redactor.Mask(input);
        var twice = redactor.Mask(once.Text);

        Assert.Equal(once.Text, twice.Text);
        Assert.Equal(0, twice.Count);

        // A third pass must also be a no-op — guards against a fix that only special-cases n=2.
        var thrice = redactor.Mask(twice.Text);
        Assert.Equal(twice.Text, thrice.Text);
        Assert.Equal(0, thrice.Count);
    }

    [Fact(DisplayName = "Overlap · en erken + en uzun eşleşme kazanır, bir kez sayılır")]
    public void Mask_OverlappingHits_CollapseToEarliestLongest()
    {
        // "anahtar: <hex>" both satisfies the keyword-near-hex rule AND, if it also looked like a
        // plain password value, could double-count. Here the hex value alone must be masked once.
        var hex = string.Concat(Enumerable.Range(0, 32).Select(i => "0123456789abcdef"[i % 16]));
        var text = $"anahtar: {hex} bitti.";
        var result = new Redactor().Mask(text);

        Assert.Equal(1, result.Count);
        Assert.DoesNotContain(hex, result.Text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Noktalama · 'password: hunter22.' sondaki nokta korunur")]
    public void Mask_TrailingSentencePunctuation_IsPreserved()
    {
        var result = new Redactor().Mask("password: hunter22.");

        Assert.EndsWith(".", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter22", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, result.Count);
    }

    [Fact(DisplayName = "Markdown kalın · '**Şifre:** deger' yakalanır")]
    public void Mask_MarkdownBoldKeyword_IsMasked()
    {
        var result = new Redactor().Mask("**Şifre:** gizliDeger9x bitti.");

        Assert.DoesNotContain("gizliDeger9x", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, result.Count);
    }

    [Theory(DisplayName = "Tek başına İngilizce kelime · 'passed'/'passport' bir anahtar kelime DEĞİLDİR")]
    [InlineData("passed: 123456")]
    [InlineData("passport: AB1234567")]
    public void Mask_EnglishStandaloneLookalikes_AreNotTreatedAsPasswordKeyword(string text)
    {
        var result = new Redactor().Mask(text);
        Assert.Equal(0, result.Count);
        Assert.Equal(text, result.Text);
    }

    [Fact(DisplayName = "LooksRandom · rakamsız 36 harfli değer 'anahtar' yakınında olsa da maskelenmez")]
    public void Mask_LongValueWithoutDigit_NearKeyword_IsNotMasked()
    {
        const string lettersOnly = "abcdefghijklmnopqrstuvwxyzabcdefghij"; // 36 letters, no digit
        var text = $"anahtar: {lettersOnly} bitti.";

        var result = new Redactor().Mask(text);

        Assert.Equal(0, result.Count);
        Assert.Contains(lettersOnly, result.Text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "UrlCredentials · şema görünür kalır, yalnız kimlik bilgisi maskelenir")]
    public void Mask_UrlCredentials_LeavesSchemeVisible()
    {
        var result = new Redactor().Mask("https://operator:Kule9GizliDeger@panel.example.com/durum");

        Assert.StartsWith("https://****(maskelendi)", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("operator:Kule9GizliDeger", result.Text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "UrlCredentials · şifre içindeki ek '@' de maskelenir (sızıntı yok)")]
    public void Mask_UrlCredentials_PasswordContainingAt_IsFullyMasked()
    {
        var result = new Redactor().Mask("bağlantı https://u:p4ss@w0rd@host.example.com burada.");

        Assert.DoesNotContain("p4ss", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("w0rd", result.Text, StringComparison.Ordinal);
        Assert.Contains("maskelendi", result.Text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "B7 · büyük harf Türkçe 'ŞİFRE:' (dotted İ) ve çoğul 'passwords:' maskelenir")]
    public void Mask_UppercaseTurkishAndPluralEnglishPasswordKeywords_AreMasked()
    {
        var upperTurkish = new Redactor().Mask("kurulum notu: ŞİFRE: gizliDeger9x — satır.");
        Assert.DoesNotContain("gizliDeger9x", upperTurkish.Text, StringComparison.Ordinal);
        Assert.Equal(1, upperTurkish.Count);

        var plural = new Redactor().Mask("erişim bilgisi passwords: hunter22x burada.");
        Assert.DoesNotContain("hunter22x", plural.Text, StringComparison.Ordinal);
        Assert.Equal(1, plural.Count);
    }

    [Fact(DisplayName = "B7 · parantezli ters tırnak jenerik biçimi: 'parola (`token`)' maskelenir")]
    public void Mask_KeywordWithParenBeforeBacktick_IsMasked()
    {
        // Generic B7 form (keyword, then an optional '(', then a backtick-quoted token) — not
        // fitted to any one real vault line; see Redactor.cs's QuotedPassword remarks.
        var result = new Redactor().Mask("kurulum notu: parola (`SentetikDeger1`) — devam eder.");

        Assert.DoesNotContain("SentetikDeger1", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, result.Count);
    }

    [Fact(DisplayName = "B7 · JSON/YAML tırnaklı anahtar biçimi: '\"password\": \"deger\"' maskelenir")]
    public void Mask_JsonQuotedKeyForm_IsMasked()
    {
        // Review finding: a closing quote between the keyword and ':' defeated the old pattern.
        var result = new Redactor().Mask("kayıt: {\"password\": \"hunter22x\"} devam eder.");

        Assert.DoesNotContain("hunter22x", result.Text, StringComparison.Ordinal);
        Assert.True(result.Count >= 1, $"expected at least 1 masked value, got {result.Count}");
    }

    [Fact(DisplayName = "B7 · büyük harfli env değişkeni öneki: 'PGPASSWORD=deger' maskelenir")]
    public void Mask_UppercaseEnvVarPrefixedPassword_IsMasked()
    {
        // Review finding: the old lookbehind rejected ANY preceding letter, including the
        // uppercase 'G' of an env-var prefix like PGPASSWORD.
        var result = new Redactor().Mask("ortam degiskeni: PGPASSWORD=hunter22x calisir.");

        Assert.DoesNotContain("hunter22x", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, result.Count);
    }

    [Fact(DisplayName = "B7 · 'passwd' anahtar kelimesi maskelenir")]
    public void Mask_PasswdKeyword_IsMasked()
    {
        var result = new Redactor().Mask("kurulum notu: passwd: hunter22x — bu satır sentetiktir.");

        Assert.DoesNotContain("hunter22x", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, result.Count);
    }

    [Theory(DisplayName = "Yanlış pozitif · tek başına 'pass' düz İngilizce kelimeyi maskelemez")]
    [InlineData("one-pass: digest hesapla")]
    [InlineData("Second pass: rebuild index")]
    public void Mask_BarePassFollowedByPlainWord_IsNotMasked(string text)
    {
        // Review finding: the bare 'pass' alternative had no hyphen guard and no requirement
        // that its value look like a credential, so ordinary prose was masked and the damage
        // (flush output is written once, irreversibly) was permanent.
        var result = new Redactor().Mask(text);

        Assert.Equal(0, result.Count);
        Assert.Equal(text, result.Text);
    }

    [Fact(DisplayName = "Yanlış pozitif · port ve yol içeren URL'de kimlik bilgisi yokken host/port/path görünür kalır")]
    public void Mask_UrlWithPortAndPathButNoCredentials_IsNotMasked()
    {
        // Review finding: the old unbounded UserInfo classes let ":8080/path?contact=a@b.com"
        // be mistaken for "user:pass@host", hiding the real host/port/path.
        const string text = "see https://example.com:8080/path?contact=a@b.com now";
        var result = new Redactor().Mask(text);

        Assert.Equal(0, result.Count);
        Assert.Equal(text, result.Text);
    }

    [Fact(DisplayName = "S3 · büyük harf Türkçe 'ŞİFRE' (dotted İ) yakınındaki hex jeton artık maskelenir")]
    public void Mask_UppercaseTurkishSifreDottedI_NearHex_IsNowMasked()
    {
        // Review finding: the Keyword regex's şifre/sifre alternative did not fold dotted İ (same
        // reasoning as Guards' own documented [iİI] handling), so this was a miss before the fix.
        var hex40 = string.Concat(Enumerable.Range(0, 40).Select(i => "0123456789abcdef"[i % 16]));
        var result = new Redactor().Mask($"ŞİFRE yakın {hex40}");

        Assert.DoesNotContain(hex40, result.Text, StringComparison.Ordinal);
        Assert.True(result.Count >= 1, $"expected at least 1 masked value, got {result.Count}");
    }

    [Fact(DisplayName = "Performans · UrlCredentials 200 KB girdide sınırlı sürede biter (ReDoS koruması)")]
    public void Mask_UrlCredentialsPattern_OnLargeInput_CompletesQuickly()
    {
        // Review finding: the old unbounded pattern measured 5.8 s on 200 KB and 143 s on 1 MB
        // of "https://a:b" repeated, with no match timeout. Bounded classes + a 1 s match
        // timeout per pattern must keep this well under a second even at 200 KB.
        var text = string.Concat(Enumerable.Repeat("https://a:b ", 200 * 1024 / 12));
        var redactor = new Redactor();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        redactor.Mask(text);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Mask on ~200 KB took {stopwatch.Elapsed}, expected well under 5 s.");
    }
}
