using System.Text;
using Microsoft.Data.Sqlite;
using Oom.Contracts;

namespace Oom.Tests.Kabul;

public sealed class RetrieveSharingKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);

    [Fact(DisplayName = "L6d O18 · Paylaşımlı daily retrieve indeksine alınır ve aranabilir")]
    public void SharedDailyRemainsInRetrieveCorpus()
    {
        using var harness = new KabulHarness();
        var vault = harness.NewScratchDirectory("vault-retrieve-sharing");
        var dailyDirectory = Path.Combine(vault, "daily");
        Directory.CreateDirectory(dailyDirectory);
        var daily = Path.Combine(dailyDirectory, "2026-09-27.md");
        File.WriteAllText(daily,
            "---\ntype: daily\ndate: 2026-09-27\n---\n# Günlük Log: 2026-09-27\n\n## Oturumlar\n\n" +
            "### Oturum (09:00)\nL6D-KASA-CANARY kasa paylaşım altında okunur.\n", Utf8);
        var index = Path.Combine(harness.Root, "retrieve-state.db");

        try
        {
            using var writer = new FileStream(daily, FileMode.Open, FileAccess.Write, FileShare.Read);
            var retrieve = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index));

            var built = retrieve.Build();
            Assert.Equal(0, built.ExitCode);
            var result = retrieve.Query("L6D KASA CANARY", "l6d-o18", 3);
            Assert.Contains(result.Hits, hit => hit.Name == "daily/2026-09-27.md#1");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }
}
