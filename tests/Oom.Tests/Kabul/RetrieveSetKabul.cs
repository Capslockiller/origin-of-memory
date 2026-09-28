using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oom.Tests.Kabul.Fixtures;
using Xunit.Abstractions;

namespace Oom.Tests.Kabul;

/// <summary>
/// Retrieval-set runner (SPEC F2-1, F2-5, NB-4, R2, R7). Each query goes through the real
/// exe as <c>oom retrieve --query &lt;q&gt; --top 3</c> via <see cref="KabulHarness"/>, and the
/// vault-relative paths it prints (the part before '#') are compared with the set's expected
/// paths. The gate is ≥ 8 of the positives with an expected path in the top 3. Negatives are
/// measured and printed, never gated (R7).
///
/// Two sets use the same runner:
///   - the PRIVATE set frozen by O2 outside the repo (R2: real personal facts, expected paths
///     chosen by O2, not by an implementer) — OOM_KABUL_EVAL_SET, run over OOM_KABUL_VAULT;
///   - the SYNTHETIC set in tests/fixtures/retrieve-set.json, run over a vault generated
///     below, so CI has a score gate that needs nothing outside the repo.
/// Both are tagged Kabul=RetrieveSet (R11) and are red on the old exe, whose corpus is
/// knowledge/concepts only. A missing set or vault fails loudly; nothing here skips.
/// </summary>
[Trait("Kabul", "RetrieveSet")]
public sealed class RetrieveSetKabul(ITestOutputHelper output)
{
    private const int MinPositiveHits = 8;
    private const int Top = 3;

    // Pins the frozen O2 set (R2, NB-4) so a swapped-in file cannot silently change what
    // F2-1/NB-4 measure. Recorded alongside the red-run evidence in tests/REGRESYON-KANITI.md.
    // Update only through a reviewed step that re-freezes and re-records the set.
    private const string PrivateSetSha256 = "33bf01d31c7d990c30238947feaabf5bf629cbf7bda62aede441f7672096464a";
    private const string PrivateSetId = "O2-arama-seti";
    private const string PrivateSetAuthor = "independent-lane-O2";
    private const int PrivateSetPositives = 10;
    private const int PrivateSetNegatives = 3;

    private static readonly string[] AllowedKinds = ["positive", "negative", "first-not"];
    private static readonly Regex HitLine = new(@"^— (?<path>\S.*?\.md(?:#\S*)?)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact(DisplayName = "F2-1 · Özel arama seti (O2, dondurulmuş): 10 pozitifin ≥ 8'i ilk 3'te")]
    public void PrivateSet_AtLeastEightOfTenPositivesInTopThree()
    {
        var setPath = Environment.GetEnvironmentVariable("OOM_KABUL_EVAL_SET") is { Length: > 0 } configured ? configured : DefaultPrivateSet();
        Assert.True(File.Exists(setPath),
            "private retrieval set not found (set OOM_KABUL_EVAL_SET). This test never skips.");

        var bytes = File.ReadAllBytes(setPath);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.True(string.Equals(digest, PrivateSetSha256, StringComparison.OrdinalIgnoreCase),
            $"F2-1: private retrieval set digest mismatch — {setPath} hashes to {digest}, pinned value is " +
            $"{PrivateSetSha256} (recorded in tests/REGRESYON-KANITI.md). A changed set no longer proves F2-1/NB-4 " +
            "consumed the pre-implementation O2 oracle; re-freeze and re-record through a reviewed step, don't just re-run this test.");

        var set = RetrievalSet.Load(setPath);
        ValidateFrozenStructure(set, PrivateSetId, PrivateSetAuthor, PrivateSetPositives, PrivateSetNegatives);
        var vault = Environment.GetEnvironmentVariable("OOM_KABUL_VAULT");
        Assert.True(!string.IsNullOrWhiteSpace(vault) && Directory.Exists(vault),
            $"OOM_KABUL_VAULT must name the vault the private set was frozen against (meta.vault_source: {set.VaultSource}); " +
            $"got '{vault}'. This test never skips.");

        using var harness = new KabulHarness();
        var score = Measure(harness, vault!, set);
        var baseline = AcceptedBaseline(set.Id);
        var gate = Math.Max(MinPositiveHits, baseline);

        Assert.True(score.PositiveHits >= gate && score.Failed.Count == 0,
            $"F2-1: private set {set.Id}: {score.PositiveHits}/{score.Positives} positives have an expected path in the top {Top}; " +
            $"the gate is ≥ {gate} (floor {MinPositiveHits}, accepted baseline {baseline} — SPEC F2-5, a score drop is red); " +
            $"infra/first-not failures: [{string.Join(", ", score.Failed)}].\n{score.Report}");
    }

    [Fact(DisplayName = "F2-5 · Sentetik arama seti (CI): ≥ 8/10 ilk 3'te, daily olgusu bulunur, 'Eylül' → Duzeltmeler değil")]
    public void SyntheticSet_ScoreGate()
    {
        var set = RetrievalSet.Load(RepoFile(Path.Combine("tests", "fixtures", "retrieve-set.json")));
        using var harness = new KabulHarness();
        var vault = SyntheticVault.Build(harness.Root);

        var score = Measure(harness, vault, set);
        var baseline = AcceptedBaseline(set.Id);
        var gate = Math.Max(MinPositiveHits, baseline);

        Assert.True(score.PositiveHits >= gate && score.Failed.Count == 0,
            $"F2-5: synthetic set {set.Id}: {score.PositiveHits}/{score.Positives} positives in the top {Top} " +
            $"(gate ≥ {gate}, floor {MinPositiveHits}, accepted baseline {baseline} — SPEC F2-5, a score drop is red); " +
            $"hard checks failed: [{string.Join(", ", score.Failed)}].\n{score.Report}");
    }

    /// <summary>Versioned accepted score floor per frozen set (SPEC F2-5): a set that scored N
    /// once must never be accepted below N again, even though the hard-coded gate stays 8.
    /// Raised only through a reviewed edit to tests/fixtures/retrieve-baseline.json.</summary>
    private static int AcceptedBaseline(string setId)
    {
        var path = RepoFile(Path.Combine("tests", "fixtures", "retrieve-baseline.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        return document.RootElement.TryGetProperty("sets", out var sets) && sets.TryGetProperty(setId, out var value)
            ? value.GetInt32()
            : MinPositiveHits;
    }

    /// <summary>Structural checks for a set that is supposed to be frozen (SPEC F2-1/NB-4/R2):
    /// the digest check alone proves the bytes didn't change, but not that the bytes were ever
    /// shaped correctly. Checked in addition to the digest, not instead of it.</summary>
    private static void ValidateFrozenStructure(RetrievalSet set, string expectedId, string expectedAuthor, int expectedPositives, int expectedNegatives)
    {
        Assert.True(set.Id == expectedId,
            $"F2-1: frozen set set_id mismatch — got '{set.Id}', expected '{expectedId}'.");
        Assert.True(set.Author == expectedAuthor,
            $"F2-1: frozen set author mismatch — got '{set.Author}', expected '{expectedAuthor}' (must stay independent of the implementing lanes).");

        var ids = set.Queries.Select(query => query.Id).ToArray();
        var duplicates = ids.GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        Assert.True(duplicates.Length == 0, $"F2-1: frozen set has duplicate query ids: [{string.Join(", ", duplicates)}].");

        var unknownKinds = set.Queries.Where(query => !AllowedKinds.Contains(query.Kind)).Select(query => $"{query.Id}:{query.Kind}").ToArray();
        Assert.True(unknownKinds.Length == 0,
            $"F2-1: frozen set has unknown kind(s) [{string.Join(", ", unknownKinds)}]; allowed: [{string.Join(", ", AllowedKinds)}]. " +
            "An unrecognized kind must not become an ungated measurement.");

        var positives = set.Queries.Where(query => query.Kind == "positive").ToArray();
        var negatives = set.Queries.Where(query => query.Kind == "negative").ToArray();
        Assert.True(positives.Length == expectedPositives,
            $"F2-1: frozen set positive count is {positives.Length}, expected exactly {expectedPositives}.");
        Assert.True(negatives.Length == expectedNegatives,
            $"F2-1: frozen set negative count is {negatives.Length}, expected exactly {expectedNegatives} (SPEC accepted amendment: 3 negatives).");

        var emptyExpected = positives.Where(query => query.Expected.Count == 0).Select(query => query.Id).ToArray();
        Assert.True(emptyExpected.Length == 0,
            $"F2-1: frozen set has positive(s) with no expected path, so they could never be scored a hit: [{string.Join(", ", emptyExpected)}].");
    }

    private SetScore Measure(KabulHarness harness, string vault, RetrievalSet set)
    {
        var report = new StringBuilder();
        var failed = new List<string>();
        var positives = 0;
        var hits = 0;
        foreach (var query in set.Queries)
        {
            var result = harness.Run(vault, ["retrieve", "--query", query.Text, "--top", Top.ToString(System.Globalization.CultureInfo.InvariantCulture)], timeout: TimeSpan.FromSeconds(60));
            var top = TopPaths(result.Stdout, vault);
            var shown = $"top: [{string.Join(", ", top)}]" + (result.ExitCode == 0 ? string.Empty : $" · exit {result.ExitCode}: {result.Stderr.Trim()}");
            // A nonzero exit is an infrastructure failure regardless of query kind: matching
            // stdout with a nonzero exit must not silently pass (a negative query's relevance
            // count still stays ungated below — only the exit-code check is unconditional, R7).
            if (result.ExitCode != 0)
                failed.Add($"{query.Id} exit {result.ExitCode}");
            string line;
            switch (query.Kind)
            {
                case "positive":
                    positives++;
                    var hit = top.Select(BeforeAnchor).Any(path => query.Expected.Contains(path, StringComparer.OrdinalIgnoreCase));
                    hits += hit ? 1 : 0;
                    if (query.Required && !hit)
                        failed.Add($"{query.Id} required");
                    line = $"{query.Id} {(hit ? "hit " : "MISS")}{(query.Required ? " (required)" : string.Empty)} · expected [{string.Join(", ", query.Expected)}] · {shown}";
                    break;

                case "first-not":
                    // top.Count > 0 is required: a zero-result query must not pass just because
                    // null != FirstNot (no hits proves nothing about ranking).
                    var first = top.Count == 0 ? null : Path.GetFileName(BeforeAnchor(top[0]));
                    var held = top.Count > 0 && !string.Equals(first, query.FirstNot, StringComparison.OrdinalIgnoreCase);
                    if (!held)
                        failed.Add($"{query.Id} first-not {query.FirstNot}");
                    line = $"{query.Id} {(held ? "ok  " : "FAIL")} · first hit must not be {query.FirstNot} (and must have ≥ 1 hit) · {shown}";
                    break;

                default:
                    line = $"{query.Id} {query.Kind} (measured, not gated) · {top.Count} hit(s) · {shown}";
                    break;
            }

            output.WriteLine(line);
            report.Append(line).Append('\n');
        }

        var summary = $"{set.Id}: positives {hits}/{positives} in top {Top}";
        output.WriteLine(summary);
        report.Append(summary).Append('\n');
        return new SetScore(positives, hits, failed, report.ToString());
    }

    /// <summary>The printed hit paths, in rank order, made vault-relative with '/' separators.</summary>
    private static IReadOnlyList<string> TopPaths(string stdout, string vault)
    {
        var root = Path.GetFullPath(vault).Replace('\\', '/').TrimEnd('/') + "/";
        return [.. HitLine.Matches(stdout.Replace("\r", string.Empty, StringComparison.Ordinal))
            .Select(match => match.Groups["path"].Value.Replace('\\', '/'))
            .Select(path => path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path[root.Length..] : path)
            .Take(Top)];
    }

    private static string BeforeAnchor(string path)
    {
        var anchor = path.IndexOf('#', StringComparison.Ordinal);
        return anchor < 0 ? path : path[..anchor];
    }

    private static string RepoFile(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Oom.sln")))
            {
                var path = Path.Combine(directory.FullName, relative);
                Assert.True(File.Exists(path), $"repo file missing: {path}");
                return path;
            }
        }

        Assert.Fail($"no Oom.sln above {AppContext.BaseDirectory}; cannot locate {relative}");
        return string.Empty;
    }

    /// <summary>Codex review (wave 3, R2): no absolute private machine path is embedded in
    /// public source. The private set's default location sits one level above the repo root
    /// (both the main repo and every worktree live under 10-Aktif) — never a literal
    /// drive/root here. OOM_KABUL_EVAL_SET still overrides it (see the caller).</summary>
    private static string DefaultPrivateSet()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Oom.sln")))
                return Path.Combine(directory.FullName, "..", "oom-surum-3.1", "degerlendirme", "retrieve-set.json");
        }

        Assert.Fail($"no Oom.sln above {AppContext.BaseDirectory}; cannot derive the default private set path.");
        return string.Empty;
    }

    private sealed record SetScore(int Positives, int PositiveHits, IReadOnlyList<string> Failed, string Report);

    private sealed record RetrievalQuery(string Id, string Text, string Kind, IReadOnlyList<string> Expected, bool Required, string? FirstNot);

    private sealed record RetrievalSet(string Id, string Author, string VaultSource, IReadOnlyList<RetrievalQuery> Queries)
    {
        public static RetrievalSet Load(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            var meta = root.GetProperty("meta");
            var queries = root.GetProperty("queries").EnumerateArray().Select(query => new RetrievalQuery(
                query.GetProperty("id").GetString()!,
                query.GetProperty("query").GetString()!,
                query.GetProperty("kind").GetString()!,
                query.TryGetProperty("expected", out var expected)
                    ? [.. expected.EnumerateArray().Select(item => item.GetString()!.Replace('\\', '/'))]
                    : [],
                query.TryGetProperty("required", out var required) && required.GetBoolean(),
                query.TryGetProperty("first_not", out var firstNot) ? firstNot.GetString() : null)).ToArray();
            Assert.True(queries.Length > 0, $"retrieval set has no queries: {path}");
            return new RetrievalSet(
                meta.TryGetProperty("set_id", out var id) ? id.GetString()! : Path.GetFileName(path),
                meta.TryGetProperty("author", out var author) ? author.GetString()! : "(unknown)",
                meta.TryGetProperty("vault_source", out var source) ? source.GetString()! : "(unknown)",
                queries);
        }
    }
}

/// <summary>
/// The synthetic vault tests/fixtures/retrieve-set.json is scored against: the Kabul fixture
/// plus invented concept notes, daily logs in flush shape and a Duzeltmeler file. No real
/// personal facts. Each fact lives only where the set's 'where_fact_lives' says it does.
/// </summary>
file static class SyntheticVault
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string Build(string root)
    {
        var vault = KabulVaultBuilder.Build(root);

        Concept(vault, "pusula-rota-cizelgesi", "Pusula Rota Çizelgesi",
            "Pusula projesinde rota çizelgesi üç durağı sırayla izler: önce Liman, sonra Değirmen, en son Gözlem Tepesi. " +
            "Çizelge her sabah ekip toplantısında okunur ve durak sırası değişirse yeniden yazılır.");
        Concept(vault, "kestane-deposu-sicaklik-kurali", "Kestane Deposu Sıcaklık Kuralı",
            "Kestane deposunda raf sıcaklığı dört dereceyi geçince havalandırma otomatik açılır. " +
            "Nem ölçer ayrıca kayıt tutar; kural depo sorumlusu tarafından konmuştur.");
        Concept(vault, "lamba-atolyesi-vardiya-duzeni", "Lamba Atölyesi Vardiya Düzeni",
            "Lamba atölyesinde vardiya düzeni iki ekip üzerine kuruldu: ekipler haftalık dönüşümle sabah ve akşam vardiyasını paylaşır. " +
            "Vardiya değişimi cuma akşamı yapılır.");
        Concept(vault, "yelken-kulubu-uyelik-akisi", "Yelken Kulübü Üyelik Akışı",
            "Yelken kulübüne üyelik akışı üç adımdan oluşur: başvuru formu, deneme seferi ve aidat onayı. " +
            "Deneme seferi kulüp eğitmeniyle yapılır.");
        Concept(vault, "kartal-sunucusu", "Kartal Sunucusu",
            "Kartal sunucusu gece yedeklemesi ve günlük toplama amacıyla kuruldu. Disk alanı her ay gözden geçirilir.");
        Concept(vault, "ceviz-uygulamasi", "Ceviz Uygulaması",
            "Ceviz uygulaması semt kütüphanesinin gönüllüleri için yapılıyor: gönüllüler kitap iade sırasını telefondan izler.");
        Concept(vault, "hasat-takvimi", "Hasat Takvimi",
            "Hasat takvimi Eylül başında açılır. Eylül ortasında bağ bozumu yapılır, Eylül sonunda ürün depoya girer.");

        Daily(vault, "2026-09-21", "Ceviz uygulamasının gönüllü ekranı üzerinde çalışıldı.",
            "Ceviz uygulaması semt kütüphanesinin gönüllüleri için yapılıyor; iade sırası ekranı tamamlandı.");
        Daily(vault, "2026-09-22", "Mercan paneli tedarik görüşmesi yapıldı.",
            "Mercan paneli için verilen siparişin numarası MRC-4471 olarak kaydedildi.");
        Daily(vault, "2026-09-23", "Kartal sunucusunun diski genişletildi.",
            "Kartal sunucusu gece yedeklemesi amacıyla kurulmuştu; disk bugün iki katına çıkarıldı.");
        Daily(vault, "2026-09-24", "Turna şeridinin ölçüm turu yapıldı.",
            "Turna şeridi dokuz ayrı testle ölçüldü; yedisi ilk denemede geçti, ikisi yeniden koşulacak.");
        Daily(vault, "2026-09-25", "25 Eylül saha günü: Fener kulesi bakım planı konuşuldu.",
            "Fener kulesinin boyası rüzgâr uyarısı yüzünden ertelendi; yeni tarih hava raporuna bağlı.");

        File.WriteAllText(Path.Combine(vault, KabulVaultBuilder.CompanionDir, "Duzeltmeler.md"),
            "# Düzeltmeler\n\n" +
            "## Toplantı saati yazımı\n" +
            "Toplantı saatleri UTC ile değil yerel saat dilimiyle yazılır; saat diliminin kısaltması parantez içinde eklenir.\n\n" +
            "## Tarih yazımı\n" +
            "Tarihler notlarda gün ve ay adıyla yazılır, örneğin 27 Eylül; yıl yalnız farklıysa eklenir. Sayısal biçim yalnız dosya " +
            "adlarında kullanılır, metnin içinde kullanılmaz. Aynı notta iki biçim karıştırılmaz ve kısaltma kullanılmaz.\n\n" +
            "## Ölçü birimi yazımı\n" +
            "Ölçüler birimiyle birlikte yazılır; birim sayıdan bir boşlukla ayrılır.\n", Utf8);

        return vault;
    }

    private static void Concept(string vault, string slug, string title, string body) =>
        File.WriteAllText(Path.Combine(vault, "knowledge", "concepts", slug + ".md"),
            "---\n" +
            $"title: {title}\n" +
            "aliases: []\n" +
            "tags: [sentetik]\n" +
            "sources: []\n" +
            "created: 2026-09-01\n" +
            "updated: 2026-09-20\n" +
            "type: concept\n" +
            "hub: genel\n" +
            "---\n" +
            $"# {title}\n\n{body}\n\n" +
            "## İlgili Kavramlar\n" +
            "- [[kabul-sentetik-kavram-01]] — aynı sentetik fixture'ın kavramı\n" +
            "- [[kabul-sentetik-kavram-02]] — aynı sentetik fixture'ın diğer kavramı\n", Utf8);

    /// <summary>A daily log in the shape flush writes: frontmatter, one session block with its anchor.</summary>
    private static void Daily(string vault, string date, string context, string decision) =>
        File.WriteAllText(Path.Combine(vault, "daily", date + ".md"),
            $"---\ntype: daily\ndate: {date}\nsource: oom\n---\n# Günlük Log: {date}\n\n## Oturumlar\n\n" +
            $"### Oturum (10:15), tarama\n<!-- session:kabul-sentetik-{date} ts:{date}T10:15:00+03:00 turns:0-11 source:claude -->\n" +
            $"## Bağlam\n- {context}\n" +
            "## Önemli Konuşmalar\n- Oturumun ayrıntıları sentetik olarak üretildi.\n" +
            $"## Alınan Kararlar\n- {decision}\n" +
            "## Öğrenilenler\n- Belirlenmedi.\n" +
            "## Yapılacaklar\n- Belirlenmedi.\n", Utf8);
}
