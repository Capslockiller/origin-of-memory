using System.Text;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class SemaScars
{
    private const string HubConfig = """
        {"catch_all": "genel", "hubs": [
          {"id": "bellek", "ad": "Bellek", "kapsam": "Hafıza mekanizması ve notlar", "tags": ["bellek", "hafiza"], "title_keys": ["hafıza"]},
          {"id": "genel", "ad": "Genel", "kapsam": "Henüz bir hub'a ayrılmamış kavramlar", "tags": [], "title_keys": []}
        ]}
        """;

    private const string ExpandedHubConfig = """
        {"catch_all": "gunluk-yasam", "hubs": [
          {"id": "kisisel-saglik", "ad": "Kişisel Sağlık", "kapsam": "Kişisel yaşam ve sağlık", "tags": ["kisisel"], "title_keys": []},
          {"id": "gunluk-yasam", "ad": "Günlük Yaşam", "kapsam": "Henüz başka bir hub'a eşleşmeyen kavramlar", "tags": [], "title_keys": []}
        ]}
        """;

    private static readonly UTF8Encoding Utf8 = new(false);

    private const string LegacyConcept = """
        ---
        title: Hafıza çapası
        aliases: [capa]
        tags: [bellek, gunluk]
        sources: [2026-09-08.md]
        created: 2026-09-01
        updated: 2026-09-08
        ---
        # Hafıza çapası

        Çapa, bir oturumun günlükteki izidir.

        ## İlgili Kavramlar
        - [[gunluk-log]] — çapa günlüğe yazılır.
        - [[oturum-kursoru]] — çapa kursoru taşır.
        """;

    private static string Vault()
    {
        var vault = ScarFixture.TempDirectory();
        Directory.CreateDirectory(Path.Combine(vault, ".oom"));
        Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts"));
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        File.WriteAllText(Path.Combine(vault, ".oom", "hub-config.json"), HubConfig, Utf8);
        return vault;
    }

    [Fact(DisplayName = "Y-313 · Yeni daily frontmatter ile başlar, ikinci blok ikinci frontmatter açmaz; çapa, derleme ve bağlam aynı kalır")]
    public void Y313_DailyFrontmatterIsWrittenOnceAndEveryReaderStillWorks()
    {
        var vault = Vault();
        try
        {
            var flush = new Flush(new FlushOptions(VaultPath: vault));
            var day = new DateOnly(2026, 9, 9);
            var first = "### Oturum (12:00)\n<!-- session:y313 ts:2026-09-09T12:00:00+03:00 turns:0-5 source:claude -->\nBirinci.";
            var second = "### Oturum (13:00)\n<!-- session:y313 ts:2026-09-09T13:00:00+03:00 turns:6-9 source:claude -->\nİkinci.";

            var daily = flush.AppendDaily(string.Empty, first, "y313", day);
            daily = flush.AppendDaily(daily, second, "y313", day);

            Assert.StartsWith("---\ntype: daily\ndate: 2026-09-09\nsource: oom\n---\n# Günlük Log: 2026-09-09", daily, StringComparison.Ordinal);
            Assert.Equal(1, daily.Split("type: daily").Length - 1);
            Assert.Equal(2, daily.Split("---").Length - 1);

            var path = Path.Combine(vault, "daily", "2026-09-09.md");
            File.WriteAllText(path, daily, Utf8);

            var anchors = new SweepRun(vault, OomSettings.Defaults(vault), flush).ReadAnchors();
            Assert.Equal(9, anchors["y313"]);

            var plan = CompilePrompt.Build("2026-09-09.md", daily, string.Empty, string.Empty);
            Assert.Contains("type: daily", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("turns:6-9", plan.Prompt, StringComparison.Ordinal);

            var context = new Context().Build(vault, new DateTimeOffset(2026, 9, 9, 18, 0, 0, TimeSpan.FromHours(3)));
            Assert.Contains("[Bugünün Logu]", context.Text, StringComparison.Ordinal);
            Assert.Contains("İkinci.", context.Text, StringComparison.Ordinal);
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "Y-314 · Eski kavram type/hub olmadan ayrışır; bilinmeyen hub reddedilir, bilinen hub kabul edilir")]
    public void Y314_LegacyConceptParsesAndOnlyKnownHubIdsValidate()
    {
        var notes = new Notes(["bellek", "genel"]);
        var legacy = notes.Parse("hafiza-capasi.md", LegacyConcept);
        Assert.Null(legacy.Type);
        Assert.Null(legacy.Hub);
        Assert.Same(legacy, notes.Validate(legacy));

        var known = notes.Parse("hafiza-capasi.md", LegacyConcept.Replace("updated: 2026-09-08", "updated: 2026-09-08\ntype: concept\nhub: bellek", StringComparison.Ordinal));
        Assert.Equal("concept", known.Type);
        Assert.Equal("bellek", known.Hub);
        notes.Validate(known);

        var unknown = notes.Parse("hafiza-capasi.md", LegacyConcept.Replace("updated: 2026-09-08", "updated: 2026-09-08\ntype: concept\nhub: uzay", StringComparison.Ordinal));
        var error = Assert.Throws<FormatException>(() => notes.Validate(unknown));
        Assert.Contains("bilinmeyen bir hub kimliği: 'uzay'", error.Message, StringComparison.Ordinal);
        Assert.Contains("bellek, genel", error.Message, StringComparison.Ordinal);
        Assert.Contains("type", Notes.SchemaKeys);
        Assert.Contains("hub", Notes.SchemaKeys);
    }

    [Fact(DisplayName = "Y-315 · Regenerate eski kavramın yalnız frontmatter'ını yazar, hub bağını ekler, hub dosyası type: hub ile başlar")]
    public void Y315_RegenerateBackfillsHubWithoutTouchingTheBody()
    {
        var vault = Vault();
        try
        {
            var path = Path.Combine(vault, "knowledge", "concepts", "hafiza-capasi.md");
            File.WriteAllText(path, LegacyConcept, Utf8);

            new RootMap(vault).Regenerate();

            var rewritten = File.ReadAllText(path, Utf8);
            Assert.Contains("type: concept\nhub: bellek\n---", rewritten, StringComparison.Ordinal);
            Assert.Contains("updated: 2026-09-08", rewritten, StringComparison.Ordinal);
            Assert.Contains("Çapa, bir oturumun günlükteki izidir.", rewritten, StringComparison.Ordinal);
            Assert.EndsWith("Hub: [[hubs/bellek]]\n", rewritten, StringComparison.Ordinal);
            Assert.Equal(1, rewritten.Split("Hub: [[hubs/").Length - 1);

            var reparsed = new Notes(["bellek", "genel"]).Parse("hafiza-capasi.md", rewritten);
            Assert.Equal("bellek", reparsed.Hub);
            Assert.Equal(new DateOnly(2026, 9, 8), reparsed.Updated);

            new RootMap(vault).Regenerate();
            Assert.Equal(rewritten, File.ReadAllText(path, Utf8));

            var hub = File.ReadAllText(Path.Combine(vault, "knowledge", "hubs", "bellek.md"), Utf8);
            Assert.StartsWith("---\ntype: hub\n---\n# Bellek", hub, StringComparison.Ordinal);
            Assert.DoesNotContain("yazan", hub, StringComparison.Ordinal);
            Assert.DoesNotContain("model:", hub, StringComparison.Ordinal);
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "Y-316 · İstem hub kimliklerini ve etiket sözlüğünü sayar; sözlük dışı ikinci etiket sağlık satırı üretir")]
    public void Y316_PromptCarriesHubIdsAndTagVocabularyAndTheSecondStrayTagWarns()
    {
        var vault = Vault();
        try
        {
            var map = new RootMap(vault);
            var corpus = new[]
            {
                ScarFixture.Note(1) with { Tags = ["bellek", "gunluk"] },
                ScarFixture.Note(2) with { Tags = ["gunluk"] }
            };
            var vocabulary = map.TagVocabulary(corpus);
            Assert.Contains("bellek", vocabulary);
            Assert.Contains("hafiza", vocabulary);
            Assert.Contains("gunluk", vocabulary);

            var plan = CompilePrompt.Build("2026-09-09.md", "günlük", string.Empty, string.Empty, map.HubLines, vocabulary);
            Assert.Contains("type: concept", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("hub: <aşağıdaki hub kimliklerinden biri>", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains(CompilePrompt.HubHeading + "\n- bellek — Hafıza mekanizması ve notlar", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains(CompilePrompt.TagHeading, plan.Prompt, StringComparison.Ordinal);
            Assert.Contains(CompilePrompt.TagRule, plan.Prompt, StringComparison.Ordinal);

            var notes = new Notes(map.HubIds, vocabulary);
            Assert.Empty(notes.TagWarnings(ScarFixture.Note(3) with { Tags = ["bellek", "kaçak"] }));
            var warning = Assert.Single(notes.TagWarnings(ScarFixture.Note(4) with { Tags = ["bellek", "kaçak", "ikinci-kaçak"] }));
            Assert.Equal(HealthLevel.Warning, warning.Level);
            Assert.Equal("etiket-sozluk-disi", warning.Code);
            Assert.Contains("kaçak, ikinci-kaçak", warning.Detail, StringComparison.Ordinal);
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "Y-317 · Doctor yetim-kavram, hub-siz ve sema-disi satırlarını sayar; sıfırda info, üstünde uyarı")]
    public void Y317_DoctorCountsOrphanHublessAndSchemalessRows()
    {
        var vault = Vault();
        try
        {
            File.WriteAllText(Path.Combine(vault, "daily", "2026-09-08.md"), "# Günlük Log: 2026-09-08\n", Utf8);
            File.WriteAllText(Path.Combine(vault, "daily", "2026-09-09.md"), "---\ntype: daily\ndate: 2026-09-09\nsource: oom\n---\n# Günlük Log: 2026-09-09\n", Utf8);

            var notes = new Notes(["bellek", "genel"]);
            var legacy = notes.Parse("hafiza-capasi.md", LegacyConcept);
            var hubbed = notes.Parse("gunluk-log.md", LegacyConcept
                .Replace("title: Hafıza çapası", "title: Günlük log", StringComparison.Ordinal)
                .Replace("updated: 2026-09-08", "updated: 2026-09-08\ntype: concept\nhub: bellek", StringComparison.Ordinal));

            var rows = Doctor.VaultSchema(vault, [legacy, hubbed], "genel");
            Assert.Equal(["yetim-kavram", "hub-siz", "sema-disi"], rows.Select(row => row.Code));
            Assert.All(rows, row => Assert.Equal("vault", row.Component));

            Assert.Equal(HealthLevel.Warning, rows[0].Level);
            Assert.Equal("1", rows[0].Key);
            Assert.Contains("1/2", rows[0].Detail, StringComparison.Ordinal);

            Assert.Equal(HealthLevel.Warning, rows[1].Level);
            Assert.Equal("1", rows[1].Key);

            Assert.Equal(HealthLevel.Warning, rows[2].Level);
            Assert.Equal("2", rows[2].Key);
            Assert.Contains("frontmatter'sız 1 daily", rows[2].Detail, StringComparison.Ordinal);

            var clean = Doctor.VaultSchema(ScarFixture.TempDirectory(), [], "genel");
            Assert.All(clean, row => Assert.Equal(HealthLevel.Info, row.Level));
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "Y-336 · Catch-all kavram yeni etiket eşleşmesi gelince doğru hub'a taşınır")]
    public void Y336_CatchAllConceptMovesWhenConfigurationGainsMatchingTag()
    {
        var vault = Vault();
        try
        {
            var path = Path.Combine(vault, "knowledge", "concepts", "kisisel-not.md");
            var configurationPath = Path.Combine(vault, ".oom", "hub-config.json");
            var original = HubbedConcept("gunluk-yasam", "kisisel");
            File.WriteAllText(path, original, Utf8);
            File.WriteAllText(configurationPath, ExpandedHubConfig.Replace("\"tags\": [\"kisisel\"]", "\"tags\": []", StringComparison.Ordinal), Utf8);

            new RootMap(vault).Regenerate();
            Assert.Equal(original, File.ReadAllText(path, Utf8));

            File.WriteAllText(configurationPath, ExpandedHubConfig, Utf8);

            new RootMap(vault).Regenerate();

            var rewritten = File.ReadAllText(path, Utf8);
            Assert.NotEqual(original, rewritten);
            Assert.Contains("hub: kisisel-saglik\n---", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("hub: gunluk-yasam", rewritten, StringComparison.Ordinal);
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "Y-337 · Catch-all'da kalan kavramın dosyası yeniden yazılmaz")]
    public void Y337_CatchAllConceptIsNotRewrittenWhenItStillMatchesNothing()
    {
        var vault = Vault();
        try
        {
            var path = Path.Combine(vault, "knowledge", "concepts", "eslesmeyen-not.md");
            var original = HubbedConcept("gunluk-yasam", "sozluk-disi");
            File.WriteAllText(path, original, Utf8);
            File.WriteAllText(Path.Combine(vault, ".oom", "hub-config.json"), ExpandedHubConfig, Utf8);
            var timestamp = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, timestamp);

            new RootMap(vault).Regenerate();

            Assert.Equal(original, File.ReadAllText(path, Utf8));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "Y-338 · Geçerli catch-all olmayan hub'a sahip kavrama dokunulmaz")]
    public void Y338_ValidNonCatchAllHubRemainsUntouched()
    {
        var vault = Vault();
        try
        {
            var path = Path.Combine(vault, "knowledge", "concepts", "sabit-not.md");
            var original = HubbedConcept("kisisel-saglik", "sozluk-disi");
            File.WriteAllText(path, original, Utf8);
            File.WriteAllText(Path.Combine(vault, ".oom", "hub-config.json"), ExpandedHubConfig, Utf8);
            var timestamp = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, timestamp);

            new RootMap(vault).Regenerate();

            Assert.Equal(original, File.ReadAllText(path, Utf8));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    private static string HubbedConcept(string hub, string tag) =>
        $"---\ntitle: Kisisel not\naliases: []\ntags: [{tag}]\nsources: [2026-09-15.md]\n" +
        $"created: 2026-09-15\nupdated: 2026-09-15\ntype: concept\nhub: {hub}\n---\n" +
        "# Kisisel not\n\nKalici bilgi.\n";
}
