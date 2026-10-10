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







    private static string HubbedConcept(string hub, string tag) =>
        $"---\ntitle: Kisisel not\naliases: []\ntags: [{tag}]\nsources: [2026-09-15.md]\n" +
        $"created: 2026-09-15\nupdated: 2026-09-15\ntype: concept\nhub: {hub}\n---\n" +
        "# Kisisel not\n\nKalici bilgi.\n";
}
