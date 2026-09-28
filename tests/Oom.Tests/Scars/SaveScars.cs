using System.Text.Json;
using Oom.Contracts;
using Oom.Tests.Gates;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class SaveScars
{
    [Fact(DisplayName = "Y-085 · save serbest metni kabul eder, zorunlu alan aranmaz, boş metin reddedilir")]
    public void Y085_SaveAcceptsFreeTextAndRejectsEmptyText()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            var freeText = new Save().WriteToVault(vault, "Karar var ama devir yok", ScarFixture.Now);
            Assert.True(freeText.Written && freeText.Verified, freeText.Error);

            var empty = new Save().WriteToVault(vault, "   ", ScarFixture.Now);
            Assert.False(empty.Written);
            Assert.False(empty.Verified);
            Assert.NotNull(empty.Error);
        }
        finally { ScarFixture.Remove(vault); }
    }

    [Fact(DisplayName = "Y-101 · save serbest metni günlük dosyaya yazar ve geri okuyup doğrular")]
    public void Y101_CheckpointIsWrittenToDailyAndVerified()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            const string text = "karar: sandık kapandı\ndüzeltme: yerel kütüphane exe içinde\ndevir: şerit FIX";
            var written = new Save().WriteToVault(vault, text, ScarFixture.Now);
            Assert.True(written.Written, written.Error);
            Assert.Null(written.Error);
            var daily = Path.Combine(vault, "daily", $"{ScarFixture.Now:yyyy-MM-dd}.md");
            Assert.True(File.Exists(daily), $"{daily} yazılmadı.");
            var body = File.ReadAllText(daily);
            Assert.Contains("karar: sandık kapandı", body);
            Assert.Contains("devir: şerit FIX", body);
        }
        finally { ScarFixture.Remove(vault); }
    }

    [Fact(DisplayName = "Y-101b · WriteToVault, günlük dizini oluşturulamazsa yazılmadı bildirir")]
    public void Y101b_WriteToVaultReportsFailureWhenDailyDirectoryCannotBeCreated()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            // `daily` already exists as a FILE, not a directory: Directory.CreateDirectory
            // then throws IOException, exercising AppendToDaily's failure path (the Y-101
            // negative case that was dropped alongside checkpointWriter).
            File.WriteAllText(Path.Combine(vault, "daily"), "engel");

            var result = new Save().WriteToVault(vault, "x", ScarFixture.Now);

            Assert.False(result.Written);
            Assert.False(result.Verified);
            Assert.Equal("Kayıt günlük dosyaya yazılamadı.", result.Error);
        }
        finally { ScarFixture.Remove(vault); }
    }

    [Fact(DisplayName = "Y-102 · save metni komuttan sonraki ilk konumsal argümandır, --vault yutulmaz")]
    public void Y102_SaveTextIsTheFirstPositionalAfterTheCommand()
    {
        string[] plain = ["save", "karar: a"];
        string[] withVault = ["--vault", @"D:\kasa", "save", "karar: a"];
        Assert.Equal("save", CommandLine.Command(plain));
        Assert.Equal("save", CommandLine.Command(withVault));
        Assert.Equal("karar: a", CommandLine.Argument(plain, 0));
        Assert.Equal("karar: a", CommandLine.Argument(withVault, 0));
        Assert.Equal(@"D:\kasa", CommandLine.Value(withVault, "--vault"));
        Assert.Null(CommandLine.Argument(["--vault", @"D:\kasa", "save"], 0));
    }

    [Fact(DisplayName = "Y-107 · save --session-json bozuk dış sözleşmede çökmek yerine rc 1 döndürür")]
    public void Y107_SaveSessionJsonRejectsMalformedInputWithoutEscapingProgram()
    {
        const string malformed = """
            {"id":"x","source":"s","turns":[{"index":0,"role":"user","kind":"text","text":{"value":"a"}}]}
            """;
        Assert.Throws<FormatException>(() => new Save().SaveSessionJson(malformed));

        var vault = ScarFixture.TempDirectory();
        var sessionJson = Path.Combine(vault, "session.json");
        var previousError = Console.Error;
        using var error = new StringWriter();
        try
        {
            File.WriteAllText(sessionJson, malformed, new System.Text.UTF8Encoding(false));
            var program = typeof(Save).Assembly.GetType("Oom.Program", throwOnError: true)!;
            var runSave = program.GetMethod("RunSave", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            Console.SetError(error);

            var returnCode = (int)runSave.Invoke(null, [new[] { "save", "--session-json", sessionJson }, vault])!;

            Assert.Equal(1, returnCode);
            Assert.Contains("kayıt yazılmadı: Dış oturum JSON sözleşmesine uymuyor.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "Y-107b · save --session-json, MinTurns altında rc 1 ve 'NoTurns' bildirir, daily'e yazmaz")]
    public void Y107b_SaveSessionJsonBelowMinTurnsReturnsNonZeroAndWritesNothing()
    {
        var start = new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.FromHours(3));
        var turns = Enumerable.Range(0, 2).Select(i => new
        {
            index = i,
            role = i % 2 == 0 ? "user" : "assistant",
            kind = "text",
            text = $"kısa {i}",
            timestamp = start.AddMinutes(i).ToString("O")
        }).ToArray();
        var json = JsonSerializer.Serialize(new { id = "az-tur-" + Guid.NewGuid().ToString("N")[..8], source = "web-disari", turns, startedAt = start.ToString("O") });

        var vault = ScarFixture.TempDirectory();
        var sessionJson = Path.Combine(vault, "session.json");
        var previousError = Console.Error;
        using var error = new StringWriter();
        try
        {
            File.WriteAllText(sessionJson, json, new System.Text.UTF8Encoding(false));
            var program = typeof(Save).Assembly.GetType("Oom.Program", throwOnError: true)!;
            var runSave = program.GetMethod("RunSave", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            Console.SetError(error);

            var returnCode = (int)runSave.Invoke(null, [new[] { "save", "--session-json", sessionJson }, vault])!;

            Assert.Equal(1, returnCode);
            Assert.Contains("kayıt yazılmadı: NoTurns", error.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(vault, "daily")), "MinTurns altı bir oturum daily/ dizini oluşturmamalı.");
        }
        finally
        {
            Console.SetError(previousError);
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "Kapı 11-3 · save --session-json dış oturumu normal flush yolundan daily bloğuna yazar")]
    public void Gate11SaveSessionJsonWritesImportedDailyBlock()
    {
        using var vault = new TempVault();
        Directory.CreateDirectory(Path.Combine(vault.Path, "daily"));
        var id = "disari-" + Guid.NewGuid().ToString("N")[..8];
        var start = new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.FromHours(3));
        var turns = Enumerable.Range(0, 6).Select(i => new
        {
            index = i,
            role = i % 2 == 0 ? "user" : "assistant",
            kind = "text",
            text = $"Dış ayrıştırıcıdan gelen {i}. tur metni.",
            timestamp = start.AddMinutes(i).ToString("O")
        }).ToArray();
        var json = JsonSerializer.Serialize(new { id, source = "web-disari", turns, startedAt = start.ToString("O") });

        var save = new Save(flush: new Flush(new FlushOptions(VaultPath: vault.Path),
            new FixedClock(start.AddHours(1)), new Runner(null, configured: false)));

        var first = save.SaveSessionJson(json);
        var second = save.SaveSessionJson(json);

        Assert.Equal(FlushOutcome.Ok, first.Outcome);
        Assert.Equal(6, first.Cursor);
        Assert.NotNull(first.DailyPath);
        Assert.Contains("fallback_backend: extractive", first.Summary!, StringComparison.Ordinal);

        var eventTime = TimeZoneInfo.ConvertTime(start.AddMinutes(5), TimeZoneInfo.Local);
        Assert.Equal(Path.Combine(vault.Path, "daily", $"{eventTime:yyyy-MM-dd}.md"), first.DailyPath);

        var daily = File.ReadAllText(first.DailyPath!, GateFixture.Utf8);
        Assert.Contains($"### Oturum ({eventTime:HH:mm}), içe aktarım:web-disari", daily, StringComparison.Ordinal);
        Assert.Contains($"<!-- session:{id} ts:{eventTime:yyyy-MM-ddTHH:mm:sszzz} turns:0-5 source:web-disari -->", daily, StringComparison.Ordinal);
        foreach (var heading in new[] { "## Bağlam", "## Önemli Konuşmalar", "## Alınan Kararlar", "## Öğrenilenler", "## Yapılacaklar" })
            Assert.Contains(heading, daily, StringComparison.Ordinal);

        Assert.Equal(FlushOutcome.NoNewTurns, second.Outcome);
        Assert.Equal(6, second.Cursor);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; } = now;
    }
}
