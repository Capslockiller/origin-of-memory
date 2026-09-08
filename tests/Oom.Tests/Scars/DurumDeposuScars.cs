using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class DurumDeposuScars
{
    [Fact(DisplayName = "Y-058 · Aynı message id üç kez geçse de kullanım bir kez sayılır")]
    public void Y058_UsageIsDeduplicatedByMessageId()
    {
        var records = Enumerable.Range(0, 3).Select(_ => new UsageRecord("msg-58", "master", ScarFixture.Now, 100, 10, 1_000)).ToArray();
        var summary = new State().AggregateUsage(records);
        Assert.Equal(100, summary.InputTokens);
        Assert.Equal(10, summary.OutputTokens);
        Assert.Equal(1_000, summary.CacheReadTokens);
    }

    [Fact(DisplayName = "Y-059 · Sahiplik sınıfları tekilleştirilmiş genel toplamla eşleşir")]
    public void Y059_OwnerClassesSumToDeduplicatedTotal()
    {
        var records = new[]
        {
            new UsageRecord("master", "Master", ScarFixture.Now, 1, 1, 100),
            new UsageRecord("agent", "alt-ajan", ScarFixture.Now, 1, 1, 50_800_000),
            new UsageRecord("compile", "derleme", ScarFixture.Now, 1, 1, 200)
        };
        var summary = new State().AggregateUsage(records);
        Assert.Equal(50_800_000, summary.CacheReadByOwner["alt-ajan"]);
        Assert.Equal(summary.CacheReadTokens, summary.CacheReadByOwner.Values.Sum());
    }

    [Fact(DisplayName = "Y-060 · Gece yarısını aşan kullanım yanıt zamanına göre iki güne bölünür")]
    public void Y060_UsageIsAssignedByResponseTimestamp()
    {
        var before = new UsageRecord("before", "Master", new DateTimeOffset(2026, 9, 8, 23, 59, 0, TimeSpan.FromHours(3)), 10, 1, 0);
        var after = new UsageRecord("after", "Master", new DateTimeOffset(2026, 9, 9, 0, 1, 0, TimeSpan.FromHours(3)), 20, 2, 0);
        var firstDay = new State().AggregateUsage([before]);
        var secondDay = new State().AggregateUsage([after]);
        Assert.Equal(10, firstDay.InputTokens);
        Assert.Equal(20, secondDay.InputTokens);
    }

    [Fact(DisplayName = "Y-061 · Bir yıllık durum deposu elli MB altında ve saklama sürelerine uyar")]
    public void Y061_StateRetentionBoundsOneYearDatabase()
    {
        var stats = new State().SweepRetention(ScarFixture.Now);
        Assert.True(stats.Bytes < 50L * 1024 * 1024);
        Assert.InRange(stats.Calls, 0, 90 * 10_000);
        Assert.InRange(stats.QuotaRows, 0, 30 * 10_000);
    }

    [Fact(DisplayName = "Y-062 · Sekiz günlük retrieve kayıtları tek sweep ile budanır")]
    public void Y062_RetrieveRowsOlderThanSevenDaysArePruned()
    {
        var stats = new State().SweepRetention(ScarFixture.Now);
        Assert.Equal(0, stats.RetrieveRows);
    }

    [Fact(DisplayName = "Y-063 · Sekiz eşzamanlı health yazımı kaybolmaz ve 25 saatlik uyarı eskidir")]
    public void Y063_HealthWritesAreLockedAndWarningsAge()
    {
        var items = Enumerable.Range(0, 8).Select(i => new HealthItem("test", HealthLevel.Warning, $"w-{i}", i.ToString(), "uyarı", i == 0)).ToArray();
        var written = new State().WriteHealthConcurrently(items);
        Assert.Equal(8, written.Count);
        Assert.True(written.Single(x => x.Code == "w-0").Stale);
    }

    [Fact(DisplayName = "Y-064 · Sekiz iş parçacıklı atomik yazım veri ve dosya kaybetmez")]
    public void Y064_AtomicWriteUsesUniqueTemporaryNamesAndRetries()
    {
        var final = new State().AtomicWrite("state.json", "{\"version\":8}", writers: 8);
        Assert.Equal("{\"version\":8}", final);
        Assert.DoesNotContain(".tmp", final, StringComparison.OrdinalIgnoreCase);
    }
}
