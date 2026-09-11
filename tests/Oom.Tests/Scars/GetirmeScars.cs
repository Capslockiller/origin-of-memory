using System.Text.Json;
using Microsoft.Data.Sqlite;
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
        Assert.Empty(retrieve.Hook("Ay tutulması kaç dakika sürer?", session).Hits);
        // A common-topic hit made solely from floored IDF is still insufficient evidence.
        Assert.Empty(retrieve.Hook("Panel güvenlik ayrıntıları nelerdir?", session).Hits);
        Assert.Empty(retrieve.Hook(prompt, session).Hits); // served-note dedupe survives
        Assert.Empty(new Retrieve(new RetrieveOptions(VaultPath: vault, StrictScore: 100))
            .Hook(prompt, Guid.NewGuid().ToString()).Hits);
    }

    // yazan: claude · opus-5
    [Fact(DisplayName = "Y-141 · Sorgu ve kanca yolu FTS indeksinden geçer, indeks cevabı değiştirmez")]
    public void Y141_QueryAndHookGenerateCandidatesThroughTheIndex()
    {
        var vault = ScarFixture.TempDirectory();
        var index = Path.Combine(vault, "state.db");
        try
        {
            Concepts(vault);
            const string prompt = "Panel güvenlik kapısı nasıl çalışıyor?";

            // `Retrieve.Candidates` shipped with a full-text statement, documented bm25 weights and
            // no caller anywhere in src/: connected in the scar suite, dead in production. These two
            // instances differ in exactly one option — whether an index path exists — so the pair
            // measures what the index changed rather than asserting that it changed nothing.
            var scanner = new Retrieve(new RetrieveOptions(VaultPath: vault));
            var indexed = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index));
            indexed.Build();

            var scanned = scanner.Query(prompt, Guid.NewGuid().ToString(), 5);
            var served = indexed.Query(prompt, Guid.NewGuid().ToString(), 5);

            Assert.Equal("corpus-scan:no-index", scanner.CandidateSource);
            Assert.Equal("fts", indexed.CandidateSource);
            Assert.NotEmpty(served.Hits);
            // Candidate generation moved; scoring did not. The statistics stay whole-corpus, so a
            // narrowed run has to reproduce the scanned run name for name and score for score — the
            // one assertion that keeps "wired to the index" from silently meaning "ranked differently".
            Assert.Equal(scanned.Hits.Select(hit => hit.Name), served.Hits.Select(hit => hit.Name));
            Assert.Equal(scanned.Hits.Select(hit => hit.Score), served.Hits.Select(hit => hit.Score));

            var hooked = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index));
            Assert.NotEmpty(hooked.Hook(prompt, Guid.NewGuid().ToString()).Hits);
            Assert.Equal("fts", hooked.CandidateSource);

            // An index that no longer describes the corpus is a fall-back, never a stale answer: the
            // note added after the build is ranked, and the path says out loud that it scanned.
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

    // yazan: claude · opus-5
    /// <summary>
    /// The served-note dedupe used to live in a <c>static readonly Dictionary</c> inside
    /// <see cref="Retrieve"/>, and production spawns a fresh <c>oom.exe</c> for every hook event: the
    /// dictionary was empty again on the very next prompt, so nothing was ever de-duplicated across
    /// that process boundary and <c>retrieve_served</c> stayed unwritten by every code path in
    /// <c>src/</c>. Instance <c>b</c> below is a second open of the same <c>state.db</c> file — not a
    /// second OS process — and it shares no in-memory state with instance <c>a</c>:
    /// <see cref="Retrieve"/>'s <c>_served</c> dictionary is instance-scoped precisely so this test
    /// measures the file, not the process. If <c>b</c> still saw the note, the file would not be
    /// carrying the silence at all.
    /// </summary>
    [Fact(DisplayName = "Y-173 · Onaylanan teslim ikinci süreçte de susar")]
    public void Y173_AcknowledgedDeliverySilencesASecondOpenOfTheSameFile()
    {
        var vault = ScarFixture.TempDirectory();
        var index = Path.Combine(vault, "state.db");
        try
        {
            Concepts(vault);
            using (var _ = new State(null, null, index)) { }
            SqliteConnection.ClearAllPools();
            var options = new RetrieveOptions(VaultPath: vault, IndexPath: index);
            new Retrieve(options).Build();

            const string prompt = "Panel güvenlik kapısı nasıl çalışıyor?";
            var session = "session-173-" + Guid.NewGuid().ToString("N")[..8];

            var a = new Retrieve(options);
            var first = a.Query(prompt, session, 3);
            Assert.NotEmpty(first.Hits);

            var acknowledged = a.AcknowledgeDelivery();
            Assert.True(acknowledged == first.Hits.Count,
                $"AcknowledgeDelivery {acknowledged} satır bildirdi, tutulan isabet sayısı {first.Hits.Count} idi");

            var rows = ServedRows(index, session);
            Assert.True(rows.Length == first.Hits.Count,
                $"oturum için tabloda {rows.Length} satır var, beklenen {first.Hits.Count} idi");
            Assert.All(rows, row => Assert.True(row.Status == "emitted", $"satır durumu 'emitted' değil: {row.Status}"));
            Assert.All(rows, row => Assert.True(row.AckedTs is not null, "acked_ts onaylanmış satırda boş kaldı"));

            // b is a second open of the same file, sharing no memory with a — only the file can carry
            // the silence across it.
            var b = new Retrieve(options);
            var second = b.Query(prompt, session, 3);
            Assert.Empty(second.Hits);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(vault);
        }
    }

    // yazan: claude · opus-5
    /// <summary>
    /// Nothing in <c>src/</c> called <see cref="Retrieve.AcknowledgeDelivery"/> before this lane, so a
    /// <c>prepared</c> row left by a run that crashed, was killed, or had its stdout discarded before
    /// the block reached the model stayed exactly <c>prepared</c> forever — and
    /// <c>Retrieve.Filter</c>'s suppression trusts only rows the ledger can prove
    /// <c>emitted</c>. Within this one test process the pid never changes between <c>a</c> and
    /// <c>b</c>, so the row asserted below stays literally <c>prepared</c>; a real second
    /// <c>oom.exe</c> opens the same file under a different pid, and
    /// <c>ServedLedger.Reclassify</c> turns that same row <c>uncertain</c> on open instead. Both
    /// statuses are equally non-suppressing — that equivalence, not the exact string, is the property
    /// under test. Instance <c>b</c> is a second open of the same file and shares no memory with
    /// <c>a</c>.
    /// </summary>
    [Fact(DisplayName = "Y-174 · Onaylanmayan teslim teslim sayılmaz; hafıza geri verilir")]
    public void Y174_UnacknowledgedDeliveryDoesNotSuppressASecondOpenOfTheSameFile()
    {
        var vault = ScarFixture.TempDirectory();
        var index = Path.Combine(vault, "state.db");
        try
        {
            Concepts(vault);
            using (var _ = new State(null, null, index)) { }
            SqliteConnection.ClearAllPools();
            var options = new RetrieveOptions(VaultPath: vault, IndexPath: index);
            new Retrieve(options).Build();

            const string prompt = "Panel güvenlik kapısı nasıl çalışıyor?";
            var session = "session-174-" + Guid.NewGuid().ToString("N")[..8];

            var a = new Retrieve(options);
            var first = a.Query(prompt, session, 3);
            Assert.NotEmpty(first.Hits);
            // Deliberately no AcknowledgeDelivery() call: the rows stay `prepared`.

            var rows = ServedRows(index, session);
            Assert.True(rows.Length == first.Hits.Count,
                $"oturum için tabloda {rows.Length} satır var, beklenen {first.Hits.Count} idi");
            Assert.All(rows, row => Assert.True(row.Status == "prepared",
                $"onaylanmadan satır durumu 'prepared' değil: {row.Status}"));
            Assert.All(rows, row => Assert.True(row.AckedTs is null, "onaylanmadan acked_ts dolu geldi"));

            var b = new Retrieve(options);
            var second = b.Query(prompt, session, 3);
            Assert.NotEmpty(second.Hits);
            Assert.True(second.Hits[0].Name == first.Hits[0].Name,
                $"ikinci açılışın ilk notu ({second.Hits[0].Name}) birincininkiyle ({first.Hits[0].Name}) eşleşmedi");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(vault);
        }
    }

    // yazan: claude · opus-5
    /// <summary>
    /// Two properties that only show up once the ledger actually crosses a process boundary:
    /// <list type="bullet">
    /// <item>the seven-day window is scoped by session (see <see cref="ServedKey.SessionId"/>'s own
    /// summary) — a delivery acknowledged in one conversation must not silence a note in a conversation
    /// that has never seen it, or a single delivery would withhold that note from every new session for
    /// a week;</item>
    /// <item>the window is scoped by query signature (Y-039's property) — a different question in the
    /// same session must see the same note again. Y-039 measured this against one process's in-memory
    /// dictionary; now that the ledger persists, the same property has to hold across a reopen of the
    /// file, not only within one process.</item>
    /// </list>
    /// Every instance after <c>a</c> below opens the same file fresh and shares no memory with it.
    /// </summary>
    [Fact(DisplayName = "Y-175 · Yedi günlük sessizlik oturumlara yayılmaz")]
    public void Y175_SevenDaySilenceStaysScopedToSessionAndSignature()
    {
        var vault = ScarFixture.TempDirectory();
        var index = Path.Combine(vault, "state.db");
        try
        {
            Concepts(vault);
            using (var _ = new State(null, null, index)) { }
            SqliteConnection.ClearAllPools();
            var options = new RetrieveOptions(VaultPath: vault, IndexPath: index);
            new Retrieve(options).Build();

            const string prompt = "Panel güvenlik kapısı nasıl çalışıyor?";
            const string otherQuestion = "Panel güvenlik bilgisi nedir?";

            var a = new Retrieve(options);
            var first = a.Query(prompt, "oturum-a", 3);
            Assert.NotEmpty(first.Hits);
            a.AcknowledgeDelivery();

            // Same session, same question, acknowledged delivery: does suppress.
            var sameSession = new Retrieve(options).Query(prompt, "oturum-a", 3);
            Assert.Empty(sameSession.Hits);

            // New session, same question: the acknowledged delivery must not have spread to it — a
            // new conversation has never been told anything.
            var newSession = new Retrieve(options).Query(prompt, "oturum-b", 3);
            Assert.NotEmpty(newSession.Hits);
            Assert.True(newSession.Hits[0].Name == first.Hits[0].Name,
                $"yeni oturumun ilk notu ({newSession.Hits[0].Name}) birincininkiyle ({first.Hits[0].Name}) eşleşmedi");

            // Same session, different question (different query signature): Y-039's property, now
            // measured across a persisted, reopened ledger instead of one process's dictionary.
            var differentQuestion = new Retrieve(options).Query(otherQuestion, "oturum-a", 3);
            Assert.NotEmpty(differentQuestion.Hits);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(vault);
        }
    }

    /// <summary>Reads <c>retrieve_served</c> rows for one session directly, bypassing <see cref="Retrieve"/>.</summary>
    private static (string Status, string? AckedTs)[] ServedRows(string index, string sessionId)
    {
        using var connection = new SqliteConnection($"Data Source={index};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, acked_ts FROM retrieve_served WHERE session_id = $session";
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string?)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return rows.ToArray();
    }
}
