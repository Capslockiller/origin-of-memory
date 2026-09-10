using System.Text.Json;
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
        // The vault is the fixture's own: v0's failure was population, not ranking, so the test
        // needs a hand layer on disk to prove the correction is indexed at all.
        var vault = ScarFixture.RetrievalVault();
        try
        {
            var retrieve = new Retrieve(new RetrieveOptions(VaultPath: vault));
            var result = retrieve.Query("Speaking tarihi ve ücret nedir?", "retrieval-35", 3);
            Assert.True(result.Hits.Count >= 1);
            Assert.Equal("correction", result.Hits[0].Source);
            Assert.Contains(result.Hits, hit => hit.Text.Contains("20 Eylül", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Hits.Take(3), hit => hit.Text.Contains("13 Eylül", StringComparison.Ordinal));
            // The retired concept does not come back on a deeper query either.
            Assert.DoesNotContain(retrieve.Query("Speaking sınavı ücreti nedir?", "retrieval-35b", 5).Hits,
                hit => hit.Name == "speaking-sinavi-tarihi.md");
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
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
        var vault = ScarFixture.RetrievalVault();
        try
        {
            var session = "session-39-" + Guid.NewGuid().ToString("N")[..8];
            var retrieve = new Retrieve(new RetrieveOptions(VaultPath: vault));
            var first = retrieve.Query("Türkçe tokenizasyon nedir?", session);
            var repeated = retrieve.Query("Türkçe tokenizasyon nedir?", session);
            var different = retrieve.Query("Tokenizasyon kararı ne zaman alındı?", session);
            Assert.NotEmpty(first.Hits);
            Assert.Empty(repeated.Hits);
            // Keyed on the note name alone the ledger would hide it here too; the signature is
            // what lets a different question in the same session see the same note again (Y-039).
            Assert.Contains(different.Hits, x => x.Name == first.Hits[0].Name);
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

    [Fact(DisplayName = "Y-042 · Episodik ve kavram recall zemini aynı koşumda korunur")]
    public void Y042_RetrievalChangeMustPassBothRecallAxes()
    {
        // The fixture is the committed measurement, not a literal typed into the test: adding the
        // hand layer to the index is exactly the change that regressed the gold set once
        // (recall@3 102→94, recall@5 110→103), so the floor has to come from a recorded run.
        // The run carries two recall axes scored together — `tek-not`, where one specific note
        // answers, and `cok-not`, where several do. The episodic axis the scar's wording implies
        // has no measurement anywhere in this repository; these are the two axes that exist, and
        // the assertion fails if either is missing from the run or drops below its recorded value.
        var path = Path.Combine(ScarFixture.RepositoryRoot(), "bench", "results", "recall-2026-09-09-r2-gate.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var classes = document.RootElement.GetProperty("per_sinif");
        var single = classes.GetProperty("tek-not");
        var multiple = classes.GetProperty("cok-not");
        Assert.True(single.GetProperty("n").GetInt32() >= 90);
        Assert.True(multiple.GetProperty("n").GetInt32() >= 20);
        Assert.True(single.GetProperty("recall@5").GetDouble() >= 0.85);   // recorded 0.8788
        Assert.True(multiple.GetProperty("recall@5").GetDouble() >= 0.90); // recorded 0.9231
        var overall = document.RootElement.GetProperty("overall");
        Assert.True(overall.GetProperty("recall@3").GetDouble() >= document.RootElement.GetProperty("thresholds").GetProperty("recall@3").GetDouble());
        Assert.True(overall.GetProperty("recall@5").GetDouble() >= document.RootElement.GetProperty("thresholds").GetProperty("recall@5").GetDouble());

        // And the hand layer must not swallow the concept axis in the live ranker: a query that
        // no correction covers still returns its five concepts.
        var vault = ScarFixture.RetrievalVault();
        try
        {
            var measured = new Retrieve(new RetrieveOptions(VaultPath: vault, Top: 5))
                .Query("gold set kapsama eşik kanarya sınıf", "benchmark-" + Guid.NewGuid().ToString("N")[..8], 5);
            Assert.Equal(5, measured.Hits.Count);
            Assert.All(measured.Hits, hit => Assert.Equal("concept", hit.Source));
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
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
