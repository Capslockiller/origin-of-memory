using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class GetirmeScars
{
    [Fact(DisplayName = "Y-141 · Sorgu yolu FTS indeksinden geçer, indeks cevabı değiştirmez")]
    public void Y141_QueryAndHookGenerateCandidatesThroughTheIndex()
    {
        var vault = ScarFixture.TempDirectory();
        var index = Path.Combine(vault, "state.db");
        try
        {
            Concepts(vault);
            const string prompt = "Panel güvenlik kapısı nasıl çalışıyor?";

            var scanner = new Retrieve(new RetrieveOptions(VaultPath: vault));
            var indexed = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index));
            indexed.Build();

            var scanned = scanner.Query(prompt, Guid.NewGuid().ToString(), 5);
            var served = indexed.Query(prompt, Guid.NewGuid().ToString(), 5);

            Assert.Equal("corpus-scan:no-index", scanner.CandidateSource);
            Assert.Equal("fts", indexed.CandidateSource);
            Assert.NotEmpty(served.Hits);
            Assert.Equal(scanned.Hits.Select(hit => hit.Name), served.Hits.Select(hit => hit.Name));
            Assert.Equal(scanned.Hits.Select(hit => hit.Score), served.Hits.Select(hit => hit.Score));

            File.WriteAllText(Path.Combine(vault, "knowledge", "concepts", "panel-guvenlik-kapisi-eki.md"),
                Frontmatter("Panel güvenlik kapısı eki") + "Panel güvenlik kapısı ek bilgisi");
            var stale = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index));
            var late = stale.Query(prompt, Guid.NewGuid().ToString(), 5);
            Assert.Equal("corpus-scan:stale-index", stale.CandidateSource);
            Assert.Contains(late.Hits, hit => hit.Name == "panel-guvenlik-kapisi-eki.md");
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    private static void Concepts(string vault)
    {
        var concepts = Path.Combine(vault, "knowledge", "concepts");
        Directory.CreateDirectory(concepts);
        File.WriteAllText(Path.Combine(concepts, "panel-guvenlik-kapisi.md"),
            Frontmatter("Panel güvenlik kapısı") + "Panel güvenlik kapısı böyle çalışıyor");
        for (var i = 0; i < 8; i++)
            File.WriteAllText(Path.Combine(concepts, $"kavram-{i}.md"),
                Frontmatter($"Kavram {i}") + $"Panel güvenlik bilgisi {i}");
    }

    private static string Frontmatter(string title) =>
        $"---\nyazan: claude\nmodel: opus-5\ntitle: {title}\naliases: []\ntags: []\nsources: [2026-09-11.md]\ncreated: 2026-09-11\nupdated: 2026-09-11\n---\n";

    [Fact(DisplayName = "Y-035 · Güncel düzeltme aranır ve eski kavram top üçe giremez")]
    public void Y035_CorrectionLayerOutranksStaleConcept()
    {
        var vault = ScarFixture.RetrievalVault();
        try
        {
            var retrieve = new Retrieve(new RetrieveOptions(VaultPath: vault));
            var result = retrieve.Query("Speaking tarihi ve ücret nedir?", "retrieval-35", 3);
            Assert.True(result.Hits.Count >= 1);
            Assert.Equal("correction", result.Hits[0].Source);
            Assert.Contains(result.Hits, hit => hit.Text.Contains("20 Eylül", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Hits.Take(3), hit => hit.Text.Contains("13 Eylül", StringComparison.Ordinal));
            Assert.DoesNotContain(retrieve.Query("Speaking sınavı ücreti nedir?", "retrieval-35b", 5).Hits,
                hit => hit.Name == "speaking-sinavi-tarihi.md");
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "Y-040 · BM25 title eşleşmesi body eşleşmesinden yüksek skor alır")]
    public void Y040_Bm25WeightsIncludeUnindexedLeadingZero()
    {
        var title = new Note("title.md", "zümrüt", [], [], ["x.md"], new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), "sıradan gövde");
        var body = new Note("body.md", "sıradan", [], [], ["x.md"], new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), "zümrüt");
        var hits = new Retrieve().Rank("zümrüt", [body, title]);
        Assert.Equal("title.md", hits[0].Name);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact(DisplayName = "Y-041 · Bilinmeyen getirme modu sessizce BM25'e düşmez")]
    public void Y041_UnknownRetrievalModeFails()
    {
        Assert.Throws<ArgumentException>(() => new Retrieve().Rank("sorgu", [ScarFixture.Note(1)], "mystery"));
    }

    [Fact(DisplayName = "Y-044 · Türkçe I ve İ indeks ile sorguda aynı özel katlamayı kullanır")]
    public void Y044_TurkishFoldHandlesDottedAndDotlessI()
    {
        var fold = new TurkishFold();
        Assert.Equal(fold.Fold("İstanbul"), fold.Fold("istanbul"));
        Assert.Equal(fold.Fold("ISTANBUL"), fold.Fold("ıstanbul"));
        Assert.Equal(fold.Tokenize("İstanbul"), fold.Tokenize("ISTANBUL"));
    }

}
