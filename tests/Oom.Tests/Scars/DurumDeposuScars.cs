using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Gates;
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
        Assert.InRange(stats.FlushLogs, 0, 90 * 10_000);
        Assert.InRange(stats.HealthRows, 0, 30 * 10_000);
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

    [Fact(DisplayName = "Y-132 · Salt okunur açılış hiçbir durum kökü yaratmaz; yazan açılış yaratır")]
    public void Y132_ReadOnlyOpenCreatesNoStateRoot()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var missing = Path.Combine(root, "yok");
            var database = Path.Combine(missing, "state.db");

            Assert.Null(State.OpenReadOnly(database));
            Assert.False(Directory.Exists(missing));
            Assert.Throws<FileNotFoundException>(() => new State(null, null, database, StateAccess.ReadOnly));
            Assert.False(Directory.Exists(missing));

            using (var writer = new State(null, null, database))
                writer.RecordFlush(ScarFixture.Now, "y132", "sessionend", "ok", 4, 40, "claude");
            SqliteConnection.ClearAllPools();
            Assert.True(File.Exists(database));

            using var reader = State.OpenReadOnly(database);
            Assert.NotNull(reader);
            Assert.Equal(StateAccess.ReadOnly, reader!.Access);
            Assert.Equal(1, reader.Scalar("SELECT COUNT(*) FROM flush_log"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    /// <summary>
    /// Y-161 · The point of the whole lane, made concrete: a unit test that calls
    /// <c>State.OpenReadOnly</c> or <c>VaultIdentity.EnsureDatabase</c> directly proves nothing
    /// about whether <c>Program.cs</c>'s command dispatcher actually reaches those helpers for a
    /// given command — only running the SHIPPED <c>oom.exe</c> and watching what it does to disk
    /// proves that. A read-only command (`context`, `retrieve`) must leave no state root behind;
    /// a write command (`compile`, even with `--dry-run`, since `--dry-run` only suppresses the
    /// model call and still opens the write handle) must create one.
    ///
    /// The whole thing runs inside a fixture profile: <c>OOM_LOCALAPPDATA</c> points the state root
    /// at this test's own directory, so a suite that measures what a command writes writes nothing
    /// into the owner's real <c>%LOCALAPPDATA%\oom</c> — not even a root it creates and removes again.
    /// </summary>
    [Fact(DisplayName = "Y-161 · Yayımlanan exe: salt okunur komut durum kökü yaratmaz, yazan komut yaratır")]
    public void Y161_ShippedExecutableCreatesNoStateRootOnReadOnlyCommandsButDoesOnWrite()
    {
        var root = ScarFixture.TempDirectory();
        var vault = Path.Combine(root, "kasa");
        var profile = Path.Combine(root, "profil");
        Directory.CreateDirectory(vault);
        Directory.CreateDirectory(Path.Combine(profile, "oom"));
        var previous = Environment.GetEnvironmentVariable("OOM_LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("OOM_LOCALAPPDATA", profile);
            var stateRoot = VaultIdentity.StateRoot(vault);
            Assert.StartsWith(profile, stateRoot, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(stateRoot), $"önceki bir koşumdan kalma durum kökü temizlenmemiş: {stateRoot}");

            var context = GateFixture.RunScoped(profile, "context", "--vault", vault);
            Assert.True(context.ExitCode == 0, $"'context --vault {vault}' çıkış kodu {context.ExitCode} döndürdü: {context.StandardError}");
            Assert.False(Directory.Exists(stateRoot), $"salt okunur 'context' komutu durum kökünü yarattı: {stateRoot}");

            // No-hit sorgunun kendi çıkış kodu var; burada tek ilgilenilen şey diskte iz bırakmaması.
            var retrieve = GateFixture.RunScoped(profile, "retrieve", "--query", "tokenizasyon", "--vault", vault);
            Assert.False(Directory.Exists(stateRoot),
                $"salt okunur 'retrieve' komutu durum kökünü yarattı: {stateRoot} (stderr: {retrieve.StandardError})");

            var compile = GateFixture.RunScoped(profile, "compile", "--dry-run", "--vault", vault);
            Assert.True(compile.ExitCode == 0, $"'compile --dry-run --vault {vault}' çıkış kodu {compile.ExitCode} döndürdü: {compile.StandardError}");
            Assert.True(Directory.Exists(stateRoot), $"yazan 'compile --dry-run' komutu durum kökünü yaratmadı: {stateRoot}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OOM_LOCALAPPDATA", previous);
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

}
