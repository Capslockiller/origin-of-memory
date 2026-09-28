using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Oom.Contracts;
using Xunit.Abstractions;

namespace Oom.Tests.Kabul;

/// <summary>
/// Oracle acceptance tests for the Redactor (SPEC-3.1.0.md S3 patterns + B7 password
/// amendment; B6's "reused at index/output time" wiring is out of scope for L1 — that is
/// W2's job, this lane only proves the masker itself). Written independently by the
/// lane's oracle author; the implementer never sees this file's reasoning.
///
/// There is no Redactor on this tree yet (S3, A3-02: "maskeleme 12 Eyl'de kaldırılmıştı").
/// src/Oom/Guards/Redactor.cs in THIS tree is a stub the oracle author added only so this
/// file compiles: Mask(text) returns (text, 0) unchanged, exactly the "no masker on the
/// old tree, so the raw tokens are present" starting state B7's own evidence describes.
/// Every test below is written to FAIL against that stub and to PASS only once a real
/// masker replaces it.
///
/// The B7 audit test (last one) reads real files from the measurement vault copy
/// (SPEC-3.1.0.md's own fixed path, private — never E:/OdenaOS) and computes SHA-256
/// hashes of the tokens on three specific lines AT TEST RUN TIME. The lane's oracle author
/// never read those lines while writing this file (the sandbox's credential-materialization
/// guard blocks a direct read of them, as it should for a real secret) — the
/// token-locating regex below is a best-effort, independent re-implementation of the B7
/// pattern language, not a value copied from the file. Every assertion message below
/// reports only a SHA-256 hash and a length, never the raw token, matching the driver's
/// "never read or print the real vault tokens".
///
/// L3-privacy (SPEC R2): the vault-kopya path and the (file, line) locations of the three
/// leaked lines are private facts, so both now come from the private JSON (PrivateEval,
/// OzelKabul.cs) instead of a literal here.
/// </summary>
public sealed class RedactorKabul(ITestOutputHelper output)
{

    public static IEnumerable<object[]> KnownPrefixSecrets()
    {
        yield return new object[] { "ghp_" + Fill(36), "ghp_****(maskelendi)" };
        yield return new object[] { "gho_" + Fill(36), "gho_****(maskelendi)" };
        yield return new object[] { "github_pat_" + Fill(60), "github_pat_****(maskelendi)" };
        yield return new object[] { "sk-ant-" + Fill(40), "sk-ant-****(maskelendi)" };
        yield return new object[] { "AKIA" + FillUpper(16), "AKIA****(maskelendi)" };
    }

    private static string Fill(int length)
        => string.Concat(Enumerable.Range(0, length).Select(i => "0123456789abcdefghijklmnopqrstuvwxyz"[i % 36]));

    private static string FillUpper(int length) => Fill(length).ToUpperInvariant();

    [Theory(DisplayName = "S3 · bilinen önekli sırlar (ghp_/gho_/github_pat_/sk-ant-/AKIA) maskelenir, '<önek>****(maskelendi)' yazılır")]
    [MemberData(nameof(KnownPrefixSecrets))]
    public void Mask_KnownPrefixSecrets_AreReplacedWithLiteralPrefixMask(string secret, string expectedMask)
    {
        // "kayıt:" (not "anahtar/şifre/password/key/token") so this case exercises only
        // the known-prefix pattern, not the separate hex/base64-near-keyword pattern.
        var text = $"panel notları burada devam eder.\nkayıt: {secret}\nbaşka bir satır.";
        var result = new Redactor().Mask(text);

        Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
        Assert.Contains(expectedMask, result.Text, StringComparison.Ordinal);
        Assert.Equal(1, result.Count);
    }

    [Fact(DisplayName = "S3/B6 · 'anahtar' kelimesine 40 karakter yakın 32+ hex dizgisi maskelenir")]
    public void Mask_HexNearKeyKeyword_IsMasked()
    {
        // "MERCAN" is a neutral synthetic panel name (same convention GetirmeScars.cs/
        // IndeksScars.cs already use), not the real project word this test originally named.
        var hex48 = string.Concat(Enumerable.Range(0, 48).Select(i => "0123456789abcdef"[i % 16]));
        var text = $"MERCAN paneli için anahtarı: {hex48} — bugün sızdı.";
        var result = new Redactor().Mask(text);

        Assert.DoesNotContain(hex48, result.Text, StringComparison.Ordinal);
        Assert.Contains("maskelendi", result.Text, StringComparison.Ordinal);
        Assert.True(result.Count >= 1, $"expected at least 1 masked value, got {result.Count}");
    }

    [Fact(DisplayName = "S3 · URL içindeki user:pass@ maskelenir")]
    public void Mask_UserPasswordInUrl_IsMasked()
    {
        const string credentials = "operator:Kule9GizliDeger@";
        var text = $"panel bağlantısı: https://{credentials}panel.example.com/durum sayfasında.";
        var result = new Redactor().Mask(text);

        Assert.DoesNotContain(credentials, result.Text, StringComparison.Ordinal);
        Assert.Contains("maskelendi", result.Text, StringComparison.Ordinal);
        Assert.True(result.Count >= 1, $"expected at least 1 masked value, got {result.Count}");
    }

    [Theory(DisplayName = "B7 · 'şifre|parola|password|pass|pwd' sonrası ':' '=' veya ters tırnakla gelen >=6 karakterlik jeton maskelenir")]
    [InlineData("şifre: gizliDeger9x", "gizliDeger9x")]
    [InlineData("parola=AnotherSecret1", "AnotherSecret1")]
    [InlineData("pwd`BacktickToken1`", "BacktickToken1")]
    public void Mask_B7PasswordPattern_IsMasked(string fragment, string rawToken)
    {
        var text = $"kurulum notu: {fragment} — bu satır sentetiktir.";
        var result = new Redactor().Mask(text);

        Assert.DoesNotContain(rawToken, result.Text, StringComparison.Ordinal);
        Assert.Contains("maskelendi", result.Text, StringComparison.Ordinal);
        Assert.True(result.Count >= 1, $"expected at least 1 masked value, got {result.Count}");
    }

    [Fact(DisplayName = "S3 · aynı metindeki birden çok sır ayrı ayrı sayılır")]
    public void Mask_CountsEachSecretSeparately()
    {
        var ghp = "ghp_" + Fill(36);
        var akia = "AKIA" + FillUpper(16);
        var text = $"birinci kayıt: {ghp}\nikinci kayıt: {akia}\n";
        var result = new Redactor().Mask(text);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(ghp, result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(akia, result.Text, StringComparison.Ordinal);
    }

    [Trait("Kabul", "OzelVault")]
    [Fact(DisplayName = "B7 · kopya vault daily/ üzerinde denetim: diğer dailyler için yanlış pozitif sayısı (ölçülür, kapı değil)")]
    public void Mask_OverVaultKopyaDaily_ReportsFalsePositiveCountOverOtherDailies()
    {
        var vaultKopyaRoot = PrivateEval.Load().VaultPath;
        // Codex review (wave 3, finding 5): OzelVault diagnostics never echo the vault path.
        Assert.True(Directory.Exists(Path.Combine(vaultKopyaRoot, "daily")),
            "B7: vault-kopya/daily bulunamadı (SPEC-3.1.0.md ölçüm kopyası, canlı E:/OdenaOS değil). Bu test hiç atlanmaz.");

        var redactor = new Redactor();
        var dailyDir = Path.Combine(vaultKopyaRoot, "daily");
        var targetFileNames = PrivateEval.Load().RedactorTargetLines.Select(t => t.File).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var falsePositiveCount = 0;
        var falsePositiveFiles = 0;
        var otherFiles = 0;
        foreach (var path in Directory.EnumerateFiles(dailyDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            if (targetFileNames.Contains(Path.GetFileName(path)))
                continue;
            otherFiles++;
            var masked = redactor.Mask(File.ReadAllText(path));
            if (masked.Count <= 0)
                continue;
            falsePositiveCount += masked.Count;
            falsePositiveFiles++;
        }

        // Measured, not gated (SPEC B7: "Yanlış pozitif sayısı rapora yazılır").
        output.WriteLine($"B7: diğer {otherFiles} daily dosyasında toplam {falsePositiveCount} maskelenen değer, " +
                          $"{falsePositiveFiles} dosyada en az bir tane (rapor amaçlı, kapı değil).");
        Assert.True(otherFiles > 0, "B7: karşılaştırma için 'diğer daily' bulunamadı — dizin boş mu?");
    }

    // L3-privacy (SPEC R2): the (file, line) location of each leaked line is itself a
    // private detail, so it now comes from the private JSON (redactor.target_lines)
    // instead of a literal array here.
    public static IEnumerable<object[]> TargetLineData() =>
        PrivateEval.Load().RedactorTargetLines.Select(t => new object[] { t.File, t.Line });

    // Codex review (wave 3, finding 7, should): TargetLineData() calls PrivateEval.Load()
    // (reads the private JSON). Without DisableDiscoveryEnumeration, xunit enumerates
    // MemberData at DISCOVERY time — i.e. it can touch/fail on the private file before the
    // Kabul=OzelVault trait filter ever gets a chance to exclude this theory, undermining
    // public-CI isolation. Deferring enumeration to execution time keeps that load inside
    // the filtered/excluded run only.
    [Trait("Kabul", "OzelVault")]
    [Theory(DisplayName = "B7 · kopya vault daily/: hedef satırın jeton hash'i maskelenmiş çıktıda 0 kez geçer")]
    [MemberData(nameof(TargetLineData), DisableDiscoveryEnumeration = true)]
    public void Mask_OverVaultKopyaDaily_TargetTokenHashNeverAppearsInOutput(string file, int line)
    {
        var dailyDir = Path.Combine(PrivateEval.Load().VaultPath, "daily");
        var path = Path.Combine(dailyDir, file);
        // Codex review (wave 3, finding 5): 'file' and 'line' name a real leaked private
        // location; the theory's own DisplayName + test parameters already identify the case
        // to a human re-running it, so the assertion messages report shape only.
        Assert.True(File.Exists(path), "B7: hedef dosya yok.");

        var lines = File.ReadAllLines(path);
        Assert.True(line - 1 < lines.Length, $"B7: hedef satır dosya uzunluğunun ({lines.Length}) dışında.");
        var rawLine = lines[line - 1];

        var token = ExtractPasswordToken(rawLine);
        Assert.True(token is not null,
            $"B7: {file}:{line} satırında B7 desenine uyan bir jeton bulunamadı (anahtar kelime + ':'/'='/ters tırnak + >=6 karakter). " +
            "Bu, jeton çıkarma deseninin gerçek satırın noktalamasına uymadığını gösterebilir — ham içerik burada basılmaz; " +
            "sürücü satırı doğrudan inceleyip deseni ayarlamalı.");

        var tokenHash = Sha256Hex(token!);
        var redactor = new Redactor();
        var maskedText = redactor.Mask(File.ReadAllText(path)).Text;
        var stillPresent = maskedText.Contains(token!, StringComparison.Ordinal);

        output.WriteLine($"B7: {file}:{line} jeton hash={tokenHash} (uzunluk {token!.Length}) — maskelenmiş çıktıda {(stillPresent ? "HÂLÂ VAR" : "yok")}.");
        Assert.False(stillPresent,
            $"B7: {file}:{line} jetonunun (hash={tokenHash}, uzunluk {token.Length}) ham hâli maskelenmiş çıktıda hâlâ geçiyor.");
    }

    private static readonly Regex BacktickForm = new(
        // Driver fix (wave 1): the real line has "(" between keyword and backtick.
        @"(?:şifre|sifre|parola|password|pass|pwd)\w*\s*(?:[:=]\s*)?\(?`(?<token>[^`]{6,})`",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SeparatorForm = new(
        @"(?:şifre|sifre|parola|password|pass|pwd)\w*\s*[:=]\s*(?<token>[^\s`\)\],;]{6,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Best-effort, independent re-implementation of the B7 pattern language, used only
    /// to LOCATE the candidate token on a known (file, line) at test run time — see the
    /// class-level remarks. Tries the backtick form first (SPEC evidence: "ters tırnak
    /// içindeki ... jetonlar"), falls back to the ':'/'=' form.
    /// </summary>
    private static string? ExtractPasswordToken(string line)
    {
        var backtick = BacktickForm.Match(line);
        if (backtick.Success)
            return backtick.Groups["token"].Value;
        var separator = SeparatorForm.Match(line);
        return separator.Success ? separator.Groups["token"].Value : null;
    }

    private static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
