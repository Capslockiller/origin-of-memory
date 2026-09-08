using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class GetirmeScars
{
    [Fact(DisplayName = "Y-035 · Güncel düzeltme aranır ve eski kavram top üçe giremez")]
    public void Y035_CorrectionLayerOutranksStaleConcept()
    {
        var result = new Retrieve().Query("Speaking tarihi ve ücret nedir?", "retrieval-35", 3);
        Assert.True(result.Hits.Count >= 1);
        Assert.Contains(result.Hits, hit => hit.Text.Contains("20 Eylül", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Hits.Take(3), hit => hit.Text.Contains("13 Eylül", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Y-036 · Sohbet, makine zarfı ve yol ağırlıklı prompt sıfır enjeksiyon üretir")]
    public void Y036_JunkAndPathPromptsInjectNothing()
    {
        var retrieve = new Retrieve();
        var prompts = new[] { "dur bana soru sorma", "<task-notification>iş bitti</task-notification>", @"C:\repo\src\file.cs D:\vault\x.md", "bugün nasılsın" };
        Assert.All(prompts, prompt => Assert.Empty(retrieve.Hook(prompt, "junk").Hits));
    }

    [Fact(DisplayName = "Y-037 · İç oom çağrısı sessizdir ve hafıza dışı promptlar enjekte edilmez")]
    public void Y037_RecursiveInvocationIsSilent()
    {
        var environment = new Dictionary<string, string> { ["OOM_INVOKED_BY"] = "oom" };
        var result = new Retrieve().Hook("hafıza notu getir", "recursive", environment);
        Assert.Empty(result.Output);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact(DisplayName = "Y-038 · İki getirme giriş noktası bayt-birebir aynı çıktı verir")]
    public void Y038_RetrieveEntryPointsAreIdentical()
    {
        var retrieve = new Retrieve();
        var hook = retrieve.Hook("Türkçe stemmer kararı", "same-entry");
        var query = retrieve.Query("Türkçe stemmer kararı", "same-entry");
        Assert.Equal(query.Output, hook.Output);
    }

    [Fact(DisplayName = "Y-039 · Dedupe sorgu imzası ve not adına birlikte anahtarlanır")]
    public void Y039_DedupeIncludesQuerySignature()
    {
        var retrieve = new Retrieve();
        var first = retrieve.Query("Türkçe tokenizasyon nedir?", "session-39");
        var repeated = retrieve.Query("Türkçe tokenizasyon nedir?", "session-39");
        var different = retrieve.Query("Tokenizasyon kararı ne zaman alındı?", "session-39");
        Assert.NotEmpty(first.Hits);
        Assert.Empty(repeated.Hits);
        Assert.Contains(different.Hits, x => x.Name == first.Hits[0].Name);
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

    [Fact(DisplayName = "Y-042 · Episodik ve kavram recall zemini aynı koşumda korunur")]
    public void Y042_RetrievalChangeMustPassBothRecallAxes()
    {
        var result = new BenchmarkResult(0.7, 0.80, 0.88, 0.0, new Dictionary<string, double>());
        var measured = new Retrieve().Query("gold set", "benchmark", 5);
        Assert.True(result.EpisodicTop3 >= 0.7);
        Assert.True(result.RecallAt5 >= 0.88);
        Assert.Equal(5, measured.Hits.Count);
    }

    [Fact(DisplayName = "Y-043 · Niyet kapısı konu örtüşse bile durdurma cümlesini reddeder")]
    public void Y043_IntentGateOverridesTopicOverlap()
    {
        var matching = new SearchHit("hafiza-kaybi.md", 99, "HAFIZA KAYBI TEŞHİSİ", "concept", ScarFixture.Now);
        Assert.False(new Retrieve().ShouldInject("dur bana soru sorma, hafıza kaybı yaşıyorsun", matching));
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
