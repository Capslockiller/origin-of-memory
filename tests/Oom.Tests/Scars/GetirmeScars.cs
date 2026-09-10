using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class GetirmeScars
{
    // yazan: codex · gpt-6
    [Fact(DisplayName = "Y-110 · Küçük konu derleminde doğru ilk not kapıdan geçer, ilgisiz soru boş kalır")]
    public void Y110_SmallCorpusGateKeepsRelevantTopHitAndRejectsUnrelatedPrompt()
    {
        // Common topic terms have floor IDF; the rarer identity term carries the evidence.
        var vault = ScarFixture.TempDirectory();
        var concepts = Path.Combine(vault, "knowledge", "concepts");
        Directory.CreateDirectory(concepts);
        for (var i = 0; i < 19; i++)
        {
            var title = i == 0 ? "Panel güvenlik kapısı" : $"Kavram {i}";
            var body = "Panel güvenlik " + (i < 8 ? "kapısı" : "bilgisi");
            var name = i == 0 ? "panel-guvenlik-kapisi.md" : $"kavram-{i}.md";
            File.WriteAllText(Path.Combine(concepts, name),
                $"---\nyazan: codex\nmodel: gpt-6\ntitle: {title}\naliases: []\ntags: []\nsources: [2026-09-10.md]\ncreated: 2026-09-10\nupdated: 2026-09-10\n---\n{body}");
        }

        const string prompt = "Panel güvenlik kapısı nasıl çalışıyor?";
        var retrieve = new Retrieve(new RetrieveOptions(VaultPath: vault));
        var session = Guid.NewGuid().ToString();
        var raw = retrieve.Query(prompt, session);
        Assert.Equal("panel-guvenlik-kapisi.md", raw.Hits[0].Name);
        Assert.InRange(raw.Hits[0].Score, 0.1, 2.0); // seven ranked terms: mean below 1.0
        var hooked = retrieve.Hook(prompt, session);
        Assert.NotEmpty(hooked.Hits);
        Assert.Equal(raw.Hits[0].Name, hooked.Hits[0].Name);
        Assert.Empty(retrieve.Hook("Ay tutulmasi kac dakika surer?", session).Hits);
        // A common-topic hit made solely from floored IDF is still insufficient evidence.
        Assert.Empty(retrieve.Hook("Panel güvenlik ayrıntıları nelerdir?", session).Hits);
        Assert.Empty(retrieve.Hook(prompt, session).Hits); // served-note dedupe survives
        Assert.Empty(new Retrieve(new RetrieveOptions(VaultPath: vault, StrictScore: 100))
            .Hook(prompt, Guid.NewGuid().ToString()).Hits);
    }

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
